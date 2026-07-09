using System;
using System.Linq;

namespace VideoShrink.Core;

/// <summary>视频目标码率的健康度。</summary>
public enum BitrateSeverity
{
    /// <summary>正常。</summary>
    Ok,
    /// <summary>偏低：可见劣化。</summary>
    Low,
    /// <summary>严重过低：画面必然崩坏。</summary>
    Critical,
}

public sealed record BitratePlan(
    long TargetVideoBitrate,        // bps
    long PlannedAudioBitrate,       // bps（直拷 = 源码率；压缩 = 目标码率）
    long TargetTotalBytes,
    bool AudioWillBeCompressed,     // 本计划里音频是否会被重编码来腾预算
    bool AudioCompressionWouldHelp, // 尚未压缩，但压缩能显著改善视频码率
    bool Clamped,                   // 触发码率下限钳制 => 实际体积会超出目标
    double Bpp,                     // 每像素每帧比特数
    BitrateSeverity Severity)
{
    /// <summary>ffmpeg -b:v 的取值。</summary>
    public string VideoBitrateArg => $"{TargetVideoBitrate / 1000}k";

    public string SeverityText => Severity switch
    {
        BitrateSeverity.Critical => "严重过低（画面必然崩坏）",
        BitrateSeverity.Low      => "偏低（可见劣化）",
        _ => "正常",
    };
}

public static class BitrateCalc
{
    /// <summary>容器/索引开销余量。</summary>
    private const double ContainerOverhead = 0.015;

    /// <summary>
    /// 比例模式下的视频码率下限。低于此值画面必然崩坏，宁可超出体积目标。
    /// <b>绝对体积模式下不钳制</b> —— 那里用户要的就是精确的体积，钳了就命不中。
    /// </summary>
    public const long MinVideoBitrate = 700_000;

    /// <summary>音频码率未知时的保守估计。</summary>
    private const long FallbackAudioBitrate = 192_000;

    /// <summary>
    /// 音频「不压缩」的上限。320kbps 是为了容忍 MP3 的音质极限：
    /// 源音频不超过它就默认直拷（但仍计入体积预算）；超过它（无损/DTS 等）本就是压缩候选。
    /// </summary>
    public const long AudioCopyCeiling = 320_000;

    // 「码率过低」判据：每像素每帧比特数，与分辨率/帧率无关
    private const double BppCritical = 0.015;
    private const double BppLow      = 0.030;
    private const long   AbsoluteFloorBps = 300_000;

    /// <summary>源音频总码率（直拷时占用的预算）。</summary>
    public static long AudioBitrateIfCopied(MediaInfo src) =>
        src.Audios.Sum(a => a.BitRate > 0 ? a.BitRate : FallbackAudioBitrate);

    /// <summary>全部音轨压缩后的总码率。</summary>
    public static long AudioBitrateIfCompressed(MediaInfo src, AppConfig cfg) =>
        (long)src.Audios.Count * cfg.AudioReencodeBitrateK * 1000;

    /// <summary>
    /// 由目标体积反推视频码率。
    ///
    /// <code>
    /// targetTotal  = Ratio ? 原字节 x ratio : TargetBytes
    /// audioBytes   = 计划音频码率/8 x 时长      // 音频即便直拷也要计入预算
    /// videoBytes   = targetTotal x (1 - 容器开销) - audioBytes
    /// videoBitrate = videoBytes x 8 / 时长
    /// </code>
    ///
    /// 音频规则：源音频总码率 <= 320kbps => 默认直拷；超过则默认压缩。
    /// <paramref name="forceAudioCompression"/> 为真时（用户在预检警告里选了强行继续）
    /// 一律压缩音频，以腾出视频码率预算。
    /// <paramref name="audioCompressionAllowed"/> 为假时（AudioPolicy = CopyOnly）
    /// 音频<b>绝不</b>被压缩，哪怕它把视频码率吃干净——用户明确要求了不许动音频。
    /// </summary>
    public static BitratePlan Compute(
        MediaInfo src, AppConfig cfg,
        bool forceAudioCompression = false,
        bool audioCompressionAllowed = true)
    {
        if (src.Duration <= 0)
            throw new ArgumentException("时长无效（ffprobe 未给出 duration）");
        if (src.Size <= 0)
            throw new ArgumentException("原文件体积无效（ffprobe 未给出 size）");

        long targetTotalBytes = cfg.SizeTargetMode switch
        {
            SizeTargetMode.Ratio => (long)(src.Size * ValidRatio(cfg.TargetRatio)),
            _ => cfg.TargetBytes > 0
                    ? cfg.TargetBytes
                    : throw new ArgumentException("目标体积必须为正数"),
        };

        long audioCopy       = AudioBitrateIfCopied(src);
        long audioCompressed = AudioBitrateIfCompressed(src, cfg);

        // 源音频 > 320kbps（无损/DTS）本就是压缩候选；用户确认后也一律压缩。
        // CopyOnly 策略下一律不压。
        bool compress = audioCompressionAllowed && (forceAudioCompression || audioCopy > AudioCopyCeiling);
        long audioBps = compress ? Math.Min(audioCompressed, audioCopy) : audioCopy;

        long videoBps = VideoBpsFor(targetTotalBytes, audioBps, src.Duration);

        bool clamped = false;
        if (cfg.SizeTargetMode == SizeTargetMode.Ratio && videoBps < MinVideoBitrate)
        {
            videoBps = MinVideoBitrate;
            clamped  = true;
        }
        if (videoBps < 1000) videoBps = 1000;         // 别喂给 ffmpeg 一个 0
        videoBps = videoBps / 1000 * 1000;            // 取整到 kbps

        double bpp = Bpp(videoBps, src.Video);
        var severity = SeverityOf(videoBps, bpp);

        // 尚未压缩音频，但压缩能把视频码率抬上来 => 预检里要提示「音频将被迫压缩」
        bool wouldHelp = false;
        if (audioCompressionAllowed && !compress && severity != BitrateSeverity.Ok && audioCompressed < audioCopy)
        {
            long better = VideoBpsFor(targetTotalBytes, audioCompressed, src.Duration);
            wouldHelp = SeverityOf(better, Bpp(better, src.Video)) < severity;
        }

        return new BitratePlan(
            TargetVideoBitrate: videoBps,
            PlannedAudioBitrate: audioBps,
            TargetTotalBytes: targetTotalBytes,
            AudioWillBeCompressed: compress,
            AudioCompressionWouldHelp: wouldHelp,
            Clamped: clamped,
            Bpp: bpp,
            Severity: severity);
    }

    /// <summary>
    /// 验收时的体积上界（字节）。
    /// 比例模式随目标比例自适应，避免用户把目标比例调高后整批被判失败。
    /// </summary>
    public static long UpperBoundBytes(MediaInfo src, AppConfig cfg) =>
        cfg.SizeTargetMode == SizeTargetMode.Ratio
            ? (long)(src.Size * Math.Max(cfg.MaxOutRatio, ValidRatio(cfg.TargetRatio) * 1.35))
            : (long)(cfg.TargetBytes * (1 + Math.Max(0, cfg.AbsoluteOverTolerance)));

    // -- 内部 ----------------------------------------------------------

    private static double ValidRatio(double r) =>
        r > 0 && r < 1 ? r : throw new ArgumentOutOfRangeException(nameof(r), "目标比例须在 (0,1) 之间");

    private static long VideoBpsFor(long targetTotalBytes, long audioBps, double duration)
    {
        double audioBytes = audioBps / 8.0 * duration;
        double videoBytes = targetTotalBytes * (1 - ContainerOverhead) - audioBytes;
        return videoBytes > 0 ? (long)(videoBytes * 8.0 / duration) : 0;
    }

    private static double Bpp(long videoBps, StreamInfo? v)
    {
        if (v is null || v.Width <= 0 || v.Height <= 0) return double.NaN;
        double fps = v.Fps > 0 ? v.Fps : 25.0;
        return videoBps / (v.Width * (double)v.Height * fps);
    }

    private static BitrateSeverity SeverityOf(long videoBps, double bpp)
    {
        if (videoBps < AbsoluteFloorBps) return BitrateSeverity.Critical;
        if (double.IsNaN(bpp))           return BitrateSeverity.Ok;   // 分辨率未知，不臆断
        if (bpp < BppCritical)           return BitrateSeverity.Critical;
        if (bpp < BppLow)                return BitrateSeverity.Low;
        return BitrateSeverity.Ok;
    }
}
