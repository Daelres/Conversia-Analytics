using Conversia.Application.DTOs.Organization;
using Conversia.Application.Interfaces;
using Conversia.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Conversia.API.Controllers;

[ApiController]
[Route("api/channels")]
[Authorize]
public class ChannelsController : ControllerBase
{
    private readonly IOrganizationService _organization;

    public ChannelsController(IOrganizationService organization)
    {
        _organization = organization;
    }

    [HttpGet]
    [Authorize(Policy = nameof(Permission.OrganizationView))]
    public async Task<ActionResult<IReadOnlyList<ChannelResponse>>> GetChannels(CancellationToken cancellationToken)
        => Ok(await _organization.ListChannelsAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [Authorize(Policy = nameof(Permission.OrganizationView))]
    public async Task<ActionResult<ChannelResponse>> GetChannel(Guid id, CancellationToken cancellationToken)
        => Ok(await _organization.GetChannelAsync(id, cancellationToken));

    /// <summary>The 201 body carries the webhook secret. It is shown here only; the channel stores just its hash.</summary>
    [HttpPost]
    [Authorize(Policy = nameof(Permission.OrganizationManage))]
    public async Task<ActionResult<CreateChannelResponse>> CreateChannel(CreateChannelRequest request, CancellationToken cancellationToken)
    {
        var channel = await _organization.CreateChannelAsync(User.GetTenantId(), request, cancellationToken);
        Response.Headers.CacheControl = "no-store";
        return CreatedAtAction(nameof(GetChannel), new { id = channel.Id }, channel);
    }
}
