using System;
using System.Collections.Generic;
using System.Globalization;

namespace VideoShrink.Core;

/// <summary>一次编码所需的全部决策，由 TranscodeJob 备齐后传进来。</summary>
public sealed record EncodeContext(
    string        VideoEncoder,   // ffmpeg 编码器名：libx265 / libsvtav1 / libx264 / h264_nvenc …
    bool          TwoPass,        // CPU 编码器走两趟 ABR；GPU 只能单趟 VBR
    ContainerRule Rule,
    AudioPlan     Audio,
    SubtitlePlan  Subtitles,
    bool          KeepCoverArt,
    ColorMeta     Color,
    string        VideoBitrateArg);

/// <summary>
/// 构造 ffmpeg 命令行。
///
/// <b>速率控制</b>：
/// - CPU（libx265 / libsvtav1 / libx264）→ <b>两趟 ABR</b>，精确命中目标体积。
/// - GPU（h264_nvenc / h264_qsv / h264_amf）→ ffmpeg 的通用两趟对它们不可用，
///   只能<b>单趟 VBR + maxrate/bufsize</b>，体积精度较低（由验收的体积上界兜底）。
///
/// <b>stats 用相对文件名</b>：x265-params 以 ':' 分隔，Windows 盘符里的 ':' 会把参数劈开——
/// 故一律把工作目录设为 jobDir，stats 只写裸文件名。
///
/// <b>输出扩展名跟着源走</b>：就地替换必须保持文件名，容器与扩展名必须一致（见 ContainerRules）。
///
/// <b>所有视频编码器选项都用 <c>:v:0</c> 限定</b>：保留封面图后输出有两条视频流，
/// 不限定的话 -vf / -preset / -b:v 会一并作用到封面图上。
/// </summary>
public static class EncoderArgs
{
    public const string StatsFileName = "pass.log";

    /// <summary>缓存里的成品文件名 —— 扩展名必须与源一致，ffmpeg 据此选 muxer。</summary>
    public static string OutFileName(ContainerRule rule) => "out" + rule.Ext;

    /// <summary>期望的输出视频编码名（ffprobe 口径）。</summary>
    public static string ExpectedCodecName(EncoderKind enc) => enc switch
    {
        EncoderKind.X265   => "hevc",
        EncoderKind.SvtAv1 => "av1",
        _                  => "h264",
    };

    /// <summary>CPU 编码器才支持通用两趟 ABR。</summary>
    public static bool SupportsTwoPass(string ffmpegEncoder) =>
        ffmpegEncoder is "libx265" or "libsvtav1" or "libx264";

    /// <summary>H.264 的 GPU 编码器只吃 8-bit；x265/AV1 用 10-bit 更省码率也更少色带。</summary>
    private static string PixFmt(EncoderKind kind) =>
        kind == EncoderKind.H264 ? "yuv420p" : "yuv420p10le";

    /// <summary>
    /// SVT-AV1 的 preset 是数字（0–13），x265/x264 是字符串——来自 AppConfig 的<b>独立字段</b>。
    /// 这里只做区间夹取，绝不会出现「解析失败 → 静默降级」。
    /// </summary>
    private static string Av1Preset(AppConfig cfg) =>
        EncoderPresets.ClampAv1(cfg.PresetAv1).ToString(CultureInfo.InvariantCulture);

    /// <summary>当前编码器实际生效的 preset（写日志用，让用户看得见跑的到底是哪档）。</summary>
    public static string EffectivePreset(AppConfig cfg, string ffmpegEncoder) => ffmpegEncoder switch
    {
        "libx265"   => cfg.Preset,
        "libsvtav1" => Av1Preset(cfg),
        "libx264"   => cfg.PresetX264,
        _           => "(硬件编码器)",
    };

    // ── 滤镜链 ────────────────────────────────────────────────────────
    private static string Resize(AppConfig cfg) =>
        cfg.ResizeMode == ResizeMode.Stretch
            ? $"scale={cfg.TargetWidth}:{cfg.TargetHeight}:flags=lanczos,setsar=1"
            : $"scale=-2:{cfg.TargetHeight}:flags=lanczos";

    /// <summary>
    /// 缩放 +（若源片标注了色彩）用 setparams 显式打标。
    ///
    /// 为什么用 setparams 而不是 -color_primaries 这类命令行选项：实测（本仓库 ffmpeg 构建）
    /// 当输入帧本身不带色彩属性时，滤镜链路属性会覆盖掉命令行选项，标签根本写不进去；
    /// 而 setparams 直接改写链路属性，两种情形下都生效。源片未标注的项不写——绝不臆造，
    /// 因为写错标签比不写更糟（播放器会照着错的去解释像素）。
    /// </summary>
    private static string FilterChain(AppConfig cfg, ColorMeta c)
    {
        string vf = Resize(cfg);

        var p = new List<string>();
        if (!ColorMeta.IsUnset(c.Primaries)) p.Add($"color_primaries={c.Primaries}");
        if (!ColorMeta.IsUnset(c.Transfer))  p.Add($"color_trc={c.Transfer}");
        if (!ColorMeta.IsUnset(c.Space))     p.Add($"colorspace={c.Space}");
        if (!ColorMeta.IsUnset(c.Range))     p.Add($"range={c.Range}");

        if (p.Count > 0) vf += ",setparams=" + string.Join(":", p);
        return vf;
    }

    // ── 视频编码器参数 ────────────────────────────────────────────────

    /// <summary>
    /// H.264 各实现的质量档参数（不含码率）。
    /// <b>试编码就用这一组参数</b> —— 这样「参数是否被该编码器接受」也会被验证到，
    /// 而不是等真跑的时候才炸。
    /// </summary>
    public static List<string> H264QualityArgs(string encoder, AppConfig cfg) => encoder switch
    {
        "h264_nvenc" => new() { "-preset:v:0", "p5", "-tune:v:0", "hq", "-rc:v:0", "vbr" },
        "h264_qsv"   => new() { "-preset:v:0", "slower" },
        "h264_amf"   => new() { "-quality:v:0", "quality", "-rc:v:0", "vbr_peak" },
        "libx264"    => new() { "-preset:v:0", cfg.PresetX264 },
        _            => new(),
    };

    /// <summary>视频编码器 + preset + 速率控制。pass: 1/2 = 两趟；0 = 单趟。</summary>
    private static void AddVideoCodec(List<string> a, AppConfig cfg, EncodeContext ctx, int pass)
    {
        a.AddRange(new[] { "-c:v:0", ctx.VideoEncoder });
        a.AddRange(new[] { "-b:v:0", ctx.VideoBitrateArg });

        switch (ctx.VideoEncoder)
        {
            case "libx265":
                a.AddRange(new[] { "-preset:v:0", cfg.Preset });
                a.AddRange(new[] { "-x265-params:v:0",
                    pass == 1 ? $"pass=1:stats={StatsFileName}:slow-firstpass=0"
                              : $"pass=2:stats={StatsFileName}" });
                break;

            case "libsvtav1":
                a.AddRange(new[] { "-preset:v:0", Av1Preset(cfg) });
                a.AddRange(new[] { "-pass:v:0", pass.ToString(CultureInfo.InvariantCulture),
                                   "-passlogfile", "pass" });
                break;

            case "libx264":
                a.AddRange(new[] { "-preset:v:0", cfg.PresetX264 });
                a.AddRange(new[] { "-pass:v:0", pass.ToString(CultureInfo.InvariantCulture),
                                   "-passlogfile", "pass" });
                break;

            default:   // GPU：单趟 VBR + 码率天花板
                a.AddRange(H264QualityArgs(ctx.VideoEncoder, cfg));
                long bps = ParseKbps(ctx.VideoBitrateArg);
                a.AddRange(new[] { "-maxrate:v:0", $"{bps * 3 / 2}k", "-bufsize:v:0", $"{bps * 3}k" });
                break;
        }
    }

    private static long ParseKbps(string arg) =>
        long.TryParse(arg.TrimEnd('k', 'K'), out var k) ? k : 1000;

    /// <summary>GPU 解码卸载：只加速解码，不碰编码，压缩效率零损失。缩放仍留在 CPU。</summary>
    private static void AddHwAccel(List<string> a, AppConfig cfg)
    {
        if (cfg.GpuDecode) a.AddRange(new[] { "-hwaccel", "auto" });
    }

    // ── 第一趟：只为收集统计，输出丢进空设备 ────────────────────────────
    public static List<string> BuildPass1(string srcPath, AppConfig cfg, EncodeContext ctx)
    {
        var a = new List<string> { "-hide_banner", "-nostdin", "-y", "-nostats", "-progress", "pipe:1" };
        AddHwAccel(a, cfg);
        a.AddRange(new[] { "-i", srcPath });
        a.AddRange(new[] { "-map", "0:v:0" });
        a.AddRange(new[] { "-filter:v:0", FilterChain(cfg, ctx.Color) });
        a.AddRange(new[] { "-pix_fmt:v:0", PixFmt(cfg.Encoder) });

        AddVideoCodec(a, cfg, ctx, pass: 1);

        a.AddRange(new[] { "-an", "-sn", "-f", "null" });
        a.Add("NUL");   // Windows 空设备；绝不能用 "-"，那会污染我们解析进度的 stdout
        return a;
    }

    // ── 产出（两趟的第二趟，或 GPU 的单趟） ──────────────────────────────
    public static List<string> BuildOutput(string srcPath, AppConfig cfg, EncodeContext ctx)
    {
        var rule = ctx.Rule;
        var a = new List<string> { "-hide_banner", "-nostdin", "-y", "-nostats", "-progress", "pipe:1" };
        AddHwAccel(a, cfg);
        a.AddRange(new[] { "-i", srcPath });

        // ── 流映射：正片 → 封面图 → 全部音轨 → 保留的字幕 → mkv 附件
        a.AddRange(new[] { "-map", "0:v:0" });
        if (ctx.KeepCoverArt) a.AddRange(new[] { "-map", "0:v:m:attached_pic?" });
        foreach (var s in ctx.Audio.Streams) a.AddRange(new[] { "-map", $"0:a:{s.OutIndex}" });
        foreach (var s in ctx.Subtitles.Kept) a.AddRange(new[] { "-map", $"0:s:{s.OutIndex}" });
        if (rule.SupportsAttachments) a.AddRange(new[] { "-map", "0:t?" });

        // 元数据与章节：不显式映射就会被丢弃，而原片可能马上要被删
        a.AddRange(new[] { "-map_metadata", "0", "-map_chapters", "0" });

        // ── 正片
        a.AddRange(new[] { "-filter:v:0", FilterChain(cfg, ctx.Color) });
        a.AddRange(new[] { "-pix_fmt:v:0", PixFmt(cfg.Encoder) });
        AddVideoCodec(a, cfg, ctx, pass: ctx.TwoPass ? 2 : 0);

        if (rule.IsMp4Family && cfg.Encoder == EncoderKind.X265)
            a.AddRange(new[] { "-tag:v:0", "hvc1" });   // QuickTime/Apple 生态识别 HEVC

        // ── 封面图：原样拷贝，别拿去编码
        if (ctx.KeepCoverArt)
        {
            a.AddRange(new[] { "-c:v:1", "copy" });
            a.AddRange(new[] { "-disposition:v:1", "attached_pic" });
        }

        // ── 音频：逐流决策
        foreach (var s in ctx.Audio.Streams)
        {
            if (s.Action == AudioAction.Copy)
            {
                a.AddRange(new[] { $"-c:a:{s.OutIndex}", "copy" });          // 零损失
            }
            else
            {
                a.AddRange(new[] { $"-c:a:{s.OutIndex}", s.Encoder });
                a.AddRange(new[] { $"-b:a:{s.OutIndex}", $"{cfg.AudioReencodeBitrateK}k" });
            }
        }

        // ── 字幕：逐流决策（输出侧的序号是「保留下来的」那几条的序号）
        int outSub = 0;
        foreach (var s in ctx.Subtitles.Kept)
            a.AddRange(new[] { $"-c:s:{outSub++}", s.Codec });

        if (rule.SupportsAttachments) a.AddRange(new[] { "-c:t", "copy" });
        if (rule.IsMp4Family)         a.AddRange(new[] { "-movflags", "+faststart" });

        a.Add(OutFileName(rule));   // 相对工作目录，扩展名 = 源扩展名
        return a;
    }

    /// <summary>整片解码校验：-xerror 遇错即非零退出；stderr 应为空。</summary>
    public static List<string> BuildFullDecodeCheck(string path) => new()
    {
        "-hide_banner", "-nostdin",
        "-v", "error", "-xerror",
        "-i", path,
        "-f", "null", "NUL",
    };
}
