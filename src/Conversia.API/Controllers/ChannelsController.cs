using Conversia.API.RateLimiting;
using Conversia.Application.DTOs.Organization;
using Conversia.Application.DTOs.Templates;
using Conversia.Application.Interfaces;
using Conversia.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Conversia.API.Controllers;

[ApiController]
[Route("api/channels")]
[Authorize]
public class ChannelsController : ControllerBase
{
    private readonly IOrganizationService _organization;
    private readonly IAuthorizationService _authorization;
    private readonly ITemplateService _templates;

    public ChannelsController(IOrganizationService organization, IAuthorizationService authorization, ITemplateService templates)
    {
        _organization = organization;
        _authorization = authorization;
        _templates = templates;
    }

    /// <summary>The channel's templates as last synced (docs/MESSAGING.md, "Templates"); in sandbox mode, the fixed sandbox list.</summary>
    [HttpGet("{id:guid}/templates")]
    [Authorize(Policy = nameof(Permission.OrganizationView))]
    [EnableRateLimiting(PollingRateLimitingExtensions.PolicyName)]
    public async Task<ActionResult<ChannelTemplatesResponse>> GetTemplates(Guid id, CancellationToken cancellationToken)
        => Ok(await _templates.ListForChannelAsync(id, cancellationToken));

    /// <summary>
    /// Reads the channel's templates from 360dialog with its key and replaces the stored copy. Each call is one or more requests to
    /// 360dialog: a sandbox key allows 25 in total. Rate-limited like a send.
    /// </summary>
    [HttpPost("{id:guid}/templates/sync")]
    [Authorize(Policy = nameof(Permission.OrganizationManage))]
    [EnableRateLimiting(MessageSendRateLimitingExtensions.PolicyName)]
    public async Task<ActionResult<TemplateSyncResponse>> SyncTemplates(Guid id, CancellationToken cancellationToken)
        => Ok(await _templates.SyncAsync(id, cancellationToken));

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

    /// <summary>
    /// Stores the provider API key of the channel (encrypted) and its environment; without <c>apiKey</c> it changes only the environment.
    /// The key is write-only: neither this answer nor any other carries it.
    /// </summary>
    [HttpPut("{id:guid}/provider-credentials")]
    [Authorize(Policy = nameof(Permission.OrganizationManage))]
    public async Task<ActionResult<ChannelResponse>> SetProviderCredentials(Guid id, SetProviderCredentialsRequest request, CancellationToken cancellationToken)
    {
        var channel = await _organization.SetChannelProviderCredentialsAsync(id, request, cancellationToken);
        Response.Headers.CacheControl = "no-store";
        return Ok(channel);
    }

    /// <summary>Forgets the provider API key of the channel: nothing can be sent through it until a new one is set. Idempotent.</summary>
    [HttpDelete("{id:guid}/provider-credentials")]
    [Authorize(Policy = nameof(Permission.OrganizationManage))]
    public async Task<ActionResult<ChannelResponse>> ClearProviderCredentials(Guid id, CancellationToken cancellationToken)
        => Ok(await _organization.ClearChannelProviderCredentialsAsync(id, cancellationToken));

    private async Task<bool> CanManageAsync()
        => (await _authorization.AuthorizeAsync(User, resource: null, nameof(Permission.OrganizationManage))).Succeeded;
}
