using Conversia.Application.Messaging;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;

namespace Conversia.Infrastructure.Messaging;

/// <summary>
/// The one place that decides which adapter sends for a channel. A company in sandbox mode (docs/SANDBOX.md) sends through the internal
/// adapter, whatever its channels say. Otherwise both provider environments are 360dialog (they differ in base URL, which the adapter
/// reads from the channel); another way of sending is another case here, chosen from the channel.
/// </summary>
public sealed class WhatsAppMessageSenderResolver(Dialog360MessageSender dialog360, SandboxMessageSender sandbox) : IWhatsAppMessageSenderResolver
{
    public IWhatsAppMessageSender Resolve(WhatsAppChannel channel, bool sandboxMode)
    {
        if (sandboxMode)
        {
            return sandbox;
        }

        return channel.ProviderEnvironment switch
        {
            WhatsAppProviderEnvironment.Sandbox or WhatsAppProviderEnvironment.Production => dialog360,
            _ => throw new InvalidOperationException($"No message sender is registered for the provider environment {channel.ProviderEnvironment}."),
        };
    }
}
