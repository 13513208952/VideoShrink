using System;
using System.Collections.Generic;
using System.Globalization;

namespace VideoShrink.Core;

/// <summary>
/// 构造 ffmpeg 两趟 ABR 命令行。
///
/// 为什么是 2-pass ABR 而不是 CRF：「压到原体积 N%」是硬指标，只有 ABR 能命中；CRF 体积不可控。
/// 为什么 stats 用相对文件名：x265-params 以 ':' 分隔，Windows 盘符里的 ':' 会把参数劈开——
/// 故一律把工作目录设为 jobDir，stats 只写裸文件名。
/// 为什么输出扩展名跟着源走：就地替换必须保持文件名，容器与扩展名必须一致（见 ContainerRules）。
/// </summary>
public static class EncoderArgs
{
    public const string StatsFileName = "pass.log";

    /// <summary>缓存里的成品文件名 —— 扩展名必须与源一致，ffmpeg 据此选 muxer。</summary>
    public static string OutFileName(ContainerRule rule) => "out" + rule.Ext;

    private static string Scale(int height) => $"scale=-2:{height}:flags=lanczos";

    /// <summary>
    /// 滤镜链：缩放 +（若源片标注了色彩）用 setparams 显式打标。
    ///
    /// 为什么用 setparams 而不是 -color_primaries 这类命令行选项：实测（本仓库 ffmpeg 构建）
    /// 当输入帧本身不带色彩属性时，滤镜链路属性会覆盖掉命令行选项，标签根本写不进去；
    /// 而 setparams 直接改写链路属性，两种情形下都生效。源片未标注的项不写——绝不臆造，
    /// 因为写错标签比不写更糟（播放器会照着错的去解释像素）。
    /// </summary>
    private static string FilterChain(int height, ColorMeta c)
    {
        string vf = Scale(height);

        var p = new List<string>();
        if (!ColorMeta.IsUnset(c.Primaries)) p.Add($"color_primaries={c.Primaries}");
        if (!ColorMeta.IsUnset(c.Transfer))  p.Add($"color_trc={c.Transfer}");
        if (!ColorMeta.IsUnset(c.Space))     p.Add($"colorspace={c.Space}");
        if (!ColorMeta.IsUnset(c.Range))     p.Add($"range={c.Range}");

        if (p.Count > 0) vf += ",setparams=" + string.Join(":", p);
        return vf;
    }

    /// <summary>
    /// SVT-AV1 的 preset 是数字（0–13），x265 是字符串——二者来自 AppConfig 的<b>两个独立字段</b>。
    /// 这里只做区间夹取，绝不会出现「解析失败 → 静默降级」。
    /// </summary>
    private static string Av1Preset(AppConfig cfg) =>
        EncoderPresets.ClampAv1(cfg.PresetAv1).ToString(CultureInfo.InvariantCulture);

    /// <summary>当前编码器实际生效的 preset（写日志用，让用户看得见跑的到底是哪档）。</summary>
    public static string EffectivePreset(AppConfig cfg) =>
        cfg.Encoder == EncoderKind.X265 ? cfg.Preset : Av1Preset(cfg);

    /// <summary>期望的输出视频编码名（ffprobe 口径）。</summary>
    public static string ExpectedCodecName(EncoderKind enc) =>
        enc == EncoderKind.X265 ? "hevc" : "av1";

    // ── 第一趟：只为收集统计，输出丢进空设备 ────────────────────────────
    public static List<string> BuildPass1(string srcPath, AppConfig cfg, string videoBitrateArg)
    {
        var a = new List<string>
        {
            // -progress / -nostats 是全局选项，放最前最稳妥
            "-hide_banner", "-nostdin", "-y", "-nostats", "-progress", "pipe:1",
            "-i", srcPath,
            "-map", "0:v:0",
            "-vf", Scale(cfg.TargetHeight),
            "-pix_fmt", "yuv420p10le",
            "-b:v", videoBitrateArg,
        };

        AddVideoCodec(a, cfg, pass: 1);

        a.AddRange(new[] { "-an", "-sn", "-f", "null" });
        a.Add("NUL");   // Windows 空设备；绝不能用 "-"，那会污染我们解析进度的 stdout
        return a;
    }

    // ── 第二趟：真正产出 ────────────────────────────────────────────────
    public static List<string> BuildPass2(
        string srcPath, AppConfig cfg, string videoBitrateArg,
        ContainerRule rule, AudioPlan audio, SubtitlePlan subs, ColorMeta srcColor)
    {
        var a = new List<string>
        {
            "-hide_banner", "-nostdin", "-y", "-nostats", "-progress", "pipe:1",
            "-i", srcPath,
        };

        // ── 流映射：视频首条 + 全部音频 (+ 字幕 + mkv 附件)
        a.AddRange(new[] { "-map", "0:v:0" });
        a.AddRange(new[] { "-map", "0:a?" });            // 有音频就带上，没有也不报错
        if (subs.Keep) a.AddRange(new[] { "-map", "0:s?" });
        if (rule.SupportsAttachments) a.AddRange(new[] { "-map", "0:t?" });

        // 元数据与章节：不显式映射就会被丢弃，而原片马上要被删
        a.AddRange(new[] { "-map_metadata", "0", "-map_chapters", "0" });

        a.AddRange(new[] { "-vf", FilterChain(cfg.TargetHeight, srcColor) });
        a.AddRange(new[] { "-pix_fmt", "yuv420p10le" });
        a.AddRange(new[] { "-b:v", videoBitrateArg });

        AddVideoCodec(a, cfg, pass: 2);

        if (rule.IsMp4Family && cfg.Encoder == EncoderKind.X265)
            a.AddRange(new[] { "-tag:v", "hvc1" });      // QuickTime/Apple 生态识别 HEVC

        // ── 音频
        if (audio.Action == AudioAction.Copy)
        {
            a.AddRange(new[] { "-c:a", "copy" });        // 零损失
        }
        else
        {
            a.AddRange(new[] { "-c:a", audio.Encoder });
            a.AddRange(new[] { "-b:a", $"{cfg.AudioReencodeBitrateK}k" });
        }

        // ── 字幕 / 附件
        if (subs.Keep) a.AddRange(new[] { "-c:s", subs.Codec });
        if (rule.SupportsAttachments) a.AddRange(new[] { "-c:t", "copy" });

        if (rule.IsMp4Family) a.AddRange(new[] { "-movflags", "+faststart" });

        a.Add(OutFileName(rule));                        // 相对工作目录，扩展名 = 源扩展名
        return a;
    }

    private static void AddVideoCodec(List<string> a, AppConfig cfg, int pass)
    {
        if (cfg.Encoder == EncoderKind.X265)
        {
            a.AddRange(new[] { "-c:v", "libx265", "-preset", cfg.Preset });
            // slow-firstpass=0：第一趟用更快的设置，只为收集统计，省大量时间
            a.AddRange(new[] { "-x265-params",
                pass == 1 ? $"pass=1:stats={StatsFileName}:slow-firstpass=0"
                          : $"pass=2:stats={StatsFileName}" });
        }
        else
        {
            a.AddRange(new[] { "-c:v", "libsvtav1", "-preset", Av1Preset(cfg) });
            a.AddRange(new[] { "-pass", pass.ToString(CultureInfo.InvariantCulture), "-passlogfile", "pass" });
        }
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
