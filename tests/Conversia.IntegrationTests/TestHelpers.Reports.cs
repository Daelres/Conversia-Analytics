using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Conversia.Application.Classification.Llm;
using Conversia.Application.DTOs.Reports;
using Conversia.Application.Messaging;
using Conversia.Application.Reports;
using Conversia.Infrastructure.Persistence;
using Conversia.Infrastructure.Reports;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Conversia.IntegrationTests;

/// <summary>Helpers of the manager reports tests: a host whose email and WhatsApp providers are stand-ins, and optionally a scripted model.</summary>
internal static partial class TestHelpers
{
    internal const string ReportsUrl = "/api/reports";

    /// <summary>Stands in for the email provider: records every message and answers what a test set for its address.</summary>
    internal sealed class RecordingEmailSender : IEmailSender
    {
        private readonly ConcurrentQueue<EmailMessage> _sent = new();
        private readonly ConcurrentDictionary<string, Func<EmailSendResult>> _answers = new(StringComparer.OrdinalIgnoreCase);

        public void Answer(string address, Func<EmailSendResult> answer) => _answers[address] = answer;

        public IReadOnlyList<EmailMessage> SentTo(string address) => _sent.Where(m => string.Equals(m.ToAddress, address, StringComparison.OrdinalIgnoreCase)).ToList();

        public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            var result = _answers.TryGetValue(message.ToAddress, out var answer) ? answer() : EmailSendResult.Sent($"<{Guid.NewGuid():N}@test>");
            if (result.Status == EmailSendStatus.Sent)
            {
                _sent.Enqueue(message);
            }

            return Task.FromResult(result);
        }
    }

    /// <summary>A model that answers what the test scripted, per call; it records what it was asked.</summary>
    internal sealed class ScriptedLlmClient : ILlmClient
    {
        private readonly ConcurrentQueue<Func<LlmCompletion>> _answers = new();

        public ConcurrentQueue<LlmRequest> Requests { get; } = new();

        public Func<LlmCompletion> Default { get; set; } = () => throw new LlmCallException(LlmFailureKind.Unavailable, "Scripted");

        public void Enqueue(Func<LlmCompletion> answer) => _answers.Enqueue(answer);

        public Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request);
            return Task.FromResult((_answers.TryDequeue(out var answer) ? answer : Default)());
        }
    }

    internal sealed record ReportsHost(WebApplicationFactory<Program> Host, RecordingEmailSender Email, FakeMessageSender WhatsApp, ScriptedLlmClient? Llm)
    {
        public ReportJobRunner Runner => Host.Services.GetRequiredService<ReportJobRunner>();

        public HttpClient ClientOf(TestActor actor)
        {
            var client = Host.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", actor.Token);
            return client;
        }
    }

    /// <summary>The shared database behind a host whose email, WhatsApp and (with <paramref name="withModel"/>) model are stand-ins.</summary>
    internal static ReportsHost ReportsHostOf(CustomWebApplicationFactory factory, bool withModel = false, string name = "reports", Dictionary<string, string>? settings = null)
        => factory.GetOrAddHost(name + (withModel ? "-llm" : string.Empty), () =>
        {
            var email = new RecordingEmailSender();
            var whatsApp = new FakeMessageSender();
            var llm = withModel ? new ScriptedLlmClient() : null;
            var host = factory.WithWebHostBuilder(builder =>
            {
                builder.UseSetting(EncryptionKeySetting, TestEncryptionKey);
                builder.UseSetting("Reports:WebBaseUrl", "https://app.conversia.test");
                foreach (var (key, value) in settings ?? [])
                {
                    builder.UseSetting(key, value);
                }

                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IEmailSender>();
                    services.AddSingleton<IEmailSender>(email);
                    services.RemoveAll<IWhatsAppMessageSenderResolver>();
                    services.AddSingleton<IWhatsAppMessageSenderResolver>(whatsApp);
                    services.RemoveAll<IWhatsAppTemplateSource>();
                    services.AddSingleton<IWhatsAppTemplateSource>(whatsApp);
                    if (llm is not null)
                    {
                        services.RemoveAll<ILlmClient>();
                        services.AddSingleton<ILlmClient>(llm);
                    }
                });
            });
            return new ReportsHost(host, email, whatsApp, llm);
        });

    internal static object DefinitionBody(
        Guid recipient, bool email = true, bool whatsApp = false, string frequency = "Weekly", string[]? sections = null, Guid[]? branches = null,
        Guid? channelId = null, string? template = null, string? language = null, string name = "Resumen semanal", int hour = 7, int day = 1, params Guid[] more)
        => new
        {
            name, isActive = true, frequency, sendDayOfWeek = day, sendHour = hour,
            branchIds = branches ?? [],
            sections = sections ?? ["Headline", "Conversion", "DropOff", "LossReasons", "Response", "AiCoverage"],
            recipients = new[] { recipient }.Concat(more).Select(u => new { userId = u, email, whatsApp }).ToArray(),
            whatsAppChannelId = channelId, whatsAppTemplateName = template, whatsAppTemplateLanguage = language,
        };

    internal static async Task<ReportDefinitionResponse> CreateDefinitionAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync($"{ReportsUrl}/definitions", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ReportDefinitionResponse>())!;
    }

    /// <summary>The definition existed long before: every occurrence of the tests' periods is after it.</summary>
    internal static async Task BackdateDefinitionAsync(IServiceProvider services, Guid definitionId)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.ExecuteSqlAsync($"""UPDATE "ReportDefinitions" SET "CreatedAt" = TIMESTAMPTZ '2020-01-01 00:00:00+00' WHERE "Id" = {definitionId}""");
    }

    internal static async Task SetTenantFlagsAsync(IServiceProvider services, Guid tenantId, bool? sandbox = null, bool? aiEnabled = null)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (sandbox is { } s)
        {
            await db.Database.ExecuteSqlAsync($"""UPDATE "Tenants" SET "SandboxMode" = {s} WHERE "Id" = {tenantId}""");
        }

        if (aiEnabled is { } ai)
        {
            await db.Database.ExecuteSqlAsync($"""UPDATE "Tenants" SET "AiEnabled" = {ai} WHERE "Id" = {tenantId}""");
        }
    }

    /// <summary>The weekly occurrence (Monday 07:00 Bogota) that is due now, and an instant a minute after it.</summary>
    internal static (ReportPeriod Period, DateTime RunAt) CurrentWeeklyOccurrence()
    {
        var occurrence = ReportSchedule.Latest(Domain.Enums.ReportFrequency.Weekly, 1, 7, DateTime.UtcNow, ReportSchedule.Bogota);
        return (occurrence.Period, occurrence.DueUtc.AddMinutes(1));
    }

    internal static DateTime NoonOf(DateOnly day) => day.ToDateTime(new TimeOnly(17, 0), DateTimeKind.Utc); // 12:00 in Bogota
}
