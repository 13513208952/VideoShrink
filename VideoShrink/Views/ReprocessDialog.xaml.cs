using System.Windows;
using VideoShrink.Core;

namespace VideoShrink.Views;

/// <summary>
/// 候选文件里混进了本程序此前产出的成品。三选一，默认（也是回车/ESC）为「放弃」。
/// 二次压缩不可逆，所以危险选项不做默认按钮。
/// </summary>
public partial class ReprocessDialog : Window
{
    public ReprocessDecision Decision { get; private set; } = ReprocessDecision.Abort;

    public ReprocessDialog(ReprocessReport report)
    {
        InitializeComponent();
        HeadlineText.Text =
            $"待处理的 {report.TotalCandidates} 个文件里，有 {report.Hits.Count} 个是此前的成品";
        DetailText.Text = report.BuildSummary();
    }

    private void IncludeAll_Click(object sender, RoutedEventArgs e) => Finish(ReprocessDecision.IncludeAll);
    private void Exclude_Click(object sender, RoutedEventArgs e)    => Finish(ReprocessDecision.ExcludeProcessed);
    private void Abort_Click(object sender, RoutedEventArgs e)      => Finish(ReprocessDecision.Abort);

    private void Finish(ReprocessDecision d)
    {
        Decision = d;
        DialogResult = d != ReprocessDecision.Abort;
        Close();
    }
}
