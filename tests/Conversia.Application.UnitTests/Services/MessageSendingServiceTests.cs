using Conversia.Application.DTOs.Conversations;
using Conversia.Application.Exceptions;
using Conversia.Application.Messaging;
using Conversia.Application.Services;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;
using Conversia.Domain.Interfaces;
using Conversia.Tests.Shared;
using Conversia.TestSupport;
using Microsoft.Extensions.Logging;
using Moq;

namespace Conversia.Application.UnitTests.Services;

/// <summary>
/// What <see cref="MessageSendingService"/> refuses before calling the provider, what it hands to the sender, and what it stores once the
/// provider accepted the message. Tenant isolation and the locks are the repositories' job (integration tests).
/// </summary>
public class MessageSendingServiceTests
{
    private const string CustomerWaId = "573005550101";
    private static readonly DateTime Start = new(2026, 11, 9, 15, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = Start.AddMinutes(5);

    private readonly Mock<IConversationRepository> _conversations = new();
    private readonly Mock<IWhatsAppChannelRepository> _channels = new();
    private readonly Mock<IContactRepository> _contacts = new();
    private readonly Mock<IMessageRepository> _messages = new();
    private readonly FakeSender _sender = new();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly List<Message> _stored = [];

    private WhatsAppChannel _channel = null!;
    private Conversation _conversation = null!;
    private Conversation _locked = null!;

    private sealed class FakeSender : IWhatsAppMessageSender, IWhatsAppMessageSenderResolver
    {
        public WhatsAppSendResult Result { get; set; } = WhatsAppSendResult.Sent("wamid.SENT-1");

        public List<WhatsAppTextMessage> Sent { get; } = [];

        public List<WhatsAppChannel> Resolved { get; } = [];

        public IWhatsAppMessageSender Resolve(WhatsAppChannel channel)
        {
            Resolved.Add(channel);
            return this;
        }

        public Task<WhatsAppSendResult> SendTextAsync(WhatsAppTextMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.FromResult(Result);
        }
    }

    private readonly RecordingLogger _logger = new();

    private sealed class RecordingLogger : ILogger<MessageSendingService>
    {
        public List<(LogLevel Level, string Text)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add((logLevel, formatter(state, exception)));
    }

    private MessageSendingService CreateSut()
        => new(_conversations.Object, _channels.Object, _contacts.Object, _messages.Object, _sender, new FixedTimeProvider(Now), _logger);

    /// <summary>An open conversation with one customer message, on an active channel that has credentials.</summary>
    private void Arrange(bool active = true, bool credentials = true, bool closed = false)
    {
        (_channel, _) = WhatsAppChannel.Create(_tenant, Guid.NewGuid(), "+57 300 555 0100");
        if (active)
        {
            _channel.Activate();
        }

        if (credentials)
        {
            _channel.SetProviderCredentials("v1.1.ciphertext", WhatsAppProviderEnvironment.Sandbox, Start);
        }

        var contact = Contact.Create(_tenant, CustomerWaId, "Laura", Start);
        _conversation = Conversation.Start(_tenant, contact.Id, _channel.Id, _channel.BranchId, Start, ConversationSource.Organic);
        _locked = _conversation; // the row the repository hands over once it is locked
        if (closed)
        {
            _conversation.Close(Start);
        }

        _conversations.Setup(r => r.GetByIdAsync(_conversation.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_conversation);
        _conversations
            .Setup(r => r.ChangeSerializedWithIngestionAsync(_tenant, _conversation.Id, It.IsAny<Func<Conversation, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(async (Guid _, Guid _, Func<Conversation, Task> change, CancellationToken _) =>
            {
                await change(_locked);
                return true;
            });
        _channels.Setup(r => r.GetByIdAsync(_channel.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_channel);
        _contacts.Setup(r => r.GetByIdAsync(contact.Id, It.IsAny<CancellationToken>())).ReturnsAsync(contact);
        _messages.Setup(r => r.AddAsync(It.IsAny<Message>(), It.IsAny<CancellationToken>()))
            .Callback<Message, CancellationToken>((m, _) => _stored.Add(m));
        _messages.Setup(r => r.ListTimesAsync(_conversation.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => [(MessageDirection.Inbound, Start), .. _stored.Select(m => (m.Direction, m.SentAt))]);
    }

    private Task<MessageResponse> SendAsync(string? text = "Hola Laura")
        => CreateSut().SendTextAsync(_conversation.Id, new SendMessageRequest { Text = text });

    // ---- The happy path ----------------------------------------------------------------------

    [Fact]
    public async Task Send_HandsTheTrimmedTextAndTheCustomersNumberToTheSenderOfTheChannel()
    {
        Arrange();

        await SendAsync("  Hola Laura  ");

        Assert.Same(_channel, Assert.Single(_sender.Resolved));
        var sent = Assert.Single(_sender.Sent);
        Assert.Same(_channel, sent.Channel);
        Assert.Equal(CustomerWaId, sent.RecipientWaId);
        Assert.Equal("Hola Laura", sent.Text);
    }

    [Fact]
    public async Task Send_StoresAnOutboundTextMessageWithTheProviderIdAndTheTimeItWasSent()
    {
        Arrange();

        var response = await SendAsync();

        var message = Assert.Single(_stored);
        Assert.Equal(_tenant, message.TenantId);
        Assert.Equal(_conversation.Id, message.ConversationId);
        Assert.Equal(_channel.Id, message.ChannelId);
        Assert.Equal("wamid.SENT-1", message.ProviderMessageId);
        Assert.Equal(MessageDirection.Outbound, message.Direction);
        Assert.Equal("text", message.Type);
        Assert.Equal("Hola Laura", message.Text);
        Assert.Equal(Now, message.SentAt);

        Assert.Equal(message.Id, response.Id);
        Assert.Equal(nameof(MessageDirection.Outbound), response.Direction);
        Assert.Equal("Hola Laura", response.Text);
        Assert.Equal(Now, response.SentAt);
    }

    [Fact]
    public async Task Send_UpdatesTheConversationTheWayIngestionDoes_TimelineAndClassificationRequest()
    {
        Arrange();

        await SendAsync();

        Assert.Equal(Start, _locked.FirstInboundAt);
        Assert.Equal(Now, _locked.FirstResponseAt);
        Assert.Equal(Now, _locked.LastMessageAt);
        Assert.True(_locked.ClassificationDirty);
        Assert.Equal(Now, _locked.ClassificationRequestedAt);
    }

    [Fact]
    public async Task Send_WhenTheProviderGaveNoMessageId_StillStoresTheMessageUnderAnIdOfItsOwn()
    {
        Arrange();
        _sender.Result = WhatsAppSendResult.Sent(null);

        await SendAsync();

        Assert.StartsWith(MessageSendingService.LocalMessageIdPrefix, Assert.Single(_stored).ProviderMessageId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_WhenTheEchoOfTheMessageWasIngestedFirst_DoesNotStoreItTwice()
    {
        Arrange();
        var echoed = Message.Create(_tenant, _conversation.Id, _channel.Id, "wamid.SENT-1", MessageDirection.Outbound, "text", "Hola Laura", Now);
        _messages.Setup(r => r.FindAsync(_channel.Id, "wamid.SENT-1", It.IsAny<CancellationToken>())).ReturnsAsync(echoed);

        var response = await SendAsync();

        Assert.Empty(_stored);
        Assert.Equal(echoed.Id, response.Id);
    }

    [Fact]
    public async Task Send_StoresTheMessageEvenWhenTheCallerGoesAway_AfterTheProviderAcceptedIt()
    {
        Arrange();
        using var cancelled = new CancellationTokenSource();
        var sut = CreateSut();
        _conversations
            .Setup(r => r.ChangeSerializedWithIngestionAsync(_tenant, _conversation.Id, It.IsAny<Func<Conversation, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(async (Guid _, Guid _, Func<Conversation, Task> change, CancellationToken token) =>
            {
                Assert.False(token.CanBeCanceled);
                await change(_locked);
                return true;
            });

        await sut.SendTextAsync(_conversation.Id, new SendMessageRequest { Text = "Hola" }, cancelled.Token);

        Assert.Single(_stored);
    }

    // ---- The conversation or the channel changed while the provider was being called ----------

    [Fact]
    public async Task Send_WhenTheConversationWasClosedMeanwhile_StillStoresTheMessage_AndWarnsWithIdsOnly()
    {
        Arrange();
        _conversations
            .Setup(r => r.ChangeSerializedWithIngestionAsync(_tenant, _conversation.Id, It.IsAny<Func<Conversation, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(async (Guid _, Guid _, Func<Conversation, Task> change, CancellationToken _) =>
            {
                _locked.Close(Start); // closed after the check, before the record
                await change(_locked);
                return true;
            });

        await SendAsync("texto-privado-del-cliente");

        Assert.Equal("wamid.SENT-1", Assert.Single(_stored).ProviderMessageId);
        var (level, text) = Assert.Single(_logger.Lines);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains(_conversation.Id.ToString(), text);
        Assert.Contains(_channel.Id.ToString(), text);
        Assert.DoesNotContain("texto-privado", text);
        Assert.DoesNotContain(CustomerWaId, text);
    }

    [Fact]
    public async Task Send_WhenTheChannelWasSwitchedOffMeanwhile_StillStoresTheMessage_AndWarnsOnce()
    {
        Arrange();
        var (switchedOff, _) = WhatsAppChannel.Create(_tenant, _channel.BranchId, "+57 300 555 0100");
        _channels.SetupSequence(r => r.GetByIdAsync(_channel.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_channel)       // the check: active
            .ReturnsAsync(switchedOff);   // the record: somebody switched it off

        await SendAsync();

        Assert.Single(_stored);
        var (level, text) = Assert.Single(_logger.Lines);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains(_conversation.Id.ToString(), text);
    }

    [Fact]
    public async Task Send_WhenNothingChangedMeanwhile_LogsNothing()
    {
        Arrange();

        await SendAsync();

        Assert.Empty(_logger.Lines);
    }

    [Fact]
    public void TheAnswerToAnUnknownDelivery_TellsTheOperatorToVerifyBeforeSendingAgain()
    {
        var message = new MessagingException(MessagingProblem.ProviderUnavailable).Message;

        Assert.Contains("no se sabe si el mensaje", message);
        Assert.Contains("Verifica", message);
    }

    // ---- Refused before the provider is called -----------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nul\0inside")]
    public async Task Send_RefusesATextThatCannotBeStored_BeforeLookingAnythingUp(string? text)
    {
        Arrange();

        await Assert.ThrowsAsync<DomainException>(() => SendAsync(text));
        await Assert.ThrowsAsync<DomainException>(() => SendAsync(new string('x', Message.TextMaxLength + 1)));

        Assert.Empty(_sender.Sent);
        _conversations.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Send_AcceptsATextOfExactlyTheMaximumLengthAfterTrimming()
    {
        Arrange();

        await SendAsync(" " + new string('x', Message.TextMaxLength) + " ");

        Assert.Equal(Message.TextMaxLength, Assert.Single(_sender.Sent).Text.Length);
    }

    [Fact]
    public async Task Send_ToAnUnknownConversation_IsNotFound_AndNothingIsSent()
    {
        Arrange();

        await Assert.ThrowsAsync<ConversationNotFoundException>(
            () => CreateSut().SendTextAsync(Guid.NewGuid(), new SendMessageRequest { Text = "Hola" }));

        Assert.Empty(_sender.Sent);
    }

    [Theory]
    [InlineData(true, true, true, MessagingProblem.ConversationClosed)]
    [InlineData(false, true, false, MessagingProblem.ChannelInactive)]
    [InlineData(true, false, false, MessagingProblem.CredentialsMissing)]
    public async Task Send_RefusesWhenTheConversationOrItsChannelCannotSend(bool active, bool credentials, bool closed, MessagingProblem expected)
    {
        Arrange(active, credentials, closed);

        var exception = await Assert.ThrowsAsync<MessagingException>(() => SendAsync());

        Assert.Equal(expected, exception.Problem);
        Assert.Empty(_sender.Sent);
        Assert.Empty(_stored);
    }

    // ---- The provider said no ----------------------------------------------------------------

    [Theory]
    [InlineData(WhatsAppSendStatus.InvalidCredentials, MessagingProblem.InvalidCredentials)]
    [InlineData(WhatsAppSendStatus.RecipientNotAllowed, MessagingProblem.RecipientNotAllowed)]
    [InlineData(WhatsAppSendStatus.LimitReached, MessagingProblem.LimitReached)]
    [InlineData(WhatsAppSendStatus.Rejected, MessagingProblem.Rejected)]
    [InlineData(WhatsAppSendStatus.TransientFailure, MessagingProblem.ProviderUnavailable)]
    public async Task Send_WhenTheProviderDoesNotAcceptTheMessage_SaysWhy_AndStoresNothing(WhatsAppSendStatus status, MessagingProblem expected)
    {
        Arrange();
        _sender.Result = WhatsAppSendResult.Failed(status);

        var exception = await Assert.ThrowsAsync<MessagingException>(() => SendAsync());

        Assert.Equal(expected, exception.Problem);
        Assert.Empty(_stored);
        _conversations.Verify(
            r => r.ChangeSerializedWithIngestionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Func<Conversation, Task>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ---- Sent, but the record failed ---------------------------------------------------------

    [Fact]
    public async Task Send_WhenTheMessageWasSentButCannotBeStored_SaysSo_SoNobodySendsItAgain()
    {
        Arrange();
        _conversations
            .Setup(r => r.ChangeSerializedWithIngestionAsync(_tenant, _conversation.Id, It.IsAny<Func<Conversation, Task>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is down"));

        var exception = await Assert.ThrowsAsync<MessagingException>(() => SendAsync());

        Assert.Equal(MessagingProblem.SentNotRecorded, exception.Problem);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    // ---- The exception itself ----------------------------------------------------------------

    [Fact]
    public void EveryMessagingProblem_HasItsOwnCodeAndASpanishMessage()
    {
        var problems = Enum.GetValues<MessagingProblem>();
        var codes = problems.Select(p => new MessagingException(p).Code).ToList();

        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.All(codes, code => Assert.Matches("^[a-z]+(_[a-z]+)*$", code));
        foreach (var problem in problems)
        {
            var message = new MessagingException(problem).Message;
            Assert.Empty(EnglishLeaks.Find(message));
            Assert.True(EnglishLeaks.LooksSpanish(message), message);
        }
    }
}
