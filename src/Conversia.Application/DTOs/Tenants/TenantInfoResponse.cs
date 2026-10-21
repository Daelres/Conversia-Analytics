using Conversia.Domain.Enums;

namespace Conversia.Application.DTOs.Tenants;

/// <summary>
/// The caller's own company as it stands with the platform: whether it may operate and how many WhatsApp lines its agreement allows.
/// Answered to any signed-in user of the company, also while it is pending or suspended.
/// </summary>
public class TenantInfoResponse
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public TenantStatus Status { get; set; }

    /// <summary>How many lines may be active at once.</summary>
    public int MaxActiveLines { get; set; }

    public int ActiveLines { get; set; }

    /// <summary>Every line the company has registered, active or not.</summary>
    public int RegisteredLines { get; set; }
}
