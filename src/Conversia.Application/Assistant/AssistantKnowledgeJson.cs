using System.Text.Json;
using System.Text.Json.Serialization;
using Conversia.Domain.Entities;

namespace Conversia.Application.Assistant;

/// <summary>How a knowledge base version is stored (<c>AssistantKnowledgeVersions.Content</c>) and sent over the API: camelCase, days by name.</summary>
public static class AssistantKnowledgeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(AssistantKnowledge knowledge) => JsonSerializer.Serialize(knowledge, Options);

    public static AssistantKnowledge Deserialize(string json) => JsonSerializer.Deserialize<AssistantKnowledge>(json, Options) ?? new AssistantKnowledge();
}
