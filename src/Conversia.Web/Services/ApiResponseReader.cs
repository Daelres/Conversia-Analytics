using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Conversia.Domain.Common;

namespace Conversia.Web.Services;

/// <summary>
/// Maps one API response onto an <see cref="ApiResult{T}"/>. A 500 that carries the API's own error code is Unavailable with that code;
/// any other answer it cannot make sense of throws <see cref="UpstreamResponseException"/>, which the client reports.
/// </summary>
internal static class ApiResponseReader
{
    private const string ErrorIdHeader = "X-Error-Id";
    private const string ErrorIdProperty = "errorId";

    public static async Task<ApiResult<T>> ReadAsync<T>(
        HttpResponseMessage response, JsonSerializerOptions json, Func<T, bool>? isUsable, CancellationToken cancellationToken)
    {
        var status = response.StatusCode;
        if ((int)status is >= 200 and < 300)
        {
            return await ReadSuccessAsync(response, json, isUsable, cancellationToken);
        }

        return status switch
        {
            HttpStatusCode.BadRequest => await ReadValidationAsync<T>(response, cancellationToken),
            HttpStatusCode.Unauthorized => ApiResult<T>.Failure(ApiResultStatus.Unauthorized),
            HttpStatusCode.Forbidden => await ReadForbiddenAsync<T>(response, cancellationToken),
            HttpStatusCode.NotFound => ApiResult<T>.Failure(ApiResultStatus.NotFound),
            HttpStatusCode.Conflict => ApiResult<T>.Conflict(ProblemDetailsErrors.TryReadDetail(await ReadBodyAsync(response, cancellationToken))),
            HttpStatusCode.TooManyRequests => ApiResult<T>.RateLimited(ReadRetryAfterSeconds(response)),
            HttpStatusCode.InternalServerError => await ReadServerErrorAsync<T>(response, cancellationToken),
            _ => throw UpstreamResponseException.For(status)
        };
    }

    private static async Task<ApiResult<T>> ReadSuccessAsync<T>(
        HttpResponseMessage response, JsonSerializerOptions json, Func<T, bool>? isUsable, CancellationToken cancellationToken)
    {
        if (typeof(T) == typeof(Unit))
        {
            return ApiResult<T>.Success(default!);
        }

        var value = await response.Content.ReadFromJsonAsync<T>(json, cancellationToken);
        return value is not null && (isUsable?.Invoke(value) ?? true)
            ? ApiResult<T>.Success(value)
            : throw UpstreamResponseException.For(response.StatusCode);
    }

    // A 403 is either "you may not do that" or the expected state of a company that cannot operate yet: the stable code tells them apart.
    private static async Task<ApiResult<T>> ReadForbiddenAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var problem = ProblemDetailsErrors.TryReadCode(await ReadBodyAsync(response, cancellationToken));
        return problem is { Code: ProblemDetailsErrors.TenantNotActiveCode }
            ? ApiResult<T>.TenantNotActive(problem.Value.TenantStatus)
            : ApiResult<T>.Failure(ApiResultStatus.Forbidden);
    }

    // A body that cannot be read is no reason to fail: the status alone already says what happened.
    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static async Task<ApiResult<T>> ReadValidationAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var errors = ProblemDetailsErrors.TryParse(body);
        return errors is null ? throw UpstreamResponseException.For(response.StatusCode) : ApiResult<T>.Invalid(errors);
    }

    private static async Task<ApiResult<T>> ReadServerErrorAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var code = ErrorIdOf(response) ?? ErrorIdOf(await response.Content.ReadAsStringAsync(cancellationToken));
        return code is null ? throw UpstreamResponseException.For(response.StatusCode) : ApiResult<T>.Unavailable(code);
    }

    // Only a well-formed code is ever carried: the id is shown to a person and sent to the log, so it is never taken on trust.
    private static string? ErrorIdOf(HttpResponseMessage response)
        => response.Headers.TryGetValues(ErrorIdHeader, out var values) && values.FirstOrDefault() is { } id && ErrorCode.IsValid(id) ? id : null;

    private static string? ErrorIdOf(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement is { ValueKind: JsonValueKind.Object } root
                && root.TryGetProperty(ErrorIdProperty, out var id)
                && id.ValueKind == JsonValueKind.String
                && id.GetString() is { } text && ErrorCode.IsValid(text)
                    ? text
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? ReadRetryAfterSeconds(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return (int)Math.Clamp(Math.Ceiling(delta.TotalSeconds), 1, int.MaxValue);
        }

        if (retryAfter?.Date is { } date)
        {
            return (int)Math.Clamp(Math.Ceiling((date - DateTimeOffset.UtcNow).TotalSeconds), 1, int.MaxValue);
        }

        return null;
    }
}
