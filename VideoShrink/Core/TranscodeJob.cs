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
/// 单个文件的完整流水线：ffprobe → 算目标码率 → 两趟 ABR 编码到 cache
/// → ffprobe 验收（+整片解码） → 原子替换原片 → 落库。
/// 任何一步失败：原片原封不动，记录失败原因，返回 Failed（由引擎跳到下一个）。
/// </summary>
public sealed class TranscodeJob
{
    private readonly FfmpegRunner _runner;
    private readonly AppConfig    _cfg;
    private readonly RecordDb     _db;
    private readonly Func<Task>   _waitIfPaused;

    /// <summary>
    /// 本次文件的「有损但可接受」处理留痕（音频重编码、字幕/附加流丢弃）。
    /// 原片马上就要被删，这些必须写进记录库，否则用户永远不会知道丢了什么。
    /// 每个 worker 独占一个 TranscodeJob 且 RunAsync 串行执行，故用实例字段安全。
    /// </summary>
    private readonly List<string> _notes = new();

    public TranscodeJob(FfmpegRunner runner, AppConfig cfg, RecordDb db, Func<Task> waitIfPaused)
    {
        _runner       = runner;
        _cfg          = cfg;
        _db           = db;
        _waitIfPaused = waitIfPaused;
    }

    public async Task<JobOutcome> RunAsync(
        string filePath, IProgress<double>? fileProgress, CancellationToken ct)
    {
        long startedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string jobDir  = Path.Combine(AppPaths.CacheDir, Guid.NewGuid().ToString("N")[..8]);

        MediaInfo? src = null;
        _notes.Clear();
        try
        {
            // ── 1. 探测原片 ──────────────────────────────────────────────
            src = await Probe.RunAsync(_runner, filePath, ct).ConfigureAwait(false);
            if (src is null || !src.HasVideo)
                return Record(filePath, null, 0, 0, RecordStatus.Failed,
                              "ffprobe 无法解析原片或没有视频流", startedAt);

            // ── 2. 筛选：分辨率未达门槛视为「已转换」，跳过（天然幂等/增量） ──
            if (src.Video!.Height < _cfg.MinSourceHeight)
                return new JobOutcome(JobStatus.Skipped,
                    $"分辨率 {src.Video.Width}x{src.Video.Height} 未达 {_cfg.MinSourceHeight}，跳过", 0);

            // ── 3. 容器：输出必须与源同扩展名（就地替换的硬约束） ──────────
            if (!ContainerRules.CanOutput(filePath, _cfg.Encoder, out string whyNot))
                return new JobOutcome(JobStatus.Skipped, whyNot, 0);
            var rule = ContainerRules.For(filePath)!;

            // ── 4. 音频 / 字幕计划（可能因 CopyOnly 策略而直接判失败） ──────
            AudioPlan audioPlan;
            try { audioPlan = ContainerRules.PlanAudio(rule, src.Audios, _cfg.AudioPolicy); }
            catch (NotSupportedException ex)
            {
                return Record(filePath, src, 0, 0, RecordStatus.Failed, ex.Message, startedAt);
            }
            var subPlan = ContainerRules.PlanSubtitles(rule, src.Subtitles, _cfg.KeepSubtitles);

            if (audioPlan.Reencoded)      _notes.Add("音频已重编码：" + audioPlan.Reason);
            if (subPlan.DroppedCount > 0) _notes.Add(subPlan.Reason);
            if (src.ExtraVideoStreams > 0)
                _notes.Add($"丢弃 {src.ExtraVideoStreams} 条附加视频流（封面图等）");
            foreach (var n in _notes) Log.Warning("{File}: {Note}", Path.GetFileName(filePath), n);

            // ── 5. 目标码率 ──────────────────────────────────────────────
            BitratePlan plan;
            try { plan = BitrateCalc.Compute(src, _cfg.TargetRatio); }
            catch (Exception ex)
            {
                return Record(filePath, src, 0, 0, RecordStatus.Failed,
                              "计算目标码率失败: " + ex.Message, startedAt);
            }

            if (plan.Clamped)
                Log.Warning("{File} 触发码率下限钳制（{Bps}），实际体积将超出 {Ratio:P0} 目标",
                            Path.GetFileName(filePath), plan.TargetVideoBitrate, _cfg.TargetRatio);

            Directory.CreateDirectory(jobDir);
            string outPath = Path.Combine(jobDir, EncoderArgs.OutFileName(rule));

            Log.Information("开始转码 {File}: {W}x{H} {Codec} {Size:N0}B → {Th}p @ {Bps}，容器 {Ext}，{Audio}",
                Path.GetFileName(filePath), src.Video.Width, src.Video.Height,
                src.Video.CodecName, src.Size, _cfg.TargetHeight, plan.VideoBitrateArg,
                rule.Ext, audioPlan.Reason);

            // ── 4. 第一趟（收集统计；进度占前 50%） ───────────────────────
            await _waitIfPaused().ConfigureAwait(false);
            var p1Args = EncoderArgs.BuildPass1(filePath, _cfg, plan.VideoBitrateArg);
            var p1 = await _runner.RunFfmpegWithProgressAsync(
                p1Args, src.Duration,
                Scaled(fileProgress, 0.0, 0.5), ct, jobDir).ConfigureAwait(false);

            if (!p1.Ok)
                return Record(filePath, src, plan.TargetVideoBitrate, 0, RecordStatus.Failed,
                              $"第一趟失败 (exit={p1.ExitCode}): {Tail(p1.Stderr)}", startedAt);

            // ── 5. 第二趟（实际产出；进度占后 50%） ───────────────────────
            await _waitIfPaused().ConfigureAwait(false);
            var p2Args = EncoderArgs.BuildPass2(
                filePath, _cfg, plan.VideoBitrateArg, rule, audioPlan, subPlan, src.Video.Color);
            var p2 = await _runner.RunFfmpegWithProgressAsync(
                p2Args, src.Duration,
                Scaled(fileProgress, 0.5, 1.0), ct, jobDir).ConfigureAwait(false);

            if (!p2.Ok || !File.Exists(outPath))
                return Record(filePath, src, plan.TargetVideoBitrate, 0, RecordStatus.Failed,
                              $"第二趟失败 (exit={p2.ExitCode}): {Tail(p2.Stderr)}", startedAt);

            // ── 6. 验收（全部依据 ffprobe / ffmpeg） ──────────────────────
            var v = await Verifier.VerifyAsync(
                _runner, src, outPath, _cfg, rule, audioPlan, subPlan, plan.Clamped, ct).ConfigureAwait(false);

            if (!v.Ok)
                return Record(filePath, src, plan.TargetVideoBitrate,
                              v.Output?.Size ?? 0, RecordStatus.Failed,
                              "验收未通过: " + v.Reason, startedAt, v.Output?.RawJson ?? "");

            // ── 7. 只有全部通过，才替换原片（文件名逐字不差） ──────────────
            long outSize = v.Output!.Size;
            try
            {
                await Replacer.ReplaceAsync(filePath, outPath, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return Record(filePath, src, plan.TargetVideoBitrate, outSize, RecordStatus.Failed,
                              "替换原片失败: " + ex.Message, startedAt, v.Output.RawJson);
            }

            long saved = Math.Max(0, src.Size - outSize);
            Record(filePath, src, plan.TargetVideoBitrate, outSize, RecordStatus.Success,
                   "", startedAt, v.Output.RawJson);
            return new JobOutcome(JobStatus.Success, "", saved);
        }
        catch (OperationCanceledException)
        {
            throw;   // 停止：不落失败记录，原片未动
        }
        catch (Exception ex)
        {
            Log.Error(ex, "转码 {File} 抛异常", filePath);
            return Record(filePath, src, 0, 0, RecordStatus.Failed, "异常: " + ex.Message, startedAt);
        }
        finally
        {
            try { if (Directory.Exists(jobDir)) Directory.Delete(jobDir, true); } catch { }
        }
    }

    /// <summary>把 0..1 的子进度映射到 [lo, hi] 区间（两趟各占一半）。</summary>
    private static IProgress<double>? Scaled(IProgress<double>? outer, double lo, double hi)
        => outer is null ? null : new Progress<double>(p => outer.Report(lo + (hi - lo) * p));

    private JobOutcome Record(
        string path, MediaInfo? src, long targetBps, long outSize,
        string status, string reason, long startedAt, string probeAfter = "")
    {
        var row = new RecordRow
        {
            FilePath      = path,
            FileName      = Path.GetFileName(path),
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
