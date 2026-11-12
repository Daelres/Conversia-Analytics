using System.Text.RegularExpressions;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.Common;

/// <summary>
/// Stable machine codes for why a lifecycle change happened (<c>billing.past_due</c>, <c>deletion.completed</c>...). They are kept apart from
/// the free-text reason an administrator types: a code is what reports and the billing job match on, and it never holds personal data.
/// Lowercase segments of letters, digits and <c>_</c>, joined by dots, at most <see cref="MaxLength"/> characters.
/// </summary>
public static partial class ReasonCodes
{
    public const int MaxLength = 64;

    /// <summary>The billing job suspended the company for a late payment.</summary>
    public const string BillingPastDue = "billing.past_due";

    /// <summary>The billing job lifted a billing suspension after the payment.</summary>
    public const string BillingPaid = "billing.paid";

    /// <summary>The billing job cancelled the company for non-payment.</summary>
    public const string BillingNonPayment = "billing.non_payment";

    /// <summary>The deletion job started because the retention window after the cancellation passed.</summary>
    public const string DeletionRetentionExpired = "deletion.retention_expired";

    /// <summary>The platform administrator asked to delete the data before the window ended.</summary>
    public const string DeletionRequestedByAdmin = "deletion.requested_by_admin";

    /// <summary>The deletion job finished: nothing of the company is left but the tombstone and the audit trail.</summary>
    public const string DeletionCompleted = "deletion.completed";

    public const string RuleMessage = "El código de motivo no es válido.";

    [GeneratedRegex("^[a-z0-9_]+(\\.[a-z0-9_]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex Shape();

    public static bool IsValid(string? code) => code is { Length: > 0 and <= MaxLength } && Shape().IsMatch(code);

    public static string Required(string? code) => IsValid(code) ? code! : throw new DomainException(RuleMessage);

    public static string? Optional(string? code) => code is null ? null : Required(code);
}

/// <summary>
/// The closed list of reasons an administrator can pick for a status change (<c>admin.*</c>), so that the free-text reason, kept forever in the
/// audit trail, is only needed for the detail. A code outside the list is refused.
/// </summary>
public static class AdminReasonCodes
{
    public const string ContractSigned = "admin.contract_signed";
    public const string AgreedPause = "admin.agreed_pause";
    public const string CustomerRequest = "admin.customer_request";
    public const string ContractEnded = "admin.contract_ended";
    public const string PaymentIssue = "admin.payment_issue";
    public const string Abuse = "admin.abuse";
    public const string Other = "admin.other";

    public static readonly IReadOnlyList<string> All = [ContractSigned, AgreedPause, CustomerRequest, ContractEnded, PaymentIssue, Abuse, Other];

    public const string RuleMessage = "El motivo elegido no está en la lista.";

    public static string? Optional(string? code)
        => string.IsNullOrWhiteSpace(code) ? null : All.Contains(code.Trim()) ? code.Trim() : throw new DomainException(RuleMessage);
}

