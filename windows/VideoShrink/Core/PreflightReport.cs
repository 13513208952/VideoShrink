using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace VideoShrink.Core;

/// <summary>某个文件在转换中会发生的、不可逆的信息损失或质量风险。</summary>
public sealed class FileRisk
{
    public string FilePath { get; init; } = "";
    public string FileName => Path.GetFileName(FilePath);

    /// <summary>会被丢弃的东西（逐条，注明是什么、为什么）。</summary>
    public List<string> Losses { get; } = new();

    /// <summary>会被重编码（有损）的音轨描述。</summary>
    public List<string> AudioReencodes { get; } = new();

    public BitrateSeverity Severity { get; set; } = BitrateSeverity.Ok;

    /// <summary>视频码率过低的说明（含分辨率/码率/bpp）。</summary>
    public string BitrateNote { get; set; } = "";

    /// <summary>视频码率之所以救不回来，是因为音频吃掉了预算；压缩音频能改善。</summary>
    public bool AudioCompressionWouldHelp { get; set; }

    /// <summary>这个文件在当前设置下必定失败（如 CopyOnly 策略遇到不兼容音频）。</summary>
    public string? FatalReason { get; set; }

    public bool HasAny =>
        Losses.Count > 0 || AudioReencodes.Count > 0 ||
        Severity != BitrateSeverity.Ok || FatalReason is not null;
}

/// <summary>
/// 扫描完成后、<b>任何编码开始前</b>汇总的风险清单。
/// 扫描阶段已经 ffprobe 过每个文件，这里不产生额外的 IO 成本。
/// </summary>
public sealed class PreflightReport
{
    public SaveMode SaveMode { get; init; }
    public List<FileRisk> Risks { get; } = new();

    public bool HasAny      => Risks.Count > 0;
    public bool AnyCritical => Risks.Any(r => r.Severity == BitrateSeverity.Critical);
    public bool AnyFatal    => Risks.Any(r => r.FatalReason is not null);

    /// <summary>有文件因音频吃满预算而画面崩坏 => 弹窗必须写明「音频将被迫压缩」。</summary>
    public bool AudioWillBeForciblyCompressed =>
        Risks.Any(r => r.AudioCompressionWouldHelp);

    /// <summary>成品会覆盖/删除源文件 => 损失不可恢复，用强警告措辞。</summary>
    public bool Destructive => SaveMode == SaveMode.DirectOverwrite;

    public int TotalLosses => Risks.Sum(r => r.Losses.Count);

    /// <summary>
    /// 逐个文件做「假编码计划」，把会丢的东西、会被重编码的音轨、过低的码率全找出来。
    /// 不写任何文件，不启动编码。
    /// </summary>
    public static async Task<PreflightReport> BuildAsync(
        FfmpegRunner runner, AppConfig cfg, SaveMode saveMode,
        IEnumerable<string> files, CancellationToken ct)
    {
        var report = new PreflightReport { SaveMode = saveMode };

        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();

            var src = await Probe.RunAsync(runner, f, ct).ConfigureAwait(false);
            if (src is null || !src.HasVideo) continue;

            var rule = ContainerRules.For(f);
            if (rule is null) continue;

            var risk = new FileRisk { FilePath = f };

            // -- 音频
            try
            {
                var ap = ContainerRules.PlanAudio(rule, src.Audios, cfg.AudioPolicy);
                foreach (var a in ap.Reencoded)
                    risk.AudioReencodes.Add($"音轨#{a.OutIndex}（{src.Audios[a.OutIndex].Describe()}）：{a.Reason}");
            }
            catch (NotSupportedException ex)
            {
                risk.FatalReason = ex.Message;   // CopyOnly 撞上不兼容音频
            }

            // -- 字幕
            var sp = ContainerRules.PlanSubtitles(rule, src.Subtitles, cfg.KeepSubtitles);
            foreach (var s in sp.Dropped)
                risk.Losses.Add($"字幕#{s.OutIndex}（{s.Describe}）将被丢弃：{s.Reason}");

            // -- 封面图 / 附加视频流
            if (src.ExtraVideos.Count > 0 && !ContainerRules.KeepCoverArt(rule, src))
                risk.Losses.Add($"{src.ExtraVideos.Count} 条附加视频流（封面图等）将被丢弃：{rule.Ext} 不支持");

            // -- 附件（mkv 字体）
            if (!rule.SupportsAttachments && src.Subtitles.Any(s => s.CodecName is "ass" or "ssa"))
                risk.Losses.Add($"ASS/SSA 字幕的字体附件将丢失：{rule.Ext} 不支持附件");

            // -- 码率
            try
            {
                bool canCompressAudio = cfg.AudioPolicy != AudioPolicy.CopyOnly;
                var bp = BitrateCalc.Compute(src, cfg, false, canCompressAudio);
                risk.Severity = bp.Severity;
                risk.AudioCompressionWouldHelp = bp.AudioCompressionWouldHelp;

                if (bp.Severity != BitrateSeverity.Ok)
                {
                    var v = src.Video!;
                    risk.BitrateNote =
                        $"目标视频码率 {bp.TargetVideoBitrate / 1000} kbps @ {v.Width}x{v.Height}" +
                        $"（{bp.Bpp:F4} bpp）—— {bp.SeverityText}";
                    if (bp.AudioCompressionWouldHelp)
                        risk.BitrateNote += "；音频占用了大量预算，继续将<b>被迫压缩音频</b>";
                }
            }
            catch (Exception ex)
            {
                risk.FatalReason ??= "无法计算目标码率: " + ex.Message;
            }

            if (risk.HasAny) report.Risks.Add(risk);
        }

        Log.Information("预检完成：{N} 个文件存在风险（丢失 {L} 项，严重码率过低 {C} 个）",
            report.Risks.Count, report.TotalLosses,
            report.Risks.Count(r => r.Severity == BitrateSeverity.Critical));

        return report;
    }

    /// <summary>给弹窗用的纯文本摘要。</summary>
    public string BuildSummary()
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine(Destructive
            ? "警告：当前为「直接文件保存」模式：原文件将被永久替换，以下内容不可恢复地丢失。"
            : "当前为「新建文件夹保存」模式：原文件保留不动，但成品会缺少以下内容。");
        sb.AppendLine();

        if (Destructive)
            sb.AppendLine("另外：目标路径下的同名文件将被直接覆盖，且不会对其做任何校验。").AppendLine();

        if (AudioWillBeForciblyCompressed)
            sb.AppendLine("警告：有文件的音频占满了体积预算，继续将被迫压缩音频（有损）。").AppendLine();

        foreach (var r in Risks)
        {
            sb.AppendLine($"文件：{r.FileName}");
            if (r.FatalReason is not null) sb.AppendLine($"    必定失败：{r.FatalReason}");
            foreach (var l in r.Losses)         sb.AppendLine($"    丢弃：{l}");
            foreach (var a in r.AudioReencodes) sb.AppendLine($"    重编码：{a}");
            if (r.Severity != BitrateSeverity.Ok)
                sb.AppendLine($"    码率：{r.BitrateNote.Replace("<b>", "").Replace("</b>", "")}");
        }

        return sb.ToString();
    }
}
