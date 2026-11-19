using System.Text.Json;
using Conversia.Application.Reports;
using Conversia.Infrastructure.Email;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conversia.IntegrationTests;

/// <summary>A throw-away SMTP server (smtp4dev) offering STARTTLS with a self-signed certificate, and one that offers no TLS at all.</summary>
public sealed class SmtpServersFixture : IAsyncLifetime
{
    private const string Image = "rnwood/smtp4dev:3.8.6";

    public IContainer Tls { get; } = Build("StartTls");

    public IContainer Plain { get; } = Build("None");

    private static IContainer Build(string tlsMode) => new ContainerBuilder(Image)
        .WithEnvironment("ServerOptions__TlsMode", tlsMode)
        .WithEnvironment("ServerOptions__HostName", "localhost")
        .WithPortBinding(25, assignRandomHostPort: true)
        .WithPortBinding(80, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(80).ForPath("/api/server")))
        .Build();

    public async Task InitializeAsync() => await Task.WhenAll(Tls.StartAsync(), Plain.StartAsync());

    public async Task DisposeAsync()
    {
        await Tls.DisposeAsync();
        await Plain.DisposeAsync();
    }
}

/// <summary>
/// The SMTP adapter against a real SMTP server in a container (docs/REPORTS.md, "Email"): the report email arrives as multipart/alternative with
/// its headers over STARTTLS; a server without TLS is refused (never plain text), and so is a certificate the system does not trust.
/// </summary>
public class SmtpEmailSenderTests(SmtpServersFixture servers) : IClassFixture<SmtpServersFixture>
{
    private static EmailOptions Options(IContainer server) => new(
        EmailOptions.SmtpProvider, "informes@conversia.test", "Conversia", new SmtpOptions(server.Hostname, server.GetMappedPublicPort(25), SmtpOptions.StartTls, null, 15), null);

    private static EmailMessage Message(string to) => ReportEmailComposer.Compose(
        to, "Láser Piloto", new ReportContent("Resumen semanal", [new ReportIndicator("Conversión", "23,5 %", "Bajó.")], "Diagnóstico.", ["Revisar el precio."]),
        "https://app.conversia.test/informes/1", "https://app.conversia.test/informes/baja?t=x", "https://app.conversia.test/informes/baja/un-clic?t=x");

    private static async Task<List<JsonElement>> MessagesAsync(IContainer server)
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://{server.Hostname}:{server.GetMappedPublicPort(80)}") };
        var json = await http.GetStringAsync("/api/messages?pageSize=50");
        return [.. JsonDocument.Parse(json).RootElement.GetProperty("results").EnumerateArray()];
    }

    private static async Task<string> RawAsync(IContainer server, string id)
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://{server.Hostname}:{server.GetMappedPublicPort(80)}") };
        return await http.GetStringAsync($"/api/messages/{id}/raw");
    }

    [Fact]
    public async Task The_report_email_arrives_over_starttls_as_text_and_html_with_its_unsubscribe_headers()
    {
        var to = $"gerente-{Guid.NewGuid():N}@example.test";
        var sender = new SmtpEmailSender(Options(servers.Tls), null, NullLogger<SmtpEmailSender>.Instance, certificateValidation: (_, _, _, _) => true);

        var result = await sender.SendAsync(Message(to));

        Assert.Equal(EmailSendStatus.Sent, result.Status);
        Assert.False(string.IsNullOrEmpty(result.MessageId));
        var stored = Assert.Single(await MessagesAsync(servers.Tls), m => m.GetProperty("to").EnumerateArray().Any(t => t.GetString()!.Contains(to, StringComparison.Ordinal)));
        var raw = await RawAsync(servers.Tls, stored.GetProperty("id").GetString()!);
        Assert.Contains("List-Unsubscribe: <https://app.conversia.test/informes/baja/un-clic?t=x>", raw);
        Assert.Contains("List-Unsubscribe-Post: List-Unsubscribe=One-Click", raw);
        Assert.Contains("multipart/alternative", raw);
        Assert.Contains("text/plain", raw);
        Assert.Contains("text/html", raw);
        Assert.Contains("informes@conversia.test", raw);
        Assert.Contains($"<{result.MessageId}>", raw);
    }

    [Fact]
    public async Task A_server_without_tls_is_refused_and_nothing_is_sent_in_plain_text()
    {
        var to = $"plano-{Guid.NewGuid():N}@example.test";
        var sender = new SmtpEmailSender(Options(servers.Plain), null, NullLogger<SmtpEmailSender>.Instance, certificateValidation: (_, _, _, _) => true);

        var result = await sender.SendAsync(Message(to));

        Assert.Equal((EmailSendStatus.TransientFailure, "smtp_tls"), (result.Status, result.ErrorCode));
        Assert.DoesNotContain(await MessagesAsync(servers.Plain), m => m.GetProperty("to").EnumerateArray().Any(t => t.GetString()!.Contains(to, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_certificate_the_system_does_not_trust_is_refused_by_default()
    {
        var to = $"cert-{Guid.NewGuid():N}@example.test";
        var sender = new SmtpEmailSender(Options(servers.Tls), null, NullLogger<SmtpEmailSender>.Instance);

        var result = await sender.SendAsync(Message(to));

        Assert.Equal((EmailSendStatus.TransientFailure, "smtp_tls"), (result.Status, result.ErrorCode));
    }

    [Fact]
    public async Task An_unreachable_server_is_a_temporary_failure()
    {
        var options = new EmailOptions(EmailOptions.SmtpProvider, "informes@conversia.test", null, new SmtpOptions("127.0.0.1", 1, SmtpOptions.StartTls, null, 5), null);

        var result = await new SmtpEmailSender(options, null, NullLogger<SmtpEmailSender>.Instance).SendAsync(Message("x@example.test"));

        Assert.Equal((EmailSendStatus.TransientFailure, "smtp_unavailable"), (result.Status, result.ErrorCode));
    }
}
