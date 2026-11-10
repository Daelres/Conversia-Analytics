namespace Conversia.Domain.Enums;

/// <summary>
/// Which installation of the WhatsApp provider a channel sends through. It selects a base URL from configuration; the URL itself
/// never comes from a request. Stored by name.
/// </summary>
public enum WhatsAppProviderEnvironment
{
    Sandbox = 0,
    Production = 1,
}
