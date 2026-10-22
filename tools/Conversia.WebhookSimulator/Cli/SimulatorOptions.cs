using System.Globalization;
using Conversia.WebhookSimulator.Generation;

namespace Conversia.WebhookSimulator.Cli;

public enum SimulatorCommandName
{
    Help,
    Setup,
    Play,
    Populate,
}

/// <summary>What the command line asked for. Credentials and secrets are never part of it: they come from the environment (<see cref="SimulatorEnvironment"/>).</summary>
public sealed record SimulatorOptions
{
    public const string DefaultApiUrl = "http://localhost:5030";

    public SimulatorCommandName Command { get; init; } = SimulatorCommandName.Help;

    public string ApiUrl { get; init; } = DefaultApiUrl;

    public bool AllowRemote { get; init; }

    // setup
    public bool PrintSecret { get; init; }

    public bool RotateSecret { get; init; }

    /// <summary>The number of lines the workspace may have active (setup, when a platform administrator approves it).</summary>
    public int LineQuota { get; init; } = 4;

    // play
    public string Scenario { get; init; } = Scenarios.All;

    public string? Branch { get; init; }

    // populate
    public int Conversations { get; init; } = 300;

    public int Days { get; init; } = 30;

    public int Seed { get; init; } = 42;

    public double PreviousShare { get; init; } = 0.3;

    public bool NoAssign { get; init; }

    public int WaitSeconds { get; init; } = 180;

    /// <summary>Requests per second to the webhook.</summary>
    public int Rate { get; init; } = 15;

    /// <summary>How the channel secret is presented: false = <c>X-Webhook-Secret</c> (default), true = <c>Authorization: Basic</c>.</summary>
    public bool BasicAuth { get; init; }

    private static readonly HashSet<string> Flags = ["allow-remote", "print-secret", "rotate-secret", "no-assign", "help"];

    private static readonly HashSet<string> Valued =
        ["api-url", "scenario", "branch", "conversations", "days", "seed", "previous-share", "wait-seconds", "rate", "line-quota", "auth"];

    private static readonly HashSet<string> CredentialLike = ["email", "password", "secret", "token", "api-key", "webhook-secret", "user", "username", "admin-email", "admin-password"];

    public static SimulatorOptions Parse(IReadOnlyList<string> args, string? apiUrlFromEnvironment = null)
    {
        if (args.Count == 0 || args[0] is "help" or "--help" or "-h")
        {
            return new SimulatorOptions { Command = SimulatorCommandName.Help };
        }

        var command = args[0].ToLowerInvariant() switch
        {
            "setup" => SimulatorCommandName.Setup,
            "play" => SimulatorCommandName.Play,
            "populate" => SimulatorCommandName.Populate,
            var other => throw new UsageException($"Unknown command '{other}'. Use setup, play or populate (or --help)."),
        };

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < args.Count; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                throw new UsageException($"Unexpected argument '{token}'. Options look like --name value.");
            }

            var body = token[2..];
            var equals = body.IndexOf('=');
            var name = (equals < 0 ? body : body[..equals]).ToLowerInvariant();
            if (CredentialLike.Contains(name))
            {
                throw new UsageException($"--{name} is not accepted: credentials and secrets never go on the command line. Use the SIM_* environment variables (see docs/SIMULATOR.md).");
            }

            if (Flags.Contains(name))
            {
                values[name] = equals < 0 ? "true" : body[(equals + 1)..];
                continue;
            }

            if (!Valued.Contains(name))
            {
                throw new UsageException($"Unknown option --{name}.");
            }

            if (equals >= 0)
            {
                values[name] = body[(equals + 1)..];
            }
            else if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                values[name] = args[++i];
            }
            else
            {
                throw new UsageException($"--{name} needs a value.");
            }
        }

        var scenario = (values.GetValueOrDefault("scenario") ?? Scenarios.All).ToLowerInvariant();
        if (command == SimulatorCommandName.Play && !Scenarios.IsKnown(scenario))
        {
            throw new UsageException($"Unknown scenario '{scenario}'. Use one of: {string.Join(", ", Scenarios.Names)}, {Scenarios.All}.");
        }

        var auth = (values.GetValueOrDefault("auth") ?? "header").ToLowerInvariant();
        if (auth is not ("header" or "basic"))
        {
            throw new UsageException("--auth must be 'header' (X-Webhook-Secret, the default) or 'basic' (Authorization: Basic).");
        }

        return new SimulatorOptions
        {
            Command = command,
            BasicAuth = auth == "basic",
            ApiUrl = values.GetValueOrDefault("api-url") ?? apiUrlFromEnvironment ?? DefaultApiUrl,
            AllowRemote = Flag(values, "allow-remote"),
            PrintSecret = Flag(values, "print-secret"),
            RotateSecret = Flag(values, "rotate-secret"),
            LineQuota = Integer(values, "line-quota", 4, 0, 1000),
            NoAssign = Flag(values, "no-assign"),
            Scenario = scenario,
            Branch = values.GetValueOrDefault("branch"),
            Conversations = Integer(values, "conversations", 300, 1, 5000),
            Days = Integer(values, "days", 30, 1, 180),
            Seed = Integer(values, "seed", 42, 0, int.MaxValue),
            WaitSeconds = Integer(values, "wait-seconds", 180, 0, 3600),
            Rate = Integer(values, "rate", 15, 1, 200),
            PreviousShare = Share(values, "previous-share", 0.3),
        };
    }

    private static bool Flag(Dictionary<string, string?> values, string name)
        => values.TryGetValue(name, out var value) && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

    private static int Integer(Dictionary<string, string?> values, string name, int fallback, int min, int max)
    {
        if (!values.TryGetValue(name, out var text))
        {
            return fallback;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < min || number > max)
        {
            throw new UsageException($"--{name} must be a whole number from {min} to {max}.");
        }

        return number;
    }

    private static double Share(Dictionary<string, string?> values, string name, double fallback)
    {
        if (!values.TryGetValue(name, out var text))
        {
            return fallback;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || number is < 0 or > 0.6)
        {
            throw new UsageException($"--{name} must be a number from 0 to 0.6 (use a dot for decimals).");
        }

        return number;
    }
}
