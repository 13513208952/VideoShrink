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
/// 批处理引擎：扫描筛选 -> 队列 -> worker 执行 -> 暂停/停止/增量继续。
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
    /// <summary>整个「恢复->扫描->转码」过程都算忙，否则扫描阶段按钮会被重复点击。</summary>
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

    // -- 启动恢复 ----------------------------------------------------------
    /// <summary>
    /// 处理上次崩溃留下的临时文件。必须在扫描/转码之前调用，
    /// 且<b>源目录与输出目录都要扫</b>（成品可能落在别处）。
    /// </summary>
    public async Task RecoverAsync(IEnumerable<string> dirs, CancellationToken ct)
    {
        var runner = new FfmpegRunner();
        var msgs = await Replacer.RecoverAsync(runner, dirs, _cfg.Recursive, ct).ConfigureAwait(false);
        foreach (var m in msgs) Say(m);
    }

    // -- 扫描 --------------------------------------------------------------
    /// <summary>
    /// 筛出待转换文件。幂等/增量的依据已从「转完就不再是 4K」改为
    /// <b>记录库 + 目标文件是否已存在</b>——因为成品可能落在别的目录，
    /// 分辨率筛选不再能天然排除它。同时必须<b>排除输出目录本身</b>，
    /// 否则第二轮会把上一轮的成品当输入吃进去。
    /// </summary>
    public async Task<List<string>> ScanAsync(OutputPlan plan, CancellationToken ct)
    {
        string dir = plan.SourceDir;
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException(dir);

        var opt = _cfg.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        var bySize = Directory.EnumerateFiles(dir, "*", opt)
            .Where(f => !plan.IsExcluded(f))                       // 不吃自己的成品
            .Where(f => VideoExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Where(f => { try { return new FileInfo(f).Length > _cfg.MinSizeBytes; } catch { return false; } })
            .ToList();

        Say($"体积预筛：{bySize.Count} 个文件 > {_cfg.MinSizeBytes / 1048576.0:F0}MB，开始 ffprobe 确认...");

        var todo = new List<string>();
        var runner = new FfmpegRunner();
        int probed = 0, skippedByContainer = 0, skippedByDest = 0;

        foreach (var f in bySize)
        {
            ct.ThrowIfCancellationRequested();

            string? st = _db.GetStatus(f);
            if (st == RecordStatus.Success) continue;
            if (st == RecordStatus.Failed && !_cfg.RetryFailed) continue;

            // 新建文件夹模式：成品已在，视为已完成（增量继续）
            if (plan.SaveMode == SaveMode.NewFolder && File.Exists(plan.DestPathFor(f)))
            {
                skippedByDest++;
                continue;
            }

            // 容器能否承载所选编码器的产物（保持扩展名不变）
            if (!ContainerRules.CanOutput(f, _cfg.Encoder, out string whyNot))
            {
                skippedByContainer++;
                Log.Information("跳过 {File}: {Why}", Path.GetFileName(f), whyNot);
                continue;
            }

            var info = await Probe.RunAsync(runner, f, ct).ConfigureAwait(false);
            probed++;
            if (info?.Video is null) continue;
            if (_cfg.MinSourceHeight > 0 && info.Video.Height < _cfg.MinSourceHeight) continue;
            if (_cfg.EnableMinSourceWidth && _cfg.MinSourceWidth > 0 && info.Video.Width < _cfg.MinSourceWidth) continue;

            todo.Add(f);
            if (probed % 50 == 0) Say($"...已探测 {probed}/{bySize.Count}，命中 {todo.Count}");
        }

        if (skippedByDest > 0)      Say($"已有成品，跳过 {skippedByDest} 个（增量继续）");
        if (skippedByContainer > 0) Say($"因容器不兼容跳过 {skippedByContainer} 个");
        Say($"扫描完成：命中 {todo.Count} 个待转换");
        return todo;
    }

    // -- 运行 --------------------------------------------------------------
    /// <summary>
    /// 跑一整批。<paramref name="confirmRisks"/> 由界面提供：预检发现风险时弹窗，
    /// 返回 false 表示用户取消，此时<b>一个文件都不处理</b>。
    /// </summary>
    public async Task StartAsync(
        OutputPlan plan,
        Func<PreflightReport, bool> confirmRisks,
        Func<ReprocessReport, ReprocessDecision> confirmReprocess,
        CancellationToken externalCt)
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
            // -- 编码器：H.264 要先探测出具体实现（独显 > 核显 > CPU）
            var probeRunner = new FfmpegRunner();
            string videoEncoder = _cfg.Encoder switch
            {
                EncoderKind.X265   => "libx265",
                EncoderKind.SvtAv1 => "libsvtav1",
                _ => await H264Support.ResolveAsync(_cfg, probeRunner, ct).ConfigureAwait(false),
            };
            bool twoPass = EncoderArgs.SupportsTwoPass(videoEncoder);

            // 登记本次的输出目录：即便本轮中途放弃，下次递归扫到它也要认得出来。
            // 但 DestDir == SourceDir（覆盖回源目录）时绝不能登记，否则下一轮会把源目录里
            // 的每个文件都判成「位于输出目录内」。那种情形只能靠成品指纹去认。
            if (plan.ShouldTrackDestDir)
            {
                try { _db.AddOutputDir(plan.DestDir); }
                catch (Exception ex) { Log.Warning(ex, "登记输出目录失败"); }
            }

            await RecoverAsync(new[] { plan.SourceDir, plan.DestDir }, ct).ConfigureAwait(false);

            var todo = await ScanAsync(plan, ct).ConfigureAwait(false);
            if (todo.Count == 0) { Say("没有需要转换的文件。"); return; }

            // -- 重复处理防护：这些候选里有没有我们自己上一轮的成品？
            //    覆盖模式与新建文件夹模式一视同仁，两条通道同时查。
            var reproc = await ReprocessGuard.CheckAsync(_db, todo, ct).ConfigureAwait(false);
            if (reproc.HasAny)
            {
                Say($"警告：发现 {reproc.Hits.Count} 个候选是此前的成品" +
                    $"（输出目录命中 {reproc.ByDir}，哈希命中 {reproc.ByHash}）");
                var decision = confirmReprocess(reproc);
                todo = ReprocessGuard.Apply(todo, reproc, decision);

                switch (decision)
                {
                    case ReprocessDecision.Abort:
                        Say("已放弃本次操作，未处理任何文件。");
                        return;
                    case ReprocessDecision.ExcludeProcessed:
                        Say($"已排除 {reproc.Hits.Count} 个已处理文件，继续处理其余 {todo.Count} 个。");
                        break;
                    case ReprocessDecision.IncludeAll:
                        Say("警告：用户选择全部包含：已处理文件将被再次压缩（画质不可逆损失）。");
                        break;
                }
                if (todo.Count == 0) { Say("排除之后没有需要转换的文件。"); return; }
            }

            // -- 预检：把会丢的东西、会被压的音频、过低的码率全找出来，编码前一次性告知
            var report = await PreflightReport.BuildAsync(
                probeRunner, _cfg, plan.SaveMode, todo, ct).ConfigureAwait(false);

            if (report.HasAny && !confirmRisks(report))
            {
                Say("用户取消，未处理任何文件。");
                return;
            }
            // 用户点了「我已了解，继续」=> 允许为腾出视频码率预算而压缩音频
            _cfg.AllowAudioCompressionForBudget = report.HasAny;

            foreach (var f in todo) _queue.Enqueue(f);
            _queueTotal = todo.Count;
            Notify();

            int par = Math.Clamp(_cfg.Parallelism, 1, 4);
            // 显式打出「实际生效」的编码器与 preset，避免用户以为设了却没生效
            Say($"开始转换：{_queueTotal} 个文件，并行 {par}，编码器 {videoEncoder}"
              + $"（preset {EncoderArgs.EffectivePreset(_cfg, videoEncoder)}，"
              + $"{(twoPass ? "两趟 ABR，体积精确" : "单趟 VBR，体积精度较低")}）"
              + $"，输出 -> {plan.DestDir}");

            var workers = new List<Task>();
            for (int i = 0; i < par; i++)
            {
                var runner = new FfmpegRunner();
                _runners.Add(runner);
                int id = i;
                workers.Add(Task.Run(() => WorkerAsync(id, runner, plan, videoEncoder, twoPass, ct), ct));
            }

            await Task.WhenAll(workers).ConfigureAwait(false);

            Say($"全部结束：成功 {_completed}，失败 {_failed}，跳过 {_skipped}，" +
                $"共节省 {_savedBytes / 1073741824.0:F2}GB");
        }
        catch (H264UnavailableException ex)
        {
            Say("失败：" + ex.Message);
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

    private async Task WorkerAsync(
        int id, FfmpegRunner runner, OutputPlan plan, string videoEncoder, bool twoPass, CancellationToken ct)
    {
        var job = new TranscodeJob(runner, _cfg, _db, () => WaitIfPausedAsync(ct), plan, videoEncoder, twoPass);

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
                    Say($"完成 {Path.GetFileName(file)}，省 {outcome.SavedBytes / 1048576.0:F0}MB " +
                        $"（耗时 {sw.Elapsed:hh\\:mm\\:ss}）");
                    break;

                case JobStatus.Failed:
                    Interlocked.Increment(ref _failed);
                    Say($"失败 {Path.GetFileName(file)}：{outcome.Reason}");
                    break;

                case JobStatus.Skipped:
                    Interlocked.Increment(ref _skipped);
                    Say($"跳过 {Path.GetFileName(file)}：{outcome.Reason}");
                    break;
            }

            _active.TryRemove(id, out _);
            Notify();
        }
    }

    // -- 暂停 / 继续 / 停止 ------------------------------------------------
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
        Say("正在停止...");
        _pauseTcs.TrySetResult();               // 先放行，免得 worker 卡在暂停门上
        foreach (var r in _runners) r.KillCurrent();
        _cts?.Cancel();
        Notify();
    }
}
