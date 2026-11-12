using Conversia.Domain.Common;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.UnitTests.Entities;

/// <summary>The system actor of the audit trail, the deletion tombstone and the contact suppression marker.</summary>
public class TenantDataDeletionEntitiesTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 15, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ASystemAuditEntry_HasNoAdministrator_AndCarriesItsReasonCode()
    {
        var tenantId = Guid.NewGuid();

        var entry = PlatformAuditLog.RecordSystem(PlatformAuditLog.Actions.TenantStatusChanged, tenantId, ReasonCodes.BillingPastDue,
            new Dictionary<string, object?> { ["from"] = "Active", ["to"] = "Suspended" });

        Assert.Null(entry.AdminId);
        Assert.True(entry.IsSystem);
        Assert.Equal(ReasonCodes.BillingPastDue, entry.ReasonCode);
        Assert.Equal(tenantId, entry.TenantId);
    }

    [Fact]
    public void ASystemAuditEntry_NeedsAValidReasonCode()
    {
        Assert.Throws<DomainException>(() => PlatformAuditLog.RecordSystem(PlatformAuditLog.Actions.TenantStatusChanged, Guid.NewGuid(), "", new Dictionary<string, object?>()));
        Assert.Throws<DomainException>(() => PlatformAuditLog.RecordSystem(PlatformAuditLog.Actions.TenantStatusChanged, Guid.NewGuid(), "Pago atrasado", new Dictionary<string, object?>()));
    }

    [Fact]
    public void AnAdministratorEntry_StillNeedsTheAdministrator_AndMayCarryAReasonCode()
    {
        Assert.Throws<DomainException>(() => PlatformAuditLog.Record(Guid.Empty, PlatformAuditLog.Actions.TenantStatusChanged, null, new Dictionary<string, object?>()));

        var admin = Guid.NewGuid();
        var entry = PlatformAuditLog.Record(admin, PlatformAuditLog.Actions.TenantDataDeletionStarted, Guid.NewGuid(), new Dictionary<string, object?>(),
            ReasonCodes.DeletionRequestedByAdmin);

        Assert.Equal(admin, entry.AdminId);
        Assert.False(entry.IsSystem);
        Assert.Equal(ReasonCodes.DeletionRequestedByAdmin, entry.ReasonCode);
        Assert.Throws<DomainException>(() => PlatformAuditLog.Record(admin, PlatformAuditLog.Actions.TenantStatusChanged, null, new Dictionary<string, object?>(), "No Válido"));
    }

    private static readonly string Hex64 = new('a', 64);

    [Fact]
    public void TheTombstone_KeepsTheKeyedHashOfTheNameItIsGiven_AndTheKeyId_NeverTheName()
    {
        var tenant = Tenant.Create("Clínica Demo");

        var record = TenantDeletion.Start(tenant, Hex64, "k2", TenantDeletionTrigger.Scheduled, null, Now);

        Assert.Equal(tenant.Id, record.TenantId);
        Assert.Equal(Hex64, record.NameHash);
        Assert.Equal("k2", record.HashKeyId);
        Assert.Equal(Now, record.StartedAt);
        Assert.Null(record.CompletedAt);
        Assert.Null(record.RequestedBy);
        Assert.Equal(TenantDeletionTrigger.Scheduled, record.Trigger);
    }

    [Theory]
    [InlineData("Clínica Demo")]
    [InlineData("")]
    [InlineData("ABCDEF")]
    public void TheTombstone_RefusesAnythingButAHexDigest(string notAHash)
        => Assert.Throws<DomainException>(() => TenantDeletion.Start(Tenant.Create("Clínica Demo"), notAHash, "k1", TenantDeletionTrigger.Manual, Guid.NewGuid(), Now));

    [Fact]
    public void TheTombstone_AddsUpTheRowsDeletedPerTable_AndCompletesOnce()
    {
        var record = TenantDeletion.Start(Tenant.Create("Clínica Demo"), Hex64, "k1", TenantDeletionTrigger.Manual, Guid.NewGuid(), Now);

        record.AddDeleted("Messages", 1000);
        record.AddDeleted("Messages", 15);
        record.AddDeleted("Contacts", 3);
        record.AddDeleted("Users", 0);

        Assert.Equal(1015, record.DeletedCounts()["Messages"]);
        Assert.Equal(3, record.DeletedCounts()["Contacts"]);
        Assert.False(record.DeletedCounts().ContainsKey("Users")); // nothing deleted, nothing written
        Assert.Equal(1018, record.TotalDeleted);

        record.Complete(Now.AddMinutes(5));
        Assert.Equal(Now.AddMinutes(5), record.CompletedAt);
        Assert.Throws<DomainException>(() => record.Complete(Now.AddMinutes(6)));
        Assert.Throws<DomainException>(() => record.AddDeleted("Messages", -1));
    }

    [Fact]
    public void TheSuppressionMarker_HoldsTheKeyedHashItIsGiven_WithItsKeyId()
    {
        var tenant = Guid.NewGuid();
        var marker = ContactSuppression.Create(tenant, Hex64, "k1", Guid.NewGuid(), Now);

        Assert.Equal(Hex64, marker.WaIdHash);
        Assert.Equal("k1", marker.HashKeyId);
        Assert.Equal(Now, marker.SuppressedAt);
        Assert.True(marker.Covers(Now)); // the boundary: a message sent at the very instant is the suppressed contact's
        Assert.False(marker.Covers(Now.AddTicks(10)));
        Assert.Throws<DomainException>(() => ContactSuppression.Create(tenant, "573001234567", "k1", Guid.NewGuid(), Now));
    }

    [Fact]
    public void SuppressingTheSameNumberAgain_MovesTheCutoffForward_Only()
    {
        var marker = ContactSuppression.Create(Guid.NewGuid(), Hex64, "k1", Guid.NewGuid(), Now);

        marker.Renew(Now.AddDays(1));
        Assert.Equal(Now.AddDays(1), marker.SuppressedAt);

        marker.Renew(Now);
        Assert.Equal(Now.AddDays(1), marker.SuppressedAt);
    }
}
