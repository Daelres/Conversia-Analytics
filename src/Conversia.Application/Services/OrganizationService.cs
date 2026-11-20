using Conversia.Application.DTOs.Organization;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Application.Licensing;
using Conversia.Application.Messaging;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;
using Conversia.Domain.Interfaces;

namespace Conversia.Application.Services;

public class OrganizationService : IOrganizationService
{
    private const string UnknownBranchMessage = "La sucursal indicada no existe.";
    private const string UnknownUserMessage = "El usuario indicado no existe.";
    public const string MissingProviderApiKeyMessage = "La clave de API del proveedor es obligatoria: este número todavía no tiene una.";

    private readonly IBranchRepository _branches;
    private readonly IAdvisorRepository _advisors;
    private readonly IWhatsAppChannelRepository _channels;
    private readonly IUserRepository _users;
    private readonly ITenantUnitOfWork _unitOfWork;
    private readonly ISecretProtector _secretProtector;
    private readonly TimeProvider _timeProvider;
    private readonly ILineLicenseRepository? _licenses;
    private readonly LicensingSettings _licensing;

    /// <param name="licenses">The platform's licence inventory: a registered or switched-on line is linked to its licence (docs/PLATFORM_ADMIN.md, "Line licences").</param>
    /// <param name="licensing"><c>Licensing:RequireAssignedLicense</c>; off when not given.</param>
    public OrganizationService(
        IBranchRepository branches, IAdvisorRepository advisors, IWhatsAppChannelRepository channels, IUserRepository users,
        ITenantUnitOfWork unitOfWork, ISecretProtector secretProtector, TimeProvider timeProvider, ILineLicenseRepository? licenses = null,
        LicensingSettings? licensing = null)
    {
        _licenses = licenses;
        _licensing = licensing ?? LicensingSettings.Default;
        _unitOfWork = unitOfWork;
        _secretProtector = secretProtector;
        _timeProvider = timeProvider;
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

        // No existence pre-check: the unique (TenantId, Name) index decides, see BranchRepository.
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
        await LinkRegisteredLineAsync(callerTenantId, channel, cancellationToken);
        return CreateChannelResponse.From(channel, secret);
    }

    // For reporting only: the line is created switched off, and switching it on links (or, with licences required, demands) a licence again
    // under the company's lock. A race with the administrator is therefore harmless here and is not reported to the person registering.
    private async Task LinkRegisteredLineAsync(Guid tenantId, WhatsAppChannel channel, CancellationToken cancellationToken)
    {
        if (_licenses is null
            || await _licenses.FindAssignedMatchAsync(tenantId, channel.DisplayPhoneNumber, channel.ProviderPhoneNumberId, cancellationToken) is not { ChannelId: null } license)
        {
            return;
        }

        license.LinkChannel(channel.Id);
        try
        {
            await _licenses.SaveAsync(null, cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
        }
    }

    public async Task<ChannelResponse> SetChannelActiveAsync(Guid callerTenantId, Guid id, bool active, CancellationToken cancellationToken = default)
    {
        if (active)
        {
            return await ActivateChannelAsync(callerTenantId, id, cancellationToken);
        }

        // Switching off is free and cannot break the quota, so it needs no lock.
        var channel = await _channels.GetForUpdateAsync(id, cancellationToken) ?? throw new ChannelNotFoundException();
        channel.Deactivate();
        await _channels.UpdateAsync(channel, cancellationToken);
        return ChannelResponse.From(channel);
    }

    // The count and the switch happen while the company's row is locked, so parallel activations take turns and the quota is never exceeded.
    private async Task<ChannelResponse> ActivateChannelAsync(Guid callerTenantId, Guid id, CancellationToken cancellationToken)
    {
        await using var change = await _unitOfWork.BeginAsync(callerTenantId, cancellationToken) ?? throw new TenantNotFoundException();
        var channel = await change.GetChannelAsync(id, cancellationToken) ?? throw new ChannelNotFoundException();
        if (channel.IsActive)
        {
            return ChannelResponse.From(channel);
        }

        var tenant = change.Tenant;
        if (tenant.Status != TenantStatus.Active)
        {
            throw new TenantNotActiveException(tenant.Status);
        }

        var activeLines = await change.CountActiveLinesAsync(cancellationToken);
        if (activeLines >= tenant.MaxActiveLines)
        {
            throw new LineQuotaFullException(activeLines, tenant.MaxActiveLines);
        }

        var license = await UsableLicenseAsync(change, channel, cancellationToken);
        if (license is null && _licensing.RequireAssignedLicense)
        {
            throw new LicenseRequiredException();
        }

        license?.LinkChannel(channel.Id);
        channel.Activate();
        await change.CommitAsync(cancellationToken);
        return ChannelResponse.From(channel);
    }

    /// <summary>
    /// An assigned licence of the company that matches the line's number and is not backing another line that is on: unlinked, already
    /// linked to this line, or linked to a line that is off (it moves to this one). Read under the company's lock.
    /// </summary>
    private static async Task<LineLicense?> UsableLicenseAsync(ITenantChange change, WhatsAppChannel channel, CancellationToken cancellationToken)
    {
        var matching = (await change.GetAssignedLicensesAsync(cancellationToken))
            .Where(l => l.Matches(channel.DisplayPhoneNumber, channel.ProviderPhoneNumberId))
            .ToList();
        if (matching.Count == 0)
        {
            return null;
        }

        var activeLines = (await change.GetChannelsAsync(cancellationToken)).Where(c => c.IsActive && c.Id != channel.Id).Select(c => c.Id).ToHashSet();
        return matching
            .Where(l => l.ChannelId is null || l.ChannelId == channel.Id || !activeLines.Contains(l.ChannelId.Value))
            .OrderByDescending(l => l.ChannelId == channel.Id)
            .ThenBy(l => l.ChannelId is null ? 0 : 1)
            .FirstOrDefault();
    }

    public async Task<CreateChannelResponse> RotateChannelSecretAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var channel = await _channels.GetForUpdateAsync(id, cancellationToken) ?? throw new ChannelNotFoundException();
        var secret = channel.RotateWebhookSecret();
        await _channels.UpdateAsync(channel, cancellationToken);
        return CreateChannelResponse.From(channel, secret);
    }

    public async Task<ChannelResponse> SetChannelProviderCredentialsAsync(Guid id, SetProviderCredentialsRequest request, CancellationToken cancellationToken = default)
    {
        // The DTO attributes already refused these over HTTP; the service does not depend on having been called through them.
        if (string.IsNullOrWhiteSpace(request.Environment)
            || !Enum.TryParse<WhatsAppProviderEnvironment>(request.Environment.Trim(), ignoreCase: true, out var environment)
            || !Enum.IsDefined(environment)
            || char.IsAsciiDigit(request.Environment.Trim()[0]))
        {
            throw new DomainException(WhatsAppChannel.ProviderEnvironmentRuleMessage);
        }

        var keyGiven = !string.IsNullOrWhiteSpace(request.ApiKey);
        if (keyGiven && !WhatsAppChannel.IsValidProviderApiKey(request.ApiKey))
        {
            throw new DomainException(WhatsAppChannel.ProviderApiKeyRuleMessage);
        }

        var channel = await _channels.GetForUpdateAsync(id, cancellationToken) ?? throw new ChannelNotFoundException();
        if (keyGiven)
        {
            // Only the encrypted form goes any further than this method.
            var ciphertext = _secretProtector.Protect(request.ApiKey!.Trim(), ProviderCredentialPurpose.ForChannel(channel.Id));
            channel.SetProviderCredentials(ciphertext, environment, _timeProvider.GetUtcNow().UtcDateTime);
        }
        else if (channel.HasProviderApiKey)
        {
            channel.SetProviderEnvironment(environment);
        }
        else
        {
            throw new DomainException(MissingProviderApiKeyMessage);
        }

        await _channels.UpdateAsync(channel, cancellationToken);
        return ChannelResponse.From(channel);
    }

    public async Task<ChannelResponse> ClearChannelProviderCredentialsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var channel = await _channels.GetForUpdateAsync(id, cancellationToken) ?? throw new ChannelNotFoundException();
        channel.ClearProviderCredentials();
        await _channels.UpdateAsync(channel, cancellationToken);
        return ChannelResponse.From(channel);
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
