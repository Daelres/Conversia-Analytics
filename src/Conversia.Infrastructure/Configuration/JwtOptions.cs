namespace Conversia.Infrastructure.Configuration;

public class JwtOptions
{
    public const string SectionName = "Jwt";

    public string SigningKey { get; set; } = string.Empty;
    public string Issuer { get; set; } = "Conversia";
    public string Audience { get; set; } = "Conversia";
}
