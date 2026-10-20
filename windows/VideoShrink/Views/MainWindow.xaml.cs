using System.Collections.Generic;
using System.Windows;
using VideoShrink.Core;
using VideoShrink.ViewModels;
using MessageBox = System.Windows.MessageBox;

namespace VideoShrink.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var vm = new MainViewModel();

        // 弹窗由 View 注入 —— ViewModel 不反向依赖 Views，也便于自测时替换
        vm.ConfirmRisks = report => Dispatcher.Invoke(() =>
        {
            var dlg = new PreflightDialog(report) { Owner = this };
            dlg.ShowDialog();
            return dlg.Confirmed;
        });

        vm.ConfirmReprocess = report => Dispatcher.Invoke(() =>
        {
            var dlg = new ReprocessDialog(report) { Owner = this };
            dlg.ShowDialog();
            return dlg.Decision;
        });

        vm.EditVisibility = hidden => Dispatcher.Invoke<IReadOnlyList<string>?>(() =>
        {
            var dlg = new VisibilityEditorDialog(hidden) { Owner = this };
            dlg.ShowDialog();
            return dlg.Result;
        });

        vm.ShowError = msg => Dispatcher.Invoke(() =>
            MessageBox.Show(this, msg, "VideoShrink", MessageBoxButton.OK, MessageBoxImage.Error));

        vm.Confirm = msg => Dispatcher.Invoke(() =>
            MessageBox.Show(this, msg, "VideoShrink", MessageBoxButton.YesNo, MessageBoxImage.Question)
                == MessageBoxResult.Yes);

        DataContext = vm;
    }
}
