namespace Conversia.Web.Services;

public enum ApiResultStatus
{
    Success,
    ValidationFailed,
    Unauthorized,
    Forbidden,
    NotFound,
    Conflict,
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

    public bool IsSuccess => Status == ApiResultStatus.Success;

    public static ApiResult<T> Success(T value) => new() { Status = ApiResultStatus.Success, Value = value };

    public static ApiResult<T> Failure(ApiResultStatus status) => new() { Status = status };

    public static ApiResult<T> Invalid(IReadOnlyDictionary<string, string[]> errors)
        => new() { Status = ApiResultStatus.ValidationFailed, ValidationErrors = errors };

    /// <param name="errorCode">The code of the unexpected error behind it: the API's own, or one the Web registered.</param>
    public static ApiResult<T> Unavailable(string? errorCode) => new() { Status = ApiResultStatus.Unavailable, ErrorCode = errorCode };

    public static ApiResult<T> RateLimited(int? retryAfterSeconds)
        => new() { Status = ApiResultStatus.RateLimited, RetryAfterSeconds = retryAfterSeconds };
}
