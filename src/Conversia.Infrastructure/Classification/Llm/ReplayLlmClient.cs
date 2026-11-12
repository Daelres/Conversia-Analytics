using System.Text.Json;
using System.Text.Json.Nodes;
using Conversia.Application.Classification.Llm;

namespace Conversia.Infrastructure.Classification.Llm;

/// <summary>
/// A zero-cost, deterministic <see cref="ILlmClient"/> for local acceptance without an API key (<c>Classification:Llm:Client=Replay</c>,
/// spec CC-11; Development only, enforced at startup). It answers from an optional JSON file of canned answers keyed by snapshot hash
/// (<c>Classification:Llm:ReplayPath</c>):
/// <code>{ "responses": { "&lt;64-hex snapshot hash&gt;": { "stage": "lost", "loss_reason": "price", ... } } }</code>
/// and, for any other hash, with one fixed answer that passes the strict mapper (in follow-up, nothing extracted, confidence 0.5). Usage is
/// zero and the model is <see cref="ModelId"/>, so its log lines never pass for real spend. Nothing leaves the process.
/// </summary>
public sealed class ReplayLlmClient : ILlmClient
{
    public const string ModelId = "replay";

    private const string PathKey = "Classification:Llm:ReplayPath";

    private readonly IReadOnlyDictionary<string, string> _canned;
    private readonly string _fallback;

    private ReplayLlmClient(IReadOnlyDictionary<string, string> canned, bool extractAgeRange)
    {
        _canned = canned;
        var fallback = new JsonObject
        {
            ["stage"] = "in_follow_up",
            ["loss_reason"] = null,
            ["objection"] = "none",
            ["services"] = new JsonArray(),
            ["interest_reason"] = null,
            ["gender"] = "undetermined",
            ["confidence"] = 0.5,
            ["won_evidence_line"] = null,
        };
        if (extractAgeRange)
        {
            fallback["age_range"] = null;
        }

        _fallback = fallback.ToJsonString();
    }

    /// <param name="path">The canned answers file; null or empty = only the fixed answer.</param>
    /// <exception cref="InvalidOperationException">The file is missing or not of the documented shape (names the option, never quotes the file).</exception>
    public static ReplayLlmClient Load(string? path, bool extractAgeRange)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new ReplayLlmClient(new Dictionary<string, string>(), extractAgeRange);
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidOperationException($"{PathKey}: the replay file cannot be read ({ex.GetType().Name}).");
        }

        if (root?["responses"] is not JsonObject responses)
        {
            throw new InvalidOperationException($"{PathKey}: the replay file must be an object with a \"responses\" object.");
        }

        var canned = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (hash, answer) in responses)
        {
            if (hash.Length != 64 || !hash.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f') || answer is not JsonObject)
            {
                throw new InvalidOperationException($"{PathKey}: every key of \"responses\" must be a 64-character lower-case hex snapshot hash and every value an object.");
            }

            canned[hash] = answer.ToJsonString();
        }

        return new ReplayLlmClient(canned, extractAgeRange);
    }

    public Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var json = _canned.TryGetValue(request.SnapshotHash, out var canned) ? canned : _fallback;
        return Task.FromResult(new LlmCompletion(LlmStopReason.EndTurn, json, LlmUsage.None, ModelId));
    }
}
