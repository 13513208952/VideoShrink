using System;
using System.Windows;
using MaterialDesignColors;
using MaterialDesignThemes.Wpf;
using VideoShrink.Core;
using Serilog;
using WpfColor = System.Windows.Media.Color;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace VideoShrink;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths.EnsureDirs();
        AppLog.Init();

        // 需求：启动时自动清理缓存
        AppPaths.CleanCache();
        Log.Information("启动：缓存已清理，AppRoot={Root}", AppPaths.AppRoot);

        // ── Material Design（Google 公开设计体系）主题框架 ─────────────
        Resources.MergedDictionaries.Add(new BundledTheme
        {
            BaseTheme      = BaseTheme.Light,
            PrimaryColor   = (PrimaryColor)MaterialDesignColor.Indigo,
            SecondaryColor = (SecondaryColor)MaterialDesignColor.Teal,
        });
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/MaterialDesignThemes.Wpf;component/Themes/MaterialDesign3.Defaults.xaml")
        });

        var palette = new PaletteHelper();
        var theme = palette.GetTheme();
        theme.SetPrimaryColor(WpfColor.FromRgb(0x3F, 0x51, 0xB5));   // Indigo 500
        theme.SetSecondaryColor(WpfColor.FromRgb(0x00, 0x89, 0x7B)); // Teal 600
        theme.SetBaseTheme(BaseTheme.Light);
        palette.SetTheme(theme);

        // MaterialDesignFont 默认 Roboto，无中文字形会 fallback 到宋体（衬线）
        Resources["MaterialDesignFont"] = new System.Windows.Media.FontFamily(
            "Microsoft YaHei UI, Microsoft YaHei, Segoe UI");

        if (!AppPaths.ToolsPresent)
        {
            MessageBox.Show(
                $"未找到 ffmpeg/ffprobe：\n{AppPaths.FfmpegPath}\n{AppPaths.FfprobePath}\n\n" +
                "请确认 tools 目录随程序一起复制。",
                "VideoShrink", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        new Views.MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
