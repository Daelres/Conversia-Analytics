using Conversia.API.Configuration;
using Conversia.Application.Classification.Llm;
using Conversia.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;

namespace Conversia.IntegrationTests;

/// <summary>
/// Startup validation of the LLM provider (design D17, D22, D26; spec CC-1, CC-11, CC-13, CC-14, CP-5, PS-1): fail fast naming keys,
/// never values; Development only; the Stage A timing budget; the local overlay must start. No database, no network.
/// </summary>
public class LlmClassificationOptionsTests
{
    private const string FakeKey = "sk-ant-not-a-real-key-0000";

    private static IConfiguration Config(params (string Key, string Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private static ClassificationOptions Validate(string environment, params (string, string)[] values)
        => ClassificationServiceCollectionExtensions.GetValidatedOptions(Config(values), environment);

    private static InvalidOperationException Fails(string environment, params (string, string)[] values)
        => Assert.Throws<InvalidOperationException>(() => Validate(environment, values));

    private static readonly (string, string)[] ReplayLlm =
    [
        ("Classification:Provider", "Llm"), ("Classification:Llm:Client", "Replay"), ("Classification:Llm:PrivacyApproved", "true"),
    ];

    private static (string, string)[] With(params (string, string)[] extra) => [.. ReplayLlm, .. extra];

    [Fact]
    public void ByDefault_TheProviderIsRules_AndNoLlmSettingIsRead()
    {
        var options = Validate("Development", ("Classification:Llm:TimeoutSeconds", "999"));

        Assert.Equal(("Rules", (LlmOptions?)null), (options.Provider, options.Llm));
    }

    [Fact]
    public void Rules_WithNoDebounce_Starts_AndNoBudgetValidationRuns()
    {
        // CC-13 S4: the existing tests run Rules with DebounceSeconds=0 and a zero retry delay.
        var options = Validate("Production", ("Classification:DebounceSeconds", "0"), ("Classification:RetryDelaySeconds", "0"));

        Assert.Equal((0, 0), (options.DebounceSeconds, options.RetryDelaySeconds));
    }

    [Fact]
    public void AnUnknownProvider_FailsNamingTheKeyAndTheAcceptedValues()
    {
        var ex = Fails("Development", ("Classification:Provider", "Banana"));

        Assert.Contains("Classification:Provider", ex.Message);
        Assert.Contains("Rules", ex.Message);
        Assert.Contains("Llm", ex.Message);
    }

    [Fact]
    public void Shadow_IsNotAvailableYet()
        => Assert.Contains("Shadow", Fails("Development", ("Classification:Provider", "Shadow")).Message);

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Llm_IsRejectedOutsideDevelopment_UntilTheGateIsLifted(string environment)
    {
        var ex = Fails(environment, ("Classification:Provider", "Llm"), ("Classification:Llm:PrivacyApproved", "true"), ("Anthropic:ApiKey", FakeKey));

        Assert.Contains("Classification:Provider", ex.Message);
        Assert.Contains("Development", ex.Message);
    }

    [Fact]
    public void Replay_IsRejectedOutsideDevelopment_NamingTheClientKey()
        => Assert.Contains("Classification:Llm:Client", Fails("Production", With()).Message);

    [Fact]
    public void TheAnthropicClient_RequiresTheApiKey_NamingIt()
    {
        var ex = Fails("Development", ("Classification:Provider", "Llm"), ("Classification:Llm:PrivacyApproved", "true"));

        Assert.Contains("Anthropic:ApiKey", ex.Message);
    }

    [Fact]
    public void TheLlm_RequiresThePrivacyApproval_NamingIt_AndNeverPrintingTheKey()
    {
        var ex = Fails("Development", ("Classification:Provider", "Llm"), ("Anthropic:ApiKey", FakeKey));

        Assert.Contains("Classification:Llm:PrivacyApproved", ex.Message);
        Assert.DoesNotContain(FakeKey, ex.Message);
    }

    [Fact]
    public void TheAnthropicClient_WithKeyAndApproval_Starts()
    {
        var options = Validate("Development", ("Classification:Provider", "Llm"), ("Classification:Llm:PrivacyApproved", "true"), ("Anthropic:ApiKey", FakeKey));

        Assert.Equal(("Llm", "Anthropic"), (options.Provider, options.Llm!.Client));
        Assert.DoesNotContain(FakeKey, options.ToString());
    }

    [Fact]
    public void Replay_InDevelopment_StartsWithoutAKey_WithTheDocumentedDefaults()
    {
        var llm = Validate("Development", With()).Llm!;

        Assert.Equal(("Replay", "claude-haiku-5-5", "low", false, 2048), (llm.Client, llm.Model, llm.Effort, llm.DisableThinking, llm.MaxTokens));
        Assert.Equal((8, 0, 5, 13), (llm.TimeoutSeconds, llm.MaxRetries, llm.OtherWaitsSeconds, llm.TotalBudgetSeconds));
        Assert.Equal((2, 5, 300), (llm.MaxFallbackRetries, llm.BreakerFailures, llm.BreakerCooldownSeconds));
        Assert.Equal((300, true, false, (string?)null), (llm.DebounceSeconds, llm.IncludeSandbox, llm.ExtractAgeRange, llm.AgeRangeLegalRef));
        Assert.Equal(LlmPricing.Default, llm.Pricing);
        Assert.Equal(ConversationPreprocessorOptions.Default, llm.Preprocessing);
    }

    [Fact]
    public void TheLocalOverlay_WithTheSimulatorDebounce_PassesValidation()
    {
        // D26 / CC-14 S2: exactly the values of docker-compose.llm-dev.yml plus docker-compose.simulator.yml.
        var options = Validate("Development",
            ("Classification:Provider", "Llm"), ("Classification:Llm:Client", "Replay"), ("Classification:Llm:PrivacyApproved", "true"),
            ("Classification:Llm:TimeoutSeconds", "8"), ("Classification:Llm:MaxRetries", "0"), ("Classification:DebounceSeconds", "5"),
            ("Classification:Llm:DebounceSeconds", "5"), ("Conversations:AutoCloseIntervalMinutes", "1"));

        Assert.Equal((5, 5), (options.DebounceSeconds, options.Llm!.DebounceSeconds));
    }

    [Theory]
    [InlineData("Classification:Llm:TimeoutSeconds", "9")]
    [InlineData("Classification:Llm:TimeoutSeconds", "0")]
    [InlineData("Classification:Llm:MaxRetries", "1")]
    [InlineData("Classification:Llm:MaxTokens", "10")]
    [InlineData("Classification:Llm:Effort", "extreme")]
    [InlineData("Classification:Llm:Thinking", "sometimes")]
    [InlineData("Classification:Llm:MaxFallbackRetries", "-1")]
    [InlineData("Classification:Llm:Breaker:Failures", "0")]
    [InlineData("Classification:Llm:Breaker:CooldownSeconds", "0")]
    [InlineData("Classification:Llm:DebounceSeconds", "-5")]
    [InlineData("Classification:Llm:IncludeSandbox", "perhaps")]
    [InlineData("Classification:Llm:Model", "Claude Haiku!")]
    [InlineData("Classification:Llm:Pricing:InputPerMTok", "-1")]
    [InlineData("Classification:Llm:Pricing:CacheReadPerMTok", "cheap")]
    [InlineData("Classification:Llm:Preprocessing:TokenBudget", "10")]
    [InlineData("Classification:Llm:Preprocessing:AdvisorMaxCharacters", "5000")]
    [InlineData("Classification:Llm:Preprocessing:KeepLastLines", "0")]
    public void ABadValue_FailsNamingItsKey(string key, string value)
        => Assert.Contains(key, Fails("Development", With((key, value))).Message);

    [Fact]
    public void DisabledThinking_IsAcceptedOnlyAtEffortHighOrBelow()
    {
        Assert.True(Validate("Development", With(("Classification:Llm:Thinking", "Disabled"), ("Classification:Llm:Effort", "high"))).Llm!.DisableThinking);
        Assert.Contains("Classification:Llm:Thinking", Fails("Development", With(("Classification:Llm:Thinking", "Disabled"), ("Classification:Llm:Effort", "xhigh"))).Message);
    }

    [Theory]
    [InlineData("Classification:ProcessingTimeoutSeconds", "13")]
    [InlineData("Classification:RetryDelaySeconds", "13")]
    public void TheStageATimingBudget_MustFitInsideTheTransactionAndTheRetryDelay_NamingTheValues(string key, string value)
    {
        // CC-13 S3: TotalBudget = TimeoutSeconds x (MaxRetries + 1) + OtherWaitsSeconds = 8 + 5 = 13 must be < both.
        var ex = Fails("Development", With((key, value)));

        Assert.Contains("Classification:Llm:TotalBudget", ex.Message);
        Assert.Contains(key, ex.Message);
        Assert.Contains("13", ex.Message);
    }

    [Fact]
    public void AskingTheAgeRange_RequiresTheLegalReference()
    {
        // CP-5 S4.
        var ex = Fails("Development", With(("Classification:Llm:ExtractAgeRange", "true")));
        Assert.Contains("Classification:Llm:AgeRangeLegalRef", ex.Message);

        var options = Validate("Development", With(("Classification:Llm:ExtractAgeRange", "true"), ("Classification:Llm:AgeRangeLegalRef", "ACTA-2026-001")));
        Assert.True(options.Llm!.ExtractAgeRange);
    }

    [Fact]
    public void TheHardeningSettings_HaveSafeDefaults()
    {
        var llm = Validate("Development", With()).Llm!;

        Assert.False(llm.AllowRealClientNonSandbox);
        Assert.Equal(new Uri("https://api.anthropic.com"), llm.BaseUrl);
        Assert.Null(llm.HashKey);
        Assert.Equal(5000, llm.ClaimWarningMilliseconds);
    }

    [Theory]
    [InlineData("Classification:Llm:BaseUrl", "http://api.anthropic.com")]
    [InlineData("Classification:Llm:BaseUrl", "not a url")]
    [InlineData("Classification:Llm:BaseUrl", "https://user:pass@api.anthropic.com")]
    [InlineData("Classification:Llm:AllowRealClientNonSandbox", "maybe")]
    [InlineData("Classification:Llm:ClaimWarningMilliseconds", "50")]
    [InlineData("Classification:Llm:HashKey", "not-base64!!")]
    [InlineData("Classification:Llm:HashKey", "c2hvcnQ=")]
    public void ABadHardeningValue_FailsNamingItsKey_NeverItsValue(string key, string value)
    {
        var ex = Fails("Development", With((key, value)));

        Assert.Contains(key, ex.Message);
        Assert.DoesNotContain(value, ex.Message);
    }

    [Fact]
    public void AHashKey_IsReadFromBase64()
    {
        var key = Enumerable.Range(0, 32).Select(i => (byte)(i * 7)).ToArray();

        var llm = Validate("Development", With(("Classification:Llm:HashKey", Convert.ToBase64String(key)))).Llm!;

        Assert.Equal(key, llm.HashKey);
        Assert.DoesNotContain(Convert.ToBase64String(key), llm.ToString());
    }

    [Fact]
    public void RealConversations_CanReachTheRealClientOnlyWhenAllowedExplicitly()
    {
        (string, string)[] real = [("Classification:Provider", "Llm"), ("Classification:Llm:PrivacyApproved", "true"), ("Anthropic:ApiKey", FakeKey)];

        Assert.False(Validate("Development", real).Llm!.AllowRealClientNonSandbox);
        Assert.True(Validate("Development", [.. real, ("Classification:Llm:AllowRealClientNonSandbox", "true")]).Llm!.AllowRealClientNonSandbox);
    }

    [Fact]
    public void Sandbox_CanBeExcludedExplicitly()
        => Assert.False(Validate("Development", With(("Classification:Llm:IncludeSandbox", "false"))).Llm!.IncludeSandbox);

    [Fact]
    public void AReplayFile_IsCheckedAtStartup()
        => Assert.Contains("Classification:Llm:ReplayPath", Fails("Development", With(("Classification:Llm:ReplayPath", Path.Combine(Path.GetTempPath(), "nope", "r.json")))).Message);
}
