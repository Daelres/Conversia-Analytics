using System.Security.Cryptography;

namespace Conversia.Web.Http;

/// <summary>
/// Security headers of every response (docs/BILLING.md, "Adding a payment method"; docs/ARCHITECTURE.md):
/// <list type="bullet">
/// <item>Everywhere: <c>X-Content-Type-Options: nosniff</c>, <c>Referrer-Policy: strict-origin-when-cross-origin</c> and a CSP that forbids
/// framing, plugins, a foreign <c>&lt;base&gt;</c> and forms posting elsewhere (the billing page may also be redirected to Wompi's checkout).</item>
/// <item>On the payment-method page, where card data is typed: scripts only from this site (and the one inline theme script, by a per-request
/// nonce) and connections only to this site and the two Wompi API hosts, so an injected script could neither run nor send the card anywhere.</item>
/// </list>
/// </summary>
public static class SecurityHeadersMiddleware
{
    public const string NonceKey = "csp-nonce";

    public const string PaymentMethodPath = "/organizacion/facturacion/medio-de-pago";
    public const string BillingPath = "/organizacion/facturacion";

    /// <summary>The only hosts the browser may send card or Nequi data to (docs.wompi.co, "Ambientes y llaves").</summary>
    public static readonly IReadOnlyList<string> WompiApiHosts = ["https://sandbox.wompi.co", "https://production.wompi.co"];

    public const string WompiCheckout = "https://checkout.wompi.co";

    /// <summary>The two API bases the payment-method page may hand to the browser (the API's answer is checked, never trusted).</summary>
    public static bool IsWompiApiBase(string? url) => url is "https://sandbox.wompi.co/v1" or "https://production.wompi.co/v1";

    /// <summary>The nonce of this request's inline theme script.</summary>
    public static string Nonce(HttpContext context) => context.Items[NonceKey] as string ?? string.Empty;

    public static string PolicyFor(PathString path, string nonce)
    {
        var common = "frame-ancestors 'none'; base-uri 'self'; object-src 'none'";
        if (path.Equals(PaymentMethodPath, StringComparison.OrdinalIgnoreCase))
        {
            return $"default-src 'self'; script-src 'self' 'nonce-{nonce}'; connect-src 'self' {string.Join(' ', WompiApiHosts)}; "
                   + $"style-src 'self' 'unsafe-inline'; img-src 'self' data:; form-action 'self'; {common}";
        }

        var formAction = path.Equals(BillingPath, StringComparison.OrdinalIgnoreCase) ? $"'self' {WompiCheckout}" : "'self'";
        return $"form-action {formAction}; {common}";
    }

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
        => app.Use(async (context, next) =>
        {
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            context.Items[NonceKey] = nonce;
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.XContentTypeOptions = "nosniff";
                headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
                headers.ContentSecurityPolicy = PolicyFor(context.Request.Path, nonce);
                return Task.CompletedTask;
            });

            await next();
        });
}
