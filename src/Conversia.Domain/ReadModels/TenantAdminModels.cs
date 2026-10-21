using Conversia.Domain.Enums;

namespace Conversia.Domain.ReadModels;

/// <summary>One page of a cross-tenant listing.</summary>
public sealed record ReadPage<T>(IReadOnlyList<T> Items, int TotalCount);

public sealed record TenantAdminFilter(TenantStatus? Status, string? Search);

/// <summary>A company as the platform administrator sees it: business facts only (no conversations, no users beyond the owner's email).</summary>
public sealed record TenantAdminSummary(
    Guid Id,
    string Name,
    TenantStatus Status,
    int MaxActiveLines,
    int ActiveLines,
    int RegisteredLines,
    string? OwnerEmail,
    DateTime RegisteredAt,
    DateTime? LastActivityAt,
    DateTime? StatusChangedAt,
    string? StatusReason);

/// <summary>A WhatsApp line of a company: the number, its branch and whether it is switched on. No customer data.</summary>
public sealed record TenantAdminLine(Guid ChannelId, string DisplayPhoneNumber, string? BranchName, bool IsActive);

public sealed record TenantAdminDetail(TenantAdminSummary Summary, IReadOnlyList<TenantAdminLine> Lines);

public sealed record PlatformAuditEntry(
    Guid Id, DateTime OccurredAt, Guid AdminId, string? AdminEmail, string Action, Guid? TenantId, string Details);
