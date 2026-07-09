using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VideoShrink.Data;
using Serilog;

namespace VideoShrink.Core;

public enum JobStatus { Success, Failed, Skipped }

public sealed record JobOutcome(JobStatus Status, string Reason, long SavedBytes);

/// <summary>
/// 单个文件的完整流水线：
///   ffprobe → 筛选 → 容器判定 → 逐流音频/字幕计划 → 由目标体积反推码率
///   → 编码到 cache（CPU 两趟 / GPU 单趟） → ffprobe 验收（含整片解码）
///   → 发布到目标路径（文件名逐字不变） → 落库
///
/// 任何一步失败：<b>源文件原封不动</b>，记录失败原因，返回 Failed（引擎跳到下一个）。
/// 新建文件夹模式下源文件本就永不删除。
/// </summary>
public sealed class TranscodeJob
{
    private readonly FfmpegRunner _runner;
    private readonly AppConfig    _cfg;
    private readonly RecordDb     _db;
    private readonly Func<Task>   _waitIfPaused;
    private readonly OutputPlan   _out;
    private readonly string       _videoEncoder;
    private readonly bool         _twoPass;

    /// <summary>
    /// 本次文件的「有损但可接受」处理留痕（音频重编码、字幕/附加流丢弃）。
    /// 就地替换时原片会被删，这些必须写进记录库，否则用户永远不会知道丢了什么。
    /// 每个 worker 独占一个 TranscodeJob 且 RunAsync 串行执行，故用实例字段安全。
    /// </summary>
    private readonly List<string> _notes = new();

    public TranscodeJob(FfmpegRunner runner, AppConfig cfg, RecordDb db, Func<Task> waitIfPaused,
                        OutputPlan outputPlan, string videoEncoder, bool twoPass)
    {
        _runner       = runner;
        _cfg          = cfg;
        _db           = db;
        _waitIfPaused = waitIfPaused;
        _out          = outputPlan;
        _videoEncoder = videoEncoder;
        _twoPass      = twoPass;
    }

    public async Task<JobOutcome> RunAsync(
        string filePath, IProgress<double>? fileProgress, CancellationToken ct)
    {
        long startedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string jobDir  = Path.Combine(AppPaths.CacheDir, Guid.NewGuid().ToString("N")[..8]);
        string destPath = _out.DestPathFor(filePath);

        MediaInfo? src = null;
        _notes.Clear();
        try
        {
            // ── 1. 探测原片 ──────────────────────────────────────────────
            src = await Probe.RunAsync(_runner, filePath, ct).ConfigureAwait(false);
            if (src is null || !src.HasVideo)
                return Record(filePath, destPath, null, 0, 0, RecordStatus.Failed,
                              "ffprobe 无法解析原片或没有视频流", startedAt);

            // ── 2. 分辨率筛选 ────────────────────────────────────────────
            var v = src.Video!;
            if (_cfg.MinSourceHeight > 0 && v.Height < _cfg.MinSourceHeight)
                return new JobOutcome(JobStatus.Skipped,
                    $"高度 {v.Height} 未达 {_cfg.MinSourceHeight}", 0);
            if (_cfg.EnableMinSourceWidth && _cfg.MinSourceWidth > 0 && v.Width < _cfg.MinSourceWidth)
                return new JobOutcome(JobStatus.Skipped,
                    $"宽度 {v.Width} 未达 {_cfg.MinSourceWidth}", 0);

            // ── 3. 容器：输出必须与源同扩展名 ──────────────────────────────
            if (!ContainerRules.CanOutput(filePath, _cfg.Encoder, out string whyNot))
                return new JobOutcome(JobStatus.Skipped, whyNot, 0);
            var rule = ContainerRules.For(filePath)!;

            // ── 4. 码率：由目标体积反推 ───────────────────────────────────
            bool canCompressAudio = _cfg.AudioPolicy != AudioPolicy.CopyOnly;
            BitratePlan bp;
            try
            {
                bp = BitrateCalc.Compute(src, _cfg, false, canCompressAudio);
                // 用户在预检里确认了「强行继续」⇒ 允许压缩音频腾出视频码率预算
                if (bp.AudioCompressionWouldHelp && _cfg.AllowAudioCompressionForBudget)
                    bp = BitrateCalc.Compute(src, _cfg, true, canCompressAudio);
            }
            catch (Exception ex)
            {
                return Record(filePath, destPath, src, 0, 0, RecordStatus.Failed,
                              "计算目标码率失败: " + ex.Message, startedAt);
            }

            if (bp.Clamped)
                Log.Warning("{File} 触发码率下限钳制（{Bps}），实际体积将超出目标",
                            Path.GetFileName(filePath), bp.TargetVideoBitrate);
            if (bp.Severity != BitrateSeverity.Ok)
                _notes.Add($"目标视频码率 {bp.TargetVideoBitrate / 1000}kbps（{bp.Bpp:F4} bpp）—— {bp.SeverityText}");

            // ── 5. 逐流的音频/字幕/封面计划 ───────────────────────────────
            AudioPlan audioPlan;
            try
            {
                audioPlan = ContainerRules.PlanAudio(rule, src.Audios, _cfg.AudioPolicy,
                                                     forceReencodeAll: bp.AudioWillBeCompressed);
            }
            catch (NotSupportedException ex)
            {
                return Record(filePath, destPath, src, 0, 0, RecordStatus.Failed, ex.Message, startedAt);
            }
            var subPlan   = ContainerRules.PlanSubtitles(rule, src.Subtitles, _cfg.KeepSubtitles);
            bool keepCover = ContainerRules.KeepCoverArt(rule, src);

            foreach (var a in audioPlan.Reencoded)
                _notes.Add($"音轨#{a.OutIndex} 已重编码：{a.Reason}");
            foreach (var s in subPlan.Dropped)
                _notes.Add($"字幕#{s.OutIndex}（{s.Describe}）已丢弃：{s.Reason}");
            if (src.ExtraVideos.Count > 0 && !keepCover)
                _notes.Add($"丢弃 {src.ExtraVideos.Count} 条附加视频流（封面图等）");
            foreach (var n in _notes) Log.Warning("{File}: {Note}", Path.GetFileName(filePath), n);

            var ctx = new EncodeContext(
                VideoEncoder: _videoEncoder, TwoPass: _twoPass, Rule: rule,
                Audio: audioPlan, Subtitles: subPlan, KeepCoverArt: keepCover,
                Color: v.Color, VideoBitrateArg: bp.VideoBitrateArg);

            Directory.CreateDirectory(jobDir);
            string outPath = Path.Combine(jobDir, EncoderArgs.OutFileName(rule));

            Log.Information("开始转码 {File}: {W}x{H} {Codec} {Size:N0}B → {Enc} @ {Bps}，容器 {Ext} → {Dest}",
                Path.GetFileName(filePath), v.Width, v.Height, v.CodecName, src.Size,
                _videoEncoder, bp.VideoBitrateArg, rule.Ext, destPath);

            // ── 6. 编码 ─────────────────────────────────────────────────
            if (_twoPass)
            {
                await _waitIfPaused().ConfigureAwait(false);
                var p1 = await _runner.RunFfmpegWithProgressAsync(
                    EncoderArgs.BuildPass1(filePath, _cfg, ctx), src.Duration,
                    Scaled(fileProgress, 0.0, 0.5), ct, jobDir).ConfigureAwait(false);

                if (!p1.Ok)
                    return EncodeFailed(filePath, destPath, src, bp, startedAt, "第一趟", p1);
            }

            await _waitIfPaused().ConfigureAwait(false);
            var enc = await _runner.RunFfmpegWithProgressAsync(
                EncoderArgs.BuildOutput(filePath, _cfg, ctx), src.Duration,
                Scaled(fileProgress, _twoPass ? 0.5 : 0.0, 1.0), ct, jobDir).ConfigureAwait(false);

            if (!enc.Ok || !File.Exists(outPath))
                return EncodeFailed(filePath, destPath, src, bp, startedAt, _twoPass ? "第二趟" : "编码", enc);

            // ── 7. 验收（全部依据 ffprobe / ffmpeg） ──────────────────────
            var verify = await Verifier.VerifyAsync(_runner, src, outPath, _cfg, ctx, bp.Clamped, ct)
                                       .ConfigureAwait(false);
            if (!verify.Ok)
                return Record(filePath, destPath, src, bp.TargetVideoBitrate,
                              verify.Output?.Size ?? 0, RecordStatus.Failed,
                              "验收未通过: " + verify.Reason, startedAt, verify.Output?.RawJson ?? "");

            // ── 8. 发布成品（文件名与扩展名逐字不变） ──────────────────────
            long outSize = verify.Output!.Size;
            bool overwrite = _out.SaveMode == SaveMode.DirectOverwrite;
            try
            {
                _out.EnsureDestSubdir(filePath);
                await Replacer.PublishAsync(outPath, destPath, overwrite, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return Record(filePath, destPath, src, bp.TargetVideoBitrate, outSize, RecordStatus.Failed,
                              "发布成品失败: " + ex.Message, startedAt, verify.Output.RawJson);
            }

            // ── 9. 登记成品指纹：下次别把它当源片再压一次 ──────────────────
            //     覆盖模式写回源目录时成品与源片同路径，光靠源路径记状态是不够的
            //     （记录库一丢，或递归扫描吃进输出目录，就会二次压缩）。
            try
            {
                string hash = await FileHash.TryComputeAsync(destPath, ct).ConfigureAwait(false);
                _db.AddOutput(destPath, new FileInfo(destPath).Length, hash, filePath);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Warning(ex, "登记成品指纹失败: {Path}", destPath); }

            long saved = Math.Max(0, src.Size - outSize);
            Record(filePath, destPath, src, bp.TargetVideoBitrate, outSize, RecordStatus.Success,
                   "", startedAt, verify.Output.RawJson);
            return new JobOutcome(JobStatus.Success, "", saved);
        }
        catch (OperationCanceledException)
        {
            throw;   // 停止：不落失败记录，源文件未动
        }
        catch (Exception ex)
        {
            Log.Error(ex, "转码 {File} 抛异常", filePath);
            return Record(filePath, destPath, src, 0, 0, RecordStatus.Failed, "异常: " + ex.Message, startedAt);
        }
        finally
        {
            try { if (Directory.Exists(jobDir)) Directory.Delete(jobDir, true); } catch { }
        }
    }

    /// <summary>编码失败。若是硬件编码器初始化出错，清掉探测缓存，下次重探。</summary>
    private JobOutcome EncodeFailed(
        string path, string dest, MediaInfo src, BitratePlan bp, long startedAt,
        string stage, ProcResult r)
    {
        if (H264Support.IsGpuEncoder(_videoEncoder) && LooksLikeEncoderInitFailure(r.Stderr))
            H264Support.InvalidateCache(_cfg);

        return Record(path, dest, src, bp.TargetVideoBitrate, 0, RecordStatus.Failed,
                      $"{stage}失败 (exit={r.ExitCode}): {Tail(r.Stderr)}", startedAt);
    }

    private static bool LooksLikeEncoderInitFailure(string stderr) =>
        stderr.Contains("Cannot load", StringComparison.OrdinalIgnoreCase) ||
        stderr.Contains("No capable devices", StringComparison.OrdinalIgnoreCase) ||
        stderr.Contains("Error initializing", StringComparison.OrdinalIgnoreCase) ||
        stderr.Contains("Error creating", StringComparison.OrdinalIgnoreCase) ||
        stderr.Contains("not supported", StringComparison.OrdinalIgnoreCase);

    /// <summary>把 0..1 的子进度映射到 [lo, hi] 区间。</summary>
    private static IProgress<double>? Scaled(IProgress<double>? outer, double lo, double hi)
        => outer is null ? null : new Progress<double>(p => outer.Report(lo + (hi - lo) * p));

    private JobOutcome Record(
        string path, string dest, MediaInfo? src, long targetBps, long outSize,
        string status, string reason, long startedAt, string probeAfter = "")
    {
        var row = new RecordRow
        {
            FilePath      = path,
            FileName      = Path.GetFileName(path),
            DestPath      = dest,
            OrigSize      = src?.Size ?? 0,
            OrigWidth     = src?.Video?.Width ?? 0,
            OrigHeight    = src?.Video?.Height ?? 0,
            OrigCodec     = src?.Video?.CodecName ?? "",
            OrigDuration  = src?.Duration ?? 0,
            OrigBitrate   = src?.BitRate ?? 0,
            TargetBitrate = targetBps,
            OutSize       = outSize,
            Ratio         = (src?.Size ?? 0) > 0 && outSize > 0 ? (double)outSize / src!.Size : 0,
            Status        = status,
            FailReason    = reason,
            Notes         = string.Join("; ", _notes),
            StartedAt     = startedAt,
            FinishedAt    = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ProbeBefore   = src?.RawJson ?? "",
            ProbeAfter    = probeAfter,
        };
        try { _db.Upsert(row); } catch (Exception ex) { Log.Error(ex, "写记录库失败"); }

        if (status == RecordStatus.Failed) Log.Warning("失败 {File}: {Reason}", row.FileName, reason);
        return new JobOutcome(
            status == RecordStatus.Success ? JobStatus.Success : JobStatus.Failed,
            reason, row.SavedBytes);
    }

    private static string Tail(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return "(无 stderr)";
        var s = stderr.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length > 300 ? "…" + s[^300..] : s;
    }
}
