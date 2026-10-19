using Conversia.WebhookSimulator.Api;
using Conversia.WebhookSimulator.Cli;
using Conversia.WebhookSimulator.Generation;

namespace Conversia.WebhookSimulator.Commands;

/// <summary>Sends one or all of the scripted scenarios to a branch's webhook, message by message, and says what each should do.</summary>
public sealed class PlayCommand(ConversiaApi api, WebhookSender sender, SimulatorEnvironment environment, TimeProvider time, TextWriter output)
{
    public async Task<int> RunAsync(SimulatorOptions options, CancellationToken cancellationToken)
    {
        var targets = await ChannelTargets.ResolveAsync(api, environment, cancellationToken);
        if (targets.Count == 0)
        {
            throw new UsageException("The workspace has no active WhatsApp number. Run  setup  first.");
        }

        var target = options.Branch is null
            ? targets.FirstOrDefault(t => t.Secret is not null) ?? targets[0]
            : targets.FirstOrDefault(t => t.BranchName.Equals(options.Branch, StringComparison.OrdinalIgnoreCase))
              ?? throw new UsageException($"No active number for branch '{options.Branch}'. Branches with a number: {string.Join(", ", targets.Select(t => t.BranchName))}.");
        var secret = target.Secret ?? throw new UsageException(
            $"No webhook secret for {target.BranchName}: set {SimulatorEnvironment.SecretsVariable}='{target.BranchName}=<secret>' (or {SimulatorEnvironment.SecretVariable}). See docs/SIMULATOR.md.");

        var context = new ScenarioContext(target.Identity, target.BranchName, time.GetUtcNow().UtcDateTime, options.Seed);
        var deliveries = Scenarios.Build(options.Scenario, context);
        output.WriteLine($"Scenario '{options.Scenario}' to {target.BranchName}: {deliveries.Count} deliveries.");
        for (var i = 0; i < deliveries.Count; i++)
        {
            await sender.SendAsync(target.WebhookPath, secret, deliveries[i].Json, cancellationToken);
            output.WriteLine($"  [{i + 1}/{deliveries.Count}] 200  {deliveries[i].Description}");
            output.WriteLine($"          expected: {deliveries[i].Expected}");
        }

        output.WriteLine();
        output.WriteLine("Sent. The conversations show up within seconds; their stage is decided once a chat has been quiet for a minute (Classification:DebounceSeconds).");
        output.WriteLine("Open the dashboard at /panel and the list at /conversaciones.");
        return 0;
    }
}
