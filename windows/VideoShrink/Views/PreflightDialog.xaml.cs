using System.Windows;
using VideoShrink.Core;

namespace VideoShrink.Views;

/// <summary>
/// 扫描完成后、任何编码开始前的风险确认。
/// 「直接文件保存」模式下损失不可恢复，用强警告措辞；「新建文件夹保存」下原文件保留，只作提示。
/// 用户点「继续」即表示接受「能保住多少保住多少」。
/// </summary>
public partial class PreflightDialog : Window
{
    public bool Confirmed { get; private set; }

    public PreflightDialog(PreflightReport report)
    {
        InitializeComponent();

        int files = report.Risks.Count;
        HeadlineText.Text = report.Destructive
            ? $"有 {files} 个文件的内容将不可恢复地丢失"
            : $"有 {files} 个文件的成品会缺少部分内容";

        var danger = new System.Text.StringBuilder();
        if (report.Destructive)
        {
            danger.AppendLine("当前为「直接文件保存」模式：**原文件将被永久替换**，被丢弃的字幕、音轨、封面等无法找回。");
            danger.AppendLine("目标路径下的同名文件也会被直接覆盖，且不会对其做任何校验。");
        }
        if (report.AudioWillBeForciblyCompressed)
            danger.AppendLine("有文件的音频占满了体积预算，继续将**被迫压缩音频**（有损）。");
        if (report.AnyCritical)
            danger.AppendLine("有文件的目标视频码率**严重过低**，画面必然崩坏。");
        if (report.AnyFatal)
            danger.AppendLine("有文件在当前设置下**必定失败**（详见下方明细）。");

        if (danger.Length > 0)
        {
            DangerText.Text = danger.ToString().Replace("**", "").TrimEnd();
            DangerBox.Visibility = Visibility.Visible;
        }

        DetailText.Text = report.BuildSummary();

        ContinueButton.Content = report.Destructive ? "我已了解，覆盖并继续" : "我已了解，继续";
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        DialogResult = false;
        Close();
    }
}
