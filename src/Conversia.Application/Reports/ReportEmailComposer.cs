namespace Conversia.Application.Reports;

/// <summary>
/// The email of one report to one person (docs/REPORTS.md, "Email"): a one-line subject, the HTML and plain-text bodies with the link to the
/// report page and the person's own unsubscribe link, and the RFC 8058 one-click unsubscribe headers.
/// </summary>
public static class ReportEmailComposer
{
    public const int SubjectMaxLength = 200;

    public static EmailMessage Compose(
        string toAddress, string company, ReportContent content, string reportUrl, string unsubscribeUrl, string oneClickUnsubscribeUrl, string? messageIdLocalPart = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        var subject = OneLine($"{company}: {content.Title}");
        if (subject.Length > SubjectMaxLength)
        {
            subject = subject[..(SubjectMaxLength - 1)].TrimEnd() + "…";
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["List-Unsubscribe"] = $"<{oneClickUnsubscribeUrl}>",
            ["List-Unsubscribe-Post"] = "List-Unsubscribe=One-Click",
        };
        return new EmailMessage(
            toAddress, subject, ReportRenderer.Html(content, reportUrl, unsubscribeUrl), ReportRenderer.Text(content, reportUrl, unsubscribeUrl), headers, messageIdLocalPart);
    }

    // A header is one line: a line break in a title could otherwise add headers (Bcc:) to the message.
    private static string OneLine(string text) => string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
