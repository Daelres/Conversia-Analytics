namespace Conversia.WebhookSimulator.Cli;

/// <summary>
/// The only place the simulator reads credentials and secrets, and it reads them from the environment only: a value on the command line
/// would land in the shell history and in <c>ps</c>. Nothing here is ever printed, logged or written to a file.
/// </summary>
public sealed class SimulatorEnvironment
{
    public const string EmailVariable = "SIM_EMAIL";
    public const string PasswordVariable = "SIM_PASSWORD";
    public const string SecretVariable = "SIM_WEBHOOK_SECRET";
    public const string SecretsVariable = "SIM_WEBHOOK_SECRETS";
    public const string ApiUrlVariable = "SIM_API_URL";

    private readonly Func<string, string?> _read;

    public SimulatorEnvironment(Func<string, string?> read)
    {
        _read = read;
    }

    public static SimulatorEnvironment FromProcess() => new(Environment.GetEnvironmentVariable);

    public string? ApiUrl => Blank(_read(ApiUrlVariable));

    /// <summary>The login of the person who runs the simulator.</summary>
    public (string Email, string Password) Credentials()
    {
        var email = Blank(_read(EmailVariable));
        var password = _read(PasswordVariable);
        var missing = new List<string>();
        if (email is null)
        {
            missing.Add(EmailVariable);
        }

        if (string.IsNullOrEmpty(password))
        {
            missing.Add(PasswordVariable);
        }

        if (missing.Count > 0)
        {
            throw new UsageException($"Set {string.Join(" and ", missing)} in the environment: the simulator logs in to the API as a user of your workspace (it never takes credentials as arguments).");
        }

        return (email!, password!);
    }

    /// <summary>
    /// The secret of the channel of a branch. Every channel has a secret of its own, so <c>SIM_WEBHOOK_SECRETS</c> lists them
    /// (<c>Centro=abc;Norte=def</c>); <c>SIM_WEBHOOK_SECRET</c> is the one secret to try for a branch that is not listed.
    /// </summary>
    public string? SecretFor(string branchName)
    {
        var listed = ParseSecrets(_read(SecretsVariable));
        return listed.TryGetValue(branchName, out var secret) ? secret : Blank(_read(SecretVariable));
    }

    public static IReadOnlyDictionary<string, string> ParseSecrets(string? text)
    {
        var secrets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in (text ?? string.Empty).Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = entry.IndexOf('=');
            if (equals > 0 && equals < entry.Length - 1)
            {
                secrets[entry[..equals].Trim()] = entry[(equals + 1)..].Trim();
            }
        }

        return secrets;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
