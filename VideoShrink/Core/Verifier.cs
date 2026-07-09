using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace VideoShrink.Core;

public sealed record VerifyResult(bool Ok, string Reason, MediaInfo? Output)
{
    public static VerifyResult Fail(string reason, MediaInfo? o = null) => new(false, reason, o);
    public static VerifyResult Pass(MediaInfo o) => new(true, "", o);
}

/// <summary>
/// 成品验收。**所有事实一律来自 ffprobe / ffmpeg 的输出**，不依赖任何自写的容器解析。
/// 任一项不过 ⇒ 判失败 ⇒ 源文件原封不动（新建文件夹模式下源文件本就不会被碰）。
///
/// 之所以把「容器类型」与「色彩标签」也列为必检项：它们是仅有的两类
/// <b>能悄无声息通过其余全部验收</b>的损坏 —— 时长、帧率、解码、体积全对，
/// 只是文件其实是别的容器、或者播出来颜色不对。
/// </summary>
public static class Verifier
{
    public static async Task<VerifyResult> VerifyAsync(
        FfmpegRunner runner,
        MediaInfo src,
        string outPath,
        AppConfig cfg,
        EncodeContext ctx,
        bool allowOversize,
        CancellationToken ct)
    {
        var rule = ctx.Rule;

        // 1) ffprobe 必须能解析成品
        var outInfo = await Probe.RunAsync(runner, outPath, ct).ConfigureAwait(false);
        if (outInfo is null)
            return VerifyResult.Fail("ffprobe 无法解析输出文件（容器损坏或不可读）");

        if (outInfo.Video is null)
            return VerifyResult.Fail("输出没有视频流");

        // 2) 容器类型必须与扩展名相符。
        //    否则会出现「MP4 数据装进 .mkv」这种能正常解码、却是坏文件的静默毁片。
        if (!rule.FormatMatches(outInfo.FormatName))
            return VerifyResult.Fail(
                $"容器不符: {rule.Ext} 期望 format_name 含 \"{rule.FormatNameMatch}\"，" +
                $"实际为 \"{outInfo.FormatName}\"", outInfo);

        var sv = src.Video!;
        var ov = outInfo.Video;

        // 3) 编码正确
        string expect = EncoderArgs.ExpectedCodecName(cfg.Encoder);
        if (!string.Equals(ov.CodecName, expect, StringComparison.OrdinalIgnoreCase))
            return VerifyResult.Fail($"输出视频编码为 {ov.CodecName}，期望 {expect}", outInfo);

        // 4) 分辨率
        if (cfg.ResizeMode == ResizeMode.Stretch)
        {
            if (ov.Width != cfg.TargetWidth || ov.Height != cfg.TargetHeight)
                return VerifyResult.Fail(
                    $"拉伸模式下分辨率未精确命中: 期望 {cfg.TargetWidth}x{cfg.TargetHeight}，" +
                    $"实际 {ov.Width}x{ov.Height}", outInfo);
        }
        else
        {
            if (ov.Height != cfg.TargetHeight)
                return VerifyResult.Fail($"输出高度 {ov.Height}，期望 {cfg.TargetHeight}", outInfo);
            if (ov.Width <= 0 || ov.Width % 2 != 0)
                return VerifyResult.Fail($"输出宽度异常: {ov.Width}", outInfo);
        }

        // 5) 时长一致（±0.5% 且 ±1s 内）——防截断/丢帧。这是真正的完整性关口。
        if (src.Duration <= 0)
            return VerifyResult.Fail("原片时长未知，无法校验", outInfo);
        double durTol  = Math.Max(1.0, src.Duration * 0.005);
        double durDiff = Math.Abs(outInfo.Duration - src.Duration);
        if (durDiff > durTol)
            return VerifyResult.Fail(
                $"时长不符: 原 {src.Duration:F2}s vs 出 {outInfo.Duration:F2}s（差 {durDiff:F2}s > 容差 {durTol:F2}s）",
                outInfo);

        // 6) 帧率一致 —— 仅 CFR 源才比对。
        //    VFR 源（录屏、部分 mkv）转码后帧率必然对不上，严格比对会把好成品误判成失败。
        if (sv.IsVariableFrameRate)
            Log.Information("源为 VFR（avg {Avg} vs r {R}），跳过帧率比对", sv.AvgFrameRate, sv.RFrameRate);
        else if (sv.Fps > 0 && Math.Abs(ov.Fps - sv.Fps) > 0.01)
            return VerifyResult.Fail(
                $"帧率不符: 原 {sv.AvgFrameRate}({sv.Fps:F3}) vs 出 {ov.AvgFrameRate}({ov.Fps:F3})", outInfo);

        // 7) 色彩标签逐项一致（源片未标注的项自动跳过，不臆造）
        string? colorDiff = sv.Color.DiffAgainst(ov.Color);
        if (colorDiff is not null)
            return VerifyResult.Fail($"色彩标签不符 —— {colorDiff}", outInfo);

        // 8) 音频：逐流比对预期。直拷的必须编码一致；重编码的只比对声道/采样率。
        if (outInfo.Audios.Count != ctx.Audio.Streams.Count)
            return VerifyResult.Fail(
                $"音频流数量不符: 预期 {ctx.Audio.Streams.Count} vs 出 {outInfo.Audios.Count}", outInfo);

        foreach (var act in ctx.Audio.Streams)
        {
            var sa = src.Audios[act.OutIndex];
            var oa = outInfo.Audios[act.OutIndex];

            if (act.Action == AudioAction.Copy)
            {
                // 直拷 ⇒ 编码、声道、采样率必须一字不差
                if (!string.Equals(sa.CodecName, oa.CodecName, StringComparison.OrdinalIgnoreCase))
                    return VerifyResult.Fail(
                        $"音轨#{act.OutIndex} 编码不符（本应直拷）: {sa.CodecName} vs {oa.CodecName}", outInfo);
                if (sa.SampleRate != oa.SampleRate)
                    return VerifyResult.Fail(
                        $"音轨#{act.OutIndex} 采样率不符: {sa.SampleRate} vs {oa.SampleRate}", outInfo);
            }
            else
            {
                // 重编码 ⇒ 编码必然变；采样率也可能被迫改变
                //（libopus 只支持 48kHz，44.1kHz 源会被重采样）。这两项不该拿来判失败。
                if (oa.SampleRate <= 0)
                    return VerifyResult.Fail($"音轨#{act.OutIndex} 采样率无效", outInfo);
            }

            // 声道数无论直拷还是重编码都必须守住
            if (sa.Channels != oa.Channels)
                return VerifyResult.Fail($"音轨#{act.OutIndex} 声道不符: {sa.Channels} vs {oa.Channels}", outInfo);
        }

        // 9) 字幕：比对「预期保留的条数」，而非源条数（装不下的那几条是有意丢弃的）
        int expectSubs = ctx.Subtitles.KeptCount;
        if (outInfo.Subtitles.Count != expectSubs)
            return VerifyResult.Fail(
                $"字幕流数量不符: 预期保留 {expectSubs} vs 出 {outInfo.Subtitles.Count}", outInfo);

        // 10) 封面图
        if (ctx.KeepCoverArt && outInfo.CoverArts.Count == 0)
            return VerifyResult.Fail("预期保留封面图，成品里没有", outInfo);

        // 11) 体积
        if (outInfo.Size <= 0) return VerifyResult.Fail("ffprobe 未给出输出体积", outInfo);
        if (src.Size    <= 0) return VerifyResult.Fail("ffprobe 未给出原片体积", outInfo);

        // 下界默认关闭：压得远小于目标是内容好压的合法结果，不是异常。仅在用户显式设置时生效。
        if (cfg.MinOutRatio > 0 && (double)outInfo.Size / src.Size < cfg.MinOutRatio)
            return VerifyResult.Fail(
                $"输出体积过小（{(double)outInfo.Size / src.Size:P1} < {cfg.MinOutRatio:P0}），疑似编码异常", outInfo);

        long upper = BitrateCalc.UpperBoundBytes(src, cfg);
        if (!allowOversize && outInfo.Size > upper)
            return VerifyResult.Fail(
                $"输出体积超出上限: {outInfo.Size:N0} > {upper:N0} 字节" +
                (EncoderArgs.SupportsTwoPass(ctx.VideoEncoder)
                    ? ""
                    : "（硬件编码器只能单趟 VBR，体积精度较低）"), outInfo);

        // 12) 整片解码校验：查隐性损坏（-xerror 遇错即非零退出）
        if (cfg.FullDecodeCheck)
        {
            var r = await runner.RunAsync(
                AppPaths.FfmpegPath, EncoderArgs.BuildFullDecodeCheck(outPath), null, ct)
                .ConfigureAwait(false);

            if (!r.Ok)
                return VerifyResult.Fail($"整片解码校验失败 (exit={r.ExitCode}): {Trim(r.Stderr)}", outInfo);
            if (!string.IsNullOrWhiteSpace(r.Stderr))
                return VerifyResult.Fail($"整片解码报错: {Trim(r.Stderr)}", outInfo);
        }

        Log.Information("验收通过: {Out}（{Bytes:N0} 字节，上界 {Upper:N0}）", outPath, outInfo.Size, upper);
        return VerifyResult.Pass(outInfo);
    }

    private static string Trim(string s)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length > 300 ? s[..300] + "…" : s;
    }
}
