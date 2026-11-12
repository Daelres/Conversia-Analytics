using Conversia.Domain.Common;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.Entities;

/// <summary>A customer, identified inside the tenant by the digits of their WhatsApp id.</summary>
public class Contact : BaseEntity
{
    public const int DisplayNameMaxLength = 200;
    public const int MinWaIdLength = 7;
    public const int MaxWaIdLength = 15;

    public static readonly string WaIdRuleMessage =
        $"El identificador de WhatsApp debe tener entre {MinWaIdLength} y {MaxWaIdLength} dígitos (con un '+' inicial opcional).";

    public const string AgeRangeInvalidMessage = "El rango de edad no es válido.";
    public const string GenderInvalidMessage = "El género no es válido.";

    public static readonly string DisplayNameRuleMessage = $"El nombre del contacto {TextRules.RuleText(DisplayNameMaxLength, required: false)}.";

    /// <summary>
    /// What every simulated customer's number starts with. No E.164 country code begins with 0, so a number that does can never be a
    /// real subscriber, and a person recognizes it at a glance. The mark of sandbox data is <see cref="IsSandbox"/>, never this prefix.
    /// </summary>
    public const string SandboxWaIdPrefix = "00000";

    public Guid TenantId { get; private set; }

    /// <summary>The WhatsApp id with digits only (no '+', spaces or separators).</summary>
    public string WaId { get; private set; } = string.Empty;

    public string? DisplayName { get; private set; }

    public DateTime FirstSeenAt { get; private set; }

    /// <summary>The time of the message whose profile name <see cref="DisplayName"/> is, so an older delivery cannot overwrite a newer name.</summary>
    public DateTime? DisplayNameAt { get; private set; }

    /// <summary>True for a customer simulated by the internal sandbox (docs/SANDBOX.md). Set at creation by ingestion, from the delivery; never changed.</summary>
    public bool IsSandbox { get; private set; }

    /// <summary>The coarse age range the customer stated; null = not stated. Never an exact age (data minimization).</summary>
    public AgeRange? AgeRange { get; private set; }

    /// <summary>Who set <see cref="AgeRange"/>; null while it was never set.</summary>
    public FieldSource? AgeRangeSource { get; private set; }

    /// <summary>The gender the customer stated; <see cref="Enums.Gender.Undetermined"/> unless stated.</summary>
    public Gender Gender { get; private set; } = Gender.Undetermined;

    /// <summary>Who set <see cref="Gender"/>; null while it was never set.</summary>
    public FieldSource? GenderSource { get; private set; }

    /// <summary>The classifier behind the last automatic profile write (only the LLM extracts a profile); null = none.</summary>
    public ClassifierProvider? ProfileClassifiedBy { get; private set; }

    /// <summary>When the profile last changed (automatically or by a person); null while it never did.</summary>
    public DateTime? ProfileChangedAt { get; private set; }

    /// <summary>The user who last set, confirmed or reverted a profile field; null when the last change was automatic. An id only.</summary>
    public Guid? ProfileChangedBy { get; private set; }

    /// <summary>
    /// The profile an LLM read in the conversation. A field is written only when it was determined (a range, a gender other than
    /// undetermined) and a person has not set it: extraction never erases what is known and never overrides a person. Returns whether
    /// anything changed.
    /// </summary>
    public bool ApplyExtractedProfile(AgeRange? ageRange, Gender gender, DateTime now)
    {
        if (ageRange is { } range && !Enum.IsDefined(range))
        {
            throw new DomainException(AgeRangeInvalidMessage);
        }

        if (!Enum.IsDefined(gender))
        {
            throw new DomainException(GenderInvalidMessage);
        }

        var changed = false;
        if (ageRange is not null && AgeRangeSource != FieldSource.Manual && (AgeRange != ageRange || AgeRangeSource is null))
        {
            AgeRange = ageRange;
            AgeRangeSource = FieldSource.Auto;
            changed = true;
        }

        if (gender != Gender.Undetermined && GenderSource != FieldSource.Manual && (Gender != gender || GenderSource is null))
        {
            Gender = gender;
            GenderSource = FieldSource.Auto;
            changed = true;
        }

        if (changed)
        {
            ProfileClassifiedBy = ClassifierProvider.Llm;
            ProfileChangedAt = UtcTime.Normalize(now);
            ProfileChangedBy = null;
        }

        return changed;
    }

    /// <summary>A person sets the age range (null: not stated). Saving it, even unchanged, pins it <see cref="FieldSource.Manual"/>.</summary>
    public void SetManualAgeRange(AgeRange? ageRange, DateTime now, Guid? changedBy = null)
    {
        if (ageRange is { } range && !Enum.IsDefined(range))
        {
            throw new DomainException(AgeRangeInvalidMessage);
        }

        AgeRange = ageRange;
        AgeRangeSource = FieldSource.Manual;
        RecordProfileChange(now, changedBy);
    }

    /// <summary>A person sets the gender (undetermined included) and pins it <see cref="FieldSource.Manual"/>.</summary>
    public void SetManualGender(Gender gender, DateTime now, Guid? changedBy = null)
    {
        if (!Enum.IsDefined(gender))
        {
            throw new DomainException(GenderInvalidMessage);
        }

        Gender = gender;
        GenderSource = FieldSource.Manual;
        RecordProfileChange(now, changedBy);
    }

    /// <summary>A person hands a profile field back to extraction: its source becomes <see cref="FieldSource.Auto"/>, its value stays.</summary>
    public void RevertProfileFieldToAuto(ProfileField field, DateTime now, Guid? changedBy = null)
    {
        switch (field)
        {
            case ProfileField.AgeRange:
                AgeRangeSource = FieldSource.Auto;
                break;
            case ProfileField.Gender:
                GenderSource = FieldSource.Auto;
                break;
            default:
                throw new DomainException("El campo no es válido.");
        }

        RecordProfileChange(now, changedBy);
    }

    private void RecordProfileChange(DateTime now, Guid? changedBy)
    {
        ProfileChangedAt = UtcTime.Normalize(now);
        ProfileChangedBy = changedBy;
    }

    // Required by EF Core for materialization.
    private Contact()
    {
    }

    public static Contact Create(Guid tenantId, string waId, string? displayName, DateTime firstSeenAt, bool isSandbox = false)
    {
        var name = TextRules.Optional(displayName, DisplayNameMaxLength, DisplayNameRuleMessage);
        var seen = UtcTime.Normalize(firstSeenAt);
        return new Contact
        {
            TenantId = TextRules.RequiredId(tenantId, "El identificador de la empresa es obligatorio."),
            WaId = NormalizeWaId(waId) ?? throw new DomainException(WaIdRuleMessage),
            DisplayName = name,
            DisplayNameAt = name is null ? null : seen,
            FirstSeenAt = seen,
            IsSandbox = isSandbox,
        };
    }

    /// <summary>
    /// Keeps the profile name of the latest message: a non-blank name replaces the current one only when it was seen at or after the
    /// message the current one came from (deliveries can be processed out of order).
    /// </summary>
    public void UpdateDisplayName(string? displayName, DateTime observedAt)
    {
        var name = TextRules.Optional(displayName, DisplayNameMaxLength, DisplayNameRuleMessage);
        var observed = UtcTime.Normalize(observedAt);
        if (name is not null && (DisplayNameAt is null || observed >= DisplayNameAt))
        {
            DisplayName = name;
            DisplayNameAt = observed;
        }
    }

    /// <summary>A message older than the first one seen moves <see cref="FirstSeenAt"/> back (webhooks can arrive out of order).</summary>
    public void ObserveAt(DateTime sentAt)
    {
        var observed = UtcTime.Normalize(sentAt);
        if (observed < FirstSeenAt)
        {
            FirstSeenAt = observed;
        }
    }

    /// <summary>
    /// The WhatsApp id when it is one: after trimming and an optional leading '+', only ASCII digits, 7 to 15 of them (E.164). Nothing
    /// is stripped or repaired: spaces, dashes, dots and letters make it null. In particular a business-scoped user id such as
    /// <c>CO.13491208655302741918</c> is not a phone number and must never be turned into one, or different customers would merge.
    /// </summary>
    public static string? NormalizeWaId(string? waId)
    {
        var id = waId?.Trim();
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        if (id[0] == '+')
        {
            id = id[1..];
        }

        return id.Length is >= MinWaIdLength and <= MaxWaIdLength && id.All(char.IsAsciiDigit) ? id : null;
    }

    /// <summary>A new simulated customer's number: <see cref="SandboxWaIdPrefix"/> and random digits up to <see cref="MaxWaIdLength"/>.</summary>
    public static string NewSandboxWaId(Random random)
    {
        var digits = new char[MaxWaIdLength - SandboxWaIdPrefix.Length];
        for (var i = 0; i < digits.Length; i++)
        {
            digits[i] = (char)('0' + random.Next(10));
        }

        return SandboxWaIdPrefix + new string(digits);
    }

    /// <summary>Whether the number is in the range the sandbox hands out. For display and for refusing to hand out anything else, not for classifying stored data.</summary>
    public static bool IsSandboxWaId(string? waId) => waId is not null && waId.StartsWith(SandboxWaIdPrefix, StringComparison.Ordinal);
}
