using System.Security.Cryptography;
using DevSup.Infrastructure.Security;
using Xunit;

namespace DevSup.Tests;

public sealed class KeyProtectorTests
{
    private const string Key = "test-key-material-for-aes-gcm-round-trip";

    [Fact]
    public void Protect_ThenUnprotect_RoundTrips()
    {
        var protector = new AesGcmKeyProtector(Key);
        const string secret = "ghp_super_secret_token_12345";

        var protectedValue = protector.Protect(secret);
        var restored = protector.Unprotect(protectedValue);

        Assert.NotEqual(secret, protectedValue);
        Assert.StartsWith("aesgcm:", protectedValue);
        Assert.Equal(secret, restored);
    }

    [Fact]
    public void Protect_ProducesUniqueCiphertexts_ForSameInput()
    {
        var protector = new AesGcmKeyProtector(Key);

        var first = protector.Protect("same-secret");
        var second = protector.Protect("same-secret");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Unprotect_TamperedValue_Throws()
    {
        var protector = new AesGcmKeyProtector(Key);
        var protectedValue = protector.Protect("original");

        var bytes = Convert.FromBase64String(protectedValue["aesgcm:".Length..]);
        bytes[^1] ^= 0xFF;

        var tampered = "aesgcm:" + Convert.ToBase64String(bytes);

        Assert.Throws<AuthenticationTagMismatchException>(() => protector.Unprotect(tampered));
    }

    [Fact]
    public void Unprotect_WithWrongKey_Throws()
    {
        var protector = new AesGcmKeyProtector(Key);
        var protectedValue = protector.Protect("original");

        var otherProtector = new AesGcmKeyProtector("a-completely-different-key");

        Assert.Throws<AuthenticationTagMismatchException>(() => otherProtector.Unprotect(protectedValue));
    }

    [Fact]
    public void Unprotect_UnknownFormat_Throws()
    {
        var protector = new AesGcmKeyProtector(Key);
        Assert.Throws<CryptographicException>(() => protector.Unprotect("plaintext"));
    }
}