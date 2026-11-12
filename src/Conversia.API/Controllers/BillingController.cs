using Conversia.API.RateLimiting;
using System.Security.Cryptography;
using Conversia.API.Configuration;
using Conversia.API.Localization;
using Conversia.API.Middleware;
using Conversia.Application.Billing;
using Conversia.Application.DTOs.Billing;
using Conversia.Application.Services;
using Conversia.Domain.Entities;
using Conversia.Domain.Enums;
using Conversia.Domain.Interfaces;
using Conversia.Infrastructure.Administration;
using Conversia.Infrastructure.Billing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Conversia.API.Controllers;

/// <summary>
/// The company's own subscription (docs/BILLING.md, "The company's billing page"). Owner or <see cref="Permission.BillingManage"/>. Open to a
/// company that is not Active (a company suspended for billing must be able to pay); the company always comes from the token.
/// </summary>
[ApiController]
[Route("api/billing")]
[Authorize(Policy = nameof(Permission.BillingManage))]
[AllowInactiveTenant]
public class BillingController : ControllerBase
{
    private readonly IBillingService _billing;

    public BillingController(IBillingService billing)
    {
        _billing = billing;
    }

    [HttpGet]
    public async Task<ActionResult<BillingAccountResponse>> Get(CancellationToken cancellationToken)
        => Ok(await _billing.GetAccountAsync(User.GetTenantId(), cancellationToken));

    /// <summary>The public key, the API URL and the terms the browser needs to tokenize a card or Nequi itself.</summary>
    [HttpGet("payment-method/setup")]
    public async Task<ActionResult<PaymentMethodSetupResponse>> Setup(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await _billing.GetPaymentMethodSetupAsync(User.GetTenantId(), cancellationToken));
    }

    [HttpPut("payment-method")]
    public async Task<IActionResult> SetPaymentMethod(SetPaymentMethodRequest request, CancellationToken cancellationToken)
    {
        await _billing.SetPaymentMethodAsync(User.GetTenantId(), User.GetUserId()!.Value, request, cancellationToken);
        return NoContent();
    }

    [HttpPost("charges/{chargeId:guid}/pay")]
    public async Task<ActionResult<PayNowResponse>> PayNow(Guid chargeId, CancellationToken cancellationToken)
        => Ok(await _billing.PayNowAsync(User.GetTenantId(), User.GetUserId()!.Value, chargeId, cancellationToken));

    [HttpPost("charges/{chargeId:guid}/payment-link")]
    public async Task<ActionResult<PaymentLinkResponse>> PaymentLink(Guid chargeId, CancellationToken cancellationToken)
        => Ok(await _billing.GetPaymentLinkAsync(User.GetTenantId(), chargeId, cancellationToken));

    [HttpPost("cancel")]
    public async Task<IActionResult> Cancel(CancellationToken cancellationToken)
    {
        await _billing.SetCancelAtPeriodEndAsync(User.GetTenantId(), User.GetUserId()!.Value, true, cancellationToken);
        return NoContent();
    }

    [HttpPost("cancel/undo")]
    public async Task<IActionResult> UndoCancel(CancellationToken cancellationToken)
    {
        await _billing.SetCancelAtPeriodEndAsync(User.GetTenantId(), User.GetUserId()!.Value, false, cancellationToken);
        return NoContent();
    }
}

/// <summary>
/// The gateway's events (<c>POST /api/billing/wompi/events</c>): anonymous, body at most 64 KB, signature-verified with the events secret
/// (body checksum and <c>X-Event-Checksum</c>), environment and signed timestamp checked (no older than <c>Billing:Events:MaxAgeHours</c>,
/// no more than 10 minutes ahead), stored once by its verified checksum and processed by the billing job, which re-reads the transaction.
/// A forged, altered or stale event gets 401 and counts against its address (429 once over budget); verified ones never do.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/billing/wompi/events")]
public class BillingEventsController : ControllerBase
{
    public const string ChecksumHeader = "X-Event-Checksum";

    private readonly IPaymentGateway _gateway;
    private readonly IBillingRepository _billing;
    private readonly BillingJobSignal _signal;
    private readonly TimeProvider _time;
    private readonly BillingEventsRateLimiter _limiter;
    private readonly BillingEventsOptions _options;

    public BillingEventsController(
        IPaymentGateway gateway, IBillingRepository billing, BillingJobSignal signal, TimeProvider time, BillingEventsRateLimiter limiter, BillingEventsOptions options)
    {
        _gateway = gateway;
        _billing = billing;
        _signal = signal;
        _time = time;
        _limiter = limiter;
        _options = options;
    }

    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        if (!_gateway.IsConfigured)
        {
            return NotFound();
        }

        var body = await ReadBodyAsync(cancellationToken);
        if (body is null)
        {
            return Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: ProblemTexts.PayloadTooLargeTitle, detail: ProblemTexts.PayloadTooLargeDetail);
        }

        var now = _time.GetUtcNow();
        var verified = _gateway.VerifyEvent(body, Request.Headers[ChecksumHeader].FirstOrDefault());
        if (verified is null || verified.SignedAt < now - _options.MaxAge || verified.SignedAt > now + BillingEventsOptions.FutureSkew)
        {
            if (_limiter.RegisterFailure(RateLimiting.ClientPartition.Of(HttpContext)))
            {
                Response.Headers.RetryAfter = "60";
                return Problem(statusCode: StatusCodes.Status429TooManyRequests, title: ProblemTexts.TooManyRequestsTitle, detail: ProblemTexts.BillingEventRejectedDetail);
            }

            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: ProblemTexts.BillingEventRejectedTitle, detail: ProblemTexts.BillingEventRejectedDetail);
        }

        if (!_limiter.TryAcceptVerified())
        {
            // The gateway retries what we do not answer with 200.
            Response.Headers.RetryAfter = "60";
            return StatusCode(StatusCodes.Status429TooManyRequests);
        }

        // Keyed by the verified signature: the same event re-sent (even re-serialized) stores nothing new.
        var key = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(verified.EventType + "|" + verified.Checksum));
        var stored = BillingEvent.Receive(key, verified.EventType, verified.TransactionId, verified.PaymentLinkId, now.UtcDateTime);
        if (await _billing.TryAddEventAsync(stored, cancellationToken))
        {
            _signal.Notify();
        }

        return Ok();
    }

    private async Task<byte[]?> ReadBodyAsync(CancellationToken cancellationToken)
    {
        if (Request.ContentLength > WompiGateway.MaxEventBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await Request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > WompiGateway.MaxEventBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}

/// <summary>The platform administrator's billing (docs/BILLING.md, "Administration"): the plan catalogue and each company's subscription.</summary>
[ApiController]
[Route("api/admin")]
[Authorize(Policy = PlatformAdminTokens.PolicyName)]
public class AdminBillingController : ControllerBase
{
    private readonly IAdminBillingService _billing;

    public AdminBillingController(IAdminBillingService billing)
    {
        _billing = billing;
    }

    private Guid AdminId => User.GetUserId()!.Value;

    [HttpGet("billing/plans")]
    public async Task<ActionResult<IReadOnlyList<PlanResponse>>> ListPlans(CancellationToken cancellationToken)
        => Ok(await _billing.ListPlansAsync(cancellationToken));

    [HttpPost("billing/plans")]
    public async Task<ActionResult<PlanResponse>> CreatePlan(PlanRequest request, CancellationToken cancellationToken)
        => StatusCode(StatusCodes.Status201Created, await _billing.CreatePlanAsync(AdminId, request, cancellationToken));

    [HttpPut("billing/plans/{id:guid}")]
    public async Task<ActionResult<PlanResponse>> UpdatePlan(Guid id, PlanRequest request, CancellationToken cancellationToken)
        => Ok(await _billing.UpdatePlanAsync(AdminId, id, request, cancellationToken));

    [HttpPost("billing/plans/{id:guid}/add-ons")]
    public async Task<ActionResult<AddOnResponse>> CreateAddOn(Guid id, AddOnRequest request, CancellationToken cancellationToken)
        => StatusCode(StatusCodes.Status201Created, await _billing.CreateAddOnAsync(AdminId, id, request, cancellationToken));

    [HttpPut("billing/add-ons/{id:guid}")]
    public async Task<ActionResult<AddOnResponse>> UpdateAddOn(Guid id, AddOnRequest request, CancellationToken cancellationToken)
        => Ok(await _billing.UpdateAddOnAsync(AdminId, id, request, cancellationToken));

    [HttpGet("tenants/{tenantId:guid}/billing")]
    public async Task<ActionResult<AdminBillingAccountResponse>> GetAccount(Guid tenantId, CancellationToken cancellationToken)
        => await _billing.GetAccountAsync(tenantId, cancellationToken) is { } account
            ? Ok(account)
            : throw new Application.Exceptions.BillingException(Application.Exceptions.BillingProblem.SubscriptionNotFound);

    [HttpPost("tenants/{tenantId:guid}/billing/start")]
    public async Task<ActionResult<AdminBillingAccountResponse>> Start(Guid tenantId, StartSubscriptionRequest request, CancellationToken cancellationToken)
        => Ok(await _billing.StartAsync(AdminId, tenantId, request, cancellationToken));

    [HttpPut("tenants/{tenantId:guid}/billing")]
    public async Task<ActionResult<AdminBillingAccountResponse>> Change(Guid tenantId, ChangeSubscriptionRequest request, CancellationToken cancellationToken)
        => Ok(await _billing.ChangeAsync(AdminId, tenantId, request, cancellationToken));

    [HttpPost("tenants/{tenantId:guid}/billing/charges/{chargeId:guid}/mark-paid")]
    public async Task<IActionResult> MarkPaid(Guid tenantId, Guid chargeId, ChargeActionRequest request, CancellationToken cancellationToken)
    {
        await _billing.MarkPaidAsync(AdminId, tenantId, chargeId, request, cancellationToken);
        return NoContent();
    }

    [HttpPost("tenants/{tenantId:guid}/billing/charges/{chargeId:guid}/void")]
    public async Task<IActionResult> Void(Guid tenantId, Guid chargeId, ChargeActionRequest request, CancellationToken cancellationToken)
    {
        await _billing.VoidAsync(AdminId, tenantId, chargeId, request, cancellationToken);
        return NoContent();
    }

    [HttpPost("tenants/{tenantId:guid}/billing/cancel")]
    public async Task<IActionResult> Cancel(Guid tenantId, AdminCancelSubscriptionRequest request, CancellationToken cancellationToken)
    {
        await _billing.CancelAsync(AdminId, tenantId, request, cancellationToken);
        return NoContent();
    }

    /// <summary>Open review items: money a person must look at (optionally of one company).</summary>
    [HttpGet("billing/review-items")]
    public async Task<ActionResult<IReadOnlyList<BillingReviewItemResponse>>> ReviewItems([FromQuery] Guid? tenantId, CancellationToken cancellationToken)
        => Ok(await _billing.ListReviewItemsAsync(tenantId, cancellationToken));

    [HttpPost("billing/review-items/{id:guid}/review")]
    public async Task<IActionResult> CloseReviewItem(Guid id, ReviewItemRequest request, CancellationToken cancellationToken)
    {
        await _billing.CloseReviewItemAsync(AdminId, id, request, cancellationToken);
        return NoContent();
    }

    [HttpPost("tenants/{tenantId:guid}/billing/attempts/{attemptId:guid}/review")]
    public async Task<IActionResult> ReviewAttempt(Guid tenantId, Guid attemptId, ReviewAttemptRequest request, CancellationToken cancellationToken)
    {
        await _billing.ReviewAttemptAsync(AdminId, tenantId, attemptId, request, cancellationToken);
        return NoContent();
    }

    [HttpPost("tenants/{tenantId:guid}/billing/cancel/undo")]
    public async Task<IActionResult> UndoCancel(Guid tenantId, CancellationToken cancellationToken)
    {
        await _billing.UndoCancelAsync(AdminId, tenantId, cancellationToken);
        return NoContent();
    }
}
