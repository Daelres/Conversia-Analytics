using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Conversia.Application.ErrorLogging;

namespace Conversia.Application.UnitTests.ErrorLogging;

/// <summary>
/// The fingerprint is the first 16 lowercase hex characters of SHA-256 over <c>ExceptionType + "|" + topApplicationFrame</c>,
/// where the top application frame is the first <c>Namespace.Type.Method</c> of the stack inside <c>Conversia</c> (or the
/// first frame when there is none). Messages, line numbers, file paths, argument lists and generic arity must not matter.
/// </summary>
public class ErrorFingerprintTests
{
    private const string InvalidOperation = "System.InvalidOperationException";

    private const string UpdateFrame = "Conversia.Application.Services.OrganizationService.UpdateAsync";

    private static string Expected(string type, string frame)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(type + "|" + frame))).ToLowerInvariant()[..16];

    private static string Details(string message, string frames, string type = InvalidOperation)
        => $"{type}: {message}\n{frames}";

    private const string UpdateStack =
        "   at Conversia.Application.Services.OrganizationService.UpdateAsync(Guid id, UpdateBranchRequest request) in /src/Services/OrganizationService.cs:line 42\n"
        + "   at Conversia.API.Controllers.BranchesController.UpdateBranch(Guid id, UpdateBranchRequest request, CancellationToken cancellationToken) in /src/Controllers/BranchesController.cs:line 55";

    [Fact]
    public void The_fingerprint_is_sixteen_lowercase_hex_characters()
    {
        var fingerprint = ErrorFingerprint.Compute(InvalidOperation, Details("boom", UpdateStack));

        Assert.Matches(new Regex("^[0-9a-f]{16}$"), fingerprint);
    }

    [Fact]
    public void It_is_the_sha256_of_the_type_a_pipe_and_the_top_application_frame_without_arguments_or_line_numbers()
    {
        var fingerprint = ErrorFingerprint.Compute(InvalidOperation, Details("boom", UpdateStack));

        Assert.Equal(Expected(InvalidOperation, UpdateFrame), fingerprint);
    }

    [Fact]
    public void Messages_line_numbers_file_paths_and_argument_lists_do_not_change_it()
    {
        var baseline = ErrorFingerprint.Compute(InvalidOperation, Details("boom", UpdateStack));

        var otherEverything = ErrorFingerprint.Compute(
            InvalidOperation,
            Details(
                "a completely different message, with Password=x and 12345",
                "   at Conversia.Application.Services.OrganizationService.UpdateAsync(Guid id) in /other/checkout/deep/OrganizationService.cs:line 999\n"
                + "   at Conversia.API.Controllers.BranchesController.Elsewhere() in C:\\build\\BranchesController.cs:line 1"));

        var noFileInformation = ErrorFingerprint.Compute(
            InvalidOperation,
            Details("x", "   at Conversia.Application.Services.OrganizationService.UpdateAsync(Guid id, UpdateBranchRequest request)"));

        var withStackOffset = ErrorFingerprint.Compute(
            InvalidOperation,
            Details("x", "   at Conversia.Application.Services.OrganizationService.UpdateAsync(Guid id, UpdateBranchRequest request) + 0x2a"));

        Assert.Equal(baseline, otherEverything);
        Assert.Equal(baseline, noFileInformation);
        Assert.Equal(baseline, withStackOffset);
    }

    [Fact]
    public void Framework_frames_above_the_application_frame_are_skipped()
    {
        var plain = ErrorFingerprint.Compute(InvalidOperation, Details("boom", UpdateStack));

        var withFrameworkFrames = ErrorFingerprint.Compute(
            InvalidOperation,
            Details(
                "boom",
                "   at System.Linq.ThrowHelper.ThrowNoElementsException()\n"
                + "   at System.Linq.Enumerable.First[TSource](IEnumerable`1 source)\n"
                + UpdateStack));

        Assert.Equal(plain, withFrameworkFrames);
    }

    [Fact]
    public void A_different_top_application_frame_gives_a_different_fingerprint()
    {
        var update = ErrorFingerprint.Compute(InvalidOperation, Details("boom", UpdateStack));
        var delete = ErrorFingerprint.Compute(
            InvalidOperation,
            Details("boom", "   at Conversia.Application.Services.OrganizationService.DeleteAsync(Guid id) in /src/OrganizationService.cs:line 42"));
        var otherType = ErrorFingerprint.Compute(
            InvalidOperation,
            Details("boom", "   at Conversia.Application.Services.OtherService.UpdateAsync(Guid id) in /src/OrganizationService.cs:line 42"));

        Assert.NotEqual(update, delete);
        Assert.NotEqual(update, otherType);
        Assert.NotEqual(delete, otherType);
    }

    [Fact]
    public void Method_names_that_differ_only_by_a_digit_are_different_frames()
    {
        var one = ErrorFingerprint.Compute(InvalidOperation, Details("x", "   at Conversia.Application.Foo.Handle1()"));
        var two = ErrorFingerprint.Compute(InvalidOperation, Details("x", "   at Conversia.Application.Foo.Handle2()"));

        Assert.NotEqual(one, two);
    }

    [Fact]
    public void A_different_exception_type_gives_a_different_fingerprint_for_the_same_frame()
    {
        var invalidOperation = ErrorFingerprint.Compute(InvalidOperation, Details("boom", UpdateStack));
        var argument = ErrorFingerprint.Compute("System.ArgumentException", Details("boom", UpdateStack, "System.ArgumentException"));

        Assert.NotEqual(invalidOperation, argument);
        Assert.Equal(Expected("System.ArgumentException", UpdateFrame), argument);
    }

    [Fact]
    public void Generic_arity_in_the_type_or_the_method_is_noise()
    {
        var plain = ErrorFingerprint.Compute(
            InvalidOperation, Details("x", "   at Conversia.Application.Repo.Handler.Run(Guid id)"));
        var genericType = ErrorFingerprint.Compute(
            InvalidOperation, Details("x", "   at Conversia.Application.Repo.Handler`1.Run(Guid id)"));
        var otherArity = ErrorFingerprint.Compute(
            InvalidOperation, Details("x", "   at Conversia.Application.Repo.Handler`2.Run(Guid id)"));
        var genericMethod = ErrorFingerprint.Compute(
            InvalidOperation, Details("x", "   at Conversia.Application.Repo.Handler.Run[T](T id)"));
        var otherMethodArity = ErrorFingerprint.Compute(
            InvalidOperation, Details("x", "   at Conversia.Application.Repo.Handler.Run[T,U](T id, U other)"));

        Assert.Equal(plain, genericType);
        Assert.Equal(plain, otherArity);
        Assert.Equal(plain, genericMethod);
        Assert.Equal(plain, otherMethodArity);
    }

    [Fact]
    public void Without_an_application_frame_the_first_frame_is_used()
    {
        const string details =
            "System.IO.IOException: disk full\n"
            + "   at System.IO.File.ReadAllText(String path) in /_/File.cs:line 12\n"
            + "   at System.Threading.Tasks.Task.Run()";

        var fingerprint = ErrorFingerprint.Compute("System.IO.IOException", details);

        Assert.Equal(Expected("System.IO.IOException", "System.IO.File.ReadAllText"), fingerprint);
    }

    [Fact]
    public void Without_an_application_frame_the_fingerprint_still_ignores_line_numbers_and_messages()
    {
        var one = ErrorFingerprint.Compute(
            "System.IO.IOException", "System.IO.IOException: a\n   at System.IO.File.ReadAllText(String path) in /_/File.cs:line 12");
        var two = ErrorFingerprint.Compute(
            "System.IO.IOException", "System.IO.IOException: b\n   at System.IO.File.ReadAllText(String path, Encoding e) in /x/File.cs:line 99");
        var other = ErrorFingerprint.Compute(
            "System.IO.IOException", "System.IO.IOException: a\n   at System.IO.File.WriteAllText(String path) in /_/File.cs:line 12");

        Assert.Equal(one, two);
        Assert.NotEqual(one, other);
    }

    [Fact]
    public void The_first_application_frame_in_the_text_wins_even_when_it_belongs_to_an_inner_exception()
    {
        const string details =
            "System.InvalidOperationException: outer\n"
            + " ---> System.ArgumentException: inner\n"
            + "   at System.String.Foo()\n"
            + "   at Conversia.Application.Inner.Thrower.Run()\n"
            + "   --- End of inner exception stack trace ---\n"
            + "   at Conversia.API.Outer.Caller.Run()";

        var fingerprint = ErrorFingerprint.Compute(InvalidOperation, details);

        Assert.Equal(Expected(InvalidOperation, "Conversia.Application.Inner.Thrower.Run"), fingerprint);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n")]
    [InlineData("System.InvalidOperationException: boom")]
    [InlineData("not a stack trace at all")]
    [InlineData("   at ")]
    [InlineData("   at (((")]
    public void Missing_or_unparsable_stack_text_gives_a_well_formed_stable_fingerprint(string? details)
    {
        var first = Record.Exception(() => ErrorFingerprint.Compute(InvalidOperation, details));
        Assert.Null(first);

        var one = ErrorFingerprint.Compute(InvalidOperation, details);
        var again = ErrorFingerprint.Compute(InvalidOperation, details);
        var otherType = ErrorFingerprint.Compute("System.ArgumentException", details);

        Assert.Matches(new Regex("^[0-9a-f]{16}$"), one);
        Assert.Equal(one, again);
        Assert.NotEqual(one, otherType);
    }

    [Fact]
    public void Without_any_frame_the_message_does_not_influence_the_fingerprint()
    {
        var one = ErrorFingerprint.Compute(InvalidOperation, "System.InvalidOperationException: first message");
        var two = ErrorFingerprint.Compute(InvalidOperation, "System.InvalidOperationException: a different message");

        Assert.Equal(one, two);
    }

    [Fact]
    public void An_empty_exception_type_is_handled()
    {
        var failure = Record.Exception(() => ErrorFingerprint.Compute(string.Empty, Details("x", UpdateStack, string.Empty)));

        Assert.Null(failure);
        Assert.Matches(new Regex("^[0-9a-f]{16}$"), ErrorFingerprint.Compute(string.Empty, null));
    }

    [Fact]
    public void A_huge_stack_text_is_handled_quickly_and_still_finds_the_first_frame()
    {
        var frames = new StringBuilder();
        for (var i = 0; i < 200_000; i++)
        {
            frames.Append("   at System.Fake.Frame").Append(i).Append(".Run()\n");
        }

        frames.Append("   at Conversia.Application.Deep.Frame.Run()");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var fingerprint = ErrorFingerprint.Compute(InvalidOperation, Details("x", frames.ToString()));
        watch.Stop();

        Assert.Equal(Expected(InvalidOperation, "Conversia.Application.Deep.Frame.Run"), fingerprint);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Took {watch.Elapsed}");
    }
}
