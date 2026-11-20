using System.Globalization;

namespace Conversia.Web.Services;

/// <summary>
/// What the conversation page compares while it polls (docs/MESSAGING.md, "Inbox"): the number of messages, the status and whether the 24-hour window is open. The page writes
/// it when drawn; <c>/conversaciones/{id}/version</c> answers the current one, and the script reloads the chat when they differ.
/// </summary>
public static class ConversationVersion
{
    /// <param name="windowOpen">Whether free text can be sent: when the window closes (with no new message) the composer must change too.</param>
    public static string Of(int messageCount, string status, bool windowOpen) => string.Create(CultureInfo.InvariantCulture, $"{messageCount}-{status}-{(windowOpen ? "w" : "t")}");
}
