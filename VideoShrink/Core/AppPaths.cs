using System;
using System.IO;

namespace VideoShrink.Core;

/// <summary>
/// 路径集中管理。exe 目录为根：cache/ 与 records/ 都放在 exe 旁。
/// 缓存刻意放在程序盘而非视频盘——视频盘已满，不能再占。
/// </summary>
public static class AppPaths
{
    /// <summary>可执行文件所在目录（single-file 发布时也正确）</summary>
    public static string AppRoot { get; } =
        Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory)!;

    public static string CacheDir   { get; } = Path.Combine(AppRoot, "cache");
    public static string RecordsDir { get; } = Path.Combine(AppRoot, "records");
    public static string ToolsDir   { get; } = Path.Combine(AppRoot, "tools");

    public static string DbPath       { get; } = Path.Combine(RecordsDir, "transcode.db");
    public static string LogPath      { get; } = Path.Combine(RecordsDir, "transcode.log");
    public static string SettingsPath { get; } = Path.Combine(AppRoot, "settings.json");

    public static string FfmpegPath  { get; } = Path.Combine(ToolsDir, "ffmpeg.exe");
    public static string FfprobePath { get; } = Path.Combine(ToolsDir, "ffprobe.exe");

    public static void EnsureDirs()
    {
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(RecordsDir);
    }

    /// <summary>启动时清空缓存目录（需求：自动在启动时清理缓存）。</summary>
    public static void CleanCache()
    {
        try
        {
            if (!Directory.Exists(CacheDir)) return;
            foreach (var f in Directory.GetFiles(CacheDir))
                try { File.Delete(f); } catch { /* 单个失败不阻塞启动 */ }
            foreach (var d in Directory.GetDirectories(CacheDir))
                try { Directory.Delete(d, recursive: true); } catch { }
        }
        catch { /* 启动清理失败不崩溃 */ }
    }

    /// <summary>ffmpeg / ffprobe 是否就位。</summary>
    public static bool ToolsPresent => File.Exists(FfmpegPath) && File.Exists(FfprobePath);
}
