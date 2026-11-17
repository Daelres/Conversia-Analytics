using Conversia.Application.Messaging;
using Conversia.Application.Sandbox;

namespace Conversia.Infrastructure.Messaging;

/// <summary>
/// The sender of a company in sandbox mode (docs/SANDBOX.md): it accepts every message at once and reaches nobody. No network call, no
/// provider credentials (it never reads the channel's key), and an id that says where it came from (<see cref="SandboxIds.MessageIdPrefix"/>).
/// The use case then records the message exactly as it does for a real provider.
/// </summary>
public sealed class SandboxMessageSender : IWhatsAppMessageSender
{
    public Task<WhatsAppSendResult> SendTextAsync(WhatsAppTextMessage message, CancellationToken cancellationToken = default)
        => Task.FromResult(WhatsAppSendResult.Sent(SandboxIds.NewOutboundMessageId()));

    /// <summary>Accepts the template: the use case only hands over one of the fixed sandbox templates (<c>SandboxTemplates</c>).</summary>
    public Task<WhatsAppSendResult> SendTemplateAsync(WhatsAppTemplateMessage message, CancellationToken cancellationToken = default)
        => Task.FromResult(WhatsAppSendResult.Sent(SandboxIds.NewOutboundMessageId()));

    /// <summary>A read receipt or typing indicator that reaches nobody; the use case logs it.</summary>
    public Task<WhatsAppSendStatus> MarkReadAsync(WhatsAppReadReceipt receipt, CancellationToken cancellationToken = default)
        => Task.FromResult(WhatsAppSendStatus.Sent);
}
