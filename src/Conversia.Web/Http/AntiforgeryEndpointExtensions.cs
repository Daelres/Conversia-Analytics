using Conversia.Web.Services;
using Microsoft.AspNetCore.Antiforgery;

namespace Conversia.Web.Http;

public static class AntiforgeryEndpointExtensions
{
    /// <summary>
    /// The antiforgery middleware only records its verdict, and a minimal-API form binder would answer a bad token
    /// with an empty 400 that the status-code pages then re-run through the pipeline. Rejecting here, with a body,
    /// before the handler touches the form, keeps the outcome a plain, controlled 400.
    /// </summary>
    public static RouteHandlerBuilder RequireValidAntiforgery(this RouteHandlerBuilder builder)
        => builder
            .WithMetadata(new RequireAntiforgeryTokenAttribute())
            .AddEndpointFilter(async (context, next) =>
                context.HttpContext.Features.Get<IAntiforgeryValidationFeature>() is { IsValid: true }
                    ? await next(context)
                    : Results.Text(UiMessages.AntiforgeryRejected, statusCode: StatusCodes.Status400BadRequest));
}
