using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VideoShrink.Data;
using Serilog;

namespace VideoShrink.Core;

/// <summary>某个候选文件为什么被认定为「我们上一轮的成品」。</summary>
public enum ReprocessReason
{
    /// <summary>它位于历史上用过的输出目录里。</summary>
    InsideOutputDir,
    /// <summary>同名同体积，且 XxHash64 与已登记的成品完全一致。</summary>
    HashMatch,
}

public sealed record ProcessedHit(string Path, ReprocessReason Reason, string Detail)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>用户对「发现已处理文件」的三选一。</summary>
public enum ReprocessDecision
{
    /// <summary>放弃本次操作（推荐，默认）—— 一个文件都不处理。</summary>
    Abort,
    /// <summary>排除所有已处理文件，继续处理其余。</summary>
    ExcludeProcessed,
    /// <summary>全部包含，重新处理它们（二次压缩，画质不可逆损失）。</summary>
    IncludeAll,
}

public sealed class ReprocessReport
{
    public List<ProcessedHit> Hits { get; } = new();
    public int TotalCandidates { get; init; }

    public bool HasAny => Hits.Count > 0;
    public int ByDir  => Hits.Count(h => h.Reason == ReprocessReason.InsideOutputDir);
    public int ByHash => Hits.Count(h => h.Reason == ReprocessReason.HashMatch);

    public string BuildSummary()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"本次待处理 {TotalCandidates} 个文件，其中 {Hits.Count} 个是本程序此前产出的成品。");
        sb.AppendLine("再压一次是不可逆的画质损失。");
        sb.AppendLine();
        if (ByDir > 0)  sb.AppendLine($"- {ByDir} 个位于历史输出目录内（多半是递归扫描把输出文件夹一起吃进来了）");
        if (ByHash > 0) sb.AppendLine($"- {ByHash} 个同名同体积且哈希完全一致");
        sb.AppendLine();
        foreach (var h in Hits)
            sb.AppendLine($"文件：{h.FileName}\n    {h.Detail}");
        return sb.ToString();
    }
}

/// <summary>
/// 防止「把自己的成品当源片再压一次」。
///
/// 为什么单靠记录库的 <c>records.file_path</c> 不够：
/// - <b>覆盖模式写回源目录</b>时，成品与源片是同一个路径 —— 记录库一旦丢失或被重置，
///   下一轮就会原地二次压缩。
/// - <b>递归扫描</b>会把上一轮新建的输出文件夹一起吃进来，那些成品的路径此前从未作为源片记录过。
/// - 成品被移动/改名后，路径索引彻底失效。
///
/// 故用两条独立通道确认，两者<b>在覆盖模式与新建文件夹模式下一视同仁</b>：
/// 1. <b>输出目录历史</b> —— 落在历史 DestDir 里的，直接判定，连文件都不读。
/// 2. <b>成品指纹</b> —— 先用 (文件名, 体积) 廉价预筛，命中了才去算 XxHash64 比对。
///
/// 正常扫描时两条通道都不会读文件内容，代价接近于零。
/// </summary>
public static class ReprocessGuard
{
    public static async Task<ReprocessReport> CheckAsync(
        RecordDb db, IReadOnlyList<string> candidates, CancellationToken ct)
    {
        var report = new ReprocessReport { TotalCandidates = candidates.Count };

        var outputDirs = db.GetOutputDirs();
        int hashed = 0;

        foreach (var f in candidates)
        {
            ct.ThrowIfCancellationRequested();

            // 通道 1：落在历史输出目录内 —— 不读文件
            string? dir = outputDirs.FirstOrDefault(d => OutputPlan.IsUnder(f, d));
            if (dir is not null)
            {
                report.Hits.Add(new ProcessedHit(f, ReprocessReason.InsideOutputDir,
                    $"位于历史输出目录：{dir}"));
                continue;
            }

            // 通道 2：(文件名, 体积) 预筛 —— 没命中就不算哈希
            long size = FileHash.SizeOf(f);
            if (size <= 0) continue;

            var known = db.OutputHashesFor(Path.GetFileName(f), size);
            if (known.Count == 0) continue;

            string hash = await FileHash.TryComputeAsync(f, ct).ConfigureAwait(false);
            hashed++;
            if (hash.Length == 0 || !known.Contains(hash)) continue;

            string? src = db.SourceOfOutput(Path.GetFileName(f), size, hash);
            report.Hits.Add(new ProcessedHit(f, ReprocessReason.HashMatch,
                $"哈希与此前的成品完全一致（XxHash64 {hash}）" +
                (string.IsNullOrEmpty(src) ? "" : $"，源自 {src}")));
        }

        if (report.HasAny)
            Log.Warning("重复处理防护：{N}/{T} 个候选是此前的成品（目录命中 {D}，哈希命中 {H}；实际算了 {X} 次哈希）",
                report.Hits.Count, candidates.Count, report.ByDir, report.ByHash, hashed);

        return report;
    }

    /// <summary>按用户的决定裁剪待处理队列。<c>Abort</c> 时返回空列表。</summary>
    public static List<string> Apply(
        IReadOnlyList<string> candidates, ReprocessReport report, ReprocessDecision decision)
    {
        switch (decision)
        {
            case ReprocessDecision.Abort:
                return new List<string>();

            case ReprocessDecision.ExcludeProcessed:
                var drop = report.Hits.Select(h => h.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                return candidates.Where(c => !drop.Contains(c)).ToList();

            default:
                return candidates.ToList();
        }
    }
}
