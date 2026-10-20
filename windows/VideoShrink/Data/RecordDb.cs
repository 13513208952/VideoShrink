using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;
using Serilog;

namespace VideoShrink.Data;

public static class RecordStatus
{
    public const string Success = "success";
    public const string Failed  = "failed";
    public const string Skipped = "skipped";
}

public sealed class RecordRow
{
    public string FilePath      { get; set; } = "";
    public string FileName      { get; set; } = "";
    /// <summary>成品的落点。新建文件夹模式下与源路径不同；就地替换时与源路径相同。</summary>
    public string DestPath      { get; set; } = "";
    public long   OrigSize      { get; set; }
    public int    OrigWidth     { get; set; }
    public int    OrigHeight    { get; set; }
    public string OrigCodec     { get; set; } = "";
    public double OrigDuration  { get; set; }
    public long   OrigBitrate   { get; set; }
    public long   TargetBitrate { get; set; }
    public long   OutSize       { get; set; }
    public double Ratio         { get; set; }
    public string Status        { get; set; } = "";
    public string FailReason    { get; set; } = "";
    /// <summary>有损但可接受的处理留痕：音频重编码、字幕/附加流丢弃等。原片会被删，必须记账。</summary>
    public string Notes         { get; set; } = "";
    public long   StartedAt     { get; set; }
    public long   FinishedAt    { get; set; }
    public string ProbeBefore   { get; set; } = "";
    public string ProbeAfter    { get; set; } = "";

    public long SavedBytes => Status == RecordStatus.Success && OrigSize > OutSize ? OrigSize - OutSize : 0;
}

/// <summary>程序目录下的完整转换记录（SQLite）。失败即落库并跳过，不中断整批。</summary>
public sealed class RecordDb : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _lock = new();

    public RecordDb(string dbPath)
    {
        var b = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };
        _conn = new SqliteConnection(b.ToString());
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL");
        Exec("PRAGMA busy_timeout=10000");
        Exec(@"
            CREATE TABLE IF NOT EXISTS records (
                file_path      TEXT PRIMARY KEY,
                file_name      TEXT    DEFAULT '',
                orig_size      INTEGER DEFAULT 0,
                orig_w         INTEGER DEFAULT 0,
                orig_h         INTEGER DEFAULT 0,
                orig_codec     TEXT    DEFAULT '',
                orig_duration  REAL    DEFAULT 0,
                orig_bitrate   INTEGER DEFAULT 0,
                target_bitrate INTEGER DEFAULT 0,
                out_size       INTEGER DEFAULT 0,
                ratio          REAL    DEFAULT 0,
                status         TEXT    NOT NULL DEFAULT '',
                fail_reason    TEXT    DEFAULT '',
                started_at     INTEGER DEFAULT 0,
                finished_at    INTEGER DEFAULT 0,
                probe_before   TEXT    DEFAULT '',
                probe_after    TEXT    DEFAULT '',
                notes          TEXT    DEFAULT '',
                dest_path      TEXT    DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS idx_status ON records(status);

            -- 我们自己产出过的成品。用于回答「这个文件是不是上一轮的输出？」
            -- (file_name,size) 是廉价预筛索引；只有它命中了才去算 hash。
            CREATE TABLE IF NOT EXISTS outputs (
                dest_path   TEXT PRIMARY KEY,
                file_name   TEXT    NOT NULL DEFAULT '',
                size        INTEGER NOT NULL DEFAULT 0,
                hash        TEXT    NOT NULL DEFAULT '',
                source_path TEXT    DEFAULT '',
                finished_at INTEGER DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_out_name_size ON outputs(file_name, size);

            -- 历史上用过的输出目录。递归扫描时若把它吃进来，即便哈希对不上也要示警。
            CREATE TABLE IF NOT EXISTS output_dirs (
                dir        TEXT PRIMARY KEY,
                first_used INTEGER DEFAULT 0
            );
        ");

        // 迁移：老库补 notes 列
        var cols = new HashSet<string>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(records)";
            using var r = cmd.ExecuteReader();
            while (r.Read()) cols.Add(r.GetString(1));
        }
        foreach (var (col, def) in new[]
        {
            ("notes",     "notes     TEXT DEFAULT ''"),
            ("dest_path", "dest_path TEXT DEFAULT ''"),
        })
        {
            if (cols.Contains(col)) continue;
            Exec($"ALTER TABLE records ADD COLUMN {def}");
            Log.Information("记录库迁移：新增列 {Col}", col);
        }
    }

    public void Upsert(RecordRow r)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO records
                  (file_path,file_name,orig_size,orig_w,orig_h,orig_codec,orig_duration,orig_bitrate,
                   target_bitrate,out_size,ratio,status,fail_reason,notes,dest_path,started_at,finished_at,
                   probe_before,probe_after)
                VALUES
                  ($p,$n,$os,$ow,$oh,$oc,$od,$ob,$tb,$outs,$ratio,$st,$fr,$notes,$dest,$sa,$fa,$pb,$pa)
                ON CONFLICT(file_path) DO UPDATE SET
                  file_name=$n, orig_size=$os, orig_w=$ow, orig_h=$oh, orig_codec=$oc,
                  orig_duration=$od, orig_bitrate=$ob, target_bitrate=$tb, out_size=$outs,
                  ratio=$ratio, status=$st, fail_reason=$fr, notes=$notes, dest_path=$dest, started_at=$sa, finished_at=$fa,
                  probe_before=$pb, probe_after=$pa";
            cmd.Parameters.AddWithValue("$p",     r.FilePath);
            cmd.Parameters.AddWithValue("$n",     r.FileName);
            cmd.Parameters.AddWithValue("$os",    r.OrigSize);
            cmd.Parameters.AddWithValue("$ow",    r.OrigWidth);
            cmd.Parameters.AddWithValue("$oh",    r.OrigHeight);
            cmd.Parameters.AddWithValue("$oc",    r.OrigCodec);
            cmd.Parameters.AddWithValue("$od",    r.OrigDuration);
            cmd.Parameters.AddWithValue("$ob",    r.OrigBitrate);
            cmd.Parameters.AddWithValue("$tb",    r.TargetBitrate);
            cmd.Parameters.AddWithValue("$outs",  r.OutSize);
            cmd.Parameters.AddWithValue("$ratio", r.Ratio);
            cmd.Parameters.AddWithValue("$st",    r.Status);
            cmd.Parameters.AddWithValue("$fr",    r.FailReason);
            cmd.Parameters.AddWithValue("$notes", r.Notes);
            cmd.Parameters.AddWithValue("$dest",  r.DestPath);
            cmd.Parameters.AddWithValue("$sa",    r.StartedAt);
            cmd.Parameters.AddWithValue("$fa",    r.FinishedAt);
            cmd.Parameters.AddWithValue("$pb",    r.ProbeBefore);
            cmd.Parameters.AddWithValue("$pa",    r.ProbeAfter);
            cmd.ExecuteNonQuery();
        }
    }

    public string? GetStatus(string filePath)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT status FROM records WHERE file_path=$p";
            cmd.Parameters.AddWithValue("$p", filePath);
            return cmd.ExecuteScalar() as string;
        }
    }

    public Dictionary<string, int> CountByStatus()
    {
        lock (_lock)
        {
            var d = new Dictionary<string, int>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT status, COUNT(*) FROM records GROUP BY status";
            using var r = cmd.ExecuteReader();
            while (r.Read()) d[r.GetString(0)] = r.GetInt32(1);
            return d;
        }
    }

    /// <summary>累计已节省字节（仅统计成功项）。</summary>
    public long TotalSavedBytes()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "SELECT COALESCE(SUM(orig_size - out_size),0) FROM records " +
                "WHERE status=$s AND orig_size > out_size";
            cmd.Parameters.AddWithValue("$s", RecordStatus.Success);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }

    /// <summary>记录表视图（不带 probe JSON，避免把界面撑爆）。</summary>
    public List<RecordRow> GetAll(int limit = 5000)
    {
        lock (_lock)
        {
            var list = new List<RecordRow>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "SELECT file_path,file_name,orig_size,orig_w,orig_h,orig_codec,orig_duration," +
                "orig_bitrate,target_bitrate,out_size,ratio,status,fail_reason,started_at,finished_at," +
                "notes,dest_path FROM records ORDER BY finished_at DESC LIMIT $l";
            cmd.Parameters.AddWithValue("$l", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new RecordRow
                {
                    FilePath      = r.GetString(0),
                    FileName      = r.GetString(1),
                    OrigSize      = r.GetInt64(2),
                    OrigWidth     = r.GetInt32(3),
                    OrigHeight    = r.GetInt32(4),
                    OrigCodec     = r.GetString(5),
                    OrigDuration  = r.GetDouble(6),
                    OrigBitrate   = r.GetInt64(7),
                    TargetBitrate = r.GetInt64(8),
                    OutSize       = r.GetInt64(9),
                    Ratio         = r.GetDouble(10),
                    Status        = r.GetString(11),
                    FailReason    = r.GetString(12),
                    StartedAt     = r.GetInt64(13),
                    FinishedAt    = r.GetInt64(14),
                    Notes         = r.IsDBNull(15) ? "" : r.GetString(15),
                    DestPath      = r.IsDBNull(16) ? "" : r.GetString(16),
                });
            }
            return list;
        }
    }

    // -- 已处理成品 与 输出目录历史 ------------------------------------
    // 覆盖模式写回源目录时，成品与源片是同一个路径；一旦记录库丢失、或递归扫描
    // 把上一轮的输出文件夹吃进来，程序就会把自己的成品当源片再压一次 —— 而二次压缩
    // 的画质损失不可逆。故除了按源路径记状态，还要单独登记「我们产出过什么」。

    public void AddOutput(string destPath, long size, string hash, string sourcePath)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO outputs(dest_path,file_name,size,hash,source_path,finished_at)
                VALUES($d,$n,$s,$h,$src,$t)
                ON CONFLICT(dest_path) DO UPDATE SET
                  file_name=$n, size=$s, hash=$h, source_path=$src, finished_at=$t";
            cmd.Parameters.AddWithValue("$d",   destPath);
            cmd.Parameters.AddWithValue("$n",   System.IO.Path.GetFileName(destPath));
            cmd.Parameters.AddWithValue("$s",   size);
            cmd.Parameters.AddWithValue("$h",   hash);
            cmd.Parameters.AddWithValue("$src", sourcePath);
            cmd.Parameters.AddWithValue("$t",   DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>廉价预筛：同名同体积的成品有哪些哈希。空 => 不必读文件算哈希。</summary>
    public List<string> OutputHashesFor(string fileName, long size)
    {
        lock (_lock)
        {
            var list = new List<string>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT hash FROM outputs WHERE file_name=$n AND size=$s AND hash<>''";
            cmd.Parameters.AddWithValue("$n", fileName);
            cmd.Parameters.AddWithValue("$s", size);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }
    }

    /// <summary>某个成品的源文件是谁（用于在警告里说明来历）。</summary>
    public string? SourceOfOutput(string fileName, long size, string hash)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "SELECT source_path FROM outputs WHERE file_name=$n AND size=$s AND hash=$h LIMIT 1";
            cmd.Parameters.AddWithValue("$n", fileName);
            cmd.Parameters.AddWithValue("$s", size);
            cmd.Parameters.AddWithValue("$h", hash);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void AddOutputDir(string dir)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO output_dirs(dir,first_used) VALUES($d,$t) ON CONFLICT(dir) DO NOTHING";
            cmd.Parameters.AddWithValue("$d", dir);
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.ExecuteNonQuery();
        }
    }

    public List<string> GetOutputDirs()
    {
        lock (_lock)
        {
            var list = new List<string>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT dir FROM output_dirs";
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }
    }

    public void ExportCsv(string path)
    {
        var rows = GetAll(int.MaxValue);
        var sb = new StringBuilder();
        sb.AppendLine("file_name,file_path,status,orig_size,out_size,ratio,orig_w,orig_h,orig_codec," +
                      "orig_duration,orig_bitrate,target_bitrate,started_at,finished_at,fail_reason,notes,dest_path");
        foreach (var r in rows)
        {
            sb.Append(Csv(r.FileName)).Append(',')
              .Append(Csv(r.FilePath)).Append(',')
              .Append(Csv(r.Status)).Append(',')
              .Append(r.OrigSize).Append(',')
              .Append(r.OutSize).Append(',')
              .Append(r.Ratio.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
              .Append(r.OrigWidth).Append(',')
              .Append(r.OrigHeight).Append(',')
              .Append(Csv(r.OrigCodec)).Append(',')
              .Append(r.OrigDuration.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
              .Append(r.OrigBitrate).Append(',')
              .Append(r.TargetBitrate).Append(',')
              .Append(r.StartedAt).Append(',')
              .Append(r.FinishedAt).Append(',')
              .Append(Csv(r.FailReason)).Append(',')
              .Append(Csv(r.Notes)).Append(',')
              .Append(Csv(r.DestPath)).AppendLine();
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        Log.Information("记录已导出 CSV: {Path}（{N} 行）", path, rows.Count);
    }

    private static string Csv(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        bool need = s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r');
        s = s.Replace("\"", "\"\"").Replace('\n', ' ').Replace('\r', ' ');
        return need ? $"\"{s}\"" : s;
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}
