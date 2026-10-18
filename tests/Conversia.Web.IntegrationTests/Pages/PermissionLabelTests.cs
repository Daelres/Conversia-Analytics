extern alias ApiHost;

using System.Net;
using Conversia.Domain.Enums;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Pages;

/// <summary>
/// Permission names are identifiers (routes, JSON, form values, <c>data-*</c> hooks) and stay <c>UsersView</c>, <c>UsersManage</c>,
/// <c>ProductsView</c> and <c>ProductsManage</c>; what a person reads is the Spanish label.
/// </summary>
[Collection(nameof(WebIntegrationCollection))]
public class PermissionLabelTests(TestApiFactory api)
{
    private static readonly string[] EnumNames = Enum.GetNames<Permission>();

    public static TheoryData<Permission> AllPermissions
    {
        get
        {
            var data = new TheoryData<Permission>();
            foreach (var permission in Enum.GetValues<Permission>())
            {
                data.Add(permission);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(AllPermissions))]
    public async Task The_profile_shows_the_spanish_label_and_keeps_the_enum_name_in_data_permission(Permission permission)
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var member = await ApiSeed.CreateMemberAsync(api, owner, "miembro", permission);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, member.Email);

        var page = (await session.GetAsync("/")).ShouldBe(HttpStatusCode.OK);

        var held = Assert.Single(page.Select(PageContract.ProfilePermission));
        Assert.Equal(permission.ToString(), held.GetAttribute("data-permission"));
        Assert.Equal(SpanishText.PermissionLabels[permission], held.TextContent.Trim());
        AssertNoEnumNameVisible(page);
    }

    [Fact]
    public async Task A_member_with_all_four_permissions_sees_four_spanish_labels_on_the_profile_and_on_the_tour()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var member = await ApiSeed.CreateMemberAsync(api, owner, "todo", Permission.UsersView, Permission.UsersManage, Permission.ProductsView, Permission.ProductsManage);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, member.Email);

        var home = (await session.GetAsync("/")).ShouldBe(HttpStatusCode.OK);
        var tour = (await session.GetAsync("/demo")).ShouldBe(HttpStatusCode.OK);

        foreach (var (page, selector) in new[] { (home, PageContract.ProfilePermission), (tour, PageContract.YouPermission) })
        {
            var shown = page.Select(selector).ToDictionary(e => e.GetAttribute("data-permission")!, e => e.TextContent.Trim());
            Assert.Equal(EnumNames.Order(), shown.Keys.Order());
            foreach (var (enumValue, label) in SpanishText.PermissionLabels)
            {
                Assert.Equal(label, shown[enumValue.ToString()]);
            }

            AssertNoEnumNameVisible(page);
        }
    }

    [Fact]
    public async Task The_access_matrix_cells_keep_the_enum_names_and_show_no_english_permission_name()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var member = await ApiSeed.CreateMemberAsync(api, owner, "matriz", Permission.ProductsView, Permission.UsersManage);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, member.Email);

        var tour = (await session.GetAsync("/demo")).ShouldBe(HttpStatusCode.OK);

        var held = tour.Select(PageContract.AccessCell).Where(c => c.GetAttribute("data-held") == "true").Select(c => c.GetAttribute("data-permission")!).Order();
        Assert.Equal(new[] { "ProductsView", "UsersManage" }.Order(), held);
        Assert.Equal(EnumNames.Order(), tour.Select(PageContract.AccessCell).Select(c => c.GetAttribute("data-permission")!).Order());
        AssertNoEnumNameVisible(tour);
    }

    [Fact]
    public async Task The_users_page_labels_every_permission_in_spanish_while_the_forms_post_the_enum_names()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var member = await ApiSeed.CreateMemberAsync(api, owner, "objetivo", Permission.UsersView);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        var page = await WebFlows.UsersAsync(session);

        var row = Assert.Single(page.Select(PageContract.UserRow), r => r.GetAttribute("data-user-id") == member.UserId.ToString());
        var labels = row.QuerySelectorAll(PageContract.PermissionLabel).ToDictionary(e => e.GetAttribute("data-permission")!, e => e.TextContent.Trim());
        Assert.Equal(EnumNames.Order(), labels.Keys.Order());
        foreach (var (permission, label) in SpanishText.PermissionLabels)
        {
            Assert.Equal(label, labels[permission.ToString()]);
        }

        foreach (var permission in EnumNames)
        {
            var form = FormModel.From(page, page.FindPermissionForm(permission == "UsersView" ? "revoke" : "grant", member.UserId, permission));
            Assert.Equal(permission, form.Get("permission")); // the value posted is the enum name, not the Spanish label
        }

        AssertNoEnumNameVisible(page);
    }

    [Fact]
    public async Task Granting_through_the_spanish_form_still_grants_the_enum_permission()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        var member = await ApiSeed.CreateMemberAsync(api, owner, "objetivo");
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        var page = await WebFlows.UsersAsync(session);
        var form = FormModel.From(page, page.FindPermissionForm("grant", member.UserId, "ProductsManage"));

        var landed = await WebFlows.FollowRedirectsAsync(session, await session.SubmitAsync(form));

        landed.ShouldBe(HttpStatusCode.OK);
        Assert.Equal(new[] { Permission.ProductsManage }, await ApiSeed.PersistedPermissionsAsync(api, member.UserId));
        var call = Assert.Single(host.Api.Calls, c => c.Method == HttpMethod.Post && c.PathAndQuery == $"/api/users/{member.UserId}/permissions");
        Assert.Equal(HttpStatusCode.NoContent, call.Status);
    }

    /// <summary>
    /// No permission identifier is shown as text or in a user-visible attribute (<c>data-*</c> attributes, hidden input values and
    /// <c>&lt;code&gt;</c> blocks, where an identifier is legitimate, are not read).
    /// </summary>
    private static void AssertNoEnumNameVisible(PageResponse page)
    {
        var visible = SpanishUi.AllText(SpanishUi.Extract(page));
        Assert.NotEmpty(visible);
        foreach (var name in EnumNames)
        {
            Assert.DoesNotContain(name, visible, StringComparison.Ordinal);
        }
    }
}
