using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace VideoShrink.Core;

public enum EncoderKind
{
    /// <summary>libx265（默认）：高压缩率 + 通用硬解，归档可随手播。</summary>
    X265,
    /// <summary>libsvtav1：压缩率更高但更慢、播放兼容性差。</summary>
    SvtAv1,
}

/// <summary>持久化配置（exe 旁 settings.json）。</summary>
public class AppConfig
{
    /// <summary>用户手动指定的目标文件夹。</summary>
    [JsonPropertyName("target_dir")]
    public string TargetDir { get; set; } = "";

    /// <summary>目标体积比例（默认 0.15 = 压到原体积 15%）。</summary>
    [JsonPropertyName("target_ratio")]
    public double TargetRatio { get; set; } = 0.15;

    /// <summary>目标高度（默认 1080）。</summary>
    [JsonPropertyName("target_height")]
    public int TargetHeight { get; set; } = 1080;

    /// <summary>体积阈值（字节）：只处理大于此值的文件。默认 1GB。</summary>
    [JsonPropertyName("min_size_bytes")]
    public long MinSizeBytes { get; set; } = 1L * 1024 * 1024 * 1024;

    /// <summary>源分辨率下限：只处理 height ≥ 此值的文件（4K = 2160）。</summary>
    [JsonPropertyName("min_source_height")]
    public int MinSourceHeight { get; set; } = 2160;

    /// <summary>
    /// x265 preset（<b>仅在 Encoder = X265 时生效</b>）。字符串：veryslow…veryfast。
    /// 固定体积下更慢的 preset 只花时间、换来的全是画质。
    /// </summary>
    [JsonPropertyName("preset")]
    public string Preset { get; set; } = "slow";

    /// <summary>
    /// SVT-AV1 preset（<b>仅在 Encoder = SvtAv1 时生效</b>）。数字 0–13：
    /// 0 最慢、压缩效率最高；13 最快、压缩效率最低。2 = 公认的质量甜点极限。
    ///
    /// 独立于 <see cref="Preset"/> 的理由：SVT-AV1 只认数字，
    /// 若与 x265 共用一个字符串字段，"veryslow" 会被解析失败并<b>静默降级</b>，用户毫无察觉。
    /// </summary>
    [JsonPropertyName("preset_av1")]
    public int PresetAv1 { get; set; } = 2;

    [JsonPropertyName("encoder")]
    public EncoderKind Encoder { get; set; } = EncoderKind.X265;

    /// <summary>是否递归扫描子目录。</summary>
    [JsonPropertyName("recursive")]
    public bool Recursive { get; set; } = false;

    /// <summary>是否重试此前失败的文件。</summary>
    [JsonPropertyName("retry_failed")]
    public bool RetryFailed { get; set; } = false;

    /// <summary>并行任务数（默认 1；多核机可设 2 提升总吞吐）。</summary>
    [JsonPropertyName("parallelism")]
    public int Parallelism { get; set; } = 1;

    // ── 音频 / 字幕 ───────────────────────────────────────────────────
    /// <summary>
    /// Auto（默认）：容器容得下就直拷，容不下回落到安全编码并明确记录。
    /// CopyOnly：绝不重编码音频，容器容不下即判失败（原片不动）。
    /// </summary>
    [JsonPropertyName("audio_policy")]
    public AudioPolicy AudioPolicy { get; set; } = AudioPolicy.Auto;

    /// <summary>音频回落重编码时的码率（kbps）。</summary>
    [JsonPropertyName("audio_reencode_bitrate_k")]
    public int AudioReencodeBitrateK { get; set; } = 192;

    /// <summary>保留字幕流（容器支持时直拷或转码；不支持则丢弃并记账）。</summary>
    [JsonPropertyName("keep_subtitles")]
    public bool KeepSubtitles { get; set; } = true;

    /// <summary>验收：整片解码校验（ffmpeg -f null）。关掉可提速但降低安全性。</summary>
    [JsonPropertyName("full_decode_check")]
    public bool FullDecodeCheck { get; set; } = true;

    // ── 验收容差 ──────────────────────────────────────────────────────
    /// <summary>
    /// 输出体积相对原片的可接受下界。<b>&lt;=0 表示关闭（默认）。</b>
    ///
    /// 默认关闭的理由：「压得远小于目标」是内容极好压的合法结果，不是编码异常。
    /// 大段静止的录屏/动画/演示视频，ABR 物理上花不掉目标码率，成品自然远低于下界，
    /// 一旦开启就会被无理由判失败（实测已发生：某录屏只压出 2.1%，被 5% 下界误杀）。
    /// 文件完整性由时长/帧率/音频流/整片解码四道关把守，不需要体积下界来兜底。
    /// </summary>
    [JsonPropertyName("min_out_ratio")]
    public double MinOutRatio { get; set; } = 0.0;

    /// <summary>输出体积相对原片的可接受上界。</summary>
    [JsonPropertyName("max_out_ratio")]
    public double MaxOutRatio { get; set; } = 0.25;

    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsPath))
            {
                var json = File.ReadAllText(AppPaths.SettingsPath);
                return JsonSerializer.Deserialize<AppConfig>(json, _opts) ?? new AppConfig();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "读取 settings.json 失败，使用默认配置");
        }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(AppPaths.SettingsPath, JsonSerializer.Serialize(this, _opts));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "写入 settings.json 失败");
        }
    }
}
