using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VideoShrink.Core;
using VideoShrink.ViewModels;

namespace VideoShrink.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var vm = new MainViewModel();

        vm.BrowseForFolder = () => PickFolderNative("选择要压缩的源文件夹");

        vm.AskOutputDir = () => PickFolderNative("选择输出目录");

        vm.ConfirmRisks = report => RunSync(async () =>
        {
            var dlg = new PreflightDialog(report);
            await dlg.ShowDialog(this);
            return dlg.Confirmed;
        });

        vm.ConfirmReprocess = report => RunSync(async () =>
        {
            var dlg = new ReprocessDialog(report);
            await dlg.ShowDialog(this);
            return dlg.Decision;
        });

        vm.EditVisibility = hidden => RunSync(async () =>
        {
            var dlg = new VisibilityEditorDialog(hidden);
            await dlg.ShowDialog(this);
            return dlg.Result!;
        });

        vm.ShowError = msg =>
        {
            ShowNiceError(msg);
        };

        vm.Confirm = msg => ConfirmNice(msg);

        DataContext = vm;
    }

    private static T RunSync<T>(Func<Task<T>> asyncFunc)
    {
        return Task.Run(async () =>
            await Dispatcher.UIThread.InvokeAsync(asyncFunc)
        ).GetAwaiter().GetResult();
    }

    // ===== 原生 Linux 对话框 =====

    private static string? PickFolderNative(string title)
    {
        string dir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return KDialogGetFolder(title, dir) ?? ZenityGetFolder(title, dir);
    }

    private static void ShowNiceError(string message)
    {
        if (!KDialogShow("--error", message)) ZenityShow("--error", message);
    }

    private static bool ConfirmNice(string message)
    {
        return KDialogAsk(message) ?? ZenityAsk(message) ?? false;
    }

    // -- kdialog (KDE) --

    private static string? KDialogGetFolder(string title, string startDir)
    {
        try
        {
            var psi = new ProcessStartInfo("kdialog")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--getexistingdirectory");
            psi.ArgumentList.Add(startDir);
            psi.ArgumentList.Add("--title");
            psi.ArgumentList.Add(title);

            using var p = Process.Start(psi);
            if (p is null) return null;
            string output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return p.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch { return null; }
    }

    private static bool KDialogShow(string type, string message)
    {
        try
        {
            var psi = new ProcessStartInfo("kdialog")
            {
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(type);
            psi.ArgumentList.Add(message);
            psi.ArgumentList.Add("--title");
            psi.ArgumentList.Add("VideoShrink");
            Process.Start(psi);
            return true;
        }
        catch { return false; }
    }

    private static bool? KDialogAsk(string message)
    {
        try
        {
            var psi = new ProcessStartInfo("kdialog")
            {
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--yesno");
            psi.ArgumentList.Add(message);
            psi.ArgumentList.Add("--title");
            psi.ArgumentList.Add("VideoShrink");

            using var p = Process.Start(psi);
            p?.WaitForExit();
            return p?.ExitCode == 0;
        }
        catch { return null; }
    }

    // -- zenity (GNOME/GTK) --

    private static string? ZenityGetFolder(string title, string startDir)
    {
        try
        {
            var psi = new ProcessStartInfo("zenity")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--file-selection");
            psi.ArgumentList.Add("--directory");
            psi.ArgumentList.Add("--title");
            psi.ArgumentList.Add(title);
            psi.ArgumentList.Add("--filename");
            psi.ArgumentList.Add(startDir);

            using var p = Process.Start(psi);
            if (p is null) return null;
            string output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return p.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch { return null; }
    }

    private static bool ZenityShow(string type, string message)
    {
        try
        {
            var psi = new ProcessStartInfo("zenity")
            {
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(type);
            psi.ArgumentList.Add("--text");
            psi.ArgumentList.Add(message);
            psi.ArgumentList.Add("--title");
            psi.ArgumentList.Add("VideoShrink");
            Process.Start(psi);
            return true;
        }
        catch { return false; }
    }

    private static bool? ZenityAsk(string message)
    {
        try
        {
            var psi = new ProcessStartInfo("zenity")
            {
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--question");
            psi.ArgumentList.Add("--text");
            psi.ArgumentList.Add(message);
            psi.ArgumentList.Add("--title");
            psi.ArgumentList.Add("VideoShrink");

            using var p = Process.Start(psi);
            p?.WaitForExit();
            return p?.ExitCode == 0;
        }
        catch { return null; }
    }
}
