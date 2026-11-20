namespace Conversia.Application.Reports;

/// <summary>One email: a recipient, a subject, an HTML and a plain-text body, and extra headers (List-Unsubscribe). A class: a record would print the address.</summary>
public sealed class EmailMessage(string toAddress, string subject, string htmlBody, string textBody, IReadOnlyDictionary<string, string> headers, string? messageIdLocalPart = null)
{
    /// <summary>
    /// A stable left part of the Message-Id (the delivery's id), so the same delivery always carries the same Message-Id: a mail program that sees it
    /// twice can tell it is one message. Null: a new one each time.
    /// </summary>
    public string? MessageIdLocalPart { get; } = messageIdLocalPart;

    public string ToAddress { get; } = toAddress;

    public string Subject { get; } = subject;

    public string HtmlBody { get; } = htmlBody;

    public string TextBody { get; } = textBody;

    public IReadOnlyDictionary<string, string> Headers { get; } = headers;

    public override string ToString() => $"EmailMessage {{ SubjectLength = {Subject.Length}, Headers = {Headers.Count} }}";
}

public enum EmailSendStatus
{
    Sent,

    /// <summary>The server could not be reached or answered with a temporary error: trying again later may work.</summary>
    TransientFailure,

    /// <summary>The server refused the message or the address for good.</summary>
    PermanentFailure,

    /// <summary>No email provider is configured (<c>Email:Provider=None</c>).</summary>
    NotConfigured,

    /// <summary>
    /// The connection failed while or after the message was handed over: the server may have accepted it. Not retried, so nobody gets it twice.
    /// </summary>
    UnknownOutcome,
}

/// <param name="ErrorCode">A stable code (<c>smtp_unavailable</c>, <c>recipient_rejected</c>, ...), never the server's text.</param>
public sealed record EmailSendResult(EmailSendStatus Status, string? MessageId = null, string? ErrorCode = null)
{
    public static EmailSendResult Sent(string? messageId) => new(EmailSendStatus.Sent, messageId);
}

/// <summary>
/// Sends one email (docs/REPORTS.md, "Email"). Adapters: SMTP over TLS (MailKit) and a development folder of <c>.eml</c> files. Never throws
/// for a delivery problem and never logs the address or the body.
/// </summary>
public interface IEmailSender
{
    Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
