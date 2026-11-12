using System.Security.Cryptography;
using Conversia.Application.Exceptions;
using Conversia.Infrastructure.Configuration;
using Conversia.Infrastructure.Security;

namespace Conversia.Infrastructure.UnitTests.Security;

/// <summary>AES-256-GCM protection of the secrets stored in the database: what is written cannot be read, altered, moved or opened with another key.</summary>
public class AesGcmSecretProtectorTests
{
    private const string Purpose = "whatsapp-channel-api-key:11111111-1111-1111-1111-111111111111";
    private const string Secret = "d360-key-for-tests-Ab3_.";

    private static AesGcmSecretProtector Protector(byte[]? key = null, string keyId = "1")
        => new(new SecretProtectionOptions(key ?? KeyA, keyId));

    private static readonly byte[] KeyA = RandomNumberGenerator.GetBytes(32);
    private static readonly byte[] KeyB = RandomNumberGenerator.GetBytes(32);

    [Theory]
    [InlineData(Secret)]
    [InlineData("x")]
    [InlineData("clave con espacios, tildes áéí y un emoji 🔑")]
    public void WhatIsProtected_ComesBackExactly(string secret)
    {
        var protector = Protector();

        Assert.Equal(secret, protector.Unprotect(protector.Protect(secret, Purpose), Purpose));
    }

    [Fact]
    public void TheStoredValue_NamesItsVersionAndKey_AndDoesNotContainTheSecret()
    {
        var stored = Protector(keyId: "2026-11").Protect(Secret, Purpose);

        Assert.StartsWith("v1.2026-11.", stored, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, stored);
        Assert.DoesNotContain(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Secret)).TrimEnd('='), stored);
        Assert.True(stored.Length <= Domain.Entities.WhatsAppChannel.ProviderApiKeyCiphertextMaxLength);
    }

    [Fact]
    public void TheLongestKeyTheDomainAccepts_FitsTheColumn()
    {
        var stored = Protector(keyId: new string('k', SecretProtectionOptions.KeyIdMaxLength))
            .Protect(new string('x', Domain.Entities.WhatsAppChannel.ProviderApiKeyMaxLength), Purpose);

        Assert.True(stored.Length <= Domain.Entities.WhatsAppChannel.ProviderApiKeyCiphertextMaxLength, $"{stored.Length} characters");
    }

    [Fact]
    public void TheSameSecretTwice_GivesDifferentStoredValues()
    {
        var protector = Protector();

        var values = Enumerable.Range(0, 50).Select(_ => protector.Protect(Secret, Purpose)).ToList();

        Assert.Equal(values.Count, values.Distinct().Count());
        Assert.All(values, v => Assert.Equal(Secret, protector.Unprotect(v, Purpose)));
    }

    [Fact]
    public void AnAlteredValue_IsRefused_WhereverItWasAltered()
    {
        var protector = Protector();
        var stored = protector.Protect(Secret, Purpose);
        var payloadStart = stored.LastIndexOf('.') + 1;

        for (var i = payloadStart; i < stored.Length; i++)
        {
            var altered = stored[..i] + (stored[i] == 'A' ? 'B' : 'A') + stored[(i + 1)..];
            if (altered == stored)
            {
                continue;
            }

            var failure = Assert.Throws<SecretProtectionException>(() => protector.Unprotect(altered, Purpose));
            Assert.Equal(SecretProtectionFailure.Unreadable, failure.Failure);
        }
    }

    [Fact]
    public void AnotherKey_CannotOpenTheValue()
    {
        var stored = Protector(KeyA).Protect(Secret, Purpose);

        var failure = Assert.Throws<SecretProtectionException>(() => Protector(KeyB).Unprotect(stored, Purpose));

        Assert.Equal(SecretProtectionFailure.Unreadable, failure.Failure);
        Assert.DoesNotContain(Secret, failure.ToString());
    }

    [Fact]
    public void AValueWrittenUnderAnotherKeyId_IsRefused_EvenWithTheSameKeyBytes()
    {
        var stored = Protector(KeyA, "old").Protect(Secret, Purpose);

        Assert.Throws<SecretProtectionException>(() => Protector(KeyA, "new").Unprotect(stored, Purpose));
    }

    [Fact]
    public void AValueMovedToAnotherPurpose_IsRefused()
    {
        var protector = Protector();
        var stored = protector.Protect(Secret, Purpose);

        Assert.Throws<SecretProtectionException>(() => protector.Unprotect(stored, Purpose.Replace('1', '2')));
    }

    [Theory]
    [InlineData("")]
    [InlineData("plaintext")]
    [InlineData("v1.1")]
    [InlineData("v1.1.")]
    [InlineData("v1.1.not base64 !!")]
    [InlineData("v1.1.AAAA")]
    [InlineData("v2.1.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("v1..AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void AMalformedValue_IsRefused_NotAnUnexpectedError(string stored)
        => Assert.Equal(SecretProtectionFailure.Unreadable, Assert.Throws<SecretProtectionException>(() => Protector().Unprotect(stored, Purpose)).Failure);

    [Fact]
    public void WithoutAKey_NothingIsProtectedOrRead_AndTheFailureSaysWhy()
    {
        var stored = Protector().Protect(Secret, Purpose);
        var unconfigured = new AesGcmSecretProtector(SecretProtectionOptions.NotConfigured);

        Assert.False(unconfigured.IsConfigured);
        Assert.True(Protector().IsConfigured);
        Assert.Equal(SecretProtectionFailure.NotConfigured, Assert.Throws<SecretProtectionException>(() => unconfigured.Protect(Secret, Purpose)).Failure);
        Assert.Equal(SecretProtectionFailure.NotConfigured, Assert.Throws<SecretProtectionException>(() => unconfigured.Unprotect(stored, Purpose)).Failure);
    }

    [Fact]
    public void TheOptions_NeverPrintTheKey()
    {
        var options = new SecretProtectionOptions(KeyA, "1");

        Assert.DoesNotContain(Convert.ToBase64String(KeyA), options.ToString());
        Assert.Contains("Configured = True", options.ToString());
    }
}
