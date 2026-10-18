using System.Reflection;
using System.Xml.Linq;

namespace Conversia.Web.IntegrationTests.Security;

/// <summary>
/// docs/ARCHITECTURE.md: Web -> Application only. The Web talks to the API over HTTP, never
/// references the Infrastructure (or API) project and never opens a database connection.
/// What is asserted: the Web project's ProjectReferences contain only Application, and its
/// compiled assembly references neither Infrastructure nor the API nor a data-access assembly.
/// The Domain assembly is not forbidden: the Domain <c>Permission</c> enum reaches the Web
/// transitively through Application DTOs, which is allowed.
/// </summary>
public class ArchitectureGuardTests
{
    private static readonly Assembly WebAssembly = typeof(Conversia.Web.Components.App).Assembly;

    [Fact]
    public void Web_project_references_only_the_Application_project()
    {
        var references = WebProject().Descendants("ProjectReference")
            .Select(r => Path.GetFileName(((string)r.Attribute("Include")!).Replace('\\', '/')))
            .ToList();

        Assert.Equal(new[] { "Conversia.Application.csproj" }, references);
    }

    [Fact]
    public void Web_project_has_no_database_package_references()
    {
        var packages = WebProject().Descendants("PackageReference")
            .Select(r => (string)r.Attribute("Include")!)
            .ToList();

        Assert.DoesNotContain(packages, p =>
            p.Contains("EntityFramework", StringComparison.OrdinalIgnoreCase)
            || p.Contains("SqlClient", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Npgsql", StringComparison.OrdinalIgnoreCase)
            || p.Contains("Dapper", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Web_assembly_does_not_reference_Infrastructure_or_data_access_assemblies()
    {
        var referenced = WebAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.DoesNotContain("Conversia.Infrastructure", referenced);
        Assert.DoesNotContain("Conversia.API", referenced);
        Assert.DoesNotContain(referenced, name =>
            name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.Data.SqlClient", StringComparison.Ordinal)
            || name.StartsWith("System.Data.SqlClient", StringComparison.Ordinal)
            || name.StartsWith("Npgsql", StringComparison.Ordinal));
    }

    [Fact]
    public void Web_configuration_declares_no_connection_string()
    {
        var projectDirectory = Path.Combine(RepositoryRoot(), "src", "Conversia.Web");
        var configFiles = Directory.GetFiles(projectDirectory, "appsettings*.json");

        Assert.NotEmpty(configFiles);
        foreach (var file in configFiles)
        {
            Assert.DoesNotContain("ConnectionStrings", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
        }
    }

    private static XDocument WebProject()
        => XDocument.Load(Path.Combine(RepositoryRoot(), "src", "Conversia.Web", "Conversia.Web.csproj"));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Conversia.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Conversia.slnx not found above the test output directory.");
    }
}
