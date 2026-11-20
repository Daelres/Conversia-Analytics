using Conversia.Application.DTOs.Admin;
using Conversia.Application.Classification.Llm;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Application.Privacy;
using Conversia.Application.Services;
using Conversia.Domain.Common;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;
using Conversia.Domain.Interfaces;
using Conversia.Domain.ReadModels;
using Conversia.Tests.Shared;
using Moq;

namespace Conversia.Application.UnitTests.Services;

/// <summary>
/// The company lifecycle as the administrator and the background jobs (billing, deletion) drive it: what changes, what is audited and what
/// stays untouched. The locking and the SQL are checked against PostgreSQL in the integration tests.
/// </summary>
public class TenantLifecycleServiceTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 15, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IPlatformAdminQueries> _queries = new();
    private readonly Mock<ITenantUnitOfWork> _unitOfWork = new();
    private readonly Mock<ITenantChange> _change = new();
    private readonly List<PlatformAuditLog> _audit = [];
    private readonly List<TenantDeletion> _tombstones = [];
    private readonly List<WhatsAppChannel> _channels = [];
    private readonly List<LineLicense> _licenses = [];
    private readonly Tenant _tenant = Tenant.Create("Clínica Demo");
    private readonly Guid _admin = Guid.NewGuid();
    private readonly RecordingSignal _signal = new();

    public TenantLifecycleServiceTests()
    {
        _change.SetupGet(c => c.Tenant).Returns(_tenant);
        _change.Setup(c => c.Record(It.IsAny<PlatformAuditLog>())).Callback<PlatformAuditLog>(_audit.Add);
        _change.Setup(c => c.AddDeletionRecord(It.IsAny<TenantDeletion>())).Callback<TenantDeletion>(_tombstones.Add);
        _change.Setup(c => c.GetChannelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => _channels);
        _change.Setup(c => c.GetAssignedLicensesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => _licenses.Where(l => l.Status == LineLicenseStatus.Assigned).ToList());
        _unitOfWork.Setup(u => u.BeginAsync(_tenant.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_change.Object);
        _queries.Setup(q => q.GetTenantAsync(_tenant.Id, It.IsAny<CancellationToken>())).ReturnsAsync(() => new TenantAdminDetail(
            new TenantAdminSummary(_tenant.Id, _tenant.Name, _tenant.Status, 4, 0, _channels.Count, "o@x.test", Now, null, null, null), []));
    }

    private sealed class RecordingSignal : ITenantDeletionSignal
    {
        public int Count { get; private set; }

        public void Notify() => Count++;
    }

    private static readonly PrivacyHasher Hasher = new(new PrivacyHashKeys("k1", Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()));

    private TenantLifecycleService Lifecycle(int retentionDays = 30)
        => new(_unitOfWork.Object, new FixedTimeProvider(Now), Hasher, new TenantLifecycleSettings(retentionDays), _signal);

    private PlatformAdminService Admin()
        => new(_queries.Object, _unitOfWork.Object, new FixedTimeProvider(Now), new PlatformLimitDefaults(120, AiQuotaSettings.Default, 1200),
            lifecycle: Lifecycle());

    private void Activate() => _tenant.ChangeStatus(TenantStatus.Active, _admin, null, Now.AddDays(-60));

    private WhatsAppChannel AddLine(string phone, bool active)
    {
        var (channel, _) = WhatsAppChannel.Create(_tenant.Id, Guid.NewGuid(), phone);
        if (active)
        {
            channel.Activate();
        }

        _channels.Add(channel);
        return channel;
    }

    private LineLicense AddLicense(string phone)
    {
        var license = LineLicense.Create(phone, null, 10m, false, null);
        license.AssignTo(_tenant.Id, Now.AddDays(-50));
        _licenses.Add(license);
        return license;
    }

    // ---- Billing hooks (unit 1c calls them) ---------------------------------------------------------

    [Fact]
    public async Task SuspendForBilling_SuspendsAnActiveCompany_AuditedAsTheSystem_WithItsReasonCode()
    {
        Activate();

        var outcome = await Lifecycle().SuspendForBillingAsync(_tenant.Id, ReasonCodes.BillingPastDue);

        Assert.Equal(TenantLifecycleOutcome.Changed, outcome);
        Assert.Equal((TenantStatus.Suspended, SuspensionCause.Billing), (_tenant.Status, _tenant.SuspensionCause));
        var entry = Assert.Single(_audit);
        Assert.True(entry.IsSystem);
        Assert.Equal(ReasonCodes.BillingPastDue, entry.ReasonCode);
        Assert.Equal(PlatformAuditLog.Actions.TenantStatusChanged, entry.Action);
        Assert.Contains("\"cause\":\"Billing\"", entry.Details);
        _change.Verify(c => c.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TheBillingHooks_AreIdempotent_AndWriteNothingWhenNothingChanges()
    {
        Activate();
        var lifecycle = Lifecycle();
        await lifecycle.SuspendForBillingAsync(_tenant.Id, ReasonCodes.BillingPastDue);

        Assert.Equal(TenantLifecycleOutcome.Unchanged, await lifecycle.SuspendForBillingAsync(_tenant.Id, ReasonCodes.BillingPastDue));
        Assert.Equal(TenantLifecycleOutcome.Changed, await lifecycle.ResumeFromBillingAsync(_tenant.Id, ReasonCodes.BillingPaid));
        Assert.Equal(TenantLifecycleOutcome.Unchanged, await lifecycle.ResumeFromBillingAsync(_tenant.Id, ReasonCodes.BillingPaid));

        Assert.Equal(2, _audit.Count);
        _change.Verify(c => c.CommitAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ResumeFromBilling_NeverLiftsAnAdministratorsSuspension()
    {
        Activate();
        _tenant.ChangeStatus(TenantStatus.Suspended, _admin, "Revisión de abuso", Now.AddDays(-1));

        Assert.Equal(TenantLifecycleOutcome.NotApplicable, await Lifecycle().ResumeFromBillingAsync(_tenant.Id, ReasonCodes.BillingPaid));

        Assert.Equal(SuspensionCause.Manual, _tenant.SuspensionCause);
        Assert.Empty(_audit);
        _change.Verify(c => c.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CancelForNonPayment_CancelsSwitchesTheLinesOffReleasesTheLicences_AndSchedulesTheDeletion()
    {
        Activate();
        var on = AddLine("3001112222", active: true);
        AddLine("3003334444", active: false);
        var license = AddLicense("3001112222");

        var outcome = await Lifecycle(retentionDays: 45).CancelForNonPaymentAsync(_tenant.Id, ReasonCodes.BillingNonPayment);

        Assert.Equal(TenantLifecycleOutcome.Changed, outcome);
        Assert.Equal(TenantStatus.Cancelled, _tenant.Status);
        Assert.Equal(Now.AddDays(45), _tenant.DataDeletionDueAt);
        Assert.False(on.IsActive);
        Assert.Equal(LineLicenseStatus.Available, license.Status);
        Assert.Null(license.AssignedTenantId);

        var status = Assert.Single(_audit, a => a.Action == PlatformAuditLog.Actions.TenantStatusChanged);
        Assert.True(status.IsSystem);
        Assert.Contains("\"linesDeactivated\":1", status.Details);
        Assert.Contains("\"licensesReleased\":1", status.Details);
        Assert.Contains("\"deletionDueAt\"", status.Details);
        var released = Assert.Single(_audit, a => a.Action == PlatformAuditLog.Actions.LicenseReleased);
        Assert.True(released.IsSystem);
        Assert.DoesNotContain("3001112222", released.Details); // masked
        _change.Verify(c => c.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TheBillingHooks_OfAnUnknownCompany_AreNotFound()
    {
        _unitOfWork.Setup(u => u.BeginAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((ITenantChange?)null);

        await Assert.ThrowsAsync<TenantNotFoundException>(() => Lifecycle().SuspendForBillingAsync(Guid.NewGuid(), ReasonCodes.BillingPastDue));
    }

    [Fact]
    public async Task TheBillingHooks_RefuseAFreeTextReason_BeforeTouchingTheCompany()
    {
        Activate();

        await Assert.ThrowsAsync<DomainException>(() => Lifecycle().SuspendForBillingAsync(_tenant.Id, "pago atrasado de marzo"));

        Assert.Equal(TenantStatus.Active, _tenant.Status);
        _unitOfWork.Verify(u => u.BeginAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- The administrator ---------------------------------------------------------------------------

    [Fact]
    public async Task AdministratorCancel_DoesTheSameSideEffects_AuditedWithTheAdministrator()
    {
        Activate();
        var on = AddLine("3001112222", active: true);
        AddLicense("3001112222");

        await Admin().ChangeStatusAsync(_admin, _tenant.Id, new ChangeTenantStatusRequest { Status = "Cancelled", Reason = "Fin del contrato" });

        Assert.Equal(TenantStatus.Cancelled, _tenant.Status);
        Assert.Equal(Now.AddDays(30), _tenant.DataDeletionDueAt);
        Assert.False(on.IsActive);
        var status = Assert.Single(_audit, a => a.Action == PlatformAuditLog.Actions.TenantStatusChanged);
        Assert.Equal(_admin, status.AdminId);
        Assert.Contains("\"reason\":\"Fin del contrato\"", status.Details);
        Assert.Contains(_audit, a => a.Action == PlatformAuditLog.Actions.LicenseReleased && a.AdminId == _admin);
    }

    [Fact]
    public async Task AdministratorPause_ChangesOnlyTheStatus_LinesStayOn()
    {
        Activate();
        var on = AddLine("3001112222", active: true);

        await Admin().ChangeStatusAsync(_admin, _tenant.Id, new ChangeTenantStatusRequest { Status = "Paused", Reason = "Vacaciones" });

        Assert.Equal(TenantStatus.Paused, _tenant.Status);
        Assert.True(on.IsActive);
        Assert.Contains("\"to\":\"Paused\"", Assert.Single(_audit).Details);
    }

    [Fact]
    public async Task Reactivating_AfterTheDeletionStarted_Is409WithItsCode()
    {
        Activate();
        _tenant.Cancel(_admin, null, null, Now.AddDays(-40), 30);
        _tenant.BeginDataDeletion(Now.AddDays(-1));

        var failure = await Assert.ThrowsAsync<TenantLifecycleConflictException>(() =>
            Admin().ChangeStatusAsync(_admin, _tenant.Id, new ChangeTenantStatusRequest { Status = "Active" }));

        Assert.Equal(TenantLifecycleConflictException.DeletionStarted, failure.Code);
        Assert.Equal(TenantStatus.Cancelled, _tenant.Status);
        Assert.Empty(_audit);
    }

    [Fact]
    public async Task DeleteNow_NeedsACancelledCompany()
    {
        Activate();

        var failure = await Assert.ThrowsAsync<TenantLifecycleConflictException>(() =>
            Admin().RequestDataDeletionAsync(_admin, _tenant.Id, new DeleteTenantDataRequest { ConfirmName = _tenant.Name, Reason = "Solicitud del cliente" }));

        Assert.Equal(TenantLifecycleConflictException.NotCancelled, failure.Code);
        Assert.Null(_tenant.DataDeletionStartedAt);
        Assert.Empty(_tombstones);
    }

    [Theory]
    [InlineData("Clinica Demo")]
    [InlineData("Clínica")]
    [InlineData("")]
    public async Task DeleteNow_NeedsTheExactCompanyName(string typed)
    {
        Activate();
        _tenant.Cancel(_admin, null, null, Now.AddDays(-1), 30);

        await Assert.ThrowsAsync<DomainException>(() =>
            Admin().RequestDataDeletionAsync(_admin, _tenant.Id, new DeleteTenantDataRequest { ConfirmName = typed, Reason = "Solicitud del cliente" }));

        Assert.Null(_tenant.DataDeletionStartedAt);
        _change.Verify(c => c.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteNow_NeedsAReason()
    {
        Activate();
        _tenant.Cancel(_admin, null, null, Now.AddDays(-1), 30);

        await Assert.ThrowsAsync<DomainException>(() =>
            Admin().RequestDataDeletionAsync(_admin, _tenant.Id, new DeleteTenantDataRequest { ConfirmName = _tenant.Name, Reason = " " }));
    }

    [Fact]
    public async Task DeleteNow_StartsTheDeletion_WritesTheTombstone_AuditsIt_AndWakesTheJob()
    {
        Activate();
        _tenant.Cancel(_admin, null, null, Now.AddDays(-1), 30);

        await Admin().RequestDataDeletionAsync(_admin, _tenant.Id, new DeleteTenantDataRequest { ConfirmName = "  clínica demo ", Reason = "Solicitud del cliente" });

        Assert.Equal(Now, _tenant.DataDeletionStartedAt);
        var tombstone = Assert.Single(_tombstones);
        Assert.Equal((TenantDeletionTrigger.Manual, (Guid?)_admin), (tombstone.Trigger, tombstone.RequestedBy));
        var entry = Assert.Single(_audit);
        Assert.Equal(PlatformAuditLog.Actions.TenantDataDeletionStarted, entry.Action);
        Assert.Equal(ReasonCodes.DeletionRequestedByAdmin, entry.ReasonCode);
        Assert.Contains("\"reason\":\"Solicitud del cliente\"", entry.Details);
        Assert.DoesNotContain("Clínica", entry.Details);
        Assert.Equal(1, _signal.Count);
    }

    [Fact]
    public async Task TheScheduledDeletion_StartsOnlyOnceTheWindowPassed_AndOnlyOnce()
    {
        Activate();
        _tenant.Cancel(_admin, null, null, Now.AddDays(-30).AddMinutes(1), 30);
        var lifecycle = Lifecycle();

        Assert.Equal(TenantLifecycleOutcome.NotApplicable, await lifecycle.BeginScheduledDeletionAsync(_tenant.Id, Now));
        Assert.Empty(_tombstones);

        // The window passes:
        var later = new TenantLifecycleService(_unitOfWork.Object, new FixedTimeProvider(Now.AddMinutes(2)), Hasher, new TenantLifecycleSettings(30), _signal);
        Assert.Equal(TenantLifecycleOutcome.Changed, await later.BeginScheduledDeletionAsync(_tenant.Id, Now.AddMinutes(2)));
        Assert.Equal(TenantLifecycleOutcome.Unchanged, await later.BeginScheduledDeletionAsync(_tenant.Id, Now.AddMinutes(2)));

        var tombstone = Assert.Single(_tombstones);
        Assert.Equal((TenantDeletionTrigger.Scheduled, (Guid?)null), (tombstone.Trigger, tombstone.RequestedBy));
        var entry = Assert.Single(_audit);
        Assert.True(entry.IsSystem);
        Assert.Equal(ReasonCodes.DeletionRetentionExpired, entry.ReasonCode);
    }

    [Fact]
    public void TheRetentionWindow_IsValidated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TenantLifecycleSettings(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TenantLifecycleSettings(Tenant.MaxDeletionRetentionDays + 1));
        Assert.Equal(30, TenantLifecycleSettings.Default.DeletionRetentionDays);
    }

    // ---- Second review round ------------------------------------------------------------------------

    [Fact]
    public async Task TheTombstone_KeepsTheKeyedHashOfTheName_UnderTheCurrentKeyId()
    {
        Activate();
        _tenant.Cancel(_admin, null, null, Now.AddDays(-1), 30);

        await Admin().RequestDataDeletionAsync(_admin, _tenant.Id, new DeleteTenantDataRequest { ConfirmName = _tenant.Name, Reason = "Solicitud del cliente" });

        var tombstone = Assert.Single(_tombstones);
        Assert.Equal(Hasher.TenantName(_tenant.Name).Hex, tombstone.NameHash);
        Assert.Equal("k1", tombstone.HashKeyId);
    }

    [Fact]
    public async Task ALatePaymentDuringAPause_IsAuditedAsAHold_AndTheResumeLandsInTheBillingSuspension()
    {
        Activate();
        _tenant.ChangeStatus(TenantStatus.Paused, _admin, null, Now.AddDays(-2));

        Assert.Equal(TenantLifecycleOutcome.Changed, await Lifecycle().SuspendForBillingAsync(_tenant.Id, ReasonCodes.BillingPastDue));
        var hold = Assert.Single(_audit);
        Assert.Contains("\"billingHold\":true", hold.Details);
        Assert.Contains("\"to\":\"Paused\"", hold.Details);

        await Admin().ChangeStatusAsync(_admin, _tenant.Id, new ChangeTenantStatusRequest { Status = "Active", Reason = "Fin de la pausa" });

        Assert.Equal((TenantStatus.Suspended, SuspensionCause.Billing), (_tenant.Status, _tenant.SuspensionCause));
        Assert.Contains("\"to\":\"Suspended\"", _audit[^1].Details);
    }

    [Fact]
    public async Task AnAdministrator_TurnsABillingSuspensionIntoAManualOne_AuditedWithTheCauses()
    {
        Activate();
        await Lifecycle().SuspendForBillingAsync(_tenant.Id, ReasonCodes.BillingPastDue);

        await Admin().ChangeStatusAsync(_admin, _tenant.Id, new ChangeTenantStatusRequest { Status = "Suspended", Reason = "Disputa", ReasonCode = AdminReasonCodes.PaymentIssue });

        Assert.Equal(SuspensionCause.Manual, _tenant.SuspensionCause);
        var entry = _audit[^1];
        Assert.Equal(_admin, entry.AdminId);
        Assert.Equal(AdminReasonCodes.PaymentIssue, entry.ReasonCode);
        Assert.Contains("\"cause\":\"Manual\"", entry.Details);
        Assert.Contains("\"previousCause\":\"Billing\"", entry.Details);
        Assert.Equal(TenantLifecycleOutcome.NotApplicable, await Lifecycle().ResumeFromBillingAsync(_tenant.Id, ReasonCodes.BillingPaid));
    }

    [Fact]
    public async Task AnAdministratorsReasonCode_MustBeOneOfTheList()
    {
        Activate();

        await Assert.ThrowsAsync<DomainException>(() =>
            Admin().ChangeStatusAsync(_admin, _tenant.Id, new ChangeTenantStatusRequest { Status = "Paused", ReasonCode = "admin.vacaciones_de_laura" }));

        Assert.Equal(TenantStatus.Active, _tenant.Status);
    }
}
