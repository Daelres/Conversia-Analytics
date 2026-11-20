using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Exceptions;

namespace Conversia.Domain.UnitTests.Entities;

/// <summary>The rules of a report definition, a delivery's states and the WhatsApp opt-in (docs/REPORTS.md).</summary>
public class ManagerReportTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    private static ReportDefinitionSettings Settings(
        string name = "Resumen semanal", ReportChannels channels = ReportChannels.Email, ReportSections sections = ReportSections.All,
        int hour = 7, int day = 1, Guid? channelId = null, string? template = null, string? language = null, params Guid[] users)
        => new(name, true, ReportFrequency.Weekly, day, hour, [], sections,
            (users.Length == 0 ? [Guid.NewGuid()] : users).Select(u => new ReportRecipientSetting(u, channels)).ToList(), channelId, template, language);

    [Fact]
    public void A_valid_definition_keeps_what_was_set()
    {
        var user = Guid.NewGuid();
        var branch = Guid.NewGuid();
        var definition = ReportDefinition.Create(Tenant, Settings(users: [user]) with { BranchIds = [branch, branch, Guid.Empty] });

        Assert.Equal("Resumen semanal", definition.Name);
        Assert.Equal([branch], definition.BranchIds);
        Assert.Equal(ReportSections.All, definition.Sections);
        var recipient = Assert.Single(definition.Recipients);
        Assert.Equal((user, definition.Id, Tenant), (recipient.UserId, recipient.DefinitionId, recipient.TenantId));
        Assert.True(recipient.ReceivesEmail);
        Assert.False(recipient.ReceivesWhatsApp);
    }

    [Theory]
    [InlineData("", 7, 1, ReportSections.All)]
    [InlineData("Informe", 24, 1, ReportSections.All)]
    [InlineData("Informe", -1, 1, ReportSections.All)]
    [InlineData("Informe", 7, 0, ReportSections.All)]
    [InlineData("Informe", 7, 8, ReportSections.All)]
    [InlineData("Informe", 7, 1, ReportSections.None)]
    [InlineData("Informe", 7, 1, (ReportSections)64)]
    public void Invalid_settings_are_refused_in_Spanish(string name, int hour, int day, ReportSections sections)
        => Assert.Throws<DomainException>(() => ReportDefinition.Create(Tenant, Settings(name, sections: sections, hour: hour, day: day)));

    [Fact]
    public void Recipients_are_required_unique_and_need_a_channel()
    {
        var user = Guid.NewGuid();
        Assert.Equal(ReportDefinition.RecipientsRequiredMessage,
            Assert.Throws<DomainException>(() => ReportDefinition.Create(Tenant, Settings() with { Recipients = [] })).Message);
        Assert.Equal(ReportDefinition.RecipientDuplicateMessage,
            Assert.Throws<DomainException>(() => ReportDefinition.Create(Tenant, Settings(users: [user, user]))).Message);
        Assert.Equal(ReportDefinition.RecipientChannelMessage,
            Assert.Throws<DomainException>(() => ReportDefinition.Create(Tenant, Settings(channels: ReportChannels.None))).Message);
    }

    [Fact]
    public void The_recipient_limit_is_the_deployments()
    {
        var settings = Settings(users: [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()]);

        Assert.Contains("como máximo 2", Assert.Throws<DomainException>(() => ReportDefinition.Create(Tenant, settings, maxRecipients: 2)).Message);
        Assert.Equal(3, ReportDefinition.Create(Tenant, settings, maxRecipients: 3).Recipients.Count);
    }

    [Fact]
    public void A_WhatsApp_recipient_needs_the_whole_template_choice()
    {
        Assert.Equal(ReportDefinition.WhatsAppTemplateRequiredMessage,
            Assert.Throws<DomainException>(() => ReportDefinition.Create(Tenant, Settings(channels: ReportChannels.Both))).Message);
        Assert.Equal(ReportDefinition.WhatsAppTemplateIncompleteMessage,
            Assert.Throws<DomainException>(() => ReportDefinition.Create(Tenant, Settings(channelId: Guid.NewGuid(), template: "resumen"))).Message);

        var definition = ReportDefinition.Create(Tenant, Settings(channels: ReportChannels.WhatsApp, channelId: Guid.NewGuid(), template: "resumen_gerente", language: "es"));
        Assert.Equal("resumen_gerente", definition.WhatsAppTemplateName);
    }

    [Fact]
    public void An_update_keeps_the_unsubscribe_of_a_recipient_who_stays_and_drops_the_removed_ones()
    {
        var (stays, leaves, joins) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var definition = ReportDefinition.Create(Tenant, Settings(users: [stays, leaves]));
        var now = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        definition.Recipients.Single(r => r.UserId == stays).UnsubscribeEmail(now);

        definition.Update(Settings(name: "Otro", users: [stays, joins]));

        Assert.Equal([stays, joins], definition.Recipients.Select(r => r.UserId));
        var kept = definition.Recipients.Single(r => r.UserId == stays);
        Assert.Equal(now, kept.EmailUnsubscribedAt);
        Assert.False(kept.ReceivesEmail);
        kept.UnsubscribeEmail(now.AddDays(1));
        Assert.Equal(now, kept.EmailUnsubscribedAt);
    }

    [Fact]
    public void A_delivery_goes_from_pending_to_an_attempt_and_then_sent_retried_or_failed()
    {
        var now = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        var delivery = ReportDelivery.Pending(Tenant, Guid.NewGuid(), Guid.NewGuid(), ReportDeliveryChannel.Email, now);
        Assert.Equal((ReportDeliveryStatus.Pending, now), (delivery.Status, delivery.NextAttemptAt));

        delivery.BeginAttempt(now, now.AddMinutes(5));
        Assert.Equal((1, now.AddMinutes(5)), (delivery.Attempts, delivery.NextAttemptAt));
        delivery.MarkRetry("smtp_unavailable", now.AddMinutes(10));
        Assert.Equal((ReportDeliveryStatus.Pending, "smtp_unavailable"), (delivery.Status, delivery.ErrorCode));

        delivery.BeginAttempt(now.AddMinutes(10), now.AddMinutes(15));
        delivery.MarkSent(now.AddMinutes(10), "id-1");
        Assert.Equal((ReportDeliveryStatus.Sent, null, (DateTime?)null), (delivery.Status, delivery.ErrorCode, delivery.NextAttemptAt));
        Assert.Throws<DomainException>(() => delivery.BeginAttempt(now, now));

        var failed = ReportDelivery.Pending(Tenant, Guid.NewGuid(), Guid.NewGuid(), ReportDeliveryChannel.WhatsApp, now);
        failed.MarkFailed("template_unavailable");
        Assert.Equal(ReportDeliveryStatus.Failed, failed.Status);
        Assert.Equal(ReportDeliveryStatus.Skipped, ReportDelivery.Skipped(Tenant, Guid.NewGuid(), Guid.NewGuid(), ReportDeliveryChannel.WhatsApp, "no_opt_in", now).Status);
    }

    [Fact]
    public void A_report_needs_a_real_period()
    {
        var definition = ReportDefinition.Create(Tenant, Settings());
        var day = new DateOnly(2026, 11, 9);
        Assert.Throws<DomainException>(() => ManagerReport.Create(
            definition, day, day, false, ReportGenerator.Template, "ai_disabled", 0, null, 0, 0, 0m, "1", "{}", "{}", "", "", "t", DateTime.UtcNow));
    }

    [Fact]
    public void The_WhatsApp_opt_in_needs_explicit_consent_and_a_valid_number_and_withdrawing_it_deletes_the_number()
    {
        var user = User.Create(Tenant, "gerente@example.test", "hash", isOwner: false);
        var now = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(User.ReportsWhatsAppConsentMessage, Assert.Throws<DomainException>(() => user.OptInToReportsOnWhatsApp("+57 300 555 0100", consent: false, now)).Message);
        Assert.Throws<DomainException>(() => user.OptInToReportsOnWhatsApp("no es un número", consent: true, now));
        Assert.False(user.ReceivesReportsOnWhatsApp);

        user.OptInToReportsOnWhatsApp("+57 300 555 0100", consent: true, now);
        Assert.Equal(("573005550100", now), (user.ReportsWhatsAppNumber, user.ReportsWhatsAppOptInAt));
        Assert.False(user.ReceivesReportsOnWhatsApp); // until the number is verified

        user.OptOutOfReportsOnWhatsApp(now.AddDays(1));
        Assert.Equal((null, null, now.AddDays(1)), (user.ReportsWhatsAppNumber, user.ReportsWhatsAppOptInAt, user.ReportsWhatsAppOptOutAt));
        Assert.False(user.ReceivesReportsOnWhatsApp);
    }

    // ---- review: verified WhatsApp numbers and held deliveries -----------------------------------------------------------------

    [Fact]
    public void An_opted_in_number_receives_reports_only_once_its_code_is_verified_within_time_and_attempts()
    {
        var user = User.Create(Tenant, "gerente@example.test", "hash", isOwner: false);
        var now = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        user.OptInToReportsOnWhatsApp("+57 300 555 0100", consent: true, now);
        user.IssueReportsWhatsAppCode("good-hash", now.AddMinutes(15));

        Assert.False(user.ReceivesReportsOnWhatsApp);
        Assert.False(user.VerifyReportsWhatsApp("bad-hash", now));
        Assert.False(user.VerifyReportsWhatsApp("good-hash", now.AddMinutes(16)));
        Assert.True(user.VerifyReportsWhatsApp("good-hash", now.AddMinutes(1)));
        Assert.True(user.ReceivesReportsOnWhatsApp);
        Assert.Equal(now.AddMinutes(1), user.ReportsWhatsAppVerifiedAt);

        user.OptInToReportsOnWhatsApp("+57 300 555 0199", consent: true, now.AddDays(1)); // a new number starts unverified
        Assert.False(user.ReceivesReportsOnWhatsApp);
        Assert.Null(user.ReportsWhatsAppVerifiedAt);
    }

    [Fact]
    public void A_code_is_locked_after_five_wrong_attempts()
    {
        var user = User.Create(Tenant, "gerente@example.test", "hash", isOwner: false);
        var now = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        user.OptInToReportsOnWhatsApp("+57 300 555 0100", consent: true, now);
        user.IssueReportsWhatsAppCode("good-hash", now.AddMinutes(15));

        for (var i = 0; i < User.MaxReportsWhatsAppCodeAttempts; i++)
        {
            Assert.False(user.VerifyReportsWhatsApp("bad-hash", now));
        }

        Assert.False(user.VerifyReportsWhatsApp("good-hash", now));
    }

    [Fact]
    public void A_held_delivery_keeps_its_attempts()
    {
        var now = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        var delivery = ReportDelivery.Pending(Tenant, Guid.NewGuid(), Guid.NewGuid(), ReportDeliveryChannel.Email, now);
        delivery.BeginAttempt(now, now.AddMinutes(10));

        delivery.Postpone("company_inactive", now.AddHours(1));

        Assert.Equal((ReportDeliveryStatus.Pending, 0, "company_inactive", now.AddHours(1)), (delivery.Status, delivery.Attempts, delivery.ErrorCode, delivery.NextAttemptAt));
    }
}
