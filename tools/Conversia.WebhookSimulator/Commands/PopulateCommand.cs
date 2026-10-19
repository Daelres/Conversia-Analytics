using System.Diagnostics;
using Conversia.WebhookSimulator.Api;
using Conversia.WebhookSimulator.Cli;
using Conversia.WebhookSimulator.Generation;

namespace Conversia.WebhookSimulator.Commands;

/// <summary>
/// Fills a workspace with a believable spread of conversations: plans them, sends each through its branch's webhook (a delivery per chat,
/// paced under the webhook limits), waits for the workers to store them and then assigns advisors through the API so the per-advisor
/// numbers exist too. Re-running with the same seed and the same moment is harmless (the messages have fixed ids); a different one adds more.
/// </summary>
public sealed class PopulateCommand(
    ConversiaApi api, WebhookSender sender, SimulatorEnvironment environment, TimeProvider time, TextWriter output, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private const int IdlePollsToStop = 5;

    private int _arrivedCount;

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? ((span, token) => Task.Delay(span, token));

    public async Task<int> RunAsync(SimulatorOptions options, CancellationToken cancellationToken)
    {
        var targets = await ChannelTargets.ResolveAsync(api, environment, cancellationToken);
        var byBranch = targets.ToDictionary(t => t.BranchName, StringComparer.OrdinalIgnoreCase);
        var missing = PilotBusiness.Branches.Select(b => b.Name).Where(name => !byBranch.ContainsKey(name)).ToList();
        if (missing.Count > 0)
        {
            throw new UsageException($"No active WhatsApp number for: {string.Join(", ", missing)}. Run  setup  first.");
        }

        var withoutSecret = targets.Where(t => t.Secret is null && PilotBusiness.Branches.Any(b => b.Name.Equals(t.BranchName, StringComparison.OrdinalIgnoreCase))).Select(t => t.BranchName).ToList();
        if (withoutSecret.Count > 0)
        {
            throw new UsageException(
                $"No webhook secret for: {string.Join(", ", withoutSecret)}. Set {SimulatorEnvironment.SecretsVariable}='Branch=<secret>;...' (setup --print-secret shows them once; setup --rotate-secret --print-secret makes new ones).");
        }

        var now = time.GetUtcNow().UtcDateTime;
        var plan = PopulationPlanner.Plan(new PopulationOptions(options.Conversations, options.Days, options.Seed, now, options.PreviousShare)).OrderBy(c => c.StartedAtUtc).ToList();
        output.WriteLine($"Planned {plan.Count} conversations over {options.Days} days (plus {plan.Count(c => c.IsPrevious)} in the {options.Days} days before) across {PilotBusiness.Branches.Count} branches.");

        var baseline = await api.CountConversationsAsync(cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < plan.Count; i++)
        {
            var conversation = plan[i];
            var target = byBranch[conversation.BranchName];
            await sender.SendAsync(target.WebhookPath, target.Secret!, Delivery(conversation, target), cancellationToken);
            if ((i + 1) % 50 == 0 || i + 1 == plan.Count)
            {
                output.WriteLine($"  sent {i + 1}/{plan.Count} deliveries ({stopwatch.Elapsed.TotalSeconds:0} s, {sender.Throttled} throttled)");
            }
        }

        var arrived = await WaitForWorkersAsync(baseline, plan.Count, options.WaitSeconds, cancellationToken);
        if (!options.NoAssign)
        {
            await AssignAdvisorsAsync(plan, cancellationToken);
        }

        output.WriteLine();
        output.WriteLine(arrived
            ? "Done. Open the dashboard at /panel."
            : _arrivedCount == 0
            ? "Nothing new was added: those conversations were already there (the same seed was sent before). Use another --seed for more."
            : "Fewer new conversations than planned (see above). If the workers are still going, wait a little and reload /panel; run populate again with the same seed to assign the advisors that were missing.");
        output.WriteLine("Two things are not instant: the classifier decides a stage after a minute of quiet (Classification:DebounceSeconds), and conversations older than 7 days close at the next run of the closing job (Conversations:AutoCloseIntervalMinutes, 15 by default).");
        return 0;
    }

    /// <summary>The delivery of a whole chat: the customer's messages and the advisors' echoes in one body.</summary>
    public static string Delivery(PlannedConversation conversation, ChannelTarget target)
    {
        var inbound = conversation.Messages.Where(m => m.Speaker == Speaker.Customer).Select(m => m.ToDelivery()).ToList();
        var echoes = conversation.Messages.Where(m => m.Speaker == Speaker.Business).Select(m => m.ToDelivery()).ToList();
        return CloudApiPayloads.Conversation(target.Identity, conversation.CustomerWaId, conversation.CustomerName, inbound, echoes, conversation.Referral);
    }

    private async Task<bool> WaitForWorkersAsync(int baseline, int planned, int waitSeconds, CancellationToken cancellationToken)
    {
        output.WriteLine("Waiting for the workers to store them...");
        var deadline = time.GetUtcNow().AddSeconds(waitSeconds);
        var last = -1;
        var idlePolls = 0;
        while (true)
        {
            var (_, health) = await api.HealthAsync(cancellationToken);
            var count = await api.CountConversationsAsync(cancellationToken);
            var arrived = count - baseline;
            _arrivedCount = arrived;
            if (arrived >= planned)
            {
                output.WriteLine($"  {arrived} new conversations stored (health: {health}).");
                return true;
            }

            // Nothing new for several polls in a row: the workers are done (or off), waiting longer will not change it.
            idlePolls = count == last ? idlePolls + 1 : 0;
            last = count;
            if (idlePolls >= IdlePollsToStop)
            {
                output.WriteLine(
                    $"  {arrived} of {planned} new conversations stored and no more arriving (health: {health}). " +
                    "That is normal when the same seed ran before (the messages were already there) or a customer had written already; otherwise the workers may be off or behind.");
                return arrived >= planned * 0.9;
            }

            if (time.GetUtcNow() >= deadline)
            {
                output.WriteLine($"  {arrived} of {planned} stored when the wait ended (health: {health}).");
                return false;
            }

            await _delay(TimeSpan.FromSeconds(3), cancellationToken);
        }
    }

    private async Task AssignAdvisorsAsync(IReadOnlyList<PlannedConversation> plan, CancellationToken cancellationToken)
    {
        var advisors = await api.GetAdvisorsAsync(cancellationToken);
        var branches = await api.GetBranchesAsync(cancellationToken);
        // A customer is told apart by number AND branch: the same number may have written to two numbers of the business.
        var stored = (await api.ListAllConversationsAsync(cancellationToken))
            .GroupBy(c => (c.Contact.WaId, Branch: c.BranchName ?? string.Empty))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.StartedAt).First());
        var assigned = 0;
        var pending = 0;
        var refused = 0;
        foreach (var conversation in plan.Where(c => c.AdvisorName is not null))
        {
            var branch = branches.FirstOrDefault(b => b.Name.Equals(conversation.BranchName, StringComparison.OrdinalIgnoreCase));
            var advisor = advisors.FirstOrDefault(a => branch is not null && a.BranchId == branch.Id && a.Name.Equals(conversation.AdvisorName, StringComparison.OrdinalIgnoreCase));
            if (advisor is null || !stored.TryGetValue((conversation.CustomerWaId, branch!.Name), out var item))
            {
                pending++;
                continue;
            }

            if (item.AdvisorId == advisor.Id)
            {
                continue;
            }

            try
            {
                await api.AssignAdvisorAsync(item.Id, advisor.Id, cancellationToken);
                assigned++;
            }
            catch (ApiFailure failure) when (failure.Status == System.Net.HttpStatusCode.BadRequest)
            {
                // One conversation the API will not give to that advisor must not stop the others.
                refused++;
            }
        }

        output.WriteLine($"Assigned advisors to {assigned} conversations{(pending > 0 ? $" ({pending} not stored yet)" : string.Empty)}{(refused > 0 ? $", {refused} refused by the API" : string.Empty)}.");
    }
}
