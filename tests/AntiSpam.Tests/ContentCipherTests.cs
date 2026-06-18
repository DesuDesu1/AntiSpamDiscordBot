using System.Security.Cryptography;
using AntiSpam.Bot.Infrastructure;

namespace AntiSpam.Tests;

public class ContentCipherTests
{
    public ContentCipherTests()
    {
        ContentCipher.Init(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    }

    [Fact]
    public void RoundTrips_Content()
    {
        const string plain = "buy cheap stuff at http://spam.example 🔥🔥🔥";
        var encrypted = ContentCipher.Encrypt(plain);

        Assert.StartsWith("enc:v1:", encrypted);
        Assert.DoesNotContain("spam.example", encrypted);
        Assert.Equal(plain, ContentCipher.Decrypt(encrypted));
    }

    [Fact]
    public void Decrypt_PassesThrough_LegacyPlaintext()
    {
        // Rows written before encryption was enabled have no prefix.
        Assert.Equal("old plaintext row", ContentCipher.Decrypt("old plaintext row"));
    }

    [Fact]
    public void Encrypt_IsNonDeterministic()
    {
        var a = ContentCipher.Encrypt("same text");
        var b = ContentCipher.Encrypt("same text");
        Assert.NotEqual(a, b);                                  // random nonce per call
        Assert.Equal("same text", ContentCipher.Decrypt(a));
        Assert.Equal("same text", ContentCipher.Decrypt(b));
    }

    [Fact]
    public void Empty_StaysEmpty()
    {
        Assert.Equal("", ContentCipher.Encrypt(""));
        Assert.Equal("", ContentCipher.Decrypt(""));
    }
}
