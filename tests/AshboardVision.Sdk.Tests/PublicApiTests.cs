using System.Text;
using Trofeo.ModuleKit;

namespace AshboardVision.Sdk.Tests;

/// <summary>
/// Le gel de l'API publique de la ligne 1.x du SDK : <c>AshboardVision.Sdk</c> et <c>Trofeo.ModuleKit</c> (embarqué dans le paquet).
/// Règle : « on ajoute, on ne retire pas ». Un module compilé contre la 1.0 doit continuer de se charger et de tourner avec toute
/// version 1.x du logiciel. Le test échoue donc si une ligne de l'instantané disparaît (membre retiré, signature changée, type ou
/// interface en moins), et aussi si un membre <b>abstrait</b> (ou d'interface) est ajouté à un type qui existait déjà : cela casserait
/// toute classe qui en hérite ou qui l'implémente. Tout autre ajout (type, membre non abstrait, valeur d'enum) passe sans toucher à l'instantané.
/// L'instantané ne couvre que le contrat des modules de la communauté : les types de <see cref="ApiSurface.Excluded"/> (décision R31) en sont exclus.
/// </summary>
public sealed class PublicApiTests
{
    private const string UpdateVariable = "ASHBOARD_UPDATE_API_SNAPSHOT";

    private static readonly string[] AbstractMemberMarkers =
    [
        " :: method public abstract ", " :: method protected abstract ", " :: method protected internal abstract ",
        " :: property public abstract ", " :: property protected abstract ", " :: property protected internal abstract ",
        " :: event public abstract ", " :: event protected abstract ", " :: event protected internal abstract ",
    ];

    private static string SnapshotPath =>
        Path.Combine(RepositoryRoot.Find(), "tests", "AshboardVision.Sdk.Tests", "api-1.0.txt");

    private static IReadOnlyList<string> Current() =>
        ApiSurface.Describe(typeof(CommunityModule).Assembly, typeof(Theme).Assembly);

    [Fact]
    public void PublicApi_MatchesSnapshot()
    {
        var actual = Current();
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            File.WriteAllText(SnapshotPath, Header + string.Join('\n', actual) + "\n", new UTF8Encoding(false));
            Assert.Fail("Instantané régénéré : relis le diff (il ne doit montrer que des lignes ajoutées) et relance sans la variable " + UpdateVariable + ".");
        }

        var expected = SnapshotLines();

        var removed = expected.Except(actual, StringComparer.Ordinal).ToArray();
        Assert.True(removed.Length == 0,
            "L'API publique du SDK a perdu ou changé ces éléments (« on ajoute, on ne retire pas ») :\n" + string.Join('\n', removed));

        // Un ajout abstrait sur un type déjà publié casserait les modules existants.
        var knownTypes = expected.Where(IsTypeLine).Select(Owner).ToHashSet(StringComparer.Ordinal);
        var breakingAdditions = actual.Except(expected, StringComparer.Ordinal)
            .Where(line => knownTypes.Contains(Owner(line)) && AbstractMemberMarkers.Any(marker => line.Contains(marker, StringComparison.Ordinal)))
            .ToArray();
        Assert.True(breakingAdditions.Length == 0,
            "Ces membres abstraits (ou d'interface) ajoutés à un type déjà publié casseraient les modules existants :\n" + string.Join('\n', breakingAdditions));
    }

    [Fact]
    public void PublicApi_Contract_ExposesNoExcludedType()
    {
        // Un type du contrat qui laisserait voir un type exclu (paramètre, retour, propriété, base, interface) le gèlerait de fait.
        Assert.Empty(ApiSurface.FindExposures(typeof(CommunityModule).Assembly, typeof(Theme).Assembly));
    }

    [Fact]
    public void PublicApi_Excluded_TypesExistAndAreAbsentFromSnapshot()
    {
        var exported = new[] { typeof(CommunityModule).Assembly, typeof(Theme).Assembly }
            .SelectMany(assembly => assembly.GetExportedTypes())
            .Select(type => type.FullName!)
            .ToHashSet(StringComparer.Ordinal);

        // Une faute de frappe dans la liste n'exclurait rien sans le dire.
        Assert.All(ApiSurface.Excluded.Keys, name => Assert.Contains(name, exported));
        Assert.DoesNotContain(Current(), line => ApiSurface.Excluded.Keys.Any(name => line.Contains("] " + name + " ::", StringComparison.Ordinal)));
        Assert.DoesNotContain(SnapshotLines(), line => ApiSurface.Excluded.Keys.Any(name => line.Contains("] " + name + " ::", StringComparison.Ordinal)));
    }

    [Fact]
    public void AssemblyVersions_AreFrozenAt1_0_0_0()
    {
        // Une version d'assemblage qui suivrait le logiciel ferait demander à un module compilé plus tard une version plus haute que celle d'un hôte plus ancien.
        var expected = new Version(1, 0, 0, 0);
        Assert.Equal(expected, typeof(CommunityModule).Assembly.GetName().Version);
        Assert.Equal(expected, typeof(Theme).Assembly.GetName().Version);
    }

    [Fact]
    public void PublicApi_Description_IsDeterministicAndFreeOfVersions()
    {
        var first = Current();
        var second = Current();

        Assert.Equal(first, second);
        Assert.Equal(first.Order(StringComparer.Ordinal), first);
        Assert.Equal(first.Count, first.Distinct().Count());
        Assert.DoesNotContain(first, line => line.Contains("Version=", StringComparison.Ordinal) || line.Contains("PublicKeyToken", StringComparison.Ordinal));
        Assert.Contains(first, line => line.StartsWith("[AshboardVision.Sdk] ", StringComparison.Ordinal));
        Assert.Contains(first, line => line.StartsWith("[Trofeo.ModuleKit] ", StringComparison.Ordinal));
    }

    [Fact]
    public void PublicApi_Snapshot_IsSorted()
    {
        var lines = SnapshotLines();

        Assert.NotEmpty(lines);
        Assert.Equal(lines.Order(StringComparer.Ordinal), lines);
    }

    private static string[] SnapshotLines() =>
        File.ReadAllLines(SnapshotPath).Where(line => line.Length > 0 && !line.StartsWith('#')).ToArray();

    private static bool IsTypeLine(string line) => line.Contains(" :: type ", StringComparison.Ordinal);

    private static string Owner(string line) => line[..line.IndexOf(" :: ", StringComparison.Ordinal)];

    private const string Header =
        "# Instantané de l'API publique de la ligne 1.x du SDK (AshboardVision.Sdk + Trofeo.ModuleKit, embarqué dans le paquet).\n" +
        "# Règle : on ajoute, on ne retire pas. Une ligne de ce fichier ne doit jamais disparaître ni changer pendant toute la 1.x.\n" +
        "# Un ajout (type, membre non abstrait) n'exige pas de mise à jour, mais on peut regénérer le fichier pour qu'il reste complet :\n" +
        "#   PowerShell : $env:ASHBOARD_UPDATE_API_SNAPSHOT = '1'; dotnet test tests/AshboardVision.Sdk.Tests --filter PublicApi_MatchesSnapshot\n" +
        "# puis relire la différence : elle ne doit montrer que des lignes ajoutées. Ajouter un membre abstrait (ou d'interface) à un type\n" +
        "# déjà publié casse les modules existants : il faut alors une version majeure (2.0), pas une mise à jour de ce fichier.\n" +
        "# Une ligne = [assemblage] Type :: fait. Triée en ordre ordinal, sans numéro de version.\n";
}
