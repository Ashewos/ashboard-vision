using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Trofeo.Control;
using Trofeo.Settings.Services;

namespace Trofeo.Settings.ViewModels;

/// <summary>Une ligne du tableau « Consommation des modules ».</summary>
public sealed class ConsumptionRow
{
    private const double Megabyte = 1024 * 1024;

    internal ConsumptionRow(ModuleConsumption source)
    {
        // Le nom vient d'un module (ou du service) : donnée non fiable, nettoyée ; l'identifiant le remplace s'il ne reste rien.
        var name = UntrustedText.Line(source.DisplayName);
        Name = name.Length > 0 ? name : UntrustedText.Line(source.Id);
        CpuText = string.Format(Texts.ConsumptionCpuValue, source.CpuPercent.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ','));
        MemoryText = string.Format(
            Texts.ConsumptionMemoryValue, ((long)Math.Round(source.MemoryBytes / Megabyte, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture));
        IsHigh = source.High;
    }

    public string Name { get; }
    public string CpuText { get; }
    public string MemoryText { get; }

    /// <summary>Le service juge la consommation durablement élevée : le repère orange « Consommation élevée » s'affiche.</summary>
    public bool IsHigh { get; }

    public string HighText => Texts.ConsumptionHigh;
}

/// <summary>
/// Le tableau « Consommation des modules » : une ligne pour les modules d'origine (avec le service), une par module complémentaire.
/// Il est relu au moment où il devient visible, puis toutes les 5 secondes, <b>seulement tant qu'il est réellement visible</b> : onglet
/// Paramètres affiché, fenêtre ni réduite ni cachée, liaison avec le service établie, tableau déplié. Sinon il n'y a ni requête ni
/// minuteur (la boucle d'attente n'existe pas).
/// </summary>
public sealed partial class ModuleConsumptionViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly IServiceLink _link;
    private readonly TimeProvider _time;
    private bool _tabShown;
    private bool _windowShown = true;
    private bool _disposed;
    private CancellationTokenSource? _polling;

    public ModuleConsumptionViewModel(IServiceLink link, TimeProvider? time = null)
    {
        _link = link;
        _time = time ?? TimeProvider.System;
        _link.Changed += Update;
    }

    /// <summary>Les lignes, dans l'ordre du service : les modules d'origine d'abord.</summary>
    public ObservableCollection<ConsumptionRow> Rows { get; } = [];

    /// <summary>Une lecture est en cours de répétition (un minuteur existe). Faux dès que le tableau n'est plus visible.</summary>
    public bool IsPolling => _polling is not null;

    /// <summary>Le tableau est déplié ; replié, plus aucune mesure n'est demandée.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleText))]
    private bool _isExpanded = true;

    partial void OnIsExpandedChanged(bool value) => Update();

    public string ToggleText => IsExpanded ? Texts.ConsumptionHide : Texts.ConsumptionShow;

    /// <summary>« Masquer » / « Afficher » : replie le tableau (plus aucune mesure) ou le déplie (relu tout de suite).</summary>
    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    /// <summary>La dernière lecture a échoué (le message du service, nettoyé) ; les lignes d'avant restent affichées.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailure))]
    private string _failure = "";

    public bool HasFailure => Failure.Length > 0;

    /// <summary>L'onglet Paramètres est affiché (et rien ne le recouvre : assistant, panneau « service arrêté »).</summary>
    public void SetTabShown(bool shown)
    {
        _tabShown = shown;
        Update();
    }

    /// <summary>La fenêtre est visible (ni réduite ni cachée).</summary>
    public void SetWindowShown(bool shown)
    {
        _windowShown = shown;
        Update();
    }

    private void Update()
    {
        var wanted = !_disposed && _tabShown && _windowShown && IsExpanded && _link.State == LinkState.Connected;
        if (wanted == IsPolling)
            return;

        if (wanted)
        {
            var polling = new CancellationTokenSource();
            _polling = polling;
            OnPropertyChanged(nameof(IsPolling));
            _ = PollAsync(polling.Token);
        }
        else
        {
            var polling = _polling;
            _polling = null;
            OnPropertyChanged(nameof(IsPolling));
            polling?.Cancel();
            polling?.Dispose();
        }
    }

    // Une lecture tout de suite, puis une toutes les 5 secondes, jusqu'à ce que le tableau ne soit plus visible.
    private async Task PollAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await ReadAsync(ct).ConfigureAwait(true);
                await Task.Delay(Interval, _time, ct).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // Le tableau n'est plus visible : la boucle s'arrête.
        }
    }

    private async Task ReadAsync(CancellationToken ct)
    {
        IReadOnlyList<ModuleConsumption> rows;
        try
        {
            rows = await _link.GetConsumptionAsync().ConfigureAwait(true);
        }
        catch (ControlException ex)
        {
            if (!ct.IsCancellationRequested)
            {
                var message = UntrustedText.Block(ex.Message);
                Failure = message.Length > 0 ? message : Texts.ConsumptionFailed;
            }

            return;
        }

        if (ct.IsCancellationRequested)
            return; // la réponse arrive après que le tableau a été masqué : elle n'intéresse plus personne

        Failure = "";
        Rows.Clear();
        foreach (var row in rows)
            Rows.Add(new ConsumptionRow(row));
    }

    public void Dispose()
    {
        _disposed = true;
        _link.Changed -= Update;
        Update();
    }
}
