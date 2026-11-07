using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Trofeo.Extensions;

namespace Trofeo.Extensions.Tests;

/// <summary>
/// Les exemples de champs de réglage de la documentation du SDK (<c>docs/sdk</c>, blocs <c>json</c> sans fichier source en légende) sont lus par le
/// vrai <see cref="ManifestReader"/>, glissés dans un manifeste valide : la doc ne peut pas montrer un champ que le logiciel refuserait. Ce test
/// remplace une copie des options du lecteur dans les tests du SDK, qui aurait pu s'en écarter.
/// </summary>
public sealed partial class SdkDocsFieldExamplesTests
{
    private static readonly string[] EveryKind = ["choice", "toggle", "text", "list", "secret", "lookup", "action", "number", "port"];

    [GeneratedRegex(@"`[^`]+\.(?:cs|json|csproj)`")]
    private static partial Regex CaptionPath();

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "trofeo-HB-dashboard.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("La racine du dépôt (trofeo-HB-dashboard.sln) est introuvable.");
    }

    /// <summary>Les blocs <c>json</c> des pages du SDK qui n'ont pas, juste avant eux, la légende d'un fichier source.</summary>
    private static IEnumerable<(string Page, int Line, string Json)> UncaptionedJsonBlocks()
    {
        foreach (var page in Directory.EnumerateFiles(Path.Combine(Root(), "docs", "sdk"), "*.md").Order(StringComparer.Ordinal))
        {
            var lines = File.ReadAllText(page).Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart() != "```json")
                {
                    continue;
                }

                var previous = lines.Take(i).Reverse().FirstOrDefault(line => line.Trim().Length > 0) ?? "";
                var end = Array.FindIndex(lines, i + 1, line => line.TrimStart().StartsWith("```", StringComparison.Ordinal));
                if (!CaptionPath().IsMatch(previous))
                {
                    yield return (Path.GetFileName(page), i + 1, string.Join('\n', lines[(i + 1)..end]));
                }

                i = end;
            }
        }
    }

    /// <summary>Un manifeste valide dont les réglages sont ceux de l'exemple (un champ, ou un tableau de champs).</summary>
    private static string ManifestWith(string fieldsJson)
    {
        var fields = JsonNode.Parse(fieldsJson)!;
        var settings = fields is JsonArray ? fields : new JsonArray(fields);
        var manifest = new JsonObject
        {
            ["id"] = "essai.exemple",
            ["name"] = "Exemple",
            ["description"] = "Un exemple de la documentation.",
            ["author"] = "Essai",
            ["version"] = "1.0.0",
            ["sdk"] = "1.0",
            ["entry"] = "Exemple.dll",
            ["minSize"] = new JsonObject { ["columns"] = 3, ["rows"] = 1 },
            ["settings"] = settings,
        };
        return manifest.ToJsonString();
    }

    [Fact]
    public void FieldExamples_AreReadByTheRealManifestReaderAndCoverEveryKind()
    {
        var problems = new List<string>();
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (page, line, json) in UncaptionedJsonBlocks())
        {
            var result = ManifestReader.Read(ManifestWith(json));
            if (result.Manifest is null)
            {
                problems.Add($"{page}, ligne {line} : " + string.Join(" ", result.Errors));
            }

            var node = JsonNode.Parse(json)!;
            foreach (var field in node is JsonArray array ? array : [node])
            {
                kinds.Add(field!["kind"]!.GetValue<string>());
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
        Assert.True(EveryKind.All(kinds.Contains), "Genres de champ sans exemple : " + string.Join(", ", EveryKind.Where(kind => !kinds.Contains(kind))));
    }

    [Theory]
    [InlineData("""{ "kind": "inconnu", "key": "a", "label": "A" }""")]
    [InlineData("""{ "kind": "number", "key": "a", "label": "A", "min": 1 }""")]
    [InlineData("""{ "kind": "number", "key": "a", "label": "A", "min": 5, "max": 1, "default": 3 }""")]
    [InlineData("""{ "kind": "port", "key": "a", "label": "A", "default": 70000 }""")]
    public void ManifestReader_RefusesAFieldTheDocsMustNeverShow(string field)
    {
        // Le contrôle doit pouvoir échouer : un champ refusé par le lecteur ferait échouer le test ci-dessus.
        Assert.Null(ManifestReader.Read(ManifestWith(field)).Manifest);
    }
}
