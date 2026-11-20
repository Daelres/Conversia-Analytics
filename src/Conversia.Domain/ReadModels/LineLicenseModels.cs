using Conversia.Domain.Enums;

namespace Conversia.Domain.ReadModels;

public sealed record LineLicenseFilter(LineLicenseStatus? Status, Guid? TenantId);

/// <summary>A licence as the administrator lists it: the number masked, the company's name, nothing of the company's data.</summary>
public sealed record LineLicenseRow(
    Guid Id,
    string Provider,
    string PhoneNumberMasked,
    string? ProviderChannelId,
    decimal MonthlyCostUsd,
    bool CustomerOwned,
    LineLicenseStatus Status,
    Guid? AssignedTenantId,
    string? AssignedTenantName,
    Guid? ChannelId,
    DateTime? AssignedAt,
    DateTime? ReleasedAt,
    DateTime? RetiredAt,
    string? Notes,
    DateTime CreatedAt);
