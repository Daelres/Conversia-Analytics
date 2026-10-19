extern alias ApiHost;

using System.Net;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Pages;

/// <summary>The messages the requirement words exactly (rate-limit seconds, the two different 404 pages) and the shape of the Spanish seconds.</summary>
[Collection(nameof(WebIntegrationCollection))]
public class SpanishMessagesTests(TestApiFactory api)
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(11)]
    [InlineData(37)]
    [InlineData(60)]
    [InlineData(100)]
    public async Task A_rate_limited_login_says_demasiados_intentos_and_the_retry_seconds(int seconds)
    {
        using var host = WebTestHost.Create(api);
        host.Api.Intercept = _ => FakeApi.TooManyRequests(seconds);
        using var session = host.NewSession();

        var response = await WebFlows.LoginAsync(session, "alguien@example.test", "Wr0ng-Pass-Value-123");

        response.ShouldBe(HttpStatusCode.OK);
        Assert.Contains(SpanishText.TooManyAttempts, response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(SpanishText.RetrySeconds(seconds), response.Text);
        if (seconds == 1)
        {
            Assert.DoesNotContain("1 segundos", response.Text, StringComparison.OrdinalIgnoreCase);
        }

        WebFlows.AssertNoCookieIssued(session, response);
    }

    [Fact]
    public async Task A_rate_limited_registration_says_demasiados_intentos_and_the_retry_seconds()
    {
        using var host = WebTestHost.Create(api);
        host.Api.Intercept = _ => FakeApi.TooManyRequests(1);
        using var session = host.NewSession();

        var response = await WebFlows.RegisterAsync(session, "Ferretería", ApiSeed.UniqueEmail("nuevo"), ApiSeed.StrongPassword);

        Assert.Contains(SpanishText.TooManyAttempts, response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(SpanishText.RetrySeconds(1), response.Text);
        WebFlows.AssertNoCookieIssued(session, response);
    }

    [Fact]
    public async Task A_rate_limit_without_a_retry_after_header_still_says_demasiados_intentos_and_names_no_number_of_seconds()
    {
        using var host = WebTestHost.Create(api);
        host.Api.Intercept = _ => FakeApi.Respond(HttpStatusCode.TooManyRequests, """{"title":"x","status":429}""");
        using var session = host.NewSession();

        var response = await WebFlows.LoginAsync(session, "alguien@example.test", "Wr0ng-Pass-Value-123");

        Assert.Contains(SpanishText.TooManyAttempts, response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"\d+\s+segundos?\b", response.Text); // "unos segundos" is fine; a made-up number of seconds is not
    }

    [Theory]
    [InlineData(1, "11 segundos", false)]
    [InlineData(1, "1 segundo.", true)]
    [InlineData(1, "en 1 segundos", false)]
    [InlineData(60, "60 segundos", true)]
    [InlineData(60, "160 segundos", false)]
    [InlineData(2, "2 segundo", false)]
    public void The_helper_that_reads_the_seconds_does_not_confuse_one_with_eleven_or_singular_with_plural(int seconds, string text, bool expected)
        => Assert.Equal(expected, SpanishText.RetrySeconds(seconds).IsMatch(text));

    [Fact]
    public async Task The_page_of_an_unknown_route_does_not_claim_a_conversation_was_not_found_and_the_conversation_page_does()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        var unknownRoute = await session.GetAsync("/esta-pagina-no-existe");
        var unknownConversation = await session.GetAsync($"/conversaciones/{Guid.NewGuid()}");

        unknownRoute.ShouldBe(HttpStatusCode.NotFound);
        unknownConversation.ShouldBe(HttpStatusCode.NotFound);
        Assert.DoesNotContain(SpanishText.ConversationNotFound, unknownRoute.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(SpanishText.ConversationNotFound, unknownConversation.Text, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(unknownRoute.Document.Title));
        Assert.False(string.IsNullOrWhiteSpace(unknownConversation.Document.Title));
    }

    [Fact]
    public async Task The_error_page_is_spanish_and_shows_no_exception_detail()
    {
        using var host = WebTestHost.Create(api);
        using var session = host.NewSession();

        var page = await session.GetAsync("/Error");

        var report = new SpanishUiReport();
        report.Check("error page", page);
        report.AssertClean();
        WebFlows.AssertNoStackTrace(page);
    }
}
