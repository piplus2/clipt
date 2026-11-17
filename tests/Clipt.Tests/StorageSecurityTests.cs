using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Clipt.Models;
using Clipt.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipt.Tests;

public sealed class StorageSecurityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clipt-tests-" + Guid.NewGuid().ToString("N"));
    private readonly CryptoVault _vault = CryptoVault.ForTesting(RandomNumberGenerator.GetBytes(32));

    public StorageSecurityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static ClipItem Text(string text, string app = "Notepad") => new()
    {
        Kind = ClipKind.Text,
        Text = text,
        Hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("text:" + text))),
        SourceApp = app,
        SourcePath = $@"C:\Windows\{app}.exe",
    };

    /// <summary>Concatenates every file the store wrote, so we can grep for leaked plaintext.</summary>
    private byte[] AllBytesOnDisk()
    {
        SqliteConnection.ClearAllPools();
        return Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadAllBytes(f)).ToArray();
    }

    private static bool Contains(byte[] haystack, string needle) =>
        haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(needle)) >= 0 ||
        haystack.AsSpan().IndexOf(Encoding.Unicode.GetBytes(needle)) >= 0;

    // ---------- vault ----------

    [Fact]
    public void Vault_round_trips_and_uses_fresh_nonces()
    {
        var a = _vault.EncryptString("hello", "text")!;
        var b = _vault.EncryptString("hello", "text")!;
        Assert.NotEqual(a, b);                                  // random nonce per message
        Assert.Equal("hello", _vault.DecryptString(a, "text"));
    }

    [Fact]
    public void Vault_detects_tampering_and_wrong_context()
    {
        var blob = _vault.EncryptString("top secret", "text")!;
        var tampered = (byte[])blob.Clone();
        tampered[^1] ^= 0x01;
        Assert.ThrowsAny<CryptographicException>(() => _vault.DecryptString(tampered, "text"));
        Assert.ThrowsAny<CryptographicException>(() => _vault.DecryptString(blob, "html")); // column-bound
    }

    [Fact]
    public void Vault_with_a_different_key_cannot_decrypt()
    {
        var other = CryptoVault.ForTesting(RandomNumberGenerator.GetBytes(32));
        var blob = _vault.EncryptString("top secret", "text")!;
        Assert.ThrowsAny<CryptographicException>(() => other.DecryptString(blob, "text"));
    }

    [Fact]
    public void Fingerprints_are_keyed()
    {
        var other = CryptoVault.ForTesting(RandomNumberGenerator.GetBytes(32));
        Assert.Equal(_vault.Fingerprint("x"), _vault.Fingerprint("x"));
        Assert.NotEqual(_vault.Fingerprint("x"), other.Fingerprint("x"));
        Assert.NotEqual(Convert.ToHexString(SHA256.HashData("x"u8)), _vault.Fingerprint("x"));
    }

    /// <summary>Stands in for DPAPI / the desktop keyring, so tests never touch the real OS key store.</summary>
    private sealed class TestKeyProtector : Clipt.Platform.IKeyProtector
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

        public byte[] Protect(byte[] secret, byte[] entropy)
        {
            var blob = new byte[12 + 16 + secret.Length];
            RandomNumberGenerator.Fill(blob.AsSpan(0, 12));
            using var aes = new AesGcm(_key, 16);
            aes.Encrypt(blob.AsSpan(0, 12), secret, blob.AsSpan(28), blob.AsSpan(12, 16), entropy);
            return blob;
        }

        public byte[] Unprotect(byte[] blob, byte[] entropy)
        {
            var plain = new byte[blob.Length - 28];
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28), blob.AsSpan(12, 16), plain, entropy);
            return plain;
        }
    }

    [Fact]
    public void Wrapped_key_file_does_not_contain_the_raw_key()
    {
        var protector = new TestKeyProtector();
        var v1 = CryptoVault.Open(_dir, protector);
        var keyFile = File.ReadAllBytes(Path.Combine(_dir, CryptoVault.KeyFileName));
        Assert.True(keyFile.Length > 32);            // wrapped blob, not a bare 32-byte key
        var v2 = CryptoVault.Open(_dir, protector);  // same user can reopen it
        Assert.Equal(v1.Fingerprint("a"), v2.Fingerprint("a"));
    }

    [Fact]
    public void Key_wrapped_for_someone_else_is_unreadable()
    {
        CryptoVault.Open(_dir, new TestKeyProtector());
        Assert.ThrowsAny<CryptographicException>(() => CryptoVault.Open(_dir, new TestKeyProtector()));
    }

    // ---------- store ----------

    [Fact]
    public void Nothing_copied_is_written_to_disk_in_plaintext()
    {
        var store = new ClipStore(_dir, _vault);
        store.Upsert(Text("MarkerAlphaPlaintextShouldNeverAppear quarterly numbers", app: "MarkerAppName"));
        var rich = Text("MarkerRichBody");
        rich.Html = "<b>MarkerHtmlBody</b>";
        rich.Rtf = @"{\rtf1 MarkerRtfBody}";
        store.Upsert(rich);
        var img = new ClipItem { Kind = ClipKind.Image, ImageWidth = 1, ImageHeight = 1, Hash = "img:abc" };
        var png = Encoding.ASCII.GetBytes("\x89PNG\r\n\x1a\nMarkerImageBytes");
        img.ImagePath = store.WriteImage(png, img.Hash);
        var stored = store.Upsert(img);
        store.SetOcrText(stored.Id, "MarkerOcrText invoice 4821");

        // Positive control: the scan really does see a plaintext leak when there is one.
        File.WriteAllText(Path.Combine(_dir, "control.txt"), "LeakedControlMarker");
        Assert.True(Contains(AllBytesOnDisk(), "LeakedControlMarker"));
        File.Delete(Path.Combine(_dir, "control.txt"));

        var disk = AllBytesOnDisk();
        Assert.True(disk.Length > 4096, "expected the database to have been written");
        foreach (var marker in new[] { "MarkerAlpha", "MarkerAppName", "MarkerRichBody", "MarkerHtmlBody",
                                       "MarkerRtfBody", "MarkerImageBytes", "MarkerOcrText", "quarterly" })
            Assert.False(Contains(disk, marker), $"plaintext '{marker}' found on disk");
        Assert.False(Contains(disk, "\x89PNG"), "unencrypted PNG found on disk");

        // ...yet everything is still usable in memory.
        Assert.Single(store.Search(new SearchRequest { Query = "quarterly" }));
        Assert.Single(store.Search(new SearchRequest { Query = "invoice 4821" }));
        Assert.Equal(png, store.ReadImage(stored.ImagePath));
        Assert.Equal("<b>MarkerHtmlBody</b>", store.GetRichFormats(store.Search(new SearchRequest { Query = "MarkerRichBody" })[0].Id).html);
    }

    [Fact]
    public void History_survives_restart_and_dedupes()
    {
        var store = new ClipStore(_dir, _vault);
        store.Upsert(Text("same thing"));
        store.Upsert(Text("same thing"));
        SqliteConnection.ClearAllPools();

        var reopened = new ClipStore(_dir, _vault);
        var all = reopened.Search(new SearchRequest());
        Assert.Single(all);
        Assert.Equal(2, all[0].UseCount);
        Assert.Equal("same thing", all[0].Text);
    }

    [Fact]
    public void Search_is_case_and_accent_insensitive()
    {
        var store = new ClipStore(_dir, _vault);
        store.Upsert(Text("Le Café de Flore"));
        Assert.Single(store.Search(new SearchRequest { Query = "cafe flore" }));
        Assert.Single(store.Search(new SearchRequest { Query = "app:notepad" }));
        Assert.Empty(store.Search(new SearchRequest { Query = "app:chrome" }));
    }

    [Fact]
    public void Sensitive_clips_are_not_searchable_by_content()
    {
        var store = new ClipStore(_dir, _vault);
        var secret = Text("Tr0ub4dor&3x!");
        secret.SetSensitive(true, "Looks like a password");
        store.Upsert(secret);

        Assert.Empty(store.Search(new SearchRequest { Query = "Tr0ub4dor" }));
        Assert.Single(store.Search(new SearchRequest { Query = "password" })); // findable by its label only
    }

    [Fact]
    public void Sensitive_clips_expire_unless_pinned()
    {
        var store = new ClipStore(_dir, _vault);
        var a = Text("secret-one-Xy9!kk"); a.SetSensitive(true, "x");
        var b = Text("secret-two-Qz8!mm"); b.SetSensitive(true, "x");
        var plain = Text("ordinary note");
        store.Upsert(a);
        var pinned = store.Upsert(b);
        store.Upsert(plain);
        store.SetPinned(pinned.Id, true);

        int removed = store.ExpireSensitive(TimeSpan.FromMilliseconds(-1)); // everything is "older" than this
        Assert.Equal(1, removed);
        Assert.Equal(2, store.Count());
    }

    [Fact]
    public void Deleting_removes_the_encrypted_image_file()
    {
        var store = new ClipStore(_dir, _vault);
        var img = new ClipItem { Kind = ClipKind.Image, ImageWidth = 1, ImageHeight = 1, Hash = "img:del" };
        img.ImagePath = store.WriteImage([1, 2, 3], img.Hash);
        var stored = store.Upsert(img);
        Assert.True(File.Exists(stored.ImagePath));
        store.Delete(stored.Id);
        Assert.False(File.Exists(stored.ImagePath));
    }

    private static ClipItem StoredImage(ClipStore store, string hash, int bytes)
    {
        var img = new ClipItem { Kind = ClipKind.Image, ImageWidth = 1, ImageHeight = 1, Hash = hash };
        img.ImagePath = store.WriteImage(new byte[bytes], img.Hash);
        var stored = store.Upsert(img);
        Thread.Sleep(5); // distinct last_used_at, so "least recently used" is well defined
        return stored;
    }

    [Fact]
    public void Image_budget_removes_least_recently_used_unpinned_images()
    {
        var store = new ClipStore(_dir, _vault);
        var oldest = StoredImage(store, "img:1", 100_000);
        var pinned = StoredImage(store, "img:2", 100_000);
        var middle = StoredImage(store, "img:3", 100_000);
        var newest = StoredImage(store, "img:4", 100_000);
        store.SetPinned(pinned.Id, true);
        store.Upsert(Text("text is not affected"));

        int removed = store.PruneImages(250_000);

        Assert.Equal(2, removed); // oldest then middle; the pinned one is skipped
        Assert.False(File.Exists(oldest.ImagePath));
        Assert.False(File.Exists(middle.ImagePath));
        Assert.True(File.Exists(pinned.ImagePath));
        Assert.True(File.Exists(newest.ImagePath));
        Assert.Equal(3, store.Count());
    }

    [Fact]
    public void Image_budget_keeps_the_latest_image_even_if_it_alone_is_too_big()
    {
        var store = new ClipStore(_dir, _vault);
        var older = StoredImage(store, "img:a", 10_000);
        var big = StoredImage(store, "img:b", 500_000);

        Assert.Equal(1, store.PruneImages(100_000));
        Assert.False(File.Exists(older.ImagePath));
        Assert.True(File.Exists(big.ImagePath));
        Assert.Equal(0, store.PruneImages(0)); // 0 = no limit
    }

    [Fact]
    public void Storage_usage_is_reported_by_kind()
    {
        var store = new ClipStore(_dir, _vault);
        store.Upsert(Text(new string('x', 5_000)));
        store.Upsert(Text("short"));
        StoredImage(store, "img:usage", 50_000);

        var usage = store.GetStorageUsage();
        var text = usage.Kinds.Single(k => k.Kind == ClipKind.Text);
        var images = usage.Kinds.Single(k => k.Kind == ClipKind.Image);

        Assert.Equal(2, text.Count);
        Assert.InRange(text.Bytes, 5_000, 6_000);  // encrypted, so a little more than the plaintext
        Assert.Equal(1, images.Count);
        Assert.InRange(images.Bytes, 50_000, 51_000);
        Assert.Equal(0, usage.Kinds.Single(k => k.Kind == ClipKind.Link).Count);
        Assert.True(usage.OtherBytes > 0);
        Assert.Equal(usage.Kinds.Sum(k => k.Bytes) + usage.OtherBytes, usage.TotalBytes);
    }

    [Fact]
    public void Thumbnails_are_encrypted_and_deleted_with_their_image()
    {
        var store = new ClipStore(_dir, _vault);
        var img = new ClipItem { Kind = ClipKind.Image, ImageWidth = 1, ImageHeight = 1, Hash = "img:thumb" };
        img.ImagePath = store.WriteImage([1, 2, 3], img.Hash);
        var stored = store.Upsert(img);

        var thumb = Encoding.ASCII.GetBytes("\x89PNG\r\n\x1a\nMarkerThumbnailBytes");
        store.WriteThumbnail(stored.ImagePath!, thumb);
        var thumbPath = ClipStore.ThumbnailPath(stored.ImagePath!);
        Assert.True(File.Exists(thumbPath));
        Assert.Equal(-1, Encoding.ASCII.GetString(File.ReadAllBytes(thumbPath)).IndexOf("MarkerThumbnailBytes", StringComparison.Ordinal));
        Assert.Equal(thumb, store.ReadThumbnail(stored.ImagePath));

        store.Delete(stored.Id);
        Assert.False(File.Exists(thumbPath));
    }

    [Fact]
    public void No_thumbnail_is_written_for_a_deleted_image()
    {
        var store = new ClipStore(_dir, _vault);
        var img = new ClipItem { Kind = ClipKind.Image, ImageWidth = 1, ImageHeight = 1, Hash = "img:gone" };
        img.ImagePath = store.WriteImage([1, 2, 3], img.Hash);
        var stored = store.Upsert(img);
        store.Delete(stored.Id);

        store.WriteThumbnail(stored.ImagePath!, [4, 5, 6]);
        Assert.False(File.Exists(ClipStore.ThumbnailPath(stored.ImagePath!)));
    }

    [Fact]
    public void Legacy_plaintext_store_is_migrated_and_destroyed()
    {
        // Build a v0.1 (unencrypted) database exactly like the first release did.
        var legacy = Path.Combine(_dir, "history.db");
        using (var c = new SqliteConnection($"Data Source={legacy};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE clips(id INTEGER PRIMARY KEY, kind INTEGER, text TEXT, html TEXT, rtf TEXT, image_path TEXT,
                  image_w INTEGER, image_h INTEGER, ocr_text TEXT, hash TEXT UNIQUE, source_app TEXT, source_path TEXT,
                  created_at INTEGER, last_used_at INTEGER, use_count INTEGER, pinned INTEGER);
                INSERT INTO clips VALUES (1, 0, 'LegacyMarker meeting notes', NULL, NULL, NULL, 0, 0, NULL, 'h1', 'Word', NULL, 1, 1, 1, 1);
                INSERT INTO clips VALUES (2, 0, 'Tr0ub4dor&3x!', NULL, NULL, NULL, 0, 0, NULL, 'h2', 'Chrome', NULL, 2, 2, 1, 0);
                """;
            cmd.ExecuteNonQuery();
        }

        var store = new ClipStore(_dir, _vault);
        Assert.False(File.Exists(legacy));
        var all = store.Search(new SearchRequest());
        Assert.Equal(2, all.Count);
        Assert.True(all.Single(i => i.Text!.StartsWith("LegacyMarker")).Pinned);
        Assert.True(all.Single(i => i.Text == "Tr0ub4dor&3x!").Sensitive); // detection applied to old history too
        Assert.False(Contains(AllBytesOnDisk(), "LegacyMarker"));
    }

    // ---------- settings ----------

    [Fact]
    public void Old_exe_list_settings_migrate_to_friendly_names()
    {
        var s = JsonSerializer.Deserialize<AppSettings>("""{ "IgnoredApps": ["KeePass.exe", "notes.exe"] }""")!;
        s.Normalize();
        Assert.Null(s.IgnoredApps);
        Assert.Contains(s.ExcludedApps!, a => a is { Exe: "KeePass.exe", Name: "KeePass" });
        Assert.Contains(s.ExcludedApps!, a => a is { Exe: "notes.exe", Name: "notes" });
        Assert.True(s.IsExcluded(@"C:\Program Files\KeePass\KeePass.exe"));
        Assert.False(s.IsExcluded(@"C:\Windows\notepad.exe"));
    }

    [Fact]
    public void Exclusions_match_Windows_and_Unix_paths_on_any_OS()
    {
        var s = new AppSettings { ExcludedApps = [new("KeePass.exe", "KeePass"), new("keepassxc", "KeePassXC")] };
        Assert.True(s.IsExcluded(@"C:\Program Files\KeePass\KeePass.exe"));
        Assert.True(s.IsExcluded("/usr/bin/keepassxc"));
        Assert.True(s.IsExcluded("/app/bin/KeePassXC"));
        Assert.False(s.IsExcluded("/usr/lib/firefox/firefox"));
        Assert.False(s.IsExcluded("/usr/bin/keepassxc-cli"));
    }

    [Fact]
    public void Fresh_settings_exclude_password_managers()
    {
        var s = new AppSettings();
        s.Normalize();
        Assert.True(OperatingSystem.IsWindows()
            ? s.IsExcluded(@"C:\Users\me\AppData\Local\1Password\app\8\1Password.exe")
            : s.IsExcluded("/usr/bin/keepassxc"));
        Assert.True(s.DetectSensitive);
        Assert.True(s.HideFromScreenCapture);
    }
}
