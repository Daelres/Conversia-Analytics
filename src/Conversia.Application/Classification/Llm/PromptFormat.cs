using System.Text;

namespace Conversia.Application.Classification.Llm;

/// <summary>Who wrote a line of the prompt.</summary>
public enum PromptRole
{
    /// <summary>The customer (<c>C</c>).</summary>
    Customer,

    /// <summary>The business (<c>A</c>, advisor).</summary>
    Advisor,
}

/// <summary>One message inside a line: its number (its position in the snapshot, 1-based) and its prepared text.</summary>
public sealed record PromptSegment(int Number, string Text);

/// <summary>An entry of the conversation block: a line of one or more consecutive messages of one side, or a gap.</summary>
public abstract record PromptEntry;

/// <summary>Consecutive messages of the same side, in order.</summary>
public sealed record PromptLine(PromptRole Role, IReadOnlyList<PromptSegment> Segments) : PromptEntry;

/// <summary>Messages left out to fit the token budget.</summary>
public sealed record PromptGap(int OmittedMessages) : PromptEntry;

/// <summary>
/// The compact line format of the conversation block, shared by <see cref="ConversationPreprocessor"/> (which measures with it) and
/// <see cref="LlmPromptBuilder"/> (which sends it). The system prompt explains it to the model:
/// <code>
/// 1 C: hola | quiero info de axilas |4 ¿cuánto vale?     (messages 1, 2 and 4 of the customer; 3 was media without text)
/// 5 A: 60 mil la sesión                                  (the advisor)
/// 9 A: =5                                                (message 9 repeats message 5)
/// [... 37 messages omitted]                              (left out to fit the budget)
/// </code>
/// A segment continues the numbering of the one before it; a segment that does not carries its own number (<c>|4</c>). Every message
/// therefore keeps its number, which is its position in the snapshot: a line the model cites maps straight back to the message.
/// <see cref="PromptTextNeutralizer"/> guarantees that no message text can produce a line break, a <c>|</c>, a <c>n C:</c> prefix or a back-reference.
/// </summary>
public static class PromptFormat
{
    /// <summary>Where a long advisor message was cut. ASCII on purpose: NFKC (<see cref="TextSanitizer"/>) turns "…" into "...".</summary>
    public const string CutMarker = "[...]";

    public const string SegmentSeparator = " | ";

    public static char RoleCode(PromptRole role) => role == PromptRole.Customer ? 'C' : 'A';

    public static string BackReference(int number) => "=" + number;

    public static string Gap(int omitted) => $"[... {omitted} messages omitted]";

    /// <summary>A rough, conservative token estimate for Spanish chat text: one token per three characters, rounded up.</summary>
    public static int EstimateTokens(int characters) => (characters + 2) / 3;

    public static string Render(PromptEntry entry)
    {
        switch (entry)
        {
            case PromptGap gap:
                return Gap(gap.OmittedMessages);
            case PromptLine line:
                var text = new StringBuilder();
                var previous = 0;
                foreach (var segment in line.Segments)
                {
                    if (text.Length == 0)
                    {
                        text.Append(segment.Number).Append(' ').Append(RoleCode(line.Role)).Append(": ");
                    }
                    else if (segment.Number == previous + 1)
                    {
                        text.Append(SegmentSeparator);
                    }
                    else
                    {
                        text.Append(" |").Append(segment.Number).Append(' ');
                    }

                    text.Append(segment.Text);
                    previous = segment.Number;
                }

                return text.ToString();
            default:
                throw new ArgumentOutOfRangeException(nameof(entry));
        }
    }

    /// <summary>The entries, one per line.</summary>
    public static string RenderLines(IEnumerable<PromptEntry> entries) => string.Join('\n', entries.Select(Render));
}
