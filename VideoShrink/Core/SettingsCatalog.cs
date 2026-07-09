using System.Collections.Generic;
using System.Linq;

namespace VideoShrink.Core;

/// <summary>一个设置项的身份。<see cref="Key"/> 是持久化用的稳定标识，绝不可改。</summary>
public sealed record SettingItem(string Key, string DisplayName, bool IsAdvanced, string Group);

/// <summary>
/// 全部设置项的目录。两套界面分层都以此为准：
///
/// - <b>高级功能关闭</b>（默认）：<c>IsAdvanced</c> 的项<b>隐藏且禁用</b>——为普通用户预设的常规面板。
/// - <b>高级功能开启</b>：全部展开，可自由调整。
/// - <b>自定义模式</b>：强制展开全部高级项，并把「启用高级调整」开关本身藏起来；
///   用户改用逐项勾选来隐藏自己不常动的选项。两套隐藏思路互斥，不允许叠加。
///
/// 被隐藏的项<b>按当前值继续生效</b>（不重置、不失效）。
/// </summary>
public static class SettingsCatalog
{
    public const string GroupOutput   = "输出";
    public const string GroupSize     = "体积目标";
    public const string GroupPicture  = "画面";
    public const string GroupFilter   = "筛选";
    public const string GroupEncoder  = "编码";
    public const string GroupAudio    = "音频与字幕";
    public const string GroupVerify   = "验收";
    public const string GroupProcess  = "流程";

    public static readonly IReadOnlyList<SettingItem> All = new[]
    {
        new SettingItem("output_location",      "输出位置",             false, GroupOutput),
        new SettingItem("save_mode",            "保存方式",             false, GroupOutput),
        new SettingItem("fixed_output_path",    "固定输出路径",         false, GroupOutput),

        new SettingItem("size_target_mode",     "体积目标模式",         false, GroupSize),
        new SettingItem("target_ratio",         "目标体积比例",         false, GroupSize),
        new SettingItem("target_bytes",         "目标体积（MB）",       false, GroupSize),

        new SettingItem("target_height",        "目标高度",             false, GroupPicture),
        new SettingItem("resize_mode",          "缩放方式（等比/拉伸）", true,  GroupPicture),
        new SettingItem("target_width",         "目标宽度（仅拉伸）",   true,  GroupPicture),

        new SettingItem("min_size_bytes",       "体积起步线（MB）",     false, GroupFilter),
        new SettingItem("min_source_height",    "源高度下限",           false, GroupFilter),
        new SettingItem("enable_min_source_width", "启用源宽度下限",    true,  GroupFilter),
        new SettingItem("min_source_width",     "源宽度下限",           true,  GroupFilter),
        new SettingItem("recursive",            "递归扫描子目录",       false, GroupFilter),

        new SettingItem("encoder",              "编码器",               false, GroupEncoder),
        new SettingItem("preset_av1",           "SVT-AV1 preset",       false, GroupEncoder),
        new SettingItem("preset_x265",          "x265 preset",          false, GroupEncoder),
        new SettingItem("preset_x264",          "libx264 preset",       false, GroupEncoder),
        new SettingItem("h264_impl",            "H.264 实现",           true,  GroupEncoder),
        new SettingItem("gpu_decode",           "GPU 解码卸载",         true,  GroupEncoder),

        new SettingItem("audio_policy",         "音频策略",             false, GroupAudio),
        new SettingItem("keep_subtitles",       "保留字幕",             false, GroupAudio),
        new SettingItem("audio_reencode_bitrate", "音频重编码码率",     true,  GroupAudio),

        new SettingItem("full_decode_check",    "整片解码校验",         true,  GroupVerify),
        new SettingItem("max_out_ratio",        "体积上界",             true,  GroupVerify),
        new SettingItem("min_out_ratio",        "体积下界",             true,  GroupVerify),

        new SettingItem("parallelism",          "并行任务数",           true,  GroupProcess),
        new SettingItem("retry_failed",         "重试此前失败的文件",   true,  GroupProcess),
    };

    public static IEnumerable<string> AdvancedKeys => All.Where(i => i.IsAdvanced).Select(i => i.Key);

    public static bool IsAdvanced(string key) =>
        All.FirstOrDefault(i => i.Key == key)?.IsAdvanced ?? false;

    /// <summary>
    /// 某个设置项当前是否应显示。
    /// 自定义模式下只看逐项隐藏；否则只看「高级功能是否开启」。
    /// </summary>
    public static bool IsVisible(AppConfig cfg, string key) =>
        cfg.CustomMode
            ? !cfg.HiddenSettings.Contains(key)
            : (!IsAdvanced(key) || cfg.ShowAdvancedUserPref);

    /// <summary>高级项在「高级功能关闭」时既不可见也<b>不可用</b>。</summary>
    public static bool IsEnabled(AppConfig cfg, string key) =>
        !IsAdvanced(key) || cfg.ShowAdvancedEffective;
}
