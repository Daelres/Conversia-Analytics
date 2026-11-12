using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Conversia.Application.Classification.Llm;
using Conversia.Infrastructure.Classification.Llm;

namespace Conversia.Infrastructure.UnitTests.Classification;

[CollectionDefinition(nameof(ProcessEnvironmentCollection), DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection;

/// <summary>
/// The SDK reads <c>ANTHROPIC_BASE_URL</c>, <c>ANTHROPIC_AUTH_TOKEN</c>, <c>ANTHROPIC_CUSTOM_HEADERS</c> and the OpenTelemetry variables
/// from the environment (spike review): the client is built so none of them can redirect the prompts and the key, add headers or export
/// prompt content. Runs alone, because it changes process-wide environment variables.
/// </summary>
[Collection(nameof(ProcessEnvironmentCollection))]
public class AnthropicEnvironmentTests
{
    private sealed class Capture : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(AnthropicLlmClientTests.MessageJson(), Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task TheEnvironment_CannotRedirectTheCall_NorAddHeaders_NorChangeTheCredential()
    {
        var variables = new Dictionary<string, string?>
        {
            ["ANTHROPIC_BASE_URL"] = "https://attacker.example.test",
            ["ANTHROPIC_AUTH_TOKEN"] = "token-from-the-environment",
            ["ANTHROPIC_CUSTOM_HEADERS"] = "X-Leak: yes",
            ["ANTHROPIC_OPEN_TELEMETRY"] = "true",
            ["ANTHROPIC_OPEN_TELEMETRY_TRACES_CONTENT_MODE"] = "content",
        };
        var saved = variables.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var (key, value) in variables)
            {
                Environment.SetEnvironmentVariable(key, value);
            }

            var capture = new Capture();
            var sdk = AnthropicLlmClient.CreateSdkClient("configured-test-key", AnthropicLlmClient.OfficialBaseUrl, TimeSpan.FromSeconds(5), 0, new HttpClient(capture));
            var client = new AnthropicLlmClient(sdk, new AnthropicRequestSettings("claude-haiku-5-5", 2048, "low", false));

            await client.CompleteAsync(new LlmRequest("s", "c", "<conversation>\n[#1][C] hola\n</conversation>", new JsonObject { ["type"] = "object" }, new string('a', 64)));

            var request = Assert.Single(capture.Seen);
            Assert.Equal("api.anthropic.com", request.RequestUri!.Host);
            Assert.False(request.Headers.Contains("X-Leak"));
            var credentials = string.Join(' ', request.Headers.Where(h => h.Key is "Authorization" or "x-api-key").SelectMany(h => h.Value));
            Assert.Contains("configured-test-key", credentials);
            Assert.DoesNotContain("token-from-the-environment", credentials);
        }
        finally
        {
            foreach (var (key, value) in saved)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }
}
