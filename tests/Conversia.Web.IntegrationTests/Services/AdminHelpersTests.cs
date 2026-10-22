using System.Text.Json;
using Conversia.Application.DTOs.Admin;
using Conversia.Domain.Enums;
using Conversia.Web.Authentication;
using Conversia.Web.Services;

namespace Conversia.Web.IntegrationTests.Services;

/// <summary>The pure helpers of the administration pages: filters, labels, the audit in words, the query strings and where a sign-in goes back to.</summary>
public class AdminHelpersTests
{
    // ---- AdminListFilters ----

    [Theory]
    [InlineData("pendiente", TenantStatus.Pending)]
    [InlineData("ACTIVA", TenantStatus.Active)]
    [InlineData("Suspendida", TenantStatus.Suspended)]
    public void A_status_word_is_read_without_regard_to_case(string word, TenantStatus expected)
        => Assert.Equal(expected, AdminListFilters.From(word, null, null).Status);

    [Theory]
    [InlineData("Pending")]
    [InlineData("activo")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_no_status_filter(string? word)
    {
        var filters = AdminListFilters.From(word, null, null);

        Assert.Null(filters.Status);
        Assert.False(filters.IsFiltered);
    }

    [Theory]
    [InlineData("3", 3)]
    [InlineData("1", 1)]
    [InlineData("0", 1)]
    [InlineData("-2", 1)]
    [InlineData("+2", 1)]
    [InlineData("2.0", 1)]
    [InlineData("99999999999999999999", 1)]
    [InlineData(null, 1)]
    public void A_page_is_a_whole_number_from_one(string? text, int expected)
        => Assert.Equal(expected, AdminListFilters.From(null, null, text).Page);

    [Fact]
    public void The_search_is_trimmed_dropped_when_blank_and_cut_to_the_length_the_api_accepts()
    {
        Assert.Equal("aurora", AdminListFilters.From(null, "  aurora ", null).Search);
        Assert.Null(AdminListFilters.From(null, "   ", null).Search);
        Assert.Equal(AdminListFilters.SearchMaxLength, AdminListFilters.From(null, new string('a', 500), null).Search!.Length);
    }

    [Fact]
    public void The_query_sent_to_the_api_uses_the_enum_name_and_the_fixed_page_size()
    {
        var query = AdminListFilters.From("suspendida", "sur", "2").ToQuery();

        Assert.Equal("Suspended", query.Status);
        Assert.Equal("sur", query.Search);
        Assert.Equal(2, query.Page);
        Assert.Equal(20, query.PageSize);
    }

    [Fact]
    public void A_page_link_keeps_the_filters_and_the_first_page_is_the_bare_address()
    {
        var filters = AdminListFilters.From("activa", "ñ & co", "2");

        Assert.Equal("/admin?estado=activa&buscar=%C3%B1%20%26%20co&pagina=3", filters.ToUrl(3));
        Assert.Equal("/admin?estado=activa&buscar=%C3%B1%20%26%20co", filters.ToUrl(1));
        Assert.Equal("/admin", AdminListFilters.From(null, null, null).ToUrl(1));
        Assert.Equal("/admin?pagina=2", AdminListFilters.From(null, null, null).ToUrl(2));
    }

    [Fact]
    public void The_status_words_round_trip()
    {
        foreach (var status in Enum.GetValues<TenantStatus>())
        {
            Assert.Equal(status, AdminListFilters.From(AdminListFilters.WordFor(status), null, null).Status);
        }

        Assert.Equal(Enum.GetValues<TenantStatus>().Length, AdminListFilters.Statuses.Count);
    }

    // ---- AdminAuditFilters ----

    [Fact]
    public void The_audit_company_is_a_guid_that_is_not_empty_or_nothing()
    {
        var id = Guid.NewGuid();

        Assert.Equal(id, AdminAuditFilters.From(id.ToString(), null).TenantId);
        Assert.Null(AdminAuditFilters.From(Guid.Empty.ToString(), null).TenantId);
        Assert.Null(AdminAuditFilters.From("x", null).TenantId);
        Assert.Null(AdminAuditFilters.From(null, null).TenantId);
        Assert.Equal(4, AdminAuditFilters.From(null, "4").Page);
        Assert.Equal(1, AdminAuditFilters.From(null, "-4").Page);
        Assert.Equal($"/admin/auditoria?empresa={id}&pagina=2", AdminAuditFilters.From(id.ToString(), null).ToUrl(2));
        Assert.Equal("/admin/auditoria", AdminAuditFilters.From(null, null).ToUrl(1));
    }

    // ---- query strings ----

    [Fact]
    public void The_api_query_strings_leave_out_empty_filters_and_escape_the_rest()
    {
        Assert.Equal("?page=1&pageSize=20", AdminQueryString.Build(new AdminTenantsQuery()));
        Assert.Equal("?status=Active&search=a%26b%3Dc%20d&page=2&pageSize=5", AdminQueryString.Build(new AdminTenantsQuery { Status = "Active", Search = "a&b=c d", Page = 2, PageSize = 5 }));
        var id = Guid.NewGuid();
        Assert.Equal($"?tenantId={id}&page=1&pageSize=20", AdminQueryString.Build(new AdminAuditQuery { TenantId = id }));
        Assert.Equal("?page=1&pageSize=20", AdminQueryString.Build(new AdminAuditQuery()));
    }

    // ---- the moves of a status ----

    [Theory]
    [InlineData(TenantStatus.Pending, new[] { "activar", "suspender" })]
    [InlineData(TenantStatus.Active, new[] { "suspender" })]
    [InlineData(TenantStatus.Suspended, new[] { "reactivar" })]
    public void The_moves_offered_follow_the_lifecycle_of_the_domain(TenantStatus current, string[] routes)
        => Assert.Equal(routes, TenantStatusLabels.ActionsFor(current).Select(a => a.Route));

    [Fact]
    public void A_move_is_found_by_its_exact_route_and_leads_to_the_status_it_names()
    {
        Assert.Equal(TenantStatus.Active, TenantStatusLabels.Find("activar")!.Target);
        Assert.Equal(TenantStatus.Active, TenantStatusLabels.Find("reactivar")!.Target);
        Assert.Equal(TenantStatus.Suspended, TenantStatusLabels.Find("suspender")!.Target);
        Assert.True(TenantStatusLabels.Find("suspender")!.Danger);
        Assert.False(TenantStatusLabels.Find("activar")!.Danger);
        Assert.Null(TenantStatusLabels.Find("ACTIVAR"));
        Assert.Null(TenantStatusLabels.Find(null));
        Assert.Null(TenantStatusLabels.Find(""));
    }

    [Fact]
    public void Every_status_has_a_label_and_an_unknown_name_is_shown_as_it_came()
    {
        Assert.Equal("Pendiente", TenantStatusLabels.Label(TenantStatus.Pending));
        Assert.Equal("Activa", TenantStatusLabels.Label("Active"));
        Assert.Equal("Suspendida", TenantStatusLabels.Label("Suspended"));
        Assert.Equal("Archived", TenantStatusLabels.Label("Archived"));
        Assert.Equal("—", TenantStatusLabels.Label((string?)null));
        Assert.Equal("7", TenantStatusLabels.Label("7")); // a number is not a status
    }

    // ---- the audit in words ----

    private static AuditEntryResponse Entry(string action, string json)
        => new() { Action = action, Details = JsonDocument.Parse(json).RootElement.Clone() };

    [Fact]
    public void A_status_change_names_both_statuses_and_the_reason_when_there_is_one()
    {
        var text = AuditFormat.Describe(Entry("tenant.status_changed", """{"from":"Suspended","to":"Active","reason":"Pagó"}"""));

        Assert.Equal("Cambió el estado: Suspendida → Activa", text.Title);
        Assert.Equal("Motivo: Pagó", text.Detail);
        Assert.Null(AuditFormat.Describe(Entry("tenant.status_changed", """{"from":"Pending","to":"Active","reason":null}""")).Detail);
        Assert.Null(AuditFormat.Describe(Entry("tenant.status_changed", """{"from":"Pending","to":"Active","reason":""}""")).Detail);
    }

    [Fact]
    public void A_quota_change_shows_the_numbers_and_a_line_change_the_number_of_the_line()
    {
        Assert.Equal("Cambió el cupo de líneas: 0 → 12", AuditFormat.Describe(Entry("tenant.quota_changed", """{"from":0,"to":12}""")).Title);
        var line = AuditFormat.Describe(Entry("channel.deactivated", """{"channelId":"9b2f1c3e-0000-4000-8000-000000000001","displayPhoneNumber":"+57 300 000 0001"}"""));
        Assert.Equal("Desactivó una línea", line.Title);
        Assert.Equal("Número: +57 300 000 0001", line.Detail);
        Assert.DoesNotContain("9b2f1c3e", line.Title + line.Detail);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("""{"from":{"a":1},"to":[1]}""")]
    public void Details_that_are_missing_or_of_another_shape_never_break_the_page(string json)
    {
        foreach (var action in new[] { "tenant.status_changed", "tenant.quota_changed", "channel.deactivated", "something.else" })
        {
            var text = AuditFormat.Describe(Entry(action, json));

            Assert.False(string.IsNullOrWhiteSpace(text.Title));
        }
    }

    [Fact]
    public void An_entry_without_any_details_is_described_from_its_action_alone()
        => Assert.Equal("Cambio registrado (x.y)", AuditFormat.Describe(new AuditEntryResponse { Action = "x.y" }).Title);

    [Fact]
    public void An_action_the_page_does_not_know_is_shown_by_its_code()
        => Assert.Equal("Cambio registrado (tenant.deleted)", AuditFormat.Describe(Entry("tenant.deleted", "{}")).Title);

    // ---- numbers and dates ----

    [Fact]
    public void Counts_and_quotas_are_written_in_spanish()
    {
        Assert.Equal("2 de 5", AdminFormat.LinesOfQuota(2, 5));
        Assert.Equal("1.234 de 1.000", AdminFormat.LinesOfQuota(1234, 1000));
        Assert.Equal("1 empresa", AdminFormat.Companies(1));
        Assert.Equal("1.200 empresas", AdminFormat.Companies(1200));
        Assert.Equal("1 cambio", AdminFormat.Changes(1));
        Assert.Equal("0 cambios", AdminFormat.Changes(0));
        Assert.Equal("5 ene 2026, 09:05", AdminFormat.Moment(new DateTime(2026, 1, 5, 9, 5, 0)));
    }

    // ---- the quota form ----

    [Theory]
    [InlineData("0", true, 0)]
    [InlineData("1000", true, 1000)]
    [InlineData(" 7 ", true, 7)]
    [InlineData("1001", false, 0)]
    [InlineData("-1", false, 0)]
    [InlineData("1,5", false, 0)]
    [InlineData("", false, 0)]
    [InlineData(null, false, 0)]
    [InlineData("１２", false, 0)] // full-width digits are not numbers for the API
    public void The_quota_is_a_whole_number_from_zero_to_one_thousand(string? text, bool valid, int expected)
    {
        Assert.Equal(valid, QuotaInput.TryParse(text, out var value));
        if (valid)
        {
            Assert.Equal(expected, value);
        }

        var input = new QuotaInput { MaxActiveLines = text };
        Assert.Equal(valid ? 0 : 1, input.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(input)).Count());
        if (valid)
        {
            Assert.Equal(expected, input.ToRequest().MaxActiveLines);
        }
    }

    [Fact]
    public void The_reason_is_trimmed_and_a_blank_one_is_not_sent()
    {
        Assert.Equal("Pagó", new StatusReasonInput { Reason = "  Pagó  " }.ToRequest("Active").Reason);
        Assert.Null(new StatusReasonInput { Reason = "   " }.ToRequest("Active").Reason);
        Assert.Null(new StatusReasonInput().ToRequest("Suspended").Reason);
        Assert.Equal("Suspended", new StatusReasonInput().ToRequest("Suspended").Status);
    }

    // ---- where a sign-in goes back to ----

    [Theory]
    [InlineData("/admin", "/admin")]
    [InlineData("/admin/auditoria", "/admin/auditoria")]
    [InlineData("/admin/empresas/abc?aviso=cupo", "/admin/empresas/abc?aviso=cupo")]
    [InlineData("/ADMIN/auditoria", "/ADMIN/auditoria")]
    [InlineData("/administrador", "/admin")]
    [InlineData("/adminx/y", "/admin")]
    [InlineData("/panel", "/admin")]
    [InlineData("/", "/admin")]
    [InlineData("//admin", "/admin")]
    [InlineData("/\\admin", "/admin")]
    [InlineData("https://evil.example/admin", "/admin")]
    [InlineData("/admin\r\nSet-Cookie: x=1", "/admin")]
    [InlineData("", "/admin")]
    [InlineData(null, "/admin")]
    public void An_administrator_goes_back_only_to_a_page_of_the_administration(string? returnUrl, string expected)
        => Assert.Equal(expected, LocalReturnUrl.OrAdminHome(returnUrl));

    [Fact]
    public void A_company_user_still_goes_back_to_any_local_page_and_the_pending_page_is_not_one_of_the_administrations()
    {
        Assert.Equal("/users", LocalReturnUrl.OrHome("/users"));
        Assert.Equal("/admin/x", LocalReturnUrl.OrHome("/admin/x"));
        Assert.Equal("/cuenta-pendiente", LocalReturnUrl.PendingAccount);
        Assert.Equal("/cuenta-pendiente?registrado=1", LocalReturnUrl.JustRegistered);
    }
}
