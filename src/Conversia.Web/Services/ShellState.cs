using Conversia.Application.DTOs.Users;
using Conversia.Domain.Enums;

namespace Conversia.Web.Services;

/// <summary>What the account card of the shell may show about the signed-in person.</summary>
public sealed record ShellUser(bool IsOwner, Guid TenantId);

/// <summary>
/// Per-request bridge from a page to the layout: a page that already loaded the caller's profile hands it over so that the shell
/// can show the role and workspace without a second API call. When no page does, the shell shows only what the cookie knows.
/// </summary>
public sealed class ShellState
{
    public ShellUser? User { get; private set; }

    /// <summary>Where the company stands, when the page has asked. Only a company that is not Active locks the navigation, so a page that never asked leaves it open.</summary>
    public TenantStatus? Tenant { get; private set; }

    public event Action? Changed;

    public void RememberTenant(TenantStatus status)
    {
        Tenant = status;
        Changed?.Invoke();
    }

    public void Remember(UserProfileResponse profile)
    {
        User = new ShellUser(profile.IsOwner, profile.TenantId);
        Changed?.Invoke();
    }
}
