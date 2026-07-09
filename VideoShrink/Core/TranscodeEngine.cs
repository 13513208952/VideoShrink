using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VideoShrink.Data;
using Serilog;

namespace VideoShrink.Core;

public sealed class EngineSnapshot
{
    public int    QueueTotal    { get; init; }
    public int    Completed     { get; init; }
    public int    Failed        { get; init; }
    public int    Skipped       { get; init; }
    public int    Remaining     { get; init; }
    public string CurrentFile   { get; init; } = "";
    public double CurrentPercent{ get; init; }
    public long   SavedBytes    { get; init; }
    public double EtaSeconds    { get; init; }
    public bool   IsRunning     { get; init; }
    public bool   IsPaused      { get; init; }
}

/// <summary>
/// 批处理引擎：扫描筛选 → 队列 → worker 执行 → 暂停/停止/增量继续。
///
/// 增量的秘密：成品只有原体积 ~15%（约 300MB），天然掉到 1GB 阈值之下，
/// 下次扫描连 ffprobe 都不用跑就被体积预筛排除。再叠加记录库双保险。
/// </summary>
public sealed class TranscodeEngine
{
    // 只认「能作为输出容器」的扩展名。就地替换必须保持文件名，
    // 所以 .ts/.flv 这类无法承载 HEVC/AV1 的容器根本不该进队列（进了只会白跑一趟）。
    private static string[] VideoExts => ContainerRules.KnownExtensions.ToArray();

    private readonly AppConfig _cfg;
    private readonly RecordDb  _db;
    private readonly List<FfmpegRunner> _runners = new();

    private readonly ConcurrentQueue<string> _queue = new();
    private readonly ConcurrentDictionary<int, string> _active = new();

    private CancellationTokenSource? _cts;
    private TaskCompletionSource _pauseTcs = Completed();
    /// <summary>整个「恢复→扫描→转码」过程都算忙，否则扫描阶段按钮会被重复点击。</summary>
    private volatile bool _busy;

    private int  _queueTotal, _completed, _failed, _skipped;
    private long _savedBytes;
    private double _currentPercent;
    private readonly object _etaLock = new();
    private double _elapsedSum;
    private int    _elapsedCount;

    public event Action<string>? LogLine;
    public event Action? StateChanged;

    public bool IsRunning => _busy;
    public bool IsPaused  => !_pauseTcs.Task.IsCompleted;

    public TranscodeEngine(AppConfig cfg, RecordDb db)
    {
        _cfg = cfg;
        _db  = db;
    }

    private static TaskCompletionSource Completed()
    {
        var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        t.SetResult();
        return t;
    }

    public EngineSnapshot Snapshot() => new()
    {
        QueueTotal     = _queueTotal,
        Completed      = _completed,
        Failed         = _failed,
        Skipped        = _skipped,
        Remaining      = Math.Max(0, _queueTotal - _completed - _failed - _skipped),
        CurrentFile    = _active.Values.FirstOrDefault() ?? "",
        CurrentPercent = _currentPercent,
        SavedBytes     = _savedBytes,
        EtaSeconds     = EstimateEta(),
        IsRunning      = IsRunning,
        IsPaused       = IsPaused,
    };

    private double EstimateEta()
    {
        lock (_etaLock)
        {
            if (_elapsedCount == 0) return 0;
            double avg = _elapsedSum / _elapsedCount;
            int remaining = Math.Max(0, _queueTotal - _completed - _failed - _skipped);
            int par = Math.Max(1, _cfg.Parallelism);
            return avg * remaining / par;
        }
    }

    private void Notify() => StateChanged?.Invoke();
    private void Say(string s) { Log.Information(s); LogLine?.Invoke(s); }

    // ── 启动恢复 ──────────────────────────────────────────────────────────
    /// <summary>处理上次崩溃留下的 *.transcoding.tmp。必须在扫描/转码之前调用。</summary>
    public async Task RecoverAsync(string dir, CancellationToken ct)
    {
        var runner = new FfmpegRunner();
        var msgs = await Replacer.RecoverAsync(runner, dir, _cfg.Recursive, ct).ConfigureAwait(false);
        foreach (var m in msgs) Say(m);
    }

    // ── 扫描 ──────────────────────────────────────────────────────────────
    /// <summary>筛出「体积 &gt; 阈值 且 分辨率 ≥ 4K」的文件，并按记录库排除已成功/已失败项。</summary>
    public async Task<List<string>> ScanAsync(string dir, CancellationToken ct)
    {
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException(dir);

        var opt = _cfg.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        // 1) 体积预筛（极快，且天然排除已转换的成品）
        var bySize = Directory.EnumerateFiles(dir, "*", opt)
            .Where(f => VideoExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Where(f => { try { return new FileInfo(f).Length > _cfg.MinSizeBytes; } catch { return false; } })
            .ToList();

        Say($"体积预筛：{bySize.Count} 个文件 > {_cfg.MinSizeBytes / 1073741824.0:F2}GB，开始 ffprobe 确认分辨率…");

        // 2) 记录库排除
        var todo = new List<string>();
        var runner = new FfmpegRunner();
        int probed = 0, skippedByContainer = 0;

        foreach (var f in bySize)
        {
            ct.ThrowIfCancellationRequested();

            string? st = _db.GetStatus(f);
            if (st == RecordStatus.Success) continue;
            if (st == RecordStatus.Failed && !_cfg.RetryFailed) continue;

            // 容器能否承载所选编码器的产物（就地替换不能改扩展名）
            if (!ContainerRules.CanOutput(f, _cfg.Encoder, out string whyNot))
            {
                skippedByContainer++;
                Log.Information("跳过 {File}: {Why}", Path.GetFileName(f), whyNot);
                continue;
            }

            var info = await Probe.RunAsync(runner, f, ct).ConfigureAwait(false);
            probed++;
            if (info?.Video is null) continue;
            if (info.Video.Height < _cfg.MinSourceHeight) continue;

            todo.Add(f);

            if (probed % 50 == 0) Say($"…已探测 {probed}/{bySize.Count}，命中 {todo.Count}");
        }

        if (skippedByContainer > 0)
            Say($"因容器不兼容跳过 {skippedByContainer} 个（所选编码器的产物装不进其扩展名）");
        Say($"扫描完成：命中 {todo.Count} 个待转换（{_cfg.MinSourceHeight}p+ 且 >阈值）");
        return todo;
    }

    // ── 运行 ──────────────────────────────────────────────────────────────
    public async Task StartAsync(string dir, CancellationToken externalCt)
    {
        if (_busy) return;
        _busy = true;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        var ct = _cts.Token;

        _pauseTcs = Completed();
        _completed = _failed = _skipped = 0;
        _savedBytes = 0;
        _currentPercent = 0;
        _queueTotal = 0;
        lock (_etaLock) { _elapsedSum = 0; _elapsedCount = 0; }
        _active.Clear();
        _runners.Clear();
        while (_queue.TryDequeue(out _)) { }
        Notify();

        try
        {
            await RecoverAsync(dir, ct).ConfigureAwait(false);

            var todo = await ScanAsync(dir, ct).ConfigureAwait(false);
            foreach (var f in todo) _queue.Enqueue(f);
            _queueTotal = todo.Count;
            Notify();

            if (_queueTotal == 0) { Say("没有需要转换的文件。"); return; }

            int par = Math.Clamp(_cfg.Parallelism, 1, 4);
            // 显式打出「实际生效」的 preset：AV1 用数字、x265 用名字，避免用户以为设了却没生效
            Say($"开始转换：{_queueTotal} 个文件，并行 {par}，编码器 {_cfg.Encoder}，"
              + $"preset {EncoderArgs.EffectivePreset(_cfg)}（实际生效）");

            var workers = new List<Task>();
            for (int i = 0; i < par; i++)
            {
                var runner = new FfmpegRunner();
                _runners.Add(runner);
                int id = i;
                workers.Add(Task.Run(() => WorkerAsync(id, runner, ct), ct));
            }

            await Task.WhenAll(workers).ConfigureAwait(false);

            Say($"全部结束：成功 {_completed}，失败 {_failed}，跳过 {_skipped}，" +
                $"共节省 {_savedBytes / 1073741824.0:F2}GB");
        }
        catch (OperationCanceledException)
        {
            Say("已停止。原文件均未被修改。");
        }
        finally
        {
            _active.Clear();
            _busy = false;
            _pauseTcs.TrySetResult();   // 免得残留的暂停门卡住下一轮
            Notify();
        }
    }

    private async Task WorkerAsync(int id, FfmpegRunner runner, CancellationToken ct)
    {
        var job = new TranscodeJob(runner, _cfg, _db, () => WaitIfPausedAsync(ct));

        while (_queue.TryDequeue(out var file))
        {
            ct.ThrowIfCancellationRequested();
            await WaitIfPausedAsync(ct).ConfigureAwait(false);

            _active[id] = Path.GetFileName(file);
            _currentPercent = 0;
            Notify();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var progress = new Progress<double>(p => { _currentPercent = p; Notify(); });

            JobOutcome outcome;
            try
            {
                outcome = await job.RunAsync(file, progress, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _active.TryRemove(id, out _);
                throw;
            }
            sw.Stop();

            switch (outcome.Status)
            {
                case JobStatus.Success:
                    Interlocked.Increment(ref _completed);
                    Interlocked.Add(ref _savedBytes, outcome.SavedBytes);
                    lock (_etaLock) { _elapsedSum += sw.Elapsed.TotalSeconds; _elapsedCount++; }
                    Say($"✓ {Path.GetFileName(file)} 完成，省 {outcome.SavedBytes / 1048576.0:F0}MB " +
                        $"（耗时 {sw.Elapsed:hh\\:mm\\:ss}）");
                    break;

                case JobStatus.Failed:
                    Interlocked.Increment(ref _failed);
                    Say($"✗ {Path.GetFileName(file)} 失败：{outcome.Reason}");
                    break;

                case JobStatus.Skipped:
                    Interlocked.Increment(ref _skipped);
                    Say($"– {Path.GetFileName(file)} 跳过：{outcome.Reason}");
                    break;
            }

            _active.TryRemove(id, out _);
            Notify();
        }
    }

    // ── 暂停 / 继续 / 停止 ────────────────────────────────────────────────
    private Task WaitIfPausedAsync(CancellationToken ct)
    {
        var tcs = _pauseTcs;
        if (tcs.Task.IsCompleted) return Task.CompletedTask;
        return tcs.Task.WaitAsync(ct);
    }

    /// <summary>暂停：真挂起正在跑的 ffmpeg（CPU 归零），并挡住下一趟的启动。</summary>
    public void Pause()
    {
        if (IsPaused || !IsRunning) return;
        _pauseTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (var r in _runners) r.SuspendCurrent();
        Say("已暂停（ffmpeg 进程已挂起，CPU 占用归零）");
        Notify();
    }

    public void Resume()
    {
        if (!IsPaused) return;
        foreach (var r in _runners) r.ResumeCurrent();
        _pauseTcs.TrySetResult();
        Say("已继续");
        Notify();
    }

    /// <summary>停止：杀掉 ffmpeg、丢弃缓存产物。原文件绝不被动。</summary>
    public void Stop()
    {
        if (!IsRunning) return;
        Say("正在停止…");
        _pauseTcs.TrySetResult();               // 先放行，免得 worker 卡在暂停门上
        foreach (var r in _runners) r.KillCurrent();
        _cts?.Cancel();
        Notify();
    }
}
