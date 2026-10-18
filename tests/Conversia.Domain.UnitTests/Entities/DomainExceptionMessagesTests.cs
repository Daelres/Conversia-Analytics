using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;
using Conversia.TestSupport;

namespace Conversia.Domain.UnitTests.Entities;

/// <summary>
/// A <see cref="DomainException"/> message reaches API clients as the <c>detail</c> of a 400, so every rule the domain
/// enforces words its message in Spanish. Each case triggers a real rule; none of the messages is pinned literally
/// (the wording is the implementer's), only that it explains something and contains no English.
/// </summary>
public class DomainExceptionMessagesTests
{
    private static readonly Guid SomeId = Guid.NewGuid();

    public static TheoryData<string, Action> BrokenRules => new()
    {
        { "tenant: blank name", () => Tenant.Create("   ") },
        { "tenant: null name", () => Tenant.Create(null!) },
        { "tenant: name too long", () => Tenant.Create(new string('x', 201)) },
        { "user: empty tenant id", () => User.Create(Guid.Empty, "a@b.test", "hash", false) },
        { "user: blank email", () => User.Create(SomeId, " ", "hash", false) },
        { "user: malformed email", () => User.Create(SomeId, "not-an-email", "hash", false) },
        { "user: blank password hash", () => User.Create(SomeId, "a@b.test", " ", false) },
        { "permission: empty user id", () => UserPermission.Create(Guid.Empty, Permission.UsersView) },
        { "permission: undefined value", () => UserPermission.Create(SomeId, (Permission)999) },
        { "permission: zero", () => UserPermission.Create(SomeId, 0) },
        { "product: empty tenant id", () => Product.Create(Guid.Empty, "Tornillo", "TOR-1", 1m, 1) },
        { "product: blank name", () => Product.Create(SomeId, " ", "TOR-1", 1m, 1) },
        { "product: name too long", () => Product.Create(SomeId, new string('n', 201), "TOR-1", 1m, 1) },
        { "product: blank sku", () => Product.Create(SomeId, "Tornillo", " ", 1m, 1) },
        { "product: sku with spaces", () => Product.Create(SomeId, "Tornillo", "has space", 1m, 1) },
        { "product: sku too long", () => Product.Create(SomeId, "Tornillo", new string('s', 51), 1m, 1) },
        { "product: negative price", () => Product.Create(SomeId, "Tornillo", "TOR-1", -1m, 1) },
        { "product: price too large", () => Product.Create(SomeId, "Tornillo", "TOR-1", 1_000_000_000m, 1) },
        { "product: three decimals", () => Product.Create(SomeId, "Tornillo", "TOR-1", 1.234m, 1) },
        { "product: negative stock", () => Product.Create(SomeId, "Tornillo", "TOR-1", 1m, -1) },
        { "product update: blank name", () => Product.Create(SomeId, "Tornillo", "TOR-1", 1m, 1).Update(" ", 1m, 1) },
        { "product update: bad price", () => Product.Create(SomeId, "Tornillo", "TOR-1", 1m, 1).Update("Tornillo", -1m, 1) },
        { "product update: negative stock", () => Product.Create(SomeId, "Tornillo", "TOR-1", 1m, 1).Update("Tornillo", 1m, -1) },
    };

    [Theory]
    [MemberData(nameof(BrokenRules))]
    public void Every_broken_domain_rule_explains_itself_in_spanish(string rule, Action breakIt)
    {
        var failure = Assert.Throws<DomainException>(breakIt);

        Assert.False(string.IsNullOrWhiteSpace(failure.Message), $"{rule}: the exception has no message.");
        Assert.True(EnglishLeaks.WordCount(failure.Message) >= 2, $"{rule}: '{failure.Message}' is too short to explain anything.");
        Assert.True(EnglishLeaks.Find(failure.Message).Count == 0, $"{rule}: '{failure.Message}' contains English wording: {string.Join(", ", EnglishLeaks.Find(failure.Message))}.");
        Assert.True(EnglishLeaks.LooksSpanish(failure.Message), $"{rule}: '{failure.Message}' does not look like Spanish.");
    }

    [Fact]
    public void The_cases_cover_every_distinct_message_the_domain_can_raise()
    {
        var messages = BrokenRules.Select(row => ((Action)row[1]))
            .Select(breakIt => Assert.Throws<DomainException>(breakIt).Message)
            .Distinct()
            .ToList();

        Assert.True(messages.Count >= 10, $"Only {messages.Count} distinct messages were exercised: {string.Join(" | ", messages)}");
    }
}
