using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Clipt.Services;

/// <summary>
/// Encryption at rest for the clipboard history.
/// A random 256-bit master key is generated once and stored wrapped with Windows DPAPI (CurrentUser scope),
/// so it can only be unwrapped by the same Windows account on the same machine. Clip data is encrypted with
/// AES-256-GCM (authenticated); content fingerprints for de-duplication use HMAC-SHA256 so the database
/// cannot be used to test whether a known secret was ever copied.
/// </summary>
public sealed class CryptoVault
{
    private const byte FormatVersion = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] DpapiEntropy = "Clipt.Vault.v1"u8.ToArray();

    private readonly byte[] _encKey;
    private readonly byte[] _macKey;

    private CryptoVault(byte[] masterKey)
    {
        _encKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, info: "clipt/aes-gcm"u8.ToArray());
        _macKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, info: "clipt/hmac"u8.ToArray());
        CryptographicOperations.ZeroMemory(masterKey);
    }

    public const string KeyFileName = "vault.key";

    /// <summary>Loads the vault key, creating one on first run.</summary>
    /// <exception cref="CryptographicException">The key exists but can't be unwrapped (different user/machine, reset password).</exception>
    public static CryptoVault Open(string dataDir)
    {
        var path = Path.Combine(dataDir, KeyFileName);
        if (File.Exists(path))
        {
            var key = ProtectedData.Unprotect(File.ReadAllBytes(path), DpapiEntropy, DataProtectionScope.CurrentUser);
            return new CryptoVault(key);
        }

        var fresh = RandomNumberGenerator.GetBytes(32);
        var wrapped = ProtectedData.Protect(fresh, DpapiEntropy, DataProtectionScope.CurrentUser);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, wrapped);
        File.Move(tmp, path, overwrite: true);
        return new CryptoVault(fresh);
    }

    /// <summary>For tests: a vault with an explicit key and no DPAPI.</summary>
    internal static CryptoVault ForTesting(byte[] key) => new((byte[])key.Clone());

    // Layout: [version:1][nonce:12][tag:16][ciphertext:n]
    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, string context)
    {
        var blob = new byte[1 + NonceSize + TagSize + plaintext.Length];
        blob[0] = FormatVersion;
        var nonce = blob.AsSpan(1, NonceSize);
        var tag = blob.AsSpan(1 + NonceSize, TagSize);
        var cipher = blob.AsSpan(1 + NonceSize + TagSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_encKey, TagSize);
        aes.Encrypt(nonce, plaintext, cipher, tag, Encoding.UTF8.GetBytes(context));
        return blob;
    }

    public byte[] Decrypt(ReadOnlySpan<byte> blob, string context)
    {
        if (blob.Length < 1 + NonceSize + TagSize || blob[0] != FormatVersion)
            throw new CryptographicException("Unknown encrypted data format.");
        var nonce = blob.Slice(1, NonceSize);
        var tag = blob.Slice(1 + NonceSize, TagSize);
        var cipher = blob[(1 + NonceSize + TagSize)..];
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_encKey, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes(context));
        return plain;
    }

    public byte[]? EncryptString(string? value, string context) =>
        value is null ? null : Encrypt(Encoding.UTF8.GetBytes(value), context);

    public string? DecryptString(byte[]? blob, string context) =>
        blob is null ? null : Encoding.UTF8.GetString(Decrypt(blob, context));

    /// <summary>Keyed fingerprint: equal inputs give equal outputs, but it reveals nothing without the key.</summary>
    public string Fingerprint(string value) =>
        Convert.ToHexString(HMACSHA256.HashData(_macKey, Encoding.UTF8.GetBytes(value)));
}
