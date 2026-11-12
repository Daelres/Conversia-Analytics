using Conversia.Application.Billing;
using Conversia.Application.Exceptions;
using Conversia.Application.Interfaces;
using Conversia.Application.Time;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Interfaces;
using Conversia.Domain.ReadModels;
using Conversia.Tests.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conversia.Application.UnitTests.Billing;

/// <summary>One company's billing rows in memory, behind the unit of work the engine uses. Tracked = the same objects.</summary>
internal sealed class FakeBillingStore : IBillingUnitOfWork
{
    public BillingSubscription? Subscription { get; set; }

    public int MaxActiveLines { get; set; } = 2;

    public TenantStatus TenantStatus { get; set; } = TenantStatus.Active;

    public SuspensionCause? SuspensionCause { get; set; }

    public DateTime? BillingHoldSince { get; set; }

    public List<BillingPlan> Plans { get; } = [];

    public List<BillingAddOn> AddOns { get; } = [];

    public List<BillingCharge> Charges { get; } = [];

    public List<BillingPaymentAttempt> Attempts { get; } = [];

    public List<AiQuotaGrant> Grants { get; } = [];

    public List<PlatformAuditLog> Audit { get; } = [];

    public List<BillingReviewItem> ReviewItems { get; } = [];

    /// <summary>Runs at the start of each lock taken (the count so far): lets a test act as another instance between two steps.</summary>
    public Action<int>? BeforeBegin { get; set; }

    public int Begins { get; private set; }

    public bool HeldByAnotherInstance { get; set; }

    public int Commits { get; private set; }

    public Task<IBillingChange?> BeginAsync(Guid tenantId, bool skipIfLocked = false, CancellationToken cancellationToken = default)
    {
        BeforeBegin?.Invoke(++Begins);
        if (Subscription is null || Subscription.TenantId != tenantId || (skipIfLocked && HeldByAnotherInstance))
        {
            return Task.FromResult<IBillingChange?>(null);
        }

        return Task.FromResult<IBillingChange?>(new Change(this));
    }

    private sealed class Change(FakeBillingStore store) : IBillingChange
    {
        public BillingSubscription Subscription => store.Subscription!;

        public Task<BillingTenantFacts?> GetTenantFactsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<BillingTenantFacts?>(new BillingTenantFacts(store.MaxActiveLines, store.TenantStatus, store.SuspensionCause, store.BillingHoldSince));

        public Task<IReadOnlyList<BillingPaymentAttempt>> GetPendingAttemptsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<BillingPaymentAttempt>>(store.Attempts.Where(a => a.IsPending).ToList());

        public Task<BillingPlan?> GetPlanAsync(Guid planId, CancellationToken cancellationToken = default) => Task.FromResult(store.Plans.FirstOrDefault(p => p.Id == planId));

        public Task<BillingAddOn?> GetAddOnAsync(Guid addOnId, CancellationToken cancellationToken = default) => Task.FromResult(store.AddOns.FirstOrDefault(a => a.Id == addOnId));

        public Task<BillingCharge?> GetOpenChargeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(store.Charges.Where(c => c.IsOpen).OrderBy(c => c.PeriodStart).FirstOrDefault());

        public Task<IReadOnlyList<BillingCharge>> GetOpenChargesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<BillingCharge>>(store.Charges.Where(c => c.IsOpen).OrderBy(c => c.PeriodStart).ToList());

        public Task<BillingCharge?> GetChargeAsync(Guid chargeId, CancellationToken cancellationToken = default) => Task.FromResult(store.Charges.FirstOrDefault(c => c.Id == chargeId));

        public Task<bool> HasChargeForPeriodAsync(DateOnly periodStart, CancellationToken cancellationToken = default)
            => Task.FromResult(store.Charges.Any(c => c.PeriodStart == periodStart));

        public Task<IReadOnlyList<BillingPaymentAttempt>> GetAttemptsAsync(Guid chargeId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<BillingPaymentAttempt>>(store.Attempts.Where(a => a.ChargeId == chargeId).ToList());

        public Task<BillingPaymentAttempt?> GetAttemptAsync(Guid attemptId, CancellationToken cancellationToken = default) => Task.FromResult(store.Attempts.FirstOrDefault(a => a.Id == attemptId));

        public Task<BillingPaymentAttempt?> GetAttemptByTransactionAsync(string transactionId, CancellationToken cancellationToken = default)
            => Task.FromResult(store.Attempts.FirstOrDefault(a => a.GatewayTransactionId == transactionId));

        public void Add(BillingCharge charge) => store.Charges.Add(charge);

        public void Add(BillingPaymentAttempt attempt) => store.Attempts.Add(attempt);

        public void Add(AiQuotaGrant grant) => store.Grants.Add(grant);

        public void Add(BillingReviewItem item) => store.ReviewItems.Add(item);

        public Task<bool> HasOpenReviewItemAsync(string transactionId, CancellationToken cancellationToken = default)
            => Task.FromResult(store.ReviewItems.Any(r => r.TransactionId == transactionId && r.IsOpen));

        public void Record(PlatformAuditLog entry) => store.Audit.Add(entry);

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            store.Commits++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>A gateway that answers what the test scripts and records what it was asked.</summary>
internal sealed class FakeGateway : IPaymentGateway
{
    private int _transactions;

    public bool IsConfigured { get; set; } = true;

    public GatewayBrowserSettings BrowserSettings => new("pub_test_x", "https://sandbox.wompi.co/v1");

    public List<GatewayChargeRequest> Charges { get; } = [];

    /// <summary>The status the next charges get (the transaction is created with it).</summary>
    public PaymentAttemptStatus NextStatus { get; set; } = PaymentAttemptStatus.Approved;

    /// <summary>When set, the outcome of the next charges instead of a created transaction.</summary>
    public GatewayChargeOutcome? NextOutcome { get; set; }

    /// <summary>The amount the gateway reports (null: the one asked).</summary>
    public long? ReportedAmount { get; set; }

    public Dictionary<string, GatewayTransaction> Transactions { get; } = [];

    public List<GatewayPaymentLinkRequest> Links { get; } = [];

    public GatewayPaymentSource Source { get; set; } = new("src-1", "AVAILABLE", null, null);

    public Task<GatewayAcceptance> GetAcceptanceAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new GatewayAcceptance("acc", "https://wompi.example/terms.pdf", "pda", "https://wompi.example/data.pdf"));

    public List<GatewayPaymentSourceRequest> Sources { get; } = [];

    public Task<GatewayPaymentSource> CreatePaymentSourceAsync(GatewayPaymentSourceRequest request, CancellationToken cancellationToken = default)
    {
        Sources.Add(request);
        return Task.FromResult(Source);
    }

    public Task<GatewayChargeResult> ChargeAsync(GatewayChargeRequest request, CancellationToken cancellationToken = default)
    {
        Charges.Add(request);
        if (NextOutcome is GatewayChargeOutcome.Unknown)
        {
            return Task.FromResult(GatewayChargeResult.Unknown("timeout"));
        }

        if (NextOutcome is GatewayChargeOutcome.Refused)
        {
            return Task.FromResult(GatewayChargeResult.Refused("INPUT_VALIDATION_ERROR"));
        }

        var transaction = new GatewayTransaction(
            $"tx-{++_transactions}", NextStatus, NextStatus == PaymentAttemptStatus.Declined ? "Fondos insuficientes" : null,
            ReportedAmount ?? request.AmountInCents, request.Currency, request.Reference, null);
        Transactions[transaction.Id] = transaction;
        return Task.FromResult(new GatewayChargeResult(GatewayChargeOutcome.Created, transaction, null));
    }

    /// <summary>The gateway's later answer about a transaction (a Nequi approval...).</summary>
    public void Settle(string transactionId, PaymentAttemptStatus status)
        => Transactions[transactionId] = Transactions[transactionId] with { Status = status };

    public Dictionary<string, GatewayTransaction> ByReference { get; } = [];

    public int Reads { get; private set; }

    public Task<GatewayTransaction?> GetTransactionAsync(string transactionId, CancellationToken cancellationToken = default)
    {
        Reads++;
        return Task.FromResult(Transactions.GetValueOrDefault(transactionId));
    }

    public Task<GatewayTransaction?> FindTransactionByReferenceAsync(string reference, CancellationToken cancellationToken = default)
        => Task.FromResult(ByReference.GetValueOrDefault(reference));

    public Task<GatewayPaymentLink> CreatePaymentLinkAsync(GatewayPaymentLinkRequest request, CancellationToken cancellationToken = default)
    {
        Links.Add(request);
        return Task.FromResult(new GatewayPaymentLink($"link-{Links.Count}", $"https://checkout.wompi.co/l/link-{Links.Count}"));
    }

    public GatewayEvent? VerifyEvent(ReadOnlySpan<byte> body, string? checksumHeader) => null;
}

/// <summary>The lifecycle hooks as the tenant entity applies them (docs/PLATFORM_ADMIN.md), on the store's company state.</summary>
internal sealed class FakeLifecycle(FakeBillingStore store) : ITenantBillingLifecycle
{
    public List<(string Call, string Code)> Calls { get; } = [];

    public bool Fail { get; set; }

    /// <summary>Runs before each hook: lets a test change the world between the engine's decision and the call.</summary>
    public Action<string>? Before { get; set; }

    public Task<TenantLifecycleOutcome> SuspendForBillingAsync(Guid tenantId, string reasonCode, CancellationToken cancellationToken = default)
        => Record("suspend", reasonCode, () =>
        {
            if (store.TenantStatus == TenantStatus.Suspended && store.SuspensionCause == SuspensionCause.Billing)
            {
                return TenantLifecycleOutcome.Unchanged;
            }

            if (store.TenantStatus == TenantStatus.Paused)
            {
                if (store.BillingHoldSince is not null)
                {
                    return TenantLifecycleOutcome.Unchanged;
                }

                store.BillingHoldSince = DateTime.UtcNow;
                return TenantLifecycleOutcome.Changed;
            }

            if (store.TenantStatus != TenantStatus.Active)
            {
                return TenantLifecycleOutcome.NotApplicable;
            }

            (store.TenantStatus, store.SuspensionCause) = (TenantStatus.Suspended, SuspensionCause.Billing);
            return TenantLifecycleOutcome.Changed;
        });

    public Task<TenantLifecycleOutcome> ResumeFromBillingAsync(Guid tenantId, string reasonCode, CancellationToken cancellationToken = default)
        => Record("resume", reasonCode, () =>
        {
            if (store.TenantStatus == TenantStatus.Paused && store.BillingHoldSince is not null)
            {
                store.BillingHoldSince = null;
                return TenantLifecycleOutcome.Changed;
            }

            if (store.TenantStatus != TenantStatus.Suspended || store.SuspensionCause != SuspensionCause.Billing)
            {
                return store.TenantStatus is TenantStatus.Active or TenantStatus.Paused ? TenantLifecycleOutcome.Unchanged : TenantLifecycleOutcome.NotApplicable;
            }

            (store.TenantStatus, store.SuspensionCause) = (TenantStatus.Active, null);
            return TenantLifecycleOutcome.Changed;
        });

    public Task<TenantLifecycleOutcome> CancelForNonPaymentAsync(Guid tenantId, string reasonCode, CancellationToken cancellationToken = default)
        => Record("cancel", reasonCode, () =>
        {
            if (store.TenantStatus == TenantStatus.Cancelled)
            {
                return TenantLifecycleOutcome.Unchanged;
            }

            if (store.TenantStatus == TenantStatus.Pending)
            {
                return TenantLifecycleOutcome.NotApplicable;
            }

            (store.TenantStatus, store.SuspensionCause, store.BillingHoldSince) = (TenantStatus.Cancelled, null, null);
            return TenantLifecycleOutcome.Changed;
        });

    private Task<TenantLifecycleOutcome> Record(string call, string code, Func<TenantLifecycleOutcome> apply)
    {
        Before?.Invoke(call);
        if (Fail)
        {
            throw new InvalidOperationException("down");
        }

        Calls.Add((call, code));
        return Task.FromResult(apply());
    }
}

/// <summary>An engine on the fakes, at a clock the test moves. Bogota is UTC-5 all year.</summary>
internal sealed class EngineHarness
{
    public static readonly Guid TenantId = Guid.NewGuid();

    public EngineHarness(DateTime utcNow, BillingSettings? settings = null)
    {
        Clock = new FixedTimeProvider(utcNow);
        Settings = settings ?? BillingSettings.Default;
        Lifecycle = new FakeLifecycle(Store);
        Engine = new BillingEngine(Store, Gateway, Lifecycle, Clock, new TenantTimeZone(TenantTimeZone.DefaultId), Settings, Logger);
    }

    public FakeBillingStore Store { get; } = new();

    public FakeGateway Gateway { get; } = new();

    public FakeLifecycle Lifecycle { get; }

    public FixedTimeProvider Clock { get; }

    public BillingSettings Settings { get; }

    public CapturingLogger Logger { get; } = new();

    public BillingEngine Engine { get; }

    public BillingSubscription Subscription => Store.Subscription!;

    public BillingCharge OnlyCharge => Assert.Single(Store.Charges);

    public BillingPlan Subscribe(long pricePerLinePesos = 100_000, int trialDays = 0, bool withCard = true, BillingAddOn? addOn = null, int addOnQuantity = 0)
    {
        var plan = BillingPlan.Create("pro", "Profesional", pricePerLinePesos * 100, 500);
        Store.Plans.Add(plan);
        var now = Clock.GetUtcNow().UtcDateTime;
        Store.Subscription = BillingSubscription.Start(TenantId, plan.Id, new TenantTimeZone(TenantTimeZone.DefaultId).LocalDate(now), trialDays, now);
        if (addOn is not null)
        {
            Store.AddOns.Add(addOn);
            Store.Subscription.SetAddOn(addOn.Id, addOnQuantity);
        }

        if (withCard)
        {
            Store.Subscription.SetPaymentMethod("src-1", PaymentMethodType.Card, "VISA •••• 4242", "owner@example.test", now);
        }

        return plan;
    }

    public Task RunAsync() => Engine.RunAsync(TenantId);

    public void Advance(TimeSpan by) => Clock.Advance(by);

    /// <summary>Moves the clock to the local (Bogota) date and hour.</summary>
    public void MoveTo(DateOnly localDate, int hour)
    {
        var target = localDate.ToDateTime(new TimeOnly(hour, 0), DateTimeKind.Utc).AddHours(5);
        var now = Clock.GetUtcNow().UtcDateTime;
        Assert.True(target >= now, "the clock only moves forward");
        Clock.Advance(target - now);
    }

    public IEnumerable<string> AuditActions => Store.Audit.Select(a => a.Action);
}

internal sealed class CapturingLogger : ILogger<BillingEngine>
{
    public List<(LogLevel Level, EventId Event, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullLogger.Instance.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, eventId, formatter(state, exception)));
}
