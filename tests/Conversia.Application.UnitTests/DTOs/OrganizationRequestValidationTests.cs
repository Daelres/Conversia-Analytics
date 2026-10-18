using System.ComponentModel.DataAnnotations;
using Conversia.Application.DTOs.Organization;
using Conversia.Domain.Entities;

namespace Conversia.Application.UnitTests.DTOs;

/// <summary>DataAnnotations of the organization requests: per-field Spanish errors, in agreement with the domain rules.</summary>
public class OrganizationRequestValidationTests
{
    private static List<ValidationResult> Validate(object request)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
        return results;
    }

    private static string[] Members(object request) => Validate(request).SelectMany(r => r.MemberNames).ToArray();

    [Theory]
    [InlineData("Centro", true)]
    [InlineData("  Centro  ", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("Cen\0tro", false)]
    public void CreateBranch_NameFollowsTheDomainRule(string name, bool valid)
    {
        Assert.Equal(valid, Validate(new CreateBranchRequest { Name = name }).Count == 0);
        Assert.Equal(valid, Conversia.Domain.Common.TextRules.IsValidRequired(name, Branch.NameMaxLength));
    }

    [Fact]
    public void CreateBranch_NameLimitIsTheDomainLimit()
    {
        Assert.Empty(Validate(new CreateBranchRequest { Name = new string('a', Branch.NameMaxLength) }));
        var error = Assert.Single(Validate(new CreateBranchRequest { Name = new string('a', Branch.NameMaxLength + 1) }));
        Assert.Contains("Nombre", error.ErrorMessage);
        Assert.Contains(Branch.NameMaxLength.ToString(), error.ErrorMessage);
    }

    [Fact]
    public void CreateAdvisor_RequiresNameAndBranch_UserIsOptional()
    {
        Assert.Empty(Validate(new CreateAdvisorRequest { Name = "Ana", BranchId = Guid.NewGuid() }));
        Assert.Contains("BranchId", Members(new CreateAdvisorRequest { Name = "Ana" }));
        Assert.Contains("Name", Members(new CreateAdvisorRequest { Name = " ", BranchId = Guid.NewGuid() }));
    }

    [Theory]
    [InlineData("+57 300 123 4567", true)]
    [InlineData("12345", false)]
    [InlineData("abc", false)]
    [InlineData("", false)]
    public void CreateChannel_DisplayPhoneNumberFollowsTheDomainRule(string phone, bool valid)
    {
        var request = new CreateChannelRequest { BranchId = Guid.NewGuid(), DisplayPhoneNumber = phone };

        Assert.Equal(valid, Validate(request).Count == 0);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("1234567890", true)]
    [InlineData("two words", false)]
    public void CreateChannel_ProviderPhoneNumberIdIsOptionalButRestricted(string? id, bool valid)
    {
        var request = new CreateChannelRequest { BranchId = Guid.NewGuid(), DisplayPhoneNumber = "3001234567", ProviderPhoneNumberId = id };

        Assert.Equal(valid, Validate(request).Count == 0);
    }

    [Fact]
    public void CreateChannel_RequiresTheBranch()
        => Assert.Contains("BranchId", Members(new CreateChannelRequest { DisplayPhoneNumber = "3001234567" }));

    [Fact]
    public void ChannelResponse_NeverCarriesTheHash_AndOnlyTheCreateResponseCarriesTheSecret()
    {
        var (channel, secret) = WhatsAppChannel.Create(Guid.NewGuid(), Guid.NewGuid(), "3001234567");

        var listed = ChannelResponse.From(channel);
        var created = CreateChannelResponse.From(channel, secret);

        Assert.DoesNotContain(typeof(ChannelResponse).GetProperties(), p => p.Name.Contains("Hash") || p.Name.Contains("Secret"));
        Assert.Equal(secret, created.WebhookSecret);
        Assert.DoesNotContain(typeof(CreateChannelResponse).GetProperties(), p => p.Name.Contains("Hash"));
        Assert.Equal($"/api/webhooks/360dialog/{channel.ChannelKey}", listed.WebhookPath);
    }
}
