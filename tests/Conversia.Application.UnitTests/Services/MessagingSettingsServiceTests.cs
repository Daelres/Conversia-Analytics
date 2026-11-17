using Conversia.Application.DTOs.Conversations;
using Conversia.Application.Services;
using Conversia.Domain.Entities;
using Conversia.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using Moq;

namespace Conversia.Application.UnitTests.Services;

/// <summary>Switching read receipts leaves a trail (review fix 6): a structured log line with the company, the user and the new value.</summary>
public class MessagingSettingsServiceTests
{
    private sealed class RecordingLogger : ILogger<MessagingSettingsService>
    {
        public List<(EventId Event, string Text)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add((eventId, formatter(state, exception)));
    }

    private readonly Tenant _tenant = Tenant.Create("Empresa");
    private readonly RecordingLogger _logger = new();
    private readonly Mock<ITenantUnitOfWork> _unitOfWork = new();
    private readonly Mock<ITenantChange> _change = new();

    public MessagingSettingsServiceTests()
    {
        _change.SetupGet(c => c.Tenant).Returns(_tenant);
        _unitOfWork.Setup(u => u.BeginAsync(_tenant.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_change.Object);
    }

    private MessagingSettingsService CreateSut() => new(Mock.Of<ITenantRepository>(), _unitOfWork.Object, _logger);

    [Fact]
    public async Task AChange_IsLoggedWithTheCompanyTheUserAndTheValue()
    {
        var user = Guid.NewGuid();

        await CreateSut().SetAsync(_tenant.Id, new SetMessagingSettingsRequest { ReadReceiptsEnabled = true }, user);

        var line = Assert.Single(_logger.Lines);
        Assert.Equal(MessagingSettingsService.ReadReceiptsChanged, line.Event);
        Assert.Contains(_tenant.Id.ToString(), line.Text);
        Assert.Contains(user.ToString(), line.Text);
        Assert.Contains("True", line.Text);
        _change.Verify(c => c.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NoChange_LeavesNoLine()
    {
        await CreateSut().SetAsync(_tenant.Id, new SetMessagingSettingsRequest { ReadReceiptsEnabled = false }, Guid.NewGuid());

        Assert.Empty(_logger.Lines);
        _change.Verify(c => c.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
