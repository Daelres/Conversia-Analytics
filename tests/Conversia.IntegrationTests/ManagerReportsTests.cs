using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Conversia.Application.Classification.Llm;
using Conversia.Application.DTOs;
using Conversia.Application.DTOs.Admin;
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
/// The automatic manager reports against PostgreSQL (docs/REPORTS.md): definitions and permissions, generation from the metrics (idempotent per
/// period, no customer data), email and WhatsApp delivery with retries and statuses, unsubscribe, opt-in, preview, retention and the
/// administrator's monitoring. Email, WhatsApp and the model are stand-ins: nothing here reaches a provider.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class ManagerReportsTests
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _anonymous;
    private readonly ReportsHost _host;

    public ManagerReportsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _anonymous = factory.CreateClient();
        _host = ReportsHostOf(factory);
    }

    private async Task<(ChannelFixture Fixture, HttpClient Owner, string OwnerEmail)> StartAsync(ReportsHost? host = null)
    {
        var f = await StartChannelAsync(_factory, _anonymous, phone: $"+57 301 {Random.Shared.Next(100, 999)} {Random.Shared.Next(1000, 9999)}");
        return (f, (host ?? _host).ClientOf(f.Tenant.Owner), f.Tenant.Session.OwnerEmail);
    }

    private async Task<ManagerReport> LoadReportAsync(Guid reportId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().ManagerReports.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.Id == reportId);
    }

    private async Task<List<ReportDelivery>> DeliveriesAsync(Guid reportId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().ReportDeliveries.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.ReportId == reportId).OrderBy(d => d.Channel).ToListAsync();
    }

    private async Task DeliverAllAsync(Guid reportId, DateTime at)
    {
        foreach (var delivery in (await DeliveriesAsync(reportId)).Where(d => d.Status == ReportDeliveryStatus.Pending))
        {
            await _host.Runner.DeliverAsync(delivery.Id, await ClaimAsync(delivery.Id, at));
        }
    }

    // What the claim does for one delivery (the job claims batches across companies; a test takes only its own). Returns the attempt claimed.
    private async Task<int> ClaimAsync(Guid deliveryId, DateTime at)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var delivery = await db.ReportDeliveries.IgnoreQueryFilters().SingleAsync(d => d.Id == deliveryId);
        delivery.BeginAttempt(at, at.AddMinutes(10));
        await db.SaveChangesAsync();
        return delivery.Attempts;
    }

    private async Task<Guid> GenerateAsync(ChannelFixture f, Guid definitionId, ReportPeriod period, ReportsHost? host = null)
        => (await (host ?? _host).Runner.GenerateAsync(f.TenantId, definitionId, period, DateTime.UtcNow)) ?? throw new InvalidOperationException("No report was generated.");

    private static readonly ReportPeriod LastWeek = new(new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 9));

    // ---- Definitions and permissions ------------------------------------------------------------------------------

    [Fact]
    public async Task The_owner_creates_reads_updates_and_deletes_a_definition()
    {
        var (f, owner, ownerEmail) = await StartAsync();
        using var _ = f;

        var created = await CreateDefinitionAsync(owner, DefinitionBody(f.Tenant.Owner.UserId, branches: [f.Branch.Id]));
        Assert.Equal(("Resumen semanal", "Weekly", 7, 1), (created.Name, created.Frequency, created.SendHour, created.SendDayOfWeek));
        Assert.Equal([f.Branch.Id], created.BranchIds);
        var recipient = Assert.Single(created.Recipients);
        Assert.Equal((ownerEmail, true, false), (recipient.Email, recipient.ByEmail, recipient.ByWhatsApp));
        Assert.True(created.NextSendAt > DateTime.UtcNow);
        Assert.Contains("semana del", created.NextPeriod.Label);

        var updated = await owner.PutAsJsonAsync($"{ReportsUrl}/definitions/{created.Id}", DefinitionBody(f.Tenant.Owner.UserId, frequency: "Monthly", sections: ["Response"], name: "Mensual"));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var read = await owner.GetFromJsonAsync<ReportDefinitionResponse>($"{ReportsUrl}/definitions/{created.Id}");
        Assert.Equal(("Mensual", "Monthly"), (read!.Name, read.Frequency));
        Assert.Equal(["Response"], read.Sections);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"{ReportsUrl}/definitions/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"{ReportsUrl}/definitions/{created.Id}")).StatusCode);
    }

    [Fact]
    public async Task Managing_reports_needs_its_permission_and_another_companys_definition_is_not_found()
    {
        var (f, owner, _) = await StartAsync();
        using var __ = f;
        var definition = await CreateDefinitionAsync(owner, DefinitionBody(f.Tenant.Owner.UserId));

        using var viewer = await f.Tenant.AddMemberAsync("viewer", Permission.MetricsView);
        var viewerClient = _host.ClientOf(viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewerClient.GetAsync($"{ReportsUrl}/definitions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewerClient.PostAsJsonAsync($"{ReportsUrl}/definitions", DefinitionBody(viewer.UserId))).StatusCode);

        using var manager = await f.Tenant.AddMemberAsync("manager", Permission.ReportsManage);
        var managerClient = _host.ClientOf(manager);
        Assert.Equal(HttpStatusCode.OK, (await managerClient.GetAsync($"{ReportsUrl}/definitions/{definition.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await managerClient.GetAsync(ReportsUrl)).StatusCode); // the history is MetricsView's

        var (other, otherOwner, _) = await StartAsync();
        using var ___ = other;
        Assert.Equal(HttpStatusCode.NotFound, (await otherOwner.GetAsync($"{ReportsUrl}/definitions/{definition.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherOwner.DeleteAsync($"{ReportsUrl}/definitions/{definition.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_definition_is_validated_in_spanish()
    {
        var (f, owner, _) = await StartAsync();
        using var _ = f;
        var (other, _, _) = await StartAsync();
        using var __ = other;

        async Task<string> RefusedAsync(object body)
        {
            var response = await owner.PostAsJsonAsync($"{ReportsUrl}/definitions", body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            return await response.Content.ReadAsStringAsync();
        }

        Assert.Contains("no pertenece a la empresa", await RefusedAsync(DefinitionBody(other.Tenant.Owner.UserId)));
        Assert.Contains("sucursales elegidas no pertenece", await RefusedAsync(DefinitionBody(f.Tenant.Owner.UserId, branches: [other.Branch.Id])));
        Assert.Contains("al menos una parte", await RefusedAsync(DefinitionBody(f.Tenant.Owner.UserId, sections: [])));
        Assert.Contains("partes del análisis elegidas no existe", await RefusedAsync(DefinitionBody(f.Tenant.Owner.UserId, sections: ["Secret"])));
        Assert.Contains("por WhatsApp elige el número", await RefusedAsync(DefinitionBody(f.Tenant.Owner.UserId, whatsApp: true)));
        Assert.Contains("plantilla elegida no está disponible", await RefusedAsync(DefinitionBody(f.Tenant.Owner.UserId, whatsApp: true, channelId: f.Channel.Id, template: "no_existe", language: "es")));
        Assert.Contains("hora de envío", await RefusedAsync(DefinitionBody(f.Tenant.Owner.UserId, hour: 24)));
    }

    [Fact]
    public async Task A_company_has_a_limited_number_of_definitions()
    {
        var limited = ReportsHostOf(_factory, name: "reports-limit", settings: new() { ["Reports:MaxDefinitionsPerCompany"] = "2" });
        var (f, owner, _) = await StartAsync(limited);
        using var _ = f;

        await CreateDefinitionAsync(owner, DefinitionBody(f.Tenant.Owner.UserId, name: "Uno"));
        await CreateDefinitionAsync(owner, DefinitionBody(f.Tenant.Owner.UserId, name: "Dos"));
        var third = await owner.PostAsJsonAsync($"{ReportsUrl}/definitions", DefinitionBody(f.Tenant.Owner.UserId, name: "Tres"));

        Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);
        Assert.Equal("report_limit", (await ReadProblemCodeAsync(third)).Code);
        var options = await owner.GetFromJsonAsync<ReportOptionsResponse>($"{ReportsUrl}/options");
        Assert.Equal((2, 2), (options!.Definitions, options.MaxDefinitions));
    }

    // ---- Generation -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_job_generates_a_due_report_once_per_period_and_never_before_its_hour()
    {
        var (f, owner, _) = await StartAsync();
        using var _ = f;
        var (period, runAt) = CurrentWeeklyOccurrence();
        await SeedConversationAsync(_factory.Services, f, NoonOf(period.From.AddDays(1)), [In(0), Out(120)]);
        var definition = await CreateDefinitionAsync(owner, DefinitionBody(f.Tenant.Owner.UserId));
        await BackdateDefinitionAsync(_factory.Services, definition.Id);

        await _host.Runner.GenerateDueAsync(runAt.AddMinutes(-2)); // a minute before the hour
        Assert.Empty((await owner.GetFromJsonAsync<PagedResponse<ReportSummaryResponse>>(ReportsUrl))!.Items);

        await _host.Runner.GenerateDueAsync(runAt);
        await _host.Runner.GenerateDueAsync(runAt.AddMinutes(5)); // a second run (or instance) finds it there

        var report = Assert.Single((await owner.GetFromJsonAsync<PagedResponse<ReportSummaryResponse>>(ReportsUrl))!.Items);
        Assert.Equal((period.From, period.To), (report.Period.From, report.Period.To));
        Assert.Equal("Template", report.Generator);
        Assert.Equal(new ReportDeliveryCounts(0, 0, 1, 0), report.Deliveries);
        Assert.Null(await _host.Runner.GenerateAsync(f.TenantId, definition.Id, period, DateTime.UtcNow));
    }

    [Fact]
    public async Task A_paused_definition_or_a_company_that_does_not_operate_gets_no_report()
    {
        var (f, owner, _) = await StartAsync();
        using var _ = f;
        var (period, runAt) = CurrentWeeklyOccurrence();
        var paused = await CreateDefinitionAsync(owner, DefinitionBody(f.Tenant.Owner.UserId) is var body ? Paused(body) : null!);
        await BackdateDefinitionAsync(_factory.Services, paused.Id);

        await _host.Runner.GenerateDueAsync(runAt);

        Assert.Empty((await owner.GetFromJsonAsync<PagedResponse<ReportSummaryResponse>>(ReportsUrl))!.Items);

        static object Paused(object definitionBody)
        {
            var node = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(definitionBody))!;
            node["isActive"] = false;
            return node;
        }
    }

    [Fact]
    public async Task The_stored_report_holds_aggregates_only_never_a_customers_name_number_or_message()
    {
        var (f, owner, _) = await StartAsync();
        using var _ = f;
        var waId = UniqueWaId();
        var seeded = await SeedConversationAsync(_factory.Services, f, NoonOf(LastWeek.From.AddDays(2)),
            [In(0, "Hola, mi cédula es 1098765432 y vivo en la calle secreta"), Out(60, "Hola Zuleima, con gusto")], waId: waId,
            configureOpportunity: o => o.SetManualOutcome(OpportunityStage.Lost, LossReason.Price, null, DateTime.UtcNow));
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database
                .ExecuteSqlAsync($"""UPDATE "Contacts" SET "DisplayName" = 'Zuleima Secreta' WHERE "Id" = {seeded.ContactId}""");
        }

        var definition = await CreateDefinitionAsync(owner, DefinitionBody(f.Tenant.Owner.UserId));
        var report = await LoadReportAsync(await GenerateAsync(f, definition.Id, LastWeek));

        foreach (var stored in new[] { report.DataPack, report.Content, report.Html, report.Text, report.Headline })
        {
            foreach (var secret in new[] { "Zuleima", waId, "cédula", "1098765432", "secreta" })
            {
                Assert.DoesNotContain(secret, stored, StringComparison.OrdinalIgnoreCase);
            }
        }

        var pack = ReportDataPack.FromJson(report.DataPack);
        Assert.Equal(1, pack.Headline!.Conversations);
        Assert.Equal(1, pack.Losses!.TotalLost);
        Assert.True(ReportGroundingValidator.Validate(ReportContent.FromJson(report.Content), pack).IsValid);
        Assert.Equal((ReportGenerator.Template, "ai_not_configured"), (report.Generator, report.FallbackReason));
    }

    [Fact]
    public async Task A_branch_scope_counts_only_the_chosen_branches()
    {
        var (f, owner, _) = await StartAsync();
        using var _ = f;
        var other = await CreateBranchAsync(f.Tenant.Owner.Client, "Sucursal Norte");
        await SeedConversationAsync(_factory.Services, f, NoonOf(LastWeek.From.AddDays(1)), [In(0), Out(60)]);
        await SeedConversationAsync(_factory.Services, f.TenantId, other.Id, f.Channel.Id, NoonOf(LastWeek.From.AddDays(1)), [In(0), Out(60)]);
        await SeedConversationAsync(_factory.Services, f.TenantId, other.Id, f.Channel.Id, NoonOf(LastWeek.From.AddDays(2)), [In(0)]);

        var all = await CreateDefinitionAsync(owner, DefinitionBody(f.Tenant.Owner.UserId, name: "Todas"));
        var north = await CreateDefinitionAsync(owner, DefinitionBody(f.Tenant.Owner.UserId, name: "Norte", branches: [other.Id]));

        Assert.Equal(3, ReportDataPack.FromJson((await LoadReportAsync(await GenerateAsync(f, all.Id, LastWeek))).DataPack).Headline!.Conversations);
        var scoped = ReportDataPack.FromJson((await LoadReportAsync(await GenerateAsync(f, north.Id, LastWeek))).DataPack);
        Assert.Equal(2, scoped.Headline!.Conversations);
        Assert.Equal("Sucursal Norte", scoped.Scope); // the name already says it is a branch
    }

    // ---- The model --------------------------------------------------------------------------------------------------

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
    public async Task With_the_model_on_and_the_ai_switched_on_the_model_writes_it_and_its_cost_is_kept_apart_from_the_conversation_quota()
    {
        var host = ReportsHostOf(_factory, withModel: true);
        var (f, owner, _) = await StartAsync(host);
        using var _ = f;
        await SetTenantFlagsAsync(_factory.Services, f.TenantId, aiEnabled: true);
        host.Llm!.Enqueue(Grounded);
        var definition = await CreateDefinitionAsync(owner, DefinitionBody(f.Tenant.Owner.UserId));

        var report = await LoadReportAsync(await GenerateAsync(f, definition.Id, LastWeek, host));

        Assert.Equal((ReportGenerator.Llm, null, 1, "claude-haiku-5-5"), (report.Generator, report.FallbackReason, report.LlmAttempts, report.Model));
        Assert.Equal(LlmPricing.Default.Estimate(new LlmUsage(3000, 600, 0, 0)), report.EstimatedUsd);
        Assert.Contains("La semana fue tranquila", report.Text);
        Assert.Contains(host.Llm.Requests, r => r.UserContent.Contains("<data_pack>", StringComparison.Ordinal));
        using var scope = _factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().AiUsageMonths.IgnoreQueryFilters().AnyAsync(u => u.TenantId == f.TenantId));
    }

    [Fact]
    public async Task With_the_ai_switched_off_or_an_ungrounded_answer_twice_the_template_writes_it()
    {
        var host = ReportsHostOf(_factory, withModel: true);
        var (f, owner, _) = await StartAsync(host);
        using var _ = f;
        var definition = await CreateDefinitionAsync(owner, DefinitionBody(f.Tenant.Owner.UserId));

        var off = await LoadReportAsync(await GenerateAsync(f, definition.Id, LastWeek, host));
        Assert.Equal((ReportGenerator.Template, "ai_disabled"), (off.Generator, off.FallbackReason));

        await SetTenantFlagsAsync(_factory.Services, f.TenantId, aiEnabled: true);
        var invented = Grounded() with { JsonText = Grounded().JsonText!.Replace("La semana fue tranquila", "Se cerraron 987 ventas", StringComparison.Ordinal) };
        host.Llm!.Enqueue(() => invented);
        host.Llm.Enqueue(() => invented);
        var rejected = await LoadReportAsync(await GenerateAsync(f, definition.Id, new ReportPeriod(LastWeek.From.AddDays(-7), LastWeek.From), host));
        Assert.Equal((ReportGenerator.Template, "invalid_output", 2), (rejected.Generator, rejected.FallbackReason, rejected.LlmAttempts));
        Assert.DoesNotContain("987", rejected.Text);
        Assert.True(rejected.EstimatedUsd > 0m);
    }

    // ---- Email delivery ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_email_goes_to_the_users_address_with_a_one_click_unsubscribe_and_the_status_is_kept()
    {
        var (f, owner, ownerEmail) = await StartAsync();
        using var _ = f;
        var definition = await CreateDefinitionAsync(owner, DefinitionBody(f.Tenant.Owner.UserId));
        var reportId = await GenerateAsync(f, definition.Id, LastWeek);

        await DeliverAllAsync(reportId, DateTime.UtcNow);

        var email = Assert.Single(_host.Email.SentTo(ownerEmail));
        Assert.Contains($"https://app.conversia.test/informes/{reportId}", email.HtmlBody);
        Assert.Contains($"https://app.conversia.test/informes/{reportId}", email.TextBody);
        Assert.StartsWith("<https://app.conversia.test/informes/baja/un-clic?t=", email.Headers["List-Unsubscribe"]);
        Assert.Equal("List-Unsubscribe=One-Click", email.Headers["List-Unsubscribe-Post"]);
        Assert.DoesNotContain("<img", email.HtmlBody, StringComparison.OrdinalIgnoreCase);
        var delivery = Assert.Single(await DeliveriesAsync(reportId));
        Assert.Equal((ReportDeliveryStatus.Sent, 1), (delivery.Status, delivery.Attempts));

        var read = await owner.GetFromJsonAsync<ReportResponse>($"{ReportsUrl}/{reportId}");
        var row = Assert.Single(read!.Deliveries);
        Assert.Equal((ownerEmail, "Email", "Sent"), (row.Email, row.Channel, row.Status));
        Assert.Equal(read.Content.Title, ReportContent.FromJson((await LoadReportAsync(reportId)).Content).Title);
    }

    [Fact]
    public async Task A_temporary_failure_is_retried_with_backoff_until_the_last_attempt_and_a_permanent_one_fails_at_once()
    {
        var (f, owner, ownerEmail) = await StartAsync();
        using var _ = f;
        using var member = await f.Tenant.AddMemberAsync("rechazado");
        var memberEmail = await MemberEmailAsync(member.UserId);
        var definition = await CreateDefinitionAsync(owner, DefinitionBody(f.Tenant.Owner.UserId, more: member.UserId));
        _host.Email.Answer(ownerEmail, () => new EmailSendResult(EmailSendStatus.TransientFailure, null, "smtp_unavailable"));
        _host.Email.Answer(memberEmail, () => new EmailSendResult(EmailSendStatus.PermanentFailure, null, "recipient_rejected"));
        var reportId = await GenerateAsync(f, definition.Id, LastWeek);
        var now = DateTime.UtcNow;

        await DeliverAllAsync(reportId, now);

        var deliveries = await DeliveriesAsync(reportId);
        var owners = deliveries.Single(d => d.UserId == f.Tenant.Owner.UserId);
        Assert.Equal((ReportDeliveryStatus.Pending, "smtp_unavailable", 1), (owners.Status, owners.ErrorCode, owners.Attempts));
        Assert.Equal(DateTime.UtcNow.AddMinutes(1), owners.NextAttemptAt!.Value, TimeSpan.FromSeconds(20)); // the backoff runs from a fresh clock
        var members = deliveries.Single(d => d.UserId == member.UserId);
        Assert.Equal((ReportDeliveryStatus.Failed, "recipient_rejected"), (members.Status, members.ErrorCode));

        for (var i = 0; i < 4; i++)
        {
            now = now.AddHours(7);
            await DeliverAllAsync(reportId, now);
        }

        var last = (await DeliveriesAsync(reportId)).Single(d => d.UserId == f.Tenant.Owner.UserId);
        Assert.Equal((ReportDeliveryStatus.Failed, 5), (last.Status, last.Attempts));
        Assert.Empty(_host.Email.SentTo(ownerEmail));
    }

    private async Task<string> MemberEmailAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users.IgnoreQueryFilters().Where(u => u.Id == userId).Select(u => u.Email).SingleAsync();
    }

    [Fact]
    public async Task The_unsubscribe_link_stops_the_emails_of_that_report_and_a_forged_one_is_not_found()
    {
        var (f, owner, ownerEmail) = await StartAsync();
        using var _ = f;
        var definition = await CreateDefinitionAsync(owner, DefinitionBody(f.Tenant.Owner.UserId, name: "Diario de ventas"));
        var first = await GenerateAsync(f, definition.Id, LastWeek);
        await DeliverAllAsync(first, DateTime.UtcNow);
        var token = Uri.UnescapeDataString(_host.Email.SentTo(ownerEmail).Single().Headers["List-Unsubscribe"].Split("t=")[1].TrimEnd('>'));

        var response = await _anonymous.PostAsync($"{ReportsUrl}/unsubscribe/{token}", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Diario de ventas", (await response.Content.ReadFromJsonAsync<ReportUnsubscribeResponse>())!.DefinitionName);
        Assert.Equal(HttpStatusCode.OK, (await _anonymous.PostAsync($"{ReportsUrl}/unsubscribe/{token}", null)).StatusCode); // idempotent

        var forged = token[..^2] + (token[^2] == 'A' ? "BB" : "AA");
        var refused = await _anonymous.PostAsync($"{ReportsUrl}/unsubscribe/{forged}", null);
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);

        var second = await GenerateAsync(f, definition.Id, new ReportPeriod(LastWeek.To, LastWeek.To.AddDays(7)));
        var delivery = Assert.Single(await DeliveriesAsync(second));
        Assert.Equal((ReportDeliveryStatus.Skipped, "unsubscribed"), (delivery.Status, delivery.ErrorCode));
        var settings = await owner.GetFromJsonAsync<ReportDefinitionResponse>($"{ReportsUrl}/definitions/{definition.Id}");
        Assert.NotNull(Assert.Single(settings!.Recipients).EmailUnsubscribedAt);
    }

    // ---- WhatsApp delivery ------------------------------------------------------------------------------------------------

    private async Task SeedTemplateAsync(ChannelFixture f, string name = "resumen_gerente", TemplateCategory category = TemplateCategory.Utility, string body = "Tu resumen de {{1}}: {{2}}. Míralo en {{3}}")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.MessageTemplates.Add(MessageTemplate.Create(f.TenantId, f.Channel.Id, name, "es", category, TemplateStatus.Approved, body, true, DateTime.UtcNow));
        await db.SaveChangesAsync();
    }

    /// <summary>Opts in and verifies with the code the stand-in provider received (the line needs its key and a report template).</summary>
    private async Task<string> OptInAsync(TestActor actor, Guid channelId)
    {
        var number = "+57 310 " + Random.Shared.Next(100, 999) + " " + Random.Shared.Next(1000, 9999);
        var digits = new string([.. number.Where(char.IsAsciiDigit)]);
        var response = await _host.ClientOf(actor).PutAsJsonAsync($"{ReportsUrl}/whatsapp-opt-in", new { phoneNumber = number, consent = true });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<ReportWhatsAppOptInResponse>())!.CodeSent);
        var verify = await _host.ClientOf(actor).PostAsJsonAsync($"{ReportsUrl}/whatsapp-opt-in/verify", new { code = CodeSentTo(channelId, digits) });
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        return digits;
    }

    private string CodeSentTo(Guid channelId, string digits)
        => _host.WhatsApp.TemplatesSentBy(channelId).Last(t => t.RecipientWaId == digits).Parameters[1].Value[^6..];

    [Fact]
    public async Task Only_approved_utility_templates_with_three_fields_are_offered()
    {
        var (f, owner, _) = await StartAsync();
        using var _ = f;
        await SeedTemplateAsync(f);
        await SeedTemplateAsync(f, "promo", TemplateCategory.Marketing);
        await SeedTemplateAsync(f, "corta", body: "Hola {{1}}");

        var options = await owner.GetFromJsonAsync<ReportOptionsResponse>($"{ReportsUrl}/options");

        var template = Assert.Single(options!.Templates);
        Assert.Equal(("resumen_gerente", "es", f.Channel.Id), (template.Name, template.Language, template.ChannelId));
        Assert.Equal(6, options.Sections.Count);
    }

    [Fact]
    public async Task The_whatsapp_notice_uses_the_template_with_period_headline_and_link_and_only_reaches_who_opted_in()
    {
        var (f, owner, _) = await StartAsync();
        using var _ = f;
        await SetCredentialsAsync(owner, f.Channel.Id);
        await SeedTemplateAsync(f);
        var number = await OptInAsync(f.Tenant.Owner, f.Channel.Id);
        using var member = await f.Tenant.AddMemberAsync("sinwhatsapp");
        var definition = await CreateDefinitionAsync(owner, DefinitionBody(
            f.Tenant.Owner.UserId, email: false, whatsApp: true, channelId: f.Channel.Id, template: "resumen_gerente", language: "es", more: member.UserId));

        var reportId = await GenerateAsync(f, definition.Id, LastWeek);
        var deliveries = await DeliveriesAsync(reportId);
        Assert.Equal((ReportDeliveryStatus.Skipped, "no_opt_in"), (deliveries.Single(d => d.UserId == member.UserId).Status, deliveries.Single(d => d.UserId == member.UserId).ErrorCode));

        await DeliverAllAsync(reportId, DateTime.UtcNow);

        var sent = Assert.Single(_host.WhatsApp.TemplatesSentBy(f.Channel.Id), t => t.Parameters[2].Value.Contains("/informes/" + reportId, StringComparison.Ordinal));
        Assert.Equal((number, "resumen_gerente", "es"), (sent.RecipientWaId, sent.Name, sent.Language));
        Assert.Equal("semana del 2 al 8 de noviembre de 2026", sent.Parameters[0].Value);
        Assert.Equal((await LoadReportAsync(reportId)).Headline, sent.Parameters[1].Value);
        Assert.Equal($"https://app.conversia.test/informes/{reportId}", sent.Parameters[2].Value);
        Assert.Equal(ReportDeliveryStatus.Sent, (await DeliveriesAsync(reportId)).Single(d => d.UserId == f.Tenant.Owner.UserId).Status);
    }

    [Fact]
    public async Task A_company_in_sandbox_mode_sends_the_notice_through_the_internal_sender_and_a_timeout_is_not_repeated()
    {
        var (f, owner, _) = await StartAsync();
        using var _ = f;
        await SeedTemplateAsync(f);
        await SetCredentialsAsync(owner, f.Channel.Id);
        await OptInAsync(f.Tenant.Owner, f.Channel.Id);
        var definition = await CreateDefinitionAsync(owner, DefinitionBody(f.Tenant.Owner.UserId, email: false, whatsApp: true, channelId: f.Channel.Id, template: "resumen_gerente", language: "es"));
        var codes = _host.WhatsApp.TemplatesSentBy(f.Channel.Id).Count;

        await SetTenantFlagsAsync(_factory.Services, f.TenantId, sandbox: true);
        var sandboxReport = await GenerateAsync(f, definition.Id, LastWeek);
        await DeliverAllAsync(sandboxReport, DateTime.UtcNow);
        var sandboxDelivery = Assert.Single(await DeliveriesAsync(sandboxReport));
        Assert.Equal(ReportDeliveryStatus.Sent, sandboxDelivery.Status);
        Assert.StartsWith(Conversia.Application.Sandbox.SandboxIds.MessageIdPrefix, sandboxDelivery.ProviderMessageId);
        Assert.Equal(codes, _host.WhatsApp.TemplatesSentBy(f.Channel.Id).Count); // nothing more reached the provider
        Assert.True((await LoadReportAsync(sandboxReport)).IsSandbox);

        await SetTenantFlagsAsync(_factory.Services, f.TenantId, sandbox: false);
        _host.WhatsApp.Answer(f.Channel.Id, () => WhatsAppSendResult.Failed(WhatsAppSendStatus.TransientFailure));
        var real = await GenerateAsync(f, definition.Id, new ReportPeriod(LastWeek.To, LastWeek.To.AddDays(7)));
        await DeliverAllAsync(real, DateTime.UtcNow);
        var failed = Assert.Single(await DeliveriesAsync(real));
        Assert.Equal((ReportDeliveryStatus.Failed, "provider_unreachable", 1), (failed.Status, failed.ErrorCode, failed.Attempts));
    }

    [Fact]
    public async Task The_opt_in_needs_explicit_consent_and_withdrawing_it_deletes_the_number()
    {
        var (f, owner, _) = await StartAsync();
        using var _ = f;

        var refused = await owner.PutAsJsonAsync($"{ReportsUrl}/whatsapp-opt-in", new { phoneNumber = "+57 310 555 0101", consent = false });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("aceptar expresamente", await refused.Content.ReadAsStringAsync());

        var accepted = await (await owner.PutAsJsonAsync($"{ReportsUrl}/whatsapp-opt-in", new { phoneNumber = "+57 310 555 0101", consent = true }))
            .Content.ReadFromJsonAsync<ReportWhatsAppOptInResponse>();
        Assert.Equal((true, "+573105550101", false, false), (accepted!.OptedIn, accepted.PhoneNumber, accepted.Verified, accepted.CodeSent));
        Assert.NotNull(accepted.OptedInAt);

        var withdrawn = await (await owner.DeleteAsync($"{ReportsUrl}/whatsapp-opt-in")).Content.ReadFromJsonAsync<ReportWhatsAppOptInResponse>();
        Assert.Equal((false, null), (withdrawn!.OptedIn, withdrawn.PhoneNumber));
        Assert.NotNull(withdrawn.OptedOutAt);
        using var scope = _factory.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users.IgnoreQueryFilters().AsNoTracking().SingleAsync(u => u.Id == f.Tenant.Owner.UserId);
        Assert.Null(user.ReportsWhatsAppNumber);
    }

    // ---- Reading, preview, retention, monitoring ----------------------------------------------------------------------------

    [Fact]
    public async Task A_report_is_readable_by_whoever_sees_the_metrics_or_receives_it_and_by_nobody_else()
    {
        var (f, owner, _) = await StartAsync();
        using var _ = f;
        using var recipient = await f.Tenant.AddMemberAsync("recibe");
        using var outsider = await f.Tenant.AddMemberAsync("ajeno");
        var definition = await CreateDefinitionAsync(owner, DefinitionBody(f.Tenant.Owner.UserId, more: recipient.UserId));
        var reportId = await GenerateAsync(f, definition.Id, LastWeek);

        var asRecipient = await _host.ClientOf(recipient).GetFromJsonAsync<ReportResponse>($"{ReportsUrl}/{reportId}");
        Assert.Empty(asRecipient!.Deliveries); // the content, not who else received it
        Assert.Equal(HttpStatusCode.NotFound, (await _host.ClientOf(outsider).GetAsync($"{ReportsUrl}/{reportId}")).StatusCode);
        Assert.Equal(2, (await owner.GetFromJsonAsync<ReportResponse>($"{ReportsUrl}/{reportId}"))!.Deliveries.Count);
    }

    [Fact]
    public async Task The_preview_writes_the_last_complete_period_without_storing_or_sending_and_is_rate_limited()
    {
        var host = ReportsHostOf(_factory, name: "reports-preview", settings: new() { ["Reports:PreviewsPerHour"] = "1" });
        var (f, owner, ownerEmail) = await StartAsync(host);
        using var _ = f;
        var definition = await CreateDefinitionAsync(owner, DefinitionBody(f.Tenant.Owner.UserId));

        var response = await owner.PostAsync($"{ReportsUrl}/definitions/{definition.Id}/preview", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = (await response.Content.ReadFromJsonAsync<ReportPreviewResponse>())!;
        Assert.Equal(ReportSchedule.LastCompleted(ReportFrequency.Weekly, DateTime.UtcNow, ReportSchedule.Bogota).From, preview.Period.From);
        Assert.Equal("Template", preview.Generator);
        Assert.NotEmpty(preview.Content.Actions);
        Assert.Empty((await owner.GetFromJsonAsync<PagedResponse<ReportSummaryResponse>>(ReportsUrl))!.Items);
        Assert.Empty(host.Email.SentTo(ownerEmail));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await owner.PostAsync($"{ReportsUrl}/definitions/{definition.Id}/preview", null)).StatusCode);
    }

    [Fact]
    public async Task Reports_older_than_the_retention_are_deleted_with_their_deliveries()
    {
        var (f, owner, _) = await StartAsync();
        using var _ = f;
        var definition = await CreateDefinitionAsync(owner, DefinitionBody(f.Tenant.Owner.UserId));
        var old = await GenerateAsync(f, definition.Id, LastWeek);
        var recent = await GenerateAsync(f, definition.Id, new ReportPeriod(LastWeek.To, LastWeek.To.AddDays(7)));
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database
                .ExecuteSqlAsync($"""UPDATE "ManagerReports" SET "GeneratedAt" = now() - interval '181 days' WHERE "Id" = {old}""");
        }

        Assert.True(await _host.Runner.PurgeAsync(DateTime.UtcNow) >= 1);

        var left = (await owner.GetFromJsonAsync<PagedResponse<ReportSummaryResponse>>(ReportsUrl))!.Items;
        Assert.Equal([recent], left.Select(r => r.Id));
        Assert.Empty(await DeliveriesAsync(old));
    }

    [Fact]
    public async Task The_administrator_sees_counts_cost_and_the_sender_never_the_text()
    {
        var (f, owner, _) = await StartAsync();
        using var _ = f;
        var definition = await CreateDefinitionAsync(owner, DefinitionBody(f.Tenant.Owner.UserId));
        var reportId = await GenerateAsync(f, definition.Id, LastWeek);
        await DeliverAllAsync(reportId, DateTime.UtcNow);
        using var admin = await StartAdminAsync(_factory, _anonymous);

        var response = await admin.Client.GetAsync("/api/admin/monitoring/reports");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var monitoring = System.Text.Json.JsonSerializer.Deserialize<ReportMonitoringResponse>(body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        var row = Assert.Single(monitoring.Companies, c => c.TenantId == f.TenantId);
        Assert.Equal((1, 0, 1, 1, 0), (row.Generated, row.ByModel, row.ByTemplate, row.Sent, row.Failed));
        Assert.DoesNotContain((await LoadReportAsync(reportId)).Headline, body);
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync("/api/admin/monitoring/reports")).StatusCode);
    }
}
