using System.Net;
using System.Net.Http.Json;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// Behaviours that exist because of PostgreSQL: text cannot hold U+0000 (so it must be a validation error, never a 500),
/// and <c>timestamptz</c> keeps microseconds (so what a write returns must equal what a later read returns).
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class PostgresValueRulesTests
{
    private const string Nul = "\0";

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PostgresValueRulesTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ANulCharacterInTheTenantName_IsAValidationError_AndNothingIsWritten()
    {
        var email = UniqueEmail("nul-tenant");

        var response = await _client.PostAsJsonAsync("/api/auth/register-tenant", new { tenantName = $"Acme{Nul}Corp", email, password = StrongPassword });

        await AssertValidationErrorUnderAsync(response, "tenantName");
        var login = await _client.PostAsJsonAsync("/api/auth/login", new { email, password = StrongPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode); // the owner was not created either
    }

    [Fact]
    public async Task ANulCharacterInAnEmail_IsAValidationError_OnRegisterLoginAndCreateUser()
    {
        var email = $"nul{Nul}@example.test";

        var register = await _client.PostAsJsonAsync("/api/auth/register-tenant", new { tenantName = UniqueTenantName("nul"), email, password = StrongPassword });
        await AssertValidationErrorUnderAsync(register, "email");

        var login = await _client.PostAsJsonAsync("/api/auth/login", new { email, password = StrongPassword });
        await AssertValidationErrorUnderAsync(login, "email");

        using var tenant = await StartProductTenantAsync(_factory, _client, "nul-user");
        var create = await tenant.Owner.Client.PostAsJsonAsync("/api/users", new { email, password = StrongPassword });
        await AssertValidationErrorUnderAsync(create, "email");
    }

    [Fact]
    public async Task ANulCharacterInTheProductName_IsAValidationError_OnCreateAndUpdate()
    {
        using var tenant = await StartProductTenantAsync(_factory, _client, "nul-product");
        var client = tenant.Owner.Client;

        await AssertValidationErrorUnderAsync(await PostProductAsync(client, $"Wid{Nul}get", UniqueSku()), "name");

        var created = await CreateProductAsync(client);
        await AssertValidationErrorUnderAsync(await PutProductAsync(client, created.Id, $"Wid{Nul}get", 1m, 1, created.RowVersion), "name");
        await AssertProductUnchangedAsync(client, created);
    }

    [Fact]
    public async Task StampedTimestamps_AreWholeMicroseconds_AndEveryWriteResponseEqualsTheLaterRead()
    {
        using var tenant = await StartProductTenantAsync(_factory, _client, "stamps");
        var client = tenant.Owner.Client;

        var created = await CreateProductAsync(client);
        AssertSameProduct(created, await GetProductOkAsync(client, created.Id));
        Assert.Equal(0, created.CreatedAt.Ticks % 10);

        var updated = await UpdateProductOkAsync(client, created);
        AssertSameProduct(updated, await GetProductOkAsync(client, created.Id));
        Assert.Equal(created.CreatedAt, updated.CreatedAt);
        Assert.Equal(0, updated.UpdatedAt!.Value.Ticks % 10);
    }
}
