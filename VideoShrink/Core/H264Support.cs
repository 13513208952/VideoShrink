using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace VideoShrink.Core;

public enum GpuVendor { Unknown, Nvidia, Intel, Amd }

public sealed record GpuInfo(string Name, GpuVendor Vendor, bool IsDiscrete)
{
    public override string ToString() =>
        $"{Name} [{Vendor}, {(IsDiscrete ? "独显" : "核显")}]";
}

public sealed class H264UnavailableException : Exception
{
    public H264UnavailableException(string message) : base(message) { }
}

public static class H264Support
{
    public const string CpuEncoder = "libx264";

    private static readonly string[] TrialArgsPrefix =
    {
        "-hide_banner", "-loglevel", "error", "-nostdin",
        "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25",
        "-frames:v", "1",
    };

    public static string EncoderFor(H264Impl impl) => impl switch
    {
        H264Impl.Nvenc => "h264_nvenc",
        H264Impl.Qsv   => "h264_qsv",
        H264Impl.Amf   => "h264_amf",
        H264Impl.Cpu   => CpuEncoder,
        _ => throw new ArgumentOutOfRangeException(nameof(impl), "Auto 没有固定编码器"),
    };

    public static bool IsGpuEncoder(string ffmpegEncoder) => ffmpegEncoder != CpuEncoder;

    // -- GPU 枚举 ------------------------------------------------------

    public static IReadOnlyList<GpuInfo> EnumerateGpus()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return EnumerateGpusWindows();
            else
                return EnumerateGpusLinux();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "枚举显卡失败，将只试 CPU 编码器");
            return Array.Empty<GpuInfo>();
        }
    }

    private static List<GpuInfo> EnumerateGpusWindows()
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(
            "Get-CimInstance Win32_VideoController | " +
            "ForEach-Object { \"$($_.Name)|$($_.PNPDeviceID)\" }");

        using var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(15000);

        var list = new List<GpuInfo>();
        foreach (var line in stdout.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0) continue;
            int bar = t.IndexOf('|');
            string name = (bar < 0 ? t : t[..bar]).Trim();
            string pnp  = (bar < 0 ? "" : t[(bar + 1)..]).Trim();
            if (name.Length == 0) continue;

            var vendor = VendorOf(pnp, name);
            if (vendor == GpuVendor.Unknown) continue;
            list.Add(new GpuInfo(name, vendor, IsDiscrete(vendor, name)));
        }
        return list;
    }

    private static List<GpuInfo> EnumerateGpusLinux()
    {
        var psi = new ProcessStartInfo("lspci")
        {
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-nn");

        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(10000);

        var list = new List<GpuInfo>();
        foreach (var line in output.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0) continue;

            bool isVga = t.Contains("VGA compatible", StringComparison.OrdinalIgnoreCase)
                      || t.Contains("Display controller", StringComparison.OrdinalIgnoreCase)
                      || t.Contains("3D controller", StringComparison.OrdinalIgnoreCase);
            if (!isVga) continue;

            int bracket = t.IndexOf(" [", StringComparison.Ordinal);
            string name = bracket > 0 ? t[..bracket].Trim() : t;

            var vendor = GpuVendor.Unknown;
            bool isDiscrete = false;

            if (t.Contains("10de", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("GeForce", StringComparison.OrdinalIgnoreCase))
            {
                vendor = GpuVendor.Nvidia;
                isDiscrete = !name.Contains("Tegra", StringComparison.OrdinalIgnoreCase);
            }
            else if (t.Contains("8086", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            {
                vendor = GpuVendor.Intel;
                isDiscrete = name.Contains("Arc", StringComparison.OrdinalIgnoreCase);
            }
            else if (t.Contains("1002", StringComparison.OrdinalIgnoreCase) ||
                     t.Contains("1022", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
            {
                vendor = GpuVendor.Amd;
                isDiscrete = !(name.Contains(" Graphics", StringComparison.OrdinalIgnoreCase)
                            && !name.Contains("RX", StringComparison.OrdinalIgnoreCase));
            }

            if (vendor != GpuVendor.Unknown)
                list.Add(new GpuInfo(name, vendor, isDiscrete));
        }
        return list;
    }

    private static GpuVendor VendorOf(string pnpDeviceId, string name)
    {
        string id = pnpDeviceId.ToUpperInvariant();
        if (id.Contains("VEN_10DE")) return GpuVendor.Nvidia;
        if (id.Contains("VEN_8086")) return GpuVendor.Intel;
        if (id.Contains("VEN_1002") || id.Contains("VEN_1022")) return GpuVendor.Amd;

        string n = name.ToUpperInvariant();
        if (n.Contains("NVIDIA") || n.Contains("GEFORCE") || n.Contains("QUADRO")) return GpuVendor.Nvidia;
        if (n.Contains("INTEL"))  return GpuVendor.Intel;
        if (n.Contains("AMD") || n.Contains("RADEON")) return GpuVendor.Amd;
        return GpuVendor.Unknown;
    }

    private static bool IsDiscrete(GpuVendor vendor, string name)
    {
        string n = name.ToUpperInvariant();
        return vendor switch
        {
            GpuVendor.Nvidia => !n.Contains("TEGRA"),
            GpuVendor.Intel  => n.Contains("ARC"),
            GpuVendor.Amd    => !(Regex.IsMatch(n, @"\b\d{3}M\b")
                                  || n.Contains("(TM) GRAPHICS")
                                  || Regex.IsMatch(n, @"\bVEGA\s+\d{1,2}\b")
                                  || n.EndsWith(" GRAPHICS") && !n.Contains("RX")),
            _ => false,
        };
    }

    public static string ComputeSignature(IReadOnlyList<GpuInfo> gpus)
    {
        string joined = string.Join(";", gpus.Select(g => $"{g.Vendor}:{g.Name}"));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(hash)[..16];
    }

    internal static List<string> CandidateEncoders(IReadOnlyList<GpuInfo> gpus)
    {
        var ordered = gpus.Where(g => g.IsDiscrete).Concat(gpus.Where(g => !g.IsDiscrete));

        var candidates = new List<string>();
        foreach (var g in ordered)
        {
            string? enc = g.Vendor switch
            {
                GpuVendor.Nvidia => "h264_nvenc",
                GpuVendor.Intel  => "h264_qsv",
                GpuVendor.Amd    => "h264_amf",
                _ => null,
            };
            if (enc is not null && !candidates.Contains(enc)) candidates.Add(enc);
        }
        candidates.Add(CpuEncoder);
        return candidates;
    }

    // -- 试编码 --------------------------------------------------------

    public static async Task<bool> TrialEncodeAsync(
        FfmpegRunner runner, string encoder, AppConfig cfg, CancellationToken ct)
    {
        var args = new List<string>(TrialArgsPrefix);
        args.Add("-c:v:0");
        args.Add(encoder);
        args.AddRange(EncoderArgs.H264QualityArgs(encoder, cfg));
        args.AddRange(new[] { "-f", "null", "NUL" });
        try
        {
            var r = await runner.RunAsync(AppPaths.FfmpegPath, args, null, ct).ConfigureAwait(false);
            if (!r.Ok) Log.Debug("试编码 {Enc} 失败: {Err}", encoder, Trim(r.Stderr));
            return r.Ok;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Debug(ex, "试编码 {Enc} 抛异常", encoder);
            return false;
        }
    }

    // -- 解析 ----------------------------------------------------------

    public static async Task<string> ResolveAsync(
        AppConfig cfg, FfmpegRunner runner, CancellationToken ct)
    {
        if (cfg.H264Impl != H264Impl.Auto)
        {
            string enc = EncoderFor(cfg.H264Impl);
            if (await TrialEncodeAsync(runner, enc, cfg, ct).ConfigureAwait(false))
            {
                Log.Information("H.264 实现：{Enc}（用户指定，试编码通过）", enc);
                return enc;
            }
            throw new H264UnavailableException(
                $"你指定的 H.264 实现 {enc} 在本机不可用（试编码失败）。" +
                "请改选其它实现或使用 Auto —— 程序不会替你静默降级。");
        }

        var gpus = EnumerateGpus();
        string signature = ComputeSignature(gpus);

        if (!string.IsNullOrEmpty(cfg.H264ResolvedEncoder) && cfg.GpuSignature == signature)
        {
            Log.Information("H.264 实现：{Enc}（命中缓存）", cfg.H264ResolvedEncoder);
            return cfg.H264ResolvedEncoder;
        }

        Log.Information("探测显卡：{Gpus}", gpus.Count == 0 ? "（未发现）" : string.Join(" / ", gpus));

        foreach (string enc in CandidateEncoders(gpus))
        {
            if (await TrialEncodeAsync(runner, enc, cfg, ct).ConfigureAwait(false))
            {
                cfg.H264ResolvedEncoder = enc;
                cfg.GpuSignature        = signature;
                cfg.Save();
                Log.Information("H.264 实现：{Enc}（自动探测选中；候选按 独显>核显>CPU 排序）", enc);
                return enc;
            }
            Log.Information("H.264 实现 {Enc} 试编码不通过，顺延", enc);
        }

        throw new H264UnavailableException("连 libx264 都无法运行，ffmpeg 可能损坏");
    }

    public static void InvalidateCache(AppConfig cfg)
    {
        if (string.IsNullOrEmpty(cfg.H264ResolvedEncoder)) return;
        Log.Warning("H.264 编码器 {Enc} 运行时报错，清除探测缓存，下次重新探测", cfg.H264ResolvedEncoder);
        cfg.H264ResolvedEncoder = "";
        cfg.GpuSignature        = "";
        cfg.Save();
    }

    private static string Trim(string s)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length > 160 ? s[..160] + "..." : s;
    }
}
