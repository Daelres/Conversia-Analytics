using Conversia.Application.DTOs.Conversations;
using Conversia.Application.Exceptions;
using Conversia.Application.Messaging;
using Conversia.Application.Services;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Interfaces;
using Conversia.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Conversia.Application.UnitTests.Services;

/// <summary>Read receipts and the typing indicator (docs/MESSAGING.md, "Read receipts and typing").</summary>
public class ConversationPresenceServiceTests
{
    private static readonly DateTime Start = new(2026, 11, 16, 15, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IConversationRepository> _conversations = new();
    private readonly Mock<IMessageRepository> _messages = new();
    private readonly Mock<IWhatsAppChannelRepository> _channels = new();
    private readonly Mock<ITenantRepository> _tenants = new();
    private readonly FakeSender _sender = new();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly FixedTimeProvider _clock = new(Start.AddMinutes(1));
    private readonly TypingIndicatorThrottle _throttle;

    private WhatsAppChannel _channel = null!;
    private Conversation _conversation = null!;

    public ConversationPresenceServiceTests()
    {
        _throttle = new TypingIndicatorThrottle(_clock);
    }

    private sealed class FakeSender : IWhatsAppMessageSender, IWhatsAppMessageSenderResolver
    {
        public WhatsAppSendStatus Status { get; set; } = WhatsAppSendStatus.Sent;

        public Exception? Throws { get; set; }

        public List<WhatsAppReadReceipt> Receipts { get; } = [];

        public List<bool> SandboxModes { get; } = [];

        public IWhatsAppMessageSender Resolve(WhatsAppChannel channel, bool sandboxMode)
        {
            SandboxModes.Add(sandboxMode);
            return this;
        }

        public Task<WhatsAppSendResult> SendTextAsync(WhatsAppTextMessage message, CancellationToken cancellationToken = default) => throw new InvalidOperationException();

        public Task<WhatsAppSendResult> SendTemplateAsync(WhatsAppTemplateMessage message, CancellationToken cancellationToken = default) => throw new InvalidOperationException();

        public Task<WhatsAppSendStatus> MarkReadAsync(WhatsAppReadReceipt receipt, CancellationToken cancellationToken = default)
        {
            Receipts.Add(receipt);
            return Throws is not null ? Task.FromException<WhatsAppSendStatus>(Throws) : Task.FromResult(Status);
        }
    }

    private ConversationPresenceService CreateSut()
        => new(_conversations.Object, _messages.Object, _channels.Object, _tenants.Object, _sender, _throttle, _clock, NullLogger<ConversationPresenceService>.Instance);

    private void Arrange(bool enabled = true, bool customerWroteLast = true, bool sandboxMode = false, bool sandboxConversation = false, bool credentials = true, DateTime? lastInbound = null)
    {
        _tenants.Setup(r => r.GetReadReceiptsEnabledAsync(_tenant, It.IsAny<CancellationToken>())).ReturnsAsync(enabled);
        _tenants.Setup(r => r.GetSandboxModeAsync(_tenant, It.IsAny<CancellationToken>())).ReturnsAsync(sandboxMode);
        (_channel, _) = WhatsAppChannel.Create(_tenant, Guid.NewGuid(), "+57 300 555 0100");
        _channel.Activate();
        if (credentials)
        {
            _channel.SetProviderCredentials("v1.1.ciphertext", WhatsAppProviderEnvironment.Production, Start);
        }

        var inbound = lastInbound ?? Start;
        _conversation = Conversation.Start(_tenant, Guid.NewGuid(), _channel.Id, _channel.BranchId, Start, ConversationSource.Organic, isSandbox: sandboxConversation);
        _conversation.ApplyTimeline(ConversationTimeline.From(customerWroteLast
            ? [(MessageDirection.Inbound, inbound)]
            : [(MessageDirection.Inbound, inbound), (MessageDirection.Outbound, inbound.AddSeconds(30))])!);

        _conversations.Setup(r => r.GetByIdAsync(_conversation.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_conversation);
        _conversations
            .Setup(r => r.ChangeSerializedWithIngestionAsync(_tenant, _conversation.Id, It.IsAny<Func<Conversation, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(async (Guid _, Guid _, Func<Conversation, Task> change, CancellationToken _) =>
            {
                await change(_conversation);
                return true;
            });
        _channels.Setup(r => r.GetByIdAsync(_channel.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_channel);
        _messages.Setup(r => r.GetLastInboundAsync(_conversation.Id, It.IsAny<CancellationToken>())).ReturnsAsync(("wamid.LAST", inbound));
    }

    // ---- Read receipts ----------------------------------------------------------------------

    [Fact]
    public async Task MarkRead_WithTheSettingOff_SendsNothing()
    {
        Arrange(enabled: false);

        var response = await CreateSut().MarkReadAsync(_conversation.Id);

        Assert.Equal(PresenceOutcome.Disabled, response.Outcome);
        Assert.Empty(_sender.Receipts);
    }

    [Fact]
    public async Task MarkRead_MarksTheCustomersLatestMessage_WithoutTyping_AndRemembersIt()
    {
        Arrange();

        var response = await CreateSut().MarkReadAsync(_conversation.Id);

        Assert.Equal(PresenceOutcome.Sent, response.Outcome);
        var receipt = Assert.Single(_sender.Receipts);
        Assert.Equal(("wamid.LAST", false), (receipt.ProviderMessageId, receipt.Typing));
        Assert.Same(_channel, receipt.Channel);
        Assert.Equal(Start, _conversation.CustomerReadMarkedAt);
    }

    [Fact]
    public async Task MarkRead_Twice_SendsOnce()
    {
        Arrange();
        var sut = CreateSut();

        await sut.MarkReadAsync(_conversation.Id);
        var second = await sut.MarkReadAsync(_conversation.Id);

        Assert.Equal(PresenceOutcome.NotNeeded, second.Outcome);
        Assert.Single(_sender.Receipts);
    }

    [Fact]
    public async Task MarkRead_AProviderRefusal_IsReportedAsFailed_AndNothingIsRemembered()
    {
        Arrange();
        _sender.Status = WhatsAppSendStatus.Rejected;

        var response = await CreateSut().MarkReadAsync(_conversation.Id);

        Assert.Equal(PresenceOutcome.Failed, response.Outcome);
        Assert.Null(_conversation.CustomerReadMarkedAt);
    }

    [Fact]
    public async Task MarkRead_AnExceptionOfTheProvider_NeverReachesTheAdvisor()
    {
        Arrange();
        _sender.Throws = new HttpRequestException("boom");

        var response = await CreateSut().MarkReadAsync(_conversation.Id);

        Assert.Equal(PresenceOutcome.Failed, response.Outcome);
    }

    [Fact]
    public async Task MarkRead_WithoutAKey_FailsWithoutCallingTheProvider()
    {
        Arrange(credentials: false);

        var response = await CreateSut().MarkReadAsync(_conversation.Id);

        Assert.Equal(PresenceOutcome.Failed, response.Outcome);
        Assert.Empty(_sender.Receipts);
    }

    [Fact]
    public async Task MarkRead_InSandboxMode_GoesThroughTheInternalSender_AndSaysSo()
    {
        Arrange(sandboxMode: true, sandboxConversation: true, credentials: false);

        var response = await CreateSut().MarkReadAsync(_conversation.Id);

        Assert.Equal(PresenceOutcome.Sandbox, response.Outcome);
        Assert.True(Assert.Single(_sender.SandboxModes));
    }

    [Fact]
    public async Task MarkRead_ARealConversationInSandboxMode_SendsNothing()
    {
        Arrange(sandboxMode: true, sandboxConversation: false);

        var response = await CreateSut().MarkReadAsync(_conversation.Id);

        Assert.Equal(PresenceOutcome.Failed, response.Outcome);
        Assert.Empty(_sender.Receipts);
    }

    [Fact]
    public async Task MarkRead_AnotherCompanysConversation_IsNotFound()
    {
        Arrange();

        await Assert.ThrowsAsync<ConversationNotFoundException>(() => CreateSut().MarkReadAsync(Guid.NewGuid()));
    }

    // ---- Typing ----------------------------------------------------------------------

    [Fact]
    public async Task Typing_SendsTheIndicatorOnTheCustomersLatestMessage()
    {
        Arrange();

        var response = await CreateSut().SendTypingAsync(_conversation.Id);

        Assert.Equal(PresenceOutcome.Sent, response.Outcome);
        Assert.Equal(("wamid.LAST", true), (Assert.Single(_sender.Receipts).ProviderMessageId, _sender.Receipts[0].Typing));
    }

    [Fact]
    public async Task Typing_AtMostOnceEvery20Seconds_PerConversation()
    {
        Arrange();
        var sut = CreateSut();

        await sut.SendTypingAsync(_conversation.Id);
        _clock.Advance(TimeSpan.FromSeconds(19));
        var throttled = await sut.SendTypingAsync(_conversation.Id);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var again = await sut.SendTypingAsync(_conversation.Id);

        Assert.Equal(PresenceOutcome.Throttled, throttled.Outcome);
        Assert.Equal(PresenceOutcome.Sent, again.Outcome);
        Assert.Equal(2, _sender.Receipts.Count);
    }

    [Fact]
    public async Task Typing_WhenTheBusinessWroteLast_IsNotPlausible()
    {
        Arrange(customerWroteLast: false);

        var response = await CreateSut().SendTypingAsync(_conversation.Id);

        Assert.Equal(PresenceOutcome.NotNeeded, response.Outcome);
        Assert.Empty(_sender.Receipts);
    }

    [Fact]
    public async Task Typing_OutsideTheWindow_IsNotPlausible()
    {
        Arrange(lastInbound: Start.AddDays(-2));

        var response = await CreateSut().SendTypingAsync(_conversation.Id);

        Assert.Equal(PresenceOutcome.NotNeeded, response.Outcome);
        Assert.Empty(_sender.Receipts);
    }

    [Fact]
    public async Task Typing_WithTheSettingOff_SendsNothing()
    {
        Arrange(enabled: false);

        var response = await CreateSut().SendTypingAsync(_conversation.Id);

        Assert.Equal(PresenceOutcome.Disabled, response.Outcome);
        Assert.Empty(_sender.Receipts);
    }

    [Fact]
    public void Throttle_IsPerConversation()
    {
        var throttle = new TypingIndicatorThrottle(_clock);
        var a = Guid.NewGuid();

        Assert.True(throttle.TryAcquire(a));
        Assert.False(throttle.TryAcquire(a));
        Assert.True(throttle.TryAcquire(Guid.NewGuid()));
    }

    // ---- Cooldown after a failed read receipt (review fix 5) ----------------------------------------------

    [Fact]
    public async Task MarkRead_AfterAFailure_WaitsTheCooldownBeforeTryingAgain()
    {
        Arrange();
        _sender.Status = WhatsAppSendStatus.Rejected;
        var sut = CreateSut();

        var failed = await sut.MarkReadAsync(_conversation.Id);
        _sender.Status = WhatsAppSendStatus.Sent;
        var cooling = await sut.MarkReadAsync(_conversation.Id);
        _clock.Advance(TypingIndicatorThrottle.ReadFailureCooldown);
        var again = await sut.MarkReadAsync(_conversation.Id);

        Assert.Equal([PresenceOutcome.Failed, PresenceOutcome.Throttled, PresenceOutcome.Sent], new[] { failed.Outcome, cooling.Outcome, again.Outcome });
        Assert.Equal(2, _sender.Receipts.Count);
    }

    [Fact]
    public async Task Typing_ThatFails_StillUsesItsSlot()
    {
        Arrange();
        _sender.Status = WhatsAppSendStatus.Rejected;
        var sut = CreateSut();

        await sut.SendTypingAsync(_conversation.Id);
        var next = await sut.SendTypingAsync(_conversation.Id);

        Assert.Equal(PresenceOutcome.Throttled, next.Outcome); // documented: the 20 seconds count from the attempt, sent or not
        Assert.Single(_sender.Receipts);
    }
}
