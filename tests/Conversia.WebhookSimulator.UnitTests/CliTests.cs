using Conversia.WebhookSimulator.Cli;
using Conversia.WebhookSimulator.Generation;

namespace Conversia.WebhookSimulator.UnitTests;

public class CliTests
{
    // ---- the address ----

    [Theory]
    [InlineData("http://localhost:15030")]
    [InlineData("http://localhost:5030/")]
    [InlineData("https://LOCALHOST")]
    [InlineData("http://127.0.0.1:5030")]
    [InlineData("http://127.5.5.5")]
    [InlineData("http://[::1]:5030")]
    [InlineData("http://api.localhost:5030")]
    public void A_local_address_is_accepted_and_normalized(string url)
    {
        var uri = ApiUrlGuard.Check(url, allowRemote: false);

        Assert.EndsWith("/", uri.ToString());
    }

    [Theory]
    [InlineData("https://conversia.example.com")]
    [InlineData("http://192.168.1.20:5030")]
    [InlineData("http://10.0.0.5")]
    [InlineData("http://localhost.evil.example")]
    [InlineData("http://evil.example/localhost")]
    [InlineData("http://api.internal")]
    public void A_remote_address_is_refused_unless_it_is_allowed_on_purpose(string url)
    {
        var refused = Assert.Throws<UsageException>(() => ApiUrlGuard.Check(url, allowRemote: false));

        Assert.Contains("--allow-remote", refused.Message);
        Assert.Equal(new Uri(url).Host, ApiUrlGuard.Check(url, allowRemote: true).Host);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("localhost:5030")]
    [InlineData("ftp://localhost")]
    [InlineData("not a url")]
    [InlineData("http://user:pass@localhost:5030")]
    [InlineData("http://localhost:5030/?token=abc")]
    [InlineData("http://localhost:5030/#frag")]
    public void A_malformed_address_or_one_with_credentials_is_refused_even_when_remote_is_allowed(string url)
    {
        Assert.Throws<UsageException>(() => ApiUrlGuard.Check(url, allowRemote: true));
    }

    [Fact]
    public void A_refused_address_never_echoes_credentials_of_the_url()
    {
        var refused = Assert.Throws<UsageException>(() => ApiUrlGuard.Check("http://admin:hunter2@localhost:5030", allowRemote: false));

        Assert.DoesNotContain("hunter2", refused.Message);
    }

    // ---- the command line ----

    [Fact]
    public void Without_arguments_or_with_help_it_shows_the_help()
    {
        Assert.Equal(SimulatorCommandName.Help, SimulatorOptions.Parse([]).Command);
        Assert.Equal(SimulatorCommandName.Help, SimulatorOptions.Parse(["--help"]).Command);
        Assert.Equal(SimulatorCommandName.Help, SimulatorOptions.Parse(["help"]).Command);
    }

    [Fact]
    public void Setup_takes_the_address_and_the_secret_flags()
    {
        var options = SimulatorOptions.Parse(["setup", "--api-url", "http://localhost:15030", "--print-secret", "--rotate-secret"]);

        Assert.Equal(SimulatorCommandName.Setup, options.Command);
        Assert.Equal("http://localhost:15030", options.ApiUrl);
        Assert.True(options.PrintSecret);
        Assert.True(options.RotateSecret);
        Assert.False(options.AllowRemote);
    }

    [Fact]
    public void Populate_has_the_documented_defaults_and_takes_its_options_in_both_spellings()
    {
        var defaults = SimulatorOptions.Parse(["populate"]);
        var given = SimulatorOptions.Parse(["populate", "--conversations", "300", "--days=30", "--seed", "42", "--previous-share=0.25", "--rate", "5", "--no-assign"]);

        Assert.Equal((300, 30, 42), (defaults.Conversations, defaults.Days, defaults.Seed));
        Assert.Equal(SimulatorOptions.DefaultApiUrl, defaults.ApiUrl);
        Assert.Equal((300, 30, 42, 5, true), (given.Conversations, given.Days, given.Seed, given.Rate, given.NoAssign));
        Assert.Equal(0.25, given.PreviousShare);
    }

    [Fact]
    public void The_address_can_come_from_the_environment_and_the_command_line_wins()
    {
        Assert.Equal("http://localhost:7000", SimulatorOptions.Parse(["setup"], "http://localhost:7000").ApiUrl);
        Assert.Equal("http://localhost:8000", SimulatorOptions.Parse(["setup", "--api-url", "http://localhost:8000"], "http://localhost:7000").ApiUrl);
    }

    [Theory]
    [InlineData("--password")]
    [InlineData("--email")]
    [InlineData("--secret")]
    [InlineData("--webhook-secret")]
    [InlineData("--token")]
    [InlineData("--api-key")]
    public void Credentials_and_secrets_are_refused_on_the_command_line(string option)
    {
        var refused = Assert.Throws<UsageException>(() => SimulatorOptions.Parse(["setup", option, "hunter2-value"]));
        var refusedEquals = Assert.Throws<UsageException>(() => SimulatorOptions.Parse(["setup", option + "=hunter2-value"]));

        Assert.Contains("environment", refused.Message);
        Assert.DoesNotContain("hunter2-value", refused.Message);
        Assert.DoesNotContain("hunter2-value", refusedEquals.Message);
    }

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("setup --nope")]
    [InlineData("setup positional")]
    [InlineData("populate --conversations")]
    [InlineData("populate --conversations abc")]
    [InlineData("populate --conversations 0")]
    [InlineData("populate --conversations 99999")]
    [InlineData("populate --days 0")]
    [InlineData("populate --days 1000")]
    [InlineData("populate --seed -1")]
    [InlineData("populate --rate 0")]
    [InlineData("populate --previous-share 0.9")]
    [InlineData("populate --previous-share abc")]
    [InlineData("play --scenario nonsense")]
    public void Bad_commands_and_options_are_refused_with_a_message(string line)
    {
        Assert.Throws<UsageException>(() => SimulatorOptions.Parse(line.Split(' ')));
    }

    [Fact]
    public void Play_defaults_to_all_and_knows_every_scenario()
    {
        Assert.Equal("all", SimulatorOptions.Parse(["play"]).Scenario);
        foreach (var name in Scenarios.Names)
        {
            Assert.Equal(name, SimulatorOptions.Parse(["play", "--scenario", name.ToUpperInvariant()]).Scenario);
        }
    }

    // ---- the environment ----

    private static SimulatorEnvironment Env(Dictionary<string, string?> values) => new(name => values.GetValueOrDefault(name));

    [Fact]
    public void The_login_comes_from_the_environment_and_a_missing_variable_is_named_never_its_value()
    {
        var (email, password) = Env(new() { ["SIM_EMAIL"] = " owner@example.test ", ["SIM_PASSWORD"] = "pw-value" }).Credentials();
        var refused = Assert.Throws<UsageException>(() => Env(new() { ["SIM_EMAIL"] = "owner@example.test" }).Credentials());

        Assert.Equal(("owner@example.test", "pw-value"), (email, password));
        Assert.Contains("SIM_PASSWORD", refused.Message);
        Assert.DoesNotContain("owner@example.test", refused.Message);
        Assert.Contains("SIM_EMAIL and SIM_PASSWORD", Assert.Throws<UsageException>(() => Env(new()).Credentials()).Message);
    }

    [Fact]
    public void A_branch_uses_its_own_secret_else_the_single_one()
    {
        var environment = Env(new() { ["SIM_WEBHOOK_SECRETS"] = "Centro=aaa; norte = bbb ;Sur=", ["SIM_WEBHOOK_SECRET"] = "fallback" });

        Assert.Equal("aaa", environment.SecretFor("Centro"));
        Assert.Equal("bbb", environment.SecretFor("Norte"));
        Assert.Equal("fallback", environment.SecretFor("Sur")); // listed without a value: ignored
        Assert.Equal("fallback", environment.SecretFor("Poblado"));
        Assert.Null(Env(new()).SecretFor("Centro"));
    }

    [Fact]
    public void Secrets_with_equals_signs_in_the_value_survive()
    {
        var secrets = SimulatorEnvironment.ParseSecrets("Centro=abc=def==;Norte=x");

        Assert.Equal("abc=def==", secrets["Centro"]);
        Assert.Equal("x", secrets["norte"]);
    }
}
