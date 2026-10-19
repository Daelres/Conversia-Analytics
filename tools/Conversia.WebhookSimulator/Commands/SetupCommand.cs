using Conversia.WebhookSimulator.Api;
using Conversia.WebhookSimulator.Cli;
using Conversia.WebhookSimulator.Generation;

namespace Conversia.WebhookSimulator.Commands;

/// <summary>
/// Makes sure the workspace has what the simulator needs: the four branches of the pilot, two advisors each and one WhatsApp number per branch.
/// Safe to run again: what exists is left alone. It prints the webhook path of each number and, only when asked (<c>--print-secret</c>),
/// the secret of a number it has just created or rotated; the API shows a secret once, and the simulator never writes one anywhere.
/// </summary>
public sealed class SetupCommand(ConversiaApi api, TextWriter output)
{
    public async Task<int> RunAsync(SimulatorOptions options, CancellationToken cancellationToken)
    {
        var branches = (await api.GetBranchesAsync(cancellationToken)).ToList();
        foreach (var wanted in PilotBusiness.Branches)
        {
            if (branches.All(b => !b.Name.Equals(wanted.Name, StringComparison.OrdinalIgnoreCase)))
            {
                branches.Add(await api.CreateBranchAsync(wanted.Name, cancellationToken));
                output.WriteLine($"Created branch {wanted.Name}.");
            }
        }

        var advisors = (await api.GetAdvisorsAsync(cancellationToken)).ToList();
        var createdAdvisors = 0;
        foreach (var wanted in PilotBusiness.Advisors)
        {
            var branch = branches.First(b => b.Name.Equals(wanted.BranchName, StringComparison.OrdinalIgnoreCase));
            if (!advisors.Any(a => a.BranchId == branch.Id && a.Name.Equals(wanted.Name, StringComparison.OrdinalIgnoreCase)))
            {
                advisors.Add(await api.CreateAdvisorAsync(wanted.Name, branch.Id, cancellationToken));
                createdAdvisors++;
            }
        }

        output.WriteLine(createdAdvisors == 0 ? "Advisors: nothing to create." : $"Created {createdAdvisors} advisor(s).");

        var channels = (await api.GetChannelsAsync(cancellationToken)).ToList();
        var secrets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var discarded = new List<string>();
        for (var index = 0; index < PilotBusiness.Branches.Count; index++)
        {
            var branch = branches.First(b => b.Name.Equals(PilotBusiness.Branches[index].Name, StringComparison.OrdinalIgnoreCase));
            var number = PilotBusiness.DisplayNumber(index);
            var channel = channels.FirstOrDefault(c => c.BranchId == branch.Id && c.DisplayPhoneNumber == number);
            string? path;
            string? secret = null;
            if (channel is null)
            {
                var created = await api.CreateChannelAsync(branch.Id, number, cancellationToken);
                (path, secret) = (created.WebhookPath, created.WebhookSecret);
                output.WriteLine($"Created the number {number} for {branch.Name}.");
            }
            else if (options.RotateSecret)
            {
                var rotated = await api.RotateSecretAsync(channel.Id, cancellationToken);
                (path, secret) = (rotated.WebhookPath, rotated.WebhookSecret);
                output.WriteLine($"Rotated the secret of {number} ({branch.Name}); the previous one stopped working.");
            }
            else
            {
                path = channel.WebhookPath;
            }

            output.WriteLine($"  {branch.Name,-8} {number}  ->  {path ?? "(webhook path hidden: log in as the owner to see it)"}");
            if (secret is not null)
            {
                if (options.PrintSecret)
                {
                    secrets[branch.Name] = secret;
                }
                else
                {
                    discarded.Add(branch.Name);
                }
            }
        }

        if (secrets.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("Webhook secrets (shown this once, nothing is saved; paste the line into your shell):");
            output.WriteLine($"  export {SimulatorEnvironment.SecretsVariable}='{string.Join(';', secrets.Select(pair => $"{pair.Key}={pair.Value}"))}'");
        }

        if (discarded.Count > 0)
        {
            output.WriteLine();
            output.WriteLine($"The secret of {string.Join(", ", discarded)} was created but not shown (you did not pass --print-secret), so it is gone.");
            output.WriteLine("Run  setup --rotate-secret --print-secret  to get new ones.");
        }
        else if (secrets.Count == 0)
        {
            output.WriteLine();
            output.WriteLine($"Everything was already there. To send, set {SimulatorEnvironment.SecretsVariable} (or {SimulatorEnvironment.SecretVariable}); the API cannot show a secret again, so use setup --rotate-secret --print-secret if you lost them.");
        }

        return 0;
    }
}
