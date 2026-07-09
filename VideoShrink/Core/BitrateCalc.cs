using System;

namespace VideoShrink.Core;

public sealed record BitratePlan(
    long TargetVideoBitrate,   // bps
    long AssumedAudioBitrate,  // bps（直拷，占 15% 预算的一部分）
    long TargetTotalBytes,
    bool Clamped)              // 触发下限钳制 ⇒ 实际体积会超出目标比例
{
    /// <summary>ffmpeg -b:v 的取值（整千 bps，便于读日志）。</summary>
    public string VideoBitrateArg => $"{TargetVideoBitrate / 1000}k";
}

public static class BitrateCalc
{
    /// <summary>容器/索引开销余量。</summary>
    private const double ContainerOverhead = 0.015;

    /// <summary>视频码率下限；低于此值画面必然崩坏，宁可超出体积目标。</summary>
    public const long MinVideoBitrate = 700_000;

    /// <summary>音频码率未知时的保守估计。</summary>
    private const long FallbackAudioBitrate = 192_000;

    /// <summary>
    /// 逐文件计算目标视频码率：
    ///   targetTotal = ratio × 原字节
    ///   audioBytes  = 音频码率/8 × 时长（音频直拷，计入预算）
    ///   videoBytes  = targetTotal × (1 − 容器开销) − audioBytes
    ///   videoBitrate= videoBytes × 8 / 时长
    /// </summary>
    public static BitratePlan Compute(MediaInfo src, double targetRatio)
    {
        if (src.Duration <= 0)
            throw new ArgumentException("时长无效（ffprobe 未给出 duration），无法计算目标码率");
        if (targetRatio <= 0 || targetRatio >= 1)
            throw new ArgumentOutOfRangeException(nameof(targetRatio));

        long origBytes = src.Size;
        if (origBytes <= 0)
            throw new ArgumentException("原文件体积无效（ffprobe 未给出 size）");

        long audioBps = 0;
        foreach (var a in src.Audios)
            audioBps += a.BitRate > 0 ? a.BitRate : FallbackAudioBitrate;

        long targetTotalBytes = (long)(origBytes * targetRatio);
        double audioBytes     = audioBps / 8.0 * src.Duration;
        double videoBytes     = targetTotalBytes * (1 - ContainerOverhead) - audioBytes;

        long videoBps = videoBytes > 0
            ? (long)(videoBytes * 8.0 / src.Duration)
            : 0;

        bool clamped = false;
        if (videoBps < MinVideoBitrate)
        {
            videoBps = MinVideoBitrate;
            clamped  = true;
        }

        // 取整到 1kbps，避免 -b:v 出现奇怪小数
        videoBps = videoBps / 1000 * 1000;

        return new BitratePlan(videoBps, audioBps, targetTotalBytes, clamped);
    }
}
