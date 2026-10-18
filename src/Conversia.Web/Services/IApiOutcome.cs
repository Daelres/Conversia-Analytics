namespace Conversia.Web.Services;

/// <summary>The part of an <see cref="ApiResult{T}"/> that does not depend on its payload, so pages can judge several calls together.</summary>
public interface IApiOutcome
{
    ApiResultStatus Status { get; }

    int? RetryAfterSeconds { get; }

    /// <summary>The code of an unexpected failure (<see cref="ApiResultStatus.Unavailable"/> only), else null.</summary>
    string? ErrorCode { get; }
}
