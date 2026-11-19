namespace Conversia.Infrastructure.Tenants;

/// <summary>
/// The tables a company's data lives in, in the order they are emptied: children before parents, because every foreign key is
/// <c>RESTRICT</c> (a statement that would orphan a row fails, and the batch rolls back). The plan names its tables explicitly; the
/// integration tests compare it with the EF model, so a new table with a <c>TenantId</c> fails the build of the plan until it is added here.
/// Kept on purpose (<see cref="Kept"/>): the platform audit trail and the deletion tombstone, which hold no content.
/// </summary>
internal static class TenantDataDeletionPlan
{
    /// <summary>One table to empty. <see cref="SelectSql"/> picks a batch of its rows of the company (<c>{0}</c> the company, <c>{1}</c> the batch size).</summary>
    public sealed record Step(string Table, string SelectSql);

    private static Step ByTenant(string table)
        => new(table, $$"""SELECT ctid FROM "{{table}}" WHERE "TenantId" = {0} LIMIT {1}""");

    public static readonly IReadOnlyList<Step> Steps =
    [
        ByTenant("Messages"), // -> Conversations, WhatsAppChannels
        ByTenant("MediaObjectDeletions"), // the queue of media objects to delete (the store itself is emptied by prefix, TenantDataDeleter)
        ByTenant("Opportunities"), // -> Conversations
        ByTenant("ConversationAssistants"), // -> Conversations; the assistant's state and timer per conversation (codes and times)
        ByTenant("AiUsageConversations"), // the AI distinct conversation sets (design D24: they go with the conversations)
        ByTenant("LlmBatchRequests"), // -> Conversations, LlmBatches; ids and hashes of the batched analyses (the batch rows hold no company)
        ByTenant("Conversations"), // -> Contacts, WhatsAppChannels, Branches, Advisors
        ByTenant("ContactSuppressions"),
        ByTenant("Contacts"),
        ByTenant("WebhookEvents"), // -> WhatsAppChannels; the raw payloads (numbers, texts)
        ByTenant("AiUsageMonths"),
        ByTenant("AiAnalysisRuns"), // counts per analysis window
        ByTenant("MessageSendOutcomes"),
        ByTenant("AiQuotaGrants"), // -> BillingCharges
        ByTenant("BillingPaymentAttempts"), // -> BillingCharges; ids, amounts and statuses (the audit trail keeps the money history)
        ByTenant("BillingCharges"), // -> BillingSubscriptions
        ByTenant("BillingSubscriptions"), // -> Tenants, BillingPlans
        ByTenant("ReportDeliveries"), // -> ManagerReports, Users; ids, statuses and codes of the report sends
        ByTenant("ManagerReports"), // -> ReportDefinitions; the generated reports (aggregates and their text, no customer data)
        ByTenant("ReportRecipients"), // -> ReportDefinitions, Users
        ByTenant("ReportDefinitions"),
        ByTenant("ReportLedgerEntries"), // counts and money of the written reports
        ByTenant("AssistantKnowledgeVersions"), // the company's knowledge base versions (business facts, no customer data)
        ByTenant("AssistantSettings"), // the assistant's settings and the owner's consent evidence
        ByTenant("AssistantActivityCounts"), // counts of the assistant's activity
        ByTenant("AssistantAuditEntries"), // the history of the assistant's settings and consent (ids and the company's own settings)
        ByTenant("MessageTemplates"), // -> WhatsAppChannels; the business's own template texts (no customer data)
        ByTenant("WhatsAppChannels"), // the lines, with their encrypted provider key -> Branches
        ByTenant("Advisors"), // -> Branches, Users
        ByTenant("Branches"),
        new("UserPermissions", """SELECT up.ctid FROM "UserPermissions" up JOIN "Users" u ON u."Id" = up."UserId" WHERE u."TenantId" = {0} LIMIT {1}"""),
        ByTenant("Users"),
        ByTenant("ErrorLogs"), // the error registry rows of the company (no foreign key)
    ];

    /// <summary>
    /// The tables with a <c>TenantId</c> that keep the company's rows: they hold ids, statuses, counts and hashes only (the billing review
    /// items: ids, amounts and a reason code, kept like the audit trail until a person closes them).
    /// </summary>
    public static readonly IReadOnlySet<string> Kept = new HashSet<string>(StringComparer.Ordinal) { "PlatformAuditLogs", "TenantDeletions", "BillingReviewItems" };
}
