using Conversia.API.RateLimiting;
using Conversia.Application.DTOs.Sandbox;
using Conversia.Application.Interfaces;
using Conversia.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Conversia.API.Controllers;

/// <summary>
/// The internal sandbox of the caller's company (docs/SANDBOX.md): a simulated customer, and the removal of what the sandbox created.
/// The company is always the one of the token; no route or body names another. Switching the mode is not here: only the platform
/// administrator can (<c>PUT /api/admin/tenants/{id}/sandbox-mode</c>).
/// </summary>
[ApiController]
[Route("api/sandbox")]
[Authorize]
public class SandboxController : ControllerBase
{
    private readonly ISandboxService _sandbox;

    public SandboxController(ISandboxService sandbox)
    {
        _sandbox = sandbox;
    }

    /// <summary>
    /// A new simulated customer writes a first message to one of the company's active numbers. 202: the message is in the ingestion queue,
    /// like a delivery of the provider, and becomes a conversation a moment later. 409 <c>sandbox_mode_off</c> outside sandbox mode.
    /// </summary>
    [HttpPost("conversations")]
    [Authorize(Policy = nameof(Permission.ConversationsManage))]
    [EnableRateLimiting(MessageSendRateLimitingExtensions.PolicyName)]
    public async Task<ActionResult<SandboxMessageAcceptedResponse>> StartConversation(StartSandboxConversationRequest request, CancellationToken cancellationToken)
        => Accepted(await _sandbox.StartConversationAsync(User.GetTenantId(), request, cancellationToken));

    /// <summary>The simulated customer of an open sandbox conversation writes again. 202, like <see cref="StartConversation"/>.</summary>
    [HttpPost("conversations/{id:guid}/customer-messages")]
    [Authorize(Policy = nameof(Permission.ConversationsManage))]
    [EnableRateLimiting(MessageSendRateLimitingExtensions.PolicyName)]
    public async Task<ActionResult<SandboxMessageAcceptedResponse>> SendCustomerMessage(Guid id, SandboxCustomerMessageRequest request, CancellationToken cancellationToken)
        => Accepted(await _sandbox.SendCustomerMessageAsync(User.GetTenantId(), id, request, cancellationToken));

    /// <summary>Deletes every sandbox conversation, message, contact and delivery of the company, and nothing else. Allowed whatever the mode.</summary>
    [HttpDelete("data")]
    [Authorize(Policy = nameof(Permission.OrganizationManage))]
    public async Task<ActionResult<SandboxDataDeletedResponse>> DeleteData(CancellationToken cancellationToken)
        => Ok(await _sandbox.DeleteDataAsync(User.GetTenantId(), User.GetUserId(), cancellationToken));
}
