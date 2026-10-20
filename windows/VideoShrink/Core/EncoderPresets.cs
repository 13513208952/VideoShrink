using System.Collections.Generic;

namespace VideoShrink.Core;

/// <summary>一个 preset 选项：值 + 给人看的说明。</summary>
public sealed record PresetOption<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// 两套 preset 的唯一事实来源。
///
/// <b>关键提醒</b>：SVT-AV1 的 preset 是 <b>数字 0-13</b>，x265 的是 <b>字符串</b>（slow/veryslow...）。
/// 二者绝不能共用一个字段——把 "veryslow" 喂给 SVT-AV1 会被静默降级，用户毫无察觉。
///
/// 两套列表都按「压缩率从高到低 / 速度从慢到快」排序，
/// 所以列表越靠上 = 压得越狠越慢，越靠下 = 越快但文件越大（同画质下）。
/// </summary>
public static class EncoderPresets
{
    /// <summary>
    /// SVT-AV1：0 最慢、压缩效率最高；13 最快、压缩效率最低。
    /// 固定体积目标下，preset 只影响画质与耗时，不影响成品体积。
    /// </summary>
    public static readonly IReadOnlyList<PresetOption<int>> Av1 = new[]
    {
        new PresetOption<int>(0,  "0 — 极限压缩，研究级（慢到不实用）"),
        new PresetOption<int>(1,  "1 — 极限压缩（收益零头，时间翻倍）"),
        new PresetOption<int>(2,  "2 — 最高实用压缩率，质量甜点极限（归档推荐）"),
        new PresetOption<int>(3,  "3 — 很高压缩率"),
        new PresetOption<int>(4,  "4 — 高压缩率，速度平衡点"),
        new PresetOption<int>(5,  "5 — 中等压缩率"),
        new PresetOption<int>(6,  "6 — 偏快，压缩率一般"),
        new PresetOption<int>(7,  "7 — 快"),
        new PresetOption<int>(8,  "8 — 快，压缩率偏低"),
        new PresetOption<int>(9,  "9 — 很快"),
        new PresetOption<int>(10, "10 — 很快，压缩率低"),
        new PresetOption<int>(11, "11 — 极快"),
        new PresetOption<int>(12, "12 — 极快，压缩率最低"),
        new PresetOption<int>(13, "13 — 最快，近实时（压缩率最差）"),
    };

    /// <summary>libx264（H.264 的 CPU 实现）：与 x265 同名同序。</summary>
    public static readonly IReadOnlyList<PresetOption<string>> X264 = new[]
    {
        new PresetOption<string>("veryslow",  "veryslow — 最高压缩率，极慢"),
        new PresetOption<string>("slower",    "slower — 很高压缩率"),
        new PresetOption<string>("slow",      "slow — 高压缩率（推荐）"),
        new PresetOption<string>("medium",    "medium — 均衡（x264 默认）"),
        new PresetOption<string>("fast",      "fast — 偏快，压缩率下降"),
        new PresetOption<string>("faster",    "faster — 快，压缩率低"),
        new PresetOption<string>("veryfast",  "veryfast — 极快，压缩率最低"),
    };

    /// <summary>x265：veryslow 压缩效率最高，ultrafast 最低。</summary>
    public static readonly IReadOnlyList<PresetOption<string>> X265 = new[]
    {
        new PresetOption<string>("veryslow",  "veryslow — 最高压缩率，极慢"),
        new PresetOption<string>("slower",    "slower — 很高压缩率"),
        new PresetOption<string>("slow",      "slow — 高压缩率（归档推荐）"),
        new PresetOption<string>("medium",    "medium — 均衡（x265 默认）"),
        new PresetOption<string>("fast",      "fast — 偏快，压缩率下降"),
        new PresetOption<string>("faster",    "faster — 快，压缩率低"),
        new PresetOption<string>("veryfast",  "veryfast — 极快，压缩率最低"),
    };

    public const int Av1Min = 0;
    public const int Av1Max = 13;

    /// <summary>把任意输入夹到合法的 AV1 preset 区间。</summary>
    public static int ClampAv1(int p) => p < Av1Min ? Av1Min : (p > Av1Max ? Av1Max : p);

    /// <summary>x265 preset 名是否合法。</summary>
    public static bool IsValidX265(string p)
    {
        foreach (var o in X265) if (o.Value == p) return true;
        return p is "placebo" or "superfast" or "ultrafast";
    }
}
