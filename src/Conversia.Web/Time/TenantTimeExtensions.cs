using Conversia.Application.Time;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Conversia.Web.Time;

public static class TenantTimeExtensions
{
    /// <summary>
    /// Reads the same <c>Metrics:TimeZone</c> key as the API (default <c>America/Bogota</c>), so "today" and the hours the pages show are the
    /// tenant's own: the API sends UTC instants and local dates, and both hosts must agree on the zone. A name the machine does not know stops the boot.
    /// </summary>
    public static IServiceCollection AddTenantTimeZone(this IServiceCollection services, IConfiguration configuration)
    {
        var id = configuration["Metrics:TimeZone"];
        id = string.IsNullOrWhiteSpace(id) ? TenantTimeZone.DefaultId : id.Trim();
        TenantTimeZone zone;
        try
        {
            zone = new TenantTimeZone(id);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new InvalidOperationException("Metrics:TimeZone must be an IANA time zone name known to this machine (for example America/Bogota).");
        }

        services.AddSingleton(zone);
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }

    /// <summary>An instant the API sent (UTC) as a wall-clock time in the tenant's zone.</summary>
    public static DateTime ToLocal(this TenantTimeZone zone, DateTime utc)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone.Zone);

    public static DateOnly Today(this TenantTimeZone zone, TimeProvider time) => zone.LocalDate(time.GetUtcNow().UtcDateTime);
}
