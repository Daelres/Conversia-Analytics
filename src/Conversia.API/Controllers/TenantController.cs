using Conversia.Application.DTOs.Tenants;
using Conversia.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Conversia.API.Controllers;

/// <summary>The caller's own company. Answers any signed-in user of it, whatever its status.</summary>
[ApiController]
[Route("api/tenant")]
[Authorize]
public class TenantController : ControllerBase
{
    private readonly ITenantService _tenants;

    public TenantController(ITenantService tenants)
    {
        _tenants = tenants;
    }

    [HttpGet]
    public async Task<ActionResult<TenantInfoResponse>> GetTenant(CancellationToken cancellationToken)
        => Ok(await _tenants.GetCurrentAsync(User.GetTenantId(), cancellationToken));
}
