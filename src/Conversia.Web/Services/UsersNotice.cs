namespace Conversia.Web.Services;

/// <summary>
/// Outcome of a users-page action, carried across the post/redirect/get as a fixed code so that no
/// posted value is ever echoed back through the query string.
/// </summary>
public static class UsersNotice
{
    public const string Created = "created";
    public const string Granted = "granted";
    public const string Revoked = "revoked";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not-found";
    public const string Invalid = "invalid";
    public const string Unavailable = "unavailable";

    public static string Url(string code) => $"/users?notice={code}";

    /// <summary>The users page after a failed permission change: the notice, and the code of the error behind it when there is one.</summary>
    public static string Url(string code, string? errorCode)
        => errorCode is null ? Url(code) : $"{Url(code)}&{ErrorReporting.ErrorCodeState.QueryKey}={Uri.EscapeDataString(errorCode)}";

    public static string ForPermissionChange(bool grant, ApiResultStatus status) => status switch
    {
        ApiResultStatus.Success => grant ? Granted : Revoked,
        ApiResultStatus.Forbidden => Forbidden,
        ApiResultStatus.NotFound => NotFound,
        ApiResultStatus.ValidationFailed => Invalid,
        _ => Unavailable
    };

    public static (string Text, bool IsError)? Describe(string? code) => code switch
    {
        Created => ("Usuario creado.", false),
        Granted => ("Permiso concedido.", false),
        Revoked => ("Permiso revocado.", false),
        Forbidden => (UiMessages.NotAuthorized, true),
        NotFound => (UiMessages.UserNotFound, true),
        Invalid => (UiMessages.InvalidRequest, true),
        Unavailable => (UiMessages.Unavailable, true),
        _ => null
    };
}
