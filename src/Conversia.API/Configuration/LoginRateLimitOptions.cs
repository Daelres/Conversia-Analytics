namespace Conversia.API.Configuration;

public sealed record LoginRateLimitOptions(int PermitLimit, int WindowSeconds)
{
    public const string SectionName = "RateLimiting:Login";
    public const string PermitLimitKey = SectionName + ":PermitLimit";
    public const string WindowSecondsKey = SectionName + ":WindowSeconds";

    public static LoginRateLimitOptions Default { get; } = new(PermitLimit: 10, WindowSeconds: 60);

    public TimeSpan Window => TimeSpan.FromSeconds(WindowSeconds);
}
