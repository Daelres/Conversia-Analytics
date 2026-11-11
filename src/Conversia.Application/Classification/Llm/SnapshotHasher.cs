using System.Security.Cryptography;
using System.Text;

namespace Conversia.Application.Classification.Llm;

/// <summary>Everything besides the snapshot that changes what the model reads or how its answer is read.</summary>
public sealed record SnapshotHashInputs(string PromptVersion, string ModelId, string SchemaVersion, bool ExtractAgeRange, string PreprocessingFingerprint);

/// <summary>
/// The snapshot hash (design D23): SHA-256, lower-case hex (64 characters), over a canonical serialization of the capped window (the latest
/// <see cref="RuleBasedConversationClassifier.MaxMessages"/> messages, the first <see cref="RuleBasedConversationClassifier.MaxCharactersPerMessage"/>
/// characters of each) as ordered (message id, direction, text), the branch name, the contact and advisor names, the sorted known names
/// and numbers (they decide what is masked) and every
/// <see cref="SnapshotHashInputs"/>. Computed locally on the real text; only the digest is ever stored. Every field is length-prefixed, so
/// moving text from one message to the next changes the hash. Pure.
/// </summary>
public static class SnapshotHasher
{
    private const string Format = "conversia.snapshot-hash.v1";

    public static string Compute(ConversationSnapshot snapshot, SnapshotHashInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(inputs);

        var data = new StringBuilder();
        void Field(string? value)
        {
            if (value is null)
            {
                data.Append("-1:");
                return;
            }

            data.Append(value.Length).Append(':').Append(value);
        }

        Field(Format);
        Field(inputs.PromptVersion);
        Field(inputs.ModelId);
        Field(inputs.SchemaVersion);
        Field(inputs.ExtractAgeRange ? "age" : "no-age");
        Field(inputs.PreprocessingFingerprint);
        Field(snapshot.BranchName);
        Field(snapshot.ContactName);
        Field(snapshot.AdvisorName);

        foreach (var list in new[] { snapshot.Names, snapshot.Numbers })
        {
            var sorted = list.Order(StringComparer.Ordinal).ToList();
            Field(sorted.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sorted.ForEach(Field);
        }

        var messages = snapshot.Messages;
        var first = Math.Max(0, messages.Count - RuleBasedConversationClassifier.MaxMessages);
        Field((messages.Count - first).ToString(System.Globalization.CultureInfo.InvariantCulture));
        for (var i = first; i < messages.Count; i++)
        {
            var message = messages[i];
            var text = message.Text;
            if (text is not null && text.Length > RuleBasedConversationClassifier.MaxCharactersPerMessage)
            {
                text = text[..RuleBasedConversationClassifier.MaxCharactersPerMessage];
            }

            Field(message.Id?.ToString("N"));
            Field(message.Direction.ToString());
            Field(text);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(data.ToString())));
    }
}
