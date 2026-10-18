using System.Text.Json;
using System.Text.RegularExpressions;
using Conversia.Web.IntegrationTests.Infrastructure;

namespace Conversia.Web.IntegrationTests.Configuration;

/// <summary>
/// The one shared secret of error reporting is wired the same way everywhere a deployment is described: the compose files hand
/// <c>ERROR_REPORT_KEY</c> to the API as <c>ErrorLogging__ReportKey</c> and to the Web as <c>ErrorReporting__ApiKey</c> (a reference,
/// never a committed value), <c>.env.example</c> documents it, and no committed settings file carries a key. These tests read the
/// repository's files; they start nothing.
/// </summary>
public class ErrorReportingDeploymentFilesTests
{
    private const string Variable = "ERROR_REPORT_KEY";

    private static string Read(params string[] path) => File.ReadAllText(Path.Combine([RepoPaths.Root(), .. path]));

    /// <summary>The lines of one service of a compose file (the 2-space-indented key under <c>services:</c>) up to the next service.</summary>
    private static IReadOnlyList<string> ServiceLines(string compose, string service)
    {
        var lines = compose.ReplaceLineEndings("\n").Split('\n');
        var start = Array.FindIndex(lines, l => l.TrimEnd() == $"  {service}:");
        Assert.True(start >= 0, $"The compose file has no service '{service}'.");
        var block = new List<string>();
        for (var i = start + 1; i < lines.Length; i++)
        {
            if (Regex.IsMatch(lines[i], @"^  \S") || Regex.IsMatch(lines[i], @"^\S"))
            {
                break;
            }

            block.Add(lines[i]);
        }

        return block;
    }

    /// <summary>
    /// The value an <c>environment:</c> entry gives <paramref name="name"/>, in either of the two forms compose accepts:
    /// the map (<c>NAME: value</c>) and the list (<c>- NAME=value</c>, the whole item possibly quoted).
    /// </summary>
    private static string? EnvironmentValue(IEnumerable<string> serviceLines, string name)
    {
        var escaped = Regex.Escape(name);
        var forms = new[]
        {
            new Regex($@"^\s+{escaped}:\s*(?<value>.+?)\s*$"),
            new Regex($@"^\s+-\s*[""']?{escaped}=(?<value>.*?)[""']?\s*$"),
        };
        foreach (var line in serviceLines.Where(l => !l.TrimStart().StartsWith('#')))
        {
            foreach (var form in forms)
            {
                var match = form.Match(line);
                if (match.Success)
                {
                    return match.Groups["value"].Value.Trim().Trim('"', '\'');
                }
            }
        }

        return null;
    }

    /// <summary>A reference to the variable, with no default or an empty one: nothing a person could mistake for a real key is committed.</summary>
    private static readonly Regex KeyReference = new(@"^\$\{" + Variable + @"(:?-|:?\?[^}]*)?\}$");

    [Fact]
    public void The_compose_file_hands_the_shared_key_to_the_api_as_ErrorLogging_ReportKey()
    {
        var compose = Read("docker-compose.yml");

        var value = EnvironmentValue(ServiceLines(compose, "api"), "ErrorLogging__ReportKey");

        Assert.True(value is not null, "The api service of docker-compose.yml does not set ErrorLogging__ReportKey.");
        Assert.Matches(KeyReference, value!);
    }

    [Fact]
    public void The_compose_file_hands_the_same_key_to_the_web_as_ErrorReporting_ApiKey()
    {
        var compose = Read("docker-compose.yml");

        var value = EnvironmentValue(ServiceLines(compose, "web"), "ErrorReporting__ApiKey");

        Assert.True(value is not null, "The web service of docker-compose.yml does not set ErrorReporting__ApiKey.");
        Assert.Matches(KeyReference, value!);
    }

    [Fact]
    public void The_api_and_the_web_read_one_and_the_same_variable()
    {
        var compose = Read("docker-compose.yml");

        var api = EnvironmentValue(ServiceLines(compose, "api"), "ErrorLogging__ReportKey");
        var web = EnvironmentValue(ServiceLines(compose, "web"), "ErrorReporting__ApiKey");

        Assert.NotNull(api);
        Assert.Equal(api, web);
    }

    [Fact]
    public void The_demo_overlay_never_puts_a_literal_key_in_either_service()
    {
        var overlay = Read("docker-compose.demo.yml");

        foreach (var (service, setting) in new[] { ("api", "ErrorLogging__ReportKey"), ("web", "ErrorReporting__ApiKey") })
        {
            var value = EnvironmentValue(ServiceLines(overlay, service), setting);
            Assert.True(value is null || KeyReference.IsMatch(value), $"The demo overlay sets {setting} to something other than a reference to {Variable}.");
        }
    }

    [Fact]
    public void The_example_environment_file_documents_the_key_and_its_minimum_length()
    {
        var example = Read(".env.example");

        var lines = example.ReplaceLineEndings("\n").Split('\n');
        var assignment = Array.FindIndex(lines, l => Regex.IsMatch(l, $@"^(#\s*)?{Variable}="));

        Assert.True(assignment >= 0, $".env.example has no {Variable}= line (commented or not).");
        var comments = lines[Math.Max(0, assignment - 5)..assignment].Where(l => l.TrimStart().StartsWith('#'));
        Assert.True(comments.Any(l => l.Contains("32")), $"None of the comment lines just above {Variable} mentions the minimum length of 32.");
    }

    [Fact]
    public void Copying_the_example_environment_file_as_it_is_never_yields_a_key_the_web_would_refuse()
    {
        var example = Read(".env.example");

        var assignment = Regex.Match(example, $@"^(?<comment>#\s*)?{Variable}=(?<value>.*)$", RegexOptions.Multiline);

        Assert.True(assignment.Success);
        if (!assignment.Groups["comment"].Success)
        {
            var value = assignment.Groups["value"].Value.Trim();
            Assert.True(value.Length == 0 || value.Length >= 32, $"{Variable} in .env.example is {value.Length} characters long: both the API and the Web refuse a key shorter than 32.");
        }
    }

    [Theory]
    [InlineData("src", "Conversia.Web", "appsettings.json", "ErrorReporting", "ApiKey")]
    [InlineData("src", "Conversia.Web", "appsettings.Development.json", "ErrorReporting", "ApiKey")]
    [InlineData("src", "Conversia.API", "appsettings.json", "ErrorLogging", "ReportKey")]
    [InlineData("src", "Conversia.API", "appsettings.Development.json", "ErrorLogging", "ReportKey")]
    public void No_committed_settings_file_carries_a_key(string a, string b, string file, string section, string key)
    {
        using var document = JsonDocument.Parse(Read(a, b, file));

        var present = document.RootElement.TryGetProperty(section, out var node) && node.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

        Assert.True(string.IsNullOrEmpty(present), $"{file} of {b} commits {section}:{key}; the key belongs in an environment variable or user secrets.");
    }

    [Fact]
    public void The_error_documentation_describes_the_web_settings_and_the_shared_variable()
    {
        var docs = Read("docs", "ERRORS.md");

        foreach (var expected in new[] { "ErrorReporting:Enabled", "ErrorReporting:ApiKey", "ErrorReporting:TimeoutSeconds", "ErrorReporting:MaxPerFingerprintPerMinute", Variable, "X-Error-Report-Key" })
        {
            Assert.Contains(expected, docs);
        }
    }
}
