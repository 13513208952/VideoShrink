using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace VideoShrink.Core;

public enum EncoderKind
{
    /// <summary>libx265：高压缩率 + 通用硬解。为压缩率而生（CPU，慢）。</summary>
    X265,
    /// <summary>libsvtav1：压缩率最高，也最慢。为压缩率而生（CPU）。</summary>
    SvtAv1,
    /// <summary>H.264：给普通用户的常规档。默认走 GPU 加速，兼顾速度与效果。</summary>
    H264,
}

/// <summary>H.264 的具体实现。GPU 各家是三套不同的 ffmpeg 编码器。</summary>
public enum H264Impl
{
    /// <summary>探测硬件：独显 &gt; 核显 &gt; CPU，并用 1 帧试编码验证。</summary>
    Auto,
    Nvenc,   // NVIDIA
    Qsv,     // Intel
    Amf,     // AMD
    /// <summary>libx264（CPU）。唯一支持两趟 ABR、能精确命中体积的 H.264 实现。</summary>
    Cpu,
}

/// <summary>成品往哪个目录放。</summary>
public enum OutputLocation
{
    /// <summary>源目录。</summary>
    SourceDir,
    /// <summary>固定的指定路径。</summary>
    FixedPath,
    /// <summary>每次开始前询问。</summary>
    AskEveryTime,
}

/// <summary>成品怎么落地。</summary>
public enum SaveMode
{
    /// <summary>在目标路径下新建 <c>&lt;源文件夹名&gt;_shrink_output</c>，原文件不动。</summary>
    NewFolder,
    /// <summary>直接写入目标路径；同名文件<b>直接覆盖</b>（目标 = 源目录时即就地替换）。</summary>
    DirectOverwrite,
}

public enum SizeTargetMode
{
    /// <summary>压到原体积的百分比。</summary>
    Ratio,
    /// <summary>压到指定的绝对体积（每个文件）。</summary>
    AbsoluteBytes,
}

public enum ResizeMode
{
    /// <summary>等比缩放，只设目标高度，宽度自动取偶数。</summary>
    Scale,
    /// <summary>强行拉伸到目标宽×高（高级）。</summary>
    Stretch,
}

/// <summary>持久化配置（exe 旁 settings.json）。任何一项被修改都应立即 <see cref="Save"/>。</summary>
public class AppConfig
{
    /// <summary>输出文件夹名的后缀（`NewFolder` 模式）。</summary>
    public const string OutputFolderSuffix = "_shrink_output";

    /// <summary>用户手动指定的源文件夹。</summary>
    [JsonPropertyName("target_dir")]
    public string TargetDir { get; set; } = "";

    // ── 输出目标：3 路径模式 × 2 保存模式 ──────────────────────────────
    [JsonPropertyName("output_location")]
    public OutputLocation OutputLocation { get; set; } = OutputLocation.SourceDir;

    [JsonPropertyName("save_mode")]
    public SaveMode SaveMode { get; set; } = SaveMode.NewFolder;

    /// <summary>`OutputLocation = FixedPath` 时使用。</summary>
    [JsonPropertyName("fixed_output_path")]
    public string FixedOutputPath { get; set; } = "";

    // ── 体积目标 ──────────────────────────────────────────────────────
    [JsonPropertyName("size_target_mode")]
    public SizeTargetMode SizeTargetMode { get; set; } = SizeTargetMode.Ratio;

    /// <summary>目标体积比例（默认 0.15 = 压到原体积 15%）。</summary>
    [JsonPropertyName("target_ratio")]
    public double TargetRatio { get; set; } = 0.15;

    /// <summary>绝对体积模式下，<b>每个文件</b>的目标字节数（默认 8MB）。</summary>
    [JsonPropertyName("target_bytes")]
    public long TargetBytes { get; set; } = 8L * 1024 * 1024;

    // ── 画面 ──────────────────────────────────────────────────────────
    [JsonPropertyName("resize_mode")]
    public ResizeMode ResizeMode { get; set; } = ResizeMode.Scale;

    /// <summary>目标高度。</summary>
    [JsonPropertyName("target_height")]
    public int TargetHeight { get; set; } = 1080;

    /// <summary>目标宽度（<b>仅 Stretch 模式生效</b>，高级项）。</summary>
    [JsonPropertyName("target_width")]
    public int TargetWidth { get; set; } = 1920;

    // ── 筛选 ──────────────────────────────────────────────────────────
    /// <summary>体积起步线：只处理大于此值的文件。默认 256MB —— 更小的文件压了也省不下多少。</summary>
    [JsonPropertyName("min_size_bytes")]
    public long MinSizeBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>源分辨率高度下限（0 = 不筛）。</summary>
    [JsonPropertyName("min_source_height")]
    public int MinSourceHeight { get; set; } = 0;

    /// <summary>启用源分辨率<b>宽度</b>下限（高级项）。启用后宽高都需达标。</summary>
    [JsonPropertyName("enable_min_source_width")]
    public bool EnableMinSourceWidth { get; set; } = false;

    [JsonPropertyName("min_source_width")]
    public int MinSourceWidth { get; set; } = 0;

    // ── 编码器 ────────────────────────────────────────────────────────
    [JsonPropertyName("encoder")]
    public EncoderKind Encoder { get; set; } = EncoderKind.X265;

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

    /// <summary>libx264 preset（<b>仅在 Encoder = H264 且实现为 Cpu 时生效</b>）。</summary>
    [JsonPropertyName("preset_x264")]
    public string PresetX264 { get; set; } = "slow";

    /// <summary>H.264 的实现选择（高级项）。</summary>
    [JsonPropertyName("h264_impl")]
    public H264Impl H264Impl { get; set; } = H264Impl.Auto;

    // ── GPU 探测缓存（首次运行写入；GPU 变化或运行中出错则重探） ─────────
    /// <summary>Auto 探测出的 ffmpeg 编码器名，如 "h264_amf" / "libx264"。空 = 尚未探测。</summary>
    [JsonPropertyName("h264_resolved_encoder")]
    public string H264ResolvedEncoder { get; set; } = "";

    /// <summary>探测时的 GPU 列表指纹。与当前不符即重探。</summary>
    [JsonPropertyName("gpu_signature")]
    public string GpuSignature { get; set; } = "";

    /// <summary>GPU 解码卸载（`-hwaccel`）。只加速解码、不碰编码，压缩效率零损失。</summary>
    [JsonPropertyName("gpu_decode")]
    public bool GpuDecode { get; set; } = false;

    // ── 流程 ──────────────────────────────────────────────────────────
    [JsonPropertyName("recursive")]
    public bool Recursive { get; set; } = false;

    [JsonPropertyName("retry_failed")]
    public bool RetryFailed { get; set; } = false;

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

    /// <summary>
    /// 用户在预检警告里选择「强行继续」时，允许压缩音频来腾出视频码率预算。
    /// 每次运行由预检结果设置，不持久化。
    /// </summary>
    [JsonIgnore]
    public bool AllowAudioCompressionForBudget { get; set; } = false;

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

    /// <summary>比例模式下的体积上界。实际上界取 <c>max(此值, TargetRatio × 1.35)</c>，避免用户调高目标比例后全批判失败。</summary>
    [JsonPropertyName("max_out_ratio")]
    public double MaxOutRatio { get; set; } = 0.25;

    /// <summary>绝对体积模式下允许的超出比例（容器开销的余量）。</summary>
    [JsonPropertyName("absolute_over_tolerance")]
    public double AbsoluteOverTolerance { get; set; } = 0.02;

    // ── 界面分层 ──────────────────────────────────────────────────────
    /// <summary>
    /// 用户对「启用高级调整」的真实偏好。
    /// <see cref="CustomMode"/> 会把生效值强制为 true，但不能污染这个偏好值。
    /// </summary>
    [JsonPropertyName("show_advanced")]
    public bool ShowAdvancedUserPref { get; set; } = false;

    /// <summary>
    /// 自定义模式：逐项勾选隐藏，打造自己的面板。
    /// 开启时强制展开全部高级项，并把「启用高级调整」开关本身藏起来
    /// （两者的隐藏思路冲突，不允许叠加）。
    /// </summary>
    [JsonPropertyName("custom_mode")]
    public bool CustomMode { get; set; } = false;

    /// <summary>自定义模式下被用户勾选为「隐藏」的设置项 key。</summary>
    [JsonPropertyName("hidden_settings")]
    public List<string> HiddenSettings { get; set; } = new();

    /// <summary>实际生效的「高级项可见/可用」值。自定义模式下恒为 true。</summary>
    [JsonIgnore]
    public bool ShowAdvancedEffective => CustomMode || ShowAdvancedUserPref;

    // ── 持久化 ────────────────────────────────────────────────────────
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

    /// <summary>
    /// 恢复默认设置：除「当前源文件夹」外全部归位（含隐藏项配置与两个界面开关）。
    /// GPU 探测缓存也一并清空，下次运行重新探测。
    /// </summary>
    public void ResetToDefaults()
    {
        string keepDir = TargetDir;
        var d = new AppConfig();
        foreach (var p in typeof(AppConfig).GetProperties())
        {
            if (!p.CanRead || !p.CanWrite) continue;
            p.SetValue(this, p.GetValue(d));
        }
        TargetDir = keepDir;
        Save();
        Log.Information("已恢复默认设置");
    }
}
