using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace VideoShrink.Core;

/// <summary>
/// 用已验收的成品替换原片，并<b>保持文件名逐字不差</b>
/// （包括扩展名：外部工具与媒体库据此判定「这是同一个文件」，改名会让它们重新入库甚至重下）。
///
/// 替换序列——任何一步崩溃，原片或成品至少有一份完整：
///   1. 成品从 cache 复制到目标目录，命名 &lt;原名&gt;.transcoding.tmp（落到目标卷）
///   2. flush 到盘 + 长度核对
///   3. 删除原片
///   4. tmp 重命名为原名（同卷改名，瞬时）
/// </summary>
public static class Replacer
{
    public const string TmpSuffix     = ".transcoding.tmp";
    public const string CorruptSuffix = ".corrupt";

    /// <summary>把 cache 里的成品替换到 originalPath。失败会抛异常（调用方保证原片不被删）。</summary>
    public static async Task ReplaceAsync(string originalPath, string producedPath, CancellationToken ct)
    {
        string tmp = originalPath + TmpSuffix;

        if (File.Exists(tmp))
        {
            try { File.Delete(tmp); }
            catch (Exception ex) { throw new IOException($"无法清理残留 tmp: {tmp}", ex); }
        }

        // 1+2. 复制到目标卷并强制落盘，再核对长度
        await CopyWithFlushAsync(producedPath, tmp, ct).ConfigureAwait(false);

        long srcLen = new FileInfo(producedPath).Length;
        long tmpLen = new FileInfo(tmp).Length;
        if (srcLen != tmpLen || tmpLen == 0)
        {
            try { File.Delete(tmp); } catch { }
            throw new IOException($"复制到目标卷后长度不符: {srcLen} vs {tmpLen}");
        }

        // 3. 删原片（此前 tmp 已是完整且验收过的成品）
        File.Delete(originalPath);

        // 4. 同卷改名 → 恢复原文件名
        File.Move(tmp, originalPath);

        Log.Information("已替换: {Path} ({Bytes} 字节)", originalPath, tmpLen);
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
    /// 启动恢复扫描：处理上次崩溃留下的 *.transcoding.tmp。
    ///   原片仍在 ⇒ 成品未确认落地，丢弃 tmp
    ///   原片已无 ⇒ 崩在「删原片」与「改名」之间；ffprobe 复验 tmp 后完成改名
    ///   原片已无且 tmp 不可用 ⇒ 极端异常，改名为 .corrupt 保留证据并高声报警（绝不静默删除）
    /// </summary>
    public static async Task<List<string>> RecoverAsync(
        FfmpegRunner runner, string dir, bool recursive, CancellationToken ct)
    {
        var msgs = new List<string>();
        if (!Directory.Exists(dir)) return msgs;

        var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        string[] leftovers;
        try { leftovers = Directory.GetFiles(dir, "*" + TmpSuffix, opt); }
        catch (Exception ex) { Log.Warning(ex, "恢复扫描失败"); return msgs; }

        foreach (var tmp in leftovers)
        {
            ct.ThrowIfCancellationRequested();
            string orig = tmp[..^TmpSuffix.Length];

            if (File.Exists(orig))
            {
                try
                {
                    File.Delete(tmp);
                    msgs.Add($"恢复：原片仍在，已丢弃未确认的临时成品 {Path.GetFileName(tmp)}");
                }
                catch (Exception ex) { msgs.Add($"恢复：删除 {tmp} 失败: {ex.Message}"); }
                continue;
            }

            // 原片不在了 —— tmp 理应是完整成品（它是复制完成后才删的原片）
            var info = await Probe.RunAsync(runner, tmp, ct).ConfigureAwait(false);
            if (info is not null && info.HasVideo && info.Duration > 0)
            {
                try
                {
                    File.Move(tmp, orig);
                    msgs.Add($"恢复：补完中断的改名 → {Path.GetFileName(orig)}");
                    Log.Warning("崩溃恢复：{Orig} 的改名已补完", orig);
                }
                catch (Exception ex) { msgs.Add($"恢复：改名 {tmp} 失败: {ex.Message}"); }
            }
            else
            {
                string bad = orig + CorruptSuffix;
                try { File.Move(tmp, bad); } catch { }
                msgs.Add($"⚠ 恢复：{Path.GetFileName(orig)} 原片已丢失且临时成品不可用，已保留为 {Path.GetFileName(bad)}");
                Log.Error("数据风险：{Orig} 原片缺失且 tmp 不可用，已保留为 {Bad}", orig, bad);
            }
        }

        return msgs;
    }
}
