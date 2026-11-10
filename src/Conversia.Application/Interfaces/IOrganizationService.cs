using Conversia.Application.DTOs.Organization;

namespace Conversia.Application.Interfaces;

/// <summary>Branches, advisors and WhatsApp channels of the caller's tenant.</summary>
public interface IOrganizationService
{
    Task<IReadOnlyList<BranchResponse>> ListBranchesAsync(CancellationToken cancellationToken = default);

    /// <summary>Throws <see cref="Exceptions.BranchNotFoundException"/> for a missing or other-tenant branch.</summary>
    Task<BranchResponse> GetBranchAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Throws <see cref="Exceptions.DuplicateBranchNameException"/> when the tenant already holds the name (ignoring case).</summary>
    Task<BranchResponse> CreateBranchAsync(Guid callerTenantId, CreateBranchRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AdvisorResponse>> ListAdvisorsAsync(CancellationToken cancellationToken = default);

    /// <summary>Throws <see cref="Exceptions.AdvisorNotFoundException"/> for a missing or other-tenant advisor.</summary>
    Task<AdvisorResponse> GetAdvisorAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an advisor. A branch or user that does not exist in the caller's tenant is a <see cref="Domain.Exceptions.DomainException"/>
    /// (400): the request body is wrong, not the URL.
    /// </summary>
    Task<AdvisorResponse> CreateAdvisorAsync(Guid callerTenantId, CreateAdvisorRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ChannelResponse>> ListChannelsAsync(CancellationToken cancellationToken = default);

    /// <summary>Throws <see cref="Exceptions.ChannelNotFoundException"/> for a missing or other-tenant channel.</summary>
    Task<ChannelResponse> GetChannelAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Creates a channel and returns its webhook secret, which is shown only here. Same branch rule as <see cref="CreateAdvisorAsync"/>.</summary>
    Task<CreateChannelResponse> CreateChannelAsync(Guid callerTenantId, CreateChannelRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Turns the channel's webhook on or off: an inactive channel answers 401 to the provider. Idempotent. Switching ON checks the company's
    /// line quota under the lock of its row: <see cref="Exceptions.LineQuotaFullException"/> (409) when it is full,
    /// <see cref="Exceptions.TenantNotActiveException"/> when the company is not Active. Switching off is always allowed.
    /// Throws <see cref="Exceptions.ChannelNotFoundException"/>.
    /// </summary>
    Task<ChannelResponse> SetChannelActiveAsync(Guid callerTenantId, Guid id, bool active, CancellationToken cancellationToken = default);

    /// <summary>Generates a new webhook secret, returned once; the previous one stops working at once. Throws <see cref="Exceptions.ChannelNotFoundException"/>.</summary>
    Task<CreateChannelResponse> RotateChannelSecretAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the provider API key of the channel, encrypted, together with its environment; without a key in the request it changes only
    /// the environment of a channel that already has one. The answer never carries the key. Throws
    /// <see cref="Exceptions.ChannelNotFoundException"/>, <see cref="Domain.Exceptions.DomainException"/> (no key given and none stored, or an
    /// invalid key or environment) and <see cref="Exceptions.SecretProtectionException"/> when the key cannot be encrypted.
    /// </summary>
    Task<ChannelResponse> SetChannelProviderCredentialsAsync(Guid id, SetProviderCredentialsRequest request, CancellationToken cancellationToken = default);

    /// <summary>Forgets the provider API key of the channel (the environment stays). Idempotent. Throws <see cref="Exceptions.ChannelNotFoundException"/>.</summary>
    Task<ChannelResponse> ClearChannelProviderCredentialsAsync(Guid id, CancellationToken cancellationToken = default);
}
