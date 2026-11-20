using Conversia.Application.Billing;
using Conversia.Domain.Common;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Billing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conversia.Infrastructure.UnitTests.Billing;

/// <summary>Runs only with sandbox keys in the environment (never in CI): it calls the real Wompi sandbox.</summary>
public sealed class WompiSandboxFactAttribute : FactAttribute
{
    public const string Prefix = "WOMPI_SANDBOX_";

    public WompiSandboxFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Prefix + "PRIVATE_KEY")))
        {
            Skip = $"Calls the real Wompi sandbox: set {Prefix}PUBLIC_KEY, {Prefix}PRIVATE_KEY, {Prefix}EVENTS_SECRET and {Prefix}INTEGRITY_SECRET to run it.";
        }
    }
}

/// <summary>
/// The facts the documentation leaves unverified (docs/BILLING.md, "Unverified"), checked against the sandbox: the acceptance call, a card
/// token turned into a payment source (brand and last four in <c>public_data</c>?), a charge with the published APPROVED test card, the
/// transaction read back and found by reference, and a payment link.
/// </summary>
public class WompiSandboxTests
{
    private static string Env(string name) => Environment.GetEnvironmentVariable(WompiSandboxFactAttribute.Prefix + name) ?? string.Empty;

    [WompiSandboxFact]
    public async Task The_whole_card_flow_works_against_the_sandbox()
    {
        var options = new WompiOptions
        {
            Environment = WompiEnvironment.Sandbox,
            PublicKey = Env("PUBLIC_KEY"),
            PrivateKey = Env("PRIVATE_KEY"),
            EventsSecret = Env("EVENTS_SECRET"),
            IntegritySecret = Env("INTEGRITY_SECRET"),
        };
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var gateway = new WompiGateway(http, options, NullLogger<WompiGateway>.Instance);
        var acceptance = await gateway.GetAcceptanceAsync();

        // What the browser does: tokenize the published APPROVED test card with the public key.
        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, WompiOptions.SandboxBaseUrl + "/tokens/cards")
        {
            Content = new StringContent("""{"number":"4242424242424242","cvc":"123","exp_month":"12","exp_year":"30","card_holder":"Prueba Conversia"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        tokenRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.PublicKey);
        using var tokenResponse = await http.SendAsync(tokenRequest);
        var token = System.Text.Json.JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync()).RootElement.GetProperty("data").GetProperty("id").GetString()!;

        var source = await gateway.CreatePaymentSourceAsync(new GatewayPaymentSourceRequest(PaymentMethodType.Card, token, "sandbox@example.test", acceptance.AcceptanceToken, acceptance.PersonalDataToken));
        var reference = BillingReference.New();
        var charge = await gateway.ChargeAsync(new GatewayChargeRequest(reference, 150000, "COP", "sandbox@example.test", source.Id, PaymentMethodType.Card));
        Assert.Equal(GatewayChargeOutcome.Created, charge.Outcome);

        GatewayTransaction? read = null;
        for (var i = 0; i < 10 && read?.Status is null or PaymentAttemptStatus.Pending; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            read = await gateway.GetTransactionAsync(charge.Transaction!.Id);
        }

        Assert.Equal(PaymentAttemptStatus.Approved, read!.Status);
        Assert.Equal(150000, read.AmountInCents);
        Assert.Equal(reference, (await gateway.FindTransactionByReferenceAsync(reference))?.Reference);
        var link = await gateway.CreatePaymentLinkAsync(new GatewayPaymentLinkRequest("Conversia prueba", "Prueba", 150000, "COP", DateTime.UtcNow.AddDays(1)));
        Assert.StartsWith(WompiOptions.CheckoutLinkBaseUrl, link.Url);
    }
}
