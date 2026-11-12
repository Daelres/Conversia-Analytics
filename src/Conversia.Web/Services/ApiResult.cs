namespace Conversia.Web.Services;

public enum ApiResultStatus
{
    Success,
    ValidationFailed,
    Unauthorized,
    Forbidden,

    /// <summary>403 with the code <c>tenant_not_active</c>: the company is pending or suspended, so it cannot operate yet. Not an error.</summary>
    TenantNotActive,
    NotFound,
    Conflict,

    /// <summary>422 with a machine code: the request was well formed but could not be carried out (the provider refused a message).</summary>
    Unprocessable,
    RateLimited,
    Unavailable
}

/// <summary>Placeholder payload for API calls that answer 204 No Content.</summary>
public readonly record struct Unit;

public sealed class ApiResult<T> : IApiOutcome
{
    private static readonly IReadOnlyDictionary<string, string[]> NoErrors = new Dictionary<string, string[]>();

    public ApiResultStatus Status { get; init; }

    public T? Value { get; init; }

    public IReadOnlyDictionary<string, string[]> ValidationErrors { get; init; } = NoErrors;

    public int? RetryAfterSeconds { get; init; }

    public string? ErrorCode { get; init; }

    /// <summary>The API's own Spanish message of a 409 (for example the line quota that is full). Null for every other outcome.</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// The stable machine <c>code</c> of the problem the API answered with (<c>provider_limit_reached</c>...), when it carried one that the
    /// call declared as expected. It tells apart refusals that share a status; a page maps it to its own wording and never shows it.
    /// </summary>
    public string? ProblemCode { get; init; }

    /// <summary>The status the API reported with a <see cref="ApiResultStatus.TenantNotActive"/> outcome ("Pending" or "Suspended"), when it did.</summary>
    public string? TenantStatus { get; init; }

    public bool IsSuccess => Status == ApiResultStatus.Success;

    public static ApiResult<T> Success(T value) => new() { Status = ApiResultStatus.Success, Value = value };

    public static ApiResult<T> Failure(ApiResultStatus status) => new() { Status = status };

    public static ApiResult<T> Conflict(string? detail, string? problemCode = null) => new() { Status = ApiResultStatus.Conflict, Detail = detail, ProblemCode = problemCode };

    /// <summary>A refusal the API worded itself and named with a code: an expected answer, so it carries no error code to report.</summary>
    public static ApiResult<T> Problem(ApiResultStatus status, string problemCode) => new() { Status = status, ProblemCode = problemCode };

    public static ApiResult<T> TenantNotActive(string? tenantStatus) => new() { Status = ApiResultStatus.TenantNotActive, TenantStatus = tenantStatus };

    public static ApiResult<T> Invalid(IReadOnlyDictionary<string, string[]> errors)
        => new() { Status = ApiResultStatus.ValidationFailed, ValidationErrors = errors };

    /// <param name="errorCode">The code of the unexpected error behind it: the API's own, or one the Web registered.</param>
    public static ApiResult<T> Unavailable(string? errorCode, string? problemCode = null) => new() { Status = ApiResultStatus.Unavailable, ErrorCode = errorCode, ProblemCode = problemCode };

    public static ApiResult<T> RateLimited(int? retryAfterSeconds, string? problemCode = null)
        => new() { Status = ApiResultStatus.RateLimited, RetryAfterSeconds = retryAfterSeconds, ProblemCode = problemCode };
}
