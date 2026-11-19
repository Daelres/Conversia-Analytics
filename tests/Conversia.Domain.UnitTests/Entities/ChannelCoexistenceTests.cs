using Conversia.Domain.Entities;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.UnitTests.Entities;

/// <summary>A number shared between the WhatsApp Business app and the API (docs/MESSAGING.md, "Coexistence").</summary>
public class ChannelCoexistenceTests
{
    private static readonly DateTime T0 = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    private static WhatsAppChannel NewChannel() => WhatsAppChannel.Create(Guid.NewGuid(), Guid.NewGuid(), "+57 300 555 0100").Channel;

    [Fact]
    public void PartnerRemoved_FlagsTheChannelAsDisconnected_WithItsReason()
    {
        var channel = NewChannel();

        Assert.True(channel.ApplyAccountUpdate("PARTNER_REMOVED", "PRIMARY_INACTIVITY", T0));

        Assert.True(channel.IsProviderDisconnected);
        Assert.Equal("PARTNER_REMOVED", channel.ProviderDisconnectionEvent);
        Assert.Equal("PRIMARY_INACTIVITY", channel.ProviderDisconnectionReason);
        Assert.Equal(T0, channel.ProviderDisconnectedAt);
    }

    [Fact]
    public void Offboarded_FlagsItToo()
    {
        var channel = NewChannel();

        channel.ApplyAccountUpdate("ACCOUNT_OFFBOARDED", null, T0);

        Assert.True(channel.IsProviderDisconnected);
        Assert.Null(channel.ProviderDisconnectionReason);
    }

    [Fact]
    public void Reconnected_ClearsTheFlag()
    {
        var channel = NewChannel();
        channel.ApplyAccountUpdate("PARTNER_REMOVED", "PRIMARY_INACTIVITY", T0);

        Assert.True(channel.ApplyAccountUpdate("ACCOUNT_RECONNECTED", null, T0.AddHours(1)));

        Assert.False(channel.IsProviderDisconnected);
        Assert.Null(channel.ProviderDisconnectionEvent);
        Assert.Null(channel.ProviderDisconnectionReason);
    }

    [Fact]
    public void AnOlderNotification_ChangesNothing()
    {
        var channel = NewChannel();
        channel.ApplyAccountUpdate("ACCOUNT_RECONNECTED", null, T0);

        Assert.False(channel.ApplyAccountUpdate("PARTNER_REMOVED", "PRIMARY_INACTIVITY", T0.AddMinutes(-1)));

        Assert.False(channel.IsProviderDisconnected);
    }

    [Fact]
    public void AnUnknownEvent_ChangesNothing()
        => Assert.False(NewChannel().ApplyAccountUpdate("VERIFIED_ACCOUNT", null, T0));

    [Fact]
    public void HistoryImport_IsOffByDefault()
    {
        var channel = NewChannel();

        Assert.False(channel.HistoryImportEnabled);
        Assert.False(channel.HistoryAiAnalysisEnabled);
        Assert.Null(channel.HistoryImportConsentedBy);
    }

    [Fact]
    public void EnablingHistoryImport_RecordsWhoConfirmedTheLegalBasisAndWhen()
    {
        var channel = NewChannel();
        var owner = Guid.NewGuid();

        channel.EnableHistoryImport(owner, T0, analyzeWithAi: true);

        Assert.True(channel.HistoryImportEnabled);
        Assert.True(channel.HistoryAiAnalysisEnabled);
        Assert.Equal(owner, channel.HistoryImportConsentedBy);
        Assert.Equal(T0, channel.HistoryImportConsentedAt);
    }

    [Fact]
    public void DisablingIt_StopsTheImportAndTheAnalysis_ButKeepsTheConsentTrail()
    {
        var channel = NewChannel();
        var owner = Guid.NewGuid();
        channel.EnableHistoryImport(owner, T0, analyzeWithAi: true);

        channel.DisableHistoryImport();

        Assert.False(channel.HistoryImportEnabled);
        Assert.False(channel.HistoryAiAnalysisEnabled);
        Assert.Equal(owner, channel.HistoryImportConsentedBy);
    }

    [Fact]
    public void EnablingWithoutAUser_IsRefused()
        => Assert.Throws<DomainException>(() => NewChannel().EnableHistoryImport(Guid.Empty, T0, false));
}
