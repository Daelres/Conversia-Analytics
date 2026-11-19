using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Conversia.Domain.Common;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.Entities;

/// <summary>
/// A WhatsApp number connected to a branch (a "line": the unit the platform bills). <see cref="ChannelKey"/> is the public identifier in the webhook URL (globally
/// unique, unguessable); the webhook secret is generated once, shown to the caller in plaintext at creation and stored only
/// as a SHA-256 hash. The provider API key used to send messages is kept only encrypted (<see cref="ProviderApiKeyCiphertext"/>): the
/// entity never sees the plaintext.
/// </summary>
public class WhatsAppChannel : BaseEntity
{
    public const int DisplayPhoneNumberMaxLength = 30;
    public const int ProviderPhoneNumberIdMaxLength = 64;
    public const int ChannelKeyMaxLength = 64;
    public const int MinChannelKeyLength = 32;
    public const int HashLength = 32;
    private const int RandomBytes = 32;
    private const int MinPhoneDigits = 7;
    private const int MaxPhoneDigits = 15;
    public const int ProviderApiKeyMaxLength = 256;
    public const int ProviderApiKeyCiphertextMaxLength = 1024;

    public static readonly string DisplayPhoneNumberRuleText =
        $"debe tener entre {MinPhoneDigits} y {MaxPhoneDigits} dígitos y solo puede incluir '+', espacios, '-', '(' y ')'";

    public static readonly string DisplayPhoneNumberRuleMessage = $"El número de teléfono {DisplayPhoneNumberRuleText}.";

    public static readonly string ProviderPhoneNumberIdRuleText =
        $"es opcional, no puede superar los {ProviderPhoneNumberIdMaxLength} caracteres y solo puede incluir letras, dígitos, '-' o '_'";

    public static readonly string ProviderPhoneNumberIdRuleMessage = $"El identificador del número en el proveedor {ProviderPhoneNumberIdRuleText}.";

    public static readonly string ProviderApiKeyRuleText =
        $"debe tener entre 1 y {ProviderApiKeyMaxLength} caracteres visibles, sin espacios ni tildes";

    public const string ProviderEnvironmentRuleMessage = "El entorno del proveedor no es válido.";

    public static readonly string ProviderApiKeyRuleMessage = $"La clave de API del proveedor {ProviderApiKeyRuleText}.";

    public Guid TenantId { get; private set; }

    public Guid BranchId { get; private set; }

    public string DisplayPhoneNumber { get; private set; } = string.Empty;

    public string? ProviderPhoneNumberId { get; private set; }

    public string ChannelKey { get; private set; } = string.Empty;

    /// <summary>SHA-256 of the webhook secret. The secret itself is never stored.</summary>
    public byte[] WebhookSecretHash { get; private set; } = [];

    /// <summary>
    /// Whether the webhook works. A new channel is registered switched OFF: the company switches it on itself, as long as it is within the
    /// line quota of its agreement (<see cref="Tenant.MaxActiveLines"/>).
    /// </summary>
    public bool IsActive { get; private set; }

    /// <summary>
    /// The provider API key, encrypted by the application before it gets here (see <c>ISecretProtector</c>). Null while the channel has no
    /// key. The plaintext is never stored and no response carries this value.
    /// </summary>
    public string? ProviderApiKeyCiphertext { get; private set; }

    /// <summary>When the current key was stored; null exactly while there is none.</summary>
    public DateTime? ProviderApiKeySetAt { get; private set; }

    public WhatsAppProviderEnvironment ProviderEnvironment { get; private set; } = WhatsAppProviderEnvironment.Sandbox;

    public bool HasProviderApiKey => ProviderApiKeyCiphertext is not null;

    /// <summary>
    /// The coexistence notification that disconnected the number from the API (<c>PARTNER_REMOVED</c> or <c>ACCOUNT_OFFBOARDED</c>,
    /// Meta's <c>account_update</c>); null while it is connected. Content-free: an event and a reason word (docs/WEBHOOKS.md, "Coexistence").
    /// </summary>
    public string? ProviderDisconnectionEvent { get; private set; }

    /// <summary>The <c>disconnection_info.reason</c> of a <c>PARTNER_REMOVED</c> (<c>PRIMARY_INACTIVITY</c>: the app was not opened for about 14 days).</summary>
    public string? ProviderDisconnectionReason { get; private set; }

    /// <summary>When the number was disconnected; null while connected.</summary>
    public DateTime? ProviderDisconnectedAt { get; private set; }

    /// <summary>The time of the last applied account notification: an older one changes nothing.</summary>
    public DateTime? ProviderAccountEventAt { get; private set; }

    public bool IsProviderDisconnected => ProviderDisconnectedAt is not null;

    /// <summary>The shared chat history (coexistence onboarding) is stored. Off by default; only the owner turns it on (docs/MESSAGING.md, "History import").</summary>
    public bool HistoryImportEnabled { get; private set; }

    /// <summary>The imported conversations are read by the AI too (it costs per conversation); otherwise only by the rules.</summary>
    public bool HistoryAiAnalysisEnabled { get; private set; }

    /// <summary>The user who last confirmed the company's legal basis to import past conversations. Kept when the import is turned off.</summary>
    public Guid? HistoryImportConsentedBy { get; private set; }

    /// <summary>When <see cref="HistoryImportConsentedBy"/> confirmed it.</summary>
    public DateTime? HistoryImportConsentedAt { get; private set; }

    public const int DisconnectionWordMaxLength = 50;

    /// <summary>
    /// Applies a coexistence <c>account_update</c>: <c>PARTNER_REMOVED</c> and <c>ACCOUNT_OFFBOARDED</c> flag the number as disconnected,
    /// <c>ACCOUNT_RECONNECTED</c> clears the flag. A notification older than the last applied one, or another event, changes nothing.
    /// Returns whether it changed something.
    /// </summary>
    public bool ApplyAccountUpdate(string accountEvent, string? reason, DateTime occurredAt)
    {
        var at = UtcTime.Normalize(occurredAt);
        if (ProviderAccountEventAt is { } last && at < last)
        {
            return false;
        }

        switch (accountEvent)
        {
            case "PARTNER_REMOVED" or "ACCOUNT_OFFBOARDED":
                ProviderDisconnectionEvent = accountEvent;
                ProviderDisconnectionReason = reason is { Length: >= 1 and <= DisconnectionWordMaxLength } && reason.All(c => char.IsAsciiLetterUpper(c) || c == '_') ? reason : null;
                ProviderDisconnectedAt = at;
                break;
            case "ACCOUNT_RECONNECTED":
                ProviderDisconnectionEvent = null;
                ProviderDisconnectionReason = null;
                ProviderDisconnectedAt = null;
                break;
            default:
                return false;
        }

        ProviderAccountEventAt = at;
        return true;
    }

    /// <summary>Turns the history import on; <paramref name="userId"/> confirmed the company has the legal basis to import past conversations.</summary>
    public void EnableHistoryImport(Guid userId, DateTime at, bool analyzeWithAi)
    {
        HistoryImportConsentedBy = TextRules.RequiredId(userId, "El usuario que confirma la base legal es obligatorio.");
        HistoryImportConsentedAt = UtcTime.Normalize(at);
        HistoryImportEnabled = true;
        HistoryAiAnalysisEnabled = analyzeWithAi;
    }

    /// <summary>Turns the history import (and its AI analysis) off. What was imported stays; the consent trail stays.</summary>
    public void DisableHistoryImport()
    {
        HistoryImportEnabled = false;
        HistoryAiAnalysisEnabled = false;
    }

    /// <summary>The AI sales assistant may answer on this line (docs/ASSISTANT.md, "Settings"). Off by default; the company's switch applies on top.</summary>
    public bool AssistantEnabled { get; private set; }

    /// <summary>Returns false when the line already was that way.</summary>
    public bool SetAssistantEnabled(bool enabled)
    {
        if (AssistantEnabled == enabled)
        {
            return false;
        }

        AssistantEnabled = enabled;
        return true;
    }

    // Required by EF Core for materialization.
    private WhatsAppChannel()
    {
    }

    /// <summary>Creates the channel and returns it with the plaintext webhook secret, which exists nowhere else.</summary>
    public static (WhatsAppChannel Channel, string WebhookSecret) Create(
        Guid tenantId, Guid branchId, string displayPhoneNumber, string? providerPhoneNumberId = null)
    {
        if (!IsValidDisplayPhoneNumber(displayPhoneNumber))
        {
            throw new DomainException(DisplayPhoneNumberRuleMessage);
        }

        if (!IsValidProviderPhoneNumberId(providerPhoneNumberId))
        {
            throw new DomainException(ProviderPhoneNumberIdRuleMessage);
        }

        var secret = GenerateToken();
        var channel = new WhatsAppChannel
        {
            TenantId = TextRules.RequiredId(tenantId, "El identificador de la empresa es obligatorio."),
            BranchId = TextRules.RequiredId(branchId, "La sucursal es obligatoria."),
            DisplayPhoneNumber = displayPhoneNumber.Trim(),
            ProviderPhoneNumberId = string.IsNullOrWhiteSpace(providerPhoneNumberId) ? null : providerPhoneNumberId.Trim(),
            ChannelKey = GenerateToken(),
            WebhookSecretHash = HashSecret(secret),
        };

        return (channel, secret);
    }

    /// <summary>
    /// Replaces the webhook secret and returns the new one, shown to the caller once (only its hash is kept). The old secret stops
    /// working at once.
    /// </summary>
    public string RotateWebhookSecret()
    {
        var secret = GenerateToken();
        WebhookSecretHash = HashSecret(secret);
        return secret;
    }

    public void Activate() => IsActive = true;

    public void Deactivate() => IsActive = false;

    /// <summary>Stores the encrypted provider API key (replacing any previous one) together with the environment it belongs to.</summary>
    public void SetProviderCredentials(string apiKeyCiphertext, WhatsAppProviderEnvironment environment, DateTime setAt)
    {
        if (!Enum.IsDefined(environment))
        {
            throw new DomainException(ProviderEnvironmentRuleMessage);
        }

        // Everything is checked before anything is assigned.
        var ciphertext = TextRules.Required(apiKeyCiphertext, ProviderApiKeyCiphertextMaxLength, "La clave de API cifrada del proveedor no es válida.");
        var at = UtcTime.Normalize(setAt);

        ProviderApiKeyCiphertext = ciphertext;
        ProviderEnvironment = environment;
        ProviderApiKeySetAt = at;
    }

    /// <summary>Changes the environment and keeps the stored key, if any.</summary>
    public void SetProviderEnvironment(WhatsAppProviderEnvironment environment)
        => ProviderEnvironment = Enum.IsDefined(environment) ? environment : throw new DomainException(ProviderEnvironmentRuleMessage);

    /// <summary>Forgets the provider API key. The environment stays. Idempotent.</summary>
    public void ClearProviderCredentials()
    {
        ProviderApiKeyCiphertext = null;
        ProviderApiKeySetAt = null;
    }

    /// <summary>True when <paramref name="providedSecret"/> hashes to the stored hash. Compared in constant time.</summary>
    public bool VerifyWebhookSecret(string? providedSecret)
        => CryptographicOperations.FixedTimeEquals(HashSecret(providedSecret ?? string.Empty), WebhookSecretHash);

    public static byte[] HashSecret(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));

    /// <summary>Cheap shape check of a key taken from a URL, so a malformed one never reaches the database.</summary>
    public static bool IsWellFormedChannelKey(string? key)
        => key is { Length: >= MinChannelKeyLength and <= ChannelKeyMaxLength }
           && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static bool IsValidDisplayPhoneNumber(string? phone)
    {
        var trimmed = phone?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > DisplayPhoneNumberMaxLength)
        {
            return false;
        }

        if (!trimmed.All(c => char.IsAsciiDigit(c) || c is '+' or ' ' or '-' or '(' or ')'))
        {
            return false;
        }

        var digits = trimmed.Count(char.IsAsciiDigit);
        return digits is >= MinPhoneDigits and <= MaxPhoneDigits;
    }

    public static bool IsValidProviderPhoneNumberId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return true;
        }

        var trimmed = id.Trim();
        return trimmed.Length <= ProviderPhoneNumberIdMaxLength
               && trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    }

    /// <summary>
    /// The plaintext key rule, applied to the trimmed value: visible ASCII only (0x21 to 0x7E). The key travels in an HTTP header, so
    /// spaces, control characters and line breaks are refused here rather than at the provider.
    /// </summary>
    public static bool IsValidProviderApiKey(string? key)
    {
        var trimmed = key?.Trim();
        return trimmed is { Length: >= 1 and <= ProviderApiKeyMaxLength } && trimmed.All(c => c is > ' ' and <= '~');
    }

    // 256 random bits as URL-safe base64 (43 characters, no padding).
    private static string GenerateToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(RandomBytes));
}
