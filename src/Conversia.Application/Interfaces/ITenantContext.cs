namespace Conversia.Application.Interfaces;

/// <summary>
/// Sets the tenant of the current DI scope for code that runs without a JWT: the anonymous webhook request (once the channel
/// has been authenticated) and the background worker (once the event to process is claimed). From then on every query of that
/// scope is filtered to the tenant like an authenticated request's, and the explicit tenant wins over any claim.
/// </summary>
public interface ITenantContext
{
    /// <summary>Throws <see cref="ArgumentException"/> for <see cref="Guid.Empty"/>.</summary>
    void SetTenant(Guid tenantId);
}
