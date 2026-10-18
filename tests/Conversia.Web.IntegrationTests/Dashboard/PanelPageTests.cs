using System.Net;
using AngleSharp.Dom;
using Conversia.Domain.Enums;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Dashboard;

/// <summary>GET /panel against a stand-in API: the six headline numbers, the alerts, the breakdowns, the charts and every state the page can be in.</summary>
public class PanelPageTests
{
    private static IElement Kpi(PageResponse page, string kpi)
        => page.Document.QuerySelector($"[data-testid=kpi-card][data-kpi={kpi}]") ?? throw new Xunit.Sdk.XunitException($"No KPI card '{kpi}'. Page text: {page.Text}");

    private static string KpiValue(PageResponse page, string kpi) => Kpi(page, kpi).QuerySelector("[data-testid=kpi-value]")!.Plain().Trim();

    private static IElement Breakdown(PageResponse page, string key)
        => page.Document.QuerySelector($"[data-testid=breakdown][data-breakdown={key}]") ?? throw new Xunit.Sdk.XunitException($"No breakdown '{key}'.");

    private static IReadOnlyList<string> RowNames(IElement breakdown)
        => breakdown.QuerySelectorAll("[data-testid=breakdown-row] .breakdown__name").Select(e => e.Plain().Trim()).ToList();

    // ---- the populated page ----

    [Fact]
    public async Task The_six_headline_numbers_are_there_with_their_explanations()
    {
        using var harness = PanelHarness.Create(MetricsStub.Populated);

        var page = (await harness.OpenAsync()).ShouldBe(HttpStatusCode.OK);

        var cards = page.Select("[data-testid=kpi-card]").Select(c => c.GetAttribute("data-kpi")).ToList();
        Assert.Equal(["conversations", "conversion", "first-response", "unanswered", "loss-reason", "variation"], cards);
        Assert.Equal("120", KpiValue(page, "conversations"));
        Assert.Equal("36,0 %", KpiValue(page, "conversion")); // 36 won of 100 decided (36 + 44 lost + 20 stale)
        Assert.Equal("15 min", KpiValue(page, "first-response")); // 14.5 rounds to 15
        Assert.Equal("12", KpiValue(page, "unanswered"));
        Assert.Equal("Precio", KpiValue(page, "loss-reason"));
        Assert.All(page.Select("[data-testid=kpi-card]"), card => Assert.False(string.IsNullOrWhiteSpace(card.QuerySelector(".kpi__help")?.Plain())));
    }

    [Fact]
    public async Task Conversion_shows_the_won_lost_and_following_counts_and_what_is_still_maturing()
    {
        using var harness = PanelHarness.Create(MetricsStub.Populated);

        var page = await harness.OpenAsync();

        Assert.Equal("36 ganadas", page.Document.QuerySelector("[data-testid=kpi-won]")!.Plain().Trim());
        Assert.Equal("44 perdidas", page.Document.QuerySelector("[data-testid=kpi-lost]")!.Plain().Trim());
        Assert.Equal("40 en seguimiento", page.Document.QuerySelector("[data-testid=kpi-following]")!.Plain().Trim());
        Assert.Contains("20 conversaciones siguen en maduración", page.Document.QuerySelector("[data-testid=kpi-maturing]")!.Plain());
    }

    [Fact]
    public async Task First_response_is_the_median_with_the_share_inside_the_sla()
    {
        using var harness = PanelHarness.Create(MetricsStub.Populated);

        var page = await harness.OpenAsync();

        Assert.Contains("80,0 % responde dentro de 30 min", Kpi(page, "first-response").Plain());
    }

    [Fact]
    public async Task Unanswered_is_the_open_count_in_danger_style_with_the_oldest_wait_and_the_closed_ones_apart()
    {
        using var harness = PanelHarness.Create(MetricsStub.Populated);

        var page = await harness.OpenAsync();

        var card = Kpi(page, "unanswered");
        Assert.Contains("kpi--danger", card.ClassName);
        var text = card.Plain();
        Assert.Contains("5 llevan más de 30 min esperando", text);
        Assert.Contains("3 h 10 min", text);
        Assert.Contains("8 conversaciones se cerraron sin respuesta", card.QuerySelector("[data-testid=kpi-unanswered-closed]")!.Plain());
    }

    [Fact]
    public async Task With_nothing_waiting_the_unanswered_card_is_calm_and_says_so_in_words()
    {
        using var harness = PanelHarness.Create(MetricsStub.Quiet);

        var page = await harness.OpenAsync();

        var card = Kpi(page, "unanswered");
        Assert.Equal("0", KpiValue(page, "unanswered"));
        Assert.DoesNotContain("kpi--danger", card.ClassName);
        Assert.Contains("Todos los clientes tienen respuesta", card.Plain());
    }

    [Fact]
    public async Task The_main_loss_reason_leaves_the_silent_customers_for_their_own_line()
    {
        using var harness = PanelHarness.Create(MetricsStub.Populated);

        var page = await harness.OpenAsync();

        var card = Kpi(page, "loss-reason");
        Assert.Contains("14 de 30 perdidas con motivo conocido (46,7", card.Plain());
        Assert.Contains("14 conversaciones se perdieron porque el cliente dejó de responder", card.QuerySelector("[data-testid=kpi-no-response]")!.Plain());
        var items = page.Select("[data-testid=loss-reasons] [data-testid=barlist-item]").ToList();
        Assert.Equal(["Price", "Timing", "Location"], items.Select(i => i.GetAttribute("data-key")));
        Assert.DoesNotContain("NoResponse", items.Select(i => i.GetAttribute("data-key")));
        Assert.Contains("14", page.Document.QuerySelector("[data-testid=loss-no-response]")!.Plain());
        Assert.Contains("inferencia", page.Document.QuerySelector("[data-testid=loss-no-response]")!.Plain());
    }

    [Fact]
    public async Task The_variation_card_shows_points_direction_and_both_rates_in_words()
    {
        using var harness = PanelHarness.Create(MetricsStub.Populated);

        var page = await harness.OpenAsync();

        var trend = page.Document.QuerySelector("[data-testid=kpi-trend]")!;
        Assert.Equal("up", trend.GetAttribute("data-trend"));
        Assert.StartsWith("+", KpiValue(page, "variation"));
        Assert.Contains("Subió", trend.Plain());
        Assert.Contains("kpi--success", Kpi(page, "variation").ClassName);
    }

    [Fact]
    public async Task A_conversion_drop_reads_as_a_drop_in_text_not_only_in_colour()
    {
        using var harness = PanelHarness.Create(date =>
        {
            var stub = MetricsStub.Populated(date);
            stub.PreviousConversion = MetricsStub.Counts(100, 70, 20, 10, 5, 100, 70, 20);
            return stub;
        });

        var page = await harness.OpenAsync();

        var trend = page.Document.QuerySelector("[data-testid=kpi-trend]")!;
        Assert.Equal("down", trend.GetAttribute("data-trend"));
        Assert.StartsWith("−", KpiValue(page, "variation"));
        Assert.Contains("Bajó", trend.Plain());
        Assert.Contains("kpi--danger", Kpi(page, "variation").ClassName);
    }

    // ---- alerts ----

    [Fact]
    public async Task Alerts_come_first_with_their_severity_in_words_and_a_link_to_what_they_are_about()
    {
        using var harness = PanelHarness.Create(MetricsStub.Populated);

        var page = await harness.OpenAsync();

        var alerts = page.Select("[data-testid=alert]").ToList();
        Assert.Equal(["UNANSWERED_OVER_SLA", "BRANCH_BELOW_AVERAGE"], alerts.Select(a => a.GetAttribute("data-code")));
        Assert.Contains("Crítica", alerts[0].Plain());
        Assert.Contains("Atención", alerts[1].Plain());
        Assert.Contains("hace más de 30 minutos", alerts[0].Plain());
        var unansweredLink = alerts[0].QuerySelector("a")!.GetAttribute("href")!;
        Assert.StartsWith("/conversaciones?", unansweredLink);
        Assert.Contains("unanswered=true", unansweredLink);
        Assert.Equal($"/panel?branch={MetricsStub.Norte}", alerts[1].QuerySelector("a")!.GetAttribute("href"));
        Assert.Null(page.Document.QuerySelector("[data-testid=alerts-calm]"));
        var firstSection = page.Select("main section").First();
        Assert.NotNull(firstSection.QuerySelector("[data-testid=alerts]"));
    }

    [Fact]
    public async Task The_longest_waiting_conversations_link_to_their_detail_without_naming_the_customer()
    {
        using var harness = PanelHarness.Create(MetricsStub.Populated);

        var page = await harness.OpenAsync();

        var rows = page.Select("[data-testid=unanswered-row]").ToList();
        Assert.Equal(2, rows.Count);
        Assert.Contains("3 h 10 min", rows[0].Plain());
        Assert.Equal($"/conversaciones/{rows[0].GetAttribute("data-conversation-id")}", rows[0].QuerySelector("a")!.GetAttribute("href"));
        Assert.Contains("Sin asignar", rows[0].Plain());
        Assert.DoesNotMatch(@"\+?\d{7,}", page.Document.QuerySelector("[data-testid=unanswered-list]")!.Plain());
    }

    [Fact]
    public async Task Without_alerts_the_page_says_everything_is_in_order()
    {
        using var harness = PanelHarness.Create(MetricsStub.Quiet);

        var page = await harness.OpenAsync();

        Assert.Contains("Todo en orden", page.Document.QuerySelector("[data-testid=alerts-calm]")!.Plain());
        Assert.Empty(page.Select("[data-testid=alert]"));
        Assert.Null(page.Document.QuerySelector("[data-testid=unanswered-list]"));
    }

    [Fact]
    public async Task A_member_without_conversations_access_gets_no_links_into_them()
    {
        using var harness = PanelHarness.Create(MetricsStub.Populated, [Permission.MetricsView]);

        var page = (await harness.OpenAsync()).ShouldBe(HttpStatusCode.OK);

        Assert.Empty(page.Select("a[href^='/conversaciones']"));
        Assert.NotEmpty(page.Select("[data-testid=alert]"));
    }

    // ---- breakdowns ----

    [Fact]
    public async Task Branches_advisors_and_sources_are_compared_side_by_side()
    {
        using var harness = PanelHarness.Create(MetricsStub.Populated);

        var page = await harness.OpenAsync();

        Assert.Equal(["Centro", "Norte"], RowNames(Breakdown(page, "branch")));
        Assert.Equal(["Ana Torres", "Luis Pérez", "Sin asignar"], RowNames(Breakdown(page, "advisor")));
        Assert.Equal(["Anuncios", "Orgánico", "Desconocido"], RowNames(Breakdown(page, "source")));
        var centro = Breakdown(page, "branch").QuerySelector("[data-testid=breakdown-row]")!;
        Assert.Equal("80", centro.QuerySelector("[data-field=total]")!.Plain().Trim());
        Assert.Equal("50,0 %", centro.QuerySelector("[data-field=conversion]")!.Plain().Trim()); // 30 of 60
        Assert.Equal("9 min", centro.QuerySelector("[data-field=response]")!.Plain().Trim());
    }

    [Fact]
    public async Task A_row_with_few_decided_conversations_is_flagged_as_a_small_sample()
    {
        using var harness = PanelHarness.Create(MetricsStub.Populated);

        var page = await harness.OpenAsync();

        var luis = Breakdown(page, "advisor").QuerySelectorAll("[data-testid=breakdown-row]").Single(r => r.Plain().Contains("Luis"));
        Assert.Equal("true", luis.GetAttribute("data-low-sample"));
        Assert.Contains("Muestra baja", luis.Plain());
        var ana = Breakdown(page, "advisor").QuerySelectorAll("[data-testid=breakdown-row]").Single(r => r.Plain().Contains("Ana"));
        Assert.Equal("false", ana.GetAttribute("data-low-sample"));
    }

    [Fact]
    public async Task A_breakdown_is_sorted_by_a_link_that_keeps_the_rest_of_the_view()
    {
        using var harness = PanelHarness.Create(MetricsStub.Populated);

        var page = (await harness.OpenAsync("?period=7d&sortBranch=-conversion")).ShouldBe(HttpStatusCode.OK);

        Assert.Equal(["Centro", "Norte"], RowNames(Breakdown(page, "branch")));
        var header = Breakdown(page, "branch").QuerySelector("th[aria-sort]")!;
        Assert.Equal("descending", header.GetAttribute("aria-sort"));
        var reversed = Breakdown(page, "branch").QuerySelectorAll("th a.sort-link").Single(a => a.Plain().Contains("Conversión")).GetAttribute("href")!;
        Assert.Contains("period=7d", reversed);
        Assert.Contains("sortBranch=conversion", reversed);
        Assert.EndsWith("#tabla-branch", reversed);

        var ascending = (await harness.OpenAsync("?sortBranch=conversion")).ShouldBe(HttpStatusCode.OK);
        Assert.Equal(["Norte", "Centro"], RowNames(Breakdown(ascending, "branch")));
        var byName = (await harness.OpenAsync("?sortAdvisor=-name")).ShouldBe(HttpStatusCode.OK);
        Assert.Equal(["Sin asignar", "Luis Pérez", "Ana Torres"], RowNames(Breakdown(byName, "advisor")));
    }

    [Fact]
    public async Task An_unknown_sort_in_the_address_is_ignored()
    {
        using var harness = PanelHarness.Create(MetricsStub.Populated);

        var page = (await harness.OpenAsync("?sortBranch=-evil&sortAdvisor=;drop")).ShouldBe(HttpStatusCode.OK);

        Assert.Equal(["Centro", "Norte"], RowNames(Breakdown(page, "branch")));
        Assert.Empty(page.Select("th[aria-sort]"));
    }

    // ---- charts ----

    [Fact]
    public async Task The_loss_reasons_are_bars_with_their_count_and_share_in_words()
    {
        using var harness = PanelHarness.Create(MetricsStub.Populated);

        var page = await harness.OpenAsync();

        var first = page.Select("[data-testid=loss-reasons] [data-testid=barlist-item]").First();
        Assert.Contains("Precio", first.Plain());
        Assert.Contains("14", first.QuerySelector(".barlist__value")!.Plain());
        Assert.Contains("46,7", first.QuerySelector(".barlist__value")!.Plain());
        var fill = first.QuerySelector("rect.barlist__fill")!;
        Assert.Equal("46.7", fill.GetAttribute("width"));
        Assert.Equal("true", first.QuerySelector("svg")!.GetAttribute("aria-hidden"));
        Assert.Contains("Y 3 más con otros motivos", page.Document.QuerySelector("[data-testid=loss-other]")!.Plain());
    }

    [Fact]
    public async Task The_daily_volume_chart_has_a_text_twin_and_a_table_of_the_data()
    {
        using var harness = PanelHarness.Create(MetricsStub.Populated);

        var page = await harness.OpenAsync();

        var chart = page.Document.QuerySelector("[data-testid=volume-chart]")!;
        Assert.Equal("30", chart.GetAttribute("data-days"));
        Assert.Equal(30, chart.QuerySelectorAll("svg rect.chart__bar, svg rect.chart__bar--peak").Length);
        var svg = chart.QuerySelector("svg")!;
        Assert.Equal("img", svg.GetAttribute("role"));
        var labelled = chart.QuerySelector("#" + svg.GetAttribute("aria-labelledby"))!;
        Assert.Contains("El día con más movimiento", labelled.Plain());
        Assert.Equal(30, chart.QuerySelectorAll("[data-testid=volume-day]").Length);
        Assert.Contains("<title>", chart.InnerHtml);
    }

    [Fact]
    public async Task The_hour_of_week_heat_map_is_a_labelled_table_of_168_cells_with_a_legend()
    {
        using var harness = PanelHarness.Create(MetricsStub.Populated);

        var page = await harness.OpenAsync();

        var heat = page.Document.QuerySelector("[data-testid=heatmap]")!;
        var cells = heat.QuerySelectorAll("td.heat__cell").ToList();
        Assert.Equal(168, cells.Count);
        var busiest = cells.Single(c => c.GetAttribute("data-count") == "9");
        Assert.Equal("2", busiest.GetAttribute("data-day")); // Tuesday
        Assert.Equal("10", busiest.GetAttribute("data-hour"));
        Assert.Equal("4", busiest.GetAttribute("data-level"));
        Assert.Contains("Martes, 10:00 a 10:59: 9 conversaciones", busiest.GetAttribute("title"));
        Assert.Equal("9", busiest.QuerySelector(".sr-only")!.Plain());
        Assert.Equal(7, heat.QuerySelectorAll("tbody th[scope=row]").Length);
        Assert.Contains("Menos", heat.QuerySelector(".heat__legend")!.Plain());
        Assert.Contains("la hora con más conversaciones nuevas es el martes", heat.QuerySelector(".chart__summary")!.Plain(), StringComparison.OrdinalIgnoreCase);
    }

    // ---- filters and the address ----

    [Fact]
    public async Task The_filter_form_offers_the_five_periods_and_the_branches_and_remembers_the_choice()
    {
        using var harness = PanelHarness.Create(MetricsStub.Populated);

        var page = await harness.OpenAsync($"?period=month&branch={MetricsStub.Norte}");

        var form = page.Document.QuerySelector("[data-testid=panel-filters]")!;
        Assert.Equal("get", form.GetAttribute("method"));
        var periods = form.QuerySelectorAll("select[name=period] option").ToList();
        Assert.Equal(["Últimos 7 días", "Últimos 30 días", "Últimos 90 días", "Este mes", "Mes anterior"], periods.Select(o => o.Plain().Trim()));
        Assert.Equal("month", periods.Single(o => o.HasAttribute("selected")).GetAttribute("value"));
        var branches = form.QuerySelectorAll("select[name=branch] option").ToList();
        Assert.Equal(["Todas las sucursales", "Centro", "Norte"], branches.Select(o => o.Plain().Trim()));
        Assert.Equal(MetricsStub.Norte.ToString(), branches.Single(o => o.HasAttribute("selected")).GetAttribute("value"));
    }

    [Fact]
    public async Task The_period_and_branch_of_the_address_become_the_query_of_every_metrics_call()
    {
        using var harness = PanelHarness.Create(MetricsStub.Populated);

        var page = (await harness.OpenAsync($"?period=7d&branch={MetricsStub.Centro}")).ShouldBe(HttpStatusCode.OK);

        var calls = harness.Stub.MetricCalls;
        Assert.Equal(6, calls.Count); // summary, conversion, first response, volume, alerts and the period before
        Assert.All(calls, uri =>
        {
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            Assert.Equal(MetricsStub.Centro.ToString(), query["branchId"]);
            Assert.Equal(7, DateOnly.Parse(query["to"]!).DayNumber - DateOnly.Parse(query["from"]!).DayNumber);
        });
        Assert.Contains("Últimos 7 días", page.Document.QuerySelector("[data-testid=panel-range]")!.Plain());
        Assert.Equal(2, calls.Count(uri => uri.AbsolutePath.EndsWith("/conversion", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task An_unknown_period_or_a_malformed_branch_falls_back_to_the_defaults()
    {
        using var harness = PanelHarness.Create(MetricsStub.Populated);

        var page = (await harness.OpenAsync("?period=forever&branch=not-a-guid")).ShouldBe(HttpStatusCode.OK);

        Assert.All(harness.Stub.MetricCalls, uri => Assert.DoesNotContain("branchId", uri.Query));
        Assert.Contains("Últimos 30 días", page.Document.QuerySelector("[data-testid=panel-range]")!.Plain());
    }

    [Fact]
    public async Task The_page_offers_a_manual_refresh_and_never_polls()
    {
        using var harness = PanelHarness.Create(MetricsStub.Populated);

        var page = await harness.OpenAsync("?period=90d");

        Assert.Equal("/panel?period=90d", page.Document.QuerySelector("[data-testid=panel-refresh]")!.GetAttribute("href"));
        Assert.Contains("Actualizado a las", page.Document.QuerySelector("[data-testid=panel-updated]")!.Plain());
        Assert.Empty(page.Select("meta[http-equiv=refresh]"));
        Assert.DoesNotContain("setInterval", page.Body);
    }

    // ---- states ----

    [Fact]
    public async Task A_period_without_conversations_is_an_empty_state_that_offers_a_next_step()
    {
        using var harness = PanelHarness.Create(MetricsStub.Empty);

        var page = (await harness.OpenAsync()).ShouldBe(HttpStatusCode.OK);

        Assert.NotNull(page.Document.QuerySelector("[data-testid=empty-state]"));
        Assert.Contains("Aún no hay conversaciones", page.Text);
        Assert.Empty(page.Select("[data-testid=kpi-card]"));
        Assert.Contains(page.Select("[data-testid=empty-state] a"), a => a.GetAttribute("href") == "/organizacion"); // an owner can connect a number
    }

    [Fact]
    public async Task A_period_still_maturing_says_so_and_never_shows_a_zero_rate()
    {
        using var harness = PanelHarness.Create(MetricsStub.Maturing);

        var page = (await harness.OpenAsync()).ShouldBe(HttpStatusCode.OK);

        Assert.Equal("En maduración", KpiValue(page, "conversion"));
        Assert.Contains("muy recientes", Kpi(page, "conversion").QuerySelector(".kpi__help")!.Plain());
        Assert.DoesNotContain("0,0", Kpi(page, "conversion").Plain());
        Assert.Equal("—", KpiValue(page, "variation"));
        Assert.Contains("en maduración", page.Document.QuerySelector("[data-testid=kpi-trend]")!.Plain());
        var row = Breakdown(page, "branch").QuerySelector("[data-testid=breakdown-row]")!;
        Assert.Equal("En maduración", row.QuerySelector("[data-field=conversion]")!.Plain().Trim());
        Assert.Null(row.QuerySelector("[data-testid=low-sample]"));
    }

    [Fact]
    public async Task A_member_without_the_metrics_permission_gets_a_friendly_403_and_no_metrics_call()
    {
        using var harness = PanelHarness.Create(MetricsStub.Populated, [Permission.ProductsView]);

        var page = (await harness.OpenAsync()).ShouldBe(HttpStatusCode.Forbidden);

        Assert.Contains("Métricas", page.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(page.Select("[data-testid=kpi-card]"));
        Assert.Empty(harness.Stub.MetricCalls);
    }

    [Fact]
    public async Task The_api_refusing_the_metrics_is_a_403_too()
    {
        using var harness = PanelHarness.Create(MetricsStub.Populated);
        harness.Stub.Failures["/api/metrics/summary"] = () => FakeBackend.Raw(HttpStatusCode.Forbidden, null);

        var page = (await harness.OpenAsync()).ShouldBe(HttpStatusCode.Forbidden);

        Assert.Empty(page.Select("[data-testid=kpi-card]"));
    }

    [Fact]
    public async Task A_throttled_api_gets_a_friendly_wait_message_and_the_refresh_button()
    {
        using var harness = PanelHarness.Create(MetricsStub.Populated);
        harness.Stub.Failures["/api/metrics/alerts"] = () => FakeBackend.Raw(HttpStatusCode.TooManyRequests, """{"title":"Demasiadas solicitudes","status":429}""", retryAfter: "37");

        var page = await harness.OpenAsync();

        Assert.Contains("37 segundos", page.Text);
        Assert.Contains("Actualizar", page.Text);
        Assert.NotNull(page.Document.QuerySelector("[data-testid=panel-refresh]"));
        Assert.Empty(page.Select("[data-testid=kpi-card]"));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task An_api_failure_is_a_503_with_the_unavailable_message(HttpStatusCode failure)
    {
        using var harness = PanelHarness.Create(MetricsStub.Populated);
        harness.Stub.Failures["/api/metrics/volume"] = () => FakeBackend.Raw(failure, null);

        var page = (await harness.OpenAsync()).ShouldBe(HttpStatusCode.ServiceUnavailable);

        Assert.Contains("no está disponible", page.Text);
        Assert.Empty(page.Select("[data-testid=kpi-card]"));
    }

    [Fact]
    public async Task A_missing_comparison_period_does_not_take_the_dashboard_down()
    {
        using var harness = PanelHarness.Create(date =>
        {
            var stub = MetricsStub.Populated(date);
            stub.PreviousConversion = null; // the stand-in answers 404 for the earlier period
            return stub;
        });

        var page = (await harness.OpenAsync()).ShouldBe(HttpStatusCode.OK);

        Assert.Equal("—", KpiValue(page, "variation"));
        Assert.Equal("36,0 %", KpiValue(page, "conversion"));
    }

    [Fact]
    public async Task An_expired_session_goes_back_to_the_login()
    {
        using var harness = PanelHarness.Create(MetricsStub.Populated);
        harness.Stub.Failures["/api/metrics/summary"] = () => FakeBackend.Raw(HttpStatusCode.Unauthorized, null);

        var page = await harness.OpenAsync();

        page.ShouldRedirectToLogin();
    }

    [Fact]
    public async Task An_anonymous_visitor_is_sent_to_the_login_with_the_panel_as_the_way_back()
    {
        using var harness = PanelHarness.Create(MetricsStub.Populated);
        using var session = harness.Host.NewSession();

        var page = await session.GetAsync("/panel?period=7d");

        Assert.Equal("/panel?period=7d", page.ShouldRedirectToLogin());
        Assert.Empty(harness.Stub.MetricCalls);
    }

    // ---- shape ----

    [Fact]
    public async Task The_page_is_one_h1_in_the_shell_with_the_panel_as_the_first_entry_of_the_navigation()
    {
        using var harness = PanelHarness.Create(MetricsStub.Populated);

        var page = await harness.OpenAsync();

        Assert.Single(page.Select("main h1"));
        Assert.Equal("panel", page.Document.QuerySelector(".page")!.GetAttribute("data-page"));
        var nav = page.Select("nav[aria-label] a.nav-link, nav a[href]").Select(a => a.GetAttribute("href")).ToList();
        Assert.Equal("/panel", nav.First());
        Assert.Equal("page", page.Document.QuerySelector("nav a[href='/panel']")!.GetAttribute("aria-current"));
        Assert.Empty(page.Select("[style]"));
        Assert.DoesNotContain(page.Select("script:not([src])"), script => script.TextContent.Contains("fetch("));
        Assert.NotNull(page.FindLogoutForm());
    }

    [Fact]
    public async Task All_the_text_of_the_page_is_spanish()
    {
        using var harness = PanelHarness.Create(MetricsStub.Populated);
        var page = await harness.OpenAsync();

        var leaks = SpanishUi.Leaks(SpanishUi.Extract(page));

        Assert.Empty(leaks);
    }

    [Fact]
    public async Task The_page_never_shows_a_phone_number_nor_leaks_a_token()
    {
        using var harness = PanelHarness.Create(MetricsStub.Populated);
        var page = await harness.OpenAsync();

        Assert.DoesNotMatch(@"\+?57\d{8,}", page.Text);
        Assert.False(PageResponse.ContainsJwt(page.Body));
    }
}

internal static class PlainText
{
    /// <summary>The node's text with the non-breaking spaces the formatting uses written as plain spaces, so a test reads what a person reads.</summary>
    public static string Plain(this IElement element) => element.TextContent.Replace(' ', ' ');

    public static string Plain(this INode node) => (node.TextContent ?? string.Empty).Replace(' ', ' ');
}
