extern alias ApiHost;

using System.Net;
using Conversia.Web.IntegrationTests.Dashboard;
using Conversia.Web.IntegrationTests.Infrastructure;
using Conversia.Web.Services;
using Microsoft.Extensions.Configuration;

namespace Conversia.Web.IntegrationTests.Pending;

/// <summary>The optional <c>Support:ContactText</c>: how a person whose company is pending or suspended reaches the team.</summary>
[Collection(nameof(WebIntegrationCollection))]
public class SupportContactTests(TestApiFactory api)
{
    private const string Contact = "Escríbenos a soporte@empresa.example o al +57 300 000 0000";

    private static IConfiguration Config(string? value)
        => new ConfigurationBuilder().AddInMemoryCollection([new KeyValuePair<string, string?>(SupportOptions.ContactTextKey, value)]).Build();

    // ---- the option ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_or_blank_means_no_contact(string? value)
        => Assert.Null(SupportOptions.FromConfiguration(Config(value)).ContactText);

    [Fact]
    public void A_text_is_trimmed_and_kept_as_it_is()
        => Assert.Equal(Contact, SupportOptions.FromConfiguration(Config($"  {Contact}  ")).ContactText);

    [Fact]
    public void Three_hundred_characters_are_the_limit()
    {
        Assert.Equal(300, SupportOptions.FromConfiguration(Config(new string('a', 300))).ContactText!.Length);

        var failure = Assert.Throws<InvalidOperationException>(() => SupportOptions.FromConfiguration(Config(new string('a', 301))));

        Assert.Contains("Support:ContactText", failure.Message);
        Assert.DoesNotContain("aaaa", failure.Message);
    }

    [Theory]
    [InlineData("primera\nsegunda")]
    [InlineData("con\ttabulador")]
    [InlineData("nulo\0")]
    public void Control_characters_are_refused_naming_the_key(string value)
    {
        var failure = Assert.Throws<InvalidOperationException>(() => SupportOptions.FromConfiguration(Config(value)));

        Assert.Contains("Support:ContactText", failure.Message);
        Assert.DoesNotContain("primera", failure.Message);
    }

    [Fact]
    public void The_web_refuses_to_start_with_an_invalid_text_and_starts_with_a_good_one_or_none()
    {
        Exception? Boot(string? value)
        {
            using var host = WebTestHost.CreateWithoutApi(WebSettings.With((SupportOptions.ContactTextKey, value)));
            return Record.Exception(() => host.NewSession());
        }

        var refused = Boot(new string('x', 301));

        Assert.NotNull(refused);
        Assert.NotNull(ExceptionChain.FindRefusal(refused!, "Support:ContactText"));
        Assert.Null(Boot(Contact));
        Assert.Null(Boot(null));
    }

    // ---- the page ----

    [Theory]
    [InlineData("Pending")]
    [InlineData("Suspended")]
    public async Task The_pending_and_suspended_pages_show_the_contact_when_it_is_configured(string status)
    {
        var admin = await ApiSeed.CreateAdminAsync(api);
        var owner = await ApiSeed.RegisterOwnerAsync(api, "ferretería", pending: status == "Pending");
        if (status == "Suspended")
        {
            await ApiSeed.SetStatusAsync(api, admin, owner.TenantId, "Suspended");
        }

        using var host = WebTestHost.Create(api, settings: WebSettings.With((SupportOptions.ContactTextKey, Contact)));
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        var page = (await session.GetAsync("/cuenta-pendiente")).ShouldBe(HttpStatusCode.OK);

        Assert.Equal(status, page.Document.QuerySelector("[data-page=account-state]")!.GetAttribute("data-status"));
        Assert.Equal(Contact, page.Document.QuerySelector("[data-testid=support-contact-text]")!.Plain().Trim());
        Assert.Equal("Cómo contactar al equipo", page.Document.QuerySelector("[data-testid=support-contact] h2")!.Plain().Trim());
    }

    [Fact]
    public async Task Without_a_contact_the_page_has_no_contact_card()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api, "droguería", pending: true);
        using var host = WebTestHost.Create(api);
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        var page = (await session.GetAsync("/cuenta-pendiente")).ShouldBe(HttpStatusCode.OK);

        Assert.Empty(page.Select("[data-testid=support-contact]"));
    }

    [Fact]
    public async Task The_contact_is_plain_text_never_markup()
    {
        const string hostile = "<script>alert(1)</script> <a href=\"https://evil.example\">aquí</a> & soporte";
        var owner = await ApiSeed.RegisterOwnerAsync(api, "papelería", pending: true);
        using var host = WebTestHost.Create(api, settings: WebSettings.With((SupportOptions.ContactTextKey, hostile)));
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        var page = (await session.GetAsync("/cuenta-pendiente")).ShouldBe(HttpStatusCode.OK);

        var element = page.Document.QuerySelector("[data-testid=support-contact-text]")!;
        Assert.Equal(hostile, element.TextContent);
        Assert.Empty(element.QuerySelectorAll("script, a"));
        Assert.DoesNotContain("<script>alert(1)", page.Body);
        Assert.Contains("&lt;script&gt;", page.Body);
    }

    [Fact]
    public async Task The_contact_never_appears_on_the_operating_pages_of_an_active_company()
    {
        var owner = await ApiSeed.RegisterOwnerAsync(api, "panadería");
        using var host = WebTestHost.Create(api, settings: WebSettings.With((SupportOptions.ContactTextKey, Contact)));
        using var session = await WebFlows.SignedInAsync(host, owner.Email);

        var panel = (await session.GetAsync("/conversaciones")).ShouldBe(HttpStatusCode.OK);

        Assert.DoesNotContain("soporte@empresa.example", panel.Body);
    }

    // ---- the deployment ----

    [Fact]
    public void The_compose_file_hands_the_contact_to_the_web()
    {
        var compose = File.ReadAllText(Path.Combine(RepoPaths.Root(), "docker-compose.yml"));

        Assert.Contains("Support__ContactText: ${SUPPORT_CONTACT_TEXT:-}", compose);
    }
}
