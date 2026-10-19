namespace Conversia.WebhookSimulator.Generation;

/// <summary>A message of a chat with its place in time and its provider id.</summary>
public sealed record TimedMessage(Speaker Speaker, string Text, DateTime SentAtUtc, string ProviderId);

/// <summary>Puts a chat on the calendar: absolute instants and ids that are the same every time the same seed runs (so re-running is idempotent).</summary>
public static class ChatTimeline
{
    /// <summary>
    /// The messages of <paramref name="chat"/> starting at <paramref name="startUtc"/>. A message that would be sent after <paramref name="notAfterUtc"/>
    /// is left out (a chat that is still going on has not said it yet), and the first one is always kept.
    /// </summary>
    public static IReadOnlyList<TimedMessage> Place(ChatScript chat, DateTime startUtc, DateTime notAfterUtc, string runKey, int conversationIndex)
    {
        var placed = new List<TimedMessage>();
        for (var i = 0; i < chat.Messages.Count; i++)
        {
            var message = chat.Messages[i];
            var sentAt = startUtc.AddSeconds(message.OffsetSeconds);
            if (i > 0 && sentAt > notAfterUtc)
            {
                break;
            }

            placed.Add(new TimedMessage(message.Speaker, message.Text, sentAt, MessageId(runKey, conversationIndex, i)));
        }

        return placed;
    }

    public static string MessageId(string runKey, int conversationIndex, int messageIndex)
        => $"wamid.SIM{runKey}C{conversationIndex:D4}M{messageIndex:D3}";

    public static DeliveryMessage ToDelivery(this TimedMessage message) => new(message.ProviderId, message.Text, CloudApiPayloads.ToUnix(message.SentAtUtc));
}
