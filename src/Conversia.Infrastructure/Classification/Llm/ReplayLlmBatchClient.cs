using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Conversia.Application.Classification.Llm;

namespace Conversia.Infrastructure.Classification.Llm;

/// <summary>
/// The Message Batches port over the <see cref="ReplayLlmClient"/> (Development only, no key, nothing leaves the process): a submitted batch
/// is answered at once from the canned answers and reported ended on the first poll. Kept in memory: a restart forgets the batches, whose
/// conversations are then claimed again when their lease expires.
/// </summary>
public sealed class ReplayLlmBatchClient(ILlmClient replay) : ILlmBatchClient
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<LlmBatchResult>> _batches = new(StringComparer.Ordinal);

    public async Task<LlmBatchSubmission> SubmitAsync(IReadOnlyList<LlmBatchItem> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        var results = new List<LlmBatchResult>(items.Count);
        foreach (var item in items)
        {
            var completion = await replay.CompleteAsync(item.Request, cancellationToken);
            results.Add(new LlmBatchResult(item.CustomId, LlmBatchResultKind.Succeeded, completion, null));
        }

        var id = "replaybatch_" + Guid.NewGuid().ToString("N");
        _batches[id] = results;
        return new LlmBatchSubmission(id, DateTime.UtcNow.AddHours(24));
    }

    public Task<LlmBatchState> GetAsync(string providerBatchId, CancellationToken cancellationToken = default)
    {
        var count = _batches.TryGetValue(providerBatchId, out var results) ? results.Count : 0;
        return Task.FromResult(new LlmBatchState(LlmBatchProcessing.Ended, 0, count, 0, 0, 0, null));
    }

    public async IAsyncEnumerable<LlmBatchSummary> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        foreach (var (id, results) in _batches)
        {
            yield return new LlmBatchSummary(id, DateTime.UtcNow, LlmBatchProcessing.Ended, results.Count);
        }
    }

    public Task CancelAsync(string providerBatchId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async IAsyncEnumerable<LlmBatchResult> ResultsAsync(string providerBatchId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        if (_batches.TryGetValue(providerBatchId, out var results))
        {
            foreach (var result in results)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return result;
            }
        }
    }
}
