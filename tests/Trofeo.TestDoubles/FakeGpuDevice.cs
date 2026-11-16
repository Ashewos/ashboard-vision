using SkiaSharp;
using Trofeo.Core.Rendering;

namespace Trofeo.TestDoubles;

/// <summary>
/// Une « carte graphique » de test : ses surfaces sont des surfaces ordinaires du processeur, si bien que les tests de Core
/// tournent aussi sous Linux. <see cref="LoseAfter"/>, <see cref="FailCreate"/> et <see cref="LoseOnSubmit"/> simulent une carte
/// perdue, une création refusée ou une panne silencieuse à l'envoi du travail.
/// </summary>
public sealed class FakeGpuDevice : IGpuDevice
{
    private int? _loseAfter;
    private string? _lostReason;

    /// <summary>Combien de surfaces ont été demandées et créées.</summary>
    public int SurfacesCreated { get; private set; }

    /// <summary>Combien de fois <see cref="CreateSurface"/> a été appelée, refus compris.</summary>
    public int SurfaceRequests { get; private set; }

    /// <summary>Combien de fois <see cref="Submit"/> a été appelée.</summary>
    public int Submits { get; private set; }

    /// <summary>Vrai : chaque demande de surface est refusée (<see cref="GpuLostException"/>), sans que la carte soit perdue.</summary>
    public bool FailCreate { get; set; }

    /// <summary>Vrai : le prochain <see cref="Submit"/> perd la carte sans lever d'exception (le pilote plante en silence).</summary>
    public bool LoseOnSubmit { get; set; }

    /// <summary>La carte est perdue dès que <paramref name="surfaces"/> surfaces ont été créées : la suivante est refusée.</summary>
    public void LoseAfter(int surfaces) => _loseAfter = surfaces;

    /// <summary>Perd la carte tout de suite.</summary>
    public void Lose(string reason = "Carte graphique de test perdue.") => _lostReason ??= reason;

    public string AdapterName => "Carte de test";

    public bool IsLost => _lostReason is not null;

    public string? LostReason => _lostReason;

    public int CacheResources => 0;

    public long CacheBytes => 0;

    public SKSurface CreateSurface(SKImageInfo info)
    {
        SurfaceRequests++;
        if (_loseAfter is { } limit && SurfacesCreated >= limit) Lose();
        if (_lostReason is not null) throw new GpuLostException(_lostReason);
        if (FailCreate) throw new GpuLostException("Création de surface refusée (test).");

        SurfacesCreated++;
        return SKSurface.Create(info) ?? throw new InvalidOperationException("Impossible de créer la surface de test.");
    }

    public void Submit()
    {
        Submits++;
        if (_lostReason is not null) throw new GpuLostException(_lostReason);
        if (LoseOnSubmit) Lose("Carte graphique de test perdue à l'envoi."); // sans exception : l'appelant doit regarder IsLost
    }

    public void PurgeUnused()
    {
    }

    public void Dispose()
    {
    }
}
