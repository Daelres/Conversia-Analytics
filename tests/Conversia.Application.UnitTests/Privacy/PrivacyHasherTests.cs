using System.Security.Cryptography;
using System.Text;
using Conversia.Application.Privacy;

namespace Conversia.Application.UnitTests.Privacy;

/// <summary>The keyed hashes the platform keeps of personal data it must still recognise (docs/USERS_TENANTS_PERMISSIONS.md, "Contact suppression").</summary>
public class PrivacyHasherTests
{
    private static readonly byte[] KeyA = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] KeyB = Enumerable.Range(101, 32).Select(i => (byte)i).ToArray();
    private static readonly Guid Tenant = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void TheHash_IsAnHmacWithTheKey_NotAPlainHashAnyoneCouldRecompute()
    {
        var hasher = new PrivacyHasher(new PrivacyHashKeys("k1", KeyA));

        var hash = hasher.ContactSuppression(Tenant, "573001234567");

        var input = Encoding.UTF8.GetBytes($"{PrivacyHasher.ContactSuppressionPurpose}\n{Tenant:N}:573001234567");
        Assert.Equal(Convert.ToHexStringLower(HMACSHA256.HashData(KeyA, input)), hash.Hex);
        Assert.NotEqual(Convert.ToHexStringLower(SHA256.HashData(input)), hash.Hex);
        Assert.Equal("k1", hash.KeyId);
    }

    [Fact]
    public void AnotherKey_AnotherCompany_OrAnotherPurpose_GiveAnotherHash()
    {
        var a = new PrivacyHasher(new PrivacyHashKeys("k1", KeyA));
        var b = new PrivacyHasher(new PrivacyHashKeys("k1", KeyB));

        Assert.Equal(a.ContactSuppression(Tenant, "573001234567"), a.ContactSuppression(Tenant, "573001234567"));
        Assert.NotEqual(a.ContactSuppression(Tenant, "573001234567").Hex, b.ContactSuppression(Tenant, "573001234567").Hex);
        Assert.NotEqual(a.ContactSuppression(Tenant, "573001234567").Hex, a.ContactSuppression(Guid.NewGuid(), "573001234567").Hex);
        Assert.NotEqual(a.Hash(PrivacyHasher.TenantNamePurpose, "X").Hex, a.Hash(PrivacyHasher.ContactSuppressionPurpose, "X").Hex);
    }

    [Fact]
    public void TheCompanyName_IsTrimmedAndCaseInsensitive()
    {
        var hasher = new PrivacyHasher(new PrivacyHashKeys("k1", KeyA));

        Assert.Equal(hasher.TenantName("Clínica Demo"), hasher.TenantName("  clínica demo "));
        Assert.NotEqual(hasher.TenantName("Clínica Demo"), hasher.TenantName("Clínica Demo 2"));
    }

    [Fact]
    public void AfterARotation_TheOldHashesAreStillCandidates_UnderTheirOwnKeyId()
    {
        var before = new PrivacyHasher(new PrivacyHashKeys("k1", KeyA));
        var after = new PrivacyHasher(new PrivacyHashKeys("k2", KeyB, new Dictionary<string, byte[]> { ["k1"] = KeyA }));

        var old = before.ContactSuppression(Tenant, "573001234567");
        var candidates = after.ContactSuppressionCandidates(Tenant, "573001234567");

        Assert.Equal("k2", after.ContactSuppression(Tenant, "573001234567").KeyId);
        Assert.Contains(old, candidates);
        Assert.Equal(2, candidates.Count);
    }

    [Theory]
    [InlineData("k1", 31)]
    [InlineData("", 32)]
    [InlineData("has space", 32)]
    public void AKey_IsAtLeast32Bytes_WithAPlainId(string id, int bytes)
        => Assert.Throws<ArgumentException>(() => new PrivacyHashKeys(id, new byte[bytes]));

    [Fact]
    public void TheDevelopmentFallback_IsMarked()
    {
        var keys = PrivacyHashKeys.DevelopmentFallback();

        Assert.True(keys.IsDevelopmentFallback);
        Assert.Equal("dev", keys.CurrentId);
        Assert.False(new PrivacyHashKeys("k1", KeyA).IsDevelopmentFallback);
    }
}
