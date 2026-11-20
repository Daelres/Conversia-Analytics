using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Conversia.Application.Ingestion;

/// <summary>
/// Rewrites a delivery before its raw body is kept (docs/WEBHOOKS.md, "Coexistence" and "History import"):
/// <list type="bullet">
/// <item>the phone's address book (<c>smb_app_state_sync</c>) is replaced by its count: names and numbers are never kept, not even raw;</item>
/// <item>a shared history is replaced by its counts when the channel has not opted in: nothing of it is kept;</item>
/// <item>a history the channel imports is split into deliveries of at most <see cref="HistorySliceSize"/> messages, so each one is processed in
/// its own short transaction (thousands of messages arrive in one chunk).</item>
/// </list>
/// A delivery with none of these is returned untouched (byte for byte, so its hash stays the provider's).
/// </summary>
public static class WebhookPayloadRewriter
{
    /// <summary>The most messages of a shared history kept in one stored delivery.</summary>
    public const int HistorySliceSize = 500;

    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The bodies to store, in order; the original one when nothing needs rewriting.</summary>
    public static IReadOnlyList<string> Rewrite(string body, bool historyImportEnabled)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body, documentOptions: new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException)
        {
            return [body];
        }

        if (root is not JsonObject envelope || envelope["entry"] is not JsonArray entries)
        {
            return [body];
        }

        var changed = false;
        var slices = new List<string>();
        foreach (var entry in entries.OfType<JsonObject>())
        {
            if (entry["changes"] is not JsonArray changes)
            {
                continue;
            }

            for (var i = 0; i < changes.Count; i++)
            {
                if (changes[i] is not JsonObject change || change["value"] is not JsonObject value)
                {
                    continue;
                }

                var field = change["field"]?.GetValueKind() == JsonValueKind.String ? change["field"]!.GetValue<string>() : null;
                if (field == Dialog360PayloadParser.AppStateSyncField)
                {
                    change["value"] = new JsonObject { ["redacted"] = field, ["count"] = (value["state_sync"] as JsonArray)?.Count ?? 0 };
                    changed = true;
                }
                else if (field == Dialog360PayloadParser.HistoryField && !historyImportEnabled)
                {
                    change["value"] = new JsonObject { ["redacted"] = field, ["messages"] = CountHistoryMessages(value) };
                    changed = true;
                }
                else if (field == Dialog360PayloadParser.HistoryField && CountHistoryMessages(value) > HistorySliceSize)
                {
                    slices.AddRange(Slice(envelope, entry, field, value));
                    changes.RemoveAt(i);
                    i--;
                    changed = true;
                }
            }
        }

        if (!changed)
        {
            return [body];
        }

        var rest = HasAnyChange(entries) ? [envelope.ToJsonString(Options)] : Array.Empty<string>();
        return [.. rest, .. slices];
    }

    private static bool HasAnyChange(JsonArray entries) => entries.OfType<JsonObject>().Any(e => e["changes"] is JsonArray { Count: > 0 });

    private static int CountHistoryMessages(JsonObject value)
        => (value["history"] as JsonArray)?.OfType<JsonObject>()
               .Sum(chunk => (chunk["threads"] as JsonArray)?.OfType<JsonObject>().Sum(t => (t["messages"] as JsonArray)?.Count ?? 0) ?? 0) ?? 0
           + ((value["messages"] as JsonArray)?.Count ?? 0);

    // One delivery per slice of messages, each a full envelope with the chunk's metadata and the thread's context.
    private static IEnumerable<string> Slice(JsonObject envelope, JsonObject entry, string field, JsonObject value)
    {
        var bodies = new List<string>();
        var currentThreads = new List<JsonObject>();
        JsonObject? currentChunkMeta = null;
        var count = 0;

        void Flush()
        {
            if (count == 0)
            {
                return;
            }

            var sliceValue = new JsonObject
            {
                ["messaging_product"] = value["messaging_product"]?.DeepClone(),
                ["metadata"] = value["metadata"]?.DeepClone(),
                ["history"] = new JsonArray(new JsonObject
                {
                    ["metadata"] = currentChunkMeta?.DeepClone(),
                    ["threads"] = new JsonArray(currentThreads.Select(t => (JsonNode)t).ToArray()),
                }),
            };
            var sliceEntry = new JsonObject
            {
                ["id"] = entry["id"]?.DeepClone(),
                ["changes"] = new JsonArray(new JsonObject { ["field"] = field, ["value"] = sliceValue }),
            };
            if (entry["time"] is { } time)
            {
                sliceEntry["time"] = time.DeepClone();
            }

            bodies.Add(new JsonObject { ["object"] = envelope["object"]?.DeepClone(), ["entry"] = new JsonArray(sliceEntry) }.ToJsonString(Options));
            currentThreads = [];
            count = 0;
        }

        foreach (var chunk in (value["history"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            Flush();
            currentChunkMeta = chunk["metadata"] as JsonObject;
            foreach (var thread in (chunk["threads"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                var messages = (thread["messages"] as JsonArray)?.ToList() ?? [];
                for (var start = 0; start < messages.Count;)
                {
                    if (count == HistorySliceSize)
                    {
                        Flush();
                    }

                    var take = Math.Min(HistorySliceSize - count, messages.Count - start);
                    var part = new JsonObject
                    {
                        ["id"] = thread["id"]?.DeepClone(),
                        ["context"] = thread["context"]?.DeepClone(),
                        ["messages"] = new JsonArray(messages.Skip(start).Take(take).Select(m => m?.DeepClone()).ToArray()),
                    };
                    currentThreads.Add(part);
                    count += take;
                    start += take;
                }
            }
        }

        Flush();

        // Media follow-ups (messages[] under the history field) go in slices of their own.
        var followUps = (value["messages"] as JsonArray)?.ToList() ?? [];
        for (var start = 0; start < followUps.Count; start += HistorySliceSize)
        {
            var sliceValue = new JsonObject
            {
                ["messaging_product"] = value["messaging_product"]?.DeepClone(),
                ["metadata"] = value["metadata"]?.DeepClone(),
                ["messages"] = new JsonArray(followUps.Skip(start).Take(HistorySliceSize).Select(m => m?.DeepClone()).ToArray()),
            };
            bodies.Add(new JsonObject
            {
                ["object"] = envelope["object"]?.DeepClone(),
                ["entry"] = new JsonArray(new JsonObject { ["id"] = entry["id"]?.DeepClone(), ["changes"] = new JsonArray(new JsonObject { ["field"] = field, ["value"] = sliceValue }) }),
            }.ToJsonString(Options));
        }

        return bodies;
    }
}
