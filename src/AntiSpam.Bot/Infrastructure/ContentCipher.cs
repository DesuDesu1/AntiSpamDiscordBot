using System.Security.Cryptography;
using System.Text;

namespace AntiSpam.Bot.Infrastructure;

/// <summary>
/// AES-256-GCM encryption for the stored spam-message content, satisfying
/// Discord's developer-policy requirement to encrypt message content at rest.
/// Applied transparently by an EF value converter on <c>SpamIncident.Content</c>,
/// so the rest of the code works with plaintext.
///
/// The key is a base64-encoded 32-byte value supplied via configuration
/// (<c>Encryption:Key</c>) and injected from a Kubernetes secret in production.
/// </summary>
public static class ContentCipher
{
    private const string Prefix = "enc:v1:";
    private static byte[]? _key;

    public static void Init(string base64Key)
    {
        var key = Convert.FromBase64String(base64Key);
        if (key.Length != 32)
            throw new InvalidOperationException(
                "Encryption:Key must be a base64-encoded 32-byte (256-bit) key.");
        _key = key;
    }

    public static string Encrypt(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return plaintext;
        var key = _key ?? throw new InvalidOperationException("ContentCipher not initialised.");

        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];

        using var aes = new AesGcm(key, AesGcm.TagByteSizes.MaxSize);
        aes.Encrypt(nonce, plain, cipher, tag);

        // layout: nonce | tag | ciphertext
        var blob = new byte[nonce.Length + tag.Length + cipher.Length];
        Buffer.BlockCopy(nonce, 0, blob, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, blob, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipher, 0, blob, nonce.Length + tag.Length, cipher.Length);
        return Prefix + Convert.ToBase64String(blob);
    }

    public static string Decrypt(string stored)
    {
        // Rows written before encryption was enabled stay plaintext; they age out
        // within the 7-day retention window, so just pass them through.
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(Prefix, StringComparison.Ordinal))
            return stored;
        var key = _key ?? throw new InvalidOperationException("ContentCipher not initialised.");

        var blob = Convert.FromBase64String(stored[Prefix.Length..]);
        var nLen = AesGcm.NonceByteSizes.MaxSize;
        var tLen = AesGcm.TagByteSizes.MaxSize;
        var nonce = blob.AsSpan(0, nLen);
        var tag = blob.AsSpan(nLen, tLen);
        var cipher = blob.AsSpan(nLen + tLen);
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(key, tLen);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}
