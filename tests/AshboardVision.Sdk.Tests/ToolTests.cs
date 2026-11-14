using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;
using Trofeo.Core.Layout;
using Trofeo.Core.Rendering;

namespace AshboardVision.Sdk.Tests;

/// <summary>
/// Le banc d'essai <c>ashboard-module</c>, lancé tel qu'il est dans son paquet, sur le module d'essai de l'hôte
/// (<c>Trofeo.ModuleHost.Tests.SampleModule</c>) : il s'enferme pour de vrai dans un AppContainer et un job object. Chaque essai a son identifiant
/// de module (donc son propre conteneur d'essai <c>ashboard-vision.check.&lt;id&gt;</c>), supprimé à la fin même en cas d'échec ; tout se passe
/// dans des dossiers temporaires, et la mesure est raccourcie à 2 secondes (<c>--mesure</c>, une option cachée) pour ne pas allonger la suite.
/// </summary>
[Collection(PackedTool.Name)]
[Trait("Windows", "Sandbox")]
public sealed class ToolTests(PackedTool packed) : IDisposable
{
    private readonly List<string> _folders = [];
    private readonly List<string> _containers = [];

    public void Dispose()
    {
        foreach (var id in _containers)
        {
            // Filet de sécurité : l'outil supprime déjà son conteneur ; un essai qui a échoué en route ne doit rien laisser.
            DeleteAppContainerProfile("ashboard-vision.check." + id);
        }

        foreach (var folder in _folders)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int DeleteAppContainerProfile(string appContainerName);

    private static string ContainerFolder(string id) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", "ashboard-vision.check." + id);

    [WindowsToolFact]
    public void Check_GoodModule_IsCompatibleAndWritesPngs()
    {
        var module = NewModule(permissions: "\"capteurs\"");
        var before = Snapshot(module.Folder);
        var output = NewFolder();

        var run = packed.Run("check", module.Folder, "--out", output, "--mesure", "2");

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("✔ Manifeste : " + module.Id + " 1.0.0", run.Output, StringComparison.Ordinal);
        Assert.Contains("✔ Démarrage enfermé : première image en", run.Output, StringComparison.Ordinal);
        Assert.Contains("✔ Images : 9 images PNG écrites", run.Output, StringComparison.Ordinal);
        Assert.Contains("✔ Sans la permission « capteurs » : le module continue de tourner.", run.Output, StringComparison.Ordinal);
        Assert.Contains("✔ Dessin :", run.Output, StringComparison.Ordinal);
        Assert.Contains("Processeur :", run.Output, StringComparison.Ordinal);
        Assert.Contains("Mémoire :", run.Output, StringComparison.Ordinal);
        Assert.Contains("✔ Arrêt propre", run.Output, StringComparison.Ordinal);
        Assert.EndsWith("Compatible avec Ashboard Vision (SDK 1.1)." + Environment.NewLine, run.Output, StringComparison.Ordinal);

        // Une image par taille (minSize, compact, idéal) et par thème, de la taille que la case aurait sur l'écran, avec le dessin du module dedans.
        var files = Directory.GetFiles(output, "*.png").Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(9, files.Length);
        foreach (var theme in new[] { "classique", "liquid-glass", "pixel" })
        {
            foreach (var (columns, rows) in new[] { (2, 1), (3, 1), (4, 2) })
            {
                var name = $"{module.Id}_{theme}_{columns}x{rows}.png";
                Assert.Contains(name, files);
                using var image = SKBitmap.Decode(Path.Combine(output, name));
                var expected = GridGeometry.ModuleRect(0, 0, columns, rows, Themes.Find(theme)!.Grid);
                Assert.Equal((expected.Width, expected.Height), (image.Width, image.Height));
                Assert.Equal(new SKColor(255, 0, 0), image.GetPixel(5, 5)); // la moitié gauche est rouge : c'est le dessin du module
            }
        }

        // Le banc d'essai ne laisse rien : ni conteneur, ni fichier dans le dossier du module.
        Assert.False(Directory.Exists(ContainerFolder(module.Id)), "Le conteneur d'essai n'a pas été supprimé.");
        Assert.Equal(before, Snapshot(module.Folder));
    }

    [WindowsToolFact]
    public void Check_DeliversEachDeclaredShortcut()
    {
        var module = NewModule(
            sdk: "1.1",
            shortcuts: """[ { "id": "refresh", "name": "Rafraîchir", "keys": "Ctrl+Alt+Shift+R" }, { "id": "colour", "name": "Changer de couleur" } ]""");

        var run = packed.Run("check", module.Folder, "--out", NewFolder(), "--mesure", "2");

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("✔ Raccourcis : 2 livrés au module (refresh, colour), sans erreur.", run.Output, StringComparison.Ordinal);
        Assert.Contains("Compatible avec Ashboard Vision (SDK 1.1).", run.Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(ContainerFolder(module.Id)), "Le conteneur d'essai n'a pas été supprimé.");
    }

    [WindowsToolFact]
    public void Check_ModuleWithoutShortcuts_SaysNothingAboutThem()
    {
        var module = NewModule();

        var run = packed.Run("check", module.Folder, "--out", NewFolder(), "--mesure", "2");

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.DoesNotContain("Raccourci", run.Output, StringComparison.Ordinal);
    }

    [WindowsToolFact]
    public void Check_ShortcutThatThrows_IsReportedAsAnError()
    {
        // Le module d'essai lève une exception quand on lui livre le raccourci « boom » : le logiciel la garde au journal sans mettre la case en
        // erreur, mais le banc la compte contre le module, comme une exception de Render.
        var module = NewModule(sdk: "1.1", shortcuts: """[ { "id": "ok", "name": "Tout va bien" }, { "id": "boom", "name": "Plante" } ]""");

        var run = packed.Run("check", module.Folder, "--out", NewFolder(), "--mesure", "2");

        Assert.True(run.ExitCode == 1, run.Output);
        Assert.Contains("✖ Raccourci « boom » : le module lève une exception dans OnShortcutAsync", run.Output, StringComparison.Ordinal);
        Assert.Contains("Pas compatible : le module lève une exception quand le raccourci « boom » est déclenché.", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("✖ Raccourci « ok »", run.Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(ContainerFolder(module.Id)), "Le conteneur d'essai n'a pas été supprimé.");
    }

    [WindowsToolFact]
    public void Check_ShortcutThatThrows_NotLast_IsBlamedOnIt()
    {
        // Le raccourci qui plante est suivi de deux autres : l'exception est rapportée au journal avec l'identifiant de « boom », pas à un voisin.
        var module = NewModule(
            sdk: "1.1",
            shortcuts: """[ { "id": "first", "name": "Premier" }, { "id": "boom", "name": "Plante" }, { "id": "last", "name": "Dernier" } ]""");

        var run = packed.Run("check", module.Folder, "--out", NewFolder(), "--mesure", "2");

        Assert.True(run.ExitCode == 1, run.Output);
        Assert.Contains("✖ Raccourci « boom » : le module lève une exception dans OnShortcutAsync", run.Output, StringComparison.Ordinal);
        Assert.Contains("Pas compatible : le module lève une exception quand le raccourci « boom » est déclenché.", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("✖ Raccourci « first »", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("✖ Raccourci « last »", run.Output, StringComparison.Ordinal);
    }

    [WindowsToolFact]
    public void Check_ModuleCrashingWithoutPermission_IsNotCompatible()
    {
        var module = NewModule(
            permissions: "\"capteurs\"",
            settings: """[ { "kind": "choice", "key": "mode", "label": "Mode", "options": [ { "value": "crashWithoutSensors", "label": "Plante" } ], "default": "crashWithoutSensors" } ]""");

        var run = packed.Run("check", module.Folder, "--out", NewFolder(), "--mesure", "2");

        Assert.True(run.ExitCode == 1, run.Output);
        Assert.Contains("✖ Sans la permission « capteurs » : l'hôte du module s'arrête", run.Output, StringComparison.Ordinal);
        Assert.Contains("Pas compatible : le module s'arrête quand la permission « capteurs » est refusée.", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Compatible avec Ashboard Vision", run.Output.Replace("Pas compatible", ""), StringComparison.Ordinal);
        Assert.False(Directory.Exists(ContainerFolder(module.Id)), "Le conteneur d'essai n'a pas été supprimé.");
    }

    [WindowsToolFact]
    public void Check_InvalidManifest_ExitCode1()
    {
        var module = NewModule(manifest: """{ "id": "PasValide", "name": "x" }""");
        var output = NewFolder();

        var run = packed.Run("check", module.Folder, "--out", output);

        Assert.True(run.ExitCode == 1, run.Output);
        Assert.Contains("✖ Manifeste : ", run.Output, StringComparison.Ordinal);
        Assert.Contains("Pas compatible : le manifeste n'est pas valide.", run.Output, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(output)); // rien n'a tourné
        Assert.True(run.Elapsed < TimeSpan.FromSeconds(30), "Un manifeste invalide ne doit lancer aucun module.");
    }

    [WindowsToolFact]
    public void Check_MissingManifest_ExitCode1()
    {
        var empty = NewFolder();

        var run = packed.Run("check", empty, "--out", NewFolder());

        Assert.True(run.ExitCode == 1, run.Output);
        Assert.Contains("manifest.json est introuvable", run.Output, StringComparison.Ordinal);
    }

    [WindowsToolFact]
    public void Check_MissingEntryDll_ExitCode1()
    {
        var module = NewModule();
        File.Delete(Path.Combine(module.Folder, "Trofeo.ModuleHost.Tests.SampleModule.dll"));

        var run = packed.Run("check", module.Folder, "--out", NewFolder());

        Assert.True(run.ExitCode == 1, run.Output);
        Assert.Contains("la DLL « Trofeo.ModuleHost.Tests.SampleModule.dll » est introuvable", run.Output, StringComparison.Ordinal);
    }

    [WindowsToolFact]
    public void Check_ModuleAskingForANewerSdk_IsNotCompatible_WithoutRunning()
    {
        var module = NewModule(sdk: "9.0");

        var run = packed.Run("check", module.Folder, "--out", NewFolder());

        Assert.True(run.ExitCode == 1, run.Output);
        Assert.Contains("✖ Compatibilité : Ce module demande une version plus récente d'Ashboard Vision", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Démarrage enfermé", run.Output, StringComparison.Ordinal);
    }

    [WindowsToolFact]
    public void Pack_ProducesInspectablePackageAndSha256()
    {
        var module = NewModule(permissions: "\"capteurs\"");
        var output = NewFolder();

        var run = packed.Run("pack", module.Folder, "--out", output);

        Assert.True(run.ExitCode == 0, run.Output);
        var package = Path.Combine(output, module.Id + "-1.0.0.avmod");
        Assert.True(File.Exists(package), run.Output);
        Assert.Equal(new[] { module.Id + "-1.0.0.avmod", module.Id + "-1.0.0.avmod.sha256" }, Directory.GetFiles(output).Select(Path.GetFileName).Order().ToArray());

        // L'empreinte au format de sha256sum : « <empreinte en minuscules>  <nom du fichier> », une ligne, sans BOM.
        var expected = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(package))) + "  " + module.Id + "-1.0.0.avmod\n";
        Assert.Equal(expected, File.ReadAllText(package + ".sha256", new UTF8Encoding(false)));
        Assert.NotEqual(0xEF, File.ReadAllBytes(package + ".sha256")[0]);

        // Le paquet passe l'inspection du logiciel (AvmodPackage.Inspect, celle de l'outil lui-même) : aucune erreur, le manifeste est lu.
        var (errors, manifestId) = Inspect(package);
        Assert.Empty(errors);
        Assert.Equal(module.Id, manifestId);
    }

    [WindowsToolFact]
    public void Pack_InvalidManifest_ExitCode1_AndNoFile()
    {
        var module = NewModule(manifest: """{ "id": "PasValide" }""");
        var output = NewFolder();

        var run = packed.Run("pack", module.Folder, "--out", output);

        Assert.True(run.ExitCode == 1, run.Output);
        Assert.Contains("✖ Manifeste : ", run.Output, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(output));
    }

    [WindowsToolFact]
    public void Pack_OutputInsideTheModuleFolder_IsRefused()
    {
        var module = NewModule();

        var run = packed.Run("pack", module.Folder, "--out", Path.Combine(module.Folder, "paquets"));

        Assert.True(run.ExitCode == 1, run.Output);
        Assert.Contains("choisis un autre dossier avec --out", run.Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(module.Folder, "paquets")));
    }

    [WindowsToolFact]
    public void Check_OutputThatIsAFile_ConcludesInFrenchWithoutRunningTheModule()
    {
        var module = NewModule(permissions: "\"capteurs\"");
        var file = Path.Combine(NewFolder(), "pas-un-dossier");
        File.WriteAllText(file, "x");

        var run = packed.Run("check", module.Folder, "--out", file, "--mesure", "2");

        Assert.True(run.ExitCode == 1, run.Output);
        Assert.Contains("✖ Images : le dossier de sortie", run.Output, StringComparison.Ordinal);
        Assert.Contains("Pas compatible : le dossier des images (--out) n'est pas utilisable.", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Démarrage enfermé", run.Output, StringComparison.Ordinal); // aucun hôte lancé
        Assert.DoesNotContain("   at ", run.Output, StringComparison.Ordinal); // pas de trace
        Assert.Equal("x", File.ReadAllText(file));
    }

    [WindowsToolFact]
    public void Pack_OutputThatIsAFile_ConcludesInFrench()
    {
        var module = NewModule();
        var file = Path.Combine(NewFolder(), "pas-un-dossier");
        File.WriteAllText(file, "x");

        var run = packed.Run("pack", module.Folder, "--out", file);

        Assert.True(run.ExitCode == 1, run.Output);
        Assert.Contains("✖ Paquet : le dossier de sortie", run.Output, StringComparison.Ordinal);
        Assert.Contains("Pas de paquet : le dossier de sortie (--out) n'est pas utilisable.", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", run.Output, StringComparison.Ordinal);
    }

    [WindowsToolFact]
    public void Check_ModuleThatStopsSlowly_IsNotCompatible()
    {
        var module = NewModule(
            settings: """[ { "kind": "choice", "key": "mode", "label": "Mode", "options": [ { "value": "slowStop", "label": "Lent" } ], "default": "slowStop" } ]""");

        var run = packed.Run("check", module.Folder, "--out", NewFolder(), "--mesure", "2");

        Assert.True(run.ExitCode == 1, run.Output);
        Assert.Contains("✖ Arrêt : ", run.Output, StringComparison.Ordinal);
        Assert.Contains("Pas compatible : le module ne s'arrête pas proprement en 5 s.", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("✔ Arrêt propre", run.Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(ContainerFolder(module.Id)), "Le conteneur d'essai n'a pas été supprimé.");
    }

    [WindowsToolFact]
    public void ContainerCleanup_NeverLetsAnExceptionOut_AndAlwaysSaysSo()
    {
        // La suppression du conteneur peut échouer de bien des façons (Marshal.ThrowExceptionForHR rend un COMException, un
        // UnauthorizedAccessException ou un IOException) : ni Dispose ni le gestionnaire de sortie du processus ne doivent laisser sortir quoi que ce soit.
        var context = new FolderLoadContext(packed.ToolFolder);
        try
        {
            var type = context.LoadFromAssemblyName(new AssemblyName("ashboard-module")).GetType("AshboardVision.Sdk.Tool.ContainerCleanup", throwOnError: true)!;
            var failures = new Exception[]
            {
                new COMException("com"), new UnauthorizedAccessException("acces"), new IOException("partage"),
                new System.ComponentModel.Win32Exception(5), new InvalidOperationException("autre"),
            };
            foreach (var failure in failures)
            {
                foreach (var viaProcessExit in new[] { false, true })
                {
                    var output = new StringWriter();
                    var cleanup = (IDisposable)Activator.CreateInstance(type, "alex.demo", output, (Action<string>)(_ => throw failure))!;
                    if (viaProcessExit)
                    {
                        type.GetMethod("OnExit", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(cleanup, [null, EventArgs.Empty]);
                    }

                    cleanup.Dispose();
                    cleanup.Dispose(); // une seconde fois : rien de plus

                    var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
                    Assert.Single(lines);
                    Assert.StartsWith("! Nettoyage : le conteneur d'essai « ashboard-vision.check.alex.demo » n'a pas pu être supprimé", lines[0], StringComparison.Ordinal);
                }
            }

            // Une console déjà fermée ne fait pas non plus sortir d'exception.
            var closed = new StreamWriter(Stream.Null);
            closed.Dispose();
            ((IDisposable)Activator.CreateInstance(type, "alex.demo", closed, (Action<string>)(_ => throw new IOException("x")))!).Dispose();
        }
        finally
        {
            context.Unload();
        }
    }

    // ---- aides --------------------------------------------------------------------------------------------------------------------

    private sealed record NewModuleInfo(string Id, string Folder);

    /// <summary>Un module neuf : le module d'essai de l'hôte copié dans un dossier temporaire, avec un manifeste (et donc un identifiant) à lui.</summary>
    private NewModuleInfo NewModule(string permissions = "", string settings = "[]", string sdk = "1.0", string? manifest = null, string shortcuts = "[]")
    {
        var id = "test.tool" + Guid.NewGuid().ToString("N")[..10];
        _containers.Add(id);
        var folder = NewFolder();
        CopyDirectory(Path.Combine(AppContext.BaseDirectory, "sample"), folder);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), manifest ?? $$"""
            {
              "id": "{{id}}", "name": "Module d'essai", "description": "Un module pour essayer le banc d'essai.", "author": "Test", "version": "1.0.0",
              "sdk": "{{sdk}}", "entry": "Trofeo.ModuleHost.Tests.SampleModule.dll",
              "permissions": [{{permissions}}],
              "minSize": { "columns": 2, "rows": 1 },
              "presets": { "compact": { "columns": 3, "rows": 1 }, "ideal": { "columns": 4, "rows": 2 } },
              "settings": {{settings}},
              "shortcuts": {{shortcuts}}
            }
            """);
        return new NewModuleInfo(id, folder);
    }

    private string NewFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ashboard-tool-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        _folders.Add(folder);
        return folder;
    }

    private static string[] Snapshot(string folder) =>
        [.. Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(folder, path)).Order(StringComparer.Ordinal)];

    /// <summary>Copie le module d'essai sans ses symboles ni ses bibliothèques natives (<c>runtimes</c>, des centaines de Mo) : l'hôte a les siens, et le paquet d'un module tient en 50 Mo.</summary>
    private static void CopyDirectory(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.StartsWith("runtimes" + Path.DirectorySeparatorChar, StringComparison.Ordinal) || file.EndsWith(".pdb", StringComparison.Ordinal))
            {
                continue;
            }

            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }

    /// <summary>
    /// Appelle <c>AvmodPackage.Inspect</c> de <c>Trofeo.Extensions.dll</c> tel qu'il est dans l'outil : ce projet de tests ne peut pas référencer
    /// Trofeo.Extensions (il vise net10.0, Extensions net10.0-windows), alors on charge l'assemblage de l'outil et on l'appelle par réflexion. Le
    /// chargement se fait dans un contexte à part, depuis la mémoire et déchargé ensuite : aucun fichier du dossier de l'outil ne reste verrouillé.
    /// </summary>
    private (string[] Errors, string? ManifestId) Inspect(string package)
    {
        var context = new FolderLoadContext(packed.ToolFolder);
        try
        {
            var assembly = context.LoadFromAssemblyName(new AssemblyName("Trofeo.Extensions"));
            var inspection = assembly.GetType("Trofeo.Extensions.AvmodPackage", throwOnError: true)!
                .GetMethod("Inspect", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, [package])!;
            var type = inspection.GetType();
            var errors = ((System.Collections.IEnumerable)type.GetProperty("Errors")!.GetValue(inspection)!).Cast<string>().ToArray();
            var manifest = type.GetProperty("Manifest")!.GetValue(inspection);
            return (errors, manifest?.GetType().GetProperty("Id")!.GetValue(manifest) as string);
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>Charge les assemblages d'un dossier depuis la mémoire (sans verrouiller leurs fichiers) ; le reste vient du contexte par défaut.</summary>
    private sealed class FolderLoadContext(string folder) : AssemblyLoadContext("inspection-du-paquet", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var path = Path.Combine(folder, assemblyName.Name + ".dll");
            return File.Exists(path) ? LoadFromStream(new MemoryStream(File.ReadAllBytes(path))) : null;
        }
    }
}
