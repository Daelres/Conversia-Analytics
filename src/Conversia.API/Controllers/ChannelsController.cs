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
    private readonly IAuthorizationService _authorization;

    public ChannelsController(IOrganizationService organization, IAuthorizationService authorization)
    {
        _organization = organization;
        _authorization = authorization;
    }

    /// <summary>Whoever can view the channels sees them, but the key of the webhook URL is for those who can manage them.</summary>
    [HttpGet]
    [Authorize(Policy = nameof(Permission.OrganizationView))]
    public async Task<ActionResult<IReadOnlyList<ChannelResponse>>> GetChannels(CancellationToken cancellationToken)
    {
        var channels = await _organization.ListChannelsAsync(cancellationToken);
        return Ok(await CanManageAsync() ? channels : channels.Select(c => c.WithoutWebhookAddress()).ToList());
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = nameof(Permission.OrganizationView))]
    public async Task<ActionResult<ChannelResponse>> GetChannel(Guid id, CancellationToken cancellationToken)
    {
        var channel = await _organization.GetChannelAsync(id, cancellationToken);
        return Ok(await CanManageAsync() ? channel : channel.WithoutWebhookAddress());
    }

    /// <summary>The 201 body carries the webhook secret. It is shown here only; the channel stores just its hash.</summary>
    [HttpPost]
    [Authorize(Policy = nameof(Permission.OrganizationManage))]
    public async Task<ActionResult<CreateChannelResponse>> CreateChannel(CreateChannelRequest request, CancellationToken cancellationToken)
    {
        var channel = await _organization.CreateChannelAsync(User.GetTenantId(), request, cancellationToken);
        Response.Headers.CacheControl = "no-store";
        return CreatedAtAction(nameof(GetChannel), new { id = channel.Id }, channel);
    }

    /// <summary>Turns the line on if the company is Active and has quota left (409 otherwise). Idempotent.</summary>
    [HttpPost("{id:guid}/activate")]
    [Authorize(Policy = nameof(Permission.OrganizationManage))]
    public async Task<ActionResult<ChannelResponse>> ActivateChannel(Guid id, CancellationToken cancellationToken)
        => Ok(await _organization.SetChannelActiveAsync(User.GetTenantId(), id, active: true, cancellationToken));

    /// <summary>Turns the webhook of the channel off: the provider gets the same 401 as for a wrong secret. Idempotent.</summary>
    [HttpPost("{id:guid}/deactivate")]
    [Authorize(Policy = nameof(Permission.OrganizationManage))]
    public async Task<ActionResult<ChannelResponse>> DeactivateChannel(Guid id, CancellationToken cancellationToken)
        => Ok(await _organization.SetChannelActiveAsync(User.GetTenantId(), id, active: false, cancellationToken));

    /// <summary>Replaces the webhook secret. The body carries the NEW secret, shown only here; the old one stops working at once.</summary>
    [HttpPost("{id:guid}/rotate-secret")]
    [Authorize(Policy = nameof(Permission.OrganizationManage))]
    public async Task<ActionResult<CreateChannelResponse>> RotateSecret(Guid id, CancellationToken cancellationToken)
    {
        var channel = await _organization.RotateChannelSecretAsync(id, cancellationToken);
        Response.Headers.CacheControl = "no-store";
        return Ok(channel);
    }

    private async Task<bool> CanManageAsync()
        => (await _authorization.AuthorizeAsync(User, resource: null, nameof(Permission.OrganizationManage))).Succeeded;
}
