using System.Globalization;
using System.IO;
using System.Text;
using Clipt.Models;
using Microsoft.Data.Sqlite;

namespace Clipt.Services;

public sealed class SearchRequest
{
    public string Query { get; init; } = "";
    public ClipKind[]? Kinds { get; init; }
    public bool PinnedOnly { get; init; }
    public int Limit { get; init; } = 400;
}

/// <summary>
/// Clipboard history persisted in SQLite with every piece of clip content encrypted (see <see cref="CryptoVault"/>).
/// Nothing that was copied is ever written to disk in plaintext: text, rich text, OCR text, source app and
/// images are all encrypted, and de-duplication uses keyed fingerprints. Search runs over an in-memory,
/// decrypted index, so no plaintext search index exists on disk either.
/// </summary>
public sealed class ClipStore
{
    public const string DbFileName = "clips.db";
    private const string LegacyDbFileName = "history.db";
    private const int SchemaVersion = 2;
    private const int MaxIndexedChars = 200_000;

    private readonly CryptoVault _vault;
    private readonly string _connectionString;
    private readonly object _gate = new();
    private readonly Dictionary<long, ClipItem> _items = [];
    private readonly Dictionary<long, string> _searchText = [];

    public string DataDir { get; }
    public string ImagesDir { get; }

    public ClipStore(string dataDir, CryptoVault vault)
    {
        _vault = vault;
        DataDir = dataDir;
        ImagesDir = Path.Combine(dataDir, "images");
        Directory.CreateDirectory(ImagesDir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataDir, DbFileName),
            Pooling = true,
        }.ToString();

        using (var c = Open())
        {
            Exec(c, "PRAGMA journal_mode=WAL;");
            Exec(c, $"""
                CREATE TABLE IF NOT EXISTS clips(
                    id               INTEGER PRIMARY KEY,
                    kind             INTEGER NOT NULL,
                    text             BLOB,
                    html             BLOB,
                    rtf              BLOB,
                    ocr_text         BLOB,
                    source_app       BLOB,
                    source_path      BLOB,
                    image_path       TEXT,
                    image_w          INTEGER NOT NULL DEFAULT 0,
                    image_h          INTEGER NOT NULL DEFAULT 0,
                    fingerprint      TEXT NOT NULL UNIQUE,
                    created_at       INTEGER NOT NULL,
                    last_used_at     INTEGER NOT NULL,
                    use_count        INTEGER NOT NULL DEFAULT 1,
                    pinned           INTEGER NOT NULL DEFAULT 0,
                    sensitive        INTEGER NOT NULL DEFAULT 0,
                    sensitive_reason TEXT
                );
                PRAGMA user_version = {SchemaVersion};
                """);
        }

        MigrateLegacy();
        LoadAll();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        // Overwrite deleted content instead of leaving it in free pages of the file.
        Exec(c, "PRAGMA secure_delete = ON;");
        return c;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ---------- encryption helpers ----------

    private object Enc(string? value, string column) => (object?)_vault.EncryptString(value, column) ?? DBNull.Value;

    private string? Dec(SqliteDataReader r, string column)
    {
        int i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : _vault.DecryptString((byte[])r.GetValue(i), column);
    }

    /// <summary>Encrypts and writes an image; returns its path. File names are keyed fingerprints.</summary>
    public string WriteImage(byte[] png, string identity)
    {
        var path = Path.Combine(ImagesDir, _vault.Fingerprint("img-file:" + identity)[..32] + ".clip");
        if (!File.Exists(path))
        {
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, _vault.Encrypt(png, "image"));
            File.Move(tmp, path, overwrite: true);
        }
        return path;
    }

    public byte[]? ReadImage(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        try { return _vault.Decrypt(File.ReadAllBytes(path), "image"); }
        catch { return null; }
    }

    // ---------- writes ----------

    /// <summary>Inserts a new clip, or bumps an identical existing one to the top. Returns the stored item.</summary>
    public ClipItem Upsert(ClipItem item)
    {
        lock (_gate)
        {
            var fingerprint = _vault.Fingerprint(item.Hash);
            using var c = Open();
            long now = Now();

            using (var find = c.CreateCommand())
            {
                find.CommandText = "SELECT id FROM clips WHERE fingerprint = $f";
                find.Parameters.AddWithValue("$f", fingerprint);
                if (find.ExecuteScalar() is long existingId && _items.TryGetValue(existingId, out var existing))
                {
                    using var upd = c.CreateCommand();
                    upd.CommandText = """
                        UPDATE clips SET last_used_at = $now, use_count = use_count + 1,
                            source_app = COALESCE($app, source_app), source_path = COALESCE($path, source_path),
                            html = COALESCE($html, html), rtf = COALESCE($rtf, rtf),
                            sensitive = MAX(sensitive, $sens), sensitive_reason = COALESCE(sensitive_reason, $reason)
                        WHERE id = $id
                        """;
                    upd.Parameters.AddWithValue("$now", now);
                    upd.Parameters.AddWithValue("$app", Enc(item.SourceApp, "source_app"));
                    upd.Parameters.AddWithValue("$path", Enc(item.SourcePath, "source_path"));
                    upd.Parameters.AddWithValue("$html", Enc(item.Html, "html"));
                    upd.Parameters.AddWithValue("$rtf", Enc(item.Rtf, "rtf"));
                    upd.Parameters.AddWithValue("$sens", item.Sensitive ? 1 : 0);
                    upd.Parameters.AddWithValue("$reason", (object?)item.SensitiveReason ?? DBNull.Value);
                    upd.Parameters.AddWithValue("$id", existingId);
                    upd.ExecuteNonQuery();

                    existing.LastUsedAt = DateTimeOffset.FromUnixTimeMilliseconds(now).UtcDateTime;
                    existing.UseCount++;
                    existing.SourceApp = item.SourceApp ?? existing.SourceApp;
                    existing.SourcePath = item.SourcePath ?? existing.SourcePath;
                    if (item.Sensitive && !existing.Sensitive) existing.SetSensitive(true, item.SensitiveReason);
                    Index(existing);
                    return existing;
                }
            }

            using var ins = c.CreateCommand();
            ins.CommandText = """
                INSERT INTO clips(kind, text, html, rtf, ocr_text, source_app, source_path, image_path, image_w, image_h,
                                  fingerprint, created_at, last_used_at, use_count, pinned, sensitive, sensitive_reason)
                VALUES ($kind, $text, $html, $rtf, $ocr, $app, $path, $img, $w, $h, $f, $now, $now, 1, 0, $sens, $reason);
                SELECT last_insert_rowid();
                """;
            ins.Parameters.AddWithValue("$kind", (int)item.Kind);
            ins.Parameters.AddWithValue("$text", Enc(item.Text, "text"));
            ins.Parameters.AddWithValue("$html", Enc(item.Html, "html"));
            ins.Parameters.AddWithValue("$rtf", Enc(item.Rtf, "rtf"));
            ins.Parameters.AddWithValue("$ocr", Enc(item.OcrText, "ocr_text"));
            ins.Parameters.AddWithValue("$app", Enc(item.SourceApp, "source_app"));
            ins.Parameters.AddWithValue("$path", Enc(item.SourcePath, "source_path"));
            ins.Parameters.AddWithValue("$img", (object?)item.ImagePath ?? DBNull.Value);
            ins.Parameters.AddWithValue("$w", item.ImageWidth);
            ins.Parameters.AddWithValue("$h", item.ImageHeight);
            ins.Parameters.AddWithValue("$f", fingerprint);
            ins.Parameters.AddWithValue("$now", now);
            ins.Parameters.AddWithValue("$sens", item.Sensitive ? 1 : 0);
            ins.Parameters.AddWithValue("$reason", (object?)item.SensitiveReason ?? DBNull.Value);

            item.Id = (long)ins.ExecuteScalar()!;
            item.CreatedAt = item.LastUsedAt = DateTimeOffset.FromUnixTimeMilliseconds(now).UtcDateTime;
            item.UseCount = 1;
            item.Html = item.Rtf = null; // rich formats are loaded on demand when pasting
            Index(item);
            return item;
        }
    }

    public void MarkUsed(long id) => Update(id, "last_used_at = $v, use_count = use_count + 1", Now(), it =>
    {
        it.LastUsedAt = DateTime.UtcNow;
        it.UseCount++;
    });

    public void SetPinned(long id, bool pinned) => Update(id, "pinned = $v", pinned ? 1 : 0, it => it.Pinned = pinned);

    public void SetSensitive(long id, bool sensitive, string? reason)
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE clips SET sensitive = $s, sensitive_reason = $r WHERE id = $id";
            cmd.Parameters.AddWithValue("$s", sensitive ? 1 : 0);
            cmd.Parameters.AddWithValue("$r", (object?)reason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
            if (_items.TryGetValue(id, out var it)) it.SetSensitive(sensitive, reason);
        }
    }

    public void SetOcrText(long id, string text)
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE clips SET ocr_text = $t WHERE id = $id";
            cmd.Parameters.AddWithValue("$t", Enc(text, "ocr_text"));
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
            if (_items.TryGetValue(id, out var it))
            {
                it.OcrText = text;
                Index(it);
            }
        }
    }

    private void Update(long id, string set, object value, Action<ClipItem> apply)
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"UPDATE clips SET {set} WHERE id = $id";
            cmd.Parameters.AddWithValue("$v", value);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
            if (_items.TryGetValue(id, out var it)) apply(it);
        }
    }

    // ---------- reads ----------

    public (string? html, string? rtf) GetRichFormats(long id)
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT html, rtf FROM clips WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? (Dec(r, "html"), Dec(r, "rtf")) : (null, null);
        }
    }

    public int Count()
    {
        lock (_gate) return _items.Count;
    }

    public List<ClipItem> Search(SearchRequest req)
    {
        var terms = new List<string>();
        var apps = new List<string>();
        foreach (var raw in req.Query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.StartsWith("app:", StringComparison.OrdinalIgnoreCase) && raw.Length > 4) apps.Add(Normalize(raw[4..]));
            else terms.Add(Normalize(raw));
        }

        lock (_gate)
        {
            IEnumerable<ClipItem> q = _items.Values;
            if (req.Kinds is { Length: > 0 }) q = q.Where(i => req.Kinds.Contains(i.Kind));
            if (req.PinnedOnly) q = q.Where(i => i.Pinned);
            if (apps.Count > 0) q = q.Where(i => apps.All(a => Normalize(i.SourceApp ?? "").Contains(a, StringComparison.Ordinal)));
            if (terms.Count > 0)
                q = q.Where(i => _searchText.TryGetValue(i.Id, out var hay) && terms.All(t => hay.Contains(t, StringComparison.Ordinal)));
            return q.OrderByDescending(i => i.LastUsedAt).Take(req.Limit).ToList();
        }
    }

    /// <summary>Distinct apps that clips were copied from (for the "exclude app" picker).</summary>
    public List<(string name, string path)> KnownSourceApps()
    {
        lock (_gate)
        {
            return _items.Values
                .Where(i => !string.IsNullOrEmpty(i.SourcePath))
                .GroupBy(i => Path.GetFileName(i.SourcePath!), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(i => i.LastUsedAt).First())
                .Select(i => (i.SourceApp ?? Path.GetFileNameWithoutExtension(i.SourcePath!), i.SourcePath!))
                .ToList();
        }
    }

    public List<(long id, string path)> ImagesMissingOcr(int limit = 50)
    {
        lock (_gate)
        {
            return _items.Values
                .Where(i => i.Kind == ClipKind.Image && i.OcrText is null && i.ImagePath is not null)
                .OrderByDescending(i => i.LastUsedAt).Take(limit)
                .Select(i => (i.Id, i.ImagePath!)).ToList();
        }
    }

    // ---------- deletes ----------

    public void Delete(long id) => DeleteWhere("id = $a", id);

    public void Clear(bool keepPinned) => DeleteWhere(keepPinned ? "pinned = 0" : "1=1");

    /// <summary>Removes sensitive clips older than <paramref name="ttl"/> (unless the user pinned them).</summary>
    public int ExpireSensitive(TimeSpan ttl) =>
        DeleteWhere("sensitive = 1 AND pinned = 0 AND last_used_at < $a", DateTimeOffset.UtcNow.Add(-ttl).ToUnixTimeMilliseconds());

    /// <summary>Applies retention: max age and max item count (pinned items are never pruned).</summary>
    public void Prune(int maxItems, int retentionDays)
    {
        if (retentionDays > 0)
            DeleteWhere("pinned = 0 AND last_used_at < $a", DateTimeOffset.UtcNow.AddDays(-retentionDays).ToUnixTimeMilliseconds());
        if (maxItems > 0)
            DeleteWhere("pinned = 0 AND id IN (SELECT id FROM clips WHERE pinned = 0 ORDER BY last_used_at DESC LIMIT -1 OFFSET $a)", maxItems);
    }

    private int DeleteWhere(string where, object? arg = null)
    {
        lock (_gate)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            var victims = new List<(long id, string? image)>();
            using (var sel = c.CreateCommand())
            {
                sel.Transaction = tx;
                sel.CommandText = $"SELECT id, image_path FROM clips WHERE {where}";
                if (arg is not null) sel.Parameters.AddWithValue("$a", arg);
                using var r = sel.ExecuteReader();
                while (r.Read()) victims.Add((r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1)));
            }
            if (victims.Count == 0) return 0;
            using (var del = c.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = $"DELETE FROM clips WHERE {where}";
                if (arg is not null) del.Parameters.AddWithValue("$a", arg);
                del.ExecuteNonQuery();
            }
            tx.Commit();

            foreach (var (id, image) in victims)
            {
                _items.Remove(id);
                _searchText.Remove(id);
                if (image is not null)
                {
                    try { File.Delete(image); } catch { /* encrypted anyway */ }
                }
            }
            // Checkpoint so deleted pages don't linger in the WAL file.
            Exec(c, "PRAGMA wal_checkpoint(TRUNCATE);");
            return victims.Count;
        }
    }

    // ---------- in-memory index ----------

    private void LoadAll()
    {
        lock (_gate)
        {
            _items.Clear();
            _searchText.Clear();
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT id, kind, text, ocr_text, source_app, source_path, image_path, image_w, image_h, " +
                              "created_at, last_used_at, use_count, pinned, sensitive, sensitive_reason FROM clips";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                ClipItem item;
                try
                {
                    item = new ClipItem
                    {
                        Id = r.GetInt64(0),
                        Kind = (ClipKind)r.GetInt32(1),
                        Text = Dec(r, "text"),
                        OcrText = Dec(r, "ocr_text"),
                        SourceApp = Dec(r, "source_app"),
                        SourcePath = Dec(r, "source_path"),
                        ImagePath = r.IsDBNull(6) ? null : r.GetString(6),
                        ImageWidth = r.GetInt32(7),
                        ImageHeight = r.GetInt32(8),
                        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(9)).UtcDateTime,
                        LastUsedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(10)).UtcDateTime,
                        UseCount = r.GetInt32(11),
                        Pinned = r.GetInt32(12) == 1,
                    };
                    item.SetSensitive(r.GetInt32(13) == 1, r.IsDBNull(14) ? null : r.GetString(14));
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                    continue; // tampered or foreign row: skip it rather than crash
                }
                Index(item);
            }
        }
    }

    private void Index(ClipItem item)
    {
        _items[item.Id] = item;
        // Sensitive clips are deliberately NOT searchable by content.
        var hay = item.Sensitive
            ? $"{item.SensitiveReason} {item.SourceApp}"
            : $"{Cap(item.Text)}\n{Cap(item.OcrText)}\n{item.SourceApp}";
        _searchText[item.Id] = Normalize(hay);
    }

    /// <summary>Call after a clip's sensitivity changes so the search index follows.</summary>
    public void Reindex(ClipItem item)
    {
        lock (_gate) Index(item);
    }

    private static string Cap(string? s) => s is null ? "" : s.Length > MaxIndexedChars ? s[..MaxIndexedChars] : s;

    /// <summary>Lower-case and strip diacritics so "cafe" finds "Café".</summary>
    public static string Normalize(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    // ---------- migration from the unencrypted v1 store ----------

    private void MigrateLegacy()
    {
        var legacy = Path.Combine(DataDir, LegacyDbFileName);
        if (!File.Exists(legacy)) return;

        var rows = new List<(ClipItem item, string? oldImage, long created, long lastUsed, int uses, bool pinned)>();
        var cs = new SqliteConnectionStringBuilder { DataSource = legacy, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        try
        {
            using var old = new SqliteConnection(cs);
            old.Open();
            using var cmd = old.CreateCommand();
            cmd.CommandText = "SELECT kind, text, html, rtf, image_path, image_w, image_h, ocr_text, hash, source_app, source_path, " +
                              "created_at, last_used_at, use_count, pinned FROM clips";
            using var r = cmd.ExecuteReader();
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            while (r.Read())
            {
                var item = new ClipItem
                {
                    Kind = (ClipKind)r.GetInt32(0), Text = S(1), Html = S(2), Rtf = S(3),
                    ImageWidth = r.GetInt32(5), ImageHeight = r.GetInt32(6), OcrText = S(7), Hash = r.GetString(8),
                    SourceApp = S(9), SourcePath = S(10),
                };
                if (item.Kind != ClipKind.Image && SensitiveDetector.Detect(item.Text) is { } reason)
                    item.SetSensitive(true, reason);
                rows.Add((item, S(4), r.GetInt64(11), r.GetInt64(12), r.GetInt32(13), r.GetInt32(14) == 1));
            }
        }
        catch (SqliteException) { /* unreadable legacy file: discard below */ }

        using (var c = Open())
        using (var tx = c.BeginTransaction())
        {
            foreach (var (item, oldImage, created, lastUsed, uses, pinned) in rows)
            {
                if (oldImage is not null)
                {
                    if (!File.Exists(oldImage)) continue;
                    item.ImagePath = WriteImage(File.ReadAllBytes(oldImage), item.Hash);
                }
                using var ins = c.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT OR IGNORE INTO clips(kind, text, html, rtf, ocr_text, source_app, source_path, image_path, image_w, image_h,
                                      fingerprint, created_at, last_used_at, use_count, pinned, sensitive, sensitive_reason)
                    VALUES ($kind, $text, $html, $rtf, $ocr, $app, $path, $img, $w, $h, $f, $c, $l, $u, $p, $s, $r)
                    """;
                ins.Parameters.AddWithValue("$kind", (int)item.Kind);
                ins.Parameters.AddWithValue("$text", Enc(item.Text, "text"));
                ins.Parameters.AddWithValue("$html", Enc(item.Html, "html"));
                ins.Parameters.AddWithValue("$rtf", Enc(item.Rtf, "rtf"));
                ins.Parameters.AddWithValue("$ocr", Enc(item.OcrText, "ocr_text"));
                ins.Parameters.AddWithValue("$app", Enc(item.SourceApp, "source_app"));
                ins.Parameters.AddWithValue("$path", Enc(item.SourcePath, "source_path"));
                ins.Parameters.AddWithValue("$img", (object?)item.ImagePath ?? DBNull.Value);
                ins.Parameters.AddWithValue("$w", item.ImageWidth);
                ins.Parameters.AddWithValue("$h", item.ImageHeight);
                ins.Parameters.AddWithValue("$f", _vault.Fingerprint(item.Hash));
                ins.Parameters.AddWithValue("$c", created);
                ins.Parameters.AddWithValue("$l", lastUsed);
                ins.Parameters.AddWithValue("$u", uses);
                ins.Parameters.AddWithValue("$p", pinned ? 1 : 0);
                ins.Parameters.AddWithValue("$s", item.Sensitive ? 1 : 0);
                ins.Parameters.AddWithValue("$r", (object?)item.SensitiveReason ?? DBNull.Value);
                ins.ExecuteNonQuery();
            }
            tx.Commit();
        }

        // Remove every plaintext artefact of the old store.
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(legacy + suffix); } catch { }
        }
        foreach (var png in Directory.EnumerateFiles(ImagesDir, "*.png"))
        {
            try { File.Delete(png); } catch { }
        }
    }
}
