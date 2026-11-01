using System.Reflection;
using System.Runtime.Loader;
using AshboardVision.Sdk;

namespace Trofeo.ModuleHost;

/// <summary>Le module ne peut pas être chargé ; le message est en français et part au service dans un <c>Failure</c>.</summary>
internal sealed class ModuleLoadException : Exception
{
    public ModuleLoadException(string message)
        : base(message)
    {
    }

    public ModuleLoadException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>Le module chargé : sa classe, et la version du SDK contre laquelle il a été compilé.</summary>
internal sealed record LoadedModule(Type ModuleType, string RequiredSdk);

/// <summary>
/// Charge la DLL d'un module dans un contexte à elle. Les assemblages que le module partage avec l'hôte (le SDK, ModuleKit,
/// SkiaSharp) viennent toujours de l'hôte, quelles que soient les copies glissées dans le dossier du module ; ceux de .NET aussi ;
/// le reste vient du dossier du module.
/// </summary>
internal static class ModuleLoader
{
    private static readonly string[] SharedAssemblies = ["AshboardVision.Sdk", "Trofeo.ModuleKit"];

    /// <summary>
    /// Charge <paramref name="entry"/> (un chemin relatif à <paramref name="moduleDirectory"/>) et y cherche la classe du module :
    /// exactement une classe publique non abstraite dérivée de <see cref="CommunityModule"/>, avec un constructeur sans paramètre.
    /// </summary>
    public static LoadedModule Load(string moduleDirectory, string entry)
    {
        if (!Path.IsPathFullyQualified(moduleDirectory) || !Directory.Exists(moduleDirectory))
        {
            throw new ModuleLoadException("Le dossier du module est introuvable.");
        }

        var directory = Path.GetFullPath(moduleDirectory);
        var path = Path.GetFullPath(Path.Combine(directory, entry));
        var root = Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ModuleLoadException("Le fichier d'entrée du module doit être dans son dossier.");
        }

        if (!File.Exists(path))
        {
            throw new ModuleLoadException($"Le fichier d'entrée du module est introuvable : {Path.GetRelativePath(directory, path)}.");
        }

        Assembly assembly;
        Type[] exported;
        try
        {
            assembly = new ModuleLoadContext(directory).LoadFromAssemblyPath(path);
            exported = assembly.GetExportedTypes();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Un fichier qui n'est pas un assemblage .NET, ou une dépendance introuvable : le message de .NET dit laquelle.
            throw new ModuleLoadException($"Impossible de charger {Path.GetFileName(path)} : {ex.Message}", ex);
        }

        var candidates = exported
            .Where(type => type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false } && typeof(CommunityModule).IsAssignableFrom(type))
            .ToList();
        if (candidates.Count == 0)
        {
            throw new ModuleLoadException(
                $"{Path.GetFileName(path)} ne contient aucune classe de module : il en faut une, publique, dérivée de CommunityModule.");
        }

        if (candidates.Count > 1)
        {
            throw new ModuleLoadException(
                $"{Path.GetFileName(path)} contient plusieurs classes de module ({string.Join(", ", candidates.Select(type => type.Name))}) : il en faut exactement une.");
        }

        var moduleType = candidates[0];
        if (moduleType.GetConstructor(Type.EmptyTypes) is null)
        {
            throw new ModuleLoadException($"La classe {moduleType.Name} n'a pas de constructeur public sans paramètre.");
        }

        return new LoadedModule(moduleType, RequiredSdkOf(assembly));
    }

    /// <summary>
    /// Le fichier <c>nom.dll</c> d'une dépendance dans le dossier du module, s'il existe. Le nom vient des métadonnées du module :
    /// un nom qui porte un séparateur ou <c>..</c> (donc qui sortirait du dossier) ne donne jamais de chemin.
    /// </summary>
    internal static string? ResolveDependency(string directory, string name)
    {
        if (name.Length == 0 || name.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, name + ".dll"));
        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate) ? candidate : null;
    }

    /// <summary>La version du SDK contre laquelle le module a été compilé, sous la forme <c>majeur.mineur</c>.</summary>
    private static string RequiredSdkOf(Assembly module)
    {
        var reference = module.GetReferencedAssemblies().FirstOrDefault(name => name.Name == "AshboardVision.Sdk");
        var version = reference?.Version ?? typeof(CommunityModule).Assembly.GetName().Version ?? new Version(1, 0);
        return $"{version.Major}.{version.Minor}";
    }

    private sealed class ModuleLoadContext : AssemblyLoadContext
    {
        private static readonly Lazy<HashSet<string>> PlatformAssemblies = new(() =>
        {
            var list = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "";
            return new HashSet<string>(
                list.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(Path.GetFileNameWithoutExtension)!,
                StringComparer.OrdinalIgnoreCase);
        });

        private readonly string _directory;

        public ModuleLoadContext(string directory)
            : base("module:" + Path.GetFileName(directory), isCollectible: false)
        {
            _directory = directory;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var name = assemblyName.Name;
            if (name is null)
            {
                return null;
            }

            // Les assemblages de l'hôte : toujours les siens, la version demandée par le module n'y change rien.
            if (SharedAssemblies.Contains(name, StringComparer.OrdinalIgnoreCase) || name.StartsWith("SkiaSharp", StringComparison.OrdinalIgnoreCase))
            {
                if (Default.Assemblies.FirstOrDefault(loaded => string.Equals(loaded.GetName().Name, name, StringComparison.OrdinalIgnoreCase)) is { } hostCopy)
                {
                    return hostCopy;
                }

                if (PlatformAssemblies.Value.Contains(name))
                {
                    return Default.LoadFromAssemblyName(new AssemblyName(name));
                }
            }
            else if (PlatformAssemblies.Value.Contains(name))
            {
                return null; // .NET et l'hôte : la résolution normale, jamais une copie du dossier du module
            }

            return ResolveDependency(_directory, name) is { } candidate ? LoadFromAssemblyPath(candidate) : null;
        }
    }
}
