using Conversia.Web.Authentication;
using Conversia.Web.Http;
using Conversia.Web.Services;

namespace Conversia.Web.Endpoints;

/// <summary>
/// Grant/revoke buttons post here. The posted ids are untrusted: they are validated locally, and the API
/// remains the authority on whether the caller may act. Every outcome ends in a redirect to the users page.
/// </summary>
public static class PermissionEndpoints
{
    public static WebApplication MapPermissionEndpoints(this WebApplication app)
    {
        app.MapPost("/users/permissions/grant", (HttpContext context, IApiClient api, CancellationToken cancellationToken)
                => ChangeAsync(grant: true, context, api, cancellationToken))
            .RequireAuthorization()
            .RequireValidAntiforgery();
        app.MapPost("/users/permissions/revoke", (HttpContext context, IApiClient api, CancellationToken cancellationToken)
                => ChangeAsync(grant: false, context, api, cancellationToken))
            .RequireAuthorization()
            .RequireValidAntiforgery();
        return app;
    }

    private static async Task<IResult> ChangeAsync(bool grant, HttpContext context, IApiClient api, CancellationToken cancellationToken)
    {
        if (!context.Request.HasFormContentType)
        {
            return Redirect(UsersNotice.Invalid);
        }

        var form = await context.Request.ReadFormAsync(cancellationToken);
        if (!PermissionChange.TryParse(form["userId"], form["permission"], out var change))
        {
            return Redirect(UsersNotice.Invalid);
        }

        var token = await context.GetApiTokenAsync();
        if (token is null)
        {
            return await EndSessionAsync(context);
        }

        var result = grant
            ? await api.AssignPermissionAsync(token, change.UserId, change.Permission, cancellationToken)
            : await api.RevokePermissionAsync(token, change.UserId, change.Permission, cancellationToken);

        if (result.Status == ApiResultStatus.TenantNotActive)
        {
            return Results.LocalRedirect(LocalReturnUrl.PendingAccount);
        }

        return result.Status == ApiResultStatus.Unauthorized
            ? await EndSessionAsync(context)
            : Redirect(UsersNotice.ForPermissionChange(grant, result.Status), result.ErrorCode);
    }

    private static IResult Redirect(string noticeCode, string? errorCode = null) => Results.LocalRedirect(UsersNotice.Url(noticeCode, errorCode));

    private static async Task<IResult> EndSessionAsync(HttpContext context)
    {
        await context.SignOutApiUserAsync();
        return Results.LocalRedirect(AuthenticationExtensions.LoginPath);
    }
}
