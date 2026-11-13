using System.Runtime.CompilerServices;
using Anthropic;
using Anthropic.Models.Messages;
using Anthropic.Models.Messages.Batches;
using Conversia.Application.Classification.Llm;

namespace Conversia.Infrastructure.Classification.Llm;

/// <summary>
/// <see cref="ILlmBatchClient"/> over the official Anthropic C# SDK (Message Batches API, docs/CLASSIFICATION.md "Batch mode"). Every request
/// carries exactly the parameters of the synchronous call (<see cref="AnthropicLlmClient.CreateParams"/>: the cached prefix, the structured
/// output schema, the effort, the thinking setting). Failures become <see cref="LlmCallException"/> with the kind and the SDK exception TYPE only.
/// </summary>
public sealed class AnthropicLlmBatchClient(AnthropicClient client, AnthropicRequestSettings settings) : ILlmBatchClient
{
    /// <summary>Creating a batch is not idempotent: the SDK must never resend it (a lost answer is recovered from the provider's list instead).</summary>
    public const int SdkMaxRetries = 0;

    /// <summary>Uploading a batch of many requests takes longer than one call.</summary>
    public static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The batch client as the API builds it: the key and base URL set explicitly, <see cref="SdkMaxRetries"/> retries.</summary>
    public static AnthropicLlmBatchClient Create(string apiKey, Uri baseUrl, AnthropicRequestSettings settings, HttpClient? httpClient = null)
        => new(AnthropicLlmClient.CreateSdkClient(apiKey, baseUrl, UploadTimeout, SdkMaxRetries, httpClient), settings);

    public async IAsyncEnumerable<LlmBatchSummary> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var page = await Call(() => client.Messages.Batches.List(new BatchListParams { Limit = 100 }, cancellationToken), cancellationToken);
        while (true)
        {
            foreach (var batch in page.Items)
            {
                var counts = batch.RequestCounts;
                yield return new LlmBatchSummary(
                    batch.ID, batch.CreatedAt.UtcDateTime, Read(batch).Processing,
                    (int)(counts.Processing + counts.Succeeded + counts.Errored + counts.Canceled + counts.Expired));
            }

            if (!page.HasNext())
            {
                yield break;
            }

            var current = page;
            page = await Call(() => current.Next(cancellationToken), cancellationToken);
        }
    }

    public async Task CancelAsync(string providerBatchId, CancellationToken cancellationToken = default)
        => await Call(() => client.Messages.Batches.Cancel(providerBatchId, null, cancellationToken), cancellationToken);
    public async Task<LlmBatchSubmission> SubmitAsync(IReadOnlyList<LlmBatchItem> items, CancellationToken cancellationToken = default)
    {
        var created = await Call(() => client.Messages.Batches.Create(CreateParams(items, settings), cancellationToken), cancellationToken);
        return new LlmBatchSubmission(created.ID, created.ExpiresAt.UtcDateTime);
    }

    public async Task<LlmBatchState> GetAsync(string providerBatchId, CancellationToken cancellationToken = default)
    {
        var batch = await Call(() => client.Messages.Batches.Retrieve(providerBatchId, null, cancellationToken), cancellationToken);
        return Read(batch);
    }

    public async IAsyncEnumerable<LlmBatchResult> ResultsAsync(string providerBatchId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in client.Messages.Batches.ResultsStreaming(providerBatchId, null, cancellationToken).WithCancellation(cancellationToken))
        {
            yield return Read(item);
        }
    }

    /// <summary>The batch parameters: one request per item, its custom id and the synchronous call's parameters. Pure.</summary>
    public static BatchCreateParams CreateParams(IReadOnlyList<LlmBatchItem> items, AnthropicRequestSettings settings)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new BatchCreateParams
        {
            Requests = [.. items.Select(i =>
            {
                var single = AnthropicLlmClient.CreateParams(i.Request, settings);
                return new Request
                {
                    CustomID = i.CustomId,
                    Params = new Params
                    {
                        Model = single.Model,
                        MaxTokens = single.MaxTokens,
                        System = new List<TextBlockParam>
                        {
                            new() { Text = i.Request.SystemPrompt },
                            new() { Text = i.Request.ContextBlock, CacheControl = new CacheControlEphemeral() },
                        },
                        Messages = single.Messages,
                        OutputConfig = single.OutputConfig,
                        Thinking = single.Thinking,
                    },
                };
            })],
        };
    }

    public static LlmBatchState Read(MessageBatch batch)
    {
        var processing = batch.ProcessingStatus.Raw() switch
        {
            "ended" => LlmBatchProcessing.Ended,
            "canceling" => LlmBatchProcessing.Canceling,
            _ => LlmBatchProcessing.InProgress,
        };
        var counts = batch.RequestCounts;
        return new LlmBatchState(
            processing, (int)counts.Processing, (int)counts.Succeeded, (int)counts.Errored, (int)counts.Canceled, (int)counts.Expired, batch.ExpiresAt.UtcDateTime);
    }

    public static LlmBatchResult Read(MessageBatchIndividualResponse item)
    {
        if (item.Result.TryPickSucceeded(out var succeeded))
        {
            return new LlmBatchResult(item.CustomID, LlmBatchResultKind.Succeeded, AnthropicLlmClient.Read(succeeded.Message), null);
        }

        if (item.Result.TryPickErrored(out var errored))
        {
            return new LlmBatchResult(item.CustomID, LlmBatchResultKind.Errored, null, errored.Error.Error.Type.Raw());
        }

        return item.Result.TryPickExpired(out _)
            ? new LlmBatchResult(item.CustomID, LlmBatchResultKind.Expired, null, null)
            : new LlmBatchResult(item.CustomID, LlmBatchResultKind.Canceled, null, null);
    }

    private static async Task<T> Call<T>(Func<Task<T>> call, CancellationToken cancellationToken)
    {
        try
        {
            return await call();
        }
        catch (Exception ex) when (AnthropicLlmClient.Classify(ex, cancellationToken) is { } kind)
        {
            throw new LlmCallException(kind, AnthropicLlmClient.MisconfigurationCondition(ex) ?? ex.GetType().Name);
        }
    }
}
