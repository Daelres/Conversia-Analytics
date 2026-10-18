using System.Text.Json;
using System.Text.Json.Nodes;

namespace Conversia.Application.UnitTests.Classification;

/// <summary>The default rules file as it is on disk, for the tests that audit or reshuffle its content.</summary>
internal static class RulesFile
{
    public static string Path
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "Conversia.slnx")))
            {
                directory = directory.Parent;
            }

            return System.IO.Path.Combine(directory!.FullName, "src", "Conversia.Application", "Classification", "Rules", "ClassificationRules.es-CO.json");
        }
    }

    public static JsonNode Read() => JsonNode.Parse(
        File.ReadAllText(Path),
        documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!;

    /// <summary>Every phrase of the file, as written (anchors and wildcards included), with where it lives.</summary>
    public static IEnumerable<(string Path, string Phrase)> Phrases()
    {
        static IEnumerable<(string, string)> Walk(string path, JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (key, value) in obj)
                    {
                        if (key is "aliases" or "locale" or "vertical" or "code")
                        {
                            continue; // abbreviations, labels and codes: not phrases
                        }

                        foreach (var item in Walk(path.Length == 0 ? key : path + "." + key, value))
                        {
                            yield return item;
                        }
                    }

                    break;
                case JsonArray array:
                    foreach (var item in array)
                    {
                        foreach (var inner in Walk(path, item))
                        {
                            yield return inner;
                        }
                    }

                    break;
                case JsonValue value when value.TryGetValue<string>(out var text):
                    yield return (path, text);
                    break;
            }
        }

        return Walk(string.Empty, Read());
    }
}
