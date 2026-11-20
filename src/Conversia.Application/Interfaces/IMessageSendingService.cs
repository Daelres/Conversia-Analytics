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
    /// <param name="actingUserId">The user who sends it: recorded on the message with the advisor linked to them (docs/MESSAGING.md, "Who sent it").</param>
    Task<MessageResponse> SendTextAsync(Guid conversationId, SendMessageRequest request, Guid? actingUserId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends an approved template of the conversation's channel (docs/MESSAGING.md, "Templates"): the same checks, attribution and record
    /// as a text, except that the customer service window does not apply. Throws <see cref="Domain.Exceptions.DomainException"/> for values
    /// that do not fit the template, and <see cref="Exceptions.MessagingException"/> (<c>template_unavailable</c>) for a template that
    /// cannot be sent.
    /// </summary>
    Task<MessageResponse> SendTemplateAsync(Guid conversationId, SendTemplateMessageRequest request, Guid? actingUserId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends an image (JPEG, PNG) or a document attached by a person (docs/MESSAGING.md, "Sending media"): the content is checked against the
    /// allow-list and Meta's limits, a copy is stored, the file is uploaded to the provider and sent by its media id with the optional
    /// caption, and the message is recorded with the stored copy. Same checks, window, attribution and record as a text.
    /// </summary>
    Task<MessageResponse> SendMediaAsync(Guid conversationId, SendMediaMessageRequest request, Guid? actingUserId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a text of the AI sales assistant (docs/ASSISTANT.md, "Pipeline"): the same checks, window, provider call and record as a text,
    /// recorded as <c>AiAssistant</c> without user or advisor. <paramref name="whileRecording"/> runs inside the record step, under the
    /// conversation's lock, with the locked conversation (the assistant's state changes with the message).
    /// </summary>
    Task<MessageResponse> SendAsAssistantAsync(
        Guid conversationId, string text, Func<Domain.Entities.Conversation, Task>? whileRecording = null, CancellationToken cancellationToken = default);
}
