using System.Net;
using System.Text;
using System.Text.Json;
using Conversia.Application.Billing;
using Conversia.Application.Exceptions;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Billing;
using Microsoft.Extensions.Logging;

namespace Conversia.Infrastructure.UnitTests.Billing;

/// <summary>The Wompi adapter against a fake transport: the requests it builds, the signatures, how each answer is read, and what it never logs.</summary>
public class WompiGatewayTests
{
    // Test-only values with the documented sandbox prefixes; nothing real.
    private const string PublicKey = "pub_test_unitkey000";
    private const string PrivateKey = "prv_test_unitkey000";
    private const string EventsSecret = "test_events_unitsecret000";
    private const string IntegritySecret = "test_integrity_unitsecret000";

    private readonly RecordingHandler _handler = new();
    private readonly RecordingLogger _logger = new();

    private WompiGateway Gateway(WompiEnvironment environment = WompiEnvironment.Sandbox) => new(
        new HttpClient(_handler),
        new WompiOptions
        {
            Environment = environment,
            PublicKey = PublicKey,
            PrivateKey = PrivateKey,
            EventsSecret = EventsSecret,
            IntegritySecret = IntegritySecret,
        },
        _logger);

    // ------------------------------------------------------------------ signatures

    [Fact]
    public void The_integrity_signature_matches_the_documented_vector()
    {
        // https://docs.wompi.co/docs/colombia/widget-checkout-web/ ("Firma de integridad"), the example without expiration.
        Assert.Equal(
            "37c8407747e595535433ef8f6a811d853cd943046624a0ec04662b17bbf33bf5",
            WompiSignatures.Integrity("sk8-438k4-xmxm392-sn2m", 2490000, "COP", "prod_integrity_Z5mMke9x0k8gpErbDqwrJXMqsI6SFli6"));
    }

    [Fact]
    public void The_event_checksum_follows_the_documented_algorithm()
    {
        // https://docs.wompi.co/docs/colombia/eventos/: the values of the properties, the timestamp and the secret, concatenated. (The page's
        // printed checksum does not match its own example string; this pins the algorithm on that string, SHA-256 computed independently.)
        Assert.Equal(
            "5a18ec5e8fdb7df463e9f94774cba8f583ba21bd04a09ceff2ea68a4bc0aefbe",
            WompiSignatures.EventChecksum(["1234-1610641025-49201", "APPROVED", "4490000"], "1530291411", "prod_events_OcHnIzeBl5socpwByQ4hA52Em3USQ93Z"));
    }

    [Fact]
    public void Hex_digests_compare_without_case_and_reject_anything_else()
    {
        Assert.True(WompiSignatures.HexEquals("ab12", "AB12"));
        Assert.False(WompiSignatures.HexEquals("ab12", "ab13"));
        Assert.False(WompiSignatures.HexEquals("ab12", "ab1"));
        Assert.False(WompiSignatures.HexEquals("ab12", null));
    }

    // ------------------------------------------------------------------ events

    private static string Event(string status = "APPROVED", string environment = "test", string secret = EventsSecret, string? linkId = null, long amount = 4490000)
    {
        var properties = new[] { "transaction.id", "transaction.status", "transaction.amount_in_cents" };
        var checksum = WompiSignatures.EventChecksum(["tx-123", status, amount.ToString()], "1530291411", secret);
        return JsonSerializer.Serialize(new
        {
            @event = "transaction.updated",
            data = new
            {
                transaction = new
                {
                    id = "tx-123", amount_in_cents = amount, reference = "cv_x", customer_email = "payer@example.test", currency = "COP",
                    payment_method_type = "NEQUI", status, payment_link_id = linkId,
                },
            },
            environment,
            signature = new { properties, checksum },
            timestamp = 1530291411,
            sent_at = "2018-07-20T16:45:05.000Z",
        });
    }

    [Fact]
    public void A_signed_event_of_this_environment_is_verified_and_reduced_to_ids()
    {
        var verified = Gateway().VerifyEvent(Encoding.UTF8.GetBytes(Event(linkId: "link-9")), null);

        Assert.Equal(("transaction.updated", "tx-123", "link-9"), (verified!.EventType, verified.TransactionId, verified.PaymentLinkId));
        Assert.Equal(WompiSignatures.EventChecksum(["tx-123", "APPROVED", "4490000"], "1530291411", EventsSecret), verified.Checksum);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1530291411), verified.SignedAt);
    }

    [Fact]
    public void A_forged_or_altered_event_is_rejected()
    {
        var gateway = Gateway();
        var tampered = Event().Replace("4490000", "100");

        Assert.Null(gateway.VerifyEvent(Encoding.UTF8.GetBytes(tampered), null));
        Assert.Null(gateway.VerifyEvent(Encoding.UTF8.GetBytes(Event(secret: "test_events_other")), null));
        Assert.Null(gateway.VerifyEvent("not json"u8, null));
        Assert.Null(gateway.VerifyEvent("{}"u8, null));
        Assert.Null(gateway.VerifyEvent(new byte[WompiGateway.MaxEventBytes + 1], null));
    }

    [Fact]
    public void The_checksum_header_must_agree_with_the_body_and_the_environment_must_be_ours()
    {
        var gateway = Gateway();
        var body = Encoding.UTF8.GetBytes(Event());

        Assert.Null(gateway.VerifyEvent(body, "deadbeef"));
        Assert.NotNull(gateway.VerifyEvent(body, WompiSignatures.EventChecksum(["tx-123", "APPROVED", "4490000"], "1530291411", EventsSecret).ToUpperInvariant()));
        Assert.Null(gateway.VerifyEvent(Encoding.UTF8.GetBytes(Event(environment: "prod")), null));
    }

    // ------------------------------------------------------------------ calls

    [Fact]
    public async Task A_charge_posts_the_documented_fields_with_the_private_key_and_the_integrity_signature()
    {
        _handler.Respond = (_, _) => Json(HttpStatusCode.Created, """{"data":{"id":"15113-1","status":"PENDING","amount_in_cents":30000000,"currency":"COP","reference":"cv_abc"}}""");

        var result = await Gateway().ChargeAsync(new GatewayChargeRequest("cv_abc", 30000000, "COP", "owner@example.test", "3891", PaymentMethodType.Card));

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://sandbox.wompi.co/v1/transactions", request.Uri.ToString());
        Assert.Equal($"Bearer {PrivateKey}", request.Authorization);
        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        Assert.Equal(30000000, root.GetProperty("amount_in_cents").GetInt64());
        Assert.Equal("COP", root.GetProperty("currency").GetString());
        Assert.Equal("cv_abc", root.GetProperty("reference").GetString());
        Assert.Equal(3891, root.GetProperty("payment_source_id").GetInt64());
        Assert.Equal(WompiSignatures.Integrity("cv_abc", 30000000, "COP", IntegritySecret), root.GetProperty("signature").GetString());
        Assert.Equal(1, root.GetProperty("payment_method").GetProperty("installments").GetInt32());
        Assert.True(root.GetProperty("recurrent").GetBoolean());
        Assert.Equal(GatewayChargeOutcome.Created, result.Outcome);
        Assert.Equal(new GatewayTransaction("15113-1", PaymentAttemptStatus.Pending, null, 30000000, "COP", "cv_abc", null), result.Transaction);
    }

    [Fact]
    public async Task A_nequi_charge_sends_no_card_fields()
    {
        _handler.Respond = (_, _) => Json(HttpStatusCode.Created, """{"data":{"id":"1","status":"PENDING","amount_in_cents":100,"currency":"COP","reference":"cv_n"}}""");

        await Gateway().ChargeAsync(new GatewayChargeRequest("cv_n", 100, "COP", "owner@example.test", "77", PaymentMethodType.Nequi));

        using var body = JsonDocument.Parse(_handler.Requests[0].Body);
        Assert.False(body.RootElement.TryGetProperty("payment_method", out _));
        Assert.False(body.RootElement.TryGetProperty("recurrent", out _));
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity, GatewayChargeOutcome.Refused)]
    [InlineData(HttpStatusCode.Unauthorized, GatewayChargeOutcome.Refused)]
    [InlineData(HttpStatusCode.InternalServerError, GatewayChargeOutcome.Unknown)]
    [InlineData(HttpStatusCode.TooManyRequests, GatewayChargeOutcome.Unknown)]
    [InlineData(HttpStatusCode.Created, GatewayChargeOutcome.Unknown)] // no usable body
    public async Task Charge_answers_are_classified_without_guessing(HttpStatusCode status, GatewayChargeOutcome expected)
    {
        _handler.Respond = (_, _) => Json(status, """{"error":{"type":"INPUT_VALIDATION_ERROR","messages":{"reference":["owner@example.test"]}}}""");

        var result = await Gateway().ChargeAsync(new GatewayChargeRequest("cv_e", 100, "COP", "owner@example.test", "1", PaymentMethodType.Card));

        Assert.Equal(expected, result.Outcome);
        Assert.DoesNotContain(_logger.Lines, l => l.Contains("owner@example.test") || l.Contains(PrivateKey) || l.Contains(IntegritySecret));
    }

    [Fact]
    public async Task A_timeout_of_a_charge_is_an_unknown_outcome_and_is_not_retried()
    {
        _handler.Respond = (_, _) => throw new TaskCanceledException("timeout");

        var result = await Gateway().ChargeAsync(new GatewayChargeRequest("cv_t", 100, "COP", "owner@example.test", "1", PaymentMethodType.Card));

        Assert.Equal(GatewayChargeOutcome.Unknown, result.Outcome);
        Assert.Single(_handler.Requests);
    }

    [Theory]
    [InlineData("APPROVED", PaymentAttemptStatus.Approved)]
    [InlineData("DECLINED", PaymentAttemptStatus.Declined)]
    [InlineData("VOIDED", PaymentAttemptStatus.Voided)]
    [InlineData("ERROR", PaymentAttemptStatus.Error)]
    [InlineData("PENDING", PaymentAttemptStatus.Pending)]
    [InlineData("SOMETHING_NEW", PaymentAttemptStatus.Pending)]
    public void Statuses_map_to_ours(string status, PaymentAttemptStatus expected) => Assert.Equal(expected, WompiGateway.MapStatus(status));

    [Fact]
    public async Task A_transaction_is_read_with_the_private_key_and_an_unknown_one_is_null()
    {
        _handler.Respond = (request, _) => request.RequestUri!.AbsolutePath.EndsWith("/missing")
            ? Json(HttpStatusCode.NotFound, """{"error":{"type":"NOT_FOUND_ERROR"}}""")
            : Json(HttpStatusCode.OK, """{"data":{"id":"tx-1","status":"DECLINED","status_message":"Fondos insuficientes","amount_in_cents":500,"currency":"COP","reference":"cv_r","payment_link_id":"lnk"}}""");

        var gateway = Gateway(WompiEnvironment.Production);
        var found = await gateway.GetTransactionAsync("tx-1");
        var missing = await gateway.GetTransactionAsync("missing");

        Assert.Equal("https://production.wompi.co/v1/transactions/tx-1", _handler.Requests[0].Uri.ToString());
        Assert.Equal($"Bearer {PrivateKey}", _handler.Requests[0].Authorization);
        Assert.Equal(new GatewayTransaction("tx-1", PaymentAttemptStatus.Declined, "Fondos insuficientes", 500, "COP", "cv_r", "lnk"), found);
        Assert.Null(missing);
    }

    [Fact]
    public async Task A_failing_read_is_gateway_unavailable()
    {
        _handler.Respond = (_, _) => Json(HttpStatusCode.BadGateway, "oops");

        var failure = await Assert.ThrowsAsync<BillingException>(() => Gateway().GetTransactionAsync("tx-1"));

        Assert.Equal(BillingProblem.GatewayUnavailable, failure.Problem);
    }

    [Fact]
    public async Task The_acceptance_comes_from_merchants_info_with_the_public_key()
    {
        _handler.Respond = (_, _) => Json(HttpStatusCode.OK, """
            {"data":{"presigned_acceptance":{"acceptance_token":"eyJ.a.b","permalink":"https://wompi.example/terms.pdf","type":"END_USER_POLICY"},
                     "presigned_personal_data_auth":{"acceptance_token":"eyJ.c.d","permalink":"https://wompi.example/data.pdf","type":"PERSONAL_DATA_AUTH"}}}
            """);

        var acceptance = await Gateway().GetAcceptanceAsync();

        var request = Assert.Single(_handler.Requests);
        Assert.Equal("https://sandbox.wompi.co/v1/merchants/info", request.Uri.ToString());
        Assert.Equal(PublicKey, request.Headers["x-merchant-public-key"].Single());
        Assert.Null(request.Authorization); // never the private key for a public call
        Assert.Equal(new GatewayAcceptance("eyJ.a.b", "https://wompi.example/terms.pdf", "eyJ.c.d", "https://wompi.example/data.pdf"), acceptance);
    }

    [Fact]
    public async Task A_payment_source_is_created_from_the_browser_token_with_both_acceptances()
    {
        _handler.Respond = (_, _) => Json(HttpStatusCode.Created, """{"data":{"id":3891,"public_data":{"type":"NEQUI","phone_number":"3991111111"},"type":"NEQUI","status":"AVAILABLE"}}""");

        var source = await Gateway().CreatePaymentSourceAsync(new GatewayPaymentSourceRequest(PaymentMethodType.Nequi, "nequi_test_tok", "owner@example.test", "eyJ.a.b", "eyJ.c.d"));

        var request = Assert.Single(_handler.Requests);
        Assert.Equal("https://sandbox.wompi.co/v1/payment_sources", request.Uri.ToString());
        Assert.Equal($"Bearer {PrivateKey}", request.Authorization);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("NEQUI", body.RootElement.GetProperty("type").GetString());
        Assert.Equal("nequi_test_tok", body.RootElement.GetProperty("token").GetString());
        Assert.Equal("eyJ.a.b", body.RootElement.GetProperty("acceptance_token").GetString());
        Assert.Equal("eyJ.c.d", body.RootElement.GetProperty("accept_personal_auth").GetString());
        Assert.Equal(new GatewayPaymentSource("3891", "AVAILABLE", null, "3991111111"), source);
    }

    [Fact]
    public async Task A_refused_payment_source_is_payment_method_rejected()
    {
        _handler.Respond = (_, _) => Json(HttpStatusCode.UnprocessableEntity, """{"error":{"type":"INPUT_VALIDATION_ERROR"}}""");

        var failure = await Assert.ThrowsAsync<BillingException>(() => Gateway().CreatePaymentSourceAsync(new GatewayPaymentSourceRequest(PaymentMethodType.Card, "tok", "o@example.test", "a", "b")));

        Assert.Equal(BillingProblem.PaymentMethodRejected, failure.Problem);
    }

    [Fact]
    public async Task A_payment_link_is_single_use_and_points_to_the_checkout()
    {
        _handler.Respond = (_, _) => Json(HttpStatusCode.Created, """{"data":{"id":"G8Ybu1"}}""");

        var link = await Gateway().CreatePaymentLinkAsync(new GatewayPaymentLinkRequest("Conversia", "Periodo", 5000000, "COP", new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc)));

        using var body = JsonDocument.Parse(_handler.Requests[0].Body);
        Assert.True(body.RootElement.GetProperty("single_use").GetBoolean());
        Assert.False(body.RootElement.GetProperty("collect_shipping").GetBoolean());
        Assert.Equal(5000000, body.RootElement.GetProperty("amount_in_cents").GetInt64());
        Assert.Equal("2026-12-01T00:00:00Z", body.RootElement.GetProperty("expires_at").GetString());
        Assert.Equal(new GatewayPaymentLink("G8Ybu1", "https://checkout.wompi.co/l/G8Ybu1"), link);
    }

    [Fact]
    public void The_unconfigured_gateway_refuses_everything_and_verifies_nothing()
    {
        var gateway = new UnconfiguredPaymentGateway();

        Assert.False(gateway.IsConfigured);
        Assert.Null(gateway.VerifyEvent(Encoding.UTF8.GetBytes(Event()), null));
        Assert.Equal(BillingProblem.GatewayNotConfigured, Assert.Throws<BillingException>(() => gateway.BrowserSettings).Problem);
    }

    // ------------------------------------------------------------------ plumbing

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> Respond { get; set; } = (_, _) => Json(HttpStatusCode.OK, "{}");

        public List<(HttpMethod Method, Uri Uri, string? Authorization, Dictionary<string, string[]> Headers, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization?.ToString(),
                request.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray(), StringComparer.OrdinalIgnoreCase),
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return Respond(request, cancellationToken);
        }
    }

    private sealed class RecordingLogger : ILogger<WompiGateway>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add($"{logLevel}: {formatter(state, exception)}");
    }
}
