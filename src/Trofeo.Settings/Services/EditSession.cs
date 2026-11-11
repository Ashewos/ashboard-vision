using Trofeo.Core.Layout;

namespace Trofeo.Settings.Services;

/// <summary>
/// Les modifications de la disposition, au-dessus de <see cref="LayoutDocument"/> : un seul historique d'annulation
/// (<see cref="LayoutHistory"/>) pour toute la fenêtre, et une seule modification à la fois. Tous les onglets enregistrent par
/// ici, donc Ctrl+Z annule la dernière modification, de quelque onglet qu'elle vienne, sans jamais en défaire une autre en
/// silence. Un changement du fichier en dehors des réglages (<see cref="LayoutDocument.Replaced"/>) vide l'historique.
/// Tout se passe sur le fil de l'interface.
/// </summary>
public sealed class EditSession : IDisposable
{
    private readonly LayoutDocument _document;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LayoutHistory _history = new();
    private int _epoch;
    private int _pending;

    public EditSession(LayoutDocument document)
    {
        _document = document;
        _document.Replaced += OnReplaced;
    }

    public LayoutDocument Document => _document;

    public bool CanUndo => _history.CanUndo;

    public bool CanRedo => _history.CanRedo;

    /// <summary>Une modification est en cours ou attend son tour : la disposition montrée est en train de changer.</summary>
    public bool IsBusy => _pending > 0;

    /// <summary>
    /// Levé quand la disposition du document change (dès qu'elle est écrite, sans attendre la réponse du service), quand
    /// le fichier est relu, quand l'historique change et quand <see cref="IsBusy"/> change.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Le message de la dernière modification (le service l'a refusée, l'écriture a échoué, le fichier avait changé) ;
    /// <c>null</c> si elle a réussi. La fenêtre le montre sous la bande, dans tous les onglets, jusqu'à la modification suivante.
    /// </summary>
    public string? Message { get; private set; }

    /// <summary>Les avertissements du dernier rechargement qui ne regardent pas l'apparence (raccourcis, modules…).</summary>
    public IReadOnlyList<string> OtherWarnings { get; private set; } = [];

    /// <summary>
    /// Les avertissements d'apparence du dernier rechargement qui a changé l'apparence. Le service ne les répète pas tant
    /// qu'elle ne change pas : ils restent donc là (modification de module, annulation d'une autre) jusqu'au prochain
    /// changement d'apparence ou jusqu'à ce que le fichier soit relu.
    /// </summary>
    public AppearanceWarnings AppearanceWarnings { get; private set; } = AppearanceWarnings.Empty;

    /// <summary>
    /// Montre un message sous la bande sans modifier la disposition (un module qui n'a pas pu relire son secret, par
    /// exemple) ; il reste jusqu'à la modification suivante. <c>null</c> l'efface.
    /// </summary>
    public void Report(string? message)
    {
        Message = message;
        Changed?.Invoke();
    }

    /// <summary>Efface le message (un nouveau glissement commence, par exemple).</summary>
    public void ClearMessage()
    {
        if (Message is null)
            return;
        Message = null;
        Changed?.Invoke();
    }

    public void Dispose() => _document.Replaced -= OnReplaced;

    /// <summary>
    /// Une modification : <paramref name="edit"/> reçoit la disposition du moment et rend la nouvelle, ou <c>null</c> (refus,
    /// rien à faire). Une disposition rendue identique à l'actuelle n'écrit rien et n'entre pas dans l'historique. Rend le
    /// résultat de l'enregistrement, ou <c>null</c> si rien n'a été enregistré.
    /// </summary>
    public async Task<SaveOutcome?> ApplyAsync(Func<DashboardLayout, DashboardLayout?> edit)
    {
        Begin();
        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (await RefuseWhileUnreadableAsync().ConfigureAwait(true) is { } refused)
                return refused;

            var current = _document.Current;
            if (edit(current) is not { } edited || Same(current, edited))
            {
                Message = null;
                return null;
            }

            var epoch = _epoch;
            var save = _document.SaveAsync(edited);
            Changed?.Invoke(); // la disposition est écrite : on la montre sans attendre le service
            var outcome = await save.ConfigureAwait(true);
            if (outcome.Saved && epoch == _epoch)
                _history.Push(current);
            Record(current, edited, outcome);
            return outcome;
        }
        finally
        {
            _gate.Release();
            End();
        }
    }

    /// <summary>
    /// Tant que <c>layout.json</c> ne se lit pas, aucune modification n'est écrite (elle écraserait le fichier de l'utilisateur
    /// par « défaut + modification »). Le fichier est relu une fois : s'il se lit maintenant, la modification, calculée sur
    /// la disposition par défaut, est refusée aussi et la disposition relue s'affiche. Rend le refus, ou <c>null</c> si tout va bien.
    /// À appeler sous <see cref="_gate"/>.
    /// </summary>
    private async Task<SaveOutcome?> RefuseWhileUnreadableAsync()
    {
        if (_document.LoadError is null)
            return null;

        var refused = await _document.SaveAsync(_document.Current).ConfigureAwait(true);
        Message = refused.Message;
        Changed?.Invoke();
        return refused;
    }

    /// <summary>
    /// « Repartir de la disposition par défaut » (<see cref="LayoutDocument.ResetToDefaultAsync"/>) : possible seulement quand le
    /// fichier est illisible. L'historique repart de zéro.
    /// </summary>
    public async Task<SaveOutcome?> ResetToDefaultAsync()
    {
        Begin();
        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            var before = _document.Current;
            var save = _document.ResetToDefaultAsync();
            Changed?.Invoke(); // le fichier est écrit : la ligne ambre disparaît sans attendre le service
            var outcome = await save.ConfigureAwait(true);
            if (outcome.Saved)
            {
                _history = new LayoutHistory();
                _epoch++;
            }

            Record(before, _document.Current, outcome);
            return outcome;
        }
        finally
        {
            _gate.Release();
            End();
        }
    }

    /// <summary>
    /// Prie le service de recharger la disposition telle qu'elle est, sans rien y changer ni rien ajouter à l'historique (après la
    /// désinstallation d'un module, par exemple) ; ses avertissements et son message sont gardés comme pour une modification. Si
    /// <c>layout.json</c> ne se lit pas, rien n'est écrit et le motif est donné.
    /// </summary>
    public async Task<SaveOutcome?> ReloadAsync()
    {
        Begin();
        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (await RefuseWhileUnreadableAsync().ConfigureAwait(true) is { } refused)
                return refused;

            var current = _document.Current;
            var save = _document.SaveAsync(current);
            Changed?.Invoke();
            var outcome = await save.ConfigureAwait(true);
            Record(current, current, outcome);
            return outcome;
        }
        finally
        {
            _gate.Release();
            End();
        }
    }

    public Task<SaveOutcome?> UndoAsync() => StepAsync(undo: true);

    public Task<SaveOutcome?> RedoAsync() => StepAsync(undo: false);

    private async Task<SaveOutcome?> StepAsync(bool undo)
    {
        Begin();
        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (await RefuseWhileUnreadableAsync().ConfigureAwait(true) is { } refused)
                return refused;

            var epoch = _epoch;
            var before = _document.Current;
            var target = undo ? _history.Undo(before) : _history.Redo(before);
            if (target is null)
            {
                Message = null;
                return null;
            }

            var save = _document.SaveAsync(target);
            Changed?.Invoke();
            var outcome = await save.ConfigureAwait(true);
            if (!outcome.Saved && epoch == _epoch)
            {
                // Rien n'a été appliqué : l'historique revient comme il était.
                if (undo) _history.Redo(target);
                else _history.Undo(target);
            }

            Record(before, target, outcome);
            return outcome;
        }
        finally
        {
            _gate.Release();
            End();
        }
    }

    /// <summary>
    /// Garde ce que le service a répondu : le message, les avertissements qui ne regardent pas l'apparence (propres à ce
    /// rechargement) et ceux d'apparence (gardés tant que l'apparence ne change pas : le service ne les répète pas).
    /// </summary>
    private void Record(DashboardLayout before, DashboardLayout after, SaveOutcome outcome)
    {
        Message = outcome.Message;
        if (!outcome.Saved)
            return;

        var (appearance, other) = WarningRouter.Route(outcome.Warnings);
        OtherWarnings = other;
        // Le service décide sur l'apparence qu'il a lui-même appliquée (redémarré, fichier réécrit à la main) : s'il en
        // signale une, c'est qu'il a reconstruit le thème, même si la fenêtre n'a rien changé. Sans avertissement et sans
        // changement d'apparence, il ne répète rien : on garde ce qu'on avait.
        if (!appearance.IsEmpty || !Equals(before.Appearance, after.Appearance))
            AppearanceWarnings = appearance;
    }

    private void Begin()
    {
        _pending++;
        if (_pending == 1)
            Changed?.Invoke();
    }

    private void End()
    {
        _pending--;
        Changed?.Invoke();
    }

    private void OnReplaced()
    {
        // Le fichier a changé en dehors des réglages : l'historique ne vaut plus rien.
        _history = new LayoutHistory();
        _epoch++;
        AppearanceWarnings = AppearanceWarnings.Empty; // l'apparence a pu changer : ces avertissements ne valent plus rien
        Changed?.Invoke();
    }

    /// <summary>Même disposition, élément par élément (le moteur d'édition rend une disposition égale mais neuve).</summary>
    internal static bool Same(DashboardLayout a, DashboardLayout b) =>
        a.Rotation == b.Rotation
        && a.MaxFps == b.MaxFps
        && Equals(a.Appearance, b.Appearance)
        && ReferenceEquals(a.Shortcuts, b.Shortcuts)
        && a.Views.Count == b.Views.Count
        && a.Views.Zip(b.Views).All(pair =>
            pair.First.Id == pair.Second.Id && pair.First.Name == pair.Second.Name
            && pair.First.Placements.SequenceEqual(pair.Second.Placements))
        && (a.Shelf ?? []).SequenceEqual(b.Shelf ?? []);
}
