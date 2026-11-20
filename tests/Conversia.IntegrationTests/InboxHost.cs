using System.Collections.Concurrent;
using System.Data.Common;
using System.Net.Http.Headers;
using Conversia.Application.Messaging;
using Conversia.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using static Conversia.IntegrationTests.TestHelpers;

namespace Conversia.IntegrationTests;

/// <summary>
/// A host of the shared database that records the SQL it sends, with its parameters, so a test can say what a request read and ask
/// PostgreSQL how it would run it (<see cref="CommandRecorder.ExplainAsync"/>). Nothing reaches 360dialog: the provider is the fake.
/// </summary>
internal sealed class InboxHost
{
    private InboxHost(WebApplicationFactory<Program> host, CommandRecorder recorder)
    {
        Host = host;
        Recorder = recorder;
    }

    public WebApplicationFactory<Program> Host { get; }

    public CommandRecorder Recorder { get; }

    public HttpClient ClientWith(string token)
    {
        var client = Host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static readonly ConcurrentDictionary<CustomWebApplicationFactory, InboxHost> Hosts = new();

    public static InboxHost Of(CustomWebApplicationFactory factory) => Hosts.GetOrAdd(factory, f =>
    {
        var recorder = new CommandRecorder();
        var sender = new FakeMessageSender();
        var host = f.WithWebHostBuilder(builder =>
        {
            builder.UseSetting(EncryptionKeySetting, TestEncryptionKey);
            builder.ConfigureServices(services =>
            {
                services.ConfigureDbContext<ApplicationDbContext>(o => o.AddInterceptors(recorder));
                services.RemoveAll<IWhatsAppMessageSenderResolver>();
                services.AddSingleton<IWhatsAppMessageSenderResolver>(sender);
                services.RemoveAll<IWhatsAppTemplateSource>();
                services.AddSingleton<IWhatsAppTemplateSource>(sender);
            });
        });
        return new InboxHost(host, recorder);
    });

    internal sealed class CommandRecorder : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<(string Text, (string Name, object? Value)[] Parameters)> _commands = new();

        public IReadOnlyList<string> Texts => _commands.Select(c => c.Text).ToList();

        public void Clear() => _commands.Clear();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            _commands.Enqueue((command.CommandText, command.Parameters.Cast<DbParameter>().Select(p => (p.ParameterName, (object?)p.Value)).ToArray()));
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        /// <summary>
        /// The plans of the recorded commands that <paramref name="match"/>, with sequential scans discouraged: on the test's small tables
        /// PostgreSQL would scan anyway, so this proves an index <b>can</b> serve the query (the plan of a large table), not what a tiny
        /// table gets.
        /// </summary>
        public async Task<IReadOnlyList<string>> ExplainAsync(IServiceProvider services, Func<string, bool> match)
        {
            string connectionString;
            using (var scope = services.CreateScope())
            {
                connectionString = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.GetConnectionString()!;
            }

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using (var off = new NpgsqlCommand("SET enable_seqscan = off; SET enable_sort = off;", connection))
            {
                await off.ExecuteNonQueryAsync();
            }

            var plans = new List<string>();
            foreach (var (text, parameters) in _commands.Where(c => match(c.Text)).DistinctBy(c => c.Text))
            {
                await using var explain = new NpgsqlCommand("EXPLAIN " + text, connection);
                foreach (var (name, value) in parameters)
                {
                    explain.Parameters.Add(new NpgsqlParameter(name, value ?? DBNull.Value));
                }

                var lines = new List<string>();
                await using var reader = await explain.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    lines.Add(reader.GetString(0));
                }

                plans.Add(string.Join('\n', lines));
            }

            return plans;
        }
    }
}
