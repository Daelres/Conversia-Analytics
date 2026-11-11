using System.Text;
using Conversia.Application.DTOs.Sandbox;
using Conversia.Application.Exceptions;
using Conversia.Application.Ingestion;
using Conversia.Application.Interfaces;
using Conversia.Application.Sandbox;
using Conversia.Application.Services;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;
using Conversia.Domain.Interfaces;
using Conversia.Tests.Shared;
using Conversia.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Conversia.Application.UnitTests.Services;

/// <summary>
/// The simulated customer of the internal sandbox: what it refuses, and that what it accepts is a provider-shaped delivery handed to
/// the real ingestion entry point, marked as sandbox. What ingestion then does with it is checked against PostgreSQL (integration tests).
/// </summary>
public class SandboxServiceTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 15, 0, 0, DateTimeKind.Utc);

    private readonly Mock<ITenantRepository> _tenants = new();
    private readonly Mock<IWhatsAppChannelRepository> _channels = new();
    private readonly Mock<IConversationRepository> _conversations = new();
    private readonly Mock<IContactRepository> _contacts = new();
    private readonly Mock<IWebhookIngestionService> _ingestion = new();
    private readonly Mock<ISandboxDataStore> _store = new();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly List<(WebhookChannelAccess Access, string Body)> _delivered = [];

    private WhatsAppChannel _channel = null!;

    public SandboxServiceTests()
    {
        _ingestion
            .Setup(i => i.StoreAsync(It.IsAny<WebhookChannelAccess>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Callback<WebhookChannelAccess, ReadOnlyMemory<byte>, CancellationToken>((access, body, _) => _delivered.Add((access, Encoding.UTF8.GetString(body.Span))))
            .ReturnsAsync(WebhookStoreResult.Stored);
    }

    private readonly QueuedNumbers _numbers = new();
    private readonly RecordingLogger _logger = new();

    /// <summary>Hands out the numbers a test queued, in order; random ones of the sandbox range once the queue is empty.</summary>
    private sealed class QueuedNumbers : ISandboxCustomerNumbers
    {
        private readonly Random _random = new(11);

        public Queue<string> Queue { get; } = new();

        public List<string> Given { get; } = [];

        public string Next()
        {
            var number = Queue.Count > 0 ? Queue.Dequeue() : Contact.NewSandboxWaId(_random);
            Given.Add(number);
            return number;
        }
    }

    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger<SandboxService>
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Text)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add((logLevel, formatter(state, exception)));
    }

    private SandboxService CreateSut()
        => new(_tenants.Object, _channels.Object, _conversations.Object, _contacts.Object, _ingestion.Object, _store.Object, _numbers,
            new FixedTimeProvider(Now), _logger);

    private void Arrange(bool sandboxMode = true, bool channelActive = true, string? phoneNumberId = "1234567890")
    {
        _tenants.Setup(r => r.GetSandboxModeAsync(_tenant, It.IsAny<CancellationToken>())).ReturnsAsync(sandboxMode);
        (_channel, _) = WhatsAppChannel.Create(_tenant, Guid.NewGuid(), "+57 300 555 0100", phoneNumberId);
        if (channelActive)
        {
            _channel.Activate();
        }

        _channels.Setup(r => r.GetByIdAsync(_channel.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_channel);
    }

    private (Conversation Conversation, Contact Contact) ArrangeConversation(bool sandbox = true, bool closed = false)
    {
        var contact = Contact.Create(_tenant, sandbox ? Contact.NewSandboxWaId(new Random(3)) : "573005550101", "Laura", Now, sandbox);
        var conversation = Conversation.Start(_tenant, contact.Id, _channel.Id, _channel.BranchId, Now, ConversationSource.Organic, isSandbox: sandbox);
        if (closed)
        {
            conversation.Close(Now);
        }

        _conversations.Setup(r => r.GetByIdAsync(conversation.Id, It.IsAny<CancellationToken>())).ReturnsAsync(conversation);
        _contacts.Setup(r => r.GetByIdAsync(contact.Id, It.IsAny<CancellationToken>())).ReturnsAsync(contact);
        return (conversation, contact);
    }

    private Task<SandboxMessageAcceptedResponse> StartAsync(string? name = "Laura Gómez", string? text = "Hola, ¿cuánto cuesta?")
        => CreateSut().StartConversationAsync(_tenant, new StartSandboxConversationRequest { ChannelId = _channel.Id, CustomerName = name, Text = text });

    // ---- Starting a simulated conversation -----------------------------------------------------------------

    [Fact]
    public async Task Start_DeliversOneInboundTextMessage_ThroughTheRealIngestionEntryPoint_MarkedAsSandbox()
    {
        Arrange();

        var accepted = await StartAsync("  Laura Gómez  ", "  Hola, ¿cuánto cuesta?  ");

        var (access, body) = Assert.Single(_delivered);
        Assert.Equal(new WebhookChannelAccess(_channel.Id, _tenant, IsSandbox: true), access);

        // The body is what the real parser reads: one customer message of the channel's own number.
        var parsed = Dialog360PayloadParser.Parse(body, Now);
        var message = Assert.Single(parsed.Messages);
        Assert.Equal((0, 0), (parsed.SkippedCount, parsed.StatusCount));
        Assert.Equal(MessageDirection.Inbound, message.Direction);
        Assert.Equal("text", message.Type);
        Assert.Equal("Hola, ¿cuánto cuesta?", message.Text);
        Assert.Equal("Laura Gómez", message.ContactName);
        Assert.Equal("1234567890", message.PhoneNumberId);
        Assert.Equal(Now, message.SentAt);
        Assert.StartsWith(SandboxIds.MessageIdPrefix, message.ProviderMessageId, StringComparison.Ordinal);

        Assert.Equal(message.ContactWaId, accepted.CustomerWaId);
        Assert.True(Contact.IsSandboxWaId(accepted.CustomerWaId));
        Assert.Equal(Now, accepted.SentAt);
    }

    [Fact]
    public async Task Start_OnAChannelWithoutAProviderNumberId_StillBuildsADeliveryTheParserReads()
    {
        Arrange(phoneNumberId: null);

        await StartAsync();

        var parsed = Dialog360PayloadParser.Parse(Assert.Single(_delivered).Body, Now);
        Assert.Null(Assert.Single(parsed.Messages).PhoneNumberId);
    }

    [Fact]
    public async Task Start_TwiceWithTheSameWords_MakesTwoDifferentCustomersAndTwoDifferentBodies()
    {
        Arrange();

        var first = await StartAsync();
        var second = await StartAsync();

        Assert.NotEqual(first.CustomerWaId, second.CustomerWaId);
        Assert.NotEqual(_delivered[0].Body, _delivered[1].Body);
    }

    [Fact]
    public async Task Start_NeverHandsOutANumberTheCompanyAlreadyHas()
    {
        Arrange();
        string[] offered = ["000001111111111", "000002222222222", "000003333333333"];
        foreach (var number in offered)
        {
            _numbers.Queue.Enqueue(number);
        }

        // The first two already belong to customers of the company; the third is free.
        _contacts.Setup(r => r.GetByWaIdAsync(offered[0], It.IsAny<CancellationToken>())).ReturnsAsync(Contact.Create(_tenant, offered[0], "Otro", Now, true));
        _contacts.Setup(r => r.GetByWaIdAsync(offered[1], It.IsAny<CancellationToken>())).ReturnsAsync(Contact.Create(_tenant, offered[1], "Otra", Now));

        var accepted = await StartAsync();

        Assert.Equal(offered[2], accepted.CustomerWaId);
        Assert.Equal(offered, _numbers.Given);
        Assert.Equal(offered[2], Assert.Single(Dialog360PayloadParser.Parse(Assert.Single(_delivered).Body, Now).Messages).ContactWaId);
    }

    [Fact]
    public async Task Start_WhenNoFreeNumberIsFound_GivesUp_InsteadOfWritingIntoSomebodyElsesConversation()
    {
        Arrange();
        _contacts.Setup(r => r.GetByWaIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string waId, CancellationToken _) => Contact.Create(_tenant, waId, "Otro", Now, true));

        await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync());

        Assert.Equal(10, _numbers.Given.Count);
        Assert.Empty(_delivered);
    }

    [Fact]
    public async Task Start_WhileSandboxModeIsOff_IsRefused_AndNothingIsDelivered()
    {
        Arrange(sandboxMode: false);

        var failure = await Assert.ThrowsAsync<SandboxException>(() => StartAsync());

        Assert.Equal(SandboxProblem.ModeOff, failure.Problem);
        Assert.Equal("sandbox_mode_off", failure.Code);
        Assert.Empty(_delivered);
    }

    [Fact]
    public async Task Start_OnAnUnknownOrForeignChannel_IsNotFound()
    {
        Arrange();

        await Assert.ThrowsAsync<ChannelNotFoundException>(() => CreateSut().StartConversationAsync(
            _tenant, new StartSandboxConversationRequest { ChannelId = Guid.NewGuid(), CustomerName = "Laura", Text = "Hola" }));

        Assert.Empty(_delivered);
    }

    [Fact]
    public async Task Start_OnAnInactiveChannel_IsRefused()
    {
        Arrange(channelActive: false);

        var failure = await Assert.ThrowsAsync<SandboxException>(() => StartAsync());

        Assert.Equal(SandboxProblem.ChannelInactive, failure.Problem);
        Assert.Empty(_delivered);
    }

    [Theory]
    [InlineData(null, "Hola")]
    [InlineData("  ", "Hola")]
    [InlineData("Laura", null)]
    [InlineData("Laura", "   ")]
    [InlineData("Laura", "nul\0")]
    public async Task Start_WithoutANameOrAText_IsADomainError(string? name, string? text)
    {
        Arrange();

        await Assert.ThrowsAsync<DomainException>(() => StartAsync(name, text));

        Assert.Empty(_delivered);
    }

    [Fact]
    public async Task Start_WhenIngestionDoesNotStoreTheDelivery_Fails_InsteadOfSayingItWasAccepted()
    {
        Arrange();
        _ingestion
            .Setup(i => i.StoreAsync(It.IsAny<WebhookChannelAccess>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(WebhookStoreResult.InvalidPayload);

        await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync());
    }

    // ---- Writing as the customer ------------------------------------------------------------------------

    [Fact]
    public async Task CustomerMessage_IsDeliveredFromTheConversationsOwnCustomer_MarkedAsSandbox()
    {
        Arrange();
        var (conversation, contact) = ArrangeConversation();

        var accepted = await CreateSut().SendCustomerMessageAsync(_tenant, conversation.Id, new SandboxCustomerMessageRequest { Text = " Me parece caro " });

        var (access, body) = Assert.Single(_delivered);
        Assert.True(access.IsSandbox);
        Assert.Equal((_channel.Id, _tenant), (access.ChannelId, access.TenantId));
        var message = Assert.Single(Dialog360PayloadParser.Parse(body, Now).Messages);
        Assert.Equal(contact.WaId, message.ContactWaId);
        Assert.Equal("Laura", message.ContactName);
        Assert.Equal("Me parece caro", message.Text);
        Assert.Equal(MessageDirection.Inbound, message.Direction);
        Assert.Equal(contact.WaId, accepted.CustomerWaId);
    }

    [Fact]
    public async Task CustomerMessage_WrittenInTheSameSecondAsTheLastMessage_TakesTheNextSecond_SoTheOrderIsKept()
    {
        Arrange();
        var (conversation, _) = ArrangeConversation();
        // The advisor answered 400 ms into the current second; the customer replies at once.
        conversation.ApplyTimeline(ConversationTimeline.From([(MessageDirection.Inbound, Now.AddSeconds(-5)), (MessageDirection.Outbound, Now.AddMilliseconds(400))])!);

        var accepted = await CreateSut().SendCustomerMessageAsync(_tenant, conversation.Id, new SandboxCustomerMessageRequest { Text = "Gracias" });

        var message = Assert.Single(Dialog360PayloadParser.Parse(Assert.Single(_delivered).Body, Now).Messages);
        Assert.Equal(Now.AddSeconds(1), message.SentAt);
        Assert.Equal(Now.AddSeconds(1), accepted.SentAt);
        Assert.True(message.SentAt > conversation.LastMessageAt);
    }

    [Fact]
    public async Task CustomerMessage_IntoARealConversation_IsRefused_EvenInSandboxMode()
    {
        Arrange();
        var (conversation, _) = ArrangeConversation(sandbox: false);

        var failure = await Assert.ThrowsAsync<SandboxException>(() =>
            CreateSut().SendCustomerMessageAsync(_tenant, conversation.Id, new SandboxCustomerMessageRequest { Text = "Hola" }));

        Assert.Equal(SandboxProblem.NotSandboxConversation, failure.Problem);
        Assert.Equal("not_sandbox_conversation", failure.Code);
        Assert.Empty(_delivered);
    }

    [Fact]
    public async Task CustomerMessage_WhileSandboxModeIsOff_IsRefused()
    {
        Arrange(sandboxMode: false);
        var (conversation, _) = ArrangeConversation();

        var failure = await Assert.ThrowsAsync<SandboxException>(() =>
            CreateSut().SendCustomerMessageAsync(_tenant, conversation.Id, new SandboxCustomerMessageRequest { Text = "Hola" }));

        Assert.Equal(SandboxProblem.ModeOff, failure.Problem);
        Assert.Empty(_delivered);
    }

    [Fact]
    public async Task CustomerMessage_IntoAClosedConversation_IsRefused()
    {
        Arrange();
        var (conversation, _) = ArrangeConversation(closed: true);

        var failure = await Assert.ThrowsAsync<SandboxException>(() =>
            CreateSut().SendCustomerMessageAsync(_tenant, conversation.Id, new SandboxCustomerMessageRequest { Text = "Hola" }));

        Assert.Equal(SandboxProblem.ConversationClosed, failure.Problem);
        Assert.Equal("conversation_closed", failure.Code);
        Assert.Empty(_delivered);
    }

    [Fact]
    public async Task CustomerMessage_IntoAnUnknownOrForeignConversation_IsNotFound()
    {
        Arrange();

        await Assert.ThrowsAsync<ConversationNotFoundException>(() =>
            CreateSut().SendCustomerMessageAsync(_tenant, Guid.NewGuid(), new SandboxCustomerMessageRequest { Text = "Hola" }));
    }

    // ---- Deleting the sandbox data ------------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeleteData_WorksWhateverTheMode_AndReportsWhatWasRemoved(bool sandboxMode)
    {
        Arrange(sandboxMode);
        _store.Setup(s => s.DeleteAllAsync(_tenant, It.IsAny<CancellationToken>())).ReturnsAsync(new SandboxDataCounts(2, 7, 2, 5));

        var deleted = await CreateSut().DeleteDataAsync(_tenant, Guid.NewGuid());

        Assert.Equal((2, 7, 2, 5), (deleted.Conversations, deleted.Messages, deleted.Contacts, deleted.WebhookEvents));
        _store.Verify(s => s.DeleteAllAsync(_tenant, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteData_LeavesOneLogLine_WithWhoDidItForWhichCompanyAndTheFourCounts()
    {
        Arrange();
        var user = Guid.NewGuid();
        _store.Setup(s => s.DeleteAllAsync(_tenant, It.IsAny<CancellationToken>())).ReturnsAsync(new SandboxDataCounts(21, 73, 22, 54));

        await CreateSut().DeleteDataAsync(_tenant, user);

        var (level, text) = Assert.Single(_logger.Lines);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, level);
        Assert.Contains(_tenant.ToString(), text);
        Assert.Contains(user.ToString(), text);
        Assert.Contains("21 conversations", text);
        Assert.Contains("73 messages", text);
        Assert.Contains("22 contacts", text);
        Assert.Contains("54 webhook events", text);
    }

    [Fact]
    public async Task DeleteData_ThatFails_LogsNothingAsDone()
    {
        Arrange();
        _store.Setup(s => s.DeleteAllAsync(_tenant, It.IsAny<CancellationToken>())).ThrowsAsync(new TimeoutException("lock"));

        await Assert.ThrowsAsync<TimeoutException>(() => CreateSut().DeleteDataAsync(_tenant, Guid.NewGuid()));

        Assert.Empty(_logger.Lines);
    }

    // ---- The exception itself ----------------------------------------------------------------------------

    [Fact]
    public void EverySandboxProblem_HasACodeAndASpanishMessage()
    {
        foreach (var problem in Enum.GetValues<SandboxProblem>())
        {
            var exception = new SandboxException(problem);
            Assert.Matches("^[a-z]+(_[a-z]+)*$", exception.Code);
            Assert.Empty(EnglishLeaks.Find(exception.Message));
            Assert.True(EnglishLeaks.LooksSpanish(exception.Message), exception.Message);
        }
    }
}
