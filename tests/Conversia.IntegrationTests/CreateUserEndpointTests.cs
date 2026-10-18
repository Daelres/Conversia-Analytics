using System.Net;
using System.Net.Http.Json;
using System.Text;
using Conversia.Application.DTOs.Auth;
using Conversia.Application.DTOs.Users;
using Conversia.Application.Exceptions;
using Conversia.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// <c>POST /api/users</c>: a user with <c>UsersManage</c> (or an owner) creates
/// a non-owner, permission-less user inside their own tenant. Everything
/// runs through real HTTP, real JWT authentication/authorization and the
/// Testcontainers PostgreSQL. Members are seeded through the repository
/// (<see cref="SeedMemberAsync"/>) so these tests depend on nothing but the
/// endpoint under test.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class CreateUserEndpointTests
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public CreateUserEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private HttpClient ClientFor(string token) => AuthorizedClient(_factory, token);

    private static async Task<UserSummaryResponse> ReadSummaryAsync(HttpResponseMessage response)
    {
        var summary = await response.Content.ReadFromJsonAsync<UserSummaryResponse>();
        Assert.NotNull(summary);
        return summary!;
    }

    // ---- Happy path -------------------------------------------------------------

    [Fact]
    public async Task CreateUser_AsOwner_Returns201WithSummaryAndPersistsANonOwnerInTheCallersTenant()
    {
        var owner = await RegisterTenantSessionAsync(_client);
        using var ownerClient = ClientFor(owner.OwnerToken);
        var email = UniqueEmail("created");

        var response = await CreateUserAsync(ownerClient, email);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var summary = await ReadSummaryAsync(response);
        Assert.NotEqual(Guid.Empty, summary.UserId);
        Assert.Equal(email, summary.Email);
        Assert.False(summary.IsOwner);

        var persisted = Assert.Single(await FindUsersByEmailAsync(_factory, email));
        Assert.Equal(summary.UserId, persisted.Id);
        Assert.Equal(owner.TenantId, persisted.TenantId);
        Assert.False(persisted.IsOwner);
        Assert.False(string.IsNullOrWhiteSpace(persisted.PasswordHash));
        Assert.DoesNotContain(StrongPassword, persisted.PasswordHash);
        Assert.Empty(await PersistedPermissionsAsync(_factory, persisted.Id));
    }

    [Fact]
    public async Task CreateUser_RecordsTheCreatingUserAsCreatedBy()
    {
        var owner = await RegisterTenantSessionAsync(_client);
        using var ownerClient = ClientFor(owner.OwnerToken);
        var email = UniqueEmail("audited");

        var response = await CreateUserAsync(ownerClient, email);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var persisted = Assert.Single(await FindUsersByEmailAsync(_factory, email));
        Assert.Equal(owner.OwnerId, persisted.CreatedBy);
    }

    [Fact]
    public async Task CreatedUser_CanLogIn_AndTheTokenCarriesTheCreatorsTenantAndIsNotOwner()
    {
        var owner = await RegisterTenantSessionAsync(_client);
        using var ownerClient = ClientFor(owner.OwnerToken);
        var email = UniqueEmail("login");
        var createResponse = await CreateUserAsync(ownerClient, email);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await ReadSummaryAsync(createResponse);

        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new { email, password = StrongPassword });

        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var login = await loginResponse.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(login);
        var (userId, tenantId, isOwner) = DecodeClaims(login!.Token);
        Assert.Equal(created.UserId, userId);
        Assert.Equal(owner.TenantId, tenantId);
        Assert.False(isOwner);
        Assert.Equal(owner.TenantId, login.TenantId);
        Assert.False(login.IsOwner);

        using var newUserClient = ClientFor(login.Token);
        var profile = await (await newUserClient.GetAsync("/api/users/me")).Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(profile);
        Assert.Equal(owner.TenantId, profile!.TenantId);
        Assert.False(profile.IsOwner);
        Assert.Empty(profile.Permissions);
    }

    [Fact]
    public async Task CreatedUser_HasNoPermissions_SoGuardedEndpointsAnswer403()
    {
        var owner = await RegisterTenantSessionAsync(_client);
        using var ownerClient = ClientFor(owner.OwnerToken);
        var email = UniqueEmail("powerless");
        var createResponse = await CreateUserAsync(ownerClient, email);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await ReadSummaryAsync(createResponse);
        using var newUserClient = ClientFor(await LoginAsync(_client, email));

        var list = await newUserClient.GetAsync("/api/users");
        var create = await CreateUserAsync(newUserClient, UniqueEmail("by-powerless"));
        var assign = await newUserClient.PostAsJsonAsync(
            $"/api/users/{created.UserId}/permissions", new { permission = (int)Permission.UsersView });

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, assign.StatusCode);
    }

    [Fact]
    public async Task CreateUser_IgnoresTenantIdIsOwnerAndPermissionsSuppliedInTheBody()
    {
        var tenantA = await RegisterTenantSessionAsync(_client, "tenantA");
        var tenantB = await RegisterTenantSessionAsync(_client, "tenantB");
        using var clientA = ClientFor(tenantA.OwnerToken);
        using var clientB = ClientFor(tenantB.OwnerToken);
        var email = UniqueEmail("smuggle");

        var response = await clientA.PostAsJsonAsync("/api/users", new
        {
            email,
            password = StrongPassword,
            tenantId = tenantB.TenantId,
            isOwner = true,
            permissions = new[] { (int)Permission.UsersView, (int)Permission.UsersManage }
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var summary = await ReadSummaryAsync(response);
        Assert.False(summary.IsOwner);

        var persisted = Assert.Single(await FindUsersByEmailAsync(_factory, email));
        Assert.Equal(tenantA.TenantId, persisted.TenantId);
        Assert.False(persisted.IsOwner);
        Assert.Empty(await PersistedPermissionsAsync(_factory, persisted.Id));

        Assert.DoesNotContain(await ListUsersAsync(clientB), u => u.UserId == summary.UserId);
        var (_, tokenTenantId, tokenIsOwner) = DecodeClaims(await LoginAsync(_client, email));
        Assert.Equal(tenantA.TenantId, tokenTenantId);
        Assert.False(tokenIsOwner);
    }

    [Fact]
    public async Task CreatedUser_AppearsInTheCreatorsTenantListAndNeverInAnotherTenants()
    {
        var tenantA = await RegisterTenantSessionAsync(_client, "tenantA");
        var tenantB = await RegisterTenantSessionAsync(_client, "tenantB");
        using var clientA = ClientFor(tenantA.OwnerToken);
        using var clientB = ClientFor(tenantB.OwnerToken);
        var email = UniqueEmail("scoped");
        var createResponse = await CreateUserAsync(clientA, email);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await ReadSummaryAsync(createResponse);

        var listA = await ListUsersAsync(clientA);
        var listB = await ListUsersAsync(clientB);

        var entry = Assert.Single(listA, u => u.UserId == created.UserId);
        Assert.Equal(email, entry.Email);
        Assert.False(entry.IsOwner);
        Assert.Equal(2, listA.Count);
        Assert.DoesNotContain(listB, u => u.UserId == created.UserId || u.Email == email);
        Assert.Single(listB);
    }

    [Fact]
    public async Task CreateUser_AsNonOwnerHoldingUsersManage_Returns201AndRecordsThatUserAsCreator()
    {
        var owner = await RegisterTenantSessionAsync(_client);
        using var ownerClient = ClientFor(owner.OwnerToken);
        var manager = await SeedMemberAsync(_factory, _client, owner.TenantId, "manager");
        await GrantAsync(ownerClient, manager.UserId, Permission.UsersManage);
        using var managerClient = ClientFor(manager.Token);
        var email = UniqueEmail("by-manager");

        var response = await CreateUserAsync(managerClient, email);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var persisted = Assert.Single(await FindUsersByEmailAsync(_factory, email));
        Assert.Equal(owner.TenantId, persisted.TenantId);
        Assert.False(persisted.IsOwner);
        Assert.Equal(manager.UserId, persisted.CreatedBy);
    }

    // ---- Authentication / authorization ------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData(Permission.UsersView)]
    public async Task CreateUser_AsNonOwnerWithoutUsersManage_Returns403AndWritesNothing(Permission? held)
    {
        var owner = await RegisterTenantSessionAsync(_client);
        using var ownerClient = ClientFor(owner.OwnerToken);
        var member = await SeedMemberAsync(_factory, _client, owner.TenantId);
        if (held is { } permission)
        {
            await GrantAsync(ownerClient, member.UserId, permission);
        }

        using var memberClient = ClientFor(member.Token);
        var email = UniqueEmail("forbidden");

        var response = await CreateUserAsync(memberClient, email);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await FindUsersByEmailAsync(_factory, email));
    }

    [Fact]
    public async Task CreateUser_WithoutToken_Returns401AndWritesNothing()
    {
        var email = UniqueEmail("anonymous");

        var response = await CreateUserAsync(_client, email);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await FindUsersByEmailAsync(_factory, email));
    }

    [Fact]
    public async Task CreateUser_WithMalformedToken_Returns401AndWritesNothing()
    {
        using var client = ClientFor("not-a-real-jwt");
        var email = UniqueEmail("bad-token");

        var response = await CreateUserAsync(client, email);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await FindUsersByEmailAsync(_factory, email));
    }

    // ---- Duplicate email ----------------------------------------------------------

    [Fact]
    public async Task CreateUser_WithEmailAlreadyUsedInTheSameTenant_Returns409ProblemJsonAndKeepsTheOriginal()
    {
        var owner = await RegisterTenantSessionAsync(_client);
        using var ownerClient = ClientFor(owner.OwnerToken);
        var email = UniqueEmail("dup");
        var first = await CreateUserAsync(ownerClient, email);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstSummary = await ReadSummaryAsync(first);

        var second = await CreateUserAsync(ownerClient, email, "Another-Secret1!");

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var problem = await ReadProblemJsonAsync(second);
        Assert.Equal(new DuplicateEmailException().Message, problem.Detail);
        var persisted = Assert.Single(await FindUsersByEmailAsync(_factory, email));
        Assert.Equal(firstSummary.UserId, persisted.Id);
        await LoginAsync(_client, email); // the original password still works
    }

    [Fact]
    public async Task CreateUser_WithEmailOfAnotherTenantsOwner_Returns409AndLeavesThatAccountUntouched()
    {
        var tenantA = await RegisterTenantSessionAsync(_client, "tenantA");
        var tenantB = await RegisterTenantSessionAsync(_client, "tenantB");
        using var clientA = ClientFor(tenantA.OwnerToken);

        var response = await CreateUserAsync(clientA, tenantB.OwnerEmail, "Another-Secret1!");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await ReadProblemJsonAsync(response);
        Assert.Equal(new DuplicateEmailException().Message, problem.Detail);

        var existing = Assert.Single(await FindUsersByEmailAsync(_factory, tenantB.OwnerEmail));
        Assert.Equal(tenantB.OwnerId, existing.Id);
        Assert.Equal(tenantB.TenantId, existing.TenantId);
        Assert.True(existing.IsOwner);
        await LoginAsync(_client, tenantB.OwnerEmail); // old password untouched
    }

    [Fact]
    public async Task CreateUser_DuplicateResponse_IsIdenticalWhetherTheEmailBelongsToOwnOrAnotherTenant_AndNeverRevealsTheOtherTenant()
    {
        var tenantA = await RegisterTenantSessionAsync(_client, "tenantA");
        var tenantB = await RegisterTenantSessionAsync(_client, "tenantB");
        using var clientA = ClientFor(tenantA.OwnerToken);
        var ownTenantEmail = UniqueEmail("own");
        Assert.Equal(HttpStatusCode.Created, (await CreateUserAsync(clientA, ownTenantEmail)).StatusCode);

        var sameTenant = await CreateUserAsync(clientA, ownTenantEmail);
        var otherTenant = await CreateUserAsync(clientA, tenantB.OwnerEmail);

        Assert.Equal(HttpStatusCode.Conflict, sameTenant.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, otherTenant.StatusCode);
        var sameTenantProblem = await ReadProblemJsonAsync(sameTenant);
        var otherTenantProblem = await ReadProblemJsonAsync(otherTenant);
        Assert.Equal(sameTenantProblem.Title, otherTenantProblem.Title);
        Assert.Equal(sameTenantProblem.Detail, otherTenantProblem.Detail);
        Assert.Equal(sameTenantProblem.Status, otherTenantProblem.Status);

        var rawBody = await otherTenant.Content.ReadAsStringAsync();
        Assert.DoesNotContain(tenantB.TenantId.ToString(), rawBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(tenantB.OwnerId.ToString(), rawBody, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Invalid input ------------------------------------------------------------

    private static async Task AssertValidationErrorUnderAsync(HttpResponseMessage response, string key)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.NotNull(problem);
        Assert.True(
            problem!.Errors.TryGetValue(key, out var messages) && messages.Length > 0,
            $"Expected a validation error under '{key}' but got keys: {string.Join(", ", problem.Errors.Keys)}.");
    }

    [Theory]
    [MemberData(nameof(RegisterTenantPasswordPolicyTests.WeakPasswords), MemberType = typeof(RegisterTenantPasswordPolicyTests))]
    public async Task CreateUser_WithPasswordViolatingThePolicy_Returns400UnderPasswordAndWritesNothing(string password)
    {
        var owner = await RegisterTenantSessionAsync(_client);
        using var ownerClient = ClientFor(owner.OwnerToken);
        var email = UniqueEmail("weak");

        var response = await CreateUserAsync(ownerClient, email, password);

        await AssertValidationErrorUnderAsync(response, "Password");
        Assert.Empty(await FindUsersByEmailAsync(_factory, email));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task CreateUser_WithNullOrEmptyPassword_Returns400UnderPasswordAndWritesNothing(string? password)
    {
        var owner = await RegisterTenantSessionAsync(_client);
        using var ownerClient = ClientFor(owner.OwnerToken);
        var email = UniqueEmail("blank-pw");

        var response = await ownerClient.PostAsJsonAsync("/api/users", new { email, password });

        await AssertValidationErrorUnderAsync(response, "Password");
        Assert.Empty(await FindUsersByEmailAsync(_factory, email));
    }

    [Fact]
    public async Task CreateUser_WithWeakPassword_DoesNotEchoThePasswordInTheResponse()
    {
        var owner = await RegisterTenantSessionAsync(_client);
        using var ownerClient = ClientFor(owner.OwnerToken);
        const string weakPassword = "hunter2-weak";

        var response = await CreateUserAsync(ownerClient, UniqueEmail("echo"), weakPassword);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(weakPassword, await response.Content.ReadAsStringAsync());
    }

    public static TheoryData<string?> InvalidEmails => new()
    {
        "not-an-email",
        "missing-at-sign.example.test",
        "@example.test",
        "member@",
        "",
        " ",
        null,
        new string('a', 244) + "@example.test", // 257 characters, one above the maximum
    };

    [Theory]
    [MemberData(nameof(InvalidEmails))]
    public async Task CreateUser_WithInvalidEmail_Returns400UnderEmailAndWritesNothing(string? email)
    {
        var owner = await RegisterTenantSessionAsync(_client);
        using var ownerClient = ClientFor(owner.OwnerToken);

        var response = await ownerClient.PostAsJsonAsync("/api/users", new { email, password = StrongPassword });

        await AssertValidationErrorUnderAsync(response, "Email");
        if (!string.IsNullOrWhiteSpace(email))
        {
            Assert.Empty(await FindUsersByEmailAsync(_factory, email));
        }
    }

    [Fact]
    public async Task CreateUser_WithEmptyObject_Returns400ReportingBothFields()
    {
        var owner = await RegisterTenantSessionAsync(_client);
        using var ownerClient = ClientFor(owner.OwnerToken);

        var response = await ownerClient.PostAsJsonAsync("/api/users", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.NotNull(problem);
        Assert.Contains("Email", problem!.Errors.Keys);
        Assert.Contains("Password", problem.Errors.Keys);
    }

    [Theory]
    [InlineData("{ this is not valid json ")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("[]")]
    public async Task CreateUser_WithUnusableJsonBody_Returns400NeverAnUnhandledException(string body)
    {
        var owner = await RegisterTenantSessionAsync(_client);
        using var ownerClient = ClientFor(owner.OwnerToken);

        var response = await ownerClient.PostAsync("/api/users", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateUser_WithWrongContentType_Returns400Or415NeverAnUnhandledException()
    {
        var owner = await RegisterTenantSessionAsync(_client);
        using var ownerClient = ClientFor(owner.OwnerToken);
        var content = new StringContent("email=a@a.test&password=x", Encoding.UTF8, "text/plain");

        var response = await ownerClient.PostAsync("/api/users", content);

        Assert.True(
            response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnsupportedMediaType,
            $"Expected 400 or 415, got {(int)response.StatusCode}.");
    }

    // ---- Accepted boundaries --------------------------------------------------------

    public static TheoryData<string> CompliantPasswords => new()
    {
        "Abcdefghij1k",                 // exactly 12 characters
        "Aa1" + new string('x', 125),   // exactly 128 characters
        "Ñandú-secreto-9",              // non-ASCII uppercase letter
    };

    [Theory]
    [MemberData(nameof(CompliantPasswords))]
    public async Task CreateUser_WithPasswordMeetingThePolicy_Returns201AndThePasswordWorksForLogin(string password)
    {
        var owner = await RegisterTenantSessionAsync(_client);
        using var ownerClient = ClientFor(owner.OwnerToken);
        var email = UniqueEmail("strong");

        var response = await CreateUserAsync(ownerClient, email, password);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await LoginAsync(_client, email, password);
        var wrong = await _client.PostAsJsonAsync("/api/auth/login", new { email, password = password + "x" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
    }

    [Fact]
    public async Task CreateUser_WithPlusTaggedEmail_Returns201AndStoresItVerbatim()
    {
        var owner = await RegisterTenantSessionAsync(_client);
        using var ownerClient = ClientFor(owner.OwnerToken);
        var email = $"first.last+{Guid.NewGuid():N}@sub.example.test";

        var response = await CreateUserAsync(ownerClient, email);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var persisted = Assert.Single(await FindUsersByEmailAsync(_factory, email));
        Assert.Equal(email, persisted.Email);
    }
}
