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

/// <summary>单条音轨的处置。逐流决策 —— 一条不兼容不该拖累其余全部重编码。</summary>
public sealed record AudioStreamAction(int OutIndex, AudioAction Action, string Encoder, string Reason);

public sealed record AudioPlan(IReadOnlyList<AudioStreamAction> Streams)
{
    public bool AnyReencoded => Streams.Any(s => s.Action == AudioAction.Reencode);
    public IEnumerable<AudioStreamAction> Reencoded => Streams.Where(s => s.Action == AudioAction.Reencode);
    public static readonly AudioPlan None = new(Array.Empty<AudioStreamAction>());
}

/// <summary>单条字幕的处置。只丢真正装不下的那几条。</summary>
public sealed record SubtitleStreamAction(int OutIndex, bool Keep, string Codec, string Reason, string Describe);

public sealed record SubtitlePlan(IReadOnlyList<SubtitleStreamAction> Streams)
{
    public IEnumerable<SubtitleStreamAction> Kept    => Streams.Where(s => s.Keep);
    public IEnumerable<SubtitleStreamAction> Dropped => Streams.Where(s => !s.Keep);
    public int KeptCount    => Kept.Count();
    public int DroppedCount => Dropped.Count();
    public bool AnyKept     => KeptCount > 0;
    public static readonly SubtitlePlan None = new(Array.Empty<SubtitleStreamAction>());
}

/// <summary>一种输出容器的能力描述。</summary>
public sealed record ContainerRule(
    string Ext,
    bool   IsMp4Family,          // 可用 -movflags +faststart 与 -tag:v hvc1
    bool   SupportsAttachments,  // mkv 的字体附件
    bool   SupportsCoverArt,     // 封面图（attached_pic）
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
///
/// <b>逐流原则</b>：无法完整保留时「能保住多少保住多少」—— 能拷的拷、能转的转、
/// 只丢真正装不下的那几条，并逐条记账。
/// </summary>
public static class ContainerRules
{
    /// <summary>文本字幕（可转成 mov_text 塞进 mp4/mov）；图形字幕（pgs/dvd）则不行。</summary>
    private static readonly string[] TextSubs =
        { "subrip", "srt", "ass", "ssa", "mov_text", "webvtt", "text" };

    private static readonly ContainerRule[] Rules =
    {
        // 以下音频白名单均以实测 muxer 行为为准，不是照抄规格书
        new(".mp4", IsMp4Family: true, SupportsAttachments: false, SupportsCoverArt: true,
            VideoCodecs:   new[] { "hevc", "av1", "h264" },
            AudioCodecs:   new[] { "aac", "ac3", "eac3", "mp3", "alac", "opus", "flac" },
            AudioFallback: "aac",
            SubtitleCodecs: new[] { "mov_text" },
            SubtitleTranscode: "mov_text", FormatNameMatch: "mp4"),

        // .m4v 走的是 ffmpeg 的 ipod muxer，比 mp4 严格得多：
        // 实测只接受 aac / ac3 / alac —— mp3 与 eac3 会直接报 "Could not find tag for codec"。
        // 列错了的后果是 -c:a copy 硬失败，整个文件白跑。
        new(".m4v", IsMp4Family: true, SupportsAttachments: false, SupportsCoverArt: true,
            VideoCodecs:   new[] { "hevc", "av1", "h264" },
            AudioCodecs:   new[] { "aac", "ac3", "alac" },
            AudioFallback: "aac",
            SubtitleCodecs: new[] { "mov_text" },
            SubtitleTranscode: "mov_text", FormatNameMatch: "mp4"),

        new(".mov", IsMp4Family: true, SupportsAttachments: false, SupportsCoverArt: true,
            VideoCodecs:   new[] { "hevc", "av1", "h264" },
            AudioCodecs:   new[] { "aac", "ac3", "eac3", "mp3", "alac",
                                   "pcm_s16le", "pcm_s24le", "pcm_s16be", "pcm_s24be" },
            AudioFallback: "aac",
            SubtitleCodecs: new[] { "mov_text" },
            SubtitleTranscode: "mov_text", FormatNameMatch: "mov"),

        // mkv 几乎什么都装得下
        new(".mkv", IsMp4Family: false, SupportsAttachments: true, SupportsCoverArt: true,
            VideoCodecs:   new[] { "hevc", "av1", "h264" },
            AudioCodecs:   new[] { "aac", "ac3", "eac3", "dts", "truehd", "flac", "opus", "vorbis",
                                   "mp3", "mp2", "alac", "pcm_s16le", "pcm_s24le" },
            AudioFallback: "libopus",
            SubtitleCodecs: new[] { "subrip", "srt", "ass", "ssa", "webvtt",
                                    "hdmv_pgs_subtitle", "dvd_subtitle" },
            SubtitleTranscode: "", FormatNameMatch: "matroska"),

        // webm 只认 AV1/VP9 + Opus/Vorbis —— 选 x265 / H.264 时它根本装不下
        new(".webm", IsMp4Family: false, SupportsAttachments: false, SupportsCoverArt: false,
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

    // ── 音频：逐流 ────────────────────────────────────────────────────

    /// <summary>
    /// 每条音轨独立判定 copy / reencode。一条不兼容<b>不会</b>拖累其余全部重编码。
    /// <paramref name="forceReencodeAll"/>：预检里用户同意「压缩音频腾出视频码率预算」时置真。
    /// </summary>
    public static AudioPlan PlanAudio(
        ContainerRule rule, IReadOnlyList<StreamInfo> audios, AudioPolicy policy,
        bool forceReencodeAll = false)
    {
        if (audios.Count == 0) return AudioPlan.None;

        var actions = new List<AudioStreamAction>(audios.Count);
        for (int i = 0; i < audios.Count; i++)
        {
            var a = audios[i];
            bool compatible = rule.AudioCodecs.Contains(a.CodecName, StringComparer.OrdinalIgnoreCase);

            if (compatible && !forceReencodeAll)
            {
                actions.Add(new AudioStreamAction(i, AudioAction.Copy, "", "直拷（容器兼容）"));
                continue;
            }

            string why = forceReencodeAll && compatible
                ? "为腾出视频码率预算而压缩音频（用户已确认）"
                : $"{rule.Ext} 容不下 {a.CodecName}";

            if (policy == AudioPolicy.CopyOnly && !forceReencodeAll)
                throw new NotSupportedException($"音轨#{i}（{a.Describe()}）：{why}，且策略为 CopyOnly（不允许重编码）");

            actions.Add(new AudioStreamAction(i, AudioAction.Reencode, rule.AudioFallback,
                                              $"{why} → 重编码为 {rule.AudioFallback}"));
        }
        return new AudioPlan(actions);
    }

    // ── 字幕：逐流 ────────────────────────────────────────────────────

    /// <summary>
    /// 每条字幕独立判定。容器接受则直拷；mp4/mov 下文本字幕转 mov_text；
    /// <b>只丢真正装不下的那几条</b>（如 PGS），其余全部保留。
    /// </summary>
    public static SubtitlePlan PlanSubtitles(
        ContainerRule rule, IReadOnlyList<StreamInfo> subs, bool keepSubtitles)
    {
        if (subs.Count == 0) return SubtitlePlan.None;

        var actions = new List<SubtitleStreamAction>(subs.Count);
        for (int i = 0; i < subs.Count; i++)
        {
            var s = subs[i];
            string desc = s.Describe();

            if (!keepSubtitles)
            {
                actions.Add(new SubtitleStreamAction(i, false, "", "用户关闭了字幕保留", desc));
                continue;
            }
            if (rule.SubtitleCodecs.Length == 0)
            {
                actions.Add(new SubtitleStreamAction(i, false, "", $"{rule.Ext} 不支持字幕", desc));
                continue;
            }
            if (rule.SubtitleCodecs.Contains(s.CodecName, StringComparer.OrdinalIgnoreCase))
            {
                actions.Add(new SubtitleStreamAction(i, true, "copy", "直拷", desc));
                continue;
            }
            if (!string.IsNullOrEmpty(rule.SubtitleTranscode) &&
                TextSubs.Contains(s.CodecName, StringComparer.OrdinalIgnoreCase))
            {
                actions.Add(new SubtitleStreamAction(i, true, rule.SubtitleTranscode,
                                                     $"文本字幕转为 {rule.SubtitleTranscode}", desc));
                continue;
            }

            actions.Add(new SubtitleStreamAction(i, false, "",
                $"{rule.Ext} 装不下字幕编码 {s.CodecName}（图形字幕无法转换）", desc));
        }
        return new SubtitlePlan(actions);
    }

    // ── 封面图 ────────────────────────────────────────────────────────

    /// <summary>容器支持且源片有封面图时保留（此前是整条丢弃）。</summary>
    public static bool KeepCoverArt(ContainerRule rule, MediaInfo src) =>
        rule.SupportsCoverArt && src.CoverArts.Count > 0;
}
