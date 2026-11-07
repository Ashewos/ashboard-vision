using System.Text.RegularExpressions;

namespace AshboardVision.Sdk.Tests;

/// <summary>
/// La documentation du SDK (<c>docs/sdk</c>) reste vraie. Deux garde-fous ici (les exemples de champs de réglage, eux, sont lus par le vrai
/// <c>ManifestReader</c> dans <c>Trofeo.Extensions.Tests.SdkDocsFieldExamplesTests</c>) :
/// <list type="bullet">
/// <item>chaque lien relatif des pages du SDK, du README et de l'utilisation avancée pointe vers un fichier qui existe (et, pour une page
/// Markdown, vers un titre qui existe) ;</item>
/// <item>chaque extrait de code des pages du SDK vient d'un fichier du dépôt (un exemple, ou le modèle de projet) et figure encore, tel
/// quel, dans ce fichier : si un exemple change sans la page, le test échoue ;</item>
/// </list>
/// Un extrait porte en légende, sur la dernière ligne non vide qui le précède, le chemin de son fichier source entre apostrophes inversées
/// (par exemple <c>*Extrait de `examples/Demo/Demo/DemoModule.cs`*</c>). Les blocs <c>csharp</c> doivent tous avoir cette légende ; les blocs
/// <c>json</c> et <c>xml</c> sont vérifiés quand ils en ont une.
/// </summary>
public sealed partial class DocsTests
{
    private static readonly string Root = RepositoryRoot.Find();

    private static string SdkDocs => Path.Combine(Root, "docs", "sdk");

    /// <summary>Les pages que la documentation du SDK doit contenir.</summary>
    private static readonly string[] ExpectedPages =
    [
        "README.md", "demarrer.md", "contrat.md", "permissions.md", "dessin.md", "reglages.md", "tests.md", "publier.md", "compatibilite.md",
        "securite.md",
    ];

    [GeneratedRegex(@"\[[^\]]*\]\(([^)\s]+)(?:\s+""[^""]*"")?\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"\b(?:src|href)=""([^""]+)""")]
    private static partial Regex HtmlLink();

    [GeneratedRegex(@"`([^`]+\.(?:cs|json|csproj))`")]
    private static partial Regex CaptionPath();

    [GeneratedRegex(@"^#{1,6}\s+(.+?)\s*#*\s*$")]
    private static partial Regex HeadingLine();

    private static IEnumerable<string> SdkPages() =>
        Directory.Exists(SdkDocs) ? Directory.EnumerateFiles(SdkDocs, "*.md").Order(StringComparer.Ordinal) : [];

    private static IEnumerable<string> LinkedPages() =>
        SdkPages().Append(Path.Combine(Root, "README.md")).Append(Path.Combine(Root, "docs", "utilisation-avancee.md"));

    private static string[] Lines(string text) => text.Replace("\r\n", "\n").Split('\n');

    // ---------------------------------------------------------------- les pages

    [Fact]
    public void SdkDocs_HasEveryExpectedPage()
    {
        Assert.True(Directory.Exists(SdkDocs), "Le dossier docs/sdk n'existe pas.");
        var missing = ExpectedPages.Where(page => !File.Exists(Path.Combine(SdkDocs, page))).ToArray();
        Assert.True(missing.Length == 0, "Pages manquantes dans docs/sdk : " + string.Join(", ", missing));
    }

    [Fact]
    public void SdkDocs_Summary_LinksToEveryOtherPage()
    {
        var summary = Path.Combine(SdkDocs, "README.md");
        Assert.True(File.Exists(summary), "docs/sdk/README.md (le sommaire) n'existe pas.");
        var text = File.ReadAllText(summary);

        var unlinked = ExpectedPages.Where(page => page != "README.md" && !text.Contains("(" + page, StringComparison.Ordinal)).ToArray();
        Assert.True(unlinked.Length == 0, "Le sommaire ne renvoie pas vers : " + string.Join(", ", unlinked));
    }

    // ---------------------------------------------------------------- les liens

    [Fact]
    public void RelativeLinks_PointToExistingFilesAndHeadings()
    {
        var problems = new List<string>();
        foreach (var page in LinkedPages())
        {
            Assert.True(File.Exists(page), page + " n'existe pas.");
            var text = File.ReadAllText(page);
            var targets = MarkdownLink().Matches(text).Select(m => m.Groups[1].Value)
                .Concat(HtmlLink().Matches(text).Select(m => m.Groups[1].Value));
            foreach (var target in targets)
            {
                if (Problem(page, target) is { } problem)
                {
                    problems.Add($"{Path.GetRelativePath(Root, page)} : {problem}");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void LinkChecker_FailsOnAMissingFileAndOnAMissingHeading()
    {
        // La vérification elle-même doit pouvoir échouer : sans cela, un « tout est vert » ne prouverait rien.
        var page = Path.Combine(Root, "README.md");
        Assert.NotNull(Problem(page, "docs/n-existe-pas.md"));
        Assert.NotNull(Problem(page, "docs/utilisation-avancee.md#un-titre-qui-n-existe-pas"));
        Assert.Null(Problem(page, "docs/utilisation-avancee.md#raccourcis-clavier"));
        Assert.Null(Problem(page, "https://example.org/ailleurs"));
        Assert.Null(Problem(page, "#modules"));
    }

    /// <summary>Ce qui ne va pas dans ce lien relatif, ou <c>null</c> s'il va bien (ou s'il n'est pas relatif).</summary>
    private static string? Problem(string page, string target)
    {
        if (Regex.IsMatch(target, @"^[a-z][a-z0-9+.-]*:", RegexOptions.IgnoreCase))
        {
            return null; // https:, mailto: : hors ligne, on ne vérifie pas
        }

        var hash = target.IndexOf('#');
        var path = hash >= 0 ? target[..hash] : target;
        var anchor = hash >= 0 ? target[(hash + 1)..] : "";

        var file = path.Length == 0 ? page : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(page)!, Uri.UnescapeDataString(path)));
        if (!File.Exists(file) && !Directory.Exists(file))
        {
            return $"le lien « {target} » ne mène à aucun fichier";
        }

        if (anchor.Length > 0 && file.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            var headings = Headings(File.ReadAllText(file));
            if (!headings.Contains(Uri.UnescapeDataString(anchor).ToLowerInvariant()))
            {
                return $"le lien « {target} » vise un titre qui n'existe pas";
            }
        }

        return null;
    }

    /// <summary>Les ancres que GitHub donne aux titres d'une page (minuscules, espaces en tirets, ponctuation retirée), hors blocs de code.</summary>
    private static HashSet<string> Headings(string markdown)
    {
        var anchors = new HashSet<string>(StringComparer.Ordinal);
        var inCode = false;
        foreach (var line in Lines(markdown))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
                continue;
            }

            var match = HeadingLine().Match(line);
            if (inCode || !match.Success)
            {
                continue;
            }

            var title = Regex.Replace(match.Groups[1].Value, @"[`*_]", "").Trim().ToLowerInvariant();
            var slug = Regex.Replace(Regex.Replace(title, @"[^\p{L}\p{N}\s_-]", ""), @"\s", "-");
            anchors.Add(slug);
        }

        return anchors;
    }

    // ---------------------------------------------------------------- les extraits

    [Fact]
    public void CodeBlocks_AreCaptionedAndStillInTheirSourceFile()
    {
        var problems = new List<string>();
        var checkedBlocks = 0;
        foreach (var page in SdkPages())
        {
            foreach (var block in Blocks(File.ReadAllText(page)))
            {
                var name = $"{Path.GetFileName(page)}, ligne {block.Line}";
                var mustBeCaptioned = block.Language == "csharp";
                if (block.Caption is null)
                {
                    if (mustBeCaptioned)
                    {
                        problems.Add($"{name} : un bloc csharp doit avoir, juste avant lui, une légende avec le chemin de son fichier source entre apostrophes inversées.");
                    }

                    continue;
                }

                if (block.Language is not ("csharp" or "json" or "xml"))
                {
                    continue;
                }

                checkedBlocks++;
                var source = Path.Combine(Root, block.Caption.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(source))
                {
                    problems.Add($"{name} : le fichier « {block.Caption} » de la légende n'existe pas.");
                }
                else if (!ContainsLines(File.ReadAllText(source), block.Text))
                {
                    problems.Add($"{name} : cet extrait ne figure plus, tel quel, dans « {block.Caption} » : mets la page à jour.");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
        Assert.True(checkedBlocks >= 10, $"Seulement {checkedBlocks} extraits vérifiés : la documentation doit montrer du vrai code des exemples.");
    }

    [Fact]
    public void ContainsLines_FailsWhenOneLineChangesAndIgnoresIndentation()
    {
        const string source = "class A\n{\n    void F()\n    {\n        Go();\n    }\n}\n";
        Assert.True(ContainsLines(source, "void F()\n{\n    Go();\n}"));
        Assert.True(ContainsLines(source, "Go();"));
        Assert.False(ContainsLines(source, "void F()\n{\n    Stop();\n}"));
        Assert.False(ContainsLines(source, "void F()\n{\n    Go();\n}\n\nvoid G() { }"));
    }

    /// <summary>Vrai si les lignes de l'extrait se suivent, une à une, dans le fichier (indentation et espaces de fin ignorés).</summary>
    private static bool ContainsLines(string source, string excerpt)
    {
        var haystack = Lines(source).Select(line => line.Trim()).ToArray();
        var needle = Lines(excerpt).Select(line => line.Trim()).ToList();
        while (needle.Count > 0 && needle[^1].Length == 0)
        {
            needle.RemoveAt(needle.Count - 1);
        }

        while (needle.Count > 0 && needle[0].Length == 0)
        {
            needle.RemoveAt(0);
        }

        if (needle.Count == 0)
        {
            return false;
        }

        for (var start = 0; start + needle.Count <= haystack.Length; start++)
        {
            var same = true;
            for (var i = 0; i < needle.Count && same; i++)
            {
                same = haystack[start + i] == needle[i];
            }

            if (same)
            {
                return true;
            }
        }

        return false;
    }

    // ---------------------------------------------------------------- lecture d'une page

    private sealed record Block(string Language, string Text, string? Caption, int Line);

    /// <summary>Les blocs de code d'une page, avec le chemin de leur légende (la dernière ligne non vide qui les précède) s'il y en a un.</summary>
    private static IEnumerable<Block> Blocks(string markdown)
    {
        var lines = Lines(markdown);
        for (var i = 0; i < lines.Length; i++)
        {
            var opening = lines[i].TrimStart();
            if (!opening.StartsWith("```", StringComparison.Ordinal))
            {
                continue;
            }

            var language = opening[3..].Trim();
            var body = new List<string>();
            var j = i + 1;
            while (j < lines.Length && !lines[j].TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                body.Add(lines[j]);
                j++;
            }

            string? caption = null;
            for (var k = i - 1; k >= 0; k--)
            {
                if (lines[k].Trim().Length == 0)
                {
                    continue;
                }

                if (CaptionPath().Match(lines[k]) is { Success: true } match)
                {
                    caption = match.Groups[1].Value;
                }

                break;
            }

            yield return new Block(language, string.Join('\n', body), caption, i + 1);
            i = j;
        }
    }
}
