using Conversia.Application.DTOs.Sandbox;

namespace Conversia.Application.Interfaces;

/// <summary>
/// The internal sandbox of a company, seen from the company (docs/SANDBOX.md): a simulated customer that writes through the real
/// ingestion pipeline, and the removal of what the sandbox created. Switching the mode itself belongs to the platform administrator
/// (<see cref="IPlatformAdminService"/>); nothing here can.
/// </summary>
public interface ISandboxService
{
    /// <summary>
    /// A new simulated customer writes a first message to one of the company's active channels. Throws
    /// <see cref="Exceptions.SandboxException"/> (sandbox mode off, channel inactive), <see cref="Exceptions.ChannelNotFoundException"/>
    /// or <see cref="Domain.Exceptions.DomainException"/> (name or text).
    /// </summary>
    Task<SandboxMessageAcceptedResponse> StartConversationAsync(Guid tenantId, StartSandboxConversationRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// The simulated customer of an open sandbox conversation writes again. Throws <see cref="Exceptions.SandboxException"/> (sandbox mode
    /// off, a real conversation, a closed one, channel inactive) or <see cref="Exceptions.ConversationNotFoundException"/>.
    /// </summary>
    Task<SandboxMessageAcceptedResponse> SendCustomerMessageAsync(
        Guid tenantId, Guid conversationId, SandboxCustomerMessageRequest request, CancellationToken cancellationToken = default);

    /// <summary>Removes every sandbox row of the company, and nothing else. Allowed whatever the mode, so a company can clean up after it is switched off.</summary>
    Task<SandboxDataDeletedResponse> DeleteDataAsync(Guid tenantId, Guid? userId, CancellationToken cancellationToken = default);
}

/// <summary>How many rows of each kind a deletion of sandbox data removed.</summary>
public sealed record SandboxDataCounts(int Conversations, int Messages, int Contacts, int WebhookEvents);

/// <summary>Persistence of the sandbox clean-up.</summary>
public interface ISandboxDataStore
{
    /// <summary>
    /// Deletes, in one transaction serialized with the company's ingestion, the conversations marked sandbox with their messages and
    /// opportunities, the contacts marked sandbox and the deliveries marked sandbox, of <paramref name="tenantId"/> only.
    /// </summary>
    Task<SandboxDataCounts> DeleteAllAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Makes up the numbers of simulated customers (<see cref="Domain.Entities.Contact.NewSandboxWaId"/>). A port so that a test decides
/// which numbers come out; the application registers the random one.
/// </summary>
public interface ISandboxCustomerNumbers
{
    /// <summary>A number of the sandbox range. It may already be taken: the caller checks and asks again.</summary>
    string Next();
}

/// <summary>Random numbers of the sandbox range.</summary>
public sealed class RandomSandboxCustomerNumbers : ISandboxCustomerNumbers
{
    public string Next() => Domain.Entities.Contact.NewSandboxWaId(Random.Shared);
}
