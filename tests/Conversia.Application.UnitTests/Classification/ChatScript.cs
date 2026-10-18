using Conversia.Application.Classification;
using Conversia.Domain.Enums;

namespace Conversia.Application.UnitTests.Classification;

/// <summary>
/// A conversation written as a script, one message per line: <c>C: ...</c> is the customer, <c>A: ...</c> the advisor. A line that starts
/// with neither continues the message above (so a message can hold several lines). A message of the form <c>C: [audio]</c> is media with no text.
/// </summary>
internal static class ChatScript
{
    // Fixed instant: the classifier never reads the clock, but the snapshot needs times.
    private static readonly DateTime Start = new(2026, 3, 10, 15, 0, 0, DateTimeKind.Utc);

    public static ConversationSnapshot Snapshot(string script)
    {
        var messages = new List<SnapshotMessage>();
        foreach (var raw in script.Split('\n'))
        {
            var line = raw.TrimEnd('\r').TrimStart();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("C:", StringComparison.Ordinal) || line.StartsWith("A:", StringComparison.Ordinal))
            {
                var direction = line[0] == 'C' ? MessageDirection.Inbound : MessageDirection.Outbound;
                var text = line[2..].Trim();
                var media = text is "[audio]" or "[image]";
                messages.Add(new SnapshotMessage(direction, media ? text.Trim('[', ']') : "text", media ? null : text, Start.AddMinutes(messages.Count)));
            }
            else
            {
                var last = messages[^1];
                messages[^1] = last with { Text = last.Text + "\n" + line };
            }
        }

        var firstInbound = messages.FirstOrDefault(m => m.Direction == MessageDirection.Inbound)?.SentAt;
        var firstResponse = firstInbound is null ? null : messages.FirstOrDefault(m => m.Direction == MessageDirection.Outbound && m.SentAt >= firstInbound)?.SentAt;
        return new ConversationSnapshot(Guid.NewGuid(), "Sede Centro", Start, firstInbound, firstResponse, messages);
    }
}
