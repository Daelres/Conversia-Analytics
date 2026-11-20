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
    [InlineData("Classification:Llm:TimeoutSeconds", "16")]
    [InlineData("Classification:Llm:TimeoutSeconds", "0")]
    [InlineData("Classification:Llm:MaxRetries", "2")]
    [InlineData("Classification:Llm:MaxConcurrent", "0")]
    [InlineData("Classification:Llm:MaxConcurrent", "17")]
    [InlineData("Classification:Workers", "0")]
    [InlineData("Classification:Llm:Batch:Enabled", "perhaps")]
    [InlineData("Classification:Llm:Batch:MaxRequests", "100001")]
    [InlineData("Classification:Llm:Batch:PollSeconds", "0")]
    [InlineData("Classification:Llm:Batch:DiscountFactor", "1.5")]
    [InlineData("Classification:Llm:PerCompanyConcurrency", "0")]
    [InlineData("Classification:Llm:Batch:PoisonGraceHours", "0")]
    [InlineData("Classification:Llm:Batch:RetentionDays", "0")]
    [InlineData("Classification:Schedule:DefaultNightlyHour", "24")]
    [InlineData("Classification:Schedule:NightlyWindowHours", "0")]
    [InlineData("Classification:Schedule:NightlyWindowHours", "13")]
    [InlineData("Classification:Schedule:SpreadMinutes", "-1")]
    [InlineData("Classification:Schedule:MaxConversationsPerWindow", "0")]
    [InlineData("Classification:Schedule:TimeZone", "Mars/Olympus")]
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

    [Fact]
    public void TheCallBudget_MustEndBeforeTheLease_TheRetryDelay_NamingTheValues()
    {
        // Design H1: TotalBudget = TimeoutSeconds x (MaxRetries + 1) + OtherWaitsSeconds = 8 + 5 = 13 must be shorter than the lease.
        var ex = Fails("Development", With(("Classification:RetryDelaySeconds", "13")));

        Assert.Contains("Classification:Llm:TotalBudget", ex.Message);
        Assert.Contains("Classification:RetryDelaySeconds", ex.Message);
        Assert.Contains("13", ex.Message);
    }

    [Fact]
    public void TheCall_IsNoLongerBoundByTheTransactionTimeout_AndMayTakeFifteenSecondsWithOneRetry()
    {
        // The call runs outside any transaction (design H1): a short transaction timeout no longer limits it; 15 x 2 + 5 = 35 < 60.
        var llm = Validate("Development", With(
            ("Classification:ProcessingTimeoutSeconds", "5"), ("Classification:Llm:TimeoutSeconds", "15"), ("Classification:Llm:MaxRetries", "1"))).Llm!;

        Assert.Equal((15, 1, 35), (llm.TimeoutSeconds, llm.MaxRetries, llm.TotalBudgetSeconds));
    }

    [Fact]
    public void TheConcurrencyAndBatchSettings_HaveTheDocumentedDefaults_AndWorkersFollowMaxConcurrent()
    {
        var llm = Validate("Development", With()).Llm!;
        Assert.Equal((4, 4, true, 1000, 60, 0.5m), (llm.MaxConcurrent, llm.Workers, llm.BatchEnabled, llm.BatchMaxRequests, llm.BatchPollSeconds, llm.BatchDiscountFactor));

        var two = Validate("Development", With(("Classification:Llm:MaxConcurrent", "2"))).Llm!;
        Assert.Equal((2, 2), (two.MaxConcurrent, two.Workers));
        Assert.Equal(6, Validate("Development", With(("Classification:Llm:MaxConcurrent", "2"), ("Classification:Workers", "6"))).Llm!.Workers);
    }

    [Fact]
    public void TheReviewSettings_HaveTheDocumentedDefaults()
    {
        var llm = Validate("Development", With()).Llm!;

        Assert.Equal((2, 48, 30), (llm.PerCompanyConcurrency, llm.BatchPoisonGraceHours, llm.BatchRetentionDays));
    }

    [Theory]
    [InlineData("Production", true, false, true)]   // batch on, no hash key, outside Development: refused
    [InlineData("Production", true, true, false)]
    [InlineData("Production", false, false, false)] // no batch: the custom id is never sent
    [InlineData("Development", true, false, false)]
    public void BatchMode_OutsideDevelopment_RequiresTheHashKey_BecauseTheCustomIdCarriesAHashPrefix(string environment, bool batch, bool key, bool refused)
    {
        var llm = LlmOptions.Default(development: environment == "Development") with { BatchEnabled = batch, HashKey = key ? new byte[32] : null };

        var error = Record.Exception(() => ClassificationServiceCollectionExtensions.ValidateBatchHashKey(environment, llm));

        if (refused)
        {
            Assert.Contains("Classification:Llm:HashKey", Assert.IsType<InvalidOperationException>(error).Message);
            Assert.Contains("Classification:Llm:Batch:Enabled", error.Message);
        }
        else
        {
            Assert.Null(error);
        }
    }

    [Fact]
    public void TheSchedule_IsReadWhateverTheProvider_WithTheDocumentedDefaults()
    {
        var rules = Validate("Production").ScheduleSettings;
        Assert.Equal(("America/Bogota", 1, 5, 60, 2000), (rules.TimeZone.Id, rules.DefaultNightlyHour, rules.NightlyWindowHours, rules.SpreadMinutes, rules.MaxConversationsPerWindow));

        var custom = Validate("Production", ("Classification:Schedule:DefaultNightlyHour", "2"), ("Classification:Schedule:SpreadMinutes", "0")).ScheduleSettings;
        Assert.Equal((2, 0), (custom.DefaultNightlyHour, custom.SpreadMinutes));
    }

    [Theory]
    [InlineData("Maximum Pool Size=100", 4, 4, true)]   // 4 x 2 + 1 + 4 + 20 = 33
    [InlineData("Maximum Pool Size=32", 4, 4, false)]
    [InlineData("Maximum Pool Size=33", 4, 4, true)]
    [InlineData("Maximum Pool Size=50", 16, 16, false)] // 16 x 2 + 1 + 16 + 20 = 69
    public void ThePoolSize_MustHoldEveryLlmWorker_NamingBothValues(string pool, int maxConcurrent, int workers, bool accepted)
    {
        var llm = LlmOptions.Default(development: true) with { MaxConcurrent = maxConcurrent, Workers = workers };
        var connection = $"Host=localhost;Database=conversia;Username=u;{pool}";

        if (accepted)
        {
            ClassificationServiceCollectionExtensions.ValidatePoolSize(connection, llm);
        }
        else
        {
            var ex = Assert.Throws<InvalidOperationException>(() => ClassificationServiceCollectionExtensions.ValidatePoolSize(connection, llm));
            Assert.Contains("Maximum Pool Size", ex.Message);
            Assert.Contains("Classification:Llm:MaxConcurrent", ex.Message);
            Assert.Contains("Classification:Workers", ex.Message);
        }
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
