namespace Conversia.Web.IntegrationTests.Infrastructure;

internal static class RepoPaths
{
    public static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Conversia.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Conversia.slnx not found above the test output directory.");
    }

    public static string WebProject() => Path.Combine(Root(), "src", "Conversia.Web");
}
