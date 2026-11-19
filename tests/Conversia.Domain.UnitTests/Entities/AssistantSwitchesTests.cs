using Conversia.Domain.Entities;

namespace Conversia.Domain.UnitTests.Entities;

public class AssistantSwitchesTests
{
    private static readonly DateTime T0 = new(2026, 11, 19, 15, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_company_starts_unblocked_and_the_block_is_idempotent()
    {
        var tenant = Tenant.Create("Láser Norte");
        Assert.False(tenant.AssistantBlocked);
        Assert.True(tenant.SetAssistantBlocked(true));
        Assert.True(tenant.AssistantBlocked);
        Assert.False(tenant.SetAssistantBlocked(true));
        Assert.True(tenant.SetAssistantBlocked(false));
    }

    [Fact]
    public void The_global_assistant_switch_defaults_on_and_records_who_changed_it()
    {
        Assert.True(PlatformSwitch.DefaultOf(PlatformSwitch.Assistant));
        Assert.False(PlatformSwitch.DefaultOf("other"));
        var admin = Guid.NewGuid();
        var s = PlatformSwitch.Create(PlatformSwitch.Assistant, true, null, T0);
        Assert.False(s.Set(true, admin, T0.AddHours(1)));
        Assert.Equal(T0, s.ChangedAt);
        Assert.True(s.Set(false, admin, T0.AddHours(2)));
        Assert.False(s.Enabled);
        Assert.Equal(admin, s.ChangedBy);
        Assert.Equal(T0.AddHours(2), s.ChangedAt);
    }

    [Fact]
    public void A_line_starts_without_the_assistant()
    {
        var channel = WhatsAppChannel.Create(Guid.NewGuid(), Guid.NewGuid(), "+57 300 555 0100").Channel;
        Assert.False(channel.AssistantEnabled);
        Assert.True(channel.SetAssistantEnabled(true));
        Assert.False(channel.SetAssistantEnabled(true));
        Assert.True(channel.AssistantEnabled);
    }

    [Fact]
    public void Metrics_name_the_reason()
    {
        Assert.Equal("handoff.max_turns", AssistantMetrics.Handoff(AssistantHandoffReasons.MaxTurns));
        Assert.Equal("discarded.answered", AssistantMetrics.DiscardedBecause("answered"));
    }
}
