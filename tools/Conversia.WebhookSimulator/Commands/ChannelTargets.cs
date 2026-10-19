using Conversia.Application.DTOs.Organization;
using Conversia.WebhookSimulator.Api;
using Conversia.WebhookSimulator.Cli;
using Conversia.WebhookSimulator.Generation;

namespace Conversia.WebhookSimulator.Commands;

/// <summary>A channel of the business as the simulator talks to it: where its webhook is, who it says it is and the secret to authenticate with (null when none is configured).</summary>
public sealed record ChannelTarget(Guid BranchId, string BranchName, string WebhookPath, ChannelIdentity Identity, string? Secret);

public static class ChannelTargets
{
    /// <summary>
    /// The active channels of the workspace, found through the API (the simulator needs no list of keys of its own), each with the secret the
    /// environment holds for its branch. Only a user who can manage the organization sees the webhook paths.
    /// </summary>
    public static async Task<IReadOnlyList<ChannelTarget>> ResolveAsync(ConversiaApi api, SimulatorEnvironment environment, CancellationToken cancellationToken)
    {
        var branches = await api.GetBranchesAsync(cancellationToken);
        var channels = await api.GetChannelsAsync(cancellationToken);
        var targets = new List<ChannelTarget>();

        // A branch may have other numbers than the simulator's (somebody added one in the app): the simulator's own is used, else the oldest.
        var simulatorNumbers = PilotBusiness.Branches.Select((_, index) => PilotBusiness.DisplayNumber(index)).ToHashSet();
        var chosen = channels
            .Where(c => c.IsActive)
            .GroupBy(c => c.BranchId)
            .Select(group => group.OrderByDescending(c => simulatorNumbers.Contains(c.DisplayPhoneNumber)).ThenBy(c => c.CreatedAt).First());
        foreach (var channel in chosen)
        {
            var branch = branches.FirstOrDefault(b => b.Id == channel.BranchId);
            if (branch is null)
            {
                continue;
            }

            if (string.IsNullOrEmpty(channel.WebhookPath))
            {
                throw new ApiFailure("The API did not return the webhook paths: log in as the owner of the workspace (or a user with Organization management).");
            }

            targets.Add(new ChannelTarget(branch.Id, branch.Name, channel.WebhookPath, IdentityOf(channel), environment.SecretFor(branch.Name)));
        }

        return targets.OrderBy(t => t.BranchName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static ChannelIdentity IdentityOf(ChannelResponse channel)
    {
        var digits = new string(channel.DisplayPhoneNumber.Where(char.IsAsciiDigit).ToArray());
        return new ChannelIdentity(digits, channel.ProviderPhoneNumberId ?? "1000" + digits[^Math.Min(digits.Length, 8)..]);
    }
}
