using System.ComponentModel.DataAnnotations;
using Conversia.Application.DTOs.Auth;

namespace Conversia.Application.UnitTests.DTOs;

/// <summary>
/// Password policy for tenant registration: 12–128 characters with at least
/// one uppercase letter, one lowercase letter and one digit. Validated
/// through DataAnnotations, the same mechanism ASP.NET Core model binding uses.
/// </summary>
public class RegisterTenantRequestPasswordPolicyTests
{
    private const string PasswordMember = nameof(RegisterTenantRequest.Password);

    private static List<ValidationResult> Validate(object request)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
        return results;
    }

    private static List<ValidationResult> PasswordErrors(string? password)
    {
        var request = new RegisterTenantRequest
        {
            TenantName = "Acme Corp",
            Email = "owner@example.test",
            Password = password!
        };

        return Validate(request)
            .Where(r => r.MemberNames.Contains(PasswordMember))
            .ToList();
    }

    public static TheoryData<string> CompliantPasswords => new()
    {
        "Abcdefghij1k",                       // exactly 12 characters
        "Sup3r-Secret!",
        "correct HORSE battery 9 staple",     // spaces are allowed
        "Ñandú-secreto-9",                    // non-ASCII uppercase letter counts as uppercase
        "Ünïcödé-Pässw0rd",
        "Aa1" + new string('x', 125),         // exactly 128 characters
    };

    [Theory]
    [MemberData(nameof(CompliantPasswords))]
    public void Password_MeetingThePolicy_IsAccepted(string password)
    {
        Assert.Empty(PasswordErrors(password));
    }

    public static TheoryData<string, string> NonCompliantPasswords => new()
    {
        { "Abcdefghi1k", "11 characters (one below the minimum)" },
        { "Aa1", "far too short" },
        { "Aa1" + new string('x', 126), "129 characters (one above the maximum)" },
        { "Aa1" + new string('x', 9_997), "10 000 characters" },
        { "abcdefghijk1", "no uppercase letter" },
        { "ABCDEFGHIJK1", "no lowercase letter" },
        { "Abcdefghijkl", "no digit" },
        { "ÜÜÜÜÜÜÜÜ1111", "non-ASCII uppercase only, no lowercase" },
        { "密码密码密码密码密码密码1A", "caseless letters provide no lowercase letter" },
        { "!!!!!!!!!!!!!!!!", "symbols only" },
        { "            ", "12 spaces (whitespace only)" },
        { "\t\t\t\t\t\t\t\t\t\t\t\t", "12 tabs (whitespace only)" },
    };

    [Theory]
    [MemberData(nameof(NonCompliantPasswords))]
    public void Password_ViolatingThePolicy_IsRejectedWithAnErrorOnThePasswordField(string password, string reason)
    {
        var errors = PasswordErrors(password);

        Assert.True(errors.Count > 0, $"Password with {reason} should be rejected.");
        Assert.All(errors, e => Assert.False(string.IsNullOrWhiteSpace(e.ErrorMessage)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Password_NullEmptyOrBlank_IsRejectedWithAnErrorOnThePasswordField(string? password)
    {
        Assert.NotEmpty(PasswordErrors(password));
    }

    [Fact]
    public void Password_ErrorMessages_NeverEchoTheSubmittedPassword()
    {
        const string weakPassword = "hunter2-weak";

        var errors = PasswordErrors(weakPassword);

        Assert.NotEmpty(errors);
        Assert.All(errors, e => Assert.DoesNotContain(weakPassword, e.ErrorMessage));
    }

    [Fact]
    public void FullyValidRequest_PassesValidation()
    {
        var request = new RegisterTenantRequest
        {
            TenantName = "Acme Corp",
            Email = "owner@example.test",
            Password = "Sup3r-Secret!"
        };

        Assert.Empty(Validate(request));
    }

    [Fact]
    public void WeakPassword_DoesNotHideOtherFieldErrors()
    {
        var request = new RegisterTenantRequest { TenantName = "", Email = "not-an-email", Password = "weak" };

        var failingMembers = Validate(request).SelectMany(r => r.MemberNames).ToHashSet();

        Assert.Contains(PasswordMember, failingMembers);
        Assert.Contains(nameof(RegisterTenantRequest.TenantName), failingMembers);
        Assert.Contains(nameof(RegisterTenantRequest.Email), failingMembers);
    }
}
