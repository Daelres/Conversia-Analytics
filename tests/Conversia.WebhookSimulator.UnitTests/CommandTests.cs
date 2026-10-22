using Conversia.Application.Ingestion;
using Conversia.Domain.Enums;
using Conversia.WebhookSimulator.Api;
using Conversia.WebhookSimulator.Cli;
using Conversia.WebhookSimulator.Commands;
using Conversia.WebhookSimulator.Generation;

namespace Conversia.WebhookSimulator.UnitTests;

public class CommandTests
{
    private sealed class FrozenClock : TimeProvider
    {
        private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static Task NoDelay(TimeSpan span, CancellationToken token) => Task.CompletedTask;

    private static async Task<ConversiaApi> LoggedInAsync(FakeConversiaApi fake)
    {
        var api = new ConversiaApi(fake.CreateClient(), NoDelay);
        await api.LoginAsync(FakeConversiaApi.Email, FakeConversiaApi.Password, CancellationToken.None);
        return api;
    }

    private static SimulatorEnvironment EnvironmentWith(params (string Name, string? Value)[] values)
    {
        var map = values.ToDictionary(v => v.Name, v => v.Value);
        return new SimulatorEnvironment(name => map.GetValueOrDefault(name));
    }

    private static SimulatorEnvironment SecretsOf(FakeConversiaApi fake)
        => EnvironmentWith((SimulatorEnvironment.SecretsVariable, string.Join(';', PilotBusiness.Branches.Select(b => $"{b.Name}={fake.SecretOf(b.Name)}"))));

    private static async Task<(FakeConversiaApi Fake, ConversiaApi Api)> SetUpWorkspaceAsync()
    {
        var fake = new FakeConversiaApi();
        var api = await LoggedInAsync(fake);
        await new SetupCommand(api, new StringWriter()).RunAsync(new SimulatorOptions { Command = SimulatorCommandName.Setup }, CancellationToken.None);
        return (fake, api);
    }

    // ---- a workspace that is not active yet ----

    private static SimulatorEnvironment AdminEnv() => EnvironmentWith(
        (SimulatorEnvironment.AdminEmailVariable, FakeConversiaApi.AdminEmail), (SimulatorEnvironment.AdminPasswordVariable, FakeConversiaApi.AdminPassword));

    [Fact]
    public async Task Setup_switches_on_the_numbers_it_creates_because_a_new_line_starts_off()
    {
        var (fake, _) = await SetUpWorkspaceAsync();

        Assert.Equal(4, fake.Channels.Count);
        Assert.All(fake.Channels, c => Assert.True(c.IsActive));
    }

    [Fact]
    public async Task Setup_on_a_pending_workspace_without_admin_credentials_stops_and_says_what_to_do()
    {
        var fake = new FakeConversiaApi { Status = TenantStatus.Pending, MaxActiveLines = 0 };

        var failure = await Assert.ThrowsAsync<UsageException>(() =>
            new SetupCommand(LoggedInAsync(fake).Result, new StringWriter(), EnvironmentWith()).RunAsync(new SimulatorOptions { Command = SimulatorCommandName.Setup }, CancellationToken.None));

        Assert.Contains("pending activation", failure.Message);
        Assert.Contains("SIM_ADMIN_EMAIL", failure.Message);
        Assert.Empty(fake.Branches); // it touched nothing
        Assert.Empty(fake.AdminCalls);
    }

    [Fact]
    public async Task Setup_on_a_pending_workspace_with_admin_credentials_activates_it_sets_the_quota_and_switches_the_numbers_on()
    {
        var fake = new FakeConversiaApi { Status = TenantStatus.Pending, MaxActiveLines = 0 };
        var output = new StringWriter();

        await new SetupCommand(await LoggedInAsync(fake), output, AdminEnv()).RunAsync(new SimulatorOptions { Command = SimulatorCommandName.Setup, LineQuota = 4 }, CancellationToken.None);

        Assert.Equal((TenantStatus.Active, 4), (fake.Status, fake.MaxActiveLines));
        Assert.Equal(4, fake.Channels.Count(c => c.IsActive));
        Assert.Contains("Activated the workspace", output.ToString());
        // The administrator's token went only to the admin API, and the owner's token only to the rest.
        Assert.All(fake.AdminCalls, call => Assert.Equal(fake.IssuedAdminToken, call.Token));
        Assert.DoesNotContain(fake.IssuedAdminToken, fake.OwnerTokensSeen);
        Assert.DoesNotContain(FakeConversiaApi.AdminPassword, output.ToString());
    }

    [Fact]
    public async Task Setup_raises_a_quota_that_is_too_small_and_honours_line_quota()
    {
        var fake = new FakeConversiaApi { Status = TenantStatus.Active, MaxActiveLines = 1 };

        await new SetupCommand(await LoggedInAsync(fake), new StringWriter(), AdminEnv()).RunAsync(new SimulatorOptions { Command = SimulatorCommandName.Setup, LineQuota = 6 }, CancellationToken.None);

        Assert.Equal(6, fake.MaxActiveLines);
        Assert.Equal(4, fake.Channels.Count(c => c.IsActive));
    }

    [Fact]
    public async Task Setup_with_a_full_quota_and_no_admin_says_the_number_stays_off()
    {
        var fake = new FakeConversiaApi { Status = TenantStatus.Active, MaxActiveLines = 2 };

        var failure = await Assert.ThrowsAsync<UsageException>(() =>
            new SetupCommand(LoggedInAsync(fake).Result, new StringWriter(), EnvironmentWith()).RunAsync(new SimulatorOptions { Command = SimulatorCommandName.Setup }, CancellationToken.None));

        Assert.Contains("quota", failure.Message);
        Assert.Equal(2, fake.Channels.Count(c => c.IsActive));
    }

    [Fact]
    public async Task A_wrong_admin_login_is_refused_without_echoing_the_credentials()
    {
        var fake = new FakeConversiaApi { Status = TenantStatus.Pending, MaxActiveLines = 0 };
        var env = EnvironmentWith((SimulatorEnvironment.AdminEmailVariable, FakeConversiaApi.AdminEmail), (SimulatorEnvironment.AdminPasswordVariable, "the-wrong-admin-password"));

        var failure = await Assert.ThrowsAsync<ApiFailure>(() =>
            new SetupCommand(LoggedInAsync(fake).Result, new StringWriter(), env).RunAsync(new SimulatorOptions { Command = SimulatorCommandName.Setup }, CancellationToken.None));

        Assert.DoesNotContain("the-wrong-admin-password", failure.Message);
        Assert.DoesNotContain(FakeConversiaApi.AdminEmail, failure.Message);
    }

    [Fact]
    public void Admin_credentials_come_from_the_environment_both_or_neither()
    {
        Assert.Null(EnvironmentWith().AdminCredentials());
        Assert.Equal((FakeConversiaApi.AdminEmail, FakeConversiaApi.AdminPassword), AdminEnv().AdminCredentials());
        Assert.Throws<UsageException>(() => EnvironmentWith((SimulatorEnvironment.AdminEmailVariable, "a@b.test")).AdminCredentials());
        Assert.Throws<UsageException>(() => EnvironmentWith((SimulatorEnvironment.AdminPasswordVariable, "x")).AdminCredentials());
    }

    [Fact]
    public void Line_quota_is_a_flag_and_credentials_are_still_refused_on_the_command_line()
    {
        Assert.Equal(7, SimulatorOptions.Parse(["setup", "--line-quota", "7"]).LineQuota);
        Assert.Equal(4, SimulatorOptions.Parse(["setup"]).LineQuota);
        Assert.Throws<UsageException>(() => SimulatorOptions.Parse(["setup", "--line-quota", "1001"]));
        Assert.Throws<UsageException>(() => SimulatorOptions.Parse(["setup", "--admin-password", "x"]));
    }

    // ---- login ----

    [Fact]
    public async Task A_wrong_login_is_refused_without_echoing_the_credentials()
    {
        var fake = new FakeConversiaApi();
        var api = new ConversiaApi(fake.CreateClient(), NoDelay);

        var failure = await Assert.ThrowsAsync<ApiFailure>(() => api.LoginAsync(FakeConversiaApi.Email, "the-wrong-password", CancellationToken.None));

        Assert.Contains("SIM_EMAIL and SIM_PASSWORD", failure.Message);
        Assert.DoesNotContain("the-wrong-password", failure.Message);
        Assert.DoesNotContain(FakeConversiaApi.Email, failure.Message);
    }

    // ---- setup ----

    [Fact]
    public async Task Setup_creates_the_branches_advisors_and_one_number_per_branch_and_prints_paths_but_no_secret()
    {
        var fake = new FakeConversiaApi();
        var output = new StringWriter();

        var code = await new SetupCommand(await LoggedInAsync(fake), output).RunAsync(new SimulatorOptions { Command = SimulatorCommandName.Setup }, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal(PilotBusiness.Branches.Select(b => b.Name), fake.Branches.Select(b => b.Name));
        Assert.Equal(PilotBusiness.Advisors.Count, fake.Advisors.Count);
        Assert.Equal(PilotBusiness.Branches.Count, fake.Channels.Count);
        var text = output.ToString();
        Assert.All(fake.Channels, channel => Assert.Contains(channel.WebhookPath!, text));
        Assert.All(fake.IssuedSecrets.Values, secret => Assert.DoesNotContain(secret, text));
        Assert.DoesNotContain(FakeConversiaApi.Password, text);
        Assert.DoesNotContain("export", text);
        Assert.Contains("--rotate-secret --print-secret", text);
    }

    [Fact]
    public async Task Setup_prints_the_secrets_once_only_when_asked_in_a_line_to_paste()
    {
        var fake = new FakeConversiaApi();
        var output = new StringWriter();

        await new SetupCommand(await LoggedInAsync(fake), output).RunAsync(new SimulatorOptions { Command = SimulatorCommandName.Setup, PrintSecret = true }, CancellationToken.None);

        var text = output.ToString();
        foreach (var branch in PilotBusiness.Branches)
        {
            Assert.Contains($"{branch.Name}={fake.SecretOf(branch.Name)}", text);
        }

        Assert.Contains("export SIM_WEBHOOK_SECRETS='", text);
        Assert.DoesNotContain("--rotate-secret --print-secret", text);
    }

    [Fact]
    public async Task Setup_is_idempotent_a_second_run_creates_nothing_and_shows_no_secret()
    {
        var (fake, api) = await SetUpWorkspaceAsync();
        var before = (fake.Branches.Count, fake.Advisors.Count, fake.Channels.Count);
        var secretsBefore = fake.IssuedSecrets.ToDictionary(p => p.Key, p => p.Value);
        var output = new StringWriter();

        await new SetupCommand(api, output).RunAsync(new SimulatorOptions { Command = SimulatorCommandName.Setup, PrintSecret = true }, CancellationToken.None);

        Assert.Equal(before, (fake.Branches.Count, fake.Advisors.Count, fake.Channels.Count));
        Assert.Equal(secretsBefore, fake.IssuedSecrets);
        Assert.All(fake.IssuedSecrets.Values, secret => Assert.DoesNotContain(secret, output.ToString()));
        Assert.DoesNotContain(fake.Calls, call => call.StartsWith("POST", StringComparison.Ordinal) && !call.Contains("auth/login") && fake.Calls.IndexOf(call) > fake.Calls.Count - 5);
        Assert.Contains("Everything was already there", output.ToString());
    }

    [Fact]
    public async Task Setup_matches_existing_names_without_regard_to_case_and_keeps_the_ones_that_are_not_the_simulators()
    {
        var fake = new FakeConversiaApi();
        fake.Branches.Add(new BranchResponseBuilder("centro").Build());
        fake.Branches.Add(new BranchResponseBuilder("Sede de otra persona").Build());
        var api = await LoggedInAsync(fake);

        await new SetupCommand(api, new StringWriter()).RunAsync(new SimulatorOptions { Command = SimulatorCommandName.Setup }, CancellationToken.None);

        Assert.Single(fake.Branches, b => b.Name.Equals("centro", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(fake.Branches, b => b.Name == "Sede de otra persona");
        Assert.Equal(PilotBusiness.Branches.Count + 1, fake.Branches.Count);
    }

    [Fact]
    public async Task Rotating_gives_new_secrets_and_the_old_ones_stop_working()
    {
        var (fake, api) = await SetUpWorkspaceAsync();
        var old = fake.SecretOf("Centro");
        var output = new StringWriter();

        await new SetupCommand(api, output).RunAsync(new SimulatorOptions { Command = SimulatorCommandName.Setup, RotateSecret = true, PrintSecret = true }, CancellationToken.None);

        Assert.NotEqual(old, fake.SecretOf("Centro"));
        Assert.Contains($"Centro={fake.SecretOf("Centro")}", output.ToString());
        Assert.DoesNotContain(old, output.ToString());
        var sender = new WebhookSender(fake.CreateClient(), 1000, NoDelay);
        var path = fake.Channels.First().WebhookPath!;
        await Assert.ThrowsAsync<ApiFailure>(() => sender.SendAsync(path, old, "{}", CancellationToken.None));
    }

    // ---- play ----

    [Fact]
    public async Task Play_sends_the_scenario_to_a_branch_with_its_secret_and_nothing_secret_is_printed()
    {
        var (fake, api) = await SetUpWorkspaceAsync();
        var output = new StringWriter();
        var play = new PlayCommand(api, new WebhookSender(fake.CreateClient(), 1000, NoDelay), SecretsOf(fake), TimeProvider.System, output);

        var code = await play.RunAsync(new SimulatorOptions { Command = SimulatorCommandName.Play, Scenario = "booking", Branch = "norte" }, CancellationToken.None);

        Assert.Equal(0, code);
        var norte = fake.Channels.Single(c => c.BranchId == fake.Branches.Single(b => b.Name == "Norte").Id);
        Assert.NotEmpty(fake.Deliveries);
        Assert.All(fake.Deliveries, d => Assert.Equal(norte.ChannelKey, d.Key));
        Assert.Contains(fake.Conversations, c => c.BranchName == "Norte");
        Assert.All(fake.IssuedSecrets.Values, secret => Assert.DoesNotContain(secret, output.ToString()));
        Assert.DoesNotContain(norte.ChannelKey!, output.ToString());
    }

    [Fact]
    public async Task Play_all_runs_every_scenario_and_the_duplicate_adds_nothing()
    {
        var (fake, api) = await SetUpWorkspaceAsync();
        var play = new PlayCommand(api, new WebhookSender(fake.CreateClient(), 1000, NoDelay), SecretsOf(fake), TimeProvider.System, new StringWriter());

        await play.RunAsync(new SimulatorOptions { Command = SimulatorCommandName.Play, Scenario = "all" }, CancellationToken.None);

        Assert.True(fake.Deliveries.Count > Scenarios.Names.Length);
        // bsuid creates no conversation; duplicate creates one with one message although it sent three bodies.
        Assert.True(fake.Conversations.Count >= Scenarios.Names.Length - 1);
        Assert.Contains(fake.Conversations, c => c.MessageCount == 1 && c.Source == "Organic");
    }

    [Fact]
    public async Task Play_without_a_secret_says_which_variable_to_set_and_never_asks_for_it_as_an_argument()
    {
        var (fake, api) = await SetUpWorkspaceAsync();
        var play = new PlayCommand(api, new WebhookSender(fake.CreateClient(), 1000, NoDelay), EnvironmentWith(), TimeProvider.System, new StringWriter());

        var refused = await Assert.ThrowsAsync<UsageException>(() => play.RunAsync(new SimulatorOptions { Command = SimulatorCommandName.Play, Scenario = "booking" }, CancellationToken.None));

        Assert.Contains("SIM_WEBHOOK_SECRETS", refused.Message);
        Assert.Empty(fake.Deliveries);
    }

    [Fact]
    public async Task Play_before_setup_asks_for_setup()
    {
        var fake = new FakeConversiaApi();
        var play = new PlayCommand(await LoggedInAsync(fake), new WebhookSender(fake.CreateClient(), 1000, NoDelay), EnvironmentWith(), TimeProvider.System, new StringWriter());

        var refused = await Assert.ThrowsAsync<UsageException>(() => play.RunAsync(new SimulatorOptions { Command = SimulatorCommandName.Play }, CancellationToken.None));

        Assert.Contains("setup", refused.Message);
    }

    [Fact]
    public async Task Play_with_an_unknown_branch_lists_the_good_ones()
    {
        var (fake, api) = await SetUpWorkspaceAsync();
        var play = new PlayCommand(api, new WebhookSender(fake.CreateClient(), 1000, NoDelay), SecretsOf(fake), TimeProvider.System, new StringWriter());

        var refused = await Assert.ThrowsAsync<UsageException>(() => play.RunAsync(new SimulatorOptions { Command = SimulatorCommandName.Play, Branch = "Atlantida" }, CancellationToken.None));

        Assert.Contains("Centro", refused.Message);
    }

    [Fact]
    public async Task A_branch_with_another_number_besides_the_simulators_still_uses_the_simulators_own()
    {
        var (fake, api) = await SetUpWorkspaceAsync();
        var centro = fake.Branches.Single(b => b.Name == "Centro");
        fake.Channels.Add(new Conversia.Application.DTOs.Organization.ChannelResponse
        {
            Id = Guid.NewGuid(), BranchId = centro.Id, DisplayPhoneNumber = "+57 300 555 0199", IsActive = true, CreatedAt = DateTime.UtcNow.AddDays(-30),
            ChannelKey = "key-someone-added", WebhookPath = "/api/webhooks/360dialog/key-someone-added",
        });

        var targets = await ChannelTargets.ResolveAsync(api, SecretsOf(fake), CancellationToken.None);

        Assert.Equal(PilotBusiness.Branches.Count, targets.Count);
        Assert.DoesNotContain(targets, t => t.WebhookPath.Contains("someone-added"));
    }

    // ---- populate ----

    private static PopulateCommand Populate(FakeConversiaApi fake, ConversiaApi api, SimulatorEnvironment environment, TextWriter output, TimeProvider? time = null)
        => new(api, new WebhookSender(fake.CreateClient(), 1000, NoDelay), environment, time ?? TimeProvider.System, output, NoDelay);

    [Fact]
    public async Task Populate_sends_every_conversation_to_its_branch_waits_for_the_workers_and_assigns_the_advisors_of_that_branch()
    {
        var (fake, api) = await SetUpWorkspaceAsync();
        var output = new StringWriter();
        var options = new SimulatorOptions { Command = SimulatorCommandName.Populate, Conversations = 60, Days = 30, Seed = 42 };

        var code = await Populate(fake, api, SecretsOf(fake), output).RunAsync(options, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal(60, fake.Deliveries.Count);
        Assert.Equal(60, fake.Conversations.Count);
        Assert.Contains("60 new conversations stored", output.ToString());
        Assert.All(fake.Conversations, c => Assert.True(c.AdvisorId is null || fake.Advisors.Single(a => a.Id == c.AdvisorId).BranchId == c.BranchId));
        Assert.True(fake.Conversations.Count(c => c.AdvisorId is not null) > 30, "most conversations get an advisor");
        Assert.Contains(fake.Conversations, c => c.AdvisorId is null);
        Assert.Equal(fake.Conversations.Count(c => c.AdvisorId is not null), fake.Writes.Count);
        Assert.All(fake.Writes, w => Assert.Matches(@"^api/conversations/[0-9a-f-]{36}/advisor$", w.Path));
        Assert.Equal(PilotBusiness.Branches.Count, fake.Conversations.Select(c => c.BranchName).Distinct().Count());
    }

    [Fact]
    public async Task Populate_never_prints_a_secret_a_password_or_a_webhook_path_and_nothing_it_sends_is_in_the_future()
    {
        var (fake, api) = await SetUpWorkspaceAsync();
        var output = new StringWriter();
        var now = TimeProvider.System.GetUtcNow().UtcDateTime;

        await Populate(fake, api, SecretsOf(fake), output).RunAsync(new SimulatorOptions { Command = SimulatorCommandName.Populate, Conversations = 40 }, CancellationToken.None);

        var text = output.ToString();
        Assert.All(fake.IssuedSecrets.Values, secret => Assert.DoesNotContain(secret, text));
        Assert.All(fake.Channels, channel => Assert.DoesNotContain(channel.ChannelKey!, text));
        Assert.DoesNotContain(FakeConversiaApi.Password, text);
        var instants = fake.Deliveries.SelectMany(d => Dialog360PayloadParser.Parse(d.Body).Messages).Select(m => m.SentAt).ToList();
        Assert.All(instants, instant => Assert.True(instant <= DateTime.UtcNow, $"{instant:O} is in the future"));
        Assert.True(instants.Max() <= now.AddSeconds(1));
    }

    [Fact]
    public async Task Populate_again_with_the_same_seed_and_moment_adds_nothing()
    {
        var (fake, api) = await SetUpWorkspaceAsync();
        var clock = new FrozenClock();
        var options = new SimulatorOptions { Command = SimulatorCommandName.Populate, Conversations = 30, Seed = 7 };
        await Populate(fake, api, SecretsOf(fake), new StringWriter(), clock).RunAsync(options, CancellationToken.None);
        var messages = fake.Conversations.Sum(c => c.MessageCount);
        var writes = fake.Writes.Count;

        await Populate(fake, api, SecretsOf(fake), new StringWriter(), clock).RunAsync(options, CancellationToken.None);

        Assert.Equal(30, fake.Conversations.Count);
        Assert.Equal(messages, fake.Conversations.Sum(c => c.MessageCount));
        Assert.Equal(writes, fake.Writes.Count); // the advisors are already there: no second write
    }

    private sealed class SteppingClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public async Task Populate_again_hours_later_with_the_same_seed_adds_no_conversation_and_assigns_nobody_twice()
    {
        var (fake, api) = await SetUpWorkspaceAsync();
        var clock = new SteppingClock();
        var options = new SimulatorOptions { Command = SimulatorCommandName.Populate, Conversations = 40, Seed = 9 };
        await Populate(fake, api, SecretsOf(fake), new StringWriter(), clock).RunAsync(options, CancellationToken.None);
        var customers = fake.Conversations.Select(c => c.Contact.WaId).Order().ToList();
        var writes = fake.Writes.Count;

        clock.Advance(TimeSpan.FromHours(5));
        await Populate(fake, api, SecretsOf(fake), new StringWriter(), clock).RunAsync(options, CancellationToken.None);

        Assert.Equal(customers, fake.Conversations.Select(c => c.Contact.WaId).Order().ToList());
        Assert.Equal(writes, fake.Writes.Count);
    }

    [Fact]
    public async Task Populate_goes_on_when_the_api_refuses_an_advisor_for_a_conversation()
    {
        var (fake, api) = await SetUpWorkspaceAsync();
        fake.RefuseAssignments = true;
        var output = new StringWriter();

        var code = await Populate(fake, api, SecretsOf(fake), output).RunAsync(new SimulatorOptions { Command = SimulatorCommandName.Populate, Conversations = 25 }, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Contains("refused by the API", output.ToString());
        Assert.Equal(25, fake.Conversations.Count);
    }

    [Fact]
    public async Task Populate_can_skip_the_assignment()
    {
        var (fake, api) = await SetUpWorkspaceAsync();

        await Populate(fake, api, SecretsOf(fake), new StringWriter()).RunAsync(new SimulatorOptions { Command = SimulatorCommandName.Populate, Conversations = 20, NoAssign = true }, CancellationToken.None);

        Assert.Empty(fake.Writes);
        Assert.All(fake.Conversations, c => Assert.Null(c.AdvisorId));
    }

    [Fact]
    public async Task Populate_waits_out_a_throttled_listing()
    {
        var (fake, api) = await SetUpWorkspaceAsync();
        fake.ThrottleListings = 3;

        var code = await Populate(fake, api, SecretsOf(fake), new StringWriter()).RunAsync(new SimulatorOptions { Command = SimulatorCommandName.Populate, Conversations = 20 }, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal(20, fake.Conversations.Count);
        Assert.Equal(0, fake.ThrottleListings);
    }

    [Fact]
    public async Task Populate_names_the_branches_that_have_no_secret_and_sends_nothing()
    {
        var (fake, api) = await SetUpWorkspaceAsync();
        var environment = EnvironmentWith((SimulatorEnvironment.SecretsVariable, $"Centro={fake.SecretOf("Centro")}"));

        var refused = await Assert.ThrowsAsync<UsageException>(
            () => Populate(fake, api, environment, new StringWriter()).RunAsync(new SimulatorOptions { Command = SimulatorCommandName.Populate, Conversations = 10 }, CancellationToken.None));

        Assert.Contains("Norte", refused.Message);
        Assert.Contains("Sur", refused.Message);
        Assert.DoesNotContain("Centro,", refused.Message);
        Assert.DoesNotContain(fake.SecretOf("Centro"), refused.Message);
        Assert.Empty(fake.Deliveries);
    }

    [Fact]
    public async Task Populate_with_a_wrong_secret_stops_at_the_first_401()
    {
        var (fake, api) = await SetUpWorkspaceAsync();
        var environment = EnvironmentWith((SimulatorEnvironment.SecretVariable, "not-the-secret"));

        var failure = await Assert.ThrowsAsync<ApiFailure>(
            () => Populate(fake, api, environment, new StringWriter()).RunAsync(new SimulatorOptions { Command = SimulatorCommandName.Populate, Conversations = 50 }, CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, failure.Status);
        Assert.Empty(fake.Deliveries);
        Assert.DoesNotContain("not-the-secret", failure.Message);
        Assert.Single(fake.Calls, c => c.StartsWith("POST /api/webhooks", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Populate_before_setup_asks_for_setup()
    {
        var fake = new FakeConversiaApi();
        var api = await LoggedInAsync(fake);

        var refused = await Assert.ThrowsAsync<UsageException>(
            () => Populate(fake, api, EnvironmentWith(), new StringWriter()).RunAsync(new SimulatorOptions { Command = SimulatorCommandName.Populate }, CancellationToken.None));

        Assert.Contains("setup", refused.Message);
    }

    [Fact]
    public async Task The_deliveries_of_a_populate_hold_the_customers_and_the_advisors_of_each_chat()
    {
        var (fake, api) = await SetUpWorkspaceAsync();

        await Populate(fake, api, SecretsOf(fake), new StringWriter()).RunAsync(new SimulatorOptions { Command = SimulatorCommandName.Populate, Conversations = 40 }, CancellationToken.None);

        var parsed = fake.Deliveries.Select(d => Dialog360PayloadParser.Parse(d.Body)).ToList();
        Assert.All(parsed, p => Assert.Equal(0, p.SkippedCount));
        Assert.Contains(parsed.SelectMany(p => p.Messages), m => m.Direction == MessageDirection.Outbound);
        Assert.Contains(parsed.SelectMany(p => p.Messages), m => m.Referral is not null);
    }

    private sealed class BranchResponseBuilder(string name)
    {
        public Conversia.Application.DTOs.Organization.BranchResponse Build() => new() { Id = Guid.NewGuid(), Name = name, IsActive = true, CreatedAt = DateTime.UtcNow };
    }
}
