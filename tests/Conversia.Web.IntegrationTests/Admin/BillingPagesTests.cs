extern alias ApiHost;

using System.Net;
using System.Net.Http.Json;
using Conversia.Application.DTOs.Billing;
using Conversia.Domain.Enums;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Admin;

/// <summary>
/// The billing pages against the real API (docs/BILLING.md): the administrator's plan catalogue and a company's subscription, the owner's
/// billing page. The test API has no gateway keys, so no payment button calls Wompi.
/// </summary>
[Collection(nameof(WebIntegrationCollection))]
public class BillingPagesTests(TestApiFactory api)
{
    private static async Task<Guid> PlanAsync(HttpClient admin, long pesos = 120_000)
    {
        var created = await admin.PostAsJsonAsync("/api/admin/billing/plans", new { code = "w" + Guid.NewGuid().ToString("N")[..12], name = "Plan web", pricePerLinePesos = pesos, includedAiConversations = 100 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<PlanResponse>())!.Id;
    }

    private async Task<(ApiSeed.Owner Owner, ApiSeed.Admin Admin, HttpClient AdminClient)> SubscribedAsync()
    {
        var admin = await ApiSeed.CreateAdminAsync(api);
        var owner = await ApiSeed.RegisterOwnerAsync(api, "facturación");
        var client = await ApiSeed.AdminClientAsync(api, admin);
        var planId = await PlanAsync(client);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/admin/tenants/{owner.TenantId}/billing/start", new { planId, trialDays = 0 })).StatusCode);
        return (owner, admin, client);
    }

    [Fact]
    public async Task The_owner_sees_the_plan_the_open_charge_and_the_history_and_cancels_at_period_end()
    {
        var (owner, _, _) = await SubscribedAsync();
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        var page = (await session.GetAsync("/organizacion/facturacion")).ShouldBe(HttpStatusCode.OK);

        Assert.Contains("Plan web", page.Text);
        Assert.Single(page.Select("[data-testid=billing-open]"));
        Assert.Single(page.Select("[data-testid=billing-charge]"));
        Assert.Contains("Sin medio de pago registrado", page.Text);
        Assert.Empty(page.Select("[data-testid=billing-pay-form]")); // no gateway, no payment buttons

        var cancel = FormModel.From(page, page.FindForm(f => f.QuerySelector("[data-testid=billing-cancel-submit]") is not null, "cancel form"));
        var after = await session.SubmitAsync(cancel);
        var followed = (await session.FollowAsync(after)).ShouldBe(HttpStatusCode.OK);

        Assert.Contains("terminará al final del periodo", followed.Text);
        Assert.Single(followed.Select("[data-testid=billing-undo]"));
    }

    [Fact]
    public async Task A_member_without_the_billing_permission_is_refused_and_a_company_without_subscription_says_so()
    {
        var (owner, _, _) = await SubscribedAsync();
        var member = await ApiSeed.CreateMemberAsync(api, owner, "sin-facturacion", Permission.OrganizationView);
        var other = await ApiSeed.RegisterOwnerAsync(api, "sin-suscripcion");
        using var host = WebTestHost.Create(api);

        using (var session = await WebFlows.SignedInAsync(host, member.Email))
        {
            var denied = (await session.GetAsync("/organizacion/facturacion")).ShouldBe(HttpStatusCode.Forbidden);
            Assert.Empty(denied.Select("[data-testid=billing-plan]"));
        }

        using var otherSession = await WebFlows.SignedInAsync(host, other.Email);
        var none = (await otherSession.GetAsync("/organizacion/facturacion")).ShouldBe(HttpStatusCode.OK);
        Assert.Single(none.Select("[data-testid=billing-none]"));
    }

    [Fact]
    public async Task Without_gateway_keys_the_payment_method_page_says_payments_are_unavailable()
    {
        var (owner, _, _) = await SubscribedAsync();
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        var page = (await session.GetAsync("/organizacion/facturacion/medio-de-pago")).ShouldBe(HttpStatusCode.ServiceUnavailable);

        Assert.Contains("no están disponibles", page.Text);
        Assert.Empty(page.Select("[data-testid=method-form]"));
    }

    [Fact]
    public async Task The_administrator_creates_a_plan_starts_a_subscription_and_marks_a_charge_paid_from_the_pages()
    {
        var admin = await ApiSeed.CreateAdminAsync(api);
        var owner = await ApiSeed.RegisterOwnerAsync(api, "facturación-admin");
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAdminAsync(host, admin);
        var code = "adm" + Guid.NewGuid().ToString("N")[..10];

        var plans = (await session.GetAsync("/admin/facturacion")).ShouldBe(HttpStatusCode.OK);
        var planForm = FormModel.From(plans, plans.FindForm(f => f.GetAttribute("data-testid") == "plan-form", "plan form"))
            .Set("Code", code).Set("Name", "Plan desde la web").Set("PricePerLinePesos", "150000").Set("IncludedAiConversations", "200");
        var saved = (await session.FollowAsync(await session.SubmitAsync(planForm))).ShouldBe(HttpStatusCode.OK);
        Assert.Contains(code, saved.Text);

        var billing = (await session.GetAsync($"/admin/empresas/{owner.TenantId}/facturacion")).ShouldBe(HttpStatusCode.OK);
        var startForm = FormModel.From(billing, billing.FindForm(f => f.GetAttribute("data-testid") == "billing-start-form", "start form"));
        var planId = billing.Select("#start-plan option").First(o => o.TextContent.Contains("Plan desde la web")).GetAttribute("value")!;
        startForm.Set("PlanId", planId).Set("TrialDays", "0");
        (await session.FollowAsync(await session.SubmitAsync(startForm))).ShouldBe(HttpStatusCode.OK);

        billing = (await session.GetAsync($"/admin/empresas/{owner.TenantId}/facturacion")).ShouldBe(HttpStatusCode.OK);
        Assert.Contains("Al día", billing.Text);
        var action = FormModel.From(billing, billing.FindForm(f => f.GetAttribute("data-testid") == "billing-charge-action-form", "charge action form"))
            .Set("ChargeId", billing.Select("#action-charge option").First().GetAttribute("value")!)
            .Set("Kind", "paid").Set("Reason", "Transferencia bancaria");
        var after = (await session.FollowAsync(await session.SubmitAsync(action))).ShouldBe(HttpStatusCode.OK);

        Assert.Equal("Paid", Assert.Single(after.Select("[data-testid=billing-charge]")).GetAttribute("data-charge-status"));
        var list = (await session.GetAsync("/admin")).ShouldBe(HttpStatusCode.OK);
        Assert.Contains(list.Select("[data-testid=company-billing]"), c => c.GetAttribute("data-billing") == "Active");
    }

    [Fact]
    public async Task The_administrator_sees_the_payments_to_review_and_closes_one()
    {
        var admin = await ApiSeed.CreateAdminAsync(api);
        var transaction = "tx-web-" + Guid.NewGuid().ToString("N");
        var itemId = await ApiSeed.SeedBillingReviewItemAsync(api, transaction);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAdminAsync(host, admin);

        var page = (await session.GetAsync("/admin/facturacion")).ShouldBe(HttpStatusCode.OK);
        Assert.Contains(page.Select("[data-testid=review-item]"), i => i.TextContent.Contains(transaction));
        var form = FormModel.From(page, page.FindForm(f => f.GetAttribute("data-testid") == "review-item-form", "review form"))
            .Set("ItemId", itemId.ToString()).Set("Reason", "Pago de otra integración");
        var after = (await session.FollowAsync(await session.SubmitAsync(form))).ShouldBe(HttpStatusCode.OK);

        Assert.DoesNotContain(after.Select("[data-testid=review-item]"), i => i.TextContent.Contains(transaction));
    }
}
