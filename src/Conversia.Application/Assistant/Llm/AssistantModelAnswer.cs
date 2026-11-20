using System.Text.Json;
using System.Text.Json.Nodes;

namespace Conversia.Application.Assistant.Llm;

/// <summary>The reply model's structured answer (docs/ASSISTANT.md, "Reply call"), read strictly: any field missing or of another type rejects it.</summary>
public sealed record AssistantModelAnswer(
    string ReplyText, string Intent, IReadOnlyList<string> UsedKnowledgeIds, bool WantsHandoff, string? HandoffReason, bool SaleClosed, bool Misunderstood)
{
    public static readonly IReadOnlyList<string> Intents = ["discover", "recommend", "answer_question", "handle_objection", "propose_booking", "close", "redirect", "handoff"];

    /// <summary>Why the model wants a person; mapped to the handoff codes (anything else is <c>generator_handoff</c>).</summary>
    public static readonly IReadOnlyList<string> HandoffReasons = ["customer_wants_human", "complaint", "out_of_scope", "needs_human_decision", "other"];

    public override string ToString() => $"AssistantModelAnswer {{ Length = {ReplyText.Length}, Intent = {Intent}, Knowledge = {UsedKnowledgeIds.Count} }}";

    public static AssistantModelAnswer? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(json) is not JsonObject root
                || root["reply_text"] is not JsonValue text || !text.TryGetValue<string>(out var replyText)
                || root["intent"] is not JsonValue intent || !intent.TryGetValue<string>(out var intentText)
                || root["used_knowledge_ids"] is not JsonArray ids
                || root["wants_handoff"] is not JsonValue handoff || !handoff.TryGetValue<bool>(out var wantsHandoff)
                || root["sale_closed"] is not JsonValue closed || !closed.TryGetValue<bool>(out var saleClosed)
                || root["misunderstood"] is not JsonValue misunderstood || !misunderstood.TryGetValue<bool>(out var notUnderstood)
                || !root.ContainsKey("handoff_reason"))
            {
                return null;
            }

            string? reason = null;
            if (root["handoff_reason"] is JsonValue reasonValue)
            {
                if (!reasonValue.TryGetValue<string>(out var reasonText))
                {
                    return null;
                }

                reason = reasonText;
            }

            var list = new List<string>();
            foreach (var id in ids)
            {
                if (id is not JsonValue value || !value.TryGetValue<string>(out var idText))
                {
                    return null;
                }

                list.Add(idText.Trim());
            }

            return new AssistantModelAnswer(replyText, intentText.Trim(), list, wantsHandoff, reason, saleClosed, notUnderstood);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
