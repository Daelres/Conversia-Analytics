using Conversia.Domain.Common;

namespace Conversia.Domain.UnitTests.Entities;

/// <summary>The link check the assistant's texts go through (docs/ASSISTANT.md, "Knowledge base"): every bypass found in review is refused.</summary>
public class LinkRulesTests
{
    private const string Allowed = "reservas.ejemplo.com";

    [Theory]
    [InlineData("Reserva en https://reservas.ejemplo.com/cita")]
    [InlineData("Reserva en https://citas.reservas.ejemplo.com")]
    [InlineData("Entra a reservas.ejemplo.com/agenda")]
    [InlineData("Escríbenos a info@ejemplo.com")]
    [InlineData("La sesión cuesta 120.000 y dura 1.5 horas")]
    public void Allowed_links_and_plain_text_pass(string text) => Assert.True(LinkRules.OnlyLinksTo(text, Allowed));

    [Theory]
    [InlineData("https://reservas.ejemplo.com@evil.com/pago")]
    [InlineData("https://evil.com\\@reservas.ejemplo.com")]
    [InlineData("https://reservas.ejemplo.com%2F@evil.com")]
    [InlineData("https://evil.com%2Freservas.ejemplo.com")]
    [InlineData("https://reservas.ejemplo.com.evil.com/")]
    [InlineData("http://user:pass@reservas.ejemplo.com")]
    [InlineData("Paga en rb.gy/abc")]
    [InlineData("Paga en is.gd/abc")]
    [InlineData("Mira x.dev")]
    [InlineData("Mira pagos.ru/x")]
    [InlineData("[reserva aquí](https://evil.com)")]
    [InlineData("[reserva aquí](evil)")]
    [InlineData("https://rеservas.ejemplo.com")]
    [InlineData("https://xn--rservas-v5a.ejemplo.com")]
    [InlineData("hxxps://evil.com")]
    [InlineData("Paga en evil.com:8080/x")]
    public void Bypasses_are_refused(string text) => Assert.False(LinkRules.OnlyLinksTo(text, Allowed));

    [Fact]
    public void An_idn_allowed_domain_matches_its_punycode_form()
    {
        Assert.True(LinkRules.OnlyLinksTo("Reserva en https://xn--peluquera-n5a.com.co/cita", LinkRules.AsciiDomain("peluquería.com.co")));
        Assert.True(LinkRules.OnlyLinksTo("Reserva en https://peluquería.com.co/cita", LinkRules.AsciiDomain("peluquería.com.co")));
    }

    [Theory]
    [InlineData("co.uk")]
    [InlineData("com.co")]
    [InlineData("linktr.ee")]
    [InlineData("bit.ly")]
    [InlineData("misitio.github.io")]
    [InlineData("tienda.wixsite.com")]
    [InlineData("wa.me")]
    public void Shared_hosts_and_public_suffixes_are_not_an_allowed_domain(string domain) => Assert.False(LinkRules.IsAllowableDomain(domain));

    [Theory]
    [InlineData("reservas.ejemplo.com")]
    [InlineData("miempresa.com.co")]
    public void A_company_domain_is_allowable(string domain) => Assert.True(LinkRules.IsAllowableDomain(domain));
}
