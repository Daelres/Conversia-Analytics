using Conversia.Application.DTOs;
using Conversia.Application.DTOs.Admin;
using Conversia.Application.Interfaces;
using Conversia.Infrastructure.Administration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Conversia.API.Controllers;

/// <summary>
/// The platform's 360dialog line licence inventory (docs/PLATFORM_ADMIN.md, "Line licences"): create, assign to a company, release, retire.
/// Only the platform administrator; every change is audited; numbers leave masked to their last four digits.
/// </summary>
[ApiController]
[Route("api/admin/licenses")]
[Authorize(Policy = PlatformAdminTokens.PolicyName)]
public class AdminLicensesController : ControllerBase
{
    private readonly ILineLicenseService _licenses;

    public AdminLicensesController(ILineLicenseService licenses)
    {
        _licenses = licenses;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResponse<LineLicenseResponse>>> List([FromQuery] AdminLicensesQuery query, CancellationToken cancellationToken)
        => Ok(await _licenses.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<LineLicenseResponse>> Get(Guid id, CancellationToken cancellationToken)
        => Ok(await _licenses.GetAsync(id, cancellationToken));

    [HttpPost]
    public async Task<ActionResult<LineLicenseResponse>> Create(CreateLineLicenseRequest request, CancellationToken cancellationToken)
    {
        var created = await _licenses.CreateAsync(AdminId, request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPost("{id:guid}/assign")]
    public async Task<ActionResult<LineLicenseResponse>> Assign(Guid id, AssignLineLicenseRequest request, CancellationToken cancellationToken)
        => Ok(await _licenses.AssignAsync(AdminId, id, request, cancellationToken));

    [HttpPost("{id:guid}/release")]
    public async Task<ActionResult<LineLicenseResponse>> Release(Guid id, CancellationToken cancellationToken)
        => Ok(await _licenses.ReleaseAsync(AdminId, id, cancellationToken));

    [HttpPost("{id:guid}/retire")]
    public async Task<ActionResult<LineLicenseResponse>> Retire(Guid id, CancellationToken cancellationToken)
        => Ok(await _licenses.RetireAsync(AdminId, id, cancellationToken));

    private Guid AdminId => User.GetUserId()!.Value;
}
