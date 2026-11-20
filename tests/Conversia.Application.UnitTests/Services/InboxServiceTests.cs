using Conversia.Application.DTOs.Inbox;
using Conversia.Application.Interfaces;
using Conversia.Application.Services;
using Conversia.Domain.Entities;
using Conversia.Domain.Interfaces;
using Conversia.Tests.Shared;
using Moq;

namespace Conversia.Application.UnitTests.Services;

/// <summary>The inbox filters and window badges (docs/MESSAGING.md, "Inbox"). Ordering and the delta are the query's (integration tests).</summary>
public class InboxServiceTests
{
    private static readonly DateTime Now = new(2026, 11, 16, 15, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IInboxQueries> _queries = new();
    private readonly Mock<ITenantRepository> _tenants = new();
    private readonly Mock<IAdvisorRepository> _advisors = new();
    private readonly Mock<ICurrentTenantService> _current = new();
    private readonly Guid _tenant = Guid.NewGuid();
    private InboxFilter? _filter;

    public InboxServiceTests()
    {
        _current.SetupGet(c => c.TenantId).Returns(_tenant);
        _tenants.Setup(r => r.GetSandboxModeAsync(_tenant, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _queries.Setup(q => q.ListAsync(It.IsAny<InboxFilter>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<InboxFilter, int, CancellationToken>((f, _, _) => _filter = f)
            .ReturnsAsync([]);
        _queries.Setup(q => q.GetChangeTokenAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => _token);
    }

    private InboxChangeToken _token = new(Now.AddMinutes(-3), 4, DbNow);

    /// <summary>The database's clock, a little apart from the application's: the cursor follows the database.</summary>
    private static readonly DateTime DbNow = Now.AddSeconds(2);

    private InboxService CreateSut() => new(_queries.Object, _tenants.Object, _advisors.Object, _current.Object, new FixedTimeProvider(Now));

    [Fact]
    public async Task Mine_IsTheAdvisorLinkedToTheCaller()
    {
        var user = Guid.NewGuid();
        var advisor = Advisor.Create(_tenant, Guid.NewGuid(), "Ana", user);
        _advisors.Setup(r => r.GetByUserIdAsync(user, It.IsAny<CancellationToken>())).ReturnsAsync(advisor);

        (await CreateSut().GetAsync(new InboxQuery { Mine = true }, user)).Response!.ToString();

        Assert.Equal(advisor.Id, _filter!.AdvisorId);
    }

    [Fact]
    public async Task Mine_WithoutALinkedAdvisor_IsEmptyAndSaysWhy_WithoutQuerying()
    {
        var response = (await CreateSut().GetAsync(new InboxQuery { Mine = true }, Guid.NewGuid())).Response!;

        Assert.True(response.NoAdvisorLinked);
        Assert.Empty(response.Items);
        Assert.Null(_filter);
    }

    [Fact]
    public async Task TheFiltersAndTheSandboxModeReachTheQuery()
    {
        _tenants.Setup(r => r.GetSandboxModeAsync(_tenant, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var branch = Guid.NewGuid();
        var since = Now.AddMinutes(-1);

        var response = (await CreateSut().GetAsync(new InboxQuery { BranchId = branch, Unassigned = true, AwaitingReply = true, Since = since }, null)).Response!;

        Assert.Equal(new InboxFilter(true, null, branch, true, true, since), _filter);
        Assert.True(response.SandboxMode);
    }

    [Fact]
    public async Task TheCursorIsDatabaseTime_AndOverlapsTheAnswer()
        => Assert.Equal(DbNow - InboxService.CursorOverlap, (await CreateSut().GetAsync(new InboxQuery(), null)).Response!.Cursor);

    [Fact]
    public async Task AVersionThatIsStillCurrent_BuildsNoList()
    {
        var first = await CreateSut().GetAsync(new InboxQuery(), null);
        _filter = null;

        var second = await CreateSut().GetAsync(new InboxQuery(), null, first.Version);

        Assert.Equal(first.Version, second.Version);
        Assert.Null(second.Response);
        Assert.Null(_filter); // the list query never ran
    }

    [Theory]
    [InlineData("change")]
    [InlineData("count")]
    [InlineData("minute")]
    [InlineData("filter")]
    public async Task TheVersionChanges_WithAnyChangeOfTheCompanyTheClockMinuteOrTheFilters(string what)
    {
        var first = await CreateSut().GetAsync(new InboxQuery(), null);
        var query = new InboxQuery();
        _token = what switch
        {
            "change" => _token with { LastChange = _token.LastChange!.Value.AddSeconds(1) },
            "count" => _token with { OpenCount = 5 },
            "minute" => _token with { DatabaseNow = _token.DatabaseNow.AddMinutes(1) },
            _ => _token,
        };
        if (what == "filter")
        {
            query.AwaitingReply = true;
        }

        var second = await CreateSut().GetAsync(query, null, first.Version);

        Assert.NotEqual(first.Version, second.Version);
        Assert.NotNull(second.Response);
    }

    [Fact]
    public async Task ADeltaWithMoreChangesThanTheLimit_AsksForAFullReload()
    {
        _queries.Setup(q => q.ListAsync(It.IsAny<InboxFilter>(), InboxResponse.MaxItems + 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. Enumerable.Range(0, InboxResponse.MaxItems + 1).Select(_ => new InboxItem())]);

        var delta = (await CreateSut().GetAsync(new InboxQuery { Since = Now.AddMinutes(-1) }, null)).Response!;
        var full = (await CreateSut().GetAsync(new InboxQuery(), null)).Response!;

        Assert.True(delta.FullReloadRequired);
        Assert.False(full.FullReloadRequired);
        Assert.True(full.Truncated);
    }

    [Fact]
    public async Task EachItemSaysItsWindow_AsOfTheAnswer()
    {
        _queries.Setup(q => q.ListAsync(It.IsAny<InboxFilter>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new InboxItem { LastInboundAt = Now.AddHours(-1) },
            new InboxItem { LastInboundAt = Now.AddHours(-23) },
            new InboxItem { LastInboundAt = Now.AddHours(-30) },
        ]);

        var items = (await CreateSut().GetAsync(new InboxQuery(), null)).Response!.Items;

        Assert.Equal([(true, false), (true, true), (false, false)], items.Select(i => (i.WindowOpen, i.WindowClosingSoon)));
        Assert.Equal(Now.AddHours(23), items[0].WindowClosesAt);
    }

    [Fact]
    public async Task MoreThanTheLimit_IsTruncated()
    {
        _queries.Setup(q => q.ListAsync(It.IsAny<InboxFilter>(), InboxResponse.MaxItems + 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. Enumerable.Range(0, InboxResponse.MaxItems + 1).Select(_ => new InboxItem())]);

        var response = (await CreateSut().GetAsync(new InboxQuery(), null)).Response!;

        Assert.True(response.Truncated);
        Assert.Equal(InboxResponse.MaxItems, response.Items.Count);
    }

    // ---- The conversation's version follows its window (review fix 7) ----------------------------------------------

    [Fact]
    public async Task ConversationVersion_ChangesWhenTheWindowCloses_WithoutAnyNewMessage()
    {
        var id = Guid.NewGuid();
        _queries.Setup(q => q.GetVersionAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(() => new ConversationVersionResponse
        {
            MessageCount = 3, Status = "Open", Version = "3-x-Open", LastInboundAt = Now.AddHours(-23).AddMinutes(-59),
        });
        var clock = new FixedTimeProvider(Now);
        var sut = new InboxService(_queries.Object, _tenants.Object, _advisors.Object, _current.Object, clock);

        var open = await sut.GetConversationVersionAsync(id);
        clock.Advance(TimeSpan.FromMinutes(2));
        var closed = await sut.GetConversationVersionAsync(id);

        Assert.True(open.WindowOpen);
        Assert.False(closed.WindowOpen);
        Assert.NotEqual(open.Version, closed.Version);
    }
}
