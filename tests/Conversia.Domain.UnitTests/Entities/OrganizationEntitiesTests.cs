using Conversia.Domain.Entities;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.UnitTests.Entities;

public class OrganizationEntitiesTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid BranchId = Guid.NewGuid();

    // ---- Branch ------------------------------------------------------------------------

    [Fact]
    public void Branch_Create_TrimsTheNameAndStartsActive()
    {
        var branch = Branch.Create(Tenant, "  Centro  ");

        Assert.Equal("Centro", branch.Name);
        Assert.Equal(Tenant, branch.TenantId);
        Assert.True(branch.IsActive);
        Assert.NotEqual(Guid.Empty, branch.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Cen\0tro")]
    public void Branch_Create_RejectsBlankOrNulCharacterNames(string name)
        => Assert.Throws<DomainException>(() => Branch.Create(Tenant, name));

    [Fact]
    public void Branch_Create_AcceptsTheMaximumLengthAndRejectsOneMore()
    {
        Assert.Equal(Branch.NameMaxLength, Branch.Create(Tenant, new string('a', Branch.NameMaxLength)).Name.Length);
        Assert.Throws<DomainException>(() => Branch.Create(Tenant, new string('a', Branch.NameMaxLength + 1)));
    }

    [Fact]
    public void Branch_Create_RejectsAnEmptyTenant()
        => Assert.Throws<DomainException>(() => Branch.Create(Guid.Empty, "Centro"));

    // ---- Advisor -----------------------------------------------------------------------

    [Fact]
    public void Advisor_Create_KeepsTheOptionalUserLink()
    {
        var userId = Guid.NewGuid();

        Assert.Equal(userId, Advisor.Create(Tenant, BranchId, "Ana", userId).UserId);
        Assert.Null(Advisor.Create(Tenant, BranchId, "Ana").UserId);
    }

    [Fact]
    public void Advisor_Create_RejectsMissingBranchEmptyUserIdAndBadNames()
    {
        Assert.Throws<DomainException>(() => Advisor.Create(Tenant, Guid.Empty, "Ana"));
        Assert.Throws<DomainException>(() => Advisor.Create(Tenant, BranchId, "Ana", Guid.Empty));
        Assert.Throws<DomainException>(() => Advisor.Create(Tenant, BranchId, " "));
        Assert.Throws<DomainException>(() => Advisor.Create(Tenant, BranchId, new string('a', Advisor.NameMaxLength + 1)));
    }

    // ---- WhatsAppChannel ---------------------------------------------------------------

    [Fact]
    public void Channel_Create_GeneratesAnUnguessableKeyAndASecretStoredOnlyAsAHash()
    {
        var (channel, secret) = WhatsAppChannel.Create(Tenant, BranchId, "+57 300 123 4567", " 1234567890 ");

        Assert.True(channel.ChannelKey.Length >= WhatsAppChannel.MinChannelKeyLength);
        Assert.True(WhatsAppChannel.IsWellFormedChannelKey(channel.ChannelKey));
        Assert.True(secret.Length >= 32);
        Assert.Equal(WhatsAppChannel.HashLength, channel.WebhookSecretHash.Length);
        Assert.Equal(WhatsAppChannel.HashSecret(secret), channel.WebhookSecretHash);
        Assert.DoesNotContain(secret, channel.ChannelKey);
        Assert.Equal("+57 300 123 4567", channel.DisplayPhoneNumber);
        Assert.Equal("1234567890", channel.ProviderPhoneNumberId);
        Assert.False(channel.IsActive); // registered switched off: the company turns it on within its quota
    }

    [Fact]
    public void Channel_Create_TwiceNeverRepeatsKeyOrSecret()
    {
        var (first, firstSecret) = WhatsAppChannel.Create(Tenant, BranchId, "3001234567");
        var (second, secondSecret) = WhatsAppChannel.Create(Tenant, BranchId, "3001234567");

        Assert.NotEqual(first.ChannelKey, second.ChannelKey);
        Assert.NotEqual(firstSecret, secondSecret);
    }

    [Fact]
    public void Channel_VerifyWebhookSecret_AcceptsOnlyTheGeneratedSecret()
    {
        var (channel, secret) = WhatsAppChannel.Create(Tenant, BranchId, "3001234567");

        Assert.True(channel.VerifyWebhookSecret(secret));
        Assert.False(channel.VerifyWebhookSecret(secret + "x"));
        Assert.False(channel.VerifyWebhookSecret(secret.ToUpperInvariant()));
        Assert.False(channel.VerifyWebhookSecret(string.Empty));
        Assert.False(channel.VerifyWebhookSecret(null));
    }

    [Fact]
    public void Channel_DeactivateAndActivate_ToggleIsActive()
    {
        var (channel, _) = WhatsAppChannel.Create(Tenant, BranchId, "3001234567");

        channel.Deactivate();
        Assert.False(channel.IsActive);
        channel.Activate();
        Assert.True(channel.IsActive);
    }

    [Theory]
    [InlineData("3001234567", true)]
    [InlineData("+57 (300) 123-4567", true)]
    [InlineData("123456", false)] // 6 digits
    [InlineData("1234567890123456", false)] // 16 digits
    [InlineData("300-ABC-4567", false)]
    [InlineData("", false)]
    public void DisplayPhoneNumber_RequiresSevenToFifteenDigitsAndOnlyPhoneSymbols(string phone, bool valid)
        => Assert.Equal(valid, WhatsAppChannel.IsValidDisplayPhoneNumber(phone));

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("abc_123-XYZ", true)]
    [InlineData("has space", false)]
    [InlineData("a\0b", false)]
    public void ProviderPhoneNumberId_IsOptionalAndRestrictedToSafeCharacters(string? id, bool valid)
        => Assert.Equal(valid, WhatsAppChannel.IsValidProviderPhoneNumberId(id));

    [Theory]
    [InlineData("short", false)]
    [InlineData("has/slash/has/slash/has/slash/has/slash/aaaa", false)]
    [InlineData(null, false)]
    public void ChannelKeyShape_RejectsShortOrUnsafeKeys(string? key, bool wellFormed)
        => Assert.Equal(wellFormed, WhatsAppChannel.IsWellFormedChannelKey(key));
}

public class TextRulesTests
{
    [Fact]
    public void Truncate_NeverSplitsASurrogatePair()
    {
        var text = new string('a', 9) + "😀" + "tail"; // the emoji takes units 9 and 10

        Assert.Equal(new string('a', 9), Conversia.Domain.Common.TextRules.Truncate(text, 10)); // would cut the pair in two
        Assert.Equal(new string('a', 9) + "😀", Conversia.Domain.Common.TextRules.Truncate(text, 11));
        Assert.Equal("short", Conversia.Domain.Common.TextRules.Truncate("short", 10));
        Assert.Equal(string.Empty, Conversia.Domain.Common.TextRules.Truncate("😀", 1));
    }
}
