using System.Net;

namespace Conversia.WebhookSimulator.Api;

/// <summary>The API (or the webhook) answered something the simulator cannot go on with. The message names the call and the status, never a body or a secret.</summary>
public sealed class ApiFailure(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}
