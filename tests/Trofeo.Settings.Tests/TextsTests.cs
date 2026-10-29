using System.Resources;
using System.Text.RegularExpressions;
using Trofeo.Settings;

namespace Trofeo.Settings.Tests;

public partial class TextsTests
{
    private static HashSet<string> ResourceKeys()
    {
        var manager = new ResourceManager("Trofeo.Settings.Resources.Strings", typeof(Texts).Assembly);
        var set = manager.GetResourceSet(System.Globalization.CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false)
                  ?? throw new InvalidOperationException("Strings.resx introuvable.");
        return set.Cast<System.Collections.DictionaryEntry>().Select(e => (string)e.Key).ToHashSet();
    }

    private static HashSet<string> TextsProperties() =>
        typeof(Texts).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(p => p.Name).ToHashSet();

    private static string SourceDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "trofeo-HB-dashboard.sln")))
            dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Racine du dépôt introuvable."), "src", "Trofeo.Settings");
    }

    [GeneratedRegex(@"Texts\.([A-Za-z0-9_]+)")]
    private static partial Regex TextsUse();

    [Fact]
    public void Texts_EveryKeyExists_AndNoUnusedKey()
    {
        var keys = ResourceKeys();
        var properties = TextsProperties();

        // Une propriété par clé, et inversement.
        Assert.Empty(properties.Except(keys));
        Assert.Empty(keys.Except(properties));

        // Chaque propriété rend un texte non vide (un oubli rendrait la clé elle-même).
        foreach (var property in typeof(Texts).GetProperties().Where(p => p.GetMethod?.IsStatic == true))
        {
            var value = (string)property.GetValue(null)!;
            Assert.False(string.IsNullOrWhiteSpace(value), property.Name);
            Assert.NotEqual(property.Name, value);
        }

        // Aucune clé inutilisée : chacune sert dans le code ou dans le XAML.
        var used = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(SourceDirectory(), "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs") || f.EndsWith(".axaml")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                                 && Path.GetFileName(f) != "Texts.cs"))
        {
            foreach (Match match in TextsUse().Matches(File.ReadAllText(file)))
                used.Add(match.Groups[1].Value);
        }

        Assert.Empty(keys.Except(used));
        Assert.Empty(used.Except(keys));
    }

    [Fact]
    public void Texts_UserFacingTexts_AreInFrenchWithTu()
    {
        Assert.Equal("Réglages d'Ashboard Vision", Texts.WindowTitle);
        Assert.Equal("Démarrer le service", Texts.StartService);
        Assert.Equal("Le service d'affichage est introuvable. Lance Trofeo.Display.exe toi-même.", Texts.ServiceNotFound);
        Assert.Equal("Le service d'affichage ne tourne pas. Sans lui, l'écran reste éteint et rien ne peut être réglé.", Texts.ServiceStoppedMessage);
    }
}
