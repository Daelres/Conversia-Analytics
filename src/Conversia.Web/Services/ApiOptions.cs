namespace Conversia.Web.Services;

public sealed class ApiOptions
{
    public const string SectionName = "Api";

    public string BaseUrl { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 10;

    public Uri BaseAddress => new(BaseUrl.EndsWith('/') ? BaseUrl : BaseUrl + "/", UriKind.Absolute);

    public static bool IsValid(ApiOptions options)
        => Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri)
           && uri.Scheme is "http" or "https"
           && options.TimeoutSeconds is >= 1 and <= 300;
}
