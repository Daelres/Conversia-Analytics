namespace Conversia.WebhookSimulator.Cli;

/// <summary>The command line (or the environment) is wrong in a way the person can fix: the message says how. Never carries a secret.</summary>
public sealed class UsageException(string message) : Exception(message);
