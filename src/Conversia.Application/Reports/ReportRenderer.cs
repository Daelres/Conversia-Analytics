using System.Net;
using System.Text;

namespace Conversia.Application.Reports;

/// <summary>
/// The email body of a report (docs/REPORTS.md, "Email"): a self-contained HTML document (inline styles only: no images, fonts, scripts,
/// trackers or anything loaded from outside, readable without them) and its plain-text twin. Every text is HTML-encoded. The links are
/// per recipient, so the stored copy (<c>ManagerReports.Html</c>) is rendered without them.
/// </summary>
public static class ReportRenderer
{
    private const string Body = "margin:0;padding:24px 16px;background:#ffffff;color:#1f2933;font-family:Arial,Helvetica,sans-serif;font-size:16px;line-height:1.5";

    public static string Html(ReportContent content, string? reportUrl = null, string? unsubscribeUrl = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"es\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
            .Append("<title>").Append(E(content.Title)).Append("</title></head>")
            .Append("<body style=\"").Append(Body).Append("\"><main style=\"max-width:600px;margin:0 auto\">")
            .Append("<h1 style=\"font-size:20px;margin:0 0 16px\">").Append(E(content.Title)).Append("</h1>");

        html.Append("<ul style=\"list-style:none;padding:0;margin:0 0 16px\">");
        foreach (var indicator in content.Indicators)
        {
            html.Append("<li style=\"border:1px solid #d9e2ec;border-radius:8px;padding:12px;margin:0 0 8px\">")
                .Append("<span style=\"display:block;font-size:14px;color:#52606d\">").Append(E(indicator.Label)).Append("</span>")
                .Append("<strong style=\"display:block;font-size:22px\">").Append(E(indicator.Value)).Append("</strong>")
                .Append("<span style=\"display:block;font-size:14px\">").Append(E(indicator.Note)).Append("</span></li>");
        }

        html.Append("</ul><h2 style=\"font-size:17px;margin:16px 0 8px\">Diagnóstico</h2><p style=\"margin:0 0 16px\">").Append(E(content.Diagnosis)).Append("</p>")
            .Append("<h2 style=\"font-size:17px;margin:16px 0 8px\">Qué hacer</h2><ol style=\"margin:0 0 16px;padding-left:20px\">");
        foreach (var action in content.Actions)
        {
            html.Append("<li style=\"margin:0 0 6px\">").Append(E(action)).Append("</li>");
        }

        html.Append("</ol>");
        if (content.CoverageNote is { } note)
        {
            html.Append("<p style=\"font-size:14px;color:#52606d;margin:0 0 16px\">").Append(E(note)).Append("</p>");
        }

        if (reportUrl is not null)
        {
            html.Append("<p style=\"margin:24px 0\"><a href=\"").Append(E(reportUrl))
                .Append("\" style=\"color:#0b5cad;font-weight:bold\">Ver el informe completo</a></p>");
        }

        if (unsubscribeUrl is not null)
        {
            html.Append("<p style=\"font-size:13px;color:#52606d;border-top:1px solid #d9e2ec;padding-top:12px\">Recibes este informe porque tu empresa te incluyó en él. ")
                .Append("<a href=\"").Append(E(unsubscribeUrl)).Append("\" style=\"color:#52606d\">Dejar de recibir este informe</a></p>");
        }

        return html.Append("</main></body></html>").ToString();
    }

    public static string Text(ReportContent content, string? reportUrl = null, string? unsubscribeUrl = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        var text = new StringBuilder();
        text.Append(Plain(content.Title)).Append("\n\n");
        foreach (var indicator in content.Indicators)
        {
            text.Append("- ").Append(Plain(indicator.Label)).Append(": ").Append(Plain(indicator.Value)).Append(". ").Append(Plain(indicator.Note)).Append('\n');
        }

        text.Append("\nDiagnóstico\n").Append(Plain(content.Diagnosis)).Append("\n\nQué hacer\n");
        for (var i = 0; i < content.Actions.Count; i++)
        {
            text.Append(i + 1).Append(". ").Append(Plain(content.Actions[i])).Append('\n');
        }

        if (content.CoverageNote is { } note)
        {
            text.Append('\n').Append(Plain(note)).Append('\n');
        }

        if (reportUrl is not null)
        {
            text.Append("\nVer el informe completo: ").Append(reportUrl).Append('\n');
        }

        if (unsubscribeUrl is not null)
        {
            text.Append("\nDejar de recibir este informe: ").Append(unsubscribeUrl).Append('\n');
        }

        return text.ToString();
    }

    private static string E(string text) => WebUtility.HtmlEncode(text);

    // The text part carries no markup: angle brackets a text may hold are written as the guillemets a reader sees anyway.
    private static string Plain(string text) => text.Replace('<', '‹').Replace('>', '›');
}
