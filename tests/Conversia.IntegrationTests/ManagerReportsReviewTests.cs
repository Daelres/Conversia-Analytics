using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Conversia.Application.Classification.Llm;
using Conversia.Application.DTOs.Reports;
using Conversia.Application.Messaging;
using Conversia.Application.Reports;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// The fixes of the Stage 4 review against PostgreSQL (docs/REPORTS.md): conversion over a matured window, waiting conversations now, previous-period
/// totals and branch scope on real data, the ledger of model use (previews included, no second call after a failure), leases, held deliveries, the
/// frequency in the idempotency key, skipped periods, the unsubscribe budget and the verified WhatsApp number.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class ManagerReportsReviewTests
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _anonymous;
    private readonly ReportsHost _host;

    public ManagerReportsReviewTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _anonymous = factory.CreateClient();
        _host = ReportsHostOf(factory);
    }

    private static readonly ReportPeriod LastWeek = new(new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 9));

    private async Task<(ChannelFixture Fixture, HttpClient Owner, string OwnerEmail)> StartAsync(ReportsHost? host = null)
    {
        var f = await StartChannelAsync(_factory, _anonymous, phone: $"+57 304 {Random.Shared.Next(100, 999)} {Random.Shared.Next(1000, 9999)}");
        return (f, (host ?? _host).ClientOf(f.Tenant.Owner), f.Tenant.Session.OwnerEmail);
    }

    private async Task<T> InDbAsync<T>(Func<ApplicationDbContext, Task<T>> read)
    {
        using var scope = _factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    private Task<ManagerReport> ReportAsync(Guid id) => InDbAsync(db => db.ManagerReports.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == id));

    private Task<List<ReportDelivery>> DeliveriesAsync(Guid reportId)
        => InDbAsync(db => db.ReportDeliveries.IgnoreQueryFilters().AsNoTracking().Where(d => d.ReportId == reportId).ToListAsync());

    private async Task<int> ClaimAsync(Guid deliveryId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var delivery = await db.ReportDeliveries.IgnoreQueryFilters().SingleAsync(d => d.Id == deliveryId);
        delivery.BeginAttempt(DateTime.UtcNow, DateTime.UtcNow.AddMinutes(10));
        await db.SaveChangesAsync();
        return delivery.Attempts;
    }

    private async Task<Guid> GenerateAsync(ReportsHost host, ChannelFixture f, Guid definitionId, ReportPeriod period)
        => (await host.Runner.GenerateAsync(f.TenantId, definitionId, period, DateTime.UtcNow)) ?? throw new InvalidOperationException("No report.");

    private static Action<Opportunity> Outcome(OpportunityStage stage) => o => o.SetManualOutcome(stage, stage == OpportunityStage.Lost ? LossReason.Price : null, null, DateTime.UtcNow);

    // ---- 1, 2, 6: the numbers on real data -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_weekly_report_states_the_conversion_of_its_latest_matured_window_on_real_data_and_none_below_the_sample()
    {
        var host = ReportsHostOf(_factory, name: "reports-sample2", settings: new() { ["Reports:MinConversionSample"] = "3" });
        var (f, owner, _) = await StartAsync(host);
        using var _ = f;
        var today = ReportSchedule.LocalDate(DateTime.UtcNow, ReportSchedule.Bogota);
        var period = ReportSchedule.LastCompleted(ReportFrequency.Weekly, DateTime.UtcNow, ReportSchedule.Bogota);
        var (window, previous) = ReportDataPackBuilder.MaturedWindow(ReportFrequency.Weekly, period, today, 7);
        foreach (var stage in new[] { OpportunityStage.Won, OpportunityStage.Won, OpportunityStage.Lost })
        {
            await SeedConversationAsync(_factory.Services, f, NoonOf(window.From), [In(0), Out(60)], configureOpportunity: Outcome(stage));
        }

        // The young week itself: two quick sales, which must not turn into "100 %".
        await SeedConversationAsync(_factory.Services, f, NoonOf(period.To.AddDays(-1)), [In(0), Out(60)], configureOpportunity: Outcome(OpportunityStage.Won));
        await SeedConversationAsync(_factory.Services, f, NoonOf(period.To.AddDays(-1)), [In(0), Out(60)], configureOpportunity: Outcome(OpportunityStage.Won));
        var definition = await CreateDefinitionAsync(owner, DefinitionBody(f.Tenant.Owner.UserId, sections: ["Headline"]));

        var pack = ReportDataPack.FromJson((await ReportAsync(await GenerateAsync(host, f, definition.Id, period))).DataPack);

        var headline = pack.Headline!;
        Assert.Equal((window.From, window.To.AddDays(-1)), (headline.Conversion.From, headline.Conversion.Until));
        Assert.Equal((3, 2, 66.7), (headline.Conversion.Decided, headline.Conversion.Won, headline.Conversion.Rate));
        Assert.Equal((previous.From, 0, (double?)null), (headline.PreviousConversion.From, headline.PreviousConversion.Decided, headline.PreviousConversion.Rate));
        Assert.Null(headline.ConversionChangePoints); // the window before has no sample: no change is stated

        var strict = await CreateDefinitionAsync(owner, DefinitionBody(f.Tenant.Owner.UserId, sections: ["Headline"], name: "Estricto"));
        var defaultPack = ReportDataPack.FromJson((await ReportAsync(await GenerateAsync(_host, f, strict.Id, period))).DataPack); // MinConversionSample 20
        Assert.Null(defaultPack.Headline!.Conversion.Rate);
        Assert.Contains("Sin datos suficientes", (await ReportAsync((await _host.Runner.GenerateAsync(f.TenantId, strict.Id, previous, DateTime.UtcNow))!.Value)).Text);
    }

    [Fact]
    public async Task The_waiting_conversations_are_the_open_ones_now_even_when_they_started_before_the_period()
    {
        var (f, owner, _) = await StartAsync();
        using var _ = f;
        await SeedConversationAsync(_factory.Services, f, DateTime.UtcNow.AddDays(-20), [In(0)]); // before any recent period, still waiting
        var definition = await CreateDefinitionAsync(owner, DefinitionBody(f.Tenant.Owner.UserId, sections: ["Response"]));

        var report = await ReportAsync(await GenerateAsync(_host, f, definition.Id, LastWeek));

        var response = ReportDataPack.FromJson(report.DataPack).Response!;
        Assert.Equal((1, 1), (response.OpenUnansweredNow, response.OpenOverSlaNow));
        Assert.Contains("Responder hoy la conversación abierta que espera respuesta.", report.Text);
    }

    [Fact]
    public async Task The_previous_period_totals_and_a_single_branch_scope_come_from_the_real_metrics()
    {
        var (f, owner, _) = await StartAsync();
        using var _ = f;
        var other = await CreateBranchAsync(f.Tenant.Owner.Client, "Norte");
        var before = new ReportPeriod(LastWeek.From.AddDays(-7), LastWeek.From);
        await SeedConversationAsync(_factory.Services, f, NoonOf(before.From.AddDays(1)), [In(0), Out(60)]);
        await SeedConversationAsync(_factory.Services, f, NoonOf(before.From.AddDays(2)), [In(0), Out(60)]);
        await SeedConversationAsync(_factory.Services, f.TenantId, other.Id, f.Channel.Id, NoonOf(before.From.AddDays(1)), [In(0)]);
        await SeedConversationAsync(_factory.Services, f, NoonOf(LastWeek.From.AddDays(1)), [In(0), Out(60)]);
        for (var i = 0; i < 3; i++)
        {
            await SeedConversationAsync(_factory.Services, f.TenantId, other.Id, f.Channel.Id, NoonOf(LastWeek.From.AddDays(2)), [In(0)]);
        }

        var definition = await CreateDefinitionAsync(owner, DefinitionBody(f.Tenant.Owner.UserId, sections: ["Headline"], branches: [f.Branch.Id]));

        var pack = ReportDataPack.FromJson((await ReportAsync(await GenerateAsync(_host, f, definition.Id, LastWeek))).DataPack);

        Assert.Equal((1, 2), (pack.Headline!.Conversations, pack.Headline.PreviousConversations));
        Assert.Equal(f.Branch.Name.StartsWith("Sucursal", StringComparison.OrdinalIgnoreCase) ? f.Branch.Name : "Sucursal " + f.Branch.Name, pack.Scope);
    }

    // ---- 7, 8: the ledger of model use -------------------------------------------------------------------------------------------------

    private static LlmCompletion Grounded() => new(LlmStopReason.EndTurn, new JsonObject
    {
        ["title"] = "Resumen semanal",
        ["indicators"] = new JsonArray(new JsonObject { ["label"] = "Ritmo", ["value"] = "Estable", ["note"] = "El equipo mantuvo la atención." }),
        ["diagnosis"] = "La semana fue tranquila y sin alertas.",
        ["actions"] = new JsonArray("Revisar las conversaciones abiertas cada mañana."),
        ["coverage_note"] = null,
        ["mentioned_names"] = new JsonArray(),
    }.ToJsonString(), new LlmUsage(3000, 600, 0, 0), "claude-haiku-5-5");

    [Fact]
    public async Task Previews_count_toward_the_month_and_past_the_daily_budget_the_template_writes_them()
    {
        var host = ReportsHostOf(_factory, withModel: true, name: "reports-budget",
            settings: new() { ["Reports:Ai:MonthlyReportsPerCompany"] = "1", ["Reports:PreviewsPerCompanyPerDay"] = "1", ["Reports:PreviewsPerHour"] = "100" });
        var (f, owner, _) = await StartAsync(host);
        using var _ = f;
        await SetTenantFlagsAsync(_factory.Services, f.TenantId, aiEnabled: true);
        host.Llm!.Default = Grounded;
        var definition = await CreateDefinitionAsync(owner, DefinitionBody(f.Tenant.Owner.UserId));

        var first = await (await owner.PostAsync($"{ReportsUrl}/definitions/{definition.Id}/preview", null)).Content.ReadFromJsonAsync<ReportPreviewResponse>();
        var second = await (await owner.PostAsync($"{ReportsUrl}/definitions/{definition.Id}/preview", null)).Content.ReadFromJsonAsync<ReportPreviewResponse>();

        Assert.Equal(("Llm", "Template", "preview_budget"), (first!.Generator, second!.Generator, second.FallbackReason));
        var scheduled = await ReportAsync(await GenerateAsync(host, f, definition.Id, LastWeek));
        Assert.Equal((ReportGenerator.Template, "monthly_limit"), (scheduled.Generator, scheduled.FallbackReason)); // the preview used the month's model report
        var ledger = await InDbAsync(db => db.ReportLedgerEntries.IgnoreQueryFilters().Where(e => e.TenantId == f.TenantId).ToListAsync());
        Assert.Equal(2, ledger.Count(e => e.Kind == ReportCompositionKind.Preview));
        Assert.True(ledger.Single(e => e.LlmAttempts > 0).EstimatedUsd > 0m);
    }

    [Fact]
    public async Task A_period_whose_model_was_already_called_is_written_by_the_template_and_the_model_is_not_called_again()
    {
        var host = ReportsHostOf(_factory, withModel: true);
        var (f, owner, _) = await StartAsync(host);
        using var _ = f;
        await SetTenantFlagsAsync(_factory.Services, f.TenantId, aiEnabled: true);
        var definition = await CreateDefinitionAsync(owner, DefinitionBody(f.Tenant.Owner.UserId));
        using (var scope = _factory.Services.CreateScope())
        {
            // What a failed run leaves: the call is in the ledger, the report is not stored.
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.ReportLedgerEntries.Add(ReportLedgerEntry.Record(f.TenantId, definition.Id, ReportCompositionKind.Scheduled, ReportFrequency.Weekly, LastWeek.From,
                ReportGenerator.Llm, null, 1, 3000, 600, 0.0006m, DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        var calls = host.Llm!.Requests.Count;
        var report = await ReportAsync(await GenerateAsync(host, f, definition.Id, LastWeek));

        Assert.Equal((ReportGenerator.Template, "model_already_called"), (report.Generator, report.FallbackReason));
        Assert.Equal(calls, host.Llm.Requests.Count);
    }

    // ---- 9, 10: leases and held deliveries ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_delivery_claimed_again_by_another_instance_after_its_lease_is_not_sent_by_the_first()
    {
        var (f, owner, ownerEmail) = await StartAsync();
        using var _ = f;
        var definition = await CreateDefinitionAsync(owner, DefinitionBody(f.Tenant.Owner.UserId));
        var delivery = Assert.Single(await DeliveriesAsync(await GenerateAsync(_host, f, definition.Id, LastWeek)));

        var first = await ClaimAsync(delivery.Id);
        var second = await ClaimAsync(delivery.Id); // the lease expired and another instance took it
        await _host.Runner.DeliverAsync(delivery.Id, first);

        Assert.Empty(_host.Email.SentTo(ownerEmail));
        await _host.Runner.DeliverAsync(delivery.Id, second);
        Assert.Single(_host.Email.SentTo(ownerEmail));
        Assert.StartsWith($"report-delivery-{delivery.Id:N}", _host.Email.SentTo(ownerEmail)[0].MessageIdLocalPart);
    }

    [Fact]
    public async Task A_company_that_stopped_operating_holds_its_deliveries_without_spending_attempts()
    {
        var (f, owner, ownerEmail) = await StartAsync();
        using var _ = f;
        var definition = await CreateDefinitionAsync(owner, DefinitionBody(f.Tenant.Owner.UserId));
        var delivery = Assert.Single(await DeliveriesAsync(await GenerateAsync(_host, f, definition.Id, LastWeek)));
        await InDbAsync(db => db.Database.ExecuteSqlAsync($"""UPDATE "Tenants" SET "Status" = 'Paused' WHERE "Id" = {f.TenantId}"""));

        await _host.Runner.DeliverAsync(delivery.Id, await ClaimAsync(delivery.Id));

        var held = Assert.Single(await DeliveriesAsync(delivery.ReportId));
        Assert.Equal((ReportDeliveryStatus.Pending, 0, "company_inactive"), (held.Status, held.Attempts, held.ErrorCode));
        Assert.True(held.NextAttemptAt > DateTime.UtcNow.AddMinutes(30));
        Assert.Empty(_host.Email.SentTo(ownerEmail));
        await InDbAsync(db => db.Database.ExecuteSqlAsync($"""UPDATE "Tenants" SET "Status" = 'Active' WHERE "Id" = {f.TenantId}"""));
    }

    [Fact]
    public async Task Without_an_email_provider_the_delivery_waits_for_one_instead_of_failing()
    {
        var (f, owner, ownerEmail) = await StartAsync();
        using var _ = f;
        _host.Email.Answer(ownerEmail, () => new EmailSendResult(EmailSendStatus.NotConfigured, null, "email_not_configured"));
        var definition = await CreateDefinitionAsync(owner, DefinitionBody(f.Tenant.Owner.UserId));
        var delivery = Assert.Single(await DeliveriesAsync(await GenerateAsync(_host, f, definition.Id, LastWeek)));

        await _host.Runner.DeliverAsync(delivery.Id, await ClaimAsync(delivery.Id));

        var held = Assert.Single(await DeliveriesAsync(delivery.ReportId));
        Assert.Equal((ReportDeliveryStatus.Pending, 0, "email_not_configured"), (held.Status, held.Attempts, held.ErrorCode));
    }

    [Fact]
    public async Task A_whatsapp_delivery_follows_the_recipients_current_choice()
    {
        var (f, owner, _) = await StartAsync();
        using var _ = f;
        await SetCredentialsAsync(owner, f.Channel.Id);
        await SeedTemplateAsync(f);
        await OptInAndVerifyAsync(f.Tenant.Owner, f.Channel.Id);
        var body = DefinitionBody(f.Tenant.Owner.UserId, email: true, whatsApp: true, channelId: f.Channel.Id, template: "resumen_gerente", language: "es");
        var definition = await CreateDefinitionAsync(owner, body);
        var reportId = await GenerateAsync(_host, f, definition.Id, LastWeek);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"{ReportsUrl}/definitions/{definition.Id}", DefinitionBody(f.Tenant.Owner.UserId))).StatusCode);
        var whatsApp = (await DeliveriesAsync(reportId)).Single(d => d.Channel == ReportDeliveryChannel.WhatsApp);
        var before = _host.WhatsApp.TemplatesSentBy(f.Channel.Id).Count;

        await _host.Runner.DeliverAsync(whatsApp.Id, await ClaimAsync(whatsApp.Id));

        var failed = (await DeliveriesAsync(reportId)).Single(d => d.Channel == ReportDeliveryChannel.WhatsApp);
        Assert.Equal((ReportDeliveryStatus.Failed, "channel_removed"), (failed.Status, failed.ErrorCode));
        Assert.Equal(before, _host.WhatsApp.TemplatesSentBy(f.Channel.Id).Count);
    }

    // ---- 11: the period key and skipped periods ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_change_of_frequency_never_makes_a_period_look_done_and_a_period_missed_by_an_outage_is_reported_as_skipped()
    {
        var (f, owner, _) = await StartAsync();
        using var _ = f;
        var definition = await CreateDefinitionAsync(owner, DefinitionBody(f.Tenant.Owner.UserId));
        await GenerateAsync(_host, f, definition.Id, LastWeek);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"{ReportsUrl}/definitions/{definition.Id}", DefinitionBody(f.Tenant.Owner.UserId, frequency: "Daily"))).StatusCode);

        Assert.NotNull(await _host.Runner.GenerateAsync(f.TenantId, definition.Id, new ReportPeriod(LastWeek.From, LastWeek.From.AddDays(1)), DateTime.UtcNow));

        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"{ReportsUrl}/definitions/{definition.Id}", DefinitionBody(f.Tenant.Owner.UserId))).StatusCode);
        await BackdateDefinitionAsync(_factory.Services, definition.Id);
        var (period, runAt) = CurrentWeeklyOccurrence();
        var run = await _host.Runner.GenerateDueAsync(runAt.AddHours(30));
        Assert.Contains((definition.Id, period.From), run.Skipped);
    }

    // ---- 12: the unsubscribe budget -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Invalid_tokens_exhaust_only_their_own_budget_never_a_valid_links()
    {
        var host = ReportsHostOf(_factory, name: "reports-unsubscribe", settings: new() { ["Reports:Unsubscribe:InvalidPerMinute"] = "2" });
        var (f, owner, _) = await StartAsync(host);
        using var _ = f;
        var definition = await CreateDefinitionAsync(owner, DefinitionBody(f.Tenant.Owner.UserId));
        var recipient = await InDbAsync(db => db.ReportRecipients.IgnoreQueryFilters().Where(r => r.DefinitionId == definition.Id).Select(r => r.Id).SingleAsync());
        var token = ReportUnsubscribeToken.Create(recipient, host.Host.Services.GetRequiredService<ReportUnsubscribeKey>().Key);
        using var anonymous = host.Host.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            statuses.Add((await anonymous.PostAsync($"{ReportsUrl}/unsubscribe/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA{i}", null)).StatusCode);
        }

        Assert.Equal([HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], statuses);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsync($"{ReportsUrl}/unsubscribe/{token}", null)).StatusCode);
    }

    // ---- 13: the verified WhatsApp number --------------------------------------------------------------------------------------------------

    private async Task SeedTemplateAsync(ChannelFixture f)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.MessageTemplates.Add(MessageTemplate.Create(f.TenantId, f.Channel.Id, "resumen_gerente", "es", TemplateCategory.Utility, TemplateStatus.Approved,
            "Tu resumen de {{1}}: {{2}}. Míralo en {{3}}", true, DateTime.UtcNow));
        await db.SaveChangesAsync();
    }

    private async Task<(string Digits, ReportWhatsAppOptInResponse Response)> OptInAsync(TestActor actor)
    {
        var number = "+57 312 " + Random.Shared.Next(100, 999) + " " + Random.Shared.Next(1000, 9999);
        var response = await _host.ClientOf(actor).PutAsJsonAsync($"{ReportsUrl}/whatsapp-opt-in", new { phoneNumber = number, consent = true });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (new string([.. number.Where(char.IsAsciiDigit)]), (await response.Content.ReadFromJsonAsync<ReportWhatsAppOptInResponse>())!);
    }

    private async Task OptInAndVerifyAsync(TestActor actor, Guid channelId)
    {
        var (digits, _) = await OptInAsync(actor);
        var code = _host.WhatsApp.TemplatesSentBy(channelId).Last(t => t.RecipientWaId == digits).Parameters[1].Value[^6..];
        Assert.Equal(HttpStatusCode.OK, (await _host.ClientOf(actor).PostAsJsonAsync($"{ReportsUrl}/whatsapp-opt-in/verify", new { code })).StatusCode);
    }

    [Fact]
    public async Task The_number_receives_a_code_through_the_company_template_and_only_a_verified_number_gets_reports()
    {
        var (f, owner, _) = await StartAsync();
        using var _ = f;
        await SetCredentialsAsync(owner, f.Channel.Id);
        await SeedTemplateAsync(f);

        var (digits, accepted) = await OptInAsync(f.Tenant.Owner);

        Assert.Equal((true, false, true), (accepted.OptedIn, accepted.Verified, accepted.CodeSent));
        var sent = _host.WhatsApp.TemplatesSentBy(f.Channel.Id).Last(t => t.RecipientWaId == digits);
        Assert.Equal("resumen_gerente", sent.Name);
        Assert.Matches("^tu código es \\d{6}$", sent.Parameters[1].Value);
        var definition = await CreateDefinitionAsync(owner, DefinitionBody(f.Tenant.Owner.UserId, email: false, whatsApp: true, channelId: f.Channel.Id, template: "resumen_gerente", language: "es"));
        var unverified = Assert.Single(await DeliveriesAsync(await GenerateAsync(_host, f, definition.Id, LastWeek)));
        Assert.Equal((ReportDeliveryStatus.Skipped, "no_opt_in"), (unverified.Status, unverified.ErrorCode));

        var wrong = await owner.PostAsJsonAsync($"{ReportsUrl}/whatsapp-opt-in/verify", new { code = sent.Parameters[1].Value[^6..] == "000000" ? "111111" : "000000" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var right = await owner.PostAsJsonAsync($"{ReportsUrl}/whatsapp-opt-in/verify", new { code = sent.Parameters[1].Value[^6..] });
        Assert.True((await right.Content.ReadFromJsonAsync<ReportWhatsAppOptInResponse>())!.Verified);
        var verified = Assert.Single(await DeliveriesAsync(await GenerateAsync(_host, f, definition.Id, new ReportPeriod(LastWeek.To, LastWeek.To.AddDays(7)))));
        Assert.Equal(ReportDeliveryStatus.Pending, verified.Status);
    }

    [Fact]
    public async Task Without_a_report_template_no_code_can_be_sent_and_the_number_stays_unverified()
    {
        var (f, _, _) = await StartAsync();
        using var _ = f;

        var (_, accepted) = await OptInAsync(f.Tenant.Owner);

        Assert.Equal((true, false, false), (accepted.OptedIn, accepted.Verified, accepted.CodeSent));
    }

    [Fact]
    public async Task A_company_in_sandbox_mode_sends_the_code_through_the_internal_sender()
    {
        var (f, _, _) = await StartAsync();
        using var _ = f;
        await SeedTemplateAsync(f);
        await SetTenantFlagsAsync(_factory.Services, f.TenantId, sandbox: true);

        var (_, accepted) = await OptInAsync(f.Tenant.Owner);

        Assert.True(accepted.CodeSent);
        Assert.False(accepted.Verified);
        Assert.Empty(_host.WhatsApp.TemplatesSentBy(f.Channel.Id)); // nothing reached the provider
    }
}
