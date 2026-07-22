using System;
using Avalonia;
using Serilog;
using VideoShrink.Core;

namespace VideoShrink;

public partial class App : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        AppPaths.EnsureDirs();
        AppLog.Init();
        AppPaths.CleanCache();
        Log.Information("启动：缓存已清理，AppRoot={Root}", AppPaths.AppRoot);

        if (!AppPaths.ToolsPresent)
        {
            Console.Error.WriteLine($"未找到 ffmpeg/ffprobe：\n{AppPaths.FfmpegPath}\n{AppPaths.FfprobePath}\n\n" +
                "请确认 tools 目录随程序一起复制。");
        }

        base.OnFrameworkInitializationCompleted();

        new Views.MainWindow().Show();
    }
}
