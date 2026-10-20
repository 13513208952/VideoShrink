using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace VideoShrink.Core;

/// <summary>
/// 色彩标签四件套。经 scale 滤镜 + 换编码器后极易丢失或被错误推断，
/// 而丢失后成品能悄无声息通过其余所有验收（时长/帧率/解码全对），只是<b>颜色不对</b>。
/// 故必须显式读出、显式传递、显式比对。空/unknown 表示源片未标注（此时不臆造）。
/// </summary>
public sealed record ColorMeta(string Primaries, string Transfer, string Space, string Range)
{
    public static readonly ColorMeta Empty = new("", "", "", "");

    /// <summary>ffprobe 用 "unknown"/"reserved" 表示没标注，等同于空。</summary>
    public static bool IsUnset(string s) =>
        string.IsNullOrEmpty(s) || s == "unknown" || s == "reserved";

    public bool HasAny => !IsUnset(Primaries) || !IsUnset(Transfer) || !IsUnset(Space) || !IsUnset(Range);

    /// <summary>
    /// 以「本实例 = 源片」为准逐项比对成品。返回 null 表示一致。
    ///
    /// 只在<b>源片</b>未标注时才跳过该项——不能要求编码器凭空造出源片没有的标签。
    /// 反过来，<b>源片有、成品没有 = 真实的数据丢失</b>，必须判为不符：
    /// 这类损坏能通过时长/帧率/解码/体积的全部检查，只是播出来颜色不对，而原片此时已被删除。
    /// </summary>
    public string? DiffAgainst(ColorMeta other)
    {
        foreach (var (name, a, b) in new[]
        {
            ("color_primaries", Primaries, other.Primaries),
            ("color_transfer",  Transfer,  other.Transfer),
            ("color_space",     Space,     other.Space),
            ("color_range",     Range,     other.Range),
        })
        {
            if (IsUnset(a)) continue;                       // 源片就没标注，无从要求
            if (IsUnset(b)) return $"{name}: 原 {a}，成品丢失了该标签";
            if (!string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                return $"{name}: 原 {a} vs 出 {b}";
        }
        return null;
    }
}

public sealed class StreamInfo
{
    public int    Index        { get; init; }
    public string CodecType    { get; init; } = "";
    public string CodecName    { get; init; } = "";
    public int    Width        { get; init; }
    public int    Height       { get; init; }
    public string AvgFrameRate { get; init; } = "";
    public string RFrameRate   { get; init; } = "";
    public long   BitRate      { get; init; }
    public int    SampleRate   { get; init; }
    public int    Channels     { get; init; }
    public ColorMeta Color     { get; init; } = ColorMeta.Empty;

    /// <summary>封面图（`disposition.attached_pic`）。它是「视频流」，但不是正片。</summary>
    public bool   AttachedPic  { get; init; }
    public bool   IsDefault    { get; init; }
    public bool   IsForced     { get; init; }

    /// <summary>`tags.language` / `tags.title`——多音轨多字幕时全靠它区分。</summary>
    public string Language     { get; init; } = "";
    public string Title        { get; init; } = "";

    /// <summary>给人看的标识，用于风险弹窗与日志。</summary>
    public string Describe()
    {
        var parts = new List<string> { CodecName };
        if (!string.IsNullOrEmpty(Language)) parts.Add(Language);
        if (!string.IsNullOrEmpty(Title))    parts.Add($"\"{Title}\"");
        if (Channels > 0)                    parts.Add($"{Channels}ch");
        return string.Join(" ", parts);
    }

    public double Fps  => ParseRational(AvgFrameRate);
    public double RFps => ParseRational(RFrameRate);

    /// <summary>
    /// 变帧率判定：avg 与 r 差得明显即视为 VFR。
    /// VFR 源转码后帧率必然对不上，严格比对会误判失败——只在 CFR 时比对帧率。
    /// </summary>
    public bool IsVariableFrameRate => Fps > 0 && RFps > 0 && Math.Abs(RFps - Fps) > 0.01;

    internal static double ParseRational(string r)
    {
        if (string.IsNullOrWhiteSpace(r)) return 0;
        int slash = r.IndexOf('/');
        if (slash < 0)
            return double.TryParse(r, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        if (double.TryParse(r.AsSpan(0, slash), NumberStyles.Float, CultureInfo.InvariantCulture, out var num) &&
            double.TryParse(r.AsSpan(slash + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var den) &&
            den != 0)
            return num / den;
        return 0;
    }
}

/// <summary>ffprobe 的一次完整探测结果。RawJson 原样入库做快照。</summary>
public sealed class MediaInfo
{
    public string RawJson  { get; init; } = "";
    public double Duration { get; init; }
    public long   Size     { get; init; }
    public long   BitRate  { get; init; }

    /// <summary>ffprobe 的 format.format_name（如 "matroska,webm" / "mov,mp4,m4a,..."）。</summary>
    public string FormatName { get; init; } = "";

    public StreamInfo?      Video     { get; init; }
    public List<StreamInfo> Audios    { get; init; } = new();
    public List<StreamInfo> Subtitles { get; init; } = new();

    /// <summary>除正片之外的视频流：封面图（attached_pic）与其它缩略图。</summary>
    public List<StreamInfo> ExtraVideos { get; init; } = new();

    /// <summary>封面图（可在 mp4/mkv 中原样保留）。</summary>
    public List<StreamInfo> CoverArts => ExtraVideos.FindAll(s => s.AttachedPic);

    public int ExtraVideoStreams => ExtraVideos.Count;

    public bool HasVideo => Video is not null;
}

/// <summary>ffprobe 调用与 JSON 解析。所有媒体事实一律来自 ffprobe，不手搓解析容器。</summary>
public static class Probe
{
    public static async Task<MediaInfo?> RunAsync(FfmpegRunner runner, string path, CancellationToken ct)
    {
        var args = new[]
        {
            "-v", "quiet",
            "-print_format", "json",
            "-show_format",
            "-show_streams",
            path,
        };

        ProcResult r;
        try
        {
            r = await runner.RunAsync(AppPaths.FfprobePath, args, null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "ffprobe 启动失败: {Path}", path);
            return null;
        }

        if (!r.Ok || string.IsNullOrWhiteSpace(r.Stdout))
        {
            Log.Warning("ffprobe 失败 exit={Exit}: {Path}", r.ExitCode, path);
            return null;
        }

        try { return Parse(r.Stdout); }
        catch (Exception ex)
        {
            Log.Warning(ex, "ffprobe JSON 解析失败: {Path}", path);
            return null;
        }
    }

    internal static MediaInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        double duration = 0;
        long size = 0, bitrate = 0;
        string formatName = "";
        if (root.TryGetProperty("format", out var fmt))
        {
            duration   = GetDouble(fmt, "duration");
            size       = GetLong(fmt, "size");
            bitrate    = GetLong(fmt, "bit_rate");
            formatName = GetString(fmt, "format_name");
        }

        StreamInfo? video = null;
        var extraVideos = new List<StreamInfo>();
        var audios = new List<StreamInfo>();
        var subs   = new List<StreamInfo>();

        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in streams.EnumerateArray())
            {
                string type = GetString(s, "codec_type");
                var si = new StreamInfo
                {
                    Index        = (int)GetLong(s, "index"),
                    CodecType    = type,
                    CodecName    = GetString(s, "codec_name"),
                    Width        = (int)GetLong(s, "width"),
                    Height       = (int)GetLong(s, "height"),
                    AvgFrameRate = GetString(s, "avg_frame_rate"),
                    RFrameRate   = GetString(s, "r_frame_rate"),
                    BitRate      = GetLong(s, "bit_rate"),
                    SampleRate   = (int)GetLong(s, "sample_rate"),
                    Channels     = (int)GetLong(s, "channels"),
                    Color        = new ColorMeta(
                        GetString(s, "color_primaries"),
                        GetString(s, "color_transfer"),
                        GetString(s, "color_space"),
                        GetString(s, "color_range")),
                    AttachedPic  = GetDisposition(s, "attached_pic"),
                    IsDefault    = GetDisposition(s, "default"),
                    IsForced     = GetDisposition(s, "forced"),
                    Language     = GetTag(s, "language"),
                    Title        = GetTag(s, "title"),
                };

                switch (type)
                {
                    // 封面图也是 codec_type=video，绝不能被当成正片
                    case "video" when si.AttachedPic: extraVideos.Add(si); break;
                    case "video" when video is null:  video = si;          break;
                    case "video":                     extraVideos.Add(si); break;
                    case "audio":                     audios.Add(si);      break;
                    case "subtitle":                  subs.Add(si);        break;
                }
            }
        }

        // format.duration 缺失时退回视频流时长
        if (duration <= 0 && video is not null &&
            root.TryGetProperty("streams", out var st2) && st2.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in st2.EnumerateArray())
                if (GetString(s, "codec_type") == "video")
                {
                    duration = GetDouble(s, "duration");
                    break;
                }
        }

        return new MediaInfo
        {
            RawJson     = json,
            Duration    = duration,
            Size        = size,
            BitRate     = bitrate,
            FormatName  = formatName,
            Video       = video,
            Audios      = audios,
            Subtitles   = subs,
            ExtraVideos = extraVideos,
        };
    }

    /// <summary>`disposition.<name>` == 1</summary>
    private static bool GetDisposition(JsonElement stream, string name) =>
        stream.TryGetProperty("disposition", out var d) &&
        d.ValueKind == JsonValueKind.Object &&
        d.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.Number &&
        v.TryGetInt32(out int n) && n != 0;

    /// <summary>`tags.<name>`（大小写不敏感：ffprobe 有时给 LANGUAGE）</summary>
    private static string GetTag(JsonElement stream, string name)
    {
        if (!stream.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object)
            return "";
        foreach (var p in tags.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) &&
                p.Value.ValueKind == JsonValueKind.String)
                return p.Value.GetString() ?? "";
        return "";
    }

    // ffprobe 的数值字段有的是 JSON number，有的是字符串（还可能是 "N/A"）——统一兜住
    private static string GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    private static long GetLong(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.TryGetInt64(out var n) ? n : 0;
        if (v.ValueKind == JsonValueKind.String)
            return long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0;
        return 0;
    }

    private static double GetDouble(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.TryGetDouble(out var n) ? n : 0;
        if (v.ValueKind == JsonValueKind.String)
            return double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 0;
        return 0;
    }
}
