using System.Text.RegularExpressions;
using Conversia.Domain.Common;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.Entities;

public class User : BaseEntity
{
    private static readonly Regex EmailPattern = new(@"^[^@\s\0]+@[^@\s\0]+\.[^@\s\0]+$", RegexOptions.Compiled);

    public Guid TenantId { get; private set; }

    public string Email { get; private set; } = string.Empty;

    public string PasswordHash { get; private set; } = string.Empty;

    public bool IsOwner { get; private set; }

    /// <summary>
    /// The person's own WhatsApp number (digits only), given to receive the manager reports there (docs/REPORTS.md, "WhatsApp opt-in"). Set
    /// only together with <see cref="ReportsWhatsAppOptInAt"/>: the number exists here only while the explicit consent does.
    /// </summary>
    public string? ReportsWhatsAppNumber { get; private set; }

    /// <summary>When the person accepted, on their own, to receive the reports on <see cref="ReportsWhatsAppNumber"/>.</summary>
    public DateTime? ReportsWhatsAppOptInAt { get; private set; }

    /// <summary>When the person last withdrew that consent (kept as the record of the withdrawal; the number is deleted).</summary>
    public DateTime? ReportsWhatsAppOptOutAt { get; private set; }

    /// <summary>The consent is recorded and the number proved it is the person's (the code sent to it came back).</summary>
    public bool ReceivesReportsOnWhatsApp => ReportsWhatsAppNumber is not null && ReportsWhatsAppOptInAt is not null && ReportsWhatsAppVerifiedAt is not null;

    public DateTime? ReportsWhatsAppVerifiedAt { get; private set; }

    public string? ReportsWhatsAppCodeHash { get; private set; }

    public DateTime? ReportsWhatsAppCodeExpiresAt { get; private set; }

    public int ReportsWhatsAppCodeAttempts { get; private set; }

    public const int MaxReportsWhatsAppCodeAttempts = 5;

    /// <summary>Keeps the hash of a new one-time code sent to the number (never the code), valid until <paramref name="expiresAtUtc"/>.</summary>
    public void IssueReportsWhatsAppCode(string codeHash, DateTime expiresAtUtc)
    {
        if (ReportsWhatsAppNumber is null)
        {
            throw new DomainException(ReportsWhatsAppConsentMessage);
        }

        ReportsWhatsAppCodeHash = codeHash;
        ReportsWhatsAppCodeExpiresAt = UtcTime.Normalize(expiresAtUtc);
        ReportsWhatsAppCodeAttempts = 0;
    }

    /// <summary>True when the code matches, has not expired and fewer than five wrong attempts were made; the number is then verified.</summary>
    public bool VerifyReportsWhatsApp(string codeHash, DateTime utcNow)
    {
        if (ReportsWhatsAppCodeHash is null || ReportsWhatsAppCodeExpiresAt is not { } expires || utcNow > expires
            || ReportsWhatsAppCodeAttempts >= MaxReportsWhatsAppCodeAttempts)
        {
            return false;
        }

        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(codeHash), System.Text.Encoding.ASCII.GetBytes(ReportsWhatsAppCodeHash)))
        {
            ReportsWhatsAppCodeAttempts++;
            return false;
        }

        ReportsWhatsAppVerifiedAt = UtcTime.Normalize(utcNow);
        ReportsWhatsAppCodeHash = null;
        ReportsWhatsAppCodeExpiresAt = null;
        ReportsWhatsAppCodeAttempts = 0;
        return true;
    }

    public const string ReportsWhatsAppConsentMessage = "Para recibir los informes por WhatsApp debes aceptar expresamente recibirlos en ese número.";

    /// <summary>Records the person's explicit consent to receive the reports on <paramref name="phone"/>, at <paramref name="utcNow"/>.</summary>
    public void OptInToReportsOnWhatsApp(string? phone, bool consent, DateTime utcNow)
    {
        if (!consent)
        {
            throw new DomainException(ReportsWhatsAppConsentMessage);
        }

        var digits = LineLicense.DigitsOf(phone) ?? throw new DomainException(WhatsAppChannel.DisplayPhoneNumberRuleMessage);
        if (digits != ReportsWhatsAppNumber)
        {
            ReportsWhatsAppVerifiedAt = null;
        }

        ReportsWhatsAppNumber = digits;
        ReportsWhatsAppOptInAt = UtcTime.Normalize(utcNow);
    }

    /// <summary>Withdraws the consent: the number is deleted and the instant kept. Idempotent.</summary>
    public void OptOutOfReportsOnWhatsApp(DateTime utcNow)
    {
        if (ReportsWhatsAppNumber is null && ReportsWhatsAppOptInAt is null)
        {
            return;
        }

        ReportsWhatsAppNumber = null;
        ReportsWhatsAppOptInAt = null;
        ReportsWhatsAppVerifiedAt = null;
        ReportsWhatsAppCodeHash = null;
        ReportsWhatsAppCodeExpiresAt = null;
        ReportsWhatsAppOptOutAt = UtcTime.Normalize(utcNow);
    }

    // Required by EF Core for materialization.
    private User()
    {
    }

    public static User Create(Guid tenantId, string email, string passwordHash, bool isOwner)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("El identificador de la empresa es obligatorio.");
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            throw new DomainException("El correo electrónico es obligatorio.");
        }

        if (!EmailPattern.IsMatch(email))
        {
            throw new DomainException("El formato del correo electrónico no es válido.");
        }

        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            throw new DomainException("El hash de la contraseña es obligatorio.");
        }

        return new User
        {
            TenantId = tenantId,
            Email = email,
            PasswordHash = passwordHash,
            IsOwner = isOwner
        };
    }
}
