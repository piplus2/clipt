#pragma warning disable CA1416 // Linux-only file (compiled only into the Linux build)
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Clipt.Platform.Linux;

/// <summary>
/// Linux counterpart of Windows DPAPI. A random 256-bit wrapping key lives in the desktop keyring
/// (Secret Service: GNOME Keyring, KWallet, KeePassXC…), which is unlocked with the user's login. Secrets are
/// wrapped with it using AES-256-GCM. Without a Secret Service the wrapping key is kept in a file only the user
/// can read (mode 0600) and Settings says so.
/// Blob layout: [mode:1][nonce:12][tag:16][ciphertext], mode 1 = keyring, 2 = file.
/// </summary>
internal sealed class SecretServiceKeyProtector : IKeyProtector
{
    private const byte ModeKeyring = 1, ModeFile = 2;
    private const int NonceSize = 12, TagSize = 16;
    private readonly string _fallbackPath;
    private byte[]? _keyringKey;
    private byte[]? _fileKey;
    private bool? _keyringWorks;

    public SecretServiceKeyProtector(string dataDir) => _fallbackPath = Path.Combine(dataDir, "wrap.key");

    /// <summary>True when secrets are protected by the desktop keyring (false: by a 0600 file).</summary>
    public bool UsesKeyring => _keyringWorks ??= TryKeyringKey(create: true) is not null;

    public byte[] Protect(byte[] secret, byte[] entropy)
    {
        byte mode = UsesKeyring ? ModeKeyring : ModeFile;
        var key = mode == ModeKeyring ? TryKeyringKey(create: true)! : FileKey(create: true)!;
        var blob = new byte[1 + NonceSize + TagSize + secret.Length];
        blob[0] = mode;
        var nonce = blob.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, secret, blob.AsSpan(1 + NonceSize + TagSize), blob.AsSpan(1 + NonceSize, TagSize), entropy);
        return blob;
    }

    public byte[] Unprotect(byte[] blob, byte[] entropy)
    {
        if (blob.Length < 1 + NonceSize + TagSize || blob[0] is not (ModeKeyring or ModeFile))
            throw new CryptographicException("Unknown protected data format.");
        byte[]? key;
        if (blob[0] == ModeKeyring)
        {
            key = LookupKeyring(out bool reachable);
            if (key is null && !reachable)
                throw new KeyStoreUnavailableException("The desktop keyring (Secret Service) isn't available, so Clipt can't unlock your history.");
        }
        else key = FileKey(create: false);
        if (key is null) throw new CryptographicException("The key that protects this data no longer exists.");

        var plain = new byte[blob.Length - 1 - NonceSize - TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(blob.AsSpan(1, NonceSize), blob.AsSpan(1 + NonceSize + TagSize), blob.AsSpan(1 + NonceSize, TagSize), plain, entropy);
        return plain;
    }

    // ---------- file fallback ----------

    private byte[]? FileKey(bool create)
    {
        if (_fileKey is not null) return _fileKey;
        if (File.Exists(_fallbackPath)) return _fileKey = Convert.FromBase64String(File.ReadAllText(_fallbackPath).Trim());
        if (!create) return null;
        var key = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(Path.GetDirectoryName(_fallbackPath)!);
        var tmp = _fallbackPath + ".tmp";
        using (var f = new FileStream(tmp, new FileStreamOptions
               {
                   Mode = FileMode.Create, Access = FileAccess.Write,
                   UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
               }))
            f.Write(Encoding.ASCII.GetBytes(Convert.ToBase64String(key)));
        File.Move(tmp, _fallbackPath, overwrite: true);
        return _fileKey = key;
    }

    // ---------- keyring ----------

    private byte[]? TryKeyringKey(bool create)
    {
        if (_keyringKey is not null) return _keyringKey;
        var existing = LookupKeyring(out bool reachable);
        if (existing is not null) return _keyringKey = existing;
        if (!reachable || !create) return null;
        var fresh = RandomNumberGenerator.GetBytes(32);
        return LibSecret.Store(Convert.ToBase64String(fresh)) ? _keyringKey = fresh : null;
    }

    private byte[]? LookupKeyring(out bool reachable)
    {
        if (_keyringKey is not null) { reachable = true; return _keyringKey; }
        var value = LibSecret.Lookup(out reachable);
        if (value is null) return null;
        try { return _keyringKey = Convert.FromBase64String(value); }
        catch (FormatException) { return null; }
    }
}

/// <summary>libsecret through its non-variadic API (lookupv/storev with a GHashTable of attributes).</summary>
internal static unsafe class LibSecret
{
    private const string Lib = "libsecret-1.so.0";
    private const string GLib = "libglib-2.0.so.0";
    private const string Label = "Clipt clipboard history key";

    [DllImport(Lib)] private static extern nint secret_password_lookupv_sync(nint schema, nint attributes, nint cancellable, out nint error);
    [DllImport(Lib)] private static extern int secret_password_storev_sync(nint schema, nint attributes, nint collection, byte* label, byte* password, nint cancellable, out nint error);
    [DllImport(Lib)] private static extern void secret_password_free(nint password);
    [DllImport(GLib)] private static extern nint g_hash_table_new(nint hashFunc, nint equalFunc);
    [DllImport(GLib)] private static extern void g_hash_table_insert(nint table, nint key, nint value);
    [DllImport(GLib)] private static extern void g_hash_table_unref(nint table);
    [DllImport(GLib)] private static extern void g_error_free(nint error);

    private static nint _schema;
    private static readonly List<nint> Strings = []; // schema strings live for the whole process

    private static nint Utf8(string s)
    {
        var p = Marshal.StringToCoTaskMemUTF8(s);
        Strings.Add(p);
        return p;
    }

    /// <summary>SecretSchema { name; flags; SecretSchemaAttribute attributes[32]; reserved… } — 592 bytes on LP64.</summary>
    private static nint Schema()
    {
        if (_schema != 0) return _schema;
        const int size = 592;
        var s = Marshal.AllocHGlobal(size);
        new Span<byte>((void*)s, size).Clear();
        *(nint*)s = Utf8("io.github.piplus2.Clipt");
        *(int*)(s + 8) = 0; // SECRET_SCHEMA_NONE
        *(nint*)(s + 16) = Utf8("purpose");  // attributes[0].name, type 0 = STRING
        return _schema = s;
    }

    private static nint Attributes()
    {
        var glib = NativeLibrary.Load(GLib);
        var table = g_hash_table_new(NativeLibrary.GetExport(glib, "g_str_hash"), NativeLibrary.GetExport(glib, "g_str_equal"));
        g_hash_table_insert(table, Utf8("purpose"), Utf8("vault-wrap-key-v1"));
        return table;
    }

    /// <summary>The stored key, or null. <paramref name="reachable"/> is false if the keyring itself failed.</summary>
    public static string? Lookup(out bool reachable)
    {
        reachable = false;
        try
        {
            var attrs = Attributes();
            try
            {
                var p = secret_password_lookupv_sync(Schema(), attrs, 0, out var error);
                if (error != 0) { LogError(error); return null; }
                reachable = true;
                if (p == 0) return null;
                try { return Marshal.PtrToStringUTF8(p); }
                finally { secret_password_free(p); }
            }
            finally { g_hash_table_unref(attrs); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    public static bool Store(string value)
    {
        try
        {
            var attrs = Attributes();
            try
            {
                var label = Encoding.UTF8.GetBytes(Label + "\0");
                var pw = Encoding.UTF8.GetBytes(value + "\0");
                fixed (byte* l = label)
                fixed (byte* p = pw)
                {
                    // NULL collection = the default keyring (normally "login", unlocked at login).
                    int ok = secret_password_storev_sync(Schema(), attrs, 0, l, p, 0, out var error);
                    if (error != 0) { LogError(error); return false; }
                    return ok != 0;
                }
            }
            finally { g_hash_table_unref(attrs); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    private static void LogError(nint error)
    {
        // GError { GQuark domain; gint code; gchar *message; }
        var message = Marshal.PtrToStringUTF8(*(nint*)(error + 8));
        App.Log(new InvalidOperationException("Secret Service: " + message));
        g_error_free(error);
    }
}
