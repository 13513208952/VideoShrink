using System;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using VideoShrink.Core;

namespace VideoShrink.Views;

public partial class PreflightDialog : Window
{
    public bool Confirmed { get; private set; }

    public PreflightDialog()
    {
        InitializeComponent();
    }

    public PreflightDialog(PreflightReport report)
    {
        InitializeComponent();

        int files = report.Risks.Count;
        HeadlineText.Text = report.Destructive
            ? $"有 {files} 个文件的内容将不可恢复地丢失"
            : $"有 {files} 个文件的成品会缺少部分内容";

        var danger = new StringBuilder();
        if (report.Destructive)
        {
            danger.AppendLine("当前为「直接文件保存」模式：原文件将被永久替换，被丢弃的字幕、音轨、封面等无法找回。");
            danger.AppendLine("目标路径下的同名文件也会被直接覆盖，且不会对其做任何校验。");
        }
        if (report.AudioWillBeForciblyCompressed)
            danger.AppendLine("有文件的音频占满了体积预算，继续将被迫压缩音频（有损）。");
        if (report.AnyCritical)
            danger.AppendLine("有文件的目标视频码率严重过低，画面必然崩坏。");
        if (report.AnyFatal)
            danger.AppendLine("有文件在当前设置下必定失败（详见下方明细）。");

        if (danger.Length > 0)
        {
            DangerText.Text = danger.ToString().TrimEnd();
            DangerBox.IsVisible = true;
        }

        DetailText.Text = report.BuildSummary();

        ContinueButton.Content = report.Destructive ? "我已了解，覆盖并继续" : "我已了解，继续";
    }

    private void Continue_Click(object? sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Confirmed = false;
        Close();
    }
}
