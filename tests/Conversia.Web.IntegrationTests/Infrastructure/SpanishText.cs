using Conversia.Domain.Enums;

namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>
/// The Spanish wording the requirement fixes (phase "i18n-es"). Tests match these case-insensitively on the quoted core text;
/// every other Spanish string of the interface is the implementer's choice and is checked by the English-leak scanner instead.
/// </summary>
internal static class SpanishText
{
    /// <summary>Login 401 (Web) and API InvalidCredentials.</summary>
    public const string InvalidCredentials = "Correo o contraseña incorrectos.";

    /// <summary>Register 409 (Web) and API DuplicateEmail.</summary>
    public const string EmailTaken = "Ese correo ya está registrado.";

    /// <summary>Rate limited (Web); followed by the retry seconds, e.g. "Vuelve a intentarlo en 60 segundos." ("1 segundo" for 1).</summary>
    public const string TooManyAttempts = "Demasiados intentos";

    /// <summary>Service unavailable (Web).</summary>
    public const string Unavailable = "no está disponible";

    /// <summary>Not authorized (Web, HTTP 403 pages and inline).</summary>
    public const string NotAuthorized = "No tienes permiso";

    /// <summary>Product not found (Web 404 page and API ProblemDetails).</summary>
    public const string ProductNotFound = "Producto no encontrado.";

    /// <summary>Stale edit (Web).</summary>
    public const string ProductModified = "Otra persona modificó este producto";

    /// <summary>Duplicate SKU (Web inline and API).</summary>
    public const string ProductSkuTaken = "Ya existe un producto con ese SKU.";

    /// <summary>The demo login shortcut button text starts with this.</summary>
    public const string SignInAs = "Entrar como";

    /// <summary>The demo accounts panel heading contains this.</summary>
    public const string DemoAccountsHeading = "Cuentas de demostración";

    /// <summary>The visible label of each permission; the enum names stay in routes, JSON and <c>data-*</c> hooks.</summary>
    public static readonly IReadOnlyDictionary<Permission, string> PermissionLabels = new Dictionary<Permission, string>
    {
        [Permission.UsersView] = "Ver usuarios",
        [Permission.UsersManage] = "Gestionar usuarios",
        [Permission.ProductsView] = "Ver productos",
        [Permission.ProductsManage] = "Gestionar productos",
    };

    /// <summary>
    /// The retry hint for <paramref name="seconds"/> as the requirement words it: "{n} segundos", "1 segundo" for one.
    /// A regular expression, because "11 segundos" must not satisfy "1 segundo".
    /// </summary>
    public static System.Text.RegularExpressions.Regex RetrySeconds(int seconds)
        => new($@"(?<![\d.,]){seconds} segundo{(seconds == 1 ? "(?!s)" : "s")}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}
