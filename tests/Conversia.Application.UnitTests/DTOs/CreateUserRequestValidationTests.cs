using System.ComponentModel.DataAnnotations;
using Conversia.Application.DTOs.Users;

namespace Conversia.Application.UnitTests.DTOs;

/// <summary>
/// Validation of <see cref="CreateUserRequest"/> (body of <c>POST /api/users</c>):
/// the same password policy and email rules as tenant registration, checked
/// through DataAnnotations — the mechanism ASP.NET Core model binding uses.
/// Acceptance cases are always paired with a rejected twin so that a request
/// type without any validation cannot satisfy them.
/// </summary>
public class CreateUserRequestValidationTests
{
    private const string EmailMember = nameof(CreateUserRequest.Email);
    private const string PasswordMember = nameof(CreateUserRequest.Password);
    private const string ValidEmail = "member@example.test";
    private const string ValidPassword = "Sup3r-Secret!";

    private static List<ValidationResult> ErrorsFor(string? email, string? password, string member)
    {
        var request = new CreateUserRequest { Email = email!, Password = password! };
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
        return results.Where(r => r.MemberNames.Contains(member)).ToList();
    }

    private static List<ValidationResult> PasswordErrors(string? password) => ErrorsFor(ValidEmail, password, PasswordMember);

    private static List<ValidationResult> EmailErrors(string? email) => ErrorsFor(email, ValidPassword, EmailMember);

    private static string EmailOfLength(int length)
    {
        const string domain = "@example.test";
        return new string('a', length - domain.Length) + domain;
    }

    [Fact]
    public void ValidRequest_PassesWhileTheSameRequestWithEitherFieldBroken_IsRejectedOnThatField()
    {
        Assert.Empty(ErrorsFor(ValidEmail, ValidPassword, EmailMember));
        Assert.Empty(ErrorsFor(ValidEmail, ValidPassword, PasswordMember));

        Assert.NotEmpty(ErrorsFor("not-an-email", ValidPassword, EmailMember));
        Assert.NotEmpty(ErrorsFor(ValidEmail, "weak", PasswordMember));
    }

    [Theory]
    [InlineData("Abcdefghij1k", "Abcdefghi1k")]                                // 12 accepted / 11 rejected
    [InlineData("Aa1xxxxxxxxxxx", "Abcdefghi1k")]                              // 14 accepted / 11 rejected
    public void Password_MinimumLengthBoundary_Is12Characters(string accepted, string rejected)
    {
        Assert.Empty(PasswordErrors(accepted));
        Assert.NotEmpty(PasswordErrors(rejected));
    }

    [Fact]
    public void Password_MaximumLengthBoundary_Is128Characters()
    {
        Assert.Empty(PasswordErrors("Aa1" + new string('x', 125)));
        Assert.NotEmpty(PasswordErrors("Aa1" + new string('x', 126)));
    }

    [Theory]
    [MemberData(nameof(RegisterTenantRequestPasswordPolicyTests.NonCompliantPasswords),
        MemberType = typeof(RegisterTenantRequestPasswordPolicyTests))]
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

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("missing-at-sign.example.test")]
    [InlineData("two@@example.test")]
    [InlineData("@example.test")]
    [InlineData("member@")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Email_MalformedOrMissing_IsRejectedWithAnErrorOnTheEmailField(string? email)
    {
        var errors = EmailErrors(email);

        Assert.NotEmpty(errors);
        Assert.All(errors, e => Assert.False(string.IsNullOrWhiteSpace(e.ErrorMessage)));
    }

    [Fact]
    public void Email_MaximumLengthBoundary_Is256Characters()
    {
        Assert.Empty(EmailErrors(EmailOfLength(256)));
        Assert.NotEmpty(EmailErrors(EmailOfLength(257)));
    }

    [Fact]
    public void Email_AndPassword_BothInvalid_ReportBothFieldErrors()
    {
        var request = new CreateUserRequest { Email = "not-an-email", Password = "weak" };
        var results = new List<ValidationResult>();

        Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);

        var failingMembers = results.SelectMany(r => r.MemberNames).ToHashSet();
        Assert.Contains(EmailMember, failingMembers);
        Assert.Contains(PasswordMember, failingMembers);
    }
}
