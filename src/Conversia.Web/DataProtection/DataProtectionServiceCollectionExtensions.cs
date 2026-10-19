using Microsoft.AspNetCore.DataProtection;

namespace Conversia.Web.DataProtection;

public static class DataProtectionServiceCollectionExtensions
{
    public const string KeysPathKey = "DataProtection:KeysPath";
    public const string ApplicationName = "Conversia.Web";

    /// <summary>
    /// Registers ASP.NET Core Data Protection (it protects the sign-in cookie and the antiforgery tokens). The application name
    /// isolates this app's keys from any other app sharing a key store. When <c>DataProtection:KeysPath</c> is configured the key
    /// ring is persisted there, so a container restart does not invalidate cookies and open forms; the directory must exist and
    /// be writable or the Web refuses to start. Unset: the framework default (a per-user folder, in-memory when there is none).
    /// The keys are stored unencrypted: protect the directory (docs/DOCKER.md).
    /// </summary>
    public static IServiceCollection AddWebDataProtection(this IServiceCollection services, IConfiguration configuration)
    {
        var builder = services.AddDataProtection().SetApplicationName(ApplicationName);

        var path = configuration[KeysPathKey];
        if (!string.IsNullOrWhiteSpace(path))
        {
            builder.PersistKeysToFileSystem(EnsureWritableDirectory(path));
        }

        return services;
    }

    private static DirectoryInfo EnsureWritableDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        if (!directory.Exists)
        {
            throw new InvalidOperationException(
                $"{KeysPathKey} points to '{path}', which does not exist. Create the directory (in Docker, mount a volume there) or unset the setting.");
        }

        var probe = Path.Combine(directory.FullName, $".write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"{KeysPathKey} points to '{path}', which the Web cannot write to ({e.GetType().Name}). Fix the permissions of the directory so the application user can write there.", e);
        }

        return directory;
    }
}
