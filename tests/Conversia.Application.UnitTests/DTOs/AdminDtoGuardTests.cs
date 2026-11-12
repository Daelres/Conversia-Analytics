using System.Reflection;
using System.Text.Json;
using Conversia.Application.DTOs.Admin;
using Conversia.Domain.Enums;

namespace Conversia.Application.UnitTests.DTOs;

/// <summary>
/// The platform administrator never sees a company's private content (docs/PLATFORM_ADMIN.md, "Security model"). Every answer of the admin API
/// is a <c>*Response</c> of <c>Conversia.Application.DTOs.Admin</c>; this pins that they hold scalars only (or other admin answers, or lists of
/// them) and no member whose name says text, payload, contact or conversation. A new member that breaks the rule fails here until somebody
/// decides, in the open, that it is legitimate.
/// </summary>
public class AdminDtoGuardTests
{
    private static readonly HashSet<Type> Scalars =
    [
        typeof(string), typeof(bool), typeof(int), typeof(long), typeof(decimal), typeof(Guid), typeof(DateTime), typeof(TenantStatus),
        typeof(JsonElement), // the audit entry's small JSON of old and new values (ids, statuses, numbers, the administrator's reason)
    ];

    // Names that would mean private content of a company or of its customers.
    private static readonly string[] ForbiddenNames =
    [
        "Text", "Body", "Payload", "RawPayload", "WaId", "ContactName", "ContactId", "ConversationId", "CustomerName", "PhoneDigits", "PhoneNumber",
        "ExceptionMessage", "ExceptionDetails", "Query", "Path", "LastError", "ProviderApiKey", "WebhookSecret", "ChannelKey", "PasswordHash",
    ];

    private static IEnumerable<Type> Responses()
        => typeof(TenantSummaryResponse).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(TenantSummaryResponse).Namespace && t.IsClass && t.Name.EndsWith("Response", StringComparison.Ordinal));

    private static bool IsAllowed(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (Scalars.Contains(underlying))
        {
            return true;
        }

        if (underlying.IsGenericType && underlying.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            return IsAllowed(underlying.GetGenericArguments()[0]);
        }

        return underlying.Namespace == typeof(TenantSummaryResponse).Namespace && underlying.Name.EndsWith("Response", StringComparison.Ordinal);
    }

    [Fact]
    public void TheGuard_SeesTheNewAnswers()
    {
        var names = Responses().Select(t => t.Name).ToHashSet();

        Assert.Superset(
            new HashSet<string>
            {
                nameof(TenantSummaryResponse), nameof(TenantDetailResponse), nameof(TenantLimitsResponse), nameof(LineLicenseResponse),
                nameof(PlatformOverviewResponse), nameof(TenantMonitoringResponse), nameof(DailyActivityResponse), nameof(ErrorGroupResponse),
            },
            names);
    }

    [Fact]
    public void EveryAdminAnswer_HoldsScalarsOnly()
    {
        var offending = Responses()
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => (Type: t, Property: p)))
            .Where(x => !IsAllowed(x.Property.PropertyType))
            .Select(x => $"{x.Type.Name}.{x.Property.Name}: {x.Property.PropertyType}")
            .ToList();

        Assert.Empty(offending);
    }

    [Fact]
    public void NoAdminAnswer_HasAMemberNamedLikePrivateContent()
    {
        var offending = Responses()
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => (Type: t, Property: p)))
            .Where(x => ForbiddenNames.Contains(x.Property.Name, StringComparer.OrdinalIgnoreCase))
            .Select(x => $"{x.Type.Name}.{x.Property.Name}")
            .ToList();

        Assert.Empty(offending);
    }

    [Fact]
    public void TheOnlyFreeTextOfTheMonitoring_IsTheFlagsOwnFixedSpanishMessage()
    {
        var withMessage = Responses().Where(t => t.GetProperty("Message") is not null).Select(t => t.Name).ToList();

        Assert.Equal([nameof(MonitoringFlagResponse)], withMessage);
    }

    [Fact]
    public void ALicenceIsShownMasked()
    {
        var phoneMembers = typeof(LineLicenseResponse).GetProperties().Where(p => p.Name.Contains("Phone", StringComparison.Ordinal)).Select(p => p.Name);

        Assert.Equal([nameof(LineLicenseResponse.PhoneNumberMasked)], phoneMembers);
    }
}
