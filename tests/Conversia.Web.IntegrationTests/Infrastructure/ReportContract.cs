using System.ComponentModel.DataAnnotations;
using Conversia.Application.DTOs.Diagnostics;
using Conversia.Application.ErrorLogging;

namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>What every suite checks about a report the Web sends: the API's own validation accepts it, and the secrets are gone.</summary>
internal static class ReportContract
{
    /// <summary>The report passes the same DataAnnotations validation the API's endpoint applies to its body.</summary>
    public static void AssertValidForTheApi(ReportErrorRequest body)
    {
        var problems = new List<ValidationResult>();
        var valid = Validator.TryValidateObject(body, new ValidationContext(body), problems, validateAllProperties: true);
        Assert.True(valid, "The API would refuse this report: " + string.Join("; ", problems.Select(p => p.ErrorMessage)));
    }

    /// <summary>The message or the details of the report carry one of the sanitizer's marks where the secret was.</summary>
    public static void AssertRedactionMarked(ReportErrorRequest body)
    {
        var marks = new[] { ErrorSanitizer.Redacted, ErrorSanitizer.RedactedJwt };
        var text = body.ExceptionMessage + body.ExceptionDetails;
        Assert.True(marks.Any(text.Contains), "The secret was removed but nothing marks where: no redaction mark in the message or details.");
    }

    /// <summary>An exception message with a secret in it, and the secret that must not leave the Web. One list for every suite.</summary>
    public static TheoryData<string, string> SecretsInMessages => new()
    {
        { "Login failed for Password=Sup3rSecretPw9;Server=db", "Sup3rSecretPw9" },
        { "The call failed: Authorization: Bearer abcDEF123456xyz", "abcDEF123456xyz" },
        { $"The token {FakeJwt.Create(Guid.NewGuid(), Guid.NewGuid())} was refused", "eyJ" },
        { "Server=db;User Id=sa;Password=Hunter2Hunter2;Encrypt=true", "Hunter2Hunter2" },
        { "api_key=0123456789abcdefSECRET failed", "0123456789abcdefSECRET" },
        { """{"password":"p@ssw0rd-json","user":"ana"}""", "p@ssw0rd-json" },
    };
}
