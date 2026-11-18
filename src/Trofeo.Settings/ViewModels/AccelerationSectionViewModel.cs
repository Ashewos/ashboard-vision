using CommunityToolkit.Mvvm.ComponentModel;
using Trofeo.Control;
using Trofeo.Core.Layout;
using Trofeo.Settings.Services;

namespace Trofeo.Settings.ViewModels;

/// <summary>Une ligne de la liste des cartes : <paramref name="id"/> est ce que l'on enregistre (<c>null</c> : Automatique).</summary>
public sealed class AccelerationAdapterItem(GpuAdapterId? id, string label)
{
    public GpuAdapterId? Id { get; } = id;

    public string Label { get; } = label;

    public override string ToString() => Label;
}

/// <summary>
/// La section « Accélération matérielle » de l'onglet Paramètres : l'interrupteur général, la carte (Automatique ou l'une de celles
/// que le service a trouvées), les cases « Dessin de l'image » et « Décodage des vidéos », et une ligne qui dit où l'on en est. Les
/// choix viennent du fichier (<see cref="EditSession"/> : même historique que les autres réglages, écrits tout de suite, le service
/// relit) ; l'état, la liste des cartes et la phrase de problème viennent du service et sont montrés tels quels. Des réglages qui
/// redeviennent ceux d'origine ne s'écrivent pas : le fichier n'en parle plus.
/// </summary>
public sealed class AccelerationSectionViewModel : ObservableObject, IDisposable
{
    private readonly IServiceLink _link;
    private readonly EditSession _session;
    private IReadOnlyList<GpuAdapterEntry> _listed = [];
    private IReadOnlyList<AccelerationAdapterItem> _entries;
    private Shown _shown;

    public AccelerationSectionViewModel(IServiceLink link, EditSession session)
    {
        _link = link;
        _session = session;
        _entries = BuildEntries();
        _shown = Read();
        _link.Changed += OnChanged;
        _session.Changed += OnChanged;
    }

    public void Dispose()
    {
        _link.Changed -= OnChanged;
        _session.Changed -= OnChanged;
    }

    // ---- Ce que la section sait ----

    /// <summary>L'état que le service donne (<c>null</c> : liaison coupée ou service d'avant le protocole 5).</summary>
    private AccelerationInfo? Info => _link.Status?.Acceleration;

    /// <summary>Les réglages du fichier (ceux d'origine quand il n'en dit rien).</summary>
    private AccelerationSettings Saved => _session.Document.Current.Acceleration ?? AccelerationSettings.Default;

    /// <summary>L'interrupteur marche : le service dit qu'une carte compatible existe.</summary>
    public bool CanSwitch => Info is { Available: true };

    /// <summary>La liste et les deux cases marchent : l'interrupteur marche et il est allumé.</summary>
    public bool CanChooseDetails => CanSwitch && Saved.Enabled;

    // ---- Les choix (le fichier) ----

    /// <summary>Utiliser la carte graphique. Le changer écrit la disposition.</summary>
    public bool Enabled
    {
        get => Saved.Enabled;
        set
        {
            if (value != Saved.Enabled)
                _ = SetEnabledAsync(value);
        }
    }

    /// <summary>« Dessin de l'image ».</summary>
    public bool Drawing
    {
        get => Saved.Drawing;
        set
        {
            if (value != Saved.Drawing)
                _ = SetDrawingAsync(value);
        }
    }

    /// <summary>« Décodage des vidéos ».</summary>
    public bool VideoDecoding
    {
        get => Saved.VideoDecoding;
        set
        {
            if (value != Saved.VideoDecoding)
                _ = SetVideoDecodingAsync(value);
        }
    }

    /// <summary>Automatique d'abord, puis chaque carte dans l'ordre du service. Refaite seulement quand la liste du service change.</summary>
    public IReadOnlyList<AccelerationAdapterItem> Entries => _entries;

    /// <summary>
    /// La ligne choisie : celle de la carte enregistrée, ou Automatique quand rien n'est enregistré ou que la carte enregistrée n'est
    /// plus dans la liste. <c>null</c> est ignoré (la liste déroulante le pose quand on refait sa liste).
    /// </summary>
    public AccelerationAdapterItem? SelectedAdapter
    {
        get => Find(Saved.Adapter);
        set
        {
            if (value is not null)
                _ = SetAdapterAsync(value);
        }
    }

    public Task SetEnabledAsync(bool enabled) => EditAsync(settings => settings with { Enabled = enabled });

    public Task SetDrawingAsync(bool drawing) => EditAsync(settings => settings with { Drawing = drawing });

    public Task SetVideoDecodingAsync(bool video) => EditAsync(settings => settings with { VideoDecoding = video });

    /// <summary>Choisit une ligne de <see cref="Entries"/> : Automatique n'enregistre aucune carte.</summary>
    public Task SetAdapterAsync(AccelerationAdapterItem item) => EditAsync(settings => settings with { Adapter = item.Id });

    private async Task EditAsync(Func<AccelerationSettings, AccelerationSettings> change)
    {
        try
        {
            await _session.ApplyAsync(layout =>
            {
                var current = layout.Acceleration ?? AccelerationSettings.Default;
                var next = change(current);
                if (next == current)
                    return null;
                return layout with { Acceleration = next == AccelerationSettings.Default ? null : next };
            }).ConfigureAwait(true);
        }
        finally
        {
            Refresh(force: true); // une case cochée mais pas enregistrée (écriture refusée) revient à ce que dit le fichier
        }
    }

    // ---- L'état (le service) ----

    /// <summary>
    /// Une phrase sous les choix : où l'on en est. <c>null</c> sans réponse du service. Sur un fond fixe, elle dit que le processeur dessine
    /// et que la carte prend le relais sur un GIF ou une vidéo (sauf avec un problème : sa phrase passe avant, voir <see cref="Problem"/>).
    /// </summary>
    public string? StateText => Info switch
    {
        null => null,
        { Available: false } => Texts.AccelerationNone,
        { DrawingWaitsForAnimation: true, Problem: null or "" } => Texts.AccelerationStateFixedBackground, // une panne garde la priorité : sa phrase est montrée à part
        { AdapterName: { } card, Drawing: true, VideoDecoding: true } => string.Format(Texts.AccelerationStateBoth, card),
        { AdapterName: { } card, Drawing: true } => string.Format(Texts.AccelerationStateDrawing, card),
        { AdapterName: { } card, VideoDecoding: true } => string.Format(Texts.AccelerationStateVideo, card),
        _ => Texts.AccelerationStateCpu,
    };

    public bool HasStateText => StateText is not null;

    /// <summary>La phrase du service quand quelque chose ne va pas (panne, carte absente, carte trop lente), montrée telle quelle.</summary>
    public string? Problem => Info is { Available: true } info ? info.Problem : null;

    public bool HasProblem => !string.IsNullOrEmpty(Problem);

    // ---- Mise à jour ----

    private void OnChanged() => Refresh(force: false);

    private void Refresh(bool force)
    {
        var rebuilt = !(Info?.Adapters ?? []).SequenceEqual(_listed);
        if (rebuilt)
            _entries = BuildEntries();

        var now = Read();
        if (!force && !rebuilt && now == _shown)
            return;
        _shown = now;

        if (rebuilt)
            OnPropertyChanged(nameof(Entries)); // avant la ligne choisie : la liste déroulante la cherche dans la nouvelle liste
        OnPropertyChanged(nameof(SelectedAdapter));
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(Drawing));
        OnPropertyChanged(nameof(VideoDecoding));
        OnPropertyChanged(nameof(CanSwitch));
        OnPropertyChanged(nameof(CanChooseDetails));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(HasStateText));
        OnPropertyChanged(nameof(Problem));
        OnPropertyChanged(nameof(HasProblem));
    }

    /// <summary>Ce que la section montre, pour ne rien notifier tant que rien n'a changé (l'état est relu chaque seconde).</summary>
    private sealed record Shown(
        bool CanSwitch, bool Enabled, bool Drawing, bool Video, AccelerationAdapterItem? Selected, string? State, string? Problem);

    private Shown Read() => new(CanSwitch, Saved.Enabled, Saved.Drawing, Saved.VideoDecoding, Find(Saved.Adapter), StateText, Problem);

    private IReadOnlyList<AccelerationAdapterItem> BuildEntries()
    {
        _listed = Info?.Adapters ?? [];
        var auto = _listed.Count > 0 ? string.Format(Texts.AccelerationAutoNamed, _listed[0].Name) : Texts.AccelerationAuto;
        return
        [
            new AccelerationAdapterItem(null, auto),
            .. _listed.Select(a => new AccelerationAdapterItem(new GpuAdapterId(a.VendorId, a.DeviceId, a.SubSysId, a.Name), a.Name)),
        ];
    }

    /// <summary>La ligne de la carte enregistrée, retrouvée comme le fait le service (identifiants, puis nom) ; Automatique sinon.</summary>
    private AccelerationAdapterItem Find(GpuAdapterId? saved)
    {
        if (saved is null)
            return _entries[0];

        var cards = _entries.Skip(1);
        return cards.FirstOrDefault(e => e.Id!.VendorId == saved.VendorId && e.Id.DeviceId == saved.DeviceId && e.Id.SubSysId == saved.SubSysId)
               ?? cards.FirstOrDefault(e => string.Equals(e.Id!.Name, saved.Name, StringComparison.OrdinalIgnoreCase))
               ?? _entries[0];
    }
}
