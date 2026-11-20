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

/// <summary>Syncing and listing the templates of a channel (docs/MESSAGING.md, "Templates").</summary>
public class TemplateServiceTests
{
    private static readonly DateTime Now = new(2026, 11, 16, 15, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IWhatsAppChannelRepository> _channels = new();
    private readonly Mock<IConversationRepository> _conversations = new();
    private readonly Mock<ITenantRepository> _tenants = new();
    private readonly Mock<IMessageTemplateRepository> _templates = new();
    private readonly Mock<IWhatsAppTemplateSource> _source = new();
    private readonly Guid _tenant = Guid.NewGuid();
    private WhatsAppChannel _channel = null!;

    private TemplateService CreateSut()
        => new(_channels.Object, _conversations.Object, _tenants.Object, _templates.Object, _source.Object, new FixedTimeProvider(Now), NullLogger<TemplateService>.Instance);

    private void Arrange(bool credentials = true, bool sandboxMode = false)
    {
        _tenants.Setup(r => r.GetSandboxModeAsync(_tenant, It.IsAny<CancellationToken>())).ReturnsAsync(sandboxMode);
        (_channel, _) = WhatsAppChannel.Create(_tenant, Guid.NewGuid(), "+57 300 555 0100");
        _channel.Activate();
        if (credentials)
        {
            _channel.SetProviderCredentials("v1.1.ciphertext", WhatsAppProviderEnvironment.Production, Now);
        }

        _channels.Setup(r => r.GetByIdAsync(_channel.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_channel);
    }

    private static readonly TemplateSnapshot Reminder = new("recordatorio_cita", "es", TemplateCategory.Utility, TemplateStatus.Approved, "Hola {{1}}", true);

    [Fact]
    public async Task Sync_ReplacesTheChannelsCopyWithWhatTheProviderListed()
    {
        Arrange();
        _source.Setup(s => s.ListAsync(_channel, It.IsAny<CancellationToken>())).ReturnsAsync(new TemplateListResult(WhatsAppSendStatus.Sent, [Reminder], Complete: true));
        _templates.Setup(r => r.ReplaceForChannelAsync(_tenant, _channel.Id, It.IsAny<IReadOnlyList<TemplateSnapshot>>(), true, Now, Now, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TemplateReplaceResult(1, 0, 2, 1));

        var response = await CreateSut().SyncAsync(_channel.Id);

        Assert.Equal((1, 0, 2, 1, false, Now), (response.Added, response.Refreshed, response.Removed, response.RemovalsHeld, response.Sandbox, response.SyncedAt));
        _templates.Verify(r => r.ReplaceForChannelAsync(
            _tenant, _channel.Id, It.Is<IReadOnlyList<TemplateSnapshot>>(l => l.Single() == Reminder), true, Now, Now, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(WhatsAppSendStatus.InvalidCredentials, MessagingProblem.InvalidCredentials)]
    [InlineData(WhatsAppSendStatus.LimitReached, MessagingProblem.LimitReached)]
    [InlineData(WhatsAppSendStatus.TransientFailure, MessagingProblem.ProviderUnavailable)]
    [InlineData(WhatsAppSendStatus.Rejected, MessagingProblem.Rejected)]
    [InlineData(WhatsAppSendStatus.RecipientNotAllowed, MessagingProblem.Rejected)]
    public async Task Sync_AProviderFailure_KeepsTheStoredCopy_AndSaysWhy(WhatsAppSendStatus status, MessagingProblem problem)
    {
        Arrange();
        _source.Setup(s => s.ListAsync(_channel, It.IsAny<CancellationToken>())).ReturnsAsync(TemplateListResult.Failed(status));

        var ex = await Assert.ThrowsAsync<MessagingException>(() => CreateSut().SyncAsync(_channel.Id));

        Assert.Equal(problem, ex.Problem);
        _templates.Verify(r => r.ReplaceForChannelAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<TemplateSnapshot>>(), It.IsAny<bool>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Sync_WithoutAKey_IsRefusedWithoutCallingTheProvider()
    {
        Arrange(credentials: false);

        var ex = await Assert.ThrowsAsync<MessagingException>(() => CreateSut().SyncAsync(_channel.Id));

        Assert.Equal(MessagingProblem.CredentialsMissing, ex.Problem);
        _source.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Sync_InSandboxMode_ReadsAndStoresNothing()
    {
        Arrange(credentials: false, sandboxMode: true);

        var response = await CreateSut().SyncAsync(_channel.Id);

        Assert.True(response.Sandbox);
        _source.VerifyNoOtherCalls();
        _templates.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Sync_AnotherCompanysChannel_IsNotFound()
    {
        Arrange();

        await Assert.ThrowsAsync<ChannelNotFoundException>(() => CreateSut().SyncAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task List_ReturnsTheStoredCopy_WithTheLastSync()
    {
        Arrange();
        var template = MessageTemplate.Create(_tenant, _channel.Id, "recordatorio_cita", "es", TemplateCategory.Utility, TemplateStatus.Approved, "Hola {{1}}", true, Now.AddHours(-1));
        _templates.Setup(r => r.ListByChannelAsync(_channel.Id, It.IsAny<CancellationToken>())).ReturnsAsync([template]);

        var response = await CreateSut().ListForChannelAsync(_channel.Id);

        var item = Assert.Single(response.Templates);
        Assert.Equal(("recordatorio_cita", "Utility", "Approved", true), (item.Name, item.Category, item.Status, item.CanBeSent));
        Assert.Equal(["1"], item.ParameterNames);
        Assert.Equal(Now.AddHours(-1), response.LastSyncedAt);
        Assert.False(response.Sandbox);
    }

    [Fact]
    public async Task List_InSandboxMode_IsTheFixedSandboxList()
    {
        Arrange(credentials: false, sandboxMode: true);

        var response = await CreateSut().ListForChannelAsync(_channel.Id);

        Assert.True(response.Sandbox);
        Assert.NotEmpty(response.Templates);
        Assert.All(response.Templates, t => Assert.StartsWith("conversia_", t.Name));
        _templates.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ListForConversation_IsThatOfItsChannel()
    {
        Arrange();
        var conversation = Conversation.Start(_tenant, Guid.NewGuid(), _channel.Id, _channel.BranchId, Now, ConversationSource.Organic);
        _conversations.Setup(r => r.GetByIdAsync(conversation.Id, It.IsAny<CancellationToken>())).ReturnsAsync(conversation);
        _templates.Setup(r => r.ListByChannelAsync(_channel.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var response = await CreateSut().ListForConversationAsync(conversation.Id);

        Assert.Empty(response.Templates);
        await Assert.ThrowsAsync<ConversationNotFoundException>(() => CreateSut().ListForConversationAsync(Guid.NewGuid()));
    }

    // ---- Meta's names ----------------------------------------------------------------------

    [Theory]
    [InlineData("APPROVED", TemplateStatus.Approved)]
    [InlineData("approved", TemplateStatus.Approved)]
    [InlineData("Active - High Quality", TemplateStatus.Approved)]
    [InlineData("Active - Quality pending", TemplateStatus.Approved)]
    [InlineData("submitted", TemplateStatus.Pending)]
    [InlineData("PENDING", TemplateStatus.Pending)]
    [InlineData("In-Review", TemplateStatus.Pending)]
    [InlineData("Appeal Requested", TemplateStatus.Pending)]
    [InlineData("REJECTED", TemplateStatus.Rejected)]
    [InlineData("Paused", TemplateStatus.Paused)]
    [InlineData("DISABLED", TemplateStatus.Disabled)]
    [InlineData("deleted", TemplateStatus.Disabled)]
    [InlineData("something_new", TemplateStatus.Unknown)]
    [InlineData(null, TemplateStatus.Unknown)]
    public void StatusOfTheList(string? status, TemplateStatus expected)
        => Assert.Equal(expected, TemplateNames.StatusOf(status));

    [Theory]
    [InlineData("APPROVED", TemplateStatus.Approved)]
    [InlineData("REINSTATED", TemplateStatus.Approved)]
    [InlineData("PENDING", TemplateStatus.Pending)]
    [InlineData("IN_APPEAL", TemplateStatus.Pending)]
    [InlineData("REJECTED", TemplateStatus.Rejected)]
    [InlineData("PAUSED", TemplateStatus.Paused)]
    [InlineData("DISABLED", TemplateStatus.Disabled)]
    [InlineData("ARCHIVED", TemplateStatus.Disabled)]
    [InlineData("DELETED", TemplateStatus.Disabled)]
    [InlineData("PENDING_DELETION", TemplateStatus.Disabled)]
    [InlineData("FLAGGED", null)]
    [InlineData("LOCKED", null)]
    [InlineData("UNARCHIVED", null)]
    [InlineData("LIMIT_EXCEEDED", null)]
    public void StatusOfAWebhookEvent(string statusEvent, TemplateStatus? expected)
        => Assert.Equal(expected, TemplateNames.StatusOfEvent(statusEvent));

    [Theory]
    [InlineData("MARKETING", TemplateCategory.Marketing)]
    [InlineData("utility", TemplateCategory.Utility)]
    [InlineData("AUTHENTICATION", TemplateCategory.Authentication)]
    [InlineData("TRANSACTIONAL", TemplateCategory.Unknown)]
    public void CategoryNames(string category, TemplateCategory expected)
        => Assert.Equal(expected, TemplateNames.CategoryOf(category));
}
