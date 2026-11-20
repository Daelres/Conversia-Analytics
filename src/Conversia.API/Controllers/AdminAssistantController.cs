using Conversia.Application.Assistant;
using Conversia.Infrastructure.Administration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Conversia.API.Controllers;

/// <summary>
/// The administrator's kill switches of the AI sales assistant and its counts (docs/PLATFORM_ADMIN.md, "Assistant"). Counts only: never a
/// conversation, a message or a company's knowledge base.
/// </summary>
[ApiController]
[Route("api/admin")]
[Authorize(Policy = PlatformAdminTokens.PolicyName)]
public class AdminAssistantController(IAssistantAdminService assistant) : ControllerBase
{
    private Guid AdminId => User.GetUserId()!.Value;

    [HttpGet("assistant")]
    public async Task<ActionResult<AssistantAdminResponse>> GetPlatform(CancellationToken cancellationToken)
        => Ok(await assistant.GetPlatformAsync(cancellationToken));

    /// <summary>Switches the assistant off (or back on) for every company; the reason is required and audited.</summary>
    [HttpPut("assistant")]
    public async Task<ActionResult<AssistantAdminResponse>> SetGlobal(SetAssistantSwitchRequest request, CancellationToken cancellationToken)
        => Ok(await assistant.SetGlobalAsync(AdminId, request, cancellationToken));

    [HttpGet("tenants/{id:guid}/assistant")]
    public async Task<ActionResult<AssistantAdminResponse>> GetCompany(Guid id, CancellationToken cancellationToken)
        => Ok(await assistant.GetCompanyAsync(id, cancellationToken));

    /// <summary><c>{"enabled": false, "reason": "..."}</c> blocks the assistant of the company whatever its settings say; audited.</summary>
    [HttpPut("tenants/{id:guid}/assistant")]
    public async Task<ActionResult<AssistantAdminResponse>> SetCompany(Guid id, SetAssistantSwitchRequest request, CancellationToken cancellationToken)
        => Ok(await assistant.SetCompanyAsync(AdminId, id, request, cancellationToken));
}
