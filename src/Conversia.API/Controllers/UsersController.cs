using System.IdentityModel.Tokens.Jwt;
using Conversia.API.ModelBinding;
using Conversia.Application.DTOs.Users;
using Conversia.Application.Interfaces;
using Conversia.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Conversia.API.Controllers;

[ApiController]
[Route("api/users")]
[Authorize]
public class UsersController : ControllerBase
{
    private readonly IPermissionService _permissionService;
    private readonly IUserManagementService _userManagementService;

    public UsersController(IPermissionService permissionService, IUserManagementService userManagementService)
    {
        _permissionService = permissionService;
        _userManagementService = userManagementService;
    }

    [HttpGet("me")]
    public async Task<ActionResult<UserProfileResponse>> GetMe(CancellationToken cancellationToken)
    {
        var profile = await _permissionService.GetMyProfileAsync(GetCurrentUserId(), cancellationToken);
        return Ok(profile);
    }

    [HttpGet]
    [Authorize(Policy = nameof(Permission.UsersView))]
    public async Task<ActionResult<IReadOnlyList<UserSummaryResponse>>> GetUsers(CancellationToken cancellationToken)
    {
        var users = await _permissionService.ListUsersInTenantAsync(GetCurrentTenantId(), cancellationToken);
        return Ok(users);
    }

    [HttpPost]
    [Authorize(Policy = nameof(Permission.UsersManage))]
    public async Task<ActionResult<UserSummaryResponse>> CreateUser(
        CreateUserRequest request,
        CancellationToken cancellationToken)
    {
        var user = await _userManagementService.CreateUserAsync(
            GetCurrentTenantId(), request, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, user);
    }

    [HttpPost("{userId:guid}/permissions")]
    [Authorize(Policy = nameof(Permission.UsersManage))]
    public async Task<IActionResult> AssignPermission(
        Guid userId,
        AssignPermissionRequest request,
        CancellationToken cancellationToken)
    {
        await _permissionService.AssignPermissionAsync(GetCurrentTenantId(), userId, request.Permission, cancellationToken);
        return NoContent();
    }

    [HttpDelete("{userId:guid}/permissions/{permission}")]
    [Authorize(Policy = nameof(Permission.UsersManage))]
    public async Task<IActionResult> RevokePermission(
        Guid userId,
        [FromRoute, ModelBinder(typeof(DefinedPermissionModelBinder))] Permission permission,
        CancellationToken cancellationToken)
    {
        await _permissionService.RevokePermissionAsync(GetCurrentTenantId(), userId, permission, cancellationToken);
        return NoContent();
    }

    private Guid GetCurrentUserId()
        => Guid.Parse(User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value);

    private Guid GetCurrentTenantId()
        => Guid.Parse(User.FindFirst("tenant_id")!.Value);
}
