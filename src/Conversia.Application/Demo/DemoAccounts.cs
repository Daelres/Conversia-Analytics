using Conversia.Domain.Enums;

namespace Conversia.Application.Demo;

/// <summary>One account of the demo dataset: what the API seeds and what the Web may show.</summary>
/// <param name="SignInLabel">The words after "Entrar como" on the login page's shortcut button; each account has its own.</param>
public sealed record DemoAccount(string Email, string SignInLabel, string Description, bool IsOwner, IReadOnlyList<Permission> Permissions);

/// <summary>
/// The single list of demo accounts. The API seeder and the Web's demo pages both read it, so what the Web
/// offers can never drift from what the API creates. Pure constants: no behaviour, no secrets.
/// </summary>
public static class DemoAccounts
{
    public const string DefaultPassword = "Demo-Pass-2026!";

    public static DemoAccount AcmeOwner { get; } = new(
        "owner@acme-demo.example", "propietario de Acme", "Propietario de Acme: tiene todos los permisos, incluida la gestión de usuarios.", true, []);

    public static DemoAccount AcmeManager { get; } = new(
        "manager@acme-demo.example", "gestor de Acme", "Gestor de Acme: ve y gestiona los productos, y ve los usuarios.", false,
        [Permission.ProductsView, Permission.ProductsManage, Permission.UsersView]);

    public static DemoAccount AcmeViewer { get; } = new(
        "viewer@acme-demo.example", "lector de Acme", "Lector de Acme: solo ve los productos.", false, [Permission.ProductsView]);

    public static DemoAccount AcmeNoAccess { get; } = new(
        "noaccess@acme-demo.example", "miembro sin permisos de Acme", "Miembro de Acme sin ningún permiso.", false, []);

    public static DemoAccount GlobexOwner { get; } = new(
        "owner@globex-demo.example", "propietario de Globex", "Propietario de Globex: un segundo espacio de trabajo con su propio catálogo, independiente del de Acme.", true, []);

    public static IReadOnlyList<DemoAccount> All { get; } = [AcmeOwner, AcmeManager, AcmeViewer, AcmeNoAccess, GlobexOwner];
}
