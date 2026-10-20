using System;
using System.IO;
using System.IO.Hashing;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace VideoShrink.Core;

/// <summary>
/// 文件指纹。用于回答一个问题：<b>「这个文件是不是我们自己产出的成品？」</b>
///
/// 用非加密的 XxHash64：我们防的是「误把成品当源片再压一次」，不是防篡改，
/// 不需要抗碰撞的密码学强度，需要的是快。
///
/// <b>调用策略同样重要</b>：正常扫描时一次哈希都不该算。
/// 先用 <c>(文件名, 体积)</c> 做极廉价的预筛，只有撞上已登记的成品才去读文件；
/// 落在已知输出目录里的文件更是直接判定，连读都不读。
/// </summary>
public static class FileHash
{
    private const int BufferSize = 1 << 20;   // 1 MiB

    /// <summary>流式计算 XxHash64，返回 16 位十六进制。</summary>
    public static async Task<string> ComputeAsync(string path, CancellationToken ct)
    {
        var h = new XxHash64();
        var buf = new byte[BufferSize];

        await using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

        int n;
        while ((n = await fs.ReadAsync(buf.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
            h.Append(buf.AsSpan(0, n));

        return Convert.ToHexString(h.GetCurrentHash());
    }

    /// <summary>算不出来（文件被占用、权限不足）时返回空串，调用方按「无法确认」处理。</summary>
    public static async Task<string> TryComputeAsync(string path, CancellationToken ct)
    {
        try { return await ComputeAsync(path, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "计算哈希失败: {Path}", path);
            return "";
        }
    }

    public static long SizeOf(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return -1; }
    }
}
