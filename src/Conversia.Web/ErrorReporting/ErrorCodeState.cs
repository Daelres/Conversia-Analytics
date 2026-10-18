using Conversia.Domain.Common;

namespace Conversia.Web.ErrorReporting;

/// <summary>
/// The code of the unexpected error that the current request ran into, for the shared layout to show. One per request (scoped); the
/// first code wins, so a page whose several API calls fail shows one. The code comes from a failed API call, from the Web's exception
/// handler (which hands it over in <c>HttpContext.Items</c>, across the scope the error page gets) or from the redirect of a
/// permission change that failed (<see cref="QueryKey"/>).
/// </summary>
public sealed class ErrorCodeState
{
    public const string ItemKey = "Conversia.Web.ErrorCode";

    /// <summary>The query parameter that carries a code across the redirect of a failed permission change.</summary>
    public const string QueryKey = "errorId";

    private string? _code;

    public ErrorCodeState(IHttpContextAccessor httpContextAccessor)
    {
        _code = Seed(httpContextAccessor.HttpContext);
    }

    public string? Code => Volatile.Read(ref _code);

    /// <summary>Raised (possibly from another thread) when a code is recorded after the state was created.</summary>
    public event Action? Changed;

    /// <summary>Keeps <paramref name="code"/> unless it is malformed or the request already has one.</summary>
    public void Record(string? code)
    {
        if (ErrorCode.IsValid(code) && Interlocked.CompareExchange(ref _code, code, null) is null)
        {
            Changed?.Invoke();
        }
    }

    private static string? Seed(HttpContext? context)
    {
        if (context is null)
        {
            return null;
        }

        var code = context.Items.TryGetValue(ItemKey, out var item) ? item as string : null;
        code ??= FromRedirect(context.Request);
        return ErrorCode.IsValid(code) ? code : null;
    }

    private static string? FromRedirect(HttpRequest request)
        => request.Query["notice"] == Services.UsersNotice.Unavailable ? request.Query[QueryKey].ToString() : null;
}
