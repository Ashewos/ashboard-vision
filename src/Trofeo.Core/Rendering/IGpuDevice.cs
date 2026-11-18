using SkiaSharp;

namespace Trofeo.Core.Rendering;

/// <summary>
/// Une carte graphique prête à dessiner pour Skia. Core ne connaît que cette interface : Direct3D 12 vit dans
/// <c>Trofeo.Display</c>. Un appareil se sert depuis un seul fil, celui de la boucle d'affichage.
/// </summary>
public interface IGpuDevice : IDisposable
{
    /// <summary>Le nom de la carte, tel que Windows l'affiche.</summary>
    string AdapterName { get; }

    /// <summary>Crée une surface de la carte. Lève <see cref="GpuLostException"/> si la carte est perdue.</summary>
    SKSurface CreateSurface(SKImageInfo info);

    /// <summary>Envoie à la carte le travail en attente, sans attendre qu'il soit fini.</summary>
    void Submit();

    /// <summary>La carte est retirée ou réinitialisée, ou le contexte de Skia est abandonné : l'appareil ne sert plus.</summary>
    bool IsLost { get; }

    /// <summary>La raison de la perte, en français, pour le journal et la fenêtre ; <c>null</c> tant que la carte est là.</summary>
    string? LostReason { get; }

    /// <summary>Le nombre de ressources dans le cache de textures de Skia.</summary>
    int CacheResources { get; }

    /// <summary>Les octets occupés par le cache de textures de Skia.</summary>
    long CacheBytes { get; }

    /// <summary>Libère les ressources du cache que personne n'utilise.</summary>
    void PurgeUnused();
}

/// <summary>La carte graphique est perdue (retirée, réinitialisée, pilote planté) : l'image doit se faire par le processeur.</summary>
public sealed class GpuLostException(string reason) : Exception(reason);
