using Conversia.Application.DTOs.Conversations;
using Conversia.Application.Exceptions;
using Conversia.Application.Messaging;
using Conversia.Application.Sandbox;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;
using Conversia.Domain.Interfaces;
using Moq;

namespace Conversia.Application.UnitTests.Services;

/// <summary>The customer service window and template sends (docs/MESSAGING.md, "Customer service window" and "Templates").</summary>
public partial class MessageSendingServiceTests
{
    private readonly Mock<IMessageTemplateRepository> _templates = new();

    private MessageTemplate ApprovedTemplate(string name = "recordatorio_cita", string body = "Hola {{1}}, tu cita es el {{2}}.", TemplateStatus status = TemplateStatus.Approved, bool supported = true)
    {
        var template = MessageTemplate.Create(_tenant, _channel.Id, name, "es", TemplateCategory.Utility, status, body, supported, Start);
        _templates.Setup(r => r.FindAsync(_channel.Id, name, "es", It.IsAny<CancellationToken>())).ReturnsAsync(template);
        return template;
    }

    private Task<MessageResponse> SendTemplateAsync(string name = "recordatorio_cita", params string?[] values)
        => CreateSut().SendTemplateAsync(_conversation.Id, new SendTemplateMessageRequest { TemplateName = name, Language = "es", Parameters = [.. values] }, Guid.NewGuid());

    // ---- The window ----------------------------------------------------------------------

    [Fact]
    public async Task SendText_MoreThan24HoursAfterTheCustomersLastMessage_IsRefusedBeforeTheProviderIsCalled()
    {
        Arrange(lastCustomerMessageAt: Now.AddHours(-24));

        var ex = await Assert.ThrowsAsync<MessagingException>(() => SendAsync());

        Assert.Equal(MessagingProblem.WindowClosed, ex.Problem);
        Assert.Equal("customer_service_window_closed", ex.Code);
        Assert.Empty(_sender.Sent);
        Assert.Empty(_sender.Resolved);
        Assert.Empty(_stored);
    }

    [Fact]
    public async Task SendText_JustInsideTheWindow_IsSent()
    {
        Arrange(lastCustomerMessageAt: Now.AddHours(-24).AddSeconds(1));

        await SendAsync();

        Assert.Single(_sender.Sent);
    }

    [Fact]
    public async Task SendText_WhenTheCustomerNeverWrote_IsRefused()
    {
        Arrange();
        _conversation.ApplyTimeline(ConversationTimeline.From([(MessageDirection.Outbound, Start)])!);

        var ex = await Assert.ThrowsAsync<MessagingException>(() => SendAsync());

        Assert.Equal(MessagingProblem.WindowClosed, ex.Problem);
        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task SendText_InSandboxMode_FollowsTheSameWindowRule()
    {
        Arrange(credentials: false, sandboxConversation: true, sandboxMode: true, lastCustomerMessageAt: Now.AddDays(-2));

        var ex = await Assert.ThrowsAsync<MessagingException>(() => SendAsync());

        Assert.Equal(MessagingProblem.WindowClosed, ex.Problem);
        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task SendText_TheProvidersOwnWindowRefusal_IsTheSameProblem()
    {
        Arrange();
        _sender.Result = WhatsAppSendResult.Failed(WhatsAppSendStatus.WindowClosed);

        var ex = await Assert.ThrowsAsync<MessagingException>(() => SendAsync());

        Assert.Equal(MessagingProblem.WindowClosed, ex.Problem);
        Assert.Empty(_stored);
    }

    [Fact]
    public async Task SendText_AClosedWindow_IsCountedForMonitoring()
    {
        Arrange(lastCustomerMessageAt: Now.AddDays(-3));

        await Assert.ThrowsAsync<MessagingException>(() => SendAsync());

        Assert.Equal("customer_service_window_closed", Assert.Single(_outcomes.Recorded).Outcome);
    }

    // ---- Templates ----------------------------------------------------------------------

    [Fact]
    public async Task SendTemplate_OutsideTheWindow_HandsNameLanguageAndOrderedValuesToTheSender()
    {
        Arrange(lastCustomerMessageAt: Now.AddDays(-5));
        ApprovedTemplate();

        await SendTemplateAsync("recordatorio_cita", " Laura ", "lunes 3 p. m.");

        var sent = Assert.Single(_sender.SentTemplates);
        Assert.Same(_channel, sent.Channel);
        Assert.Equal(CustomerWaId, sent.RecipientWaId);
        Assert.Equal(("recordatorio_cita", "es"), (sent.Name, sent.Language));
        Assert.Equal(["Laura", "lunes 3 p. m."], sent.Parameters.Select(p => p.Value));
        Assert.All(sent.Parameters, p => Assert.Null(p.Name)); // positional placeholders travel without names
        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task SendTemplate_NamedPlaceholders_TravelWithTheirNames()
    {
        Arrange();
        ApprovedTemplate(body: "Gracias, {{first_name}}! Tu pedido {{order_number}} está listo.");

        await SendTemplateAsync("recordatorio_cita", "Laura", "A-42");

        var sent = Assert.Single(_sender.SentTemplates);
        Assert.Equal(["first_name", "order_number"], sent.Parameters.Select(p => p.Name));
    }

    [Fact]
    public async Task SendTemplate_IsRecordedAsTheRenderedText_WithTemplateNameCategoryAndAuthor()
    {
        Arrange(lastCustomerMessageAt: Now.AddDays(-5));
        ApprovedTemplate();
        var user = Guid.NewGuid();
        var advisor = Advisor.Create(_tenant, _channel.BranchId, "Ana", user);
        _advisors.Setup(r => r.GetByUserIdAsync(user, It.IsAny<CancellationToken>())).ReturnsAsync(advisor);

        var response = await CreateSut().SendTemplateAsync(
            _conversation.Id, new SendTemplateMessageRequest { TemplateName = "recordatorio_cita", Language = "es", Parameters = ["Laura", "lunes"] }, user);

        var message = Assert.Single(_stored);
        Assert.Equal(MessageDirection.Outbound, message.Direction);
        Assert.Equal("template", message.Type);
        Assert.Equal("Hola Laura, tu cita es el lunes.", message.Text);
        Assert.Equal(("recordatorio_cita", (TemplateCategory?)TemplateCategory.Utility), (message.TemplateName, message.TemplateCategory));
        Assert.Equal((MessageSender.Conversia, (Guid?)user, (Guid?)advisor.Id), (message.Sender, message.SentByUserId, message.SentByAdvisorId));
        Assert.Equal(("recordatorio_cita", "Utility"), (response.TemplateName, response.TemplateCategory));
    }

    [Theory]
    [InlineData(TemplateStatus.Pending, true)]
    [InlineData(TemplateStatus.Paused, true)]
    [InlineData(TemplateStatus.Rejected, true)]
    [InlineData(TemplateStatus.Approved, false)] // approved but not something Conversia can send (a media header)
    public async Task SendTemplate_ThatCannotBeSent_IsRefusedBeforeTheProviderIsCalled(TemplateStatus status, bool supported)
    {
        Arrange();
        ApprovedTemplate(status: status, supported: supported);

        var ex = await Assert.ThrowsAsync<MessagingException>(() => SendTemplateAsync("recordatorio_cita", "Laura", "lunes"));

        Assert.Equal(MessagingProblem.TemplateUnavailable, ex.Problem);
        Assert.Equal("template_unavailable", ex.Code);
        Assert.Empty(_sender.SentTemplates);
    }

    [Fact]
    public async Task SendTemplate_UnknownToTheChannel_IsRefused()
    {
        Arrange();

        var ex = await Assert.ThrowsAsync<MessagingException>(() => SendTemplateAsync("no_existe", "Laura"));

        Assert.Equal(MessagingProblem.TemplateUnavailable, ex.Problem);
        Assert.Empty(_sender.SentTemplates);
    }

    [Theory]
    [InlineData("Laura")]
    [InlineData("Laura|lunes|extra")]
    [InlineData("Laura|dos\nlíneas")]
    [InlineData("Laura|")]
    public async Task SendTemplate_WithValuesThatDoNotFit_IsAValidationErrorAndNothingIsSent(string joined)
    {
        Arrange();
        ApprovedTemplate();

        await Assert.ThrowsAsync<DomainException>(() => SendTemplateAsync("recordatorio_cita", joined.Split('|')));

        Assert.Empty(_sender.SentTemplates);
        Assert.Empty(_outcomes.Recorded); // a request that was not valid is not a send
    }

    [Fact]
    public async Task SendTemplate_KeepsTheChecksOfAText_AClosedConversationIsRefused()
    {
        Arrange(closed: true);
        ApprovedTemplate();

        var ex = await Assert.ThrowsAsync<MessagingException>(() => SendTemplateAsync("recordatorio_cita", "Laura", "lunes"));

        Assert.Equal(MessagingProblem.ConversationClosed, ex.Problem);
        Assert.Empty(_sender.SentTemplates);
    }

    [Fact]
    public async Task SendTemplate_ProviderRefusal_IsMappedLikeAText()
    {
        Arrange();
        ApprovedTemplate();
        _sender.Result = WhatsAppSendResult.Failed(WhatsAppSendStatus.Rejected);

        var ex = await Assert.ThrowsAsync<MessagingException>(() => SendTemplateAsync("recordatorio_cita", "Laura", "lunes"));

        Assert.Equal(MessagingProblem.Rejected, ex.Problem);
        Assert.Empty(_stored);
    }

    [Fact]
    public async Task SendTemplate_InSandboxMode_AcceptsAFixedSandboxTemplate_WithoutAKeyAndWithoutTheRepository()
    {
        Arrange(credentials: false, sandboxConversation: true, sandboxMode: true, lastCustomerMessageAt: Now.AddDays(-2));

        await SendTemplateAsync("conversia_recordatorio_cita", "Laura", "lunes");

        Assert.True(Assert.Single(_sender.ResolvedSandboxModes));
        Assert.Single(_sender.SentTemplates);
        var message = Assert.Single(_stored);
        Assert.Equal("conversia_recordatorio_cita", message.TemplateName);
        _templates.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SendTemplate_InSandboxMode_RefusesATemplateOutsideTheFixedList()
    {
        Arrange(credentials: false, sandboxConversation: true, sandboxMode: true);
        ApprovedTemplate(); // a real template of the channel is not a sandbox template

        var ex = await Assert.ThrowsAsync<MessagingException>(() => SendTemplateAsync("recordatorio_cita", "Laura", "lunes"));

        Assert.Equal(MessagingProblem.TemplateUnavailable, ex.Problem);
        Assert.Empty(_sender.SentTemplates);
    }

    [Fact]
    public void SandboxTemplates_AreApprovedAndSendable()
        => Assert.All(SandboxTemplates.For(WhatsAppChannel.Create(_tenant, Guid.NewGuid(), "+57 300 555 0100").Channel, Start), t => Assert.True(t.CanBeSent));
}
