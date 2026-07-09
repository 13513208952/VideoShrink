using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using VideoShrink.Core;
using VideoShrink.Data;
using Serilog;
// UseWindowsForms=true 会让 Application 在 WPF/WinForms 间产生歧义
using Application = System.Windows.Application;

namespace VideoShrink.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private readonly AppConfig       _cfg;
    private readonly RecordDb        _db;
    private readonly TranscodeEngine _engine;
    private CancellationTokenSource? _cts;

    public ObservableCollection<string>    Logs    { get; } = new();
    public ObservableCollection<RecordRow> Records { get; } = new();

    /// <summary>两套 preset 列表，均按「压缩率高→低 / 速度慢→快」排序。</summary>
    public IReadOnlyList<PresetOption<string>> X265Presets { get; } = EncoderPresets.X265;
    public IReadOnlyList<PresetOption<int>>    Av1Presets  { get; } = EncoderPresets.Av1;

    public IReadOnlyList<EncoderKind> Encoders { get; } =
        new[] { EncoderKind.X265, EncoderKind.SvtAv1 };

    public IReadOnlyList<AudioPolicy> AudioPolicies { get; } =
        new[] { AudioPolicy.Auto, AudioPolicy.CopyOnly };

    public MainViewModel()
    {
        _cfg    = AppConfig.Load();
        _db     = new RecordDb(AppPaths.DbPath);
        _engine = new TranscodeEngine(_cfg, _db);

        _engine.LogLine     += OnEngineLog;
        _engine.StateChanged += OnEngineStateChanged;

        _targetDir = _cfg.TargetDir;

        BrowseCommand      = new RelayCommand(Browse, () => !IsRunning);
        ScanCommand        = new RelayCommand(async () => await ScanAsync(), () => !IsRunning && HasDir);
        StartCommand       = new RelayCommand(async () => await StartAsync(), () => !IsRunning && HasDir);
        PauseCommand       = new RelayCommand(() => _engine.Pause(),  () => IsRunning && !IsPaused);
        ResumeCommand      = new RelayCommand(() => _engine.Resume(), () => IsRunning && IsPaused);
        StopCommand        = new RelayCommand(() => _engine.Stop(),   () => IsRunning);
        ExportCsvCommand   = new RelayCommand(ExportCsv);
        RefreshRecordsCommand = new RelayCommand(RefreshRecords);
        OpenRecordsCommand = new RelayCommand(() => OpenFolder(AppPaths.RecordsDir));

        RefreshHistory();
        RefreshRecords();
    }

    // ── 命令 ──────────────────────────────────────────────────────────────
    public RelayCommand BrowseCommand         { get; }
    public RelayCommand ScanCommand           { get; }
    public RelayCommand StartCommand          { get; }
    public RelayCommand PauseCommand          { get; }
    public RelayCommand ResumeCommand         { get; }
    public RelayCommand StopCommand           { get; }
    public RelayCommand ExportCsvCommand      { get; }
    public RelayCommand RefreshRecordsCommand { get; }
    public RelayCommand OpenRecordsCommand    { get; }

    // ── 目标文件夹 ────────────────────────────────────────────────────────
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

    // ── 进度状态 ──────────────────────────────────────────────────────────
    private int _queueTotal;   public int QueueTotal { get => _queueTotal; private set => SetField(ref _queueTotal, value); }
    private int _completed;    public int Completed  { get => _completed;  private set => SetField(ref _completed, value); }
    private int _failed;       public int Failed     { get => _failed;     private set => SetField(ref _failed, value); }
    private int _skipped;      public int Skipped    { get => _skipped;    private set => SetField(ref _skipped, value); }
    private int _remaining;    public int Remaining  { get => _remaining;  private set => SetField(ref _remaining, value); }

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

    private bool _isRunning; public bool IsRunning { get => _isRunning; private set { if (SetField(ref _isRunning, value)) OnPropertyChanged(nameof(IsIdle)); } }
    private bool _isPaused;  public bool IsPaused  { get => _isPaused;  private set => SetField(ref _isPaused, value); }
    public bool IsIdle => !IsRunning;

    private string _status = "就绪";
    public string Status { get => _status; private set => SetField(ref _status, value); }

    // ── 历史累计 ──────────────────────────────────────────────────────────
    private int _historySuccess; public int HistorySuccess { get => _historySuccess; private set => SetField(ref _historySuccess, value); }
    private int _historyFailed;  public int HistoryFailed  { get => _historyFailed;  private set => SetField(ref _historyFailed, value); }
    private string _historySaved = "0 B";
    public string HistorySaved { get => _historySaved; private set => SetField(ref _historySaved, value); }

    // ── 设置 ──────────────────────────────────────────────────────────────
    public int TargetRatioPercent
    {
        get => (int)Math.Round(_cfg.TargetRatio * 100);
        set { _cfg.TargetRatio = Math.Clamp(value, 1, 99) / 100.0; _cfg.Save(); OnPropertyChanged(); }
    }

    public int TargetHeight
    {
        get => _cfg.TargetHeight;
        set { _cfg.TargetHeight = value; _cfg.Save(); OnPropertyChanged(); }
    }

    public double MinSizeGb
    {
        get => _cfg.MinSizeBytes / 1073741824.0;
        set { _cfg.MinSizeBytes = (long)(Math.Max(0.01, value) * 1073741824.0); _cfg.Save(); OnPropertyChanged(); }
    }

    public int MinSourceHeight
    {
        get => _cfg.MinSourceHeight;
        set { _cfg.MinSourceHeight = value; _cfg.Save(); OnPropertyChanged(); }
    }

    /// <summary>x265 preset（仅 Encoder=X265 时生效）。</summary>
    public string Preset
    {
        get => _cfg.Preset;
        set { _cfg.Preset = value; _cfg.Save(); OnPropertyChanged(); }
    }

    /// <summary>SVT-AV1 preset 0–13（仅 Encoder=SvtAv1 时生效）。0 压得最狠最慢，13 最快最差。</summary>
    public int PresetAv1
    {
        get => _cfg.PresetAv1;
        set { _cfg.PresetAv1 = EncoderPresets.ClampAv1(value); _cfg.Save(); OnPropertyChanged(); }
    }

    /// <summary>供界面切换：哪一套 preset 才是当前生效的。</summary>
    public bool IsAv1  => _cfg.Encoder == EncoderKind.SvtAv1;
    public bool IsX265 => _cfg.Encoder == EncoderKind.X265;

    public EncoderKind Encoder
    {
        get => _cfg.Encoder;
        set
        {
            _cfg.Encoder = value; _cfg.Save(); OnPropertyChanged();
            OnPropertyChanged(nameof(IsAv1));
            OnPropertyChanged(nameof(IsX265));
        }
    }

    public bool Recursive
    {
        get => _cfg.Recursive;
        set { _cfg.Recursive = value; _cfg.Save(); OnPropertyChanged(); }
    }

    public bool RetryFailed
    {
        get => _cfg.RetryFailed;
        set { _cfg.RetryFailed = value; _cfg.Save(); OnPropertyChanged(); }
    }

    public bool FullDecodeCheck
    {
        get => _cfg.FullDecodeCheck;
        set { _cfg.FullDecodeCheck = value; _cfg.Save(); OnPropertyChanged(); }
    }

    /// <summary>Auto：容器容不下就回落重编码并记账；CopyOnly：绝不重编码，容不下即判失败。</summary>
    public AudioPolicy AudioPolicy
    {
        get => _cfg.AudioPolicy;
        set { _cfg.AudioPolicy = value; _cfg.Save(); OnPropertyChanged(); }
    }

    public int AudioReencodeBitrateK
    {
        get => _cfg.AudioReencodeBitrateK;
        set { _cfg.AudioReencodeBitrateK = Math.Clamp(value, 32, 640); _cfg.Save(); OnPropertyChanged(); }
    }

    public bool KeepSubtitles
    {
        get => _cfg.KeepSubtitles;
        set { _cfg.KeepSubtitles = value; _cfg.Save(); OnPropertyChanged(); }
    }

    public int Parallelism
    {
        get => _cfg.Parallelism;
        set { _cfg.Parallelism = Math.Clamp(value, 1, 4); _cfg.Save(); OnPropertyChanged(); }
    }

    // ── 引擎事件 ──────────────────────────────────────────────────────────
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
        OverallPercent = s.QueueTotal > 0
            ? (done + (s.CurrentPercent)) / s.QueueTotal * 100.0
            : 0;

        Status = !s.IsRunning ? "就绪"
               : s.IsPaused   ? "已暂停"
               : "转换中";
    });

    private static void Dispatch(Action a)
    {
        var app = Application.Current;
        if (app is null) return;
        if (app.Dispatcher.CheckAccess()) a();
        else app.Dispatcher.BeginInvoke(a);
    }

    // ── 动作 ──────────────────────────────────────────────────────────────
    private void Browse()
    {
        using var d = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择包含 4K 视频的目标文件夹",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(TargetDir) ? TargetDir : "",
        };
        if (d.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            TargetDir = d.SelectedPath;
    }

    private async Task ScanAsync()
    {
        try
        {
            Status = "扫描中…";
            await _engine.RecoverAsync(TargetDir, CancellationToken.None);
            var todo = await _engine.ScanAsync(TargetDir, CancellationToken.None);
            QueueTotal = todo.Count;
            Remaining  = todo.Count;
            Completed  = Failed = Skipped = 0;
            OverallPercent = 0;
            Status = "就绪";
            RefreshHistory();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "扫描失败");
            OnEngineLog("扫描失败: " + ex.Message);
            Status = "就绪";
        }
    }

    private async Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        try
        {
            await Task.Run(() => _engine.StartAsync(TargetDir, _cts.Token));
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
            string path = Path.Combine(AppPaths.RecordsDir,
                $"transcode_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
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

    // ── 格式化 ────────────────────────────────────────────────────────────
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
