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
}
