using System;
using System.IO;

namespace VideoShrink.Core;

public static class AppPaths
{
    public static string AppRoot { get; } =
        Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory)!;

    public static string CacheDir   { get; } = Path.Combine(AppRoot, "cache");
    public static string RecordsDir { get; } = Path.Combine(AppRoot, "records");
    public static string ToolsDir   { get; } = Path.Combine(AppRoot, "tools");

    public static string DbPath       { get; } = Path.Combine(RecordsDir, "transcode.db");
    public static string LogPath      { get; } = Path.Combine(RecordsDir, "transcode.log");
    public static string SettingsPath { get; } = Path.Combine(AppRoot, "settings.json");

    public static string FfmpegPath  { get; } = ResolveTool("ffmpeg");
    public static string FfprobePath { get; } = ResolveTool("ffprobe");

    private static string ResolveTool(string name)
    {
        string exe = Path.Combine(ToolsDir, name + ".exe");
        if (File.Exists(exe)) return exe;
        string bundled = Path.Combine(ToolsDir, name);
        if (File.Exists(bundled)) return bundled;

        // Source/development builds may use FFmpeg from PATH. Published builds
        // bundle a pinned BtbN build in tools/ for reproducible performance.
        return name;
    }

    public static void EnsureDirs()
    {
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(RecordsDir);
    }

    public static void CleanCache()
    {
        try
        {
            if (!Directory.Exists(CacheDir)) return;
            foreach (var f in Directory.GetFiles(CacheDir))
                try { File.Delete(f); } catch { }
            foreach (var d in Directory.GetDirectories(CacheDir))
                try { Directory.Delete(d, recursive: true); } catch { }
        }
        catch { }
    }

    public static bool ToolsPresent => ToolExists(FfmpegPath) && ToolExists(FfprobePath);

    private static bool ToolExists(string tool)
    {
        if (File.Exists(tool)) return true;
        if (Path.IsPathRooted(tool)) return false;

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return false;
        foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (File.Exists(Path.Combine(dir, tool))) return true;
        return false;
    }
}
