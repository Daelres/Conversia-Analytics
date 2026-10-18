using System.ComponentModel.DataAnnotations;
using Conversia.Tests.Shared;

namespace Conversia.Application.UnitTests.DTOs;

/// <summary>Runs DataAnnotations validation the way MVC does, with the request services carrying a clock the test sets.</summary>
internal static class FixedClockValidation
{
    /// <summary>2026-10-18: the latest date allowed is 2027-10-19 (today plus 366 days).</summary>
    public static readonly DateTime Today = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

    public static List<ValidationResult> Validate(object instance, DateTime? now = null)
    {
        var results = new List<ValidationResult>();
        var services = new Services(new FixedTimeProvider(now ?? Today));
        Validator.TryValidateObject(instance, new ValidationContext(instance, services, items: null), results, validateAllProperties: true);
        return results;
    }

    private sealed class Services(TimeProvider clock) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(TimeProvider) ? clock : null;
    }
}
