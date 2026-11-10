namespace Conversia.Web.Services;

public sealed class ApiOptions
{
    public const string SectionName = "Api";

    public string BaseUrl { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// The limit of sending a WhatsApp message, which is longer than any other call: the API first calls the provider
    /// (<c>Dialog360:TimeoutSeconds</c>) and then records the result (<c>Messaging:RecordTimeoutSeconds</c>). It must outlast both
    /// together, or the Web gives up on a message the API is still sending and can only say that the outcome is unknown.
    /// </summary>
    public int SendTimeoutSeconds { get; set; } = 60;

    public Uri BaseAddress => new(BaseUrl.EndsWith('/') ? BaseUrl : BaseUrl + "/", UriKind.Absolute);

    public static bool IsValid(ApiOptions options)
        => Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri)
           && uri.Scheme is "http" or "https"
           && options.TimeoutSeconds is >= 1 and <= 300
           && options.SendTimeoutSeconds is >= 1 and <= 600;
}
