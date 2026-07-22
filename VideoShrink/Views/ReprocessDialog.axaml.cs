using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using VideoShrink.Core;

namespace VideoShrink.Views;

public partial class ReprocessDialog : Window
{
    public ReprocessDecision Decision { get; private set; } = ReprocessDecision.Abort;

    public ReprocessDialog()
    {
        InitializeComponent();
    }

    public ReprocessDialog(ReprocessReport report)
    {
        InitializeComponent();
        HeadlineText.Text =
            $"待处理的 {report.TotalCandidates} 个文件里，有 {report.Hits.Count} 个是此前的成品";
        DetailText.Text = report.BuildSummary();
    }

    private void IncludeAll_Click(object? sender, RoutedEventArgs e) => Finish(ReprocessDecision.IncludeAll);
    private void Exclude_Click(object? sender, RoutedEventArgs e)    => Finish(ReprocessDecision.ExcludeProcessed);
    private void Abort_Click(object? sender, RoutedEventArgs e)      => Finish(ReprocessDecision.Abort);

    private void Finish(ReprocessDecision d)
    {
        Decision = d;
        Close();
    }
}
