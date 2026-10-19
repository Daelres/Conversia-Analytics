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
/// <item>A success confirmation after a write is an element <c>data-testid="notice-success"</c> on the page the redirect lands on.
/// Failures are recognised by text only, using the Spanish words the requirement quotes (<see cref="SpanishText"/>): "No tienes permiso",
/// "no está disponible".</item>
/// <item>The navigation is the <c>nav</c> element: a link to <c>/</c> always, a link to <c>/users</c>
/// only when signed in (their Spanish wording is free; each has visible text).</item>
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

    public const string Pager = "[data-testid='pager']";
    public const string PagerPrevious = "a[data-testid='pager-prev'][href]";
    public const string PagerNext = "a[data-testid='pager-next'][href]";
    public const string NoticeSuccess = "[data-testid='notice-success']";

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
