using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Conversia.Application.Classification.Llm;

namespace Conversia.Infrastructure.Classification.Llm;

/// <param name="Model">The model id as a plain string (<c>claude-haiku-5-5</c>; spike S4: no SDK constant needed).</param>
/// <param name="MaxTokens">Ceiling of the answer, thinking included (<c>Classification:Llm:MaxTokens</c>).</param>
/// <param name="Effort"><c>low</c>, <c>medium</c>, <c>high</c>, <c>xhigh</c> or <c>max</c> (<c>Classification:Llm:Effort</c>).</param>
/// <param name="DisableThinking">Send <c>thinking: disabled</c> (accepted by Claude Haiku 5.5 at effort <c>high</c> or below); false leaves
/// the model's default, adaptive thinking.</param>
public sealed record AnthropicRequestSettings(string Model, int MaxTokens, string Effort, bool DisableThinking);

/// <summary>
/// <see cref="ILlmClient"/> over the official Anthropic C# SDK (NuGet <c>Anthropic</c>): one Messages call per classification, with the
/// static prefix cached (two system blocks, the breakpoint on the second), structured output constrained by the extraction schema, the
/// effort in <c>output_config</c>, no sampling parameters, no prefill and no tools. The SDK's own retries are configured by the caller
/// (Stage A: zero) and its timeout bounds the call. Failures become <see cref="LlmCallException"/> with the kind and the SDK exception
/// TYPE only: the SDK's messages may echo the request, so they are never kept.
/// </summary>
public sealed class AnthropicLlmClient(AnthropicClient client, AnthropicRequestSettings settings) : ILlmClient
{
    /// <summary>The SDK client with an explicit key (never resolved from the environment or a profile), timeout and retry count.</summary>
    public static readonly Uri OfficialBaseUrl = new("https://api.anthropic.com");

    public static AnthropicClient CreateSdkClient(string apiKey, Uri baseUrl, TimeSpan timeout, int maxRetries, HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentNullException.ThrowIfNull(baseUrl);
        if (!baseUrl.IsAbsoluteUri || baseUrl.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("The base URL must be an absolute https URL.", nameof(baseUrl));
        }

        // Everything the SDK would otherwise take from the environment is set explicitly: the key (so ANTHROPIC_AUTH_TOKEN, profiles and
        // federation variables are not read), the base URL (ANTHROPIC_BASE_URL could send prompts and the key elsewhere) and telemetry
        // (ANTHROPIC_OPEN_TELEMETRY_* could export prompt content as spans).
        var client = new AnthropicClient
        {
            ApiKey = apiKey,
            BaseUrl = baseUrl.ToString().TrimEnd('/'),
            Timeout = timeout,
            MaxRetries = maxRetries,
            OpenTelemetry = false,
        };
        return httpClient is null ? client : new AnthropicClient
        {
            ApiKey = apiKey,
            BaseUrl = baseUrl.ToString().TrimEnd('/'),
            Timeout = timeout,
            MaxRetries = maxRetries,
            OpenTelemetry = false,
            HttpClient = httpClient,
        };
    }

    public async Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Message message;
        try
        {
            message = await client.Messages.Create(CreateParams(request, settings), cancellationToken);
        }
        catch (Exception ex) when (Classify(ex, cancellationToken) is { } kind)
        {
            throw new LlmCallException(kind, MisconfigurationCondition(ex) ?? ex.GetType().Name);
        }

        try
        {
            return Read(message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new LlmCallException(LlmFailureKind.InvalidOutput, ex.GetType().Name);
        }
    }

    /// <summary>The request parameters (spikes S1, S2, S4, S5). Pure.</summary>
    public static MessageCreateParams CreateParams(LlmRequest request, AnthropicRequestSettings settings)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(settings);

        var schema = request.Schema.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value));
        return new MessageCreateParams
        {
            Model = settings.Model,
            MaxTokens = settings.MaxTokens,
            System = new List<TextBlockParam>
            {
                new() { Text = request.SystemPrompt },
                new() { Text = request.ContextBlock, CacheControl = new CacheControlEphemeral() },
            },
            Messages = [new UserMessageParam { Content = request.UserContent }],
            OutputConfig = new OutputConfig
            {
                Effort = ParseEffort(settings.Effort),
                Format = new JsonOutputFormat { Schema = schema },
            },
            Thinking = settings.DisableThinking ? new ThinkingConfigDisabled() : (ThinkingConfigParam?)null,
        };
    }

    /// <summary>The answer: the text blocks joined, the stop reason, the four usage counters and the model (spikes S3, S6).</summary>
    public static LlmCompletion Read(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var text = new StringBuilder();
        var any = false;
        foreach (var block in message.Content)
        {
            if (block.TryPickText(out var textBlock))
            {
                text.Append(textBlock.Text);
                any = true;
            }
        }

        var usage = message.Usage;
        return new LlmCompletion(
            ReadStopReason(message.StopReason?.Raw()),
            any ? text.ToString() : null,
            new LlmUsage(usage.InputTokens, usage.OutputTokens, usage.CacheReadInputTokens ?? 0, usage.CacheCreationInputTokens ?? 0),
            message.Model.Raw());
    }

    internal static LlmStopReason ReadStopReason(string? wire) => wire switch
    {
        "end_turn" => LlmStopReason.EndTurn,
        "max_tokens" => LlmStopReason.MaxTokens,
        "refusal" => LlmStopReason.Refusal,
        _ => LlmStopReason.Other,
    };

    /// <summary>
    /// Which failure an SDK exception is (design D3): 429 = rate limited; a timeout of the SDK (not the caller's cancellation) = timeout;
    /// 5xx, any other error status (bad key, bad request: a configuration problem the breaker contains) and network errors = unavailable;
    /// an answer the SDK cannot read = invalid output. Null = not a provider failure (the caller cancelled): let it propagate.
    /// </summary>
    internal static LlmFailureKind? Classify(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        OperationCanceledException when cancellationToken.IsCancellationRequested => null,
        AnthropicRateLimitException => LlmFailureKind.RateLimited,
        AnthropicUnauthorizedException or AnthropicForbiddenException or AnthropicNotFoundException => LlmFailureKind.Misconfigured,
        OperationCanceledException or TimeoutException => LlmFailureKind.Timeout,
        AnthropicInvalidDataException or JsonException => LlmFailureKind.InvalidOutput,
        AnthropicApiException or AnthropicIOException or AnthropicException or HttpRequestException => LlmFailureKind.Unavailable,
        _ => null,
    };

    /// <summary>The condition of a rejected configuration, as a code (never the key or the provider's message).</summary>
    internal static string? MisconfigurationCondition(Exception exception) => exception switch
    {
        AnthropicUnauthorizedException => "authentication",
        AnthropicForbiddenException => "permission",
        AnthropicNotFoundException => "model_not_found",
        _ => null,
    };

    private static Effort ParseEffort(string effort) => effort switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "high" => Effort.High,
        "xhigh" => Effort.Xhigh,
        "max" => Effort.Max,
        _ => throw new ArgumentOutOfRangeException(nameof(effort), effort, "Unknown effort."),
    };
}
