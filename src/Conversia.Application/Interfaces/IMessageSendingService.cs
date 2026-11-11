using Conversia.Application.DTOs.Conversations;

namespace Conversia.Application.Interfaces;

/// <summary>A person sends a WhatsApp message into a conversation of the caller's tenant. Not an automated reply: nothing calls this by itself.</summary>
public interface IMessageSendingService
{
    /// <summary>
    /// Sends the text to the customer of the conversation through its channel and, once the provider accepted it, stores it as an
    /// Outbound message the way ingestion would. While the company is in sandbox mode the "provider" is the internal sender and only
    /// sandbox conversations take messages (docs/SANDBOX.md). Throws <see cref="Exceptions.ConversationNotFoundException"/> for a missing or
    /// other-tenant conversation, <see cref="Domain.Exceptions.DomainException"/> for a text that cannot be sent, and
    /// <see cref="Exceptions.MessagingException"/> for everything else that keeps the message from being sent or recorded.
    /// </summary>
    Task<MessageResponse> SendTextAsync(Guid conversationId, SendMessageRequest request, CancellationToken cancellationToken = default);
}
