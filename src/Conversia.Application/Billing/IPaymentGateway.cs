using Conversia.Domain.Enums;

namespace Conversia.Application.Billing;

/// <summary>
/// The outbound port to the payment gateway (Wompi, docs/BILLING.md). No gateway type crosses it: the adapter in Infrastructure is the only
/// class that knows the gateway's URLs, keys, field names and signatures. Card data never reaches it: the browser tokenizes the card with the
/// public key and the server only ever sees the token. Calls that create something are made once (no automatic retry): after a timeout,
/// nobody knows what the gateway did, so the caller treats the result as unknown and asks again later.
/// </summary>
public interface IPaymentGateway
{
    /// <summary>False when the deployment has no gateway keys: every call then throws <see cref="Exceptions.BillingProblem.GatewayNotConfigured"/>.</summary>
    bool IsConfigured { get; }

    /// <summary>What the browser needs to tokenize a card or a Nequi number itself: the public key and the API base URL. Never a secret.</summary>
    GatewayBrowserSettings BrowserSettings { get; }

    /// <summary>The current terms the payer must accept (the end-user policy and the personal data authorization), with their links.</summary>
    Task<GatewayAcceptance> GetAcceptanceAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates a reusable payment source from a browser token. Throws <see cref="Exceptions.BillingProblem.PaymentMethodRejected"/> on a refusal.</summary>
    Task<GatewayPaymentSource> CreatePaymentSourceAsync(GatewayPaymentSourceRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Charges a payment source. Never throws for the gateway: a transport failure or a timeout is <see cref="GatewayChargeOutcome.Unknown"/>
    /// (the transaction may exist: look it up by reference later), a refusal before any transaction is <see cref="GatewayChargeOutcome.Refused"/>.
    /// </summary>
    Task<GatewayChargeResult> ChargeAsync(GatewayChargeRequest request, CancellationToken cancellationToken = default);

    /// <summary>The transaction as the gateway has it now (null when it does not know it). Throws <see cref="Exceptions.BillingProblem.GatewayUnavailable"/>.</summary>
    Task<GatewayTransaction?> GetTransactionAsync(string transactionId, CancellationToken cancellationToken = default);

    /// <summary>The transaction created with our reference, if any (after an unknown outcome).</summary>
    Task<GatewayTransaction?> FindTransactionByReferenceAsync(string reference, CancellationToken cancellationToken = default);

    /// <summary>A single-use payment link for a manual method (PSE...). Throws <see cref="Exceptions.BillingProblem.GatewayUnavailable"/>.</summary>
    Task<GatewayPaymentLink> CreatePaymentLinkAsync(GatewayPaymentLinkRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// The event, when its signature (the gateway's checksum with the events secret) is valid and it is from this deployment's environment;
    /// null otherwise (forged, altered or from the other environment: ignore it).
    /// </summary>
    GatewayEvent? VerifyEvent(ReadOnlySpan<byte> body, string? checksumHeader);
}

public sealed record GatewayBrowserSettings(string PublicKey, string ApiBaseUrl);

/// <param name="AcceptanceToken">The end-user policy acceptance token (sent back as <c>acceptance_token</c>).</param>
/// <param name="PersonalDataToken">The personal data authorization token (sent back as <c>accept_personal_auth</c>).</param>
public sealed record GatewayAcceptance(string AcceptanceToken, string AcceptancePermalink, string PersonalDataToken, string PersonalDataPermalink);

public sealed record GatewayPaymentSourceRequest(
    PaymentMethodType Type, string Token, string CustomerEmail, string AcceptanceToken, string PersonalDataToken);

/// <param name="Brand">Card brand when the gateway gives it.</param>
/// <param name="LastFour">The last four digits of the card or of the Nequi number when the gateway gives them.</param>
public sealed record GatewayPaymentSource(string Id, string Status, string? Brand, string? LastFour);

public sealed record GatewayChargeRequest(
    string Reference, long AmountInCents, string Currency, string CustomerEmail, string PaymentSourceId, PaymentMethodType MethodType);

public enum GatewayChargeOutcome
{
    /// <summary>The gateway created the transaction: <see cref="GatewayChargeResult.Transaction"/> is set (usually still pending).</summary>
    Created,

    /// <summary>The gateway refused the request: no transaction exists.</summary>
    Refused,

    /// <summary>No answer (timeout, transport failure, 5xx): the transaction may or may not exist.</summary>
    Unknown,
}

public sealed record GatewayChargeResult(GatewayChargeOutcome Outcome, GatewayTransaction? Transaction, string? Reason)
{
    public static GatewayChargeResult Unknown(string reason) => new(GatewayChargeOutcome.Unknown, null, reason);

    public static GatewayChargeResult Refused(string reason) => new(GatewayChargeOutcome.Refused, null, reason);
}

/// <summary>A transaction as the gateway reports it. The status is mapped to ours; an unknown status is <see cref="PaymentAttemptStatus.Pending"/>.</summary>
public sealed record GatewayTransaction(
    string Id, PaymentAttemptStatus Status, string? StatusMessage, long AmountInCents, string Currency, string? Reference, string? PaymentLinkId);

public sealed record GatewayPaymentLinkRequest(string Name, string Description, long AmountInCents, string Currency, DateTime ExpiresAt);

public sealed record GatewayPaymentLink(string Id, string Url);

/// <summary>
/// A verified event: what it is about, never trusted for amounts or statuses (the transaction is re-read). <paramref name="Checksum"/> is the
/// verified signature (lowercase hex), the key a re-sent event is recognised by; <paramref name="SignedAt"/> its signed timestamp.
/// </summary>
public sealed record GatewayEvent(string EventType, string? TransactionId, string? PaymentLinkId, string Checksum, DateTimeOffset SignedAt);
