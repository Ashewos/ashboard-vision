using System.ComponentModel;
using System.Text.Json;
using AshboardVision.Sdk;
using Microsoft.Win32.SafeHandles;
using SkiaSharp;
using Trofeo.Core.Rendering;
using Trofeo.Extensions.Protocol;
using Trofeo.ModuleKit;

namespace Trofeo.ModuleHost;

/// <summary>
/// Une séance de l'hôte : lit les messages du service, charge le module, ouvre et ferme les instances, répond aux recherches
/// et aux actions. La boucle de lecture ne fait jamais de travail du module ni d'envoi qui bloque : tout cela part sur des fils du pool.
/// </summary>
internal sealed class HostSession
{
    private const int ExitFailure = 3;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);
    private static readonly JsonElement EmptySettings = JsonDocument.Parse("{}").RootElement.Clone();
    private static readonly JsonElement JsonNull = JsonSerializer.SerializeToElement<object?>(null);

    private readonly MessageChannel _channel;
    private readonly HostLink _link;
    private readonly ISystemFonts _fonts = new LazySystemFonts();
    private readonly Dictionary<Guid, InstanceRenderer> _instances = [];
    private readonly List<Task> _background = [];
    private readonly CancellationTokenSource _stop = new();
    private HostContext? _context;
    private string _requiredSdk = "1.0";

    private HostSession(MessageChannel channel)
    {
        _channel = channel;
        _link = new HostLink(channel);
    }

    /// <summary>Fait toute la séance et renvoie le code de sortie : 0 si le service a fini ou fermé le tube, 3 si l'hôte a dû s'arrêter (avec un <c>Failure</c>).</summary>
    public static async Task<int> RunAsync(Stream input, Stream output)
    {
        await using var channel = new MessageChannel(input, output);
        var session = new HostSession(channel);
        return await session.RunAsync().ConfigureAwait(false);
    }

    private async Task<int> RunAsync()
    {
        var code = 0;
        try
        {
            await using var messages = _channel.ReadAllAsync(_stop.Token).GetAsyncEnumerator(_stop.Token);
            if (!await messages.MoveNextAsync().ConfigureAwait(false))
            {
                return await EndAsync(0).ConfigureAwait(false); // le service est parti avant de dire quoi que ce soit
            }

            if (messages.Current is not Hello hello)
            {
                throw new ServiceMistake("Le premier message du service doit être Hello.");
            }

            _context = Initialize(hello);
            _link.Post(new Ready(_requiredSdk));

            while (await messages.MoveNextAsync().ConfigureAwait(false))
            {
                if (!Handle(messages.Current))
                {
                    break; // Shutdown
                }
            }
        }
        catch (ModuleLoadException ex)
        {
            _link.Post(new Failure(HostLink.Clip(ex.Message)));
            code = ExitFailure;
        }
        catch (ServiceMistake ex)
        {
            _link.Post(new Failure(HostLink.Clip(ex.Message)));
            code = ExitFailure;
        }
        catch (InvalidDataException ex)
        {
            _link.Post(new Failure(HostLink.Clip("Message illisible du service : " + ex.Message)));
            code = ExitFailure;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Une panne de l'hôte lui-même (dossier illisible, polices…) : le service doit le savoir plutôt que voir un hôte muet.
            _link.Post(new Failure(HostLink.Clip("Erreur de l'hôte de module : " + ex.Message)));
            code = ExitFailure;
        }

        return await EndAsync(code).ConfigureAwait(false);
    }

    /// <summary>Valide le <see cref="Hello"/>, charge le module et prépare ce que partagent les instances.</summary>
    private HostContext Initialize(Hello hello)
    {
        if (!TryMajor(hello.Protocol, out var major) || !TryMajor(HostMessage.ProtocolVersion, out var ours) || major != ours)
        {
            throw new ServiceMistake($"Version du protocole non prise en charge : {hello.Protocol} (cet hôte parle la {HostMessage.ProtocolVersion}).");
        }

        if (hello.MaxFps <= 0)
        {
            throw new ServiceMistake("Le plafond d'images par seconde doit être positif.");
        }

        if (string.IsNullOrWhiteSpace(hello.ModuleDirectory) || string.IsNullOrWhiteSpace(hello.Entry))
        {
            throw new ServiceMistake("Le dossier et le fichier d'entrée du module sont obligatoires.");
        }

        var loaded = ModuleLoader.Load(hello.ModuleDirectory, hello.Entry);
        _requiredSdk = loaded.RequiredSdk;
        return new HostContext(_link, loaded.ModuleType, [.. hello.Granted.Distinct()], hello.DataDirectory, ResolveTheme(hello.Appearance), hello.MaxFps);
    }

    /// <summary>Traite un message. Faux pour s'arrêter proprement (<see cref="Shutdown"/>).</summary>
    private bool Handle(HostMessage message)
    {
        switch (message)
        {
            case Open open:
                OpenInstance(open);
                break;
            case Close close:
                CloseInstance(close.InstanceId);
                break;
            case FrameDone done:
                if (_instances.TryGetValue(done.InstanceId, out var frameOwner))
                {
                    frameOwner.FrameDone(done.Buffer);
                }

                break;
            case Visibility visibility:
                if (_instances.TryGetValue(visibility.InstanceId, out var visibleOwner))
                {
                    visibleOwner.SetVisible(visibility.Visible); // une instance inconnue (fermée entre-temps) est ignorée
                }

                break;
            case Shortcut shortcut:
                if (_instances.TryGetValue(shortcut.InstanceId, out var shortcutOwner))
                {
                    shortcutOwner.DeliverShortcut(shortcut.ShortcutId); // sans vérifier la liste du manifeste ni la visibilité : le service filtre
                }

                break;
            case AppearanceChanged changed:
                _context!.Theme = ResolveTheme(changed.Appearance);
                foreach (var instance in _instances.Values)
                {
                    instance.Redraw();
                }

                break;
            case Limits limits:
                if (limits.MaxFps <= 0)
                {
                    throw new ServiceMistake("Le plafond d'images par seconde doit être positif.");
                }

                _context!.MaxFps = limits.MaxFps;
                foreach (var instance in _instances.Values)
                {
                    instance.LimitChanged();
                }

                break;
            case Lookup lookup:
                Background(() => AnswerLookupAsync(lookup));
                break;
            case RunAction action:
                Background(() => AnswerActionAsync(action));
                break;
            case SecretValue secret:
                _link.TryComplete(secret.Id, secret);
                break;
            case DataValue data:
                _link.TryComplete(data.Id, data);
                break;
            case LocalRefused refused:
                _link.TryComplete(refused.Id, refused);
                break;
            case LocalConnected connected:
                if (!_link.TryComplete(connected.Id, connected))
                {
                    HostBackend.CloseRelay(connected); // personne n'attend plus cette connexion : les poignées reçues sont fermées
                }

                break;
            case Shutdown:
                return false;
        }

        // Tout le reste (un second Hello, un message qui n'est fait que pour le service) n'a rien à faire ici : ignoré.
        return true;
    }

    // --- Instances -------------------------------------------------------------------------------------------------------------

    private void OpenInstance(Open open)
    {
        if (open.Width <= 0 || open.Height <= 0 || open.Columns <= 0 || open.Rows <= 0 || !float.IsFinite(open.TextScale) || open.TextScale <= 0
            || open.Buffer0 <= 0)
        {
            throw new ServiceMistake("Open : taille, damier, échelle de texte ou images partagées invalides.");
        }

        if (_instances.ContainsKey(open.InstanceId))
        {
            throw new ServiceMistake("Open : cette instance est déjà ouverte.");
        }

        IntPtr view;
        try
        {
            FrameView.BufferBytes(open.Width, open.Height); // refuse une image démesurée
            view = FrameView.Map(open.Buffer0, open.Width, open.Height);
        }
        catch (Exception ex) when (ex is ArgumentException or Win32Exception)
        {
            throw new ServiceMistake($"Open : images partagées inutilisables ({ex.Message}).");
        }
        finally
        {
            // La projection reste valable sans la poignée de la section : on la ferme.
            new SafeFileHandle(new IntPtr(open.Buffer0), ownsHandle: true).Dispose();
        }

        InstanceRenderer instance;
        try
        {
            instance = new InstanceRenderer(_context!, open, view);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            FrameView.Unmap(view);
            throw new ServiceMistake($"Open : impossible de dessiner dans les images partagées ({ex.Message}).");
        }

        _instances[open.InstanceId] = instance;
        instance.Start();
    }

    private void CloseInstance(Guid id)
    {
        if (_instances.Remove(id, out var instance))
        {
            Background(async () => await instance.DisposeAsync().ConfigureAwait(false));
        }
    }

    // --- Recherches et actions des réglages ----------------------------------------------------------------------------------

    private async Task AnswerLookupAsync(Lookup lookup)
    {
        IReadOnlyList<LookupResult> results = [];
        try
        {
            await WithModuleAsync(EmptySettings, Guid.Empty, async (module, ct) =>
            {
                var found = await module.LookupAsync(lookup.FieldKey, lookup.Query, ct).ConfigureAwait(false);
                results = found is null ? [] : [.. found.Where(r => r is not null).Select(Sanitize)];
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ReportSettingsFailure("la recherche", ex);
            results = [];
        }

        _link.Post(new LookupReply(lookup.Id, results), new LookupReply(lookup.Id, []));
    }

    private async Task AnswerActionAsync(RunAction action)
    {
        var result = new ActionResult(false, "Erreur du module.");
        try
        {
            await WithModuleAsync(action.Settings.ValueKind == JsonValueKind.Object ? action.Settings : EmptySettings, action.InstanceId, async (module, ct) =>
            {
                var done = await module.RunActionAsync(action.ActionKey, ct).ConfigureAwait(false);
                if (done is not null)
                {
                    result = new ActionResult(done.Ok, HostLink.Clip(done.Message));
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ReportSettingsFailure("l'action", ex);
        }

        _link.Post(new ActionReply(action.Id, result), new ActionReply(action.Id, new ActionResult(false, "La réponse du module est trop grosse : raccourcis-la.")));
    }

    /// <summary>Un résultat que le protocole sait écrire et lire : texte non nul, valeur JSON présente.</summary>
    private static LookupResult Sanitize(LookupResult result) =>
        new(HostLink.Clip(result.Label), result.Value.ValueKind == JsonValueKind.Undefined ? JsonNull : result.Value);

    /// <summary>Crée une instance à part (sans <c>StartAsync</c>), lui fait faire un travail, puis la libère.</summary>
    private async Task WithModuleAsync(JsonElement settings, Guid instanceId, Func<CommunityModule, CancellationToken, Task> work)
    {
        var module = _context!.CreateModule(settings, new HostLog(_link, "[réglages] "), HostBackend.Detached(_link, instanceId));
        try
        {
            await work(module, _stop.Token).ConfigureAwait(false);
        }
        finally
        {
            module.CloseSignals();
            try
            {
                await module.DisposeAsync().AsTask().WaitAsync(Patience).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // la libération d'une instance éphémère ne doit pas masquer la réponse
            }
        }
    }

    private void ReportSettingsFailure(string what, Exception exception)
    {
        if (!ModuleGuard.CrashIfFatal(exception))
        {
            _link.Post(new Log("error", HostLink.Clip($"Exception dans {what} d'un réglage du module : {exception}")));
        }
    }

    // --- Apparence --------------------------------------------------------------------------------------------------------------

    private Theme ResolveTheme(JsonElement appearance)
    {
        AppearanceSettings settings;
        try
        {
            settings = HostPayloads.ReadAppearance(appearance);
        }
        catch (JsonException)
        {
            _link.Post(new Log("warn", "Apparence illisible : thème Classique utilisé."));
            settings = AppearanceSettings.Default;
        }

        return AppearanceResolver.Resolve(settings, _fonts, message => _link.Post(new Log("warn", HostLink.Clip(message)))).Theme;
    }

    // --- Fin --------------------------------------------------------------------------------------------------------------------

    private void Background(Func<Task> work)
    {
        var task = Task.Run(work);
        lock (_background)
        {
            _background.RemoveAll(done => done.IsCompleted);
            _background.Add(task);
        }
    }

    /// <summary>Ferme les instances, laisse partir ce qui reste à envoyer et renvoie le code de sortie.</summary>
    private async Task<int> EndAsync(int code)
    {
        _stop.Cancel();
        var closing = _instances.Values.Select(instance => instance.DisposeAsync().AsTask()).ToList();
        _instances.Clear();
        Task[] pending;
        lock (_background)
        {
            pending = [.. _background];
        }

        try
        {
            await Task.WhenAll(closing.Concat(pending)).WaitAsync(Patience).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ModuleGuard.CrashIfFatal(ex))
        {
            // une instance qui ne se laisse pas fermer n'empêche pas l'hôte de s'arrêter : le processus finit de toute façon
        }

        await _link.CompleteAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        return code;
    }

    private static bool TryMajor(string version, out int major)
    {
        var dot = version.IndexOf('.');
        major = 0;
        return dot > 0 && int.TryParse(version.AsSpan(0, dot), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out major);
    }

    /// <summary>Le service a envoyé quelque chose d'inexploitable : l'hôte le dit et s'arrête.</summary>
    private sealed class ServiceMistake : Exception
    {
        public ServiceMistake(string message)
            : base(message)
        {
        }
    }

    /// <summary>Les polices installées, listées seulement si un réglage d'apparence en demande une (la liste coûte du temps et de la mémoire).</summary>
    private sealed class LazySystemFonts : ISystemFonts
    {
        private readonly Lazy<SkiaSystemFonts> _fonts = new(() => new SkiaSystemFonts());

        public IReadOnlyList<string> Families => _fonts.Value.Families;

        public SKTypeface? Match(string family) => _fonts.Value.Match(family);
    }
}
