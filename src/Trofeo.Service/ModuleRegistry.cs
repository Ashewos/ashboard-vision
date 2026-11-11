using System.Diagnostics;
using Trofeo.Core.Layout;
using Trofeo.Core.Logging;
using Trofeo.ModuleKit;

namespace Trofeo.Display;

/// <summary>
/// Les types de modules connus de l'application, indexés par leur identifiant : ceux d'origine (fixés à la construction, toujours en
/// tête) puis ceux des modules complémentaires, que <see cref="SetCommunityFactories"/> remplace d'un bloc. Chaque lecture
/// (<see cref="Descriptors"/>, <see cref="Types"/>, <see cref="Factory"/>) voit un instantané cohérent, sûr entre fils.
/// </summary>
public sealed class ModuleRegistry
{
    /// <summary>Les types connus à un instant : jamais modifié après sa publication.</summary>
    private sealed class Snapshot(Dictionary<string, IModuleFactory> factories, IReadOnlyList<ModuleDescriptor> types)
    {
        public Dictionary<string, IModuleFactory> Factories { get; } = factories;
        public IReadOnlyDictionary<string, ModuleDescriptor> Descriptors { get; } = factories.ToDictionary(f => f.Key, f => f.Value.Descriptor);
        public IReadOnlyList<ModuleDescriptor> Types { get; } = types;
    }

    private readonly List<IModuleFactory> _builtIn;
    private readonly Func<string, ISecretStore> _secretsFor;
    private readonly object _gate = new();
    private volatile Snapshot _snapshot;

    /// <param name="factories">Les fabriques des types de modules d'origine.</param>
    /// <param name="secretsFor">Le coffre à secrets d'un type de module, indexé par son identifiant de type.</param>
    public ModuleRegistry(IEnumerable<IModuleFactory> factories, Func<string, ISecretStore> secretsFor)
    {
        _secretsFor = secretsFor;
        _builtIn = factories.ToList();
        _ = _builtIn.ToDictionary(f => f.Descriptor.TypeId); // refuse deux fabriques du même type d'origine
        _snapshot = Build([]);
    }

    /// <summary>Levé après chaque remplacement des fabriques complémentaires (depuis le fil qui l'a demandé).</summary>
    public event EventHandler? TypesChanged;

    public IReadOnlyDictionary<string, ModuleDescriptor> Descriptors => _snapshot.Descriptors;

    /// <summary>Les types connus : ceux d'origine dans l'ordre où ils ont été enregistrés, puis les complémentaires.</summary>
    public IReadOnlyList<ModuleDescriptor> Types => _snapshot.Types;

    /// <summary>La fabrique d'un type ; <c>null</c> si le type est inconnu.</summary>
    public IModuleFactory? Factory(string typeId) => _snapshot.Factories.GetValueOrDefault(typeId);

    /// <summary>Le coffre à secrets d'un type de module.</summary>
    public ISecretStore SecretsFor(string typeId) => _secretsFor(typeId);

    /// <summary>
    /// Remplace d'un bloc les fabriques des modules complémentaires ; celles d'origine restent en tête. Un type complémentaire qui
    /// porte l'identifiant d'un type d'origine (ou d'un complémentaire déjà vu) est ignoré et journalisé : un module ne prend pas
    /// la place d'un autre. Lève <see cref="TypesChanged"/> une fois le nouvel instantané publié.
    /// </summary>
    public void SetCommunityFactories(IReadOnlyList<IModuleFactory> factories)
    {
        ArgumentNullException.ThrowIfNull(factories);
        lock (_gate)
        {
            _snapshot = Build(factories);
        }

        try
        {
            TypesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Un abonné à TypesChanged a échoué : {ex}");
        }
    }

    private Snapshot Build(IReadOnlyList<IModuleFactory> community)
    {
        var all = new List<IModuleFactory>(_builtIn);
        var known = new HashSet<string>(_builtIn.Select(f => f.Descriptor.TypeId));
        foreach (var factory in community)
        {
            if (known.Add(factory.Descriptor.TypeId))
            {
                all.Add(factory);
            }
            else
            {
                Trace.TraceWarning($"Module complémentaire ignoré : le type « {factory.Descriptor.TypeId} » existe déjà.");
            }
        }

        return new Snapshot(all.ToDictionary(f => f.Descriptor.TypeId), [.. all.Select(f => f.Descriptor)]);
    }

    /// <summary>Crée le module d'un emplacement. Type inconnu ou création en échec : journalisé, <c>null</c>.</summary>
    public IModule? Create(ModulePlacement p, TimeProvider time)
    {
        if (!_snapshot.Factories.TryGetValue(p.TypeId, out var factory))
        {
            Trace.TraceWarning($"Module ignoré : type inconnu « {p.TypeId} » ({p.InstanceId}).");
            return null;
        }

        try
        {
            var context = new ModuleContext(
                p.InstanceId, p.Settings, time, new TraceModuleLog(p.TypeId, p.InstanceId, time), _secretsFor(p.TypeId));
            return factory.Create(context);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Module ignoré : échec de création de « {p.TypeId} » ({p.InstanceId}) : {ex}");
            return null;
        }
    }
}
