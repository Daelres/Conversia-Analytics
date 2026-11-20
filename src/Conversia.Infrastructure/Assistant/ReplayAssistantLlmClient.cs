using System.Text.Json.Nodes;
using Conversia.Application.Classification.Llm;

namespace Conversia.Infrastructure.Assistant;

/// <summary>
/// A zero-cost, deterministic model for the assistant in Development (<c>Assistant:Llm:Client=Replay</c>, no key, nothing leaves the
/// process): the guard answers <c>on_topic</c>, the reply a short greeting-and-question that passes every check. Usage is zero and the model
/// is <see cref="ModelId"/>, so its log lines never pass for real spend. The evaluation CLI scripts its own answers per case instead.
/// </summary>
public sealed class ReplayAssistantLlmClient : ILlmClient
{
    public const string ModelId = "replay";

    public const string ReplyText = "¡Hola {nombre}! Con gusto te ayudo con lo que necesites de nuestros servicios. ¿Qué te gustaría saber?";

    public Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var isGuard = request.Schema["properties"] is JsonObject properties && properties.ContainsKey("label");
        var json = isGuard
            ? new JsonObject { ["label"] = "on_topic", ["confidence"] = 0.9 }
            : new JsonObject
            {
                ["reply_text"] = ReplyText,
                ["intent"] = "discover",
                ["used_knowledge_ids"] = new JsonArray(),
                ["wants_handoff"] = false,
                ["handoff_reason"] = null,
                ["sale_closed"] = false,
                ["misunderstood"] = false,
            };
        return Task.FromResult(new LlmCompletion(LlmStopReason.EndTurn, json.ToJsonString(), LlmUsage.None, ModelId));
    }
}
