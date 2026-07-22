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

public sealed class FfmpegRunner
{
    private readonly object _lock = new();
    private Process? _current;
    private bool _suspended;

    public bool IsSuspended { get { lock (_lock) return _suspended; } }

    public void SuspendCurrent()
    {
        lock (_lock)
        {
            if (_current is { HasExited: false } p && !_suspended)
            {
                try { SignalProcess(p, stop: true); _suspended = true; }
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
                try { SignalProcess(p, stop: false); _suspended = false; }
                catch (Exception ex) { Log.Warning(ex, "恢复 ffmpeg 失败"); }
            }
        }
    }

    public void KillCurrent()
    {
        lock (_lock)
        {
            if (_current is { HasExited: false } p)
            {
                try
                {
                    if (_suspended) { SignalProcess(p, stop: false); _suspended = false; }
                    p.Kill(entireProcessTree: true);
                }
                catch (Exception ex) { Log.Warning(ex, "终止 ffmpeg 失败"); }
            }
        }
    }

    private static void SignalProcess(Process p, bool stop)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (stop) NtSuspendProcess(p.Handle);
            else      NtResumeProcess(p.Handle);
        }
        else
        {
            var psi = new ProcessStartInfo("kill")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(stop ? "-STOP" : "-CONT");
            psi.ArgumentList.Add(p.Id.ToString());
            using var killer = Process.Start(psi);
            killer?.WaitForExit(2000);
        }
    }

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtResumeProcess(IntPtr processHandle);

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

    internal static double? ParseProgressSeconds(string line)
    {
        int eq = line.IndexOf('=');
        if (eq <= 0) return null;
        var key = line.AsSpan(0, eq);
        var val = line.AsSpan(eq + 1);

        if (key.SequenceEqual("out_time_us") || key.SequenceEqual("out_time_ms"))
        {
            if (long.TryParse(val, out long us) && us >= 0) return us / 1_000_000.0;
            return null;
        }
        return null;
    }
}
