using System.Net;
using System.Net.Http.Json;
using Conversia.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace Conversia.IntegrationTests;

/// <summary>Helpers shared by every feature's integration tests: a registered tenant, its callers and the generic response assertions.</summary>
internal static partial class TestHelpers
{
    /// <summary>An authenticated caller (owner or member) with its own client; disposing releases the client.</summary>
    internal sealed class TestActor : IDisposable
    {
        public TestActor(CustomWebApplicationFactory factory, Guid userId, string token)
        {
            UserId = userId;
            Token = token;
            Client = AuthorizedClient(factory, token);
        }

        public Guid UserId { get; }

        public string Token { get; }

        public HttpClient Client { get; }

        public void Dispose() => Client.Dispose();
    }

    /// <summary>A freshly registered tenant with its owner signed in.</summary>
    internal sealed class TestTenant : IDisposable
    {
        private readonly CustomWebApplicationFactory _factory;
        private readonly HttpClient _anonymous;

        public TestTenant(CustomWebApplicationFactory factory, HttpClient anonymous, TenantSession session)
        {
            _factory = factory;
            _anonymous = anonymous;
            Session = session;
            Owner = new TestActor(factory, session.OwnerId, session.OwnerToken);
        }

        public TenantSession Session { get; }

        public Guid TenantId => Session.TenantId;

        public TestActor Owner { get; }

        /// <summary>Seeds a non-owner member of this tenant holding exactly <paramref name="permissions"/>, and signs them in.</summary>
        public async Task<TestActor> AddMemberAsync(string prefix, params Permission[] permissions)
        {
            var member = await SeedMemberAsync(_factory, _anonymous, TenantId, prefix);
            foreach (var permission in permissions)
            {
                await GrantAsync(Owner.Client, member.UserId, permission);
            }

            return new TestActor(_factory, member.UserId, member.Token);
        }

        public void Dispose() => Owner.Dispose();
    }

    internal static async Task<TestTenant> StartTenantAsync(
        CustomWebApplicationFactory factory, HttpClient anonymous, string prefix = "tenant", bool pending = false)
        => new(factory, anonymous, await RegisterTenantSessionAsync(anonymous, prefix, pending));

    /// <summary>A 400 whose validation errors include <paramref name="key"/> (property name, matched case-insensitively).</summary>
    public static async Task AssertValidationErrorUnderAsync(HttpResponseMessage response, string key)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.NotNull(problem);
        Assert.True(
            problem!.Errors.Any(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase) && e.Value.Length > 0),
            $"Expected a validation error under '{key}' but got keys: {string.Join(", ", problem.Errors.Keys)}.");
    }

    /// <summary>The generic markers of a leaked failure; a handled error body must never contain them.</summary>
    public static void AssertNoInternalsLeaked(string body)
    {
        foreach (var marker in new[] { "StackTrace", "   at ", "Exception", "SqlException", "IX_", "Cannot insert duplicate", "DbUpdate" })
        {
            Assert.DoesNotContain(marker, body, StringComparison.Ordinal);
        }
    }
}
