using System.Net;
using System.Net.Http.Json;
using Conversia.Application.DTOs.Users;
using Conversia.Domain.Enums;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// End-to-end user-management flow using only public endpoints: create a user,
/// grant a permission, watch it work, revoke it, watch it stop — all on one token.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class UserManagementFlowTests
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public UserManagementFlowTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task OwnerCreatesUser_GrantsUsersView_NewUserListsUsers_OwnerRevokes_NewUserIsForbiddenWithoutRelogin()
    {
        var owner = await RegisterTenantSessionAsync(_client);
        using var ownerClient = AuthorizedClient(_factory, owner.OwnerToken);
        var email = UniqueEmail("flow");

        var createResponse = await CreateUserAsync(ownerClient, email);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = (await createResponse.Content.ReadFromJsonAsync<UserSummaryResponse>())!;

        var token = await LoginAsync(_client, email);
        using var newUserClient = AuthorizedClient(_factory, token);
        Assert.Equal(HttpStatusCode.Forbidden, (await newUserClient.GetAsync("/api/users")).StatusCode);

        await GrantAsync(ownerClient, created.UserId, Permission.UsersView);

        var users = await ListUsersAsync(newUserClient);
        Assert.Contains(users, u => u.UserId == owner.OwnerId && u.IsOwner);
        Assert.Contains(users, u => u.UserId == created.UserId && u.Email == email && !u.IsOwner);
        var profile = await (await newUserClient.GetAsync("/api/users/me")).Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.Equal(new[] { Permission.UsersView }, profile!.Permissions);

        var revokeResponse = await RevokeAsync(ownerClient, created.UserId, Permission.UsersView);
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await newUserClient.GetAsync("/api/users")).StatusCode);
        profile = await (await newUserClient.GetAsync("/api/users/me")).Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.Empty(profile!.Permissions);
    }

    [Fact]
    public async Task UserCreatedByOwner_GivenUsersManage_CanCreateAndRevokeForOthers_ThenLosesThatPowerWhenRevoked()
    {
        var owner = await RegisterTenantSessionAsync(_client);
        using var ownerClient = AuthorizedClient(_factory, owner.OwnerToken);
        var managerEmail = UniqueEmail("manager");
        var managerResponse = await CreateUserAsync(ownerClient, managerEmail);
        Assert.Equal(HttpStatusCode.Created, managerResponse.StatusCode);
        var manager = (await managerResponse.Content.ReadFromJsonAsync<UserSummaryResponse>())!;
        await GrantAsync(ownerClient, manager.UserId, Permission.UsersManage);
        using var managerClient = AuthorizedClient(_factory, await LoginAsync(_client, managerEmail));

        var staffEmail = UniqueEmail("staff");
        var staffResponse = await CreateUserAsync(managerClient, staffEmail);
        Assert.Equal(HttpStatusCode.Created, staffResponse.StatusCode);
        var staff = (await staffResponse.Content.ReadFromJsonAsync<UserSummaryResponse>())!;
        await GrantAsync(managerClient, staff.UserId, Permission.UsersView);
        using var staffClient = AuthorizedClient(_factory, await LoginAsync(_client, staffEmail));
        Assert.Equal(HttpStatusCode.OK, (await staffClient.GetAsync("/api/users")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await RevokeAsync(ownerClient, manager.UserId, Permission.UsersManage)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await CreateUserAsync(managerClient, UniqueEmail("late"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await RevokeAsync(managerClient, staff.UserId, Permission.UsersView)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await staffClient.GetAsync("/api/users")).StatusCode);
    }
}
