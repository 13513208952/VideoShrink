using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace VideoShrink.Core;

public sealed record ProcResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>
/// ffmpeg / ffprobe 子进程封装：
/// - 用 ArgumentList 传参，杜绝引号/空格路径问题
/// - 解析 ffmpeg `-progress pipe:1` 的 out_time_us 得到实时进度
/// - 暂停 = NtSuspendProcess 真挂起（CPU 归零），继续 = NtResumeProcess
/// </summary>
public sealed class FfmpegRunner
{
    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtResumeProcess(IntPtr processHandle);

    private readonly object _lock = new();
    private Process? _current;
    private bool _suspended;

    public bool IsSuspended { get { lock (_lock) return _suspended; } }

    /// <summary>挂起当前正在跑的 ffmpeg（CPU 占用归零，进度冻结）。</summary>
    public void SuspendCurrent()
    {
        lock (_lock)
        {
            if (_current is { HasExited: false } p && !_suspended)
            {
                try { NtSuspendProcess(p.Handle); _suspended = true; }
                catch (Exception ex) { Log.Warning(ex, "挂起 ffmpeg 失败"); }
            }
        }
    }

    public void ResumeCurrent()
    {
        lock (_lock)
        {
            if (_current is { HasExited: false } p && _suspended)
            {
                try { NtResumeProcess(p.Handle); _suspended = false; }
                catch (Exception ex) { Log.Warning(ex, "恢复 ffmpeg 失败"); }
            }
        }
    }

    /// <summary>停止：先恢复（挂起态无法被 kill 干净）再杀。</summary>
    public void KillCurrent()
    {
        lock (_lock)
        {
            if (_current is { HasExited: false } p)
            {
                try
                {
                    if (_suspended) { NtResumeProcess(p.Handle); _suspended = false; }
                    p.Kill(entireProcessTree: true);
                }
                catch (Exception ex) { Log.Warning(ex, "终止 ffmpeg 失败"); }
            }
        }
    }

    /// <summary>
    /// 跑一个进程并抓全部输出。onProgressLine 非空时逐行喂 stdout（用于 -progress）。
    /// </summary>
    public async Task<ProcResult> RunAsync(
        string exe,
        IReadOnlyList<string> args,
        Action<string>? onStdoutLine,
        CancellationToken ct,
        string? workingDir = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = exe,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
        };
        if (!string.IsNullOrEmpty(workingDir)) psi.WorkingDirectory = workingDir;
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var sbOut = new StringBuilder();
        var sbErr = new StringBuilder();

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            if (onStdoutLine is not null) onStdoutLine(e.Data);
            else sbOut.Append(e.Data).Append('\n');
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            // stderr 可能很长（整片解码报错），限个上限防爆内存
            if (sbErr.Length < 64 * 1024) sbErr.Append(e.Data).Append('\n');
        };

        proc.Start();
        lock (_lock) { _current = proc; _suspended = false; }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillCurrent();
            throw;
        }
        finally
        {
            lock (_lock) { _current = null; _suspended = false; }
        }

        return new ProcResult(proc.ExitCode, sbOut.ToString(), sbErr.ToString());
    }

    /// <summary>
    /// 跑 ffmpeg 并按 `-progress pipe:1` 汇报进度（0..1）。调用方须已在 args 里带 -progress pipe:1 -nostats。
    /// </summary>
    public Task<ProcResult> RunFfmpegWithProgressAsync(
        IReadOnlyList<string> args,
        double totalSeconds,
        IProgress<double>? progress,
        CancellationToken ct,
        string? workingDir = null)
    {
        return RunAsync(AppPaths.FfmpegPath, args, line =>
        {
            if (progress is null || totalSeconds <= 0) return;
            double? sec = ParseProgressSeconds(line);
            if (sec is not null)
            {
                double p = sec.Value / totalSeconds;
                progress.Report(p < 0 ? 0 : (p > 1 ? 1 : p));
            }
        }, ct, workingDir);
    }

    /// <summary>
    /// 解析 -progress 的时间行。优先 out_time_us；回退 out_time_ms
    /// （ffmpeg 的 out_time_ms 历史上其实也是微秒，故同样按 1e6 换算）。
    /// </summary>
    internal static double? ParseProgressSeconds(string line)
    {
        int eq = line.IndexOf('=');
        if (eq <= 0) return null;
        var key = line.AsSpan(0, eq);
        var val = line.AsSpan(eq + 1);

        if (key.SequenceEqual("out_time_us") || key.SequenceEqual("out_time_ms"))
        {
            if (long.TryParse(val, out long us) && us >= 0) return us / 1_000_000.0;
            return null;   // 可能是 "N/A"
        }
        return null;
    }
}
