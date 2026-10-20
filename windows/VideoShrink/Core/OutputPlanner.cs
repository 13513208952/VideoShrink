using System;
using System.IO;
using Serilog;

namespace VideoShrink.Core;

/// <summary>输出目录准备失败。调用方必须据此<b>立即终止整批</b>，一个文件都不处理。</summary>
public sealed class OutputSetupException : Exception
{
    public OutputSetupException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// 一次运行的输出落点计划。
///
/// 3 种路径模式 x 2 种保存模式 = 6 种组合：
/// <code>
///                 NewFolder(默认)                        DirectOverwrite
/// SourceDir     源目录/&lt;源名&gt;_shrink_output/       写回源目录 => 就地替换原文件
/// FixedPath     指定路径/&lt;源名&gt;_shrink_output/     直接写入指定路径，同名即覆盖
/// AskEveryTime  每次询问路径，然后同上                  每次询问路径，然后同上
/// </code>
/// </summary>
public sealed class OutputPlan
{
    /// <summary>被扫描的源文件夹。</summary>
    public string SourceDir { get; }

    /// <summary>成品最终落到的目录（`NewFolder` 时即新建的那个）。</summary>
    public string DestDir { get; }

    public SaveMode SaveMode { get; }

    /// <summary>
    /// 扫描时必须排除的目录（输出目录落在源目录内部时）。
    /// 不排除的话，第二轮会把上一轮的成品当输入吃进去。
    /// </summary>
    public string? ExcludedDir { get; }

    private OutputPlan(string sourceDir, string destDir, SaveMode saveMode, string? excludedDir)
    {
        SourceDir   = sourceDir;
        DestDir     = destDir;
        SaveMode    = saveMode;
        ExcludedDir = excludedDir;
    }

    /// <summary>
    /// 该不该把 <see cref="DestDir"/> 记进「输出目录历史」。
    ///
    /// 覆盖模式写回源目录时 <c>DestDir == SourceDir</c>，登记它会让下一轮把
    /// <b>源目录里的每个文件</b>都判成「位于输出目录内」。那种情形只能靠成品指纹去认。
    /// </summary>
    public bool ShouldTrackDestDir => !PathEquals(DestDir, SourceDir);

    /// <summary>成品是否会覆盖掉源文件本身（就地替换）。</summary>
    public bool IsInPlace(string srcFile) =>
        string.Equals(Path.GetFullPath(DestPathFor(srcFile)),
                      Path.GetFullPath(srcFile),
                      StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 某个源文件的成品落点。文件名与扩展名<b>逐字不变</b>；
    /// 递归模式下在输出目录里镜像源的相对子目录结构。
    /// </summary>
    public string DestPathFor(string srcFile)
    {
        string rel = Path.GetRelativePath(SourceDir, srcFile);
        // 非递归时 rel 就是文件名；递归时含子目录
        return Path.GetFullPath(Path.Combine(DestDir, rel));
    }

    /// <summary>为某个源文件准备好其成品所在的子目录（递归模式下才可能需要新建）。</summary>
    public void EnsureDestSubdir(string srcFile)
    {
        string dir = Path.GetDirectoryName(DestPathFor(srcFile))!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
    }

    // -- 构建 ----------------------------------------------------------

    /// <summary>
    /// 解析出「目标基准路径」。<c>AskEveryTime</c> 时调用 <paramref name="askUser"/>
    /// （由界面提供目录选择框）；用户取消则返回 null，调用方应放弃本次运行。
    /// </summary>
    public static string? ResolveBaseDir(AppConfig cfg, string sourceDir, Func<string?> askUser) =>
        cfg.OutputLocation switch
        {
            OutputLocation.SourceDir    => sourceDir,
            OutputLocation.FixedPath    => string.IsNullOrWhiteSpace(cfg.FixedOutputPath)
                                              ? throw new OutputSetupException("未设置固定输出路径")
                                              : cfg.FixedOutputPath,
            OutputLocation.AskEveryTime => askUser(),
            _ => sourceDir,
        };

    /// <summary>
    /// 落实输出目录。<c>NewFolder</c> 模式会<b>在转换开始前</b>创建文件夹；
    /// 创建失败即抛 <see cref="OutputSetupException"/>，调用方必须立即终止整批。
    /// </summary>
    public static OutputPlan Prepare(AppConfig cfg, string sourceDir, string baseDir)
    {
        sourceDir = Path.GetFullPath(sourceDir);
        if (!Directory.Exists(sourceDir))
            throw new OutputSetupException($"源文件夹不存在：{sourceDir}");

        baseDir = Path.GetFullPath(baseDir);

        if (cfg.SaveMode == SaveMode.DirectOverwrite)
        {
            if (!Directory.Exists(baseDir))
                throw new OutputSetupException($"输出路径不存在：{baseDir}");

            // 直接写入 baseDir；若 baseDir == sourceDir，则是就地替换
            string? excluded = IsUnder(baseDir, sourceDir) && !PathEquals(baseDir, sourceDir)
                ? baseDir : null;
            Log.Information("输出模式：直接写入 {Dir}（同名覆盖）", baseDir);
            return new OutputPlan(sourceDir, baseDir, cfg.SaveMode, excluded);
        }

        // NewFolder：在 baseDir 下新建 <源文件夹名>_shrink_output
        string folderName = new DirectoryInfo(sourceDir).Name + AppConfig.OutputFolderSuffix;
        string destDir    = Path.GetFullPath(Path.Combine(baseDir, folderName));

        if (PathEquals(destDir, sourceDir))
            throw new OutputSetupException($"输出文件夹与源文件夹相同：{destDir}");

        try
        {
            Directory.CreateDirectory(destDir);
            // 真正写一下，确认有写权限（只读盘/无权限目录上 CreateDirectory 可能不报错）
            string probe = Path.Combine(destDir, ".vsw_write_probe");
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            throw new OutputSetupException($"无法创建或写入输出文件夹：{destDir}（{ex.Message}）", ex);
        }

        // 输出目录若在源目录内部，扫描时必须排除它
        string? exclude = IsUnder(destDir, sourceDir) ? destDir : null;

        Log.Information("输出模式：新建文件夹 {Dir}（原文件不动）", destDir);
        return new OutputPlan(sourceDir, destDir, cfg.SaveMode, exclude);
    }

    // -- 路径工具 ------------------------------------------------------
    private static bool PathEquals(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                      Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                      StringComparison.OrdinalIgnoreCase);

    /// <summary>child 是否位于 parent 之下（含相等）。</summary>
    internal static bool IsUnder(string child, string parent)
    {
        string c = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar);
        string p = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(c, p, StringComparison.OrdinalIgnoreCase)) return true;
        return c.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>扫描时是否应跳过这个文件（它落在被排除的输出目录里）。</summary>
    public bool IsExcluded(string path) =>
        ExcludedDir is not null && IsUnder(path, ExcludedDir);
}
