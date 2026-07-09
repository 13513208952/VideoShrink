using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace VideoShrink.Core;

/// <summary>
/// 把已验收的成品从 cache 发布到目标路径，<b>文件名与扩展名逐字不变</b>
/// （外部工具与媒体库据此判定「这是同一个文件」，改名会让它们重新入库甚至重下）。
///
/// 两种落地形态，崩溃安全性不同，因此用<b>两种不同的临时后缀</b>加以区分：
///
/// <b>A. 目标不存在</b>（新建文件夹模式的常态）—— 后缀 <c>.publishing.tmp</c>
///   1. 复制到 <c>&lt;目标&gt;.publishing.tmp</c>，flush 落盘，核对长度
///   2. 改名为目标名
///   崩溃恢复：目标不存在 ⇒ 这份 tmp 必然是**未完成的复制**（源文件完好无损）⇒ 直接丢弃。
///
/// <b>B. 目标已存在、将被覆盖</b>（就地替换，或直接写入模式撞到同名）—— 后缀 <c>.transcoding.tmp</c>
///   1. 复制到 <c>&lt;目标&gt;.transcoding.tmp</c>，flush 落盘，核对长度
///   2. <b>确认长度无误之后</b>才删除目标
///   3. 改名为目标名
///   崩溃恢复：目标不存在而 tmp 存在 ⇒ 崩在第 2、3 步之间，tmp 必然完整 ⇒ 复验后补完改名。
///
/// 这个区分不是形式主义：A 形态下「目标不存在」是**常态**，若沿用 B 的恢复逻辑，
/// 一份复制到一半的 tmp 会被误当成完整成品改名就位。
/// </summary>
public static class Replacer
{
    /// <summary>覆盖既有目标时使用（恢复时可补完改名）。</summary>
    public const string TmpSuffix = ".transcoding.tmp";

    /// <summary>目标原本不存在时使用（恢复时一律丢弃）。</summary>
    public const string PublishTmpSuffix = ".publishing.tmp";

    public const string CorruptSuffix = ".corrupt";

    /// <summary>
    /// 把 cache 里已验收的成品发布到 <paramref name="destPath"/>。
    /// 目标已存在时：<paramref name="overwriteExisting"/> 为 false 则抛异常，为 true 则覆盖
    /// （就地替换即此路径：destPath == 源文件）。
    /// 失败一律抛异常，调用方保证源文件不被动。
    /// </summary>
    public static async Task PublishAsync(
        string producedPath, string destPath, bool overwriteExisting, CancellationToken ct)
    {
        bool destExists = File.Exists(destPath);
        if (destExists && !overwriteExisting)
            throw new IOException($"目标已存在且未允许覆盖：{destPath}");

        string tmp = destPath + (destExists ? TmpSuffix : PublishTmpSuffix);

        if (File.Exists(tmp))
        {
            try { File.Delete(tmp); }
            catch (Exception ex) { throw new IOException($"无法清理残留 tmp: {tmp}", ex); }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        // 1. 复制到目标卷并强制落盘
        await CopyWithFlushAsync(producedPath, tmp, ct).ConfigureAwait(false);

        // 2. 核对长度 —— 必须在删除任何既有文件之前完成
        long srcLen = new FileInfo(producedPath).Length;
        long tmpLen = new FileInfo(tmp).Length;
        if (srcLen != tmpLen || tmpLen == 0)
        {
            try { File.Delete(tmp); } catch { }
            throw new IOException($"复制到目标卷后长度不符: {srcLen} vs {tmpLen}");
        }

        // 3. 覆盖场景：确认无误后才删除既有目标
        if (destExists) File.Delete(destPath);

        // 4. 同卷改名（瞬时）
        File.Move(tmp, destPath);

        Log.Information("已发布: {Path} ({Bytes} 字节{Mode})",
            destPath, tmpLen, destExists ? "，覆盖了原有文件" : "");
    }

    private static async Task CopyWithFlushAsync(string src, string dst, CancellationToken ct)
    {
        const int BUF = 1 << 20;
        await using var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read,
                                           BUF, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (var o = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None,
                                            BUF, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await i.CopyToAsync(o, BUF, ct).ConfigureAwait(false);
            await o.FlushAsync(ct).ConfigureAwait(false);
            o.Flush(flushToDisk: true);   // 逼到物理盘，抗断电
        }
    }

    /// <summary>
    /// 启动恢复扫描。需覆盖<b>源目录与输出目录</b>两处。
    ///
    /// <c>.publishing.tmp</c>（目标原本不存在）：一律丢弃 —— 源文件完好，重跑即可。
    /// <c>.transcoding.tmp</c>（覆盖既有目标）：
    ///   目标仍在 ⇒ 成品未确认落地，丢弃 tmp
    ///   目标已无 ⇒ 崩在删除与改名之间，ffprobe 复验后补完改名
    ///   目标已无且 tmp 不可用 ⇒ 极端异常，改名为 .corrupt 保留证据并高声报警（绝不静默删除）
    /// </summary>
    public static async Task<List<string>> RecoverAsync(
        FfmpegRunner runner, IEnumerable<string> dirs, bool recursive, CancellationToken ct)
    {
        var msgs = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var opt  = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        foreach (var dir in dirs)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
            if (!seen.Add(Path.GetFullPath(dir))) continue;

            // A. 未完成的发布：源文件完好，直接丢弃
            foreach (var tmp in SafeGetFiles(dir, "*" + PublishTmpSuffix, opt))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    File.Delete(tmp);
                    msgs.Add($"恢复：丢弃未完成的成品 {Path.GetFileName(tmp)}（源文件未受影响）");
                }
                catch (Exception ex) { msgs.Add($"恢复：删除 {tmp} 失败: {ex.Message}"); }
            }

            // B. 覆盖过程中断
            foreach (var tmp in SafeGetFiles(dir, "*" + TmpSuffix, opt))
            {
                ct.ThrowIfCancellationRequested();
                string dest = tmp[..^TmpSuffix.Length];

                if (File.Exists(dest))
                {
                    try
                    {
                        File.Delete(tmp);
                        msgs.Add($"恢复：目标仍在，已丢弃未确认的临时成品 {Path.GetFileName(tmp)}");
                    }
                    catch (Exception ex) { msgs.Add($"恢复：删除 {tmp} 失败: {ex.Message}"); }
                    continue;
                }

                // 目标不在了 —— tmp 理应完整（长度核对通过后才删的目标）
                var info = await Probe.RunAsync(runner, tmp, ct).ConfigureAwait(false);
                if (info is not null && info.HasVideo && info.Duration > 0)
                {
                    try
                    {
                        File.Move(tmp, dest);
                        msgs.Add($"恢复：补完中断的改名 → {Path.GetFileName(dest)}");
                        Log.Warning("崩溃恢复：{Dest} 的改名已补完", dest);
                    }
                    catch (Exception ex) { msgs.Add($"恢复：改名 {tmp} 失败: {ex.Message}"); }
                }
                else
                {
                    string bad = dest + CorruptSuffix;
                    try { File.Move(tmp, bad); } catch { }
                    msgs.Add($"⚠ 恢复：{Path.GetFileName(dest)} 已丢失且临时成品不可用，已保留为 {Path.GetFileName(bad)}");
                    Log.Error("数据风险：{Dest} 缺失且 tmp 不可用，已保留为 {Bad}", dest, bad);
                }
            }
        }

        return msgs;
    }

    private static string[] SafeGetFiles(string dir, string pattern, SearchOption opt)
    {
        try { return Directory.GetFiles(dir, pattern, opt); }
        catch (Exception ex) { Log.Warning(ex, "恢复扫描 {Dir} 失败", dir); return Array.Empty<string>(); }
    }
}
