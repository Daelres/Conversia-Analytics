using Conversia.Application.DTOs.Organization;
using Conversia.Application.Interfaces;
using Conversia.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Conversia.API.Controllers;

[ApiController]
[Route("api/branches")]
[Authorize]
public class BranchesController : ControllerBase
{
    private readonly IOrganizationService _organization;

    public BranchesController(IOrganizationService organization)
    {
        _organization = organization;
    }

    [HttpGet]
    [Authorize(Policy = nameof(Permission.OrganizationView))]
    public async Task<ActionResult<IReadOnlyList<BranchResponse>>> GetBranches(CancellationToken cancellationToken)
        => Ok(await _organization.ListBranchesAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [Authorize(Policy = nameof(Permission.OrganizationView))]
    public async Task<ActionResult<BranchResponse>> GetBranch(Guid id, CancellationToken cancellationToken)
        => Ok(await _organization.GetBranchAsync(id, cancellationToken));

    [HttpPost]
    [Authorize(Policy = nameof(Permission.OrganizationManage))]
    public async Task<ActionResult<BranchResponse>> CreateBranch(CreateBranchRequest request, CancellationToken cancellationToken)
    {
        var branch = await _organization.CreateBranchAsync(User.GetTenantId(), request, cancellationToken);
        return CreatedAtAction(nameof(GetBranch), new { id = branch.Id }, branch);
    }
}
