using Conversia.Application.Exceptions;
using Conversia.Domain.Enums;
using Conversia.Domain.Interfaces;
using Conversia.Infrastructure.Administration;
using Microsoft.AspNetCore.Authorization;

namespace Conversia.API.Middleware;

/// <summary>Marks an endpoint a signed-in user of a pending or suspended company may still call (to learn where the company stands).</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class AllowInactiveTenantAttribute : Attribute
{
}

/// <summary>
/// A company that is not Active cannot operate: every endpoint that needs a signed-in company user answers 403 with the code
/// <c>tenant_not_active</c>, before permissions are looked at. Exempt: anonymous endpoints (login, registration, webhooks, which check the
/// status themselves), the admin API, and those marked <see cref="AllowInactiveTenantAttribute"/>.
/// The status is read on every request (a primary-key lookup of one column) rather than cached: a suspension takes effect on the very next
/// request on every API instance, with no invalidation to get wrong.
/// </summary>
public sealed class ActiveTenantMiddleware
{
    private readonly RequestDelegate _next;

    public ActiveTenantMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ITenantRepository tenants)
    {
        if (await MustBeActiveAsync(context, tenants) is { } status && status != TenantStatus.Active)
        {
            // The outermost middleware turns this into the 403 problem with the code.
            throw new TenantNotActiveException(status);
        }

        await _next(context);
    }

    private static async Task<TenantStatus?> MustBeActiveAsync(HttpContext context, ITenantRepository tenants)
    {
        var endpoint = context.GetEndpoint();
        var protectedEndpoint = endpoint?.Metadata.GetOrderedMetadata<IAuthorizeData>() ?? [];
        if (endpoint is null
            || protectedEndpoint.Count == 0
            || endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null
            || endpoint.Metadata.GetMetadata<AllowInactiveTenantAttribute>() is not null
            || protectedEndpoint.Any(a => a.Policy == PlatformAdminTokens.PolicyName)
            || context.User.Identity?.IsAuthenticated != true
            || !Guid.TryParse(context.User.FindFirst("tenant_id")?.Value, out var tenantId))
        {
            return null;
        }

        // A token whose company no longer exists is as good as no token.
        return await tenants.GetStatusAsync(tenantId, context.RequestAborted) ?? TenantStatus.Suspended;
    }
}
