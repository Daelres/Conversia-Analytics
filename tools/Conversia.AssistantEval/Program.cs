using System.Globalization;
using Conversia.Application.Assistant.Llm;
using Conversia.Application.Classification.Llm;
using Conversia.AssistantEval;
using Conversia.Infrastructure.Classification.Llm;

// The assistant's evaluation (docs/ASSISTANT.md, "Evaluation"):
//   dotnet run --project tools/Conversia.AssistantEval                      Replay: the scripted answers of each case, no key, no network, no cost
//   dotnet run --project tools/Conversia.AssistantEval -- --case <id>        one case
//   CONVERSIA_REAL_LLM_API_KEY=... dotnet run --project tools/Conversia.AssistantEval -- --real
//                                                                             the real API (Claude Haiku 5.5): costs money; synthetic chats only
// Exit code 0 when every case passes.
var real = args.Contains("--real");
var only = Value("--case");
var path = Value("--set") ?? EvalSet.DefaultPath;
var set = EvalSet.Load(path);
var pricing = LlmPricing.Default;
var cached = new HashSet<string>(StringComparer.Ordinal);

Func<EvalCase, AssistantLlmClients> clients;
if (real)
{
    var key = Environment.GetEnvironmentVariable("CONVERSIA_REAL_LLM_API_KEY");
    if (string.IsNullOrWhiteSpace(key))
    {
        Console.Error.WriteLine("--real calls the real Anthropic API and costs money: set CONVERSIA_REAL_LLM_API_KEY to run it.");
        return 2;
    }

    // As the API builds them: the key and the endpoint explicit (no environment redirect), no SDK retry.
    var sdk = AnthropicLlmClient.CreateSdkClient(key, AnthropicLlmClient.OfficialBaseUrl, TimeSpan.FromSeconds(12), maxRetries: 0);
    var shared = new AssistantLlmClients(
        new AnthropicLlmClient(sdk, new AnthropicRequestSettings("claude-haiku-5-5", 1024, "low", DisableThinking: true)),
        new AnthropicLlmClient(sdk, new AnthropicRequestSettings("claude-haiku-5-5", 128, "low", DisableThinking: true)));
    clients = _ => shared;
}
else
{
    clients = c => new AssistantLlmClients(new ScriptedEvalModel(c, cached), new ScriptedEvalModel(c, cached));
}

var evaluator = new AssistantEvaluator(clients, pricing);
var results = new List<EvalResult>();
foreach (var @case in set.Cases.Where(c => only is null || c.Id == only))
{
    var result = await evaluator.RunAsync(set, @case);
    results.Add(result);
    Console.WriteLine($"{(result.Passed ? "PASS" : "FAIL")}  {result.CaseId,-44} {result.Category,-11} {result.Outcome}{(result.Reason is null ? string.Empty : ":" + result.Reason)}"
        + (result.Usage is { } u ? $"  calls {u.Calls} USD {u.EstimatedUsd.ToString("0.000000", CultureInfo.InvariantCulture)}" : string.Empty));
    foreach (var failure in result.Failures)
    {
        Console.WriteLine($"      - {failure}");
    }
}

var withModel = results.Where(r => r.Usage is not null).ToList();
var usd = withModel.Sum(r => r.Usage!.EstimatedUsd);
Console.WriteLine();
Console.WriteLine($"Set {set.Version} ({(real ? "real API" : "Replay, costs estimated from the prompt sizes")}): {results.Count(r => r.Passed)}/{results.Count} passed.");
var realOnly = set.Cases.Where(c => c.Meaningful == "real").Select(c => c.Id).ToHashSet();
if (!real && realOnly.Count > 0)
{
    Console.WriteLine($"  {realOnly.Count} of them are meaningful only against the real API (Replay scripts the model): {string.Join(", ", realOnly.Order())}");
}
foreach (var group in results.GroupBy(r => r.Category).OrderBy(g => g.Key))
{
    Console.WriteLine($"  {group.Key,-11} {group.Count(r => r.Passed)}/{group.Count()}");
}

if (withModel.Count > 0)
{
    var calls = withModel.Sum(r => r.Usage!.Calls);
    var guard = withModel.Sum(r => r.Usage!.GuardCalls);
    Console.WriteLine($"Model: {withModel.Count} generations, {calls} calls ({guard} guard), tokens in {withModel.Sum(r => r.Usage!.InputTokens)} out {withModel.Sum(r => r.Usage!.OutputTokens)} "
        + $"cache read {withModel.Sum(r => r.Usage!.CacheReadTokens)} cache write {withModel.Sum(r => r.Usage!.CacheWriteTokens)}; "
        + $"USD {usd.ToString("0.000000", CultureInfo.InvariantCulture)} ({(usd / withModel.Count).ToString("0.000000", CultureInfo.InvariantCulture)} per generation).");
}

return results.All(r => r.Passed) ? 0 : 1;

string? Value(string name)
{
    var at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
}
