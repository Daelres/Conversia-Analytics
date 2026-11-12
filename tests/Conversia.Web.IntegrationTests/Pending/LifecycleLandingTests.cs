extern alias ApiHost;

using System.Net;
using Conversia.Application.Interfaces;
using Conversia.Domain.Common;
using Conversia.Web.IntegrationTests.Dashboard;
using Conversia.Web.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Conversia.Web.IntegrationTests.Pending;

/// <summary>
/// The landing page of a company in pause, suspended for a late payment, or cancelled (docs/PLATFORM_ADMIN.md, "Lifecycle of a company"):
/// each says where the company stands in its own words, and the operating pages send the person there. Runs against the real API.
/// </summary>
[Collection(nameof(WebIntegrationCollection))]
public class LifecycleLandingTests(TestApiFactory api)
{
    private const string PendingPage = "/cuenta-pendiente";

    private async Task<PageResponse> LandAsync(ApiSeed.Owner owner)
    {
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);
        (await session.GetAsync("/conversaciones")).ShouldRedirectTo(PendingPage);
        return (await session.GetAsync(PendingPage)).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_paused_company_lands_on_a_page_that_says_its_messages_keep_being_saved()
    {
        var admin = await ApiSeed.CreateAdminAsync(api);
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        await ApiSeed.SetStatusAsync(api, admin, owner.TenantId, "Paused", "Vacaciones");

        var page = await LandAsync(owner);

        Assert.Equal("Tu empresa está en pausa", page.Document.QuerySelector("main h1")!.Plain().Trim());
        Assert.Equal("Paused", page.Document.QuerySelector("[data-page=account-state]")!.GetAttribute("data-status"));
        Assert.Equal("En pausa", page.Document.QuerySelector("[data-testid=tenant-status]")!.Plain().Trim());
        Assert.Contains("se guardan todos", page.Document.QuerySelector("[data-testid=paused-help]")!.Plain());
        Assert.DoesNotContain("Vacaciones", page.Body);
        Assert.Empty(page.Select("[data-testid=error-code]"));
    }

    [Fact]
    public async Task A_company_suspended_for_a_late_payment_lands_on_a_payment_pending_page()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using (var scope = api.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ITenantBillingLifecycle>().SuspendForBillingAsync(owner.TenantId, ReasonCodes.BillingPastDue);
        }

        var page = await LandAsync(owner);

        Assert.Equal("Tu empresa tiene un pago pendiente", page.Document.QuerySelector("main h1")!.Plain().Trim());
        Assert.Equal("Billing", page.Document.QuerySelector("[data-page=account-state]")!.GetAttribute("data-cause"));
        Assert.Equal("Suspendida por pago pendiente", page.Document.QuerySelector("[data-testid=tenant-status]")!.Plain().Trim());
        var help = page.Document.QuerySelector("[data-testid=billing-help]")!.Plain();
        Assert.Contains("pago pendiente", help);
        Assert.Contains("no pierdes ninguno", help);
        Assert.Empty(page.Select("[data-testid=suspended-help]"));
    }

    [Fact]
    public async Task A_cancelled_company_lands_on_a_page_with_the_date_its_data_is_deleted()
    {
        var admin = await ApiSeed.CreateAdminAsync(api);
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        await ApiSeed.SetStatusAsync(api, admin, owner.TenantId, "Cancelled", "Fin del contrato");

        var page = await LandAsync(owner);

        Assert.Equal("El servicio de tu empresa fue cancelado", page.Document.QuerySelector("main h1")!.Plain().Trim());
        Assert.Equal("Servicio cancelado", page.Document.QuerySelector("[data-testid=tenant-status]")!.Plain().Trim());
        var date = page.Document.QuerySelector("[data-testid=deletion-date]")!.Plain();
        Assert.Contains("Tus datos se conservan hasta el", date);
        Assert.Contains(DateTime.UtcNow.AddDays(30).Year.ToString(), date);
        Assert.DoesNotContain("Fin del contrato", page.Body);
    }
}
