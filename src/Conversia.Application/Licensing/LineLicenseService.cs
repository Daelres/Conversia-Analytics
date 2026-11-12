using Conversia.Application.DTOs;
using Conversia.Application.DTOs.Admin;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;
using Conversia.Domain.Interfaces;
using Conversia.Domain.ReadModels;

namespace Conversia.Application.Licensing;

/// <summary>
/// The platform administrator's licence inventory. Assigning and releasing lock the company's row (<see cref="ITenantUnitOfWork"/>) like
/// every change to a company's lines, so they never interleave with a line being switched on; the licence row itself is protected by its
/// concurrency token (a lost race is a 409). Audit entries carry the licence id and the masked number, never the full number.
/// </summary>
public sealed class LineLicenseService : ILineLicenseService
{
    private readonly ILineLicenseRepository _licenses;
    private readonly ITenantUnitOfWork _unitOfWork;
    private readonly LicensingSettings _settings;
    private readonly TimeProvider _time;

    public LineLicenseService(ILineLicenseRepository licenses, ITenantUnitOfWork unitOfWork, LicensingSettings settings, TimeProvider time)
    {
        _licenses = licenses;
        _unitOfWork = unitOfWork;
        _settings = settings;
        _time = time;
    }

    public async Task<PagedResponse<LineLicenseResponse>> ListAsync(AdminLicensesQuery query, CancellationToken cancellationToken = default)
    {
        var status = Enum.TryParse<LineLicenseStatus>(query.Status, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) && !int.TryParse(query.Status, out _)
            ? parsed
            : (LineLicenseStatus?)null;
        var page = await _licenses.ListAsync(new LineLicenseFilter(status, query.TenantId), query.Page, query.PageSize, cancellationToken);
        return new PagedResponse<LineLicenseResponse>
        {
            Items = page.Items.Select(LineLicenseResponse.From).ToList(),
            Page = query.Page,
            PageSize = query.PageSize,
            TotalCount = page.TotalCount,
        };
    }

    public async Task<LineLicenseResponse> GetAsync(Guid id, CancellationToken cancellationToken = default)
        => LineLicenseResponse.From(await _licenses.GetRowAsync(id, cancellationToken) ?? throw new LineLicenseNotFoundException());

    public async Task<LineLicenseResponse> CreateAsync(Guid adminId, CreateLineLicenseRequest request, CancellationToken cancellationToken = default)
    {
        var cost = request.MonthlyCostUsd ?? throw new DomainException(LineLicense.CostRuleMessage);
        var license = LineLicense.Create(request.PhoneNumber, request.ProviderChannelId, cost, request.CustomerOwned, request.Notes);
        _licenses.Add(license);
        await _licenses.SaveAsync(Audit(adminId, PlatformAuditLog.Actions.LicenseCreated, null, license, new()
        {
            ["customerOwned"] = license.CustomerOwned,
            ["monthlyCostUsd"] = license.MonthlyCostUsd,
        }), cancellationToken);
        return LineLicenseResponse.From(license);
    }

    public async Task<LineLicenseResponse> AssignAsync(Guid adminId, Guid id, AssignLineLicenseRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = request.TenantId ?? throw new DomainException("Indica la empresa a la que se asigna la licencia.");
        var license = await _licenses.GetAsync(id, cancellationToken) ?? throw new LineLicenseNotFoundException();
        if (license.Status != LineLicenseStatus.Available)
        {
            throw new LineLicenseConflictException(LineLicenseConflictException.NotAvailable);
        }

        string tenantName;
        await using (var change = await _unitOfWork.BeginAsync(tenantId, cancellationToken) ?? throw new TenantNotFoundException())
        {
            tenantName = change.Tenant.Name;
            if (change.Tenant.Status == TenantStatus.Cancelled)
            {
                // Its licences were released at the cancellation; a new one would outlive the company's data.
                throw new TenantLifecycleConflictException(TenantLifecycleConflictException.Cancelled);
            }

            license.AssignTo(tenantId, _time.GetUtcNow().UtcDateTime);

            // A line the company already registered with this number is linked now (an active one first); one already backed by another
            // assigned licence of the company is left to it.
            var taken = (await change.GetAssignedLicensesAsync(cancellationToken)).Where(l => l.Id != license.Id && l.ChannelId is not null).Select(l => l.ChannelId!.Value).ToHashSet();
            var line = (await change.GetChannelsAsync(cancellationToken))
                .Where(c => !taken.Contains(c.Id) && license.Matches(c.DisplayPhoneNumber, c.ProviderPhoneNumberId))
                .OrderByDescending(c => c.IsActive)
                .FirstOrDefault();
            if (line is not null)
            {
                license.LinkChannel(line.Id);
            }

            change.Record(Audit(adminId, PlatformAuditLog.Actions.LicenseAssigned, tenantId, license, new() { ["channelId"] = license.ChannelId }));
            await change.CommitAsync(cancellationToken);
        }

        return LineLicenseResponse.From(license, tenantName);
    }

    public async Task<LineLicenseResponse> ReleaseAsync(Guid adminId, Guid id, CancellationToken cancellationToken = default)
    {
        var license = await _licenses.GetAsync(id, cancellationToken) ?? throw new LineLicenseNotFoundException();
        if (license.Status != LineLicenseStatus.Assigned || license.AssignedTenantId is not { } tenantId)
        {
            throw new LineLicenseConflictException(LineLicenseConflictException.NotAssigned);
        }

        await using (var change = await _unitOfWork.BeginAsync(tenantId, cancellationToken) ?? throw new TenantNotFoundException())
        {
            var deactivated = false;
            if (_settings.RequireAssignedLicense && license.ChannelId is { } channelId
                && (await change.GetChannelsAsync(cancellationToken)).FirstOrDefault(c => c.Id == channelId) is { IsActive: true } line)
            {
                // With licences required, a line works only while its licence is assigned.
                line.Deactivate();
                deactivated = true;
            }

            var channel = license.ChannelId;
            license.Release(_time.GetUtcNow().UtcDateTime);
            change.Record(Audit(adminId, PlatformAuditLog.Actions.LicenseReleased, tenantId, license, new()
            {
                ["channelId"] = channel,
                ["lineDeactivated"] = deactivated,
            }));
            await change.CommitAsync(cancellationToken);
        }

        return LineLicenseResponse.From(license);
    }

    public async Task<LineLicenseResponse> RetireAsync(Guid adminId, Guid id, CancellationToken cancellationToken = default)
    {
        var license = await _licenses.GetAsync(id, cancellationToken) ?? throw new LineLicenseNotFoundException();
        switch (license.Status)
        {
            case LineLicenseStatus.Assigned:
                throw new LineLicenseConflictException(LineLicenseConflictException.StillAssigned);
            case LineLicenseStatus.Retired:
                throw new LineLicenseConflictException(LineLicenseConflictException.NotAvailable);
        }

        license.Retire(_time.GetUtcNow().UtcDateTime);
        await _licenses.SaveAsync(Audit(adminId, PlatformAuditLog.Actions.LicenseRetired, null, license, []), cancellationToken);
        return LineLicenseResponse.From(license);
    }

    // The licence id and its masked number: enough to tell it apart, never the full number.
    private static PlatformAuditLog Audit(Guid adminId, string action, Guid? tenantId, LineLicense license, Dictionary<string, object?> details)
    {
        details["licenseId"] = license.Id;
        details["number"] = license.MaskedPhoneNumber;
        return PlatformAuditLog.Record(adminId, action, tenantId, details);
    }
}
