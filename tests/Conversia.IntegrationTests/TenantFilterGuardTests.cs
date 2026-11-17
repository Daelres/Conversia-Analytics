using Conversia.Domain.Entities;
using Conversia.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Conversia.IntegrationTests;

/// <summary>
/// Isolation is the query filters, so the places that switch them off are few, known and reviewed: a new one fails these tests until
/// somebody decides, in the open, that it is legitimate.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class TenantFilterGuardTests
{
    // Every file of src/ that calls IgnoreQueryFilters (migrations aside), and why.
    private static readonly string[] AllowedFiles =
    [
        "Conversia.Infrastructure/Classification/ClassificationBacklogMonitor.cs", // counts of the shared backlog (operations view)
        "Conversia.Infrastructure/Classification/ConversationClassificationRunner.cs", // the worker claims across companies
        "Conversia.Infrastructure/Classification/LlmAnalysisRunner.cs", // claims and relocks name the company explicitly; batch collection spans companies
        "Conversia.Infrastructure/Classification/LlmAnalysisScheduler.cs", // which companies are due: every company's pending conversations and window counts
        "Conversia.Infrastructure/Classification/Usage/AiUsageMeter.cs", // the deployment USD cap sums every company; the recorder has no company set
        "Conversia.Infrastructure/Conversations/ConversationAutoCloser.cs", // the worker claims across companies
        "Conversia.Infrastructure/Messaging/MessageSendOutcomeRecorder.cs", // retention of the send counters across companies
        "Conversia.Infrastructure/Messaging/TemplateSyncService.cs", // the periodic template sync lists the channels of every company, then syncs each in its own company
        "Conversia.Infrastructure/Monitoring/PlatformMonitoringQueries.cs", // the platform administrator's monitoring counts
        "Conversia.Infrastructure/Repositories/BillingRepositories.cs", // the billing lock and lookups name the company explicitly; the job and events run without one
        "Conversia.Infrastructure/Repositories/PlatformAdminQueries.cs", // the platform administrator's scalar projections
        "Conversia.Infrastructure/Repositories/TenantUnitOfWork.cs", // names the locked company explicitly in every query
        "Conversia.Infrastructure/Repositories/UserRepository.cs", // the email is unique across companies (login, registration)
        "Conversia.Infrastructure/Repositories/WhatsAppChannelRepository.cs", // the webhook finds its channel by key
        "Conversia.Infrastructure/Webhooks/WebhookEventPurger.cs", // retention across companies
        "Conversia.Infrastructure/Webhooks/WebhookEventRunner.cs", // the worker claims across companies
        "Conversia.Infrastructure/Webhooks/WebhookQueueMonitor.cs", // counts of the shared queue (operations view)
    ];

    private readonly CustomWebApplicationFactory _factory;

    public TenantFilterGuardTests(CustomWebApplicationFactory factory) => _factory = factory;

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Conversia.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Conversia.slnx not found."), "src");
    }

    [Fact]
    public void OnlyTheKnownFilesSwitchTheTenantFiltersOff()
    {
        var root = SourceRoot();
        var users = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains("IgnoreQueryFilters", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(AllowedFiles.Order(StringComparer.Ordinal), users);
    }

    [Fact]
    public void EveryEntityWithATenantHasTheFilter_ExceptThePlatformTables()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var unfiltered = db.Model.GetEntityTypes()
            .Where(t => t.GetDeclaredQueryFilters().Count == 0 && t.FindProperty("TenantId") is not null)
            .Select(t => t.ClrType)
            .ToHashSet();

        // ErrorLogs: the operations registry; PlatformAuditLogs: the administrators' trail; TenantDeletions: the tombstone of a company
        // whose data is gone (its key is the id the company had, and there is no company left to filter by); BillingReviewItems: money for a
        // person to look at, which may name no company (an unmatched payment); only the administrator reads it.
        Assert.Equal(new HashSet<Type> { typeof(ErrorLog), typeof(PlatformAuditLog), typeof(TenantDeletion), typeof(BillingReviewItem) }, unfiltered);
    }

    [Fact]
    public void TheLicenceInventory_IsAPlatformTable_WhoseCompanyIsData_NotATenantColumn()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var licenses = db.Model.FindEntityType(typeof(LineLicense))!;

        Assert.Null(licenses.FindProperty("TenantId"));
        Assert.NotNull(licenses.FindProperty(nameof(LineLicense.AssignedTenantId)));
        Assert.Empty(licenses.GetDeclaredQueryFilters());
    }

    [Fact]
    public void ThePlatformAdminsTableHasNoTenant()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        Assert.Null(db.Model.FindEntityType(typeof(PlatformAdmin))!.FindProperty("TenantId"));
    }
}
