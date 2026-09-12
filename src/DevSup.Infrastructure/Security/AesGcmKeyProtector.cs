using System.Security.Cryptography;
using System.Text;

namespace DevSup.Infrastructure.Security;

public interface IKeyProtector
{
    /// <summary>Encrypts a secret at rest. Output is a self-describing base64 string.</summary>
    string Protect(string plaintext);

    /// <summary>Decrypts a value previously produced by <see cref="Protect"/>. Throws on tampering.</summary>
    string Unprotect(string protectedValue);
}

/// <summary>
/// AES-256-GCM authenticated encryption for secrets at rest (AI keys, OAuth tokens).
/// The key is an SHA-256 digest of configured key material, so any >= 32-byte secret
/// works. Uses a fresh random nonce per value; tampered ciphertext fails with
/// CryptographicException. A production deployment should source the key from a KMS
/// or environment variable, never a checked-in constant.
/// </summary>
public sealed class AesGcmKeyProtector(string keyMaterial) : IKeyProtector
{
    private const string Prefix = "aesgcm:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key = SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial));

    public string Protect(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);

        var payload = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(payload, 0);
        tag.CopyTo(payload, NonceSize);
        cipher.CopyTo(payload, NonceSize + TagSize);

        return Prefix + Convert.ToBase64String(payload);
    }

    public string Unprotect(string protectedValue)
    {
        if (!protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new CryptographicException("Protected value has an unknown format.");
        }

        var payload = Convert.FromBase64String(protectedValue[Prefix.Length..]);
        if (payload.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Protected value is too short.");
        }

        var nonce = payload.AsSpan(0, NonceSize);
        var tag = payload.AsSpan(NonceSize, TagSize);
        var cipher = payload.AsSpan(NonceSize + TagSize);

        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);

        return Encoding.UTF8.GetString(plain);
    }
}