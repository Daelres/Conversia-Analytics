using AngleSharp.Dom;
using AngleSharp.Html.Dom;

namespace Conversia.Web.IntegrationTests.Infrastructure;

/// <summary>
/// The markup conventions the requirement leaves open and these tests therefore pin. Everything else
/// (CSS, layout, element order, the prefix of field names) is free.
/// <list type="bullet">
/// <item>Forms are located by what they contain, not by id or class: the login, registration and
/// create-user forms by their password input. There is <b>exactly one</b> form with a password input on
/// <c>/login</c>, on <c>/register</c> and on <c>/users</c> (for a caller who may create users); the logout
/// form is found by <c>method="post"</c> + an action ending in <c>/logout</c>, and there is <b>exactly one</b>
/// such form per page.</item>
/// <item>Text fields are matched by the <b>last name segment</b>, case-insensitively: a control named
/// <c>Input.Email</c> (or <c>email</c>, <c>Model.EMAIL</c>) is "email". The segments used are <c>email</c>,
/// <c>password</c> and (register only) <c>tenantName</c>; anything before the last <c>.</c> or <c>]</c> is ignored
/// (see <see cref="FormModel"/>).</item>
/// <item>The antiforgery field is the hidden input <c>__RequestVerificationToken</c> of every POST form.</item>
/// <item><c>/users</c>: one element per listed user with <c>data-testid="user-row"</c> and
/// <c>data-user-id="{guid}"</c>, containing the email; owners also contain <c>data-testid="owner-badge"</c>.</item>
/// <item><c>/users</c>: a grant/revoke form is a <c>&lt;form method="post"&gt;</c> with
/// <c>data-action="grant"</c> or <c>"revoke"</c> and hidden inputs whose names end in <c>UserId</c> and
/// <c>Permission</c> (value: permission name or number). Whether the UI shows both forms for every user and
/// permission or only the one matching the current state is its own decision (the user-list API does not even report
/// other users' permissions); tests only look up the form the scenario makes natural (grant for a permission the user
/// lacks, revoke for one the user holds) and never require both for one user and permission.</item>
/// <item><c>/</c> (authenticated): <c>data-testid="profile-tenant-id"</c> (text = tenant id),
/// <c>data-testid="profile-is-owner"</c> (<c>data-value="true|false"</c>), and one
/// <c>data-testid="profile-permission"</c> element per held permission whose <c>data-permission</c> is the enum name
/// (<c>UsersView</c>, ...) and whose visible text is the Spanish label (<see cref="SpanishText.PermissionLabels"/>).</item>
/// <item>Any authenticated page offers one <c>&lt;form method="post" action="/logout"&gt;</c>.</item>
/// <item><c>/products</c>: one element per listed product with <c>data-testid="product-row"</c> and
/// <c>data-product-id="{guid}"</c>, containing four cells marked <c>data-field="sku"</c>, <c>"name"</c>, <c>"price"</c>
/// and <c>"stock"</c>; the sku and name cells hold exactly the text; the price cell carries <c>data-price</c> = the invariant
/// number with two decimals (<c>249.90</c>, <c>1234.50</c>) and the stock cell <c>data-stock</c> = the invariant integer
/// (<c>1200</c>), so numbers are read without parsing locale text. The price is DISPLAYED with the Spanish culture
/// (<c>1.234,50</c>). Management is offered as plain links inside the row whose path is
/// <c>/products/{id}/edit</c> and <c>/products/{id}/delete</c>; "shown" means present and not inside a <c>hidden</c> element.</item>
/// <item><c>/products</c> pager: <c>data-testid="pager"</c> with <c>data-page</c>, <c>data-page-count</c> and
/// <c>data-total-count</c> (the API's total, over all pages) and, in its text, the current page and the page count
/// (the Spanish wording around them is free). Previous/next are anchors
/// <c>data-testid="pager-prev"</c> / <c>"pager-next"</c> with an <c>href</c> (a non-link when there is no previous/next
/// page: the anchor is absent or has no <c>href</c>). Their targets keep the current <c>search</c>.</item>
/// <item><c>/products</c> search: a <c>&lt;form data-testid="product-search"&gt;</c> (GET) with a text input whose
/// last name segment is <c>search</c>, showing the current term back.</item>
/// <item>Product forms are located by their fields (never by id or class): the create form is the visible, usable POST form
/// with a <c>sku</c> field (plus <c>name</c>, <c>price</c>, <c>stock</c>); the edit form the visible, usable POST form with a
/// <c>rowVersion</c> field (hidden input, base64 exactly as the API returned it) plus <c>name</c>, <c>price</c>, <c>stock</c>;
/// the delete confirmation the one visible, usable POST form of <c>/products/{id}/delete</c> other than logout. The price field accepts
/// <c>19,99</c> and <c>19.99</c> (both 19.99) and <c>1,5</c> (1.5), and the edit form pre-fills it with a comma decimal (<c>19,99</c>).
/// A management
/// form may exist in the markup for a caller who may not manage (static SSR needs it in the render tree to answer a forged
/// post) but then it is inside a <c>hidden</c> element or has no submit control.</item>
/// <item>A success confirmation after a product is created, edited or deleted is an element
/// <c>data-testid="notice-success"</c> on the page the redirect lands on. Failures are recognised by text only, using the
/// Spanish words the requirement quotes (<see cref="SpanishText"/>): "No tienes permiso", "no está disponible",
/// "Ya existe un producto con ese SKU.", "Producto no encontrado.", "Otra persona modificó este producto" (plus a link to the
/// product's edit page). "Producto no encontrado." is the visible text of the 404 response itself: the generic
/// page for an unknown route is a different page and does not satisfy it.</item>
/// <item><c>/demo</c> (signed in): <c>data-testid="you-now"</c> contains <c>you-tenant-id</c> (text = tenant id),
/// <c>you-is-owner</c> (<c>data-value="true|false"</c>), one <c>you-permission</c> per held permission (<c>data-permission</c> =
/// enum name, text = Spanish label), and the access matrix: one <c>data-testid="access-cell"</c> per permission of Users view/manage and
/// Products view/manage with <c>data-permission="UsersView|UsersManage|ProductsView|ProductsManage"</c> and
/// <c>data-held="true|false"</c>. What the matrix says about an owner is not pinned.</item>
/// <item><c>/demo</c> with <c>Demo:ShowAccounts</c>: one <c>data-testid="demo-account-row"</c> per account with
/// <c>data-email</c>, <c>data-testid="demo-password"</c> elements (text = the shared password) and
/// <c>data-testid="demo-scenario"</c> items. <c>/login</c> with <c>Demo:ShowAccounts</c>: a
/// <c>data-testid="demo-login-panel"</c> holding one POST form per account whose <c>email</c> and <c>password</c> fields are
/// hidden inputs (the page still has exactly one form with a <c>type=password</c> input, the real login form) and a submit
/// button whose text starts with "Entrar como" (the rest names the account and is free). The panel and the demo table are
/// introduced by a heading containing "Cuentas de demostración". With the setting off none of these exist and
/// neither an account email nor the password appears anywhere.</item>
/// <item><c>/demo</c> (public): what the template provides is a list of elements <c>data-testid="tour-feature"</c>, one per
/// feature, each with non-blank text. The tests require the list to be non-empty and the page to link to <c>/login</c>,
/// <c>/register</c>, <c>/users</c> and <c>/products</c>; they pin no wording of the tour itself.</item>
/// <item>The navigation is the <c>nav</c> element: a link to <c>/demo</c> always, links to <c>/products</c>
/// and <c>/users</c> only when signed in (their Spanish wording is free; each has visible text).</item>
/// <item>Language: every page is <c>&lt;html lang="es"&gt;</c> with a non-empty <c>&lt;title&gt;</c>, and no visible text or user-visible
/// attribute (<c>title</c>, <c>placeholder</c>, <c>aria-label</c>, <c>alt</c>, submit <c>value</c>) contains English interface wording
/// (<see cref="SpanishUi"/>; the denylist and allowlist are documented in <c>EnglishLeaks</c>). The Web always renders in the
/// <c>es</c> culture, whatever <c>Accept-Language</c> says. Permission enum names stay in routes, form values and <c>data-*</c>
/// hooks; only their visible labels are Spanish.</item>
/// <item><c>/users</c>: next to each permission's grant/revoke forms, the permission is named by an element
/// <c>data-testid="permission-label"</c> with <c>data-permission</c> = the enum name and the Spanish label as text.</item>
/// <item>Cookies: every cookie that is not an antiforgery cookie (name containing "antiforgery") is treated as
/// part of the authentication ticket (see <see cref="WebSession.AuthCookies"/>); a ticket split into chunks is fine.
/// The <c>Secure</c> attribute assertion goes beyond the written requirement (which asks for HttpOnly and
/// SameSite); it is valid here only because the test host is served over https (<see cref="WebTestHost.BaseAddress"/>).</item>
/// </list>
/// </summary>
internal static class PageContract
{
    public const string UserRow = "[data-testid='user-row']";
    public const string OwnerBadge = "[data-testid='owner-badge']";
    public const string ProfileTenantId = "[data-testid='profile-tenant-id']";
    public const string ProfileIsOwner = "[data-testid='profile-is-owner']";
    public const string ProfilePermission = "[data-testid='profile-permission']";
    public const string PermissionLabel = "[data-testid='permission-label']";

    public const string ProductRow = "[data-testid='product-row']";
    public const string Pager = "[data-testid='pager']";
    public const string PagerPrevious = "a[data-testid='pager-prev'][href]";
    public const string PagerNext = "a[data-testid='pager-next'][href]";
    public const string SearchForm = "form[data-testid='product-search']";
    public const string NoticeSuccess = "[data-testid='notice-success']";

    public const string TourFeature = "[data-testid='tour-feature']";

    public const string YouNow = "[data-testid='you-now']";
    public const string YouTenantId = "[data-testid='you-tenant-id']";
    public const string YouIsOwner = "[data-testid='you-is-owner']";
    public const string YouPermission = "[data-testid='you-permission']";
    public const string AccessCell = "[data-testid='access-cell']";

    public const string DemoAccountRow = "[data-testid='demo-account-row']";
    public const string DemoPassword = "[data-testid='demo-password']";
    public const string DemoScenario = "[data-testid='demo-scenario']";
    public const string DemoLoginPanel = "[data-testid='demo-login-panel']";

    /// <summary>Every hook that only exists while <c>Demo:ShowAccounts</c> is on.</summary>
    public static readonly string[] DemoAccountHooks = { DemoAccountRow, DemoPassword, DemoScenario, DemoLoginPanel };

    public static string Field(string name) => $"[data-field='{name}']";

    public static bool IsHidden(IElement element) => element.Closest("[hidden]") is not null;

    /// <summary>True when a person could use the form: not hidden and with a submit control that is not disabled.</summary>
    public static bool IsVisibleAndUsable(IHtmlFormElement form) => !IsHidden(form) && IsUsable(form);

    /// <summary>The form's control whose last name segment is <paramref name="field"/> (the same rule as <see cref="FormModel"/>).</summary>
    public static IElement? ControlNamed(IHtmlFormElement form, string field)
        => form.QuerySelectorAll("input[name], textarea[name], select[name]").FirstOrDefault(c =>
        {
            var name = c.GetAttribute("name")!;
            return string.Equals(name[(name.LastIndexOfAny(new[] { '.', ']' }) + 1)..], field, StringComparison.OrdinalIgnoreCase);
        });

    public static bool IsLogoutForm(IHtmlFormElement form)
        => string.Equals(form.GetAttribute("method"), "post", StringComparison.OrdinalIgnoreCase)
           && (form.GetAttribute("action") ?? string.Empty).TrimEnd('/').EndsWith("/logout", StringComparison.OrdinalIgnoreCase);

    public static bool IsPermissionForm(IHtmlFormElement form, string action, Guid userId, string permission)
    {
        if (!string.Equals(form.GetAttribute("data-action"), action, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var userField = HiddenValue(form, "UserId");
        var permissionField = HiddenValue(form, "Permission");
        return Guid.TryParse(userField, out var id) && id == userId
            && PermissionMatches(permissionField, permission);
    }

    /// <summary>True when the form has at least one submit control a user could actually activate.</summary>
    public static bool IsUsable(IHtmlFormElement form)
        => form.QuerySelectorAll("button:not([type=button]):not([type=reset]), input[type=submit]")
            .Any(submitter => !IsDisabled(submitter));

    private static bool IsDisabled(IElement element)
        => element.HasAttribute("disabled") || element.Closest("fieldset[disabled]") is not null;

    private static string? HiddenValue(IHtmlFormElement form, string suffix)
        => form.QuerySelectorAll("input[name]")
            .FirstOrDefault(i => i.GetAttribute("name")!.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            ?.GetAttribute("value");

    private static bool PermissionMatches(string? actual, string expected)
        => actual is not null
           && Enum.TryParse<Conversia.Domain.Enums.Permission>(actual, ignoreCase: true, out var a)
           && Enum.TryParse<Conversia.Domain.Enums.Permission>(expected, ignoreCase: true, out var e)
           && a == e;
}
