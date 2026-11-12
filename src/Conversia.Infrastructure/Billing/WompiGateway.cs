using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Conversia.Application.Billing;
using Conversia.Application.Exceptions;
using Conversia.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Conversia.Infrastructure.Billing;

/// <summary>
/// The Wompi (Colombia) adapter of <see cref="IPaymentGateway"/>: the only class that knows Wompi's URLs, keys, field names and signatures
/// (every fact with its source in docs/BILLING.md, "Wompi facts"). One attempt per call (no automatic retry: a POST creates a transaction or
/// a link, and after a timeout nobody knows whether it did). The keys, the secrets, the tokens and the bodies never reach a log: a failure is
/// logged by its status and the gateway's error type only.
/// </summary>
public sealed class WompiGateway : IPaymentGateway
{
    /// <summary>More than any answer of the gateway should weigh; a bigger one is not read.</summary>
    public const int MaxResponseBytes = 256 * 1024;

    /// <summary>The largest event body the events endpoint reads.</summary>
    public const int MaxEventBytes = 64 * 1024;

    private static readonly MediaTypeHeaderValue JsonMediaType = new("application/json");

    private readonly HttpClient _http;
    private readonly WompiOptions _options;
    private readonly ILogger<WompiGateway> _logger;

    public WompiGateway(HttpClient http, WompiOptions options, ILogger<WompiGateway> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    public bool IsConfigured => true;

    public GatewayBrowserSettings BrowserSettings => new(_options.PublicKey, _options.BaseUrl);

    // GET /merchants/info with the public key in x-merchant-public-key (GET /merchants/{public_key} stopped on 2026-10-31).
    public async Task<GatewayAcceptance> GetAcceptanceAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url("merchants/info"));
        request.Headers.TryAddWithoutValidation("x-merchant-public-key", _options.PublicKey);
        var data = await SendForDataAsync(request, "acceptance", cancellationToken)
                   ?? throw new BillingException(BillingProblem.GatewayUnavailable);
        var policy = data["presigned_acceptance"];
        var personal = data["presigned_personal_data_auth"];
        return new GatewayAcceptance(
            Text(policy?["acceptance_token"]) ?? throw Malformed("acceptance"),
            Text(policy?["permalink"]) ?? throw Malformed("acceptance"),
            Text(personal?["acceptance_token"]) ?? throw Malformed("acceptance"),
            Text(personal?["permalink"]) ?? throw Malformed("acceptance"));
    }

    public async Task<GatewayPaymentSource> CreatePaymentSourceAsync(GatewayPaymentSourceRequest request, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["type"] = request.Type == PaymentMethodType.Nequi ? "NEQUI" : "CARD",
            ["token"] = request.Token,
            ["customer_email"] = request.CustomerEmail,
            ["acceptance_token"] = request.AcceptanceToken,
            ["accept_personal_auth"] = request.PersonalDataToken,
        };
        using var message = Private(HttpMethod.Post, "payment_sources", body);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception ex) when (IsTransport(ex, cancellationToken))
        {
            LogTransport("payment source", ex);
            throw new BillingException(BillingProblem.GatewayUnavailable, ex);
        }

        using (response)
        {
            var json = await ReadAsync(response, cancellationToken);
            if (IsClientError(response.StatusCode))
            {
                _logger.LogWarning("Wompi refused a payment source: {Status} {ErrorType}.", (int)response.StatusCode, ErrorType(json));
                throw new BillingException(BillingProblem.PaymentMethodRejected);
            }

            if (!response.IsSuccessStatusCode || json?["data"] is not JsonObject data)
            {
                _logger.LogWarning("Wompi failed to create a payment source: {Status}.", (int)response.StatusCode);
                throw new BillingException(BillingProblem.GatewayUnavailable);
            }

            var publicData = data["public_data"];
            var phone = Text(publicData?["phone_number"]);
            return new GatewayPaymentSource(
                Text(data["id"]) ?? throw Malformed("payment source"),
                Text(data["status"]) ?? string.Empty,
                Text(publicData?["brand"]),
                Text(publicData?["last_four"]) ?? phone);
        }
    }

    public async Task<GatewayChargeResult> ChargeAsync(GatewayChargeRequest request, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["amount_in_cents"] = request.AmountInCents,
            ["currency"] = request.Currency,
            ["signature"] = WompiSignatures.Integrity(request.Reference, request.AmountInCents, request.Currency, _options.IntegritySecret),
            ["customer_email"] = request.CustomerEmail,
            ["reference"] = request.Reference,
            ["payment_source_id"] = long.TryParse(request.PaymentSourceId, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric)
                ? JsonValue.Create(numeric)
                : JsonValue.Create(request.PaymentSourceId),
        };
        if (request.MethodType == PaymentMethodType.Card)
        {
            // One installment, and "recurrent" (Credential on File, same amount each period): honoured for VISA and Mastercard through RBM.
            body["payment_method"] = new JsonObject { ["installments"] = 1 };
            body["recurrent"] = true;
        }

        using var message = Private(HttpMethod.Post, "transactions", body);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception ex) when (IsTransport(ex, cancellationToken))
        {
            LogTransport("transaction", ex);
            return GatewayChargeResult.Unknown(ex.GetType().Name);
        }

        using (response)
        {
            JsonNode? json;
            try
            {
                json = await ReadAsync(response, cancellationToken);
            }
            catch (Exception ex) when (IsTransport(ex, cancellationToken))
            {
                LogTransport("transaction body", ex);
                return GatewayChargeResult.Unknown(ex.GetType().Name);
            }

            if (IsClientError(response.StatusCode))
            {
                var type = ErrorType(json);
                _logger.LogWarning("Wompi refused a transaction: {Status} {ErrorType}.", (int)response.StatusCode, type);
                return GatewayChargeResult.Refused(type ?? $"http_{(int)response.StatusCode}");
            }

            if (!response.IsSuccessStatusCode || json?["data"] is not JsonObject data || Transaction(data) is not { } transaction)
            {
                _logger.LogWarning("Wompi answered a transaction with {Status} and no usable body: outcome unknown.", (int)response.StatusCode);
                return GatewayChargeResult.Unknown($"http_{(int)response.StatusCode}");
            }

            return new GatewayChargeResult(GatewayChargeOutcome.Created, transaction, null);
        }
    }

    public async Task<GatewayTransaction?> GetTransactionAsync(string transactionId, CancellationToken cancellationToken = default)
    {
        using var request = Private(HttpMethod.Get, "transactions/" + Uri.EscapeDataString(transactionId), null);
        var data = await SendForDataAsync(request, "transaction", cancellationToken);
        return data is JsonObject found ? Transaction(found) : null;
    }

    // Unverified: the documentation does not show this filter; isolated here and covered by the opt-in sandbox test.
    public async Task<GatewayTransaction?> FindTransactionByReferenceAsync(string reference, CancellationToken cancellationToken = default)
    {
        using var request = Private(HttpMethod.Get, "transactions?reference=" + Uri.EscapeDataString(reference), null);
        var data = await SendForDataAsync(request, "transaction search", cancellationToken);
        var match = data switch
        {
            JsonArray list => list.OfType<JsonObject>().FirstOrDefault(t => Text(t["reference"]) == reference),
            JsonObject single when Text(single["reference"]) == reference => single,
            _ => null,
        };
        return match is null ? null : Transaction(match);
    }

    public async Task<GatewayPaymentLink> CreatePaymentLinkAsync(GatewayPaymentLinkRequest request, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["name"] = request.Name,
            ["description"] = request.Description,
            ["single_use"] = true,
            ["collect_shipping"] = false,
            ["currency"] = request.Currency,
            ["amount_in_cents"] = request.AmountInCents,
            ["expires_at"] = request.ExpiresAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        };
        using var message = Private(HttpMethod.Post, "payment_links", body);
        var data = await SendForDataAsync(message, "payment link", cancellationToken) ?? throw new BillingException(BillingProblem.GatewayUnavailable);
        var id = Text(data["id"]) ?? throw Malformed("payment link");
        return new GatewayPaymentLink(id, WompiOptions.CheckoutLinkBaseUrl + Uri.EscapeDataString(id));
    }

    public GatewayEvent? VerifyEvent(ReadOnlySpan<byte> body, string? checksumHeader)
    {
        if (body.Length is 0 or > MaxEventBytes)
        {
            return null;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }

        if (root is not JsonObject json
            || Text(json["event"]) is not { Length: > 0 } eventType
            || json["data"] is not JsonObject data
            || json["signature"] is not JsonObject signature
            || signature["properties"] is not JsonArray properties
            || Text(signature["checksum"]) is not { Length: > 0 } checksum
            || Text(json["timestamp"]) is not { Length: > 0 } timestamp)
        {
            return null;
        }

        // Never hard-code the properties: each event lists its own (the documentation insists).
        var values = new List<string>();
        foreach (var property in properties)
        {
            if (Text(property) is not { Length: > 0 } path)
            {
                return null;
            }

            JsonNode? node = data;
            foreach (var segment in path.Split('.'))
            {
                node = node is JsonObject o ? o[segment] : null;
            }

            values.Add(Text(node) ?? string.Empty);
        }

        var expected = WompiSignatures.EventChecksum(values, timestamp, _options.EventsSecret);
        if (!WompiSignatures.HexEquals(expected, checksum) || (checksumHeader is not null && !WompiSignatures.HexEquals(expected, checksumHeader)))
        {
            return null;
        }

        if (!string.Equals(Text(json["environment"]), _options.EventEnvironment, StringComparison.Ordinal))
        {
            // A real event of the other environment (the same account, two configurations): not ours to apply.
            return null;
        }

        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds > 253402300799)
        {
            return null;
        }

        var transaction = data["transaction"] as JsonObject;
        return new GatewayEvent(eventType, Text(transaction?["id"]), Text(transaction?["payment_link_id"]), expected, DateTimeOffset.FromUnixTimeSeconds(seconds));
    }

    // ------------------------------------------------------------------ plumbing

    private Uri Url(string path) => new(_options.BaseUrl + "/" + path);

    private HttpRequestMessage Private(HttpMethod method, string path, JsonObject? body)
    {
        var request = new HttpRequestMessage(method, Url(path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.PrivateKey);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body.ToJsonString())) { Headers = { ContentType = JsonMediaType } };
        }

        return request;
    }

    /// <summary>The <c>data</c> of a successful answer; null for a 404; throws <see cref="BillingProblem.GatewayUnavailable"/> otherwise.</summary>
    private async Task<JsonNode?> SendForDataAsync(HttpRequestMessage request, string what, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception ex) when (IsTransport(ex, cancellationToken))
        {
            LogTransport(what, ex);
            throw new BillingException(BillingProblem.GatewayUnavailable, ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            JsonNode? json;
            try
            {
                json = await ReadAsync(response, cancellationToken);
            }
            catch (Exception ex) when (IsTransport(ex, cancellationToken))
            {
                LogTransport(what, ex);
                throw new BillingException(BillingProblem.GatewayUnavailable, ex);
            }

            if (!response.IsSuccessStatusCode || json?["data"] is not { } data)
            {
                _logger.LogWarning("Wompi answered the {What} call with {Status} {ErrorType}.", what, (int)response.StatusCode, ErrorType(json));
                throw new BillingException(BillingProblem.GatewayUnavailable);
            }

            return data;
        }
    }

    private static async Task<JsonNode?> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(buffer.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static GatewayTransaction? Transaction(JsonObject data)
    {
        if (Text(data["id"]) is not { Length: > 0 } id || data["amount_in_cents"] is not JsonValue amountNode || !amountNode.TryGetValue<long>(out var amount))
        {
            return null;
        }

        return new GatewayTransaction(
            id,
            MapStatus(Text(data["status"])),
            Text(data["status_message"]),
            amount,
            Text(data["currency"]) ?? string.Empty,
            Text(data["reference"]),
            Text(data["payment_link_id"]));
    }

    /// <summary>The documented statuses (https://docs.wompi.co/docs/colombia/transacciones/); anything else is treated as still pending.</summary>
    public static PaymentAttemptStatus MapStatus(string? status) => status switch
    {
        "APPROVED" => PaymentAttemptStatus.Approved,
        "DECLINED" => PaymentAttemptStatus.Declined,
        "VOIDED" => PaymentAttemptStatus.Voided,
        "ERROR" => PaymentAttemptStatus.Error,
        _ => PaymentAttemptStatus.Pending,
    };

    private static string? Text(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out var s) => s,
        JsonValue value when value.TryGetValue<long>(out var l) => l.ToString(CultureInfo.InvariantCulture),
        JsonValue value when value.TryGetValue<decimal>(out var d) => d.ToString(CultureInfo.InvariantCulture),
        JsonValue value when value.TryGetValue<bool>(out var b) => b ? "true" : "false",
        _ => null,
    };

    private static string? ErrorType(JsonNode? json)
    {
        var type = Text(json?["error"]?["type"]);
        // Only a short machine word: never the messages (they can echo what was sent).
        return type is { Length: <= 64 } && type.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? type : null;
    }

    private static bool IsClientError(HttpStatusCode status) => (int)status is >= 400 and < 500 and not 408 and not 429;

    private static bool IsTransport(Exception ex, CancellationToken cancellationToken)
        => !cancellationToken.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException or IOException;

    private void LogTransport(string what, Exception ex)
        => _logger.LogWarning("The Wompi {What} call failed: {ExceptionType}.", what, ex.GetType().Name);

    private static BillingException Malformed(string what) => new(BillingProblem.GatewayUnavailable, new InvalidOperationException($"Wompi answered the {what} call without the expected fields."));
}

/// <summary>The gateway of a deployment without Wompi keys: billing still creates charges, nothing is ever sent.</summary>
public sealed class UnconfiguredPaymentGateway : IPaymentGateway
{
    public bool IsConfigured => false;

    public GatewayBrowserSettings BrowserSettings => throw NotConfigured();

    public Task<GatewayAcceptance> GetAcceptanceAsync(CancellationToken cancellationToken = default) => throw NotConfigured();

    public Task<GatewayPaymentSource> CreatePaymentSourceAsync(GatewayPaymentSourceRequest request, CancellationToken cancellationToken = default) => throw NotConfigured();

    public Task<GatewayChargeResult> ChargeAsync(GatewayChargeRequest request, CancellationToken cancellationToken = default) => throw NotConfigured();

    public Task<GatewayTransaction?> GetTransactionAsync(string transactionId, CancellationToken cancellationToken = default) => throw NotConfigured();

    public Task<GatewayTransaction?> FindTransactionByReferenceAsync(string reference, CancellationToken cancellationToken = default) => throw NotConfigured();

    public Task<GatewayPaymentLink> CreatePaymentLinkAsync(GatewayPaymentLinkRequest request, CancellationToken cancellationToken = default) => throw NotConfigured();

    public GatewayEvent? VerifyEvent(ReadOnlySpan<byte> body, string? checksumHeader) => null;

    private static BillingException NotConfigured() => new(BillingProblem.GatewayNotConfigured);
}
