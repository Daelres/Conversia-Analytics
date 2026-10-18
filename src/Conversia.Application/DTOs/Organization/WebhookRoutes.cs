namespace Conversia.Application.DTOs.Organization;

/// <summary>The public webhook route, defined once for the controller and for the path shown when a channel is created.</summary>
public static class WebhookRoutes
{
    public const string Dialog360Base = "api/webhooks/360dialog";

    public const string SecretHeader = "X-Webhook-Secret";

    public static string Dialog360Path(string channelKey) => $"/{Dialog360Base}/{channelKey}";
}
