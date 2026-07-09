using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using VideoShrink.Core;
using VideoShrink.Data;
using Serilog;
// UseWindowsForms=true 会让 Application 在 WPF/WinForms 间产生歧义；
// System.Windows.ResizeMode（窗口边框行为）也和我们的缩放模式撞名
using Application = System.Windows.Application;
using ResizeMode  = VideoShrink.Core.ResizeMode;

namespace VideoShrink.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private readonly AppConfig       _cfg;
    private readonly RecordDb        _db;
    private readonly TranscodeEngine _engine;
    private CancellationTokenSource? _cts;

    public ObservableCollection<string>    Logs    { get; } = new();
    public ObservableCollection<RecordRow> Records { get; } = new();

    // ── 界面分层：XAML 直接按 key 绑定 {Binding Vis[target_width]} ─────
    public Dictionary<string, Visibility> Vis     { get; private set; } = new();
    public Dictionary<string, bool>       Enabled { get; private set; } = new();

    // ── 弹窗由 View 注入，ViewModel 不反向依赖 Views ──────────────────
    /// <summary>询问输出目录（AskEveryTime 模式）。返回 null 表示用户取消。</summary>
    public Func<string?>? AskOutputDir { get; set; }
    /// <summary>预检风险弹窗。返回 true = 用户确认继续。</summary>
    public Func<PreflightReport, bool>? ConfirmRisks { get; set; }
    /// <summary>发现已处理成品时的三选一。默认放弃（二次压缩不可逆）。</summary>
    public Func<ReprocessReport, ReprocessDecision>? ConfirmReprocess { get; set; }
    /// <summary>自定义模式的逐项隐藏编辑界面。返回 null 表示取消。</summary>
    public Func<IReadOnlyList<string>, IReadOnlyList<string>?>? EditVisibility { get; set; }
    /// <summary>致命错误提示。</summary>
    public Action<string>? ShowError { get; set; }
    /// <summary>二次确认。</summary>
    public Func<string, bool>? Confirm { get; set; }

    // ── 选项列表 ──────────────────────────────────────────────────────
    public IReadOnlyList<PresetOption<string>> X265Presets { get; } = EncoderPresets.X265;
    public IReadOnlyList<PresetOption<string>> X264Presets { get; } = EncoderPresets.X264;
    public IReadOnlyList<PresetOption<int>>    Av1Presets  { get; } = EncoderPresets.Av1;

    public IReadOnlyList<EncoderKind>    Encoders        { get; } = new[] { EncoderKind.H264, EncoderKind.X265, EncoderKind.SvtAv1 };
    public IReadOnlyList<H264Impl>       H264Impls       { get; } = Enum.GetValues<H264Impl>();
    public IReadOnlyList<AudioPolicy>    AudioPolicies   { get; } = new[] { AudioPolicy.Auto, AudioPolicy.CopyOnly };
    public IReadOnlyList<OutputLocation> OutputLocations { get; } = Enum.GetValues<OutputLocation>();
    public IReadOnlyList<SaveMode>       SaveModes       { get; } = Enum.GetValues<SaveMode>();
    public IReadOnlyList<SizeTargetMode> SizeTargetModes { get; } = Enum.GetValues<SizeTargetMode>();
    public IReadOnlyList<ResizeMode>     ResizeModes     { get; } = Enum.GetValues<ResizeMode>();

    public MainViewModel()
    {
        _cfg    = AppConfig.Load();
        _db     = new RecordDb(AppPaths.DbPath);
        _engine = new TranscodeEngine(_cfg, _db);

        _engine.LogLine      += OnEngineLog;
        _engine.StateChanged += OnEngineStateChanged;

        _targetDir = _cfg.TargetDir;
        RebuildVisibility();

        BrowseCommand         = new RelayCommand(Browse, () => !IsRunning);
        BrowseFixedCommand    = new RelayCommand(BrowseFixed, () => !IsRunning);
        ScanCommand           = new RelayCommand(async () => await ScanAsync(), () => !IsRunning && HasDir);
        StartCommand          = new RelayCommand(async () => await StartAsync(), () => !IsRunning && HasDir);
        PauseCommand          = new RelayCommand(() => _engine.Pause(),  () => IsRunning && !IsPaused);
        ResumeCommand         = new RelayCommand(() => _engine.Resume(), () => IsRunning && IsPaused);
        StopCommand           = new RelayCommand(() => _engine.Stop(),   () => IsRunning);
        ExportCsvCommand      = new RelayCommand(ExportCsv);
        RefreshRecordsCommand = new RelayCommand(RefreshRecords);
        OpenRecordsCommand    = new RelayCommand(() => OpenFolder(AppPaths.RecordsDir));
        EditVisibilityCommand = new RelayCommand(DoEditVisibility, () => CustomMode);
        ResetDefaultsCommand  = new RelayCommand(DoResetDefaults, () => !IsRunning);

        RefreshHistory();
        RefreshRecords();
    }

    // ── 命令 ──────────────────────────────────────────────────────────
    public RelayCommand BrowseCommand         { get; }
    public RelayCommand BrowseFixedCommand    { get; }
    public RelayCommand ScanCommand           { get; }
    public RelayCommand StartCommand          { get; }
    public RelayCommand PauseCommand          { get; }
    public RelayCommand ResumeCommand         { get; }
    public RelayCommand StopCommand           { get; }
    public RelayCommand ExportCsvCommand      { get; }
    public RelayCommand RefreshRecordsCommand { get; }
    public RelayCommand OpenRecordsCommand    { get; }
    public RelayCommand EditVisibilityCommand { get; }
    public RelayCommand ResetDefaultsCommand  { get; }

    // ── 源文件夹 ──────────────────────────────────────────────────────
    private string _targetDir = "";
    public string TargetDir
    {
        get => _targetDir;
        set
        {
            if (SetField(ref _targetDir, value))
            {
                _cfg.TargetDir = value;
                _cfg.Save();
                OnPropertyChanged(nameof(HasDir));
            }
        }
    }
    public bool HasDir => !string.IsNullOrWhiteSpace(TargetDir) && Directory.Exists(TargetDir);

    // ── 进度状态 ──────────────────────────────────────────────────────
    private int _queueTotal; public int QueueTotal { get => _queueTotal; private set => SetField(ref _queueTotal, value); }
    private int _completed;  public int Completed  { get => _completed;  private set => SetField(ref _completed, value); }
    private int _failed;     public int Failed     { get => _failed;     private set => SetField(ref _failed, value); }
    private int _skipped;    public int Skipped    { get => _skipped;    private set => SetField(ref _skipped, value); }
    private int _remaining;  public int Remaining  { get => _remaining;  private set => SetField(ref _remaining, value); }

    private string _currentFile = "";
    public string CurrentFile { get => _currentFile; private set => SetField(ref _currentFile, value); }

    private double _currentPercent;
    public double CurrentPercent { get => _currentPercent; private set => SetField(ref _currentPercent, value); }

    private double _overallPercent;
    public double OverallPercent { get => _overallPercent; private set => SetField(ref _overallPercent, value); }

    private string _savedText = "0 B";
    public string SavedText { get => _savedText; private set => SetField(ref _savedText, value); }

    private string _etaText = "—";
    public string EtaText { get => _etaText; private set => SetField(ref _etaText, value); }

    private bool _isRunning;
    public bool IsRunning { get => _isRunning; private set { if (SetField(ref _isRunning, value)) OnPropertyChanged(nameof(IsIdle)); } }
    private bool _isPaused; public bool IsPaused { get => _isPaused; private set => SetField(ref _isPaused, value); }
    public bool IsIdle => !IsRunning;

    private string _status = "就绪";
    public string Status { get => _status; private set => SetField(ref _status, value); }

    private int _historySuccess; public int HistorySuccess { get => _historySuccess; private set => SetField(ref _historySuccess, value); }
    private int _historyFailed;  public int HistoryFailed  { get => _historyFailed;  private set => SetField(ref _historyFailed, value); }
    private string _historySaved = "0 B";
    public string HistorySaved { get => _historySaved; private set => SetField(ref _historySaved, value); }

    // ── 设置：每一次修改都立即写入 settings.json ───────────────────────
    private void SaveAndNotify([System.Runtime.CompilerServices.CallerMemberName] string? n = null)
    {
        _cfg.Save();
        OnPropertyChanged(n);
    }

    public OutputLocation OutputLocation
    {
        get => _cfg.OutputLocation;
        set { _cfg.OutputLocation = value; SaveAndNotify(); }
    }
    public SaveMode SaveMode
    {
        get => _cfg.SaveMode;
        set
        {
            _cfg.SaveMode = value; SaveAndNotify();
            OnPropertyChanged(nameof(IsDestructive));
            OnPropertyChanged(nameof(DestructiveWarnVisible));
        }
    }
    public bool IsDestructive => _cfg.SaveMode == SaveMode.DirectOverwrite;

    /// <summary>就地覆盖是危险操作，主界面上要一直挂着红色横幅。</summary>
    public Visibility DestructiveWarnVisible => IsDestructive ? Visibility.Visible : Visibility.Collapsed;

    public string FixedOutputPath
    {
        get => _cfg.FixedOutputPath;
        set { _cfg.FixedOutputPath = value; SaveAndNotify(); }
    }

    public SizeTargetMode SizeTargetMode
    {
        get => _cfg.SizeTargetMode;
        set { _cfg.SizeTargetMode = value; SaveAndNotify(); }
    }
    public int TargetRatioPercent
    {
        get => (int)Math.Round(_cfg.TargetRatio * 100);
        set { _cfg.TargetRatio = Math.Clamp(value, 1, 99) / 100.0; SaveAndNotify(); }
    }
    public double TargetMegabytes
    {
        get => _cfg.TargetBytes / 1048576.0;
        set { _cfg.TargetBytes = (long)(Math.Max(0.1, value) * 1048576); SaveAndNotify(); }
    }

    public ResizeMode ResizeMode
    {
        get => _cfg.ResizeMode;
        set { _cfg.ResizeMode = value; SaveAndNotify(); }
    }
    public int TargetHeight
    {
        get => _cfg.TargetHeight;
        set { _cfg.TargetHeight = Math.Max(2, value); SaveAndNotify(); }
    }
    public int TargetWidth
    {
        get => _cfg.TargetWidth;
        set { _cfg.TargetWidth = Math.Max(2, value); SaveAndNotify(); }
    }

    public double MinSizeMegabytes
    {
        get => _cfg.MinSizeBytes / 1048576.0;
        set { _cfg.MinSizeBytes = (long)(Math.Max(0, value) * 1048576); SaveAndNotify(); }
    }
    public int MinSourceHeight
    {
        get => _cfg.MinSourceHeight;
        set { _cfg.MinSourceHeight = Math.Max(0, value); SaveAndNotify(); }
    }
    public bool EnableMinSourceWidth
    {
        get => _cfg.EnableMinSourceWidth;
        set { _cfg.EnableMinSourceWidth = value; SaveAndNotify(); }
    }
    public int MinSourceWidth
    {
        get => _cfg.MinSourceWidth;
        set { _cfg.MinSourceWidth = Math.Max(0, value); SaveAndNotify(); }
    }
    public bool Recursive
    {
        get => _cfg.Recursive;
        set { _cfg.Recursive = value; SaveAndNotify(); }
    }

    public EncoderKind Encoder
    {
        get => _cfg.Encoder;
        set
        {
            _cfg.Encoder = value; SaveAndNotify();
            OnPropertyChanged(nameof(IsAv1));
            OnPropertyChanged(nameof(IsX265));
            OnPropertyChanged(nameof(IsH264));
        }
    }
    public bool IsAv1  => _cfg.Encoder == EncoderKind.SvtAv1;
    public bool IsX265 => _cfg.Encoder == EncoderKind.X265;
    public bool IsH264 => _cfg.Encoder == EncoderKind.H264;

    public string Preset      { get => _cfg.Preset;     set { _cfg.Preset = value; SaveAndNotify(); } }
    public string PresetX264  { get => _cfg.PresetX264; set { _cfg.PresetX264 = value; SaveAndNotify(); } }
    public int    PresetAv1
    {
        get => _cfg.PresetAv1;
        set { _cfg.PresetAv1 = EncoderPresets.ClampAv1(value); SaveAndNotify(); }
    }
    public H264Impl H264Impl { get => _cfg.H264Impl; set { _cfg.H264Impl = value; SaveAndNotify(); } }
    public bool GpuDecode    { get => _cfg.GpuDecode; set { _cfg.GpuDecode = value; SaveAndNotify(); } }

    public AudioPolicy AudioPolicy { get => _cfg.AudioPolicy; set { _cfg.AudioPolicy = value; SaveAndNotify(); } }
    public int AudioReencodeBitrateK
    {
        get => _cfg.AudioReencodeBitrateK;
        set { _cfg.AudioReencodeBitrateK = Math.Clamp(value, 32, 640); SaveAndNotify(); }
    }
    public bool KeepSubtitles { get => _cfg.KeepSubtitles; set { _cfg.KeepSubtitles = value; SaveAndNotify(); } }

    public bool FullDecodeCheck { get => _cfg.FullDecodeCheck; set { _cfg.FullDecodeCheck = value; SaveAndNotify(); } }
    public int MaxOutRatioPercent
    {
        get => (int)Math.Round(_cfg.MaxOutRatio * 100);
        set { _cfg.MaxOutRatio = Math.Clamp(value, 1, 200) / 100.0; SaveAndNotify(); }
    }
    public int MinOutRatioPercent
    {
        get => (int)Math.Round(_cfg.MinOutRatio * 100);
        set { _cfg.MinOutRatio = Math.Clamp(value, 0, 99) / 100.0; SaveAndNotify(); }
    }
    public int Parallelism
    {
        get => _cfg.Parallelism;
        set { _cfg.Parallelism = Math.Clamp(value, 1, 4); SaveAndNotify(); }
    }
    public bool RetryFailed { get => _cfg.RetryFailed; set { _cfg.RetryFailed = value; SaveAndNotify(); } }

    // ── 界面分层 ──────────────────────────────────────────────────────
    /// <summary>「启用高级调整」。自定义模式下此开关本身被隐藏（两套隐藏思路互斥）。</summary>
    public bool ShowAdvanced
    {
        get => _cfg.ShowAdvancedUserPref;
        set { _cfg.ShowAdvancedUserPref = value; _cfg.Save(); OnPropertyChanged(); RebuildVisibility(); }
    }

    /// <summary>自定义模式：强制展开全部高级项，改用逐项勾选隐藏。</summary>
    public bool CustomMode
    {
        get => _cfg.CustomMode;
        set
        {
            _cfg.CustomMode = value; _cfg.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowAdvancedToggleVisible));
            RebuildVisibility();
        }
    }

    /// <summary>自定义模式下「启用高级调整」开关本身要藏起来。</summary>
    public Visibility ShowAdvancedToggleVisible => CustomMode ? Visibility.Collapsed : Visibility.Visible;

    private void RebuildVisibility()
    {
        Vis     = SettingsCatalog.All.ToDictionary(
                      i => i.Key, i => SettingsCatalog.IsVisible(_cfg, i.Key) ? Visibility.Visible : Visibility.Collapsed);
        Enabled = SettingsCatalog.All.ToDictionary(
                      i => i.Key, i => SettingsCatalog.IsEnabled(_cfg, i.Key));
        OnPropertyChanged(nameof(Vis));
        OnPropertyChanged(nameof(Enabled));
    }

    private void DoEditVisibility()
    {
        if (EditVisibility is null) return;
        var result = EditVisibility(_cfg.HiddenSettings.ToList());
        if (result is null) return;                       // 用户取消
        _cfg.HiddenSettings = result.ToList();
        _cfg.Save();                                      // 保存并退出编辑界面即写盘
        RebuildVisibility();
        OnEngineLog($"自定义面板已更新：隐藏 {result.Count} 项");
    }

    private void DoResetDefaults()
    {
        if (Confirm is not null && !Confirm("确定恢复全部默认设置吗？\n（源文件夹保留，其余全部归位，自定义隐藏项清空）"))
            return;
        _cfg.ResetToDefaults();
        RebuildVisibility();
        foreach (var p in typeof(MainViewModel).GetProperties())
            if (p.CanRead && p.GetIndexParameters().Length == 0)
                OnPropertyChanged(p.Name);
        OnEngineLog("已恢复默认设置");
    }

    // ── 引擎事件 ──────────────────────────────────────────────────────
    private void OnEngineLog(string line) => Dispatch(() =>
    {
        Logs.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
        while (Logs.Count > 500) Logs.RemoveAt(0);
    });

    private void OnEngineStateChanged() => Dispatch(() =>
    {
        var s = _engine.Snapshot();
        QueueTotal     = s.QueueTotal;
        Completed      = s.Completed;
        Failed         = s.Failed;
        Skipped        = s.Skipped;
        Remaining      = s.Remaining;
        CurrentFile    = s.CurrentFile;
        CurrentPercent = s.CurrentPercent * 100.0;
        SavedText      = FormatBytes(s.SavedBytes);
        EtaText        = FormatEta(s.EtaSeconds);
        IsRunning      = s.IsRunning;
        IsPaused       = s.IsPaused;

        int done = s.Completed + s.Failed + s.Skipped;
        OverallPercent = s.QueueTotal > 0 ? (done + s.CurrentPercent) / s.QueueTotal * 100.0 : 0;
        Status = !s.IsRunning ? "就绪" : s.IsPaused ? "已暂停" : "转换中";
    });

    private static void Dispatch(Action a)
    {
        var app = Application.Current;
        if (app is null) { a(); return; }
        if (app.Dispatcher.CheckAccess()) a();
        else app.Dispatcher.BeginInvoke(a);
    }

    // ── 动作 ──────────────────────────────────────────────────────────
    private static string? PickFolder(string description, string initial)
    {
        using var d = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(initial) ? initial : "",
        };
        return d.ShowDialog() == System.Windows.Forms.DialogResult.OK ? d.SelectedPath : null;
    }

    private void Browse()
    {
        var p = PickFolder("选择要压缩的源文件夹", TargetDir);
        if (p is not null) TargetDir = p;
    }

    private void BrowseFixed()
    {
        var p = PickFolder("选择固定输出路径", FixedOutputPath);
        if (p is not null) FixedOutputPath = p;
    }

    /// <summary>解析目标路径并建好输出文件夹。失败即返回 null（调用方必须放弃整批）。</summary>
    private OutputPlan? BuildOutputPlan()
    {
        try
        {
            string? baseDir = OutputPlan.ResolveBaseDir(_cfg, TargetDir,
                () => AskOutputDir?.Invoke() ?? PickFolder("选择本次的输出路径", TargetDir));
            if (baseDir is null) { OnEngineLog("已取消：未选择输出路径"); return null; }

            return OutputPlan.Prepare(_cfg, TargetDir, baseDir);
        }
        catch (OutputSetupException ex)
        {
            Log.Error(ex, "输出目录准备失败");
            ShowError?.Invoke("输出目录准备失败，已终止，未处理任何文件：\n\n" + ex.Message);
            OnEngineLog("✗ 输出目录准备失败：" + ex.Message);
            return null;
        }
    }

    private async Task ScanAsync()
    {
        try
        {
            Status = "扫描中…";
            var plan = BuildOutputPlan();
            if (plan is null) { Status = "就绪"; return; }

            await _engine.RecoverAsync(new[] { plan.SourceDir, plan.DestDir }, CancellationToken.None);
            var todo = await _engine.ScanAsync(plan, CancellationToken.None);
            QueueTotal = todo.Count;
            Remaining  = todo.Count;
            Completed  = Failed = Skipped = 0;
            OverallPercent = 0;
            RefreshHistory();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "扫描失败");
            OnEngineLog("扫描失败: " + ex.Message);
        }
        finally { Status = "就绪"; }
    }

    private async Task StartAsync()
    {
        var plan = BuildOutputPlan();
        if (plan is null) return;

        _cts = new CancellationTokenSource();
        try
        {
            await Task.Run(() => _engine.StartAsync(
                plan,
                report => ConfirmRisks?.Invoke(report) ?? true,
                report => ConfirmReprocess?.Invoke(report) ?? ReprocessDecision.Abort,
                _cts.Token));
        }
        catch (OperationCanceledException) { /* 停止属正常 */ }
        catch (Exception ex)
        {
            Log.Error(ex, "转换过程异常");
            OnEngineLog("转换过程异常: " + ex.Message);
        }
        finally
        {
            RefreshHistory();
            RefreshRecords();
            OnEngineStateChanged();
        }
    }

    private void RefreshHistory()
    {
        try
        {
            var c = _db.CountByStatus();
            HistorySuccess = c.GetValueOrDefault(RecordStatus.Success);
            HistoryFailed  = c.GetValueOrDefault(RecordStatus.Failed);
            HistorySaved   = FormatBytes(_db.TotalSavedBytes());
        }
        catch (Exception ex) { Log.Warning(ex, "读取历史统计失败"); }
    }

    private void RefreshRecords()
    {
        try
        {
            Records.Clear();
            foreach (var r in _db.GetAll(2000)) Records.Add(r);
        }
        catch (Exception ex) { Log.Warning(ex, "读取记录失败"); }
    }

    private void ExportCsv()
    {
        try
        {
            string path = Path.Combine(AppPaths.RecordsDir, $"transcode_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            _db.ExportCsv(path);
            OnEngineLog("已导出: " + path);
        }
        catch (Exception ex) { OnEngineLog("导出失败: " + ex.Message); }
    }

    private static void OpenFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warning(ex, "打开目录失败"); }
    }

    // ── 格式化 ────────────────────────────────────────────────────────
    public static string FormatBytes(long b)
    {
        if (b <= 0) return "0 B";
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {u[i]}";
    }

    private static string FormatEta(double sec)
    {
        if (sec <= 0 || double.IsInfinity(sec) || double.IsNaN(sec)) return "—";
        var t = TimeSpan.FromSeconds(sec);
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays} 天 {t.Hours} 小时";
        return $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";
    }
}
