using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VideoShrink.Core;

public enum AudioPolicy
{
    /// <summary>默认：容器容得下就直拷；容不下就回落到该容器的安全编码，并明确记录。</summary>
    Auto,
    /// <summary>绝不重编码音频；容器容不下即判失败（原片不动）。</summary>
    CopyOnly,
}

public enum AudioAction { Copy, Reencode }

public sealed record AudioPlan(AudioAction Action, string Encoder, string Reason)
{
    public bool Reencoded => Action == AudioAction.Reencode;
}

public sealed record SubtitlePlan(bool Keep, string Codec, int DroppedCount, string Reason);

/// <summary>
/// 一种输出容器的能力描述。
/// </summary>
public sealed record ContainerRule(
    string Ext,
    bool   IsMp4Family,          // 可用 -movflags +faststart 与 -tag:v hvc1
    bool   SupportsAttachments,  // mkv 的字体附件
    string[] VideoCodecs,        // 允许承载的视频编码（ffprobe 口径）
    string[] AudioCodecs,        // 允许直拷的音频编码（ffprobe 口径）
    string   AudioFallback,      // 直拷不成时的回落编码器（ffmpeg 口径）
    string[] SubtitleCodecs,     // 允许直拷的字幕编码；空数组 = 不支持字幕
    string   SubtitleTranscode,  // 文本字幕需转码时的目标（如 mov_text）；空 = 不转
    string   FormatNameMatch)    // 验收时比对 ffprobe 的 format_name 应含此子串
{
    /// <summary>成品的容器类型是否真的对得上扩展名（防「MP4 数据装进 .mkv」这种静默毁片）。</summary>
    public bool FormatMatches(string ffprobeFormatName) =>
        ffprobeFormatName.Contains(FormatNameMatch, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 由**源文件扩展名**决定输出容器与可用参数。
///
/// 这修的是一个会当场毁数据的 bug：原先无论输入什么，都编码成 out.mp4，
/// 再改名成原文件名 —— 于是 <c>movie.mkv</c> 变成「装着 MP4 数据、却叫 .mkv」的文件，而原片已被删。
/// 就地替换必须保持扩展名，因此**容器由源扩展名反推**，容不下就在扫描阶段直接排除。
/// </summary>
public static class ContainerRules
{
    // 文本字幕（可被转成 mov_text 塞进 mp4/mov）；图形字幕（pgs/dvd）则不行
    private static readonly string[] TextSubs = { "subrip", "srt", "ass", "ssa", "mov_text", "webvtt", "text" };

    private static readonly ContainerRule[] Rules =
    {
        new(".mp4", IsMp4Family: true, SupportsAttachments: false,
            VideoCodecs:   new[] { "hevc", "av1" },
            AudioCodecs:   new[] { "aac", "ac3", "eac3", "mp3", "alac", "opus" },
            AudioFallback: "aac",
            SubtitleCodecs: new[] { "mov_text" },
            SubtitleTranscode: "mov_text", FormatNameMatch: "mp4"),

        new(".m4v", IsMp4Family: true, SupportsAttachments: false,
            VideoCodecs:   new[] { "hevc", "av1" },
            AudioCodecs:   new[] { "aac", "ac3", "eac3", "mp3", "alac" },
            AudioFallback: "aac",
            SubtitleCodecs: new[] { "mov_text" },
            SubtitleTranscode: "mov_text", FormatNameMatch: "mp4"),

        new(".mov", IsMp4Family: true, SupportsAttachments: false,
            VideoCodecs:   new[] { "hevc", "av1" },
            AudioCodecs:   new[] { "aac", "ac3", "eac3", "mp3", "alac",
                                   "pcm_s16le", "pcm_s24le", "pcm_s16be", "pcm_s24be" },
            AudioFallback: "aac",
            SubtitleCodecs: new[] { "mov_text" },
            SubtitleTranscode: "mov_text", FormatNameMatch: "mov"),

        // mkv 几乎什么都装得下
        new(".mkv", IsMp4Family: false, SupportsAttachments: true,
            VideoCodecs:   new[] { "hevc", "av1" },
            AudioCodecs:   new[] { "aac", "ac3", "eac3", "dts", "truehd", "flac", "opus", "vorbis",
                                   "mp3", "mp2", "alac", "pcm_s16le", "pcm_s24le" },
            AudioFallback: "libopus",
            SubtitleCodecs: new[] { "subrip", "srt", "ass", "ssa", "webvtt",
                                    "hdmv_pgs_subtitle", "dvd_subtitle" },
            SubtitleTranscode: "", FormatNameMatch: "matroska"),

        // webm 只认 AV1/VP9 + Opus/Vorbis —— 选 x265 时它根本装不下
        new(".webm", IsMp4Family: false, SupportsAttachments: false,
            VideoCodecs:   new[] { "av1" },
            AudioCodecs:   new[] { "opus", "vorbis" },
            AudioFallback: "libopus",
            SubtitleCodecs: new[] { "webvtt" },
            SubtitleTranscode: "", FormatNameMatch: "webm"),
    };

    /// <summary>扫描阶段会考察的扩展名（能否真正作为输出容器，见 <see cref="CanOutput"/>）。</summary>
    public static IReadOnlyList<string> KnownExtensions { get; } = Rules.Select(r => r.Ext).ToArray();

    public static ContainerRule? For(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        foreach (var r in Rules) if (r.Ext == ext) return r;
        return null;   // .ts / .flv 等：我们不作为输出容器
    }

    /// <summary>该编码器的产物能否装进这个容器。</summary>
    public static bool CanOutput(string path, EncoderKind enc, out string reason)
    {
        var rule = For(path);
        if (rule is null)
        {
            reason = $"不支持把 {Path.GetExtension(path)} 作为输出容器（就地替换必须保持扩展名）";
            return false;
        }

        string codec = EncoderArgs.ExpectedCodecName(enc);
        if (!rule.VideoCodecs.Contains(codec, StringComparer.OrdinalIgnoreCase))
        {
            reason = $"{rule.Ext} 装不下 {codec}（改用其它编码器，或换容器）";
            return false;
        }

        reason = "";
        return true;
    }

    /// <summary>音频：容器容得下就直拷；容不下按策略回落或判失败。</summary>
    public static AudioPlan PlanAudio(ContainerRule rule, IReadOnlyList<StreamInfo> audios, AudioPolicy policy)
    {
        if (audios.Count == 0)
            return new AudioPlan(AudioAction.Copy, "", "无音频流");

        var bad = audios
            .Where(a => !rule.AudioCodecs.Contains(a.CodecName, StringComparer.OrdinalIgnoreCase))
            .Select(a => a.CodecName)
            .Distinct()
            .ToList();

        if (bad.Count == 0)
            return new AudioPlan(AudioAction.Copy, "", "音频直拷（容器兼容）");

        string why = $"{rule.Ext} 容不下音频编码 {string.Join("/", bad)}";
        if (policy == AudioPolicy.CopyOnly)
            throw new NotSupportedException(why + "，且策略为 CopyOnly（不允许重编码）");

        return new AudioPlan(AudioAction.Reencode, rule.AudioFallback,
                             $"{why} → 重编码为 {rule.AudioFallback}");
    }

    /// <summary>字幕：能直拷就直拷；需转码且是文本字幕则转码；图形字幕装不进就明确丢弃并记账。</summary>
    public static SubtitlePlan PlanSubtitles(ContainerRule rule, IReadOnlyList<StreamInfo> subs, bool keepSubtitles)
    {
        if (subs.Count == 0)                return new SubtitlePlan(false, "", 0, "");
        if (!keepSubtitles)                 return new SubtitlePlan(false, "", subs.Count, "用户关闭了字幕保留");
        if (rule.SubtitleCodecs.Length == 0)
            return new SubtitlePlan(false, "", subs.Count, $"{rule.Ext} 不支持字幕");

        bool allCopyable = subs.All(s => rule.SubtitleCodecs.Contains(s.CodecName, StringComparer.OrdinalIgnoreCase));
        if (allCopyable)
            return new SubtitlePlan(true, "copy", 0, "字幕直拷");

        // mp4/mov：文本字幕可转 mov_text；图形字幕（pgs/dvd）转不了
        if (!string.IsNullOrEmpty(rule.SubtitleTranscode) &&
            subs.All(s => TextSubs.Contains(s.CodecName, StringComparer.OrdinalIgnoreCase)))
            return new SubtitlePlan(true, rule.SubtitleTranscode, 0, $"文本字幕转为 {rule.SubtitleTranscode}");

        var kinds = subs.Select(s => s.CodecName).Distinct();
        return new SubtitlePlan(false, "", subs.Count,
            $"{rule.Ext} 装不下字幕编码 {string.Join("/", kinds)}，已丢弃 {subs.Count} 条");
    }
}
