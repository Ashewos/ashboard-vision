using System.Diagnostics;
using System.Security.Cryptography;

namespace Trofeo.Distribution;

/// <summary>Où en sont les mises à jour.</summary>
public enum UpdateState
{
    /// <summary>Rien n'a encore été vérifié depuis le démarrage.</summary>
    Unknown,
    Checking,
    UpToDate,

    /// <summary>Tu es sur une bêta et le canal stable n'a encore aucune version.</summary>
    NoStableYet,
    Available,
    Downloading,

    /// <summary>L'installateur est lancé : il va arrêter le service et le remplacer.</summary>
    Installing,

    /// <summary>La dernière opération a échoué ; <see cref="UpdateStatus.Message"/> dit pourquoi.</summary>
    Failed,
}

/// <summary>
/// Ce que la fenêtre et l'icône de notification affichent. <paramref name="Version"/> est celle qui est proposée,
/// <paramref name="IsDowngrade"/> dit qu'elle est plus ancienne que l'installée, <paramref name="Percent"/> n'a de sens
/// que pendant le téléchargement et <paramref name="Message"/> est écrit pour être lu tel quel.
/// </summary>
public sealed record UpdateStatus(
    UpdateState State, string? Version, bool IsDowngrade, int? Percent, string? Message, UpdatePreferences Preferences);

/// <summary>
/// Les mises à jour, de la vérification au lancement de l'installateur. Une opération à la fois (vérifier, régler,
/// installer) : les autres attendent leur tour, sauf la vérification du minuteur, qui laisse sa place à ce qui tourne.
/// Rien ne s'exécute entre deux vérifications.
/// </summary>
/// <param name="installed">La version qui tourne.</param>
/// <param name="settingsDir">Le dossier des réglages : on y pose le repère de réouverture, et on le copie avant un retour en arrière.</param>
/// <param name="backupsRoot">Où ranger les copies des réglages.</param>
/// <param name="downloadDir">Où télécharger l'installateur ; vidé avant chaque téléchargement.</param>
public sealed class UpdateService(
    UpdateChecker checker, UpdateDownloader downloader, IInstallerLauncher launcher, UpdatePreferencesStore prefs,
    AppVersion installed, string settingsDir, string backupsRoot, string downloadDir, TimeProvider time)
    : IDisposable
{
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    // Une opération à la fois, comme _reloadGate dans ServiceControlHandler.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    // Sous _gate : les choix en vigueur et la version que la dernière vérification propose (null : aucune).
    private UpdatePreferences _preferences = prefs.Load();
    private UpdateOffer? _offer;

    private volatile UpdateStatus _current = new(UpdateState.Unknown, null, false, null, null, UpdatePreferences.Default);
    private ITimer? _timer;
    private bool _disposed;

    public UpdateStatus Current => _current with { Preferences = _preferences };

    /// <summary>Levé à chaque changement de <see cref="Current"/>, sur le fil qui l'a provoqué.</summary>
    public event EventHandler? Changed;

    /// <summary>Première vérification 60 s plus tard, puis toutes les 24 h. Sans effet si le service est déjà démarré.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _timer ??= time.CreateTimer(_ => _ = CheckScheduledAsync(), null, FirstCheckDelay, CheckInterval);
    }

    /// <summary>Vérifie tout de suite. Attend son tour si une autre opération est en cours.</summary>
    public async Task CheckNowAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await CheckAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Enregistre tes choix, puis vérifie de nouveau : ils changent ce qui est proposé.</summary>
    public async Task SetPreferencesAsync(UpdatePreferences preferences, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try
            {
                prefs.Save(preferences);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Publish(UpdateState.Failed, $"Tes choix de mise à jour n'ont pas pu être enregistrés : {ex.Message}");
                return;
            }

            _preferences = preferences;
            await CheckAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Télécharge la version proposée, vérifie son empreinte puis lance l'installateur. Un retour à une version plus
    /// ancienne demande <paramref name="confirmDowngrade"/> et copie les réglages avant de lancer. Avec
    /// <paramref name="reopenSettings"/>, le repère de réouverture de la fenêtre est posé avant le lancement.
    /// Rend <c>Ok</c> vrai quand l'installateur est lancé (il arrête ensuite le service) ; sinon <c>Message</c> dit pourquoi.
    /// </summary>
    public async Task<(bool Ok, string Message)> InstallAsync(bool confirmDowngrade, bool reopenSettings, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Une seconde demande (deux fenêtres, fenêtre et icône) arrive ici une fois la première finie : l'installateur est lancé et
            // son fichier en cours d'emploi, on ne télécharge ni ne lance rien de plus.
            if (_current.State is UpdateState.Downloading or UpdateState.Installing)
            {
                return (false, "L'installation de la mise à jour est déjà lancée.");
            }

            if (_offer is not { } offer)
            {
                return (false, "Il n'y a pas de mise à jour à installer : vérifie d'abord les mises à jour.");
            }

            if (offer.IsDowngrade && !confirmDowngrade)
            {
                return (false, "Revenir à une version plus ancienne demande ta confirmation.");
            }

            return await InstallOfferAsync(offer, reopenSettings, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _timer?.Dispose();
    }

    private async Task<(bool Ok, string Message)> InstallOfferAsync(UpdateOffer offer, bool reopenSettings, CancellationToken ct)
    {
        DownloadedSetup downloaded;
        PublishDownload(offer, 0);
        try
        {
            downloaded = await downloader.DownloadVerifiedAsync(offer.Release, downloadDir, new StepProgress(f => PublishDownload(offer, f)), ct)
                .ConfigureAwait(false);
        }
        catch (UpdateException ex)
        {
            return Fail(offer, ex.Message);
        }
        catch (OperationCanceledException)
        {
            PublishAvailable(offer, null);
            throw;
        }
        catch (Exception ex)
        {
            // Une panne qu'on n'a pas prévue ne doit pas laisser l'état figé sur « Téléchargement » : le détail va au journal.
            Trace.TraceError($"Mise à jour : le téléchargement a échoué de façon inattendue : {ex}");
            return Fail(offer, "Le téléchargement a échoué de façon inattendue : la mise à jour n'est pas installée.");
        }

        var setupPath = downloaded.Path;
        if (offer.IsDowngrade)
        {
            try
            {
                SettingsBackup.Create(settingsDir, backupsRoot, installed, time.GetLocalNow());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DeleteQuietly(setupPath);
                return Fail(offer, $"La copie des réglages a échoué ({ex.Message.TrimEnd('.')}) : la mise à jour est annulée et tes réglages n'ont pas été touchés.");
            }
        }

        if (reopenSettings)
        {
            try
            {
                ReopenSettingsMarker.Create(settingsDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // La fenêtre ne se rouvrira pas toute seule, mais la mise à jour peut se faire.
                Trace.TraceWarning($"Mise à jour : le repère de réouverture n'a pas pu être posé : {ex.Message}");
            }
        }

        const string installing = "Windows va demander l'autorisation d'installer la mise à jour.";
        Publish(UpdateState.Installing, installing, offer.Release.Version.ToString(), offer.IsDowngrade);

        // Le fichier a attendu dans un dossier temporaire (la sauvegarde, la réponse à Windows) : on revérifie son empreinte juste avant de
        // le passer au lanceur, qui l'exécute avec des droits d'administrateur.
        if (!await StillMatchesAsync(setupPath, downloaded.Sha256).ConfigureAwait(false))
        {
            if (reopenSettings)
            {
                ReopenSettingsMarker.Consume(settingsDir);
            }

            DeleteQuietly(setupPath);
            return Fail(offer, "L'installateur téléchargé a changé avant son lancement : par prudence, il n'est pas installé.");
        }

        InstallerLaunch launch;
        try
        {
            // Le lancement peut attendre ta réponse à Windows : pas sur le fil de l'appelant (la fenêtre).
            launch = await Task.Run(() => launcher.Launch(setupPath), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.TraceError($"Mise à jour : l'installateur n'a pas pu être lancé : {ex}");
            launch = InstallerLaunch.Failed;
        }

        if (launch == InstallerLaunch.Started)
        {
            // L'installateur tourne : l'offre est consommée (une seconde demande n'a rien à relancer).
            _offer = null;
            return (true, installing);
        }

        // L'installateur n'a pas démarré : ni repère de réouverture, ni fichier téléchargé à garder.
        if (reopenSettings)
        {
            ReopenSettingsMarker.Consume(settingsDir);
        }

        DeleteQuietly(setupPath);
        if (launch == InstallerLaunch.Cancelled)
        {
            const string cancelled = "Mise à jour annulée : l'autorisation n'a pas été donnée.";
            PublishAvailable(offer, cancelled);
            return (false, cancelled);
        }

        return Fail(offer, "L'installateur n'a pas pu être lancé.");
    }

    // Vrai si le fichier a toujours l'empreinte vérifiée au téléchargement. Un fichier illisible vaut « changé ».
    private static async Task<bool> StillMatchesAsync(string path, string expectedSha256)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
            return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Le minuteur : si une autre opération est en cours, elle compte aussi comme une vérification, on passe.
    private async Task CheckScheduledAsync()
    {
        if (_lifetime.IsCancellationRequested || !_gate.Wait(0))
        {
            return;
        }

        try
        {
            await CheckAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Le service s'arrête.
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Mise à jour : la vérification planifiée a échoué : {ex}");
        }
        finally
        {
            _gate.Release();
        }
    }

    // Sous _gate. Une panne passe dans l'état, jamais en exception : le minuteur continue.
    private async Task CheckAsync(CancellationToken ct)
    {
        var before = _current;
        Publish(UpdateState.Checking, null);
        try
        {
            var releases = await checker.FetchAsync(ct).ConfigureAwait(false);
            var decision = UpdatePolicy.Choose(releases, installed, _preferences);
            _offer = decision.Offer;
            if (decision.Offer is { } offer)
            {
                PublishAvailable(offer, null);
            }
            else if (decision.NoStableYet)
            {
                Publish(UpdateState.NoStableYet, "Aucune version stable pour l'instant.");
            }
            else
            {
                Publish(UpdateState.UpToDate, "Tu as la dernière version.");
            }
        }
        catch (OperationCanceledException)
        {
            _current = before;
            RaiseChanged();
            throw;
        }
        catch (Exception ex)
        {
            if (ex is not UpdateException)
            {
                Trace.TraceError($"Mise à jour : la vérification a échoué : {ex}");
            }

            _offer = null;
            Publish(UpdateState.Failed, $"Impossible de vérifier les mises à jour : {ex.Message}");
        }
    }

    private void PublishAvailable(UpdateOffer offer, string? message)
    {
        var version = offer.Release.Version.ToString();
        Publish(UpdateState.Available, message ?? $"La version {version} est disponible.", version, offer.IsDowngrade);
    }

    private void PublishDownload(UpdateOffer offer, double fraction)
    {
        var percent = Math.Clamp((int)Math.Floor(fraction * 100), 0, 100);
        // Un pas de téléchargement tous les 80 Ko : on ne prévient que quand le pourcentage change.
        if (_current.State == UpdateState.Downloading && _current.Percent == percent)
        {
            return;
        }

        Publish(UpdateState.Downloading, $"Téléchargement… {percent} %", offer.Release.Version.ToString(), offer.IsDowngrade, percent);
    }

    private (bool Ok, string Message) Fail(UpdateOffer offer, string message)
    {
        Publish(UpdateState.Failed, message, offer.Release.Version.ToString(), offer.IsDowngrade);
        return (false, message);
    }

    private void Publish(UpdateState state, string? message, string? version = null, bool isDowngrade = false, int? percent = null)
    {
        _current = new UpdateStatus(state, version, isDowngrade, percent, message, _preferences);
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // Un abonné qui plante ne doit pas arrêter les mises à jour.
            Trace.TraceError($"Mise à jour : un abonné à Changed a échoué : {ex}");
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Un fichier de plus dans le dossier temporaire : il sera vidé au prochain téléchargement.
        }
    }

    /// <summary>Un <see cref="IProgress{T}"/> qui répond tout de suite, sur le fil du téléchargement (pas de contexte de synchronisation).</summary>
    private sealed class StepProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
