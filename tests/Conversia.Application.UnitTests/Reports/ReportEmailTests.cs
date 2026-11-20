using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Conversia.Application.Reports;

namespace Conversia.Application.UnitTests.Reports;

/// <summary>The report email (docs/REPORTS.md, "Email"): accessible, self-contained, with a plain-text part and a one-click unsubscribe.</summary>
public class ReportEmailTests
{
    private const string ReportUrl = "https://app.conversia.test/informes/7b0c";
    private const string UnsubscribeUrl = "https://app.conversia.test/informes/baja?t=abc";
    private const string OneClickUrl = "https://app.conversia.test/informes/baja/un-clic?t=abc";

    private static EmailMessage Compose(ReportContent? content = null)
        => ReportEmailComposer.Compose("gerente@example.test", "Láser Piloto", content ?? TemplateReportWriter.Write(ReportPacks.Sample()), ReportUrl, UnsubscribeUrl, OneClickUrl);

    [Fact]
    public void The_html_loads_nothing_from_outside_and_links_only_to_the_report_and_the_unsubscribe()
    {
        var html = Compose().HtmlBody;

        Assert.StartsWith("<!doctype html>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lang=\"es\"", html);
        foreach (var forbidden in new[] { "<img", "<script", "src=", "<link", "@import", "url(", "<iframe", "<form", "<style" })
        {
            Assert.DoesNotContain(forbidden, html, StringComparison.OrdinalIgnoreCase);
        }

        var links = Regex.Matches(html, "https?://[^\"'<> ]+").Select(m => m.Value.Replace("&amp;", "&")).Distinct().Order().ToList();
        Assert.Equal(new[] { UnsubscribeUrl, ReportUrl }.Order(), links);
        Assert.Contains("Ver el informe completo", html);
        Assert.Contains("Dejar de recibir este informe", html);
    }

    [Fact]
    public void Every_text_of_the_report_is_encoded()
    {
        var content = new ReportContent("Resumen <b>", [new ReportIndicator("Conversión", "23,5 %", "Nota & <script>")], "Diagnóstico \"x\"", ["Revisar <i>"]);

        var html = Compose(content).HtmlBody;

        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<b>", html);
        Assert.Contains("Nota &amp; &lt;script&gt;", html);
    }

    [Fact]
    public void The_plain_text_part_has_the_whole_report_and_both_links()
    {
        var content = TemplateReportWriter.Write(ReportPacks.Sample());
        var text = Compose(content).TextBody;

        Assert.Contains(content.Title, text);
        Assert.All(content.Actions, action => Assert.Contains(action, text));
        Assert.Contains(content.Diagnosis, text);
        Assert.Contains(ReportUrl, text);
        Assert.Contains(UnsubscribeUrl, text);
        Assert.DoesNotContain("<", text);
    }

    [Fact]
    public void The_headers_offer_a_one_click_unsubscribe_and_the_subject_is_one_line()
    {
        var content = TemplateReportWriter.Write(ReportPacks.Sample()) with { Title = "Resumen\r\nBcc: otro@example.test" };
        var message = Compose(content);

        Assert.Equal($"<{OneClickUrl}>", message.Headers["List-Unsubscribe"]);
        Assert.Equal("List-Unsubscribe=One-Click", message.Headers["List-Unsubscribe-Post"]);
        Assert.DoesNotContain('\n', message.Subject);
        Assert.DoesNotContain('\r', message.Subject);
        Assert.Contains("Láser Piloto", message.Subject);
        Assert.Equal("gerente@example.test", message.ToAddress);
        Assert.DoesNotContain("gerente", message.ToString());
    }

    [Fact]
    public void An_unsubscribe_token_names_its_recipient_and_cannot_be_forged()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var recipient = Guid.NewGuid();

        var token = ReportUnsubscribeToken.Create(recipient, key);

        Assert.Matches("^[A-Za-z0-9_-]+$", token);
        Assert.True(ReportUnsubscribeToken.TryRead(token, key, out var read));
        Assert.Equal(recipient, read);
        Assert.False(ReportUnsubscribeToken.TryRead(token, RandomNumberGenerator.GetBytes(32), out _));
        Assert.False(ReportUnsubscribeToken.TryRead(ReportUnsubscribeToken.Create(Guid.NewGuid(), key)[..22] + token[22..], key, out _));
        Assert.False(ReportUnsubscribeToken.TryRead(token[..^1] + (token[^1] == 'A' ? 'B' : 'A'), key, out _));
        Assert.False(ReportUnsubscribeToken.TryRead("no-es-un-token", key, out _));
        Assert.False(ReportUnsubscribeToken.TryRead(null, key, out _));
        Assert.False(ReportUnsubscribeToken.TryRead(new string('A', 5000), key, out _));
    }
}
