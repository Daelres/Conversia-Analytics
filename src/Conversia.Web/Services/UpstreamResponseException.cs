using System.Net;
using Conversia.Domain.Enums;

namespace Conversia.Web.Services;

/// <summary>An answer of the API that is neither a known outcome nor a usable result; the client reports it as an error of the Web.</summary>
internal sealed class UpstreamResponseException(HttpStatusCode status, ErrorKind kind)
    : Exception($"The API answered {(int)status} and the answer could not be used.")
{
    public HttpStatusCode Status { get; } = status;

    public ErrorKind Kind { get; } = kind;

    /// <summary>502, 503 and 504 mean the API (or what is in front of it) is not there; anything else is an answer it should not give.</summary>
    public static UpstreamResponseException For(HttpStatusCode status)
        => new(status, status is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout
            ? ErrorKind.UpstreamUnavailable
            : ErrorKind.UpstreamBadResponse);
}
