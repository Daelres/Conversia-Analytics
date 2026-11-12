extern alias ApiHost;

using System.Net;
using System.Text.Json;
using AngleSharp.Html.Dom;
using Conversia.Domain.Common;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Web.IntegrationTests.Dashboard;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Admin;

/// <summary>
/// The lifecycle of a company in the administrator's pages (docs/PLATFORM_ADMIN.md, "Lifecycle of a company"): pause, resume, cancel with the
/// deletion date, reactivate, delete now with the typed name, the history from the audit trail. With a stand-in API.
/// </summary>
public class AdminLifecyclePageTests
{
    private static string Detail(Guid id) => $"/admin/empresas/{id}";

    private static IHtmlFormElement Form(PageResponse page, string testId) => page.FindForm(f => f.GetAttribute("data-testid") == testId, testId);

    private static void Cancelled(AdminStub stub, Guid id, bool started = false)
    {
        var company = stub.Companies.First(c => c.Id == id);
        company.Status = TenantStatus.Cancelled;
        company.CancelledAt = new DateTime(2026, 11, 1, 15, 0, 0, DateTimeKind.Utc);
        company.DataDeletionDueAt = new DateTime(2026, 12, 1, 15, 0, 0, DateTimeKind.Utc);
        company.DataDeletionStartedAt = started ? new DateTime(2026, 11, 2, 15, 0, 0, DateTimeKind.Utc) : null;
        company.DeletionRetentionDays = 30;
    }

    [Theory]
    [InlineData(TenantStatus.Pending, new[] { "activar", "suspender", "cancelar" })]
    [InlineData(TenantStatus.Active, new[] { "pausar", "suspender", "cancelar" })]
    [InlineData(TenantStatus.Paused, new[] { "reanudar", "suspender", "cancelar" })]
    [InlineData(TenantStatus.Suspended, new[] { "reactivar", "cancelar" })]
    [InlineData(TenantStatus.Cancelled, new[] { "reactivar" })]
    public async Task Each_status_offers_exactly_its_moves(TenantStatus status, string[] actions)
    {
        using var harness = AdminHarness.Create(stub => stub.Companies.First(c => c.Id == AdminStub.Aurora).Status = status);

        var page = (await harness.OpenAsync(Detail(AdminStub.Aurora))).ShouldBe(HttpStatusCode.OK);

        Assert.Equal(actions, page.Select("[data-testid=status-actions] a[href*='/estado/']").Select(a => a.GetAttribute("data-action")));
    }

    [Fact]
    public async Task A_cancelled_company_shows_its_deletion_date_and_offers_to_delete_now()
    {
        using var harness = AdminHarness.Create(stub => Cancelled(stub, AdminStub.Aurora));

        var page = (await harness.OpenAsync(Detail(AdminStub.Aurora))).ShouldBe(HttpStatusCode.OK);

        Assert.Equal("Cancelada", page.Document.QuerySelector("[data-testid=company-status]")!.Plain().Trim());
        Assert.Contains("1 dic 2026", page.Document.QuerySelector("[data-testid=deletion-due-at]")!.Plain());
        Assert.Equal($"{Detail(AdminStub.Aurora)}/eliminar-datos", page.Document.QuerySelector("[data-action=eliminar-datos]")!.GetAttribute("href"));
    }

    [Fact]
    public async Task Once_the_deletion_started_nothing_is_offered_and_the_page_says_so()
    {
        using var harness = AdminHarness.Create(stub => Cancelled(stub, AdminStub.Aurora, started: true));

        var page = (await harness.OpenAsync(Detail(AdminStub.Aurora))).ShouldBe(HttpStatusCode.OK);

        Assert.Empty(page.Select("[data-testid=status-actions] a"));
        Assert.Contains("ya no se puede reactivar", page.Document.QuerySelector("[data-testid=deletion-in-progress]")!.Plain());
    }

    [Fact]
    public async Task A_billing_suspension_is_shown_with_its_cause_and_the_system_as_who_did_it()
    {
        using var harness = AdminHarness.Create(stub =>
        {
            var company = stub.Companies.First(c => c.Id == AdminStub.Aurora);
            company.Status = TenantStatus.Suspended;
            company.SuspensionCause = SuspensionCause.Billing;
            var entry = AdminStub.Entry(PlatformAuditLog.Actions.TenantStatusChanged, AdminStub.Aurora, """{"from":"Active","to":"Suspended","cause":"Billing"}""");
            entry.AdminId = null;
            entry.AdminEmail = null;
            entry.ReasonCode = ReasonCodes.BillingPastDue;
            stub.Audit.Add(entry);
        });

        var page = (await harness.OpenAsync(Detail(AdminStub.Aurora))).ShouldBe(HttpStatusCode.OK);

        Assert.Contains("Pago pendiente", page.Document.QuerySelector("[data-testid=suspension-cause]")!.Plain());
        var history = page.Document.QuerySelector("[data-testid=lifecycle-card]")!.Plain();
        Assert.Contains("Activa → Suspendida (pago pendiente)", history);
        Assert.Contains("Por el sistema (pago vencido)", history);
    }

    [Fact]
    public async Task The_cancel_confirmation_says_when_the_data_would_be_deleted()
    {
        using var harness = AdminHarness.Create(stub => stub.Companies.First(c => c.Id == AdminStub.Aurora).DeletionRetentionDays = 45);

        var page = (await harness.OpenAsync($"{Detail(AdminStub.Aurora)}/estado/cancelar")).ShouldBe(HttpStatusCode.OK);

        var notice = page.Document.QuerySelector("[data-testid=deletion-date]")!.Plain();
        Assert.Contains("se eliminarán para siempre", notice);
        Assert.Contains("45 días", notice);
    }

    [Fact]
    public async Task Pausing_sends_the_paused_status_with_the_reason()
    {
        using var harness = AdminHarness.Create();
        using var session = await harness.SignInAsync();
        var confirm = (await session.GetAsync($"{Detail(AdminStub.Aurora)}/estado/pausar")).ShouldBe(HttpStatusCode.OK);

        var response = await session.SubmitAsync(FormModel.From(confirm, Form(confirm, "status-action-form")).Set("reason", "Vacaciones colectivas"));

        response.ShouldRedirectTo($"{Detail(AdminStub.Aurora)}?aviso=estado");
        using var body = JsonDocument.Parse(Assert.Single(harness.Writes).Body!);
        Assert.Equal("Paused", body.RootElement.GetProperty("status").GetString());
    }

    // ---- delete now ----

    [Fact]
    public async Task Deleting_now_needs_the_typed_name_and_a_reason_and_then_goes_back_with_the_notice()
    {
        using var harness = AdminHarness.Create(stub => Cancelled(stub, AdminStub.Aurora));
        using var session = await harness.SignInAsync();
        var page = (await session.GetAsync($"{Detail(AdminStub.Aurora)}/eliminar-datos")).ShouldBe(HttpStatusCode.OK);
        Assert.Contains("No se puede deshacer", page.Document.QuerySelector("[data-testid=delete-warning]")!.Plain());

        var missing = await session.SubmitAsync(FormModel.From(page, Form(page, "delete-data-form")));
        Assert.Contains("Escribe el nombre de la empresa", missing.Text);
        Assert.Empty(harness.Stub.DeletionRequests);

        var wrong = await session.SubmitAsync(FormModel.From(page, Form(page, "delete-data-form")).Set("ConfirmName", "Depilación Boreal").Set("Reason", "Solicitud del cliente"));
        wrong.ShouldBe(HttpStatusCode.BadRequest);
        Assert.Contains("no coincide con el nombre de la empresa", wrong.Text);

        var done = await session.SubmitAsync(FormModel.From(page, Form(page, "delete-data-form")).Set("ConfirmName", "depilación aurora").Set("Reason", "Solicitud del cliente"));
        done.ShouldRedirectTo($"{Detail(AdminStub.Aurora)}?aviso=eliminacion");
        using var body = JsonDocument.Parse(harness.Stub.DeletionRequests[^1]);
        Assert.Equal("depilación aurora", body.RootElement.GetProperty("confirmName").GetString());
        Assert.Equal("Solicitud del cliente", body.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Deleting_now_is_not_offered_for_a_company_that_is_not_cancelled()
    {
        using var harness = AdminHarness.Create();

        var page = (await harness.OpenAsync($"{Detail(AdminStub.Aurora)}/eliminar-datos")).ShouldBe(HttpStatusCode.OK);

        Assert.Contains("Cancélala primero", page.Document.QuerySelector("[data-testid=not-allowed]")!.Plain());
        Assert.Empty(page.Select("[data-testid=delete-data-form] button[type=submit]"));
    }

    [Fact]
    public async Task The_list_filters_by_the_new_statuses()
    {
        using var harness = AdminHarness.Create(stub => stub.Companies.First(c => c.Id == AdminStub.Aurora).Status = TenantStatus.Paused);

        var page = (await harness.OpenAsync("/admin?estado=en-pausa")).ShouldBe(HttpStatusCode.OK);

        Assert.Contains(harness.Backend.DataRequests, r => r.Path == "/api/admin/tenants" && r.Uri.Query.Contains("status=Paused", StringComparison.Ordinal));
        Assert.Contains("Depilación Aurora", page.Text);
    }

    [Fact]
    public async Task The_status_form_offers_the_closed_list_of_reasons_and_sends_the_chosen_code()
    {
        using var harness = AdminHarness.Create();
        using var session = await harness.SignInAsync();
        var confirm = (await session.GetAsync($"{Detail(AdminStub.Aurora)}/estado/cancelar")).ShouldBe(HttpStatusCode.OK);

        var options = confirm.Select("[data-testid=reason-code] option").Select(o => o.GetAttribute("value")).ToList();
        Assert.Equal(["", .. AdminReasonCodes.All], options);
        Assert.Contains("se guarda para siempre", confirm.Text);

        var response = await session.SubmitAsync(FormModel.From(confirm, Form(confirm, "status-action-form")).Set("ReasonCode", AdminReasonCodes.ContractEnded));

        response.ShouldRedirectTo($"{Detail(AdminStub.Aurora)}?aviso=estado");
        using var body = JsonDocument.Parse(Assert.Single(harness.Writes).Body!);
        Assert.Equal(AdminReasonCodes.ContractEnded, body.RootElement.GetProperty("reasonCode").GetString());
    }

    [Fact]
    public async Task The_history_names_the_administrators_reason_from_the_list()
    {
        using var harness = AdminHarness.Create(stub =>
        {
            var entry = AdminStub.Entry(PlatformAuditLog.Actions.TenantStatusChanged, AdminStub.Aurora, """{"from":"Active","to":"Paused"}""");
            entry.ReasonCode = AdminReasonCodes.AgreedPause;
            stub.Audit.Add(entry);
        });

        var page = (await harness.OpenAsync(Detail(AdminStub.Aurora))).ShouldBe(HttpStatusCode.OK);

        Assert.Contains($"Por {AdminStub.Email} (pausa acordada con la empresa)", page.Document.QuerySelector("[data-testid=lifecycle-card]")!.Plain());
    }
}
