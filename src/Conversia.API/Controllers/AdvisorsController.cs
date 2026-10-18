using Conversia.Application.DTOs.Organization;
using Conversia.Application.Interfaces;
using Conversia.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Conversia.API.Controllers;

[ApiController]
[Route("api/advisors")]
[Authorize]
public class AdvisorsController : ControllerBase
{
    private readonly IOrganizationService _organization;

    public AdvisorsController(IOrganizationService organization)
    {
        _organization = organization;
    }

    [HttpGet]
    [Authorize(Policy = nameof(Permission.OrganizationView))]
    public async Task<ActionResult<IReadOnlyList<AdvisorResponse>>> GetAdvisors(CancellationToken cancellationToken)
        => Ok(await _organization.ListAdvisorsAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [Authorize(Policy = nameof(Permission.OrganizationView))]
    public async Task<ActionResult<AdvisorResponse>> GetAdvisor(Guid id, CancellationToken cancellationToken)
        => Ok(await _organization.GetAdvisorAsync(id, cancellationToken));

    [HttpPost]
    [Authorize(Policy = nameof(Permission.OrganizationManage))]
    public async Task<ActionResult<AdvisorResponse>> CreateAdvisor(CreateAdvisorRequest request, CancellationToken cancellationToken)
    {
        var advisor = await _organization.CreateAdvisorAsync(User.GetTenantId(), request, cancellationToken);
        return CreatedAtAction(nameof(GetAdvisor), new { id = advisor.Id }, advisor);
    }
}
