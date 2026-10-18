using Conversia.Application.DTOs.Organization;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Domain.Entities;
using Conversia.Domain.Exceptions;
using Conversia.Domain.Interfaces;

namespace Conversia.Application.Services;

public class OrganizationService : IOrganizationService
{
    private const string UnknownBranchMessage = "La sucursal indicada no existe.";
    private const string UnknownUserMessage = "El usuario indicado no existe.";

    private readonly IBranchRepository _branches;
    private readonly IAdvisorRepository _advisors;
    private readonly IWhatsAppChannelRepository _channels;
    private readonly IUserRepository _users;

    public OrganizationService(
        IBranchRepository branches, IAdvisorRepository advisors, IWhatsAppChannelRepository channels, IUserRepository users)
    {
        _branches = branches;
        _advisors = advisors;
        _channels = channels;
        _users = users;
    }

    public async Task<IReadOnlyList<BranchResponse>> ListBranchesAsync(CancellationToken cancellationToken = default)
        => (await _branches.ListAsync(cancellationToken)).Select(BranchResponse.From).ToList();

    public async Task<BranchResponse> GetBranchAsync(Guid id, CancellationToken cancellationToken = default)
        => BranchResponse.From(await _branches.GetByIdAsync(id, cancellationToken) ?? throw new BranchNotFoundException());

    public async Task<BranchResponse> CreateBranchAsync(Guid callerTenantId, CreateBranchRequest request, CancellationToken cancellationToken = default)
    {
        var branch = Branch.Create(callerTenantId, request.Name);

        // No existence pre-check: the unique (TenantId, Name) index decides, see ProductService.
        await _branches.AddAsync(branch, cancellationToken);
        return BranchResponse.From(branch);
    }

    public async Task<IReadOnlyList<AdvisorResponse>> ListAdvisorsAsync(CancellationToken cancellationToken = default)
        => (await _advisors.ListAsync(cancellationToken)).Select(AdvisorResponse.From).ToList();

    public async Task<AdvisorResponse> GetAdvisorAsync(Guid id, CancellationToken cancellationToken = default)
        => AdvisorResponse.From(await _advisors.GetByIdAsync(id, cancellationToken) ?? throw new AdvisorNotFoundException());

    public async Task<AdvisorResponse> CreateAdvisorAsync(Guid callerTenantId, CreateAdvisorRequest request, CancellationToken cancellationToken = default)
    {
        var branchId = await RequireBranchAsync(request.BranchId, cancellationToken);
        if (request.UserId is { } userId && await _users.GetByIdAsync(userId, cancellationToken) is null)
        {
            throw new DomainException(UnknownUserMessage);
        }

        var advisor = Advisor.Create(callerTenantId, branchId, request.Name, request.UserId);
        await _advisors.AddAsync(advisor, cancellationToken);
        return AdvisorResponse.From(advisor);
    }

    public async Task<IReadOnlyList<ChannelResponse>> ListChannelsAsync(CancellationToken cancellationToken = default)
        => (await _channels.ListAsync(cancellationToken)).Select(ChannelResponse.From).ToList();

    public async Task<ChannelResponse> GetChannelAsync(Guid id, CancellationToken cancellationToken = default)
        => ChannelResponse.From(await _channels.GetByIdAsync(id, cancellationToken) ?? throw new ChannelNotFoundException());

    public async Task<CreateChannelResponse> CreateChannelAsync(Guid callerTenantId, CreateChannelRequest request, CancellationToken cancellationToken = default)
    {
        var branchId = await RequireBranchAsync(request.BranchId, cancellationToken);
        var (channel, secret) = WhatsAppChannel.Create(callerTenantId, branchId, request.DisplayPhoneNumber, request.ProviderPhoneNumberId);

        await _channels.AddAsync(channel, cancellationToken);
        return CreateChannelResponse.From(channel, secret);
    }

    public async Task<ChannelResponse> SetChannelActiveAsync(Guid id, bool active, CancellationToken cancellationToken = default)
    {
        var channel = await _channels.GetForUpdateAsync(id, cancellationToken) ?? throw new ChannelNotFoundException();
        if (active)
        {
            channel.Activate();
        }
        else
        {
            channel.Deactivate();
        }

        await _channels.UpdateAsync(channel, cancellationToken);
        return ChannelResponse.From(channel);
    }

    public async Task<CreateChannelResponse> RotateChannelSecretAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var channel = await _channels.GetForUpdateAsync(id, cancellationToken) ?? throw new ChannelNotFoundException();
        var secret = channel.RotateWebhookSecret();
        await _channels.UpdateAsync(channel, cancellationToken);
        return CreateChannelResponse.From(channel, secret);
    }

    // A branch of another tenant is invisible through the query filter, so it fails exactly like an unknown id.
    private async Task<Guid> RequireBranchAsync(Guid? branchId, CancellationToken cancellationToken)
    {
        if (branchId is not { } id || await _branches.GetByIdAsync(id, cancellationToken) is null)
        {
            throw new DomainException(UnknownBranchMessage);
        }

        return id;
    }
}
