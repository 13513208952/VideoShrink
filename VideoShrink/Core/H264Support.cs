using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
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

/// <summary>H.264 实现不可用（用户显式指定了本机跑不了的编码器）。</summary>
public sealed class H264UnavailableException : Exception
{
    public H264UnavailableException(string message) : base(message) { }
}

/// <summary>
/// H.264 实现的探测与选择。
///
/// 优先级：<b>独立显卡 &gt; 集成显卡 &gt; CPU</b>。厂商决定用哪个 ffmpeg 编码器
/// （NVIDIA→h264_nvenc，Intel→h264_qsv，AMD→h264_amf）。
///
/// <b>但厂商/独显判定只用来排「先试谁」的顺序，能不能用一律以 1 帧试编码为准。</b>
/// 这不是多此一举：ffmpeg 的编码器清单里永远有 h264_nvenc，机器上有没有 N 卡、
/// 驱动够不够新、笔记本的 dGPU 是否被禁用，只有实际试过才知道
/// （实测某台只有 AMD 核显的机器，h264_nvenc 依然出现在 -encoders 里，
///  试编码才报出 "Cannot load nvcuda.dll"）。
///
/// 探测结果连同 GPU 列表指纹缓存进 settings.json；GPU 变化或运行中遇到编码器
/// 初始化错误时重探。
/// </summary>
public static class H264Support
{
    public const string CpuEncoder = "libx264";

    private static readonly string[] TrialArgsPrefix =
    {
        "-hide_banner", "-loglevel", "error", "-nostdin",
        "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25",
        "-frames:v", "1",
    };

    /// <summary>H264Impl → ffmpeg 编码器名。<c>Auto</c> 无固定答案。</summary>
    public static string EncoderFor(H264Impl impl) => impl switch
    {
        H264Impl.Nvenc => "h264_nvenc",
        H264Impl.Qsv   => "h264_qsv",
        H264Impl.Amf   => "h264_amf",
        H264Impl.Cpu   => CpuEncoder,
        _ => throw new ArgumentOutOfRangeException(nameof(impl), "Auto 没有固定编码器"),
    };

    public static bool IsGpuEncoder(string ffmpegEncoder) => ffmpegEncoder != CpuEncoder;

    // ── GPU 枚举 ──────────────────────────────────────────────────────

    /// <summary>
    /// 用 PowerShell 的 CIM 查询枚举显卡。不引入 System.Management 依赖，
    /// 且只在首次运行 / 指纹失配 / 出错重探时调用一次。
    /// </summary>
    public static IReadOnlyList<GpuInfo> EnumerateGpus()
    {
        try
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
                if (vendor == GpuVendor.Unknown) continue;   // 虚拟显示器驱动等，忽略
                list.Add(new GpuInfo(name, vendor, IsDiscrete(vendor, name)));
            }
            return list;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "枚举显卡失败，将只试 CPU 编码器");
            return Array.Empty<GpuInfo>();
        }
    }

    private static GpuVendor VendorOf(string pnpDeviceId, string name)
    {
        string id = pnpDeviceId.ToUpperInvariant();
        if (id.Contains("VEN_10DE")) return GpuVendor.Nvidia;
        if (id.Contains("VEN_8086")) return GpuVendor.Intel;
        if (id.Contains("VEN_1002") || id.Contains("VEN_1022")) return GpuVendor.Amd;

        // PNPDeviceID 拿不到时退回名字匹配
        string n = name.ToUpperInvariant();
        if (n.Contains("NVIDIA") || n.Contains("GEFORCE") || n.Contains("QUADRO")) return GpuVendor.Nvidia;
        if (n.Contains("INTEL"))  return GpuVendor.Intel;
        if (n.Contains("AMD") || n.Contains("RADEON")) return GpuVendor.Amd;
        return GpuVendor.Unknown;
    }

    /// <summary>
    /// 独显 / 核显 判定。没有干净的系统 API，只能按型号命名规律判：
    /// - NVIDIA 台式/移动 GPU 一律视为独显（Tegra 除外）
    /// - Intel 只有 Arc 是独显；UHD / Iris / HD Graphics 都是核显
    /// - AMD 的核显叫 "Radeon 780M Graphics" / "Radeon(TM) Graphics" / "Vega 8 Graphics"；
    ///   RX / Pro / FirePro 是独显
    /// 判错的后果有限：它只影响「先试谁」，能不能用仍由试编码决定。
    /// </summary>
    private static bool IsDiscrete(GpuVendor vendor, string name)
    {
        string n = name.ToUpperInvariant();
        return vendor switch
        {
            GpuVendor.Nvidia => !n.Contains("TEGRA"),
            GpuVendor.Intel  => n.Contains("ARC"),
            GpuVendor.Amd    => !(Regex.IsMatch(n, @"\b\d{3}M\b")          // 680M / 760M / 780M
                                  || n.Contains("(TM) GRAPHICS")
                                  || Regex.IsMatch(n, @"\bVEGA\s+\d{1,2}\b")   // Vega 8 / Vega 11 核显
                                  || n.EndsWith(" GRAPHICS") && !n.Contains("RX")),
            _ => false,
        };
    }

    /// <summary>GPU 列表指纹。换了显卡就重探。</summary>
    public static string ComputeSignature(IReadOnlyList<GpuInfo> gpus)
    {
        string joined = string.Join(";", gpus.Select(g => $"{g.Vendor}:{g.Name}"));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(hash)[..16];
    }

    // ── 候选顺序：独显 > 核显 > CPU ────────────────────────────────────
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
        candidates.Add(CpuEncoder);   // 兜底，libx264 永远可用
        return candidates;
    }

    // ── 试编码 ────────────────────────────────────────────────────────

    /// <summary>
    /// 1 帧试编码。<b>用的是真跑时的那组质量参数</b>——这样「该编码器是否接受这些参数」
    /// 也会被一并验证，而不是等真正开跑时才炸。
    /// </summary>
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

    // ── 解析 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 得出本次要用的 ffmpeg H.264 编码器名。
    /// - 显式指定（Nvenc/Qsv/Amf/Cpu）：试编码验证，<b>不通过就报错，绝不静默回落</b>。
    /// - Auto：命中缓存则直接用；否则按「独显 &gt; 核显 &gt; CPU」逐个试，第一个跑通的胜出并写入缓存。
    /// </summary>
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

        // CandidateEncoders 一定以 libx264 结尾，理论到不了这里
        throw new H264UnavailableException("连 libx264 都无法运行，ffmpeg 可能损坏");
    }

    /// <summary>运行中遇到编码器初始化错误时清缓存，下次重探。</summary>
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
        return s.Length > 160 ? s[..160] + "…" : s;
    }
}
