using System.ComponentModel.DataAnnotations;
using Conversia.Application.DTOs.Auth;

namespace Conversia.Application.UnitTests.DTOs;

/// <summary>
/// Login must not apply the registration password policy: a user whose
/// stored hash predates the policy still has to be able to attempt a login,
/// and a wrong password is answered with a generic 401 further down the stack.
/// </summary>
public class LoginRequestPasswordTests
{
    private static List<ValidationResult> PasswordErrors(string? password)
    {
        var request = new LoginRequest { Email = "user@example.test", Password = password! };
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
        return results.Where(r => r.MemberNames.Contains(nameof(LoginRequest.Password))).ToList();
    }

    [Theory]
    [InlineData("a")]
    [InlineData("password")]
    [InlineData("ALLUPPERCASE")]
    [InlineData("1234567890")]
    public void Password_WeakButPresent_IsNotRejectedByComplexityRules(string password)
    {
        Assert.Empty(PasswordErrors(password));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Password_NullOrEmpty_IsRejected(string? password)
    {
        Assert.NotEmpty(PasswordErrors(password));
    }
}
