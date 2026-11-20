using Conversia.Application.Billing;
using Conversia.Application.DTOs.Billing;
using Conversia.Application.Exceptions;
using Conversia.Application.Time;
using Conversia.Domain.Common;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Interfaces;
using Conversia.Domain.ReadModels;

namespace Conversia.Application.Services;

/// <summary>The billing page of a company (docs/BILLING.md, "The company's billing page"). Every call names the caller's company from its token.</summary>
public interface IBillingService
{
    Task<BillingAccountResponse> GetAccountAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<PaymentMethodSetupResponse> GetPaymentMethodSetupAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task SetPaymentMethodAsync(Guid tenantId, Guid userId, SetPaymentMethodRequest request, CancellationToken cancellationToken = default);

    Task<PayNowResponse> PayNowAsync(Guid tenantId, Guid userId, Guid chargeId, CancellationToken cancellationToken = default);

    Task<PaymentLinkResponse> GetPaymentLinkAsync(Guid tenantId, Guid chargeId, CancellationToken cancellationToken = default);

    Task SetCancelAtPeriodEndAsync(Guid tenantId, Guid userId, bool cancel, CancellationToken cancellationToken = default);
}

/// <summary>The platform administrator's billing: the plan catalogue and each company's subscription (docs/BILLING.md, "Administration").</summary>
public interface IAdminBillingService
{
    Task<IReadOnlyList<PlanResponse>> ListPlansAsync(CancellationToken cancellationToken = default);

    Task<PlanResponse> CreatePlanAsync(Guid adminId, PlanRequest request, CancellationToken cancellationToken = default);

    Task<PlanResponse> UpdatePlanAsync(Guid adminId, Guid planId, PlanRequest request, CancellationToken cancellationToken = default);

    Task<AddOnResponse> CreateAddOnAsync(Guid adminId, Guid planId, AddOnRequest request, CancellationToken cancellationToken = default);

    Task<AddOnResponse> UpdateAddOnAsync(Guid adminId, Guid addOnId, AddOnRequest request, CancellationToken cancellationToken = default);

    /// <summary>Null when the company has no subscription.</summary>
    Task<AdminBillingAccountResponse?> GetAccountAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<AdminBillingAccountResponse> StartAsync(Guid adminId, Guid tenantId, StartSubscriptionRequest request, CancellationToken cancellationToken = default);

    Task<AdminBillingAccountResponse> ChangeAsync(Guid adminId, Guid tenantId, ChangeSubscriptionRequest request, CancellationToken cancellationToken = default);

    Task MarkPaidAsync(Guid adminId, Guid tenantId, Guid chargeId, ChargeActionRequest request, CancellationToken cancellationToken = default);

    Task VoidAsync(Guid adminId, Guid tenantId, Guid chargeId, ChargeActionRequest request, CancellationToken cancellationToken = default);

    Task CancelAsync(Guid adminId, Guid tenantId, AdminCancelSubscriptionRequest request, CancellationToken cancellationToken = default);

    Task UndoCancelAsync(Guid adminId, Guid tenantId, CancellationToken cancellationToken = default);

    Task ReviewAttemptAsync(Guid adminId, Guid tenantId, Guid attemptId, ReviewAttemptRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BillingReviewItemResponse>> ListReviewItemsAsync(Guid? tenantId, CancellationToken cancellationToken = default);

    Task CloseReviewItemAsync(Guid adminId, Guid itemId, ReviewItemRequest request, CancellationToken cancellationToken = default);
}

public sealed class BillingService : IBillingService
{
    private const int HistoryCharges = 24;

    private readonly IBillingRepository _billing;
    private readonly BillingEngine _engine;
    private readonly IPaymentGateway _gateway;
    private readonly IUserRepository _users;
    private readonly TimeProvider _time;

    public BillingService(IBillingRepository billing, BillingEngine engine, IPaymentGateway gateway, IUserRepository users, TimeProvider time)
    {
        _billing = billing;
        _engine = engine;
        _gateway = gateway;
        _users = users;
        _time = time;
    }

    public async Task<BillingAccountResponse> GetAccountAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var account = await _billing.GetAccountAsync(tenantId, HistoryCharges, cancellationToken) ?? throw new BillingException(BillingProblem.SubscriptionNotFound);
        return BillingViews.Fill(new BillingAccountResponse(), account, _gateway.IsConfigured, _time.GetUtcNow().UtcDateTime);
    }

    public async Task<PaymentMethodSetupResponse> GetPaymentMethodSetupAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (!_gateway.IsConfigured)
        {
            throw new BillingException(BillingProblem.GatewayNotConfigured);
        }

        _ = await _billing.GetAccountAsync(tenantId, 0, cancellationToken) ?? throw new BillingException(BillingProblem.SubscriptionNotFound);
        var acceptance = await _gateway.GetAcceptanceAsync(cancellationToken);
        var browser = _gateway.BrowserSettings;
        return new PaymentMethodSetupResponse
        {
            PublicKey = browser.PublicKey,
            ApiBaseUrl = browser.ApiBaseUrl,
            AcceptanceToken = acceptance.AcceptanceToken,
            AcceptancePermalink = acceptance.AcceptancePermalink,
            PersonalDataToken = acceptance.PersonalDataToken,
            PersonalDataPermalink = acceptance.PersonalDataPermalink,
        };
    }

    public async Task SetPaymentMethodAsync(Guid tenantId, Guid userId, SetPaymentMethodRequest request, CancellationToken cancellationToken = default)
    {
        // The gateway receives the email of whoever registers the method (a user of this company; the tenant filter guarantees it).
        var user = await _users.GetByIdAsync(userId, cancellationToken) ?? throw new BillingException(BillingProblem.SubscriptionNotFound);
        var type = Enum.Parse<PaymentMethodType>(request.Type!, ignoreCase: true);
        await _engine.SetPaymentMethodAsync(
            tenantId, userId,
            new GatewayPaymentSourceRequest(type, request.Token!, user.Email, request.AcceptanceToken!, request.PersonalDataToken!),
            type == PaymentMethodType.Card ? request.CardBrand : null,
            type == PaymentMethodType.Card ? request.CardLastFour : null,
            cancellationToken);
    }

    public async Task<PayNowResponse> PayNowAsync(Guid tenantId, Guid userId, Guid chargeId, CancellationToken cancellationToken = default)
        => new() { Status = await _engine.PayNowAsync(tenantId, chargeId, userId, cancellationToken) };

    public async Task<PaymentLinkResponse> GetPaymentLinkAsync(Guid tenantId, Guid chargeId, CancellationToken cancellationToken = default)
        => new() { Url = (await _engine.GetPaymentLinkAsync(tenantId, chargeId, cancellationToken)).Url };

    public Task SetCancelAtPeriodEndAsync(Guid tenantId, Guid userId, bool cancel, CancellationToken cancellationToken = default)
        => _engine.SetCancelAtPeriodEndAsync(tenantId, cancel, null, userId, null, cancellationToken);
}

public sealed class AdminBillingService : IAdminBillingService
{
    private const int HistoryCharges = 36;

    private readonly IBillingRepository _billing;
    private readonly IBillingUnitOfWork _unitOfWork;
    private readonly BillingEngine _engine;
    private readonly IPaymentGateway _gateway;
    private readonly ITenantRepository _tenants;
    private readonly TimeProvider _time;
    private readonly TenantTimeZone _zone;

    public AdminBillingService(
        IBillingRepository billing, IBillingUnitOfWork unitOfWork, BillingEngine engine, IPaymentGateway gateway, ITenantRepository tenants, TimeProvider time,
        TenantTimeZone zone)
    {
        _billing = billing;
        _unitOfWork = unitOfWork;
        _engine = engine;
        _gateway = gateway;
        _tenants = tenants;
        _time = time;
        _zone = zone;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<PlanResponse>> ListPlansAsync(CancellationToken cancellationToken = default)
    {
        var plans = await _billing.ListPlansAsync(cancellationToken);
        var addOns = await _billing.ListAddOnsAsync(cancellationToken);
        return plans.Select(p => BillingViews.Plan(p, addOns.Where(a => a.PlanId == p.Id))).ToList();
    }

    public async Task<PlanResponse> CreatePlanAsync(Guid adminId, PlanRequest request, CancellationToken cancellationToken = default)
    {
        var plan = BillingPlan.Create(request.Code ?? string.Empty, request.Name!, request.PricePerLinePesos * 100, request.IncludedAiConversations);
        if (!request.IsActive)
        {
            plan.Update(plan.Name, plan.PricePerLineInCents, plan.IncludedAiConversations, isActive: false);
        }

        if (!await _billing.TryAddPlanAsync(plan, PlatformAuditLog.Record(adminId, PlatformAuditLog.Actions.BillingPlanCreated, null, PlanDetails(plan)), cancellationToken))
        {
            throw new BillingException(BillingProblem.DuplicateCode);
        }

        return BillingViews.Plan(plan, []);
    }

    public async Task<PlanResponse> UpdatePlanAsync(Guid adminId, Guid planId, PlanRequest request, CancellationToken cancellationToken = default)
    {
        var plan = await _billing.GetPlanAsync(planId, cancellationToken) ?? throw new BillingException(BillingProblem.PlanNotFound);
        var before = PlanDetails(plan);
        plan.Update(request.Name!, request.PricePerLinePesos * 100, request.IncludedAiConversations, request.IsActive);
        await _billing.SaveCatalogueAsync(PlatformAuditLog.Record(adminId, PlatformAuditLog.Actions.BillingPlanUpdated, null, new Dictionary<string, object?>
        {
            ["planId"] = plan.Id,
            ["from"] = before,
            ["to"] = PlanDetails(plan),
        }), cancellationToken);
        var addOns = await _billing.ListAddOnsAsync(cancellationToken);
        return BillingViews.Plan(plan, addOns.Where(a => a.PlanId == plan.Id));
    }

    public async Task<AddOnResponse> CreateAddOnAsync(Guid adminId, Guid planId, AddOnRequest request, CancellationToken cancellationToken = default)
    {
        _ = await _billing.GetPlanAsync(planId, cancellationToken) ?? throw new BillingException(BillingProblem.PlanNotFound);
        var addOn = BillingAddOn.Create(planId, request.Code ?? string.Empty, request.Name!, request.Conversations, request.PricePesos * 100);
        if (!request.IsActive)
        {
            addOn.Update(addOn.Name, addOn.Conversations, addOn.PriceInCents, isActive: false);
        }

        if (!await _billing.TryAddAddOnAsync(addOn, PlatformAuditLog.Record(adminId, PlatformAuditLog.Actions.BillingAddOnCreated, null, AddOnDetails(addOn)), cancellationToken))
        {
            throw new BillingException(BillingProblem.DuplicateCode);
        }

        return BillingViews.AddOn(addOn);
    }

    public async Task<AddOnResponse> UpdateAddOnAsync(Guid adminId, Guid addOnId, AddOnRequest request, CancellationToken cancellationToken = default)
    {
        var addOn = await _billing.GetAddOnAsync(addOnId, cancellationToken) ?? throw new BillingException(BillingProblem.AddOnNotFound);
        var before = AddOnDetails(addOn);
        addOn.Update(request.Name!, request.Conversations, request.PricePesos * 100, request.IsActive);
        await _billing.SaveCatalogueAsync(PlatformAuditLog.Record(adminId, PlatformAuditLog.Actions.BillingAddOnUpdated, null, new Dictionary<string, object?>
        {
            ["addOnId"] = addOn.Id,
            ["from"] = before,
            ["to"] = AddOnDetails(addOn),
        }), cancellationToken);
        return BillingViews.AddOn(addOn);
    }

    public async Task<AdminBillingAccountResponse?> GetAccountAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var account = await _billing.GetAccountAsync(tenantId, HistoryCharges, cancellationToken);
        return account is null ? null : BillingViews.FillAdmin(account, _gateway.IsConfigured, Now);
    }

    public async Task<AdminBillingAccountResponse> StartAsync(Guid adminId, Guid tenantId, StartSubscriptionRequest request, CancellationToken cancellationToken = default)
    {
        _ = await _tenants.GetByIdAsync(tenantId, cancellationToken) ?? throw new TenantNotFoundException();
        var plan = await _billing.GetPlanAsync(request.PlanId!.Value, cancellationToken) ?? throw new BillingException(BillingProblem.PlanNotFound);
        if (!plan.IsActive)
        {
            throw new BillingException(BillingProblem.PlanInactive);
        }

        var now = Now;
        var subscription = BillingSubscription.Start(tenantId, plan.Id, _zone.LocalDate(now), request.TrialDays, now);
        subscription.SetBilledLinesOverride(request.BilledLinesOverride);
        var audit = PlatformAuditLog.Record(adminId, PlatformAuditLog.Actions.BillingSubscriptionStarted, tenantId, new Dictionary<string, object?>
        {
            ["subscriptionId"] = subscription.Id,
            ["plan"] = plan.Code,
            ["trialDays"] = request.TrialDays,
            ["billedLinesOverride"] = request.BilledLinesOverride,
            ["periodStart"] = subscription.CurrentPeriodStart.ToString("yyyy-MM-dd"),
            ["periodEnd"] = subscription.CurrentPeriodEnd.ToString("yyyy-MM-dd"),
        });
        if (!await _billing.TryAddSubscriptionAsync(subscription, audit, cancellationToken))
        {
            throw new BillingException(BillingProblem.SubscriptionExists);
        }

        // The first run at once, so the first period (and its charge, without a trial) exists when the administrator looks.
        await _engine.RunAsync(tenantId, cancellationToken);
        return (await GetAccountAsync(tenantId, cancellationToken))!;
    }

    public async Task<AdminBillingAccountResponse> ChangeAsync(Guid adminId, Guid tenantId, ChangeSubscriptionRequest request, CancellationToken cancellationToken = default)
    {
        await using (var change = await _unitOfWork.BeginAsync(tenantId, cancellationToken: cancellationToken) ?? throw new BillingException(BillingProblem.SubscriptionNotFound))
        {
            var subscription = change.Subscription;
            if (subscription.IsCancelled)
            {
                throw new BillingException(BillingProblem.SubscriptionCancelled);
            }

            var plan = await change.GetPlanAsync(request.PlanId!.Value, cancellationToken) ?? throw new BillingException(BillingProblem.PlanNotFound);
            if (!plan.IsActive && plan.Id != subscription.PlanId)
            {
                throw new BillingException(BillingProblem.PlanInactive);
            }

            BillingAddOn? addOn = null;
            if (request.AddOnQuantity > 0)
            {
                addOn = request.AddOnId is { } addOnId ? await change.GetAddOnAsync(addOnId, cancellationToken) : null;
                if (addOn is null)
                {
                    throw new BillingException(BillingProblem.AddOnNotFound);
                }

                if (addOn.PlanId != plan.Id)
                {
                    throw new BillingException(BillingProblem.AddOnOfAnotherPlan);
                }

                if (!addOn.IsActive && addOn.Id != subscription.AddOnId)
                {
                    throw new BillingException(BillingProblem.PlanInactive);
                }
            }

            var before = new Dictionary<string, object?>
            {
                ["planId"] = subscription.PlanId,
                ["billedLinesOverride"] = subscription.BilledLinesOverride,
                ["addOnId"] = subscription.AddOnId,
                ["addOnQuantity"] = subscription.AddOnQuantity,
            };
            subscription.ChangePlan(plan.Id);
            subscription.SetBilledLinesOverride(request.BilledLinesOverride);
            subscription.SetAddOn(addOn?.Id, addOn is null ? 0 : request.AddOnQuantity);
            change.Record(PlatformAuditLog.Record(adminId, PlatformAuditLog.Actions.BillingSubscriptionChanged, tenantId, new Dictionary<string, object?>
            {
                ["from"] = before,
                ["to"] = new Dictionary<string, object?>
                {
                    ["planId"] = subscription.PlanId,
                    ["plan"] = plan.Code,
                    ["billedLinesOverride"] = subscription.BilledLinesOverride,
                    ["addOnId"] = subscription.AddOnId,
                    ["addOnQuantity"] = subscription.AddOnQuantity,
                },
                ["effectiveFrom"] = subscription.CurrentPeriodEnd.ToString("yyyy-MM-dd"),
            }));
            await change.CommitAsync(cancellationToken);
        }

        return (await GetAccountAsync(tenantId, cancellationToken))!;
    }

    public Task MarkPaidAsync(Guid adminId, Guid tenantId, Guid chargeId, ChargeActionRequest request, CancellationToken cancellationToken = default)
        => _engine.MarkPaidManuallyAsync(tenantId, chargeId, adminId, request.Reason!, null, cancellationToken);

    public Task VoidAsync(Guid adminId, Guid tenantId, Guid chargeId, ChargeActionRequest request, CancellationToken cancellationToken = default)
        => _engine.VoidChargeAsync(tenantId, chargeId, adminId, request.Reason!, null, cancellationToken);

    public Task CancelAsync(Guid adminId, Guid tenantId, AdminCancelSubscriptionRequest request, CancellationToken cancellationToken = default)
    {
        var code = AdminReasonCodes.Optional(request.ReasonCode) ?? throw new Domain.Exceptions.DomainException(AdminReasonCodes.RuleMessage);
        return request.AtPeriodEnd
            ? _engine.SetCancelAtPeriodEndAsync(tenantId, true, adminId, null, code, cancellationToken)
            : _engine.CancelNowAsync(tenantId, adminId, code, cancellationToken);
    }

    public Task UndoCancelAsync(Guid adminId, Guid tenantId, CancellationToken cancellationToken = default)
        => _engine.SetCancelAtPeriodEndAsync(tenantId, false, adminId, null, null, cancellationToken);

    public Task ReviewAttemptAsync(Guid adminId, Guid tenantId, Guid attemptId, ReviewAttemptRequest request, CancellationToken cancellationToken = default)
        => _engine.ReviewAttemptAsync(tenantId, attemptId, adminId, request.MarkFailed, request.Reason!, cancellationToken);

    public async Task<IReadOnlyList<BillingReviewItemResponse>> ListReviewItemsAsync(Guid? tenantId, CancellationToken cancellationToken = default)
        => (await _billing.ListOpenReviewItemsAsync(tenantId, 200, cancellationToken)).Select(r => new BillingReviewItemResponse
        {
            Id = r.Id,
            TenantId = r.TenantId,
            ChargeId = r.ChargeId,
            AttemptId = r.AttemptId,
            TransactionId = r.TransactionId,
            Reason = r.Reason,
            AmountInCents = r.AmountInCents,
            Currency = r.Currency,
            FlaggedAt = r.FlaggedAt,
        }).ToList();

    public async Task CloseReviewItemAsync(Guid adminId, Guid itemId, ReviewItemRequest request, CancellationToken cancellationToken = default)
    {
        var item = await _billing.GetReviewItemAsync(itemId, cancellationToken) ?? throw new BillingException(BillingProblem.ChargeNotFound);
        if (!item.MarkReviewed(adminId, request.Reason!, Now))
        {
            return;
        }

        await _billing.SaveCatalogueAsync(PlatformAuditLog.Record(adminId, PlatformAuditLog.Actions.BillingPaymentReviewed, item.TenantId, new Dictionary<string, object?>
        {
            ["reviewItemId"] = item.Id,
            ["transactionId"] = item.TransactionId,
            ["why"] = item.Reason,
            ["note"] = item.ReviewNote,
        }), cancellationToken);
    }

    private static Dictionary<string, object?> PlanDetails(BillingPlan plan) => new()
    {
        ["planId"] = plan.Id,
        ["code"] = plan.Code,
        ["name"] = plan.Name,
        ["pricePerLineInCents"] = plan.PricePerLineInCents,
        ["includedAiConversations"] = plan.IncludedAiConversations,
        ["isActive"] = plan.IsActive,
    };

    private static Dictionary<string, object?> AddOnDetails(BillingAddOn addOn) => new()
    {
        ["addOnId"] = addOn.Id,
        ["planId"] = addOn.PlanId,
        ["code"] = addOn.Code,
        ["name"] = addOn.Name,
        ["conversations"] = addOn.Conversations,
        ["priceInCents"] = addOn.PriceInCents,
        ["isActive"] = addOn.IsActive,
    };
}

/// <summary>How the billing rows become the pages' DTOs.</summary>
internal static class BillingViews
{
    public static T Fill<T>(T view, BillingAccountView account, bool gatewayConfigured, DateTime now) where T : BillingAccountResponse
    {
        var s = account.Subscription;
        var lines = s.BilledLines(account.TenantMaxActiveLines);
        var addOnQuantity = account.AddOn is null ? 0 : s.AddOnQuantity;
        var open = account.Charges.Where(c => c.IsOpen).OrderBy(c => c.PeriodStart).FirstOrDefault();

        view.PlanCode = account.Plan.Code;
        view.PlanName = account.Plan.Name;
        view.PricePerLineInCents = account.Plan.PricePerLineInCents;
        view.IncludedAiConversations = account.Plan.IncludedAiConversations;
        view.Status = s.Status;
        view.DunningStage = DunningPolicy.Stage(s.Status, open is not null && s.Status == SubscriptionStatus.PastDue, s.BillingSuspendedAt is not null);
        view.CurrentPeriodStart = s.CurrentPeriodStart;
        view.CurrentPeriodEnd = s.CurrentPeriodEnd;
        view.BilledLines = lines;
        view.AddOnName = account.AddOn?.Name;
        view.AddOnQuantity = addOnQuantity;
        view.AddOnConversations = account.AddOn?.Conversations ?? 0;
        view.NextChargeOn = s.IsCancelled || s.CancelAtPeriodEnd ? null : s.CurrentPeriodEnd;
        view.NextChargeAmountInCents = view.NextChargeOn is null ? null : lines * account.Plan.PricePerLineInCents + addOnQuantity * (account.AddOn?.PriceInCents ?? 0);
        view.CancelAtPeriodEnd = s.CancelAtPeriodEnd;
        view.CancelledAt = s.CancelledAt;
        view.PaymentMethodType = s.PaymentMethodType;
        view.PaymentMethodLabel = s.PaymentMethodLabel;
        view.BillingSuspendedAt = s.BillingSuspendedAt;
        view.GatewayConfigured = gatewayConfigured;
        view.Charges = account.Charges.Select(c => Charge(c, account.Attempts, now)).ToList();
        view.OpenCharge = open is null ? null : view.Charges.First(c => c.Id == open.Id);
        return view;
    }

    public static AdminBillingAccountResponse FillAdmin(BillingAccountView account, bool gatewayConfigured, DateTime now)
    {
        var view = Fill(new AdminBillingAccountResponse(), account, gatewayConfigured, now);
        var s = account.Subscription;
        view.TenantId = s.TenantId;
        view.PlanId = s.PlanId;
        view.AddOnId = s.AddOnId;
        view.BilledLinesOverride = s.BilledLinesOverride;
        view.TenantMaxActiveLines = account.TenantMaxActiveLines;
        view.PaymentMethodUpdatedAt = s.PaymentMethodUpdatedAt;
        view.NextRunAt = s.NextRunAt;
        view.CancellationReasonCode = s.CancellationReasonCode;
        return view;
    }

    private static BillingChargeResponse Charge(BillingCharge c, IReadOnlyList<BillingPaymentAttempt> attempts, DateTime now)
    {
        var own = attempts.Where(a => a.ChargeId == c.Id).OrderByDescending(a => a.AttemptedAt).ToList();
        return new BillingChargeResponse
        {
            Id = c.Id,
            PeriodStart = c.PeriodStart,
            PeriodEnd = c.PeriodEnd,
            DueOn = c.DueOn,
            PlanName = c.PlanName,
            Lines = c.Lines,
            UnitPriceInCents = c.UnitPriceInCents,
            AddOnName = c.AddOnName,
            AddOnQuantity = c.AddOnQuantity,
            AmountInCents = c.AmountInCents,
            Currency = c.Currency,
            Status = c.Status,
            PaidAt = c.PaidAt,
            Settlement = c.Settlement,
            Note = c.Note,
            AutomaticAttempts = c.AutomaticAttempts,
            NextAttemptAt = c.NextAttemptAt,
            PaymentLinkUrl = c.IsOpen && c.HasUsablePaymentLink(now) ? c.PaymentLinkUrl : null,
            HasPendingAttempt = own.Any(a => a.IsPending),
            Attempts = own.Select(a => new BillingAttemptResponse
            {
                Id = a.Id,
                ReviewReason = a.NeedsReview ? a.ReviewReason : null,
                Reference = a.Reference,
                AttemptedAt = a.AttemptedAt,
                Kind = a.Kind,
                Status = a.Status,
                MethodType = a.MethodType,
                StatusReason = a.StatusReason,
                ResolvedAt = a.ResolvedAt,
            }).ToList(),
        };
    }

    public static PlanResponse Plan(BillingPlan plan, IEnumerable<BillingAddOn> addOns) => new()
    {
        Id = plan.Id,
        Code = plan.Code,
        Name = plan.Name,
        PricePerLinePesos = plan.PricePerLineInCents / 100,
        IncludedAiConversations = plan.IncludedAiConversations,
        IsActive = plan.IsActive,
        AddOns = addOns.OrderBy(a => a.Code, StringComparer.Ordinal).Select(AddOn).ToList(),
    };

    public static AddOnResponse AddOn(BillingAddOn addOn) => new()
    {
        Id = addOn.Id,
        PlanId = addOn.PlanId,
        Code = addOn.Code,
        Name = addOn.Name,
        Conversations = addOn.Conversations,
        PricePesos = addOn.PriceInCents / 100,
        IsActive = addOn.IsActive,
    };
}
