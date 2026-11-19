using System.Net.Security;
using System.Net.Sockets;
using Conversia.Application.Reports;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Conversia.Infrastructure.Email;

/// <summary>
/// The validated <c>Email</c> section (docs/REPORTS.md, "Email configuration"). The SMTP password is not here: it is read once at startup and
/// handed to the adapter only.
/// </summary>
/// <param name="Provider"><c>None</c> (no email is sent; deliveries fail with <c>email_not_configured</c>), <c>Smtp</c> or <c>Pickup</c> (Development: .eml files).</param>
/// <param name="FromAddress">The sender identity every email carries (validated at startup).</param>
public sealed record EmailOptions(string Provider, string? FromAddress, string? FromName, SmtpOptions? Smtp, string? PickupDirectory)
{
    public const string SectionName = "Email";
    public const string ProviderKey = SectionName + ":Provider";
    public const string FromAddressKey = SectionName + ":FromAddress";
    public const string FromNameKey = SectionName + ":FromName";
    public const string PickupDirectoryKey = SectionName + ":Pickup:Directory";
    public const string SmtpHostKey = SectionName + ":Smtp:Host";
    public const string SmtpPortKey = SectionName + ":Smtp:Port";
    public const string SmtpSecurityKey = SectionName + ":Smtp:Security";
    public const string SmtpUserNameKey = SectionName + ":Smtp:UserName";
    public const string SmtpPasswordKey = SectionName + ":Smtp:Password";
    public const string SmtpTimeoutSecondsKey = SectionName + ":Smtp:TimeoutSeconds";

    public const string NoneProvider = "None";
    public const string SmtpProvider = "Smtp";
    public const string PickupProvider = "Pickup";

    public static EmailOptions Disabled { get; } = new(NoneProvider, null, null, null, null);

    public bool Enabled => Provider != NoneProvider;
}

/// <param name="Security"><c>StartTls</c> (port 587) or <c>SslOnConnect</c> (port 465). There is no option without TLS.</param>
public sealed record SmtpOptions(string Host, int Port, string Security, string? UserName, int TimeoutSeconds)
{
    public const string StartTls = "StartTls";
    public const string SslOnConnect = "SslOnConnect";
}

/// <summary>Builds the MIME message (multipart/alternative: plain text first, then HTML; the extra headers; a Message-Id of the sender's domain).</summary>
internal static class MimeMessages
{
    public static MimeMessage Build(EmailMessage message, EmailOptions options)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(options.FromName ?? string.Empty, options.FromAddress ?? throw new InvalidOperationException("The sender address is missing.")));
        mime.To.Add(MailboxAddress.Parse(message.ToAddress));
        mime.Subject = message.Subject;
        foreach (var (name, value) in message.Headers)
        {
            mime.Headers.Add(name, value);
        }

        var domain = options.FromAddress?.Split('@').LastOrDefault();
        domain = string.IsNullOrWhiteSpace(domain) ? "localhost" : domain;
        mime.MessageId = message.MessageIdLocalPart is { } local ? $"{local}@{domain}" : MimeKit.Utils.MimeUtils.GenerateMessageId(domain);
        mime.Body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody }.ToMessageBody();
        return mime;
    }
}

/// <summary>
/// SMTP over TLS with MailKit (docs/REPORTS.md, "Why MailKit"): <c>System.Net.Mail.SmtpClient</c> is marked by Microsoft as not recommended for
/// new development and cannot require TLS reliably; MailKit is the library its documentation points to. TLS is mandatory (STARTTLS or implicit
/// TLS, never plain text), the certificate is validated by the system unless a test hands a callback in code. Outcomes are mapped to stable codes;
/// neither the address nor the server's text is logged.
/// </summary>
public sealed class SmtpEmailSender(EmailOptions options, string? password, ILogger<SmtpEmailSender> logger, RemoteCertificateValidationCallback? certificateValidation = null)
    : IEmailSender
{
    public static readonly EventId EmailNotSent = new(4304, "ReportEmailNotSent");

    public async Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var smtp = options.Smtp ?? throw new InvalidOperationException("The SMTP options are missing.");
        MimeMessage mime;
        try
        {
            mime = MimeMessages.Build(message, options);
        }
        catch (ParseException)
        {
            return new EmailSendResult(EmailSendStatus.PermanentFailure, null, "recipient_invalid");
        }

        using var client = new SmtpClient { Timeout = smtp.TimeoutSeconds * 1000 };
        if (certificateValidation is not null)
        {
            client.ServerCertificateValidationCallback = certificateValidation;
        }

        var handedOver = false;
        try
        {
            await client.ConnectAsync(smtp.Host, smtp.Port, smtp.Security == SmtpOptions.SslOnConnect ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, cancellationToken);
            if (!string.IsNullOrEmpty(smtp.UserName))
            {
                await client.AuthenticateAsync(smtp.UserName, password ?? string.Empty, cancellationToken);
            }

            handedOver = true;
            await client.SendAsync(mime, cancellationToken);
            try
            {
                await client.DisconnectAsync(quit: true, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // The server accepted the message; a failed goodbye does not unsend it.
            }

            return EmailSendResult.Sent(mime.MessageId);
        }
        catch (Exception ex) when (Classify(ex, cancellationToken, handedOver) is { } failure)
        {
            logger.LogWarning(EmailNotSent, "A report email was not sent: {Code} ({ExceptionType}).", failure.ErrorCode, ex.GetType().Name);
            return failure;
        }
    }

    /// <param name="handedOver">The failure happened while sending the message (after MAIL FROM): a lost connection or a timeout may hide an
    /// acceptance, so the outcome is unknown and the delivery is not retried. A refusal by the server is still a refusal.</param>
    internal static EmailSendResult? Classify(Exception exception, CancellationToken cancellationToken, bool handedOver = false) => exception switch
    {
        OperationCanceledException when cancellationToken.IsCancellationRequested => null,
        SocketException or IOException or TimeoutException or OperationCanceledException or ServiceNotConnectedException or SmtpProtocolException
            when handedOver => new(EmailSendStatus.UnknownOutcome, null, "smtp_unknown_outcome"),
        SmtpCommandException { ErrorCode: SmtpErrorCode.RecipientNotAccepted } => new(EmailSendStatus.PermanentFailure, null, "recipient_rejected"),
        SmtpCommandException { ErrorCode: SmtpErrorCode.SenderNotAccepted } => new(EmailSendStatus.PermanentFailure, null, "sender_rejected"),
        SmtpCommandException command when (int)command.StatusCode >= 500 => new(EmailSendStatus.PermanentFailure, null, "message_rejected"),
        SmtpCommandException => new(EmailSendStatus.TransientFailure, null, "smtp_temporary"),
        AuthenticationException => new(EmailSendStatus.TransientFailure, null, "smtp_authentication"),
        SslHandshakeException => new(EmailSendStatus.TransientFailure, null, "smtp_tls"),
        NotSupportedException => new(EmailSendStatus.TransientFailure, null, "smtp_tls"),
        SocketException or IOException or TimeoutException or OperationCanceledException or ServiceNotConnectedException or SmtpProtocolException or ProtocolException
            => new(EmailSendStatus.TransientFailure, null, "smtp_unavailable"),
        _ => null,
    };
}

/// <summary>
/// Development only: every email is written as an <c>.eml</c> file to a folder (<c>Email:Pickup:Directory</c>), to open with any mail program.
/// Nothing leaves the machine.
/// </summary>
public sealed class PickupDirectoryEmailSender(EmailOptions options) : IEmailSender
{
    public async Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var directory = options.PickupDirectory ?? throw new InvalidOperationException("The pickup directory is missing.");
        MimeMessage mime;
        try
        {
            mime = MimeMessages.Build(message, options);
        }
        catch (ParseException)
        {
            return new EmailSendResult(EmailSendStatus.PermanentFailure, null, "recipient_invalid");
        }

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.eml");
        await mime.WriteToAsync(path, cancellationToken);
        return EmailSendResult.Sent(mime.MessageId);
    }
}

/// <summary><c>Email:Provider=None</c>: no email is sent; each delivery fails with <c>email_not_configured</c>, visible in the history.</summary>
public sealed class DisabledEmailSender : IEmailSender
{
    public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        => Task.FromResult(new EmailSendResult(EmailSendStatus.NotConfigured, null, "email_not_configured"));
}
