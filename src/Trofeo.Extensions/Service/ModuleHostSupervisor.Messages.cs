using System.Diagnostics;
using System.Text.Json;
using AshboardVision.Sdk;
using SkiaSharp;
using Trofeo.Extensions.Protocol;
using Trofeo.ModuleKit;

namespace Trofeo.Extensions.Service;

// Ce que l'hôte écrit au service, et ce que le service lui demande en retour (recherches et actions des réglages).
public sealed partial class ModuleHostSupervisor
{
    /// <summary>Ce que répond une action quand l'hôte ne tourne pas.</summary>
    public const string HostNotRunningText = "Module arrêté.";

    private const int MaxLogLinesPerSecond = 50;
    private const int MaxLookupResults = 100;
    private const int MaxSecretKeyLength = 200;
    private const int MaxSecretValueLength = 64 * 1024;

    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(30);

    private readonly object _secretsGate = new();
    private Task _secretWork = Task.CompletedTask;
    private readonly object _logGate = new();
    private long _logWindowStart;
    private int _logLines;

    /// <summary>
    /// Lit ce que l'hôte écrit, jusqu'à la fin du tube. Un flux illisible ou coupé fait arrêter l'hôte : c'est une panne comme une
    /// autre, jamais une exception qui remonte jusqu'à la boucle d'affichage.
    /// </summary>
    private async Task ReadLoopAsync(HostConnection connection)
    {
        try
        {
            await foreach (var message in connection.Link.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                if (!HostMessage.IsFromHost(message))
                {
                    _log.Warning($"L'hôte a envoyé un message qui n'est pas de son sens ({message.GetType().Name}) : ignoré.");
                    continue;
                }

                try
                {
                    Handle(connection, message);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _log.Error($"Message {message.GetType().Name} de l'hôte non traité : {ex.Message}");
                }
            }
        }
        catch (InvalidDataException ex)
        {
            _log.Error("L'hôte du module a écrit un message illisible : " + ex.Message);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // le tube s'est fermé avec l'hôte
        }

        connection.KillQuietly(); // un hôte dont le flux est fini ou illisible ne sert plus à rien
    }

    private void Handle(HostConnection connection, HostMessage message)
    {
        switch (message)
        {
            case Frame frame:
                OnFrame(connection, frame);
                break;
            case Status status:
                if (_slots.TryGetValue(status.InstanceId, out var statusSlot))
                {
                    var text = HostText.Clean(status.Message, 200);
                    statusSlot.Owner.SetHostStatus(status.Health, text.Length > 0 ? text : null);
                }

                break;
            case Log log:
                OnLog(log);
                break;
            case SecretGet get:
                EnqueueSecret(() => AnswerSecret(connection, get.Id, get.Key, write: false, value: null));
                break;
            case SecretSet set:
                EnqueueSecret(() => AnswerSecret(connection, set.Id, set.Key, write: true, set.Value));
                break;
            case DataGet data:
                _ = Task.Run(() => AnswerData(connection, data));
                break;
            case LocalConnect connect:
                OnLocalConnect(connection, connect);
                break;
            case LookupReply lookup:
                connection.Complete(lookup.Id, lookup);
                break;
            case ActionReply action:
                connection.Complete(action.Id, action);
                break;
            case Failure failure:
                var failureText = HostText.Clean(failure.Message, 300);
                lock (_gate)
                {
                    connection.FailureText = failureText;
                }

                _log.Error("L'hôte du module s'arrête : " + failureText);
                connection.KillQuietly();
                break;
        }

        // Ready : rien à faire (la version du SDK du module est déjà vérifiée par le manifeste).
    }

    /// <summary>
    /// Une image est prête : on la copie (indice et instance vérifiés, c'est une entrée hostile), on acquitte, puis la case la pose.
    /// Une image d'une case refermée, d'une autre taille ou d'un hôte déjà remplacé est ignorée.
    /// </summary>
    private void OnFrame(HostConnection connection, Frame frame)
    {
        if (frame.Buffer is not (0 or 1))
        {
            _log.Warning($"L'hôte a annoncé une image dans le tampon {frame.Buffer} : ignorée.");
            return;
        }

        if (!_slots.TryGetValue(frame.InstanceId, out var slot))
        {
            return;
        }

        lock (_gate)
        {
            if (!ReferenceEquals(_session, connection))
            {
                return;
            }
        }

        SKBitmap? image;
        try
        {
            image = slot.CopyFrame(frame.Buffer);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or ArgumentException)
        {
            return; // la case s'est refermée pendant la copie
        }

        if (image is null)
        {
            return;
        }

        connection.Post(new FrameDone(frame.InstanceId, frame.Buffer));
        slot.Owner.Publish(slot, image, frame.Animating);
    }

    private void OnLog(Log log)
    {
        lock (_logGate)
        {
            var now = Stopwatch.GetTimestamp();
            if (now - _logWindowStart >= Stopwatch.Frequency)
            {
                _logWindowStart = now;
                _logLines = 0;
            }

            if (++_logLines > MaxLogLinesPerSecond)
            {
                return; // un hôte bavard ne remplit pas le journal du service
            }
        }

        var text = HostText.Clean(log.Message, 2000);
        switch (log.Level)
        {
            case "error":
                _log.Error(text);
                break;
            case "warn" or "warning":
                _log.Warning(text);
                break;
            case "info":
                _log.Info(text);
                break;
        }
    }

    private void OnLocalConnect(HostConnection connection, LocalConnect request)
    {
        // Les réglages qui font foi sont ceux de l'instance qui demande, ou de l'action de la fenêtre de réglage en cours.
        JsonElement? settings = null;
        if (request.InstanceId != Guid.Empty)
        {
            if (_slots.TryGetValue(request.InstanceId, out var slot))
            {
                settings = slot.Owner.Settings;
            }
            else
            {
                lock (_gate)
                {
                    if (_actionSettings.TryGetValue(request.InstanceId, out var forAction))
                    {
                        settings = forAction;
                    }
                }
            }
        }

        if (settings is null || connection.Relay is not { } relay)
        {
            connection.Post(new LocalRefused(request.Id, LocalRelay.PortRefusedReason));
            return;
        }

        relay.Connect(request, settings.Value);
    }

    /// <summary>Les demandes de secrets se font dans l'ordre où l'hôte les a écrites (une écriture suivie d'une lecture se relit), sur un fil du pool, jamais sur la lecture.</summary>
    private void EnqueueSecret(Action work)
    {
        lock (_secretsGate)
        {
            _secretWork = _secretWork.ContinueWith(_ => work(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    private void AnswerSecret(HostConnection connection, int id, string key, bool write, string? value)
    {
        string? result = null;
        try
        {
            if (key.Length is > 0 and <= MaxSecretKeyLength)
            {
                if (!write)
                {
                    result = _secrets.Get(key);
                }
                else if (value is null || value.Length <= MaxSecretValueLength)
                {
                    _secrets.Set(key, value);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Error($"Le coffre à secrets du module a échoué : {ex.Message}");
        }

        connection.Post(new SecretValue(id, result));
    }

    private void AnswerData(HostConnection connection, DataGet request)
    {
        var unavailable = new DataValue(request.Id, "unavailable");
        DataValue answer;
        try
        {
            answer = request.Kind switch
            {
                HostPayloads.SensorsKind => Answer(request.Id, ModulePermission.Sensors, _data.Sensors, HostPayloads.SensorsToJson),
                HostPayloads.NowPlayingKind => Answer(request.Id, ModulePermission.NowPlaying, _data.NowPlaying, HostPayloads.NowPlayingToJson),
                _ => unavailable,
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Error($"Lecture des données du service impossible : {ex.Message}");
            answer = unavailable;
        }

        connection.Post(answer, unavailable); // une pochette trop grosse pour une trame devient « indisponible »
    }

    private DataValue Answer<T>(int id, ModulePermission needed, Func<DataResult<T>> read, Func<T, JsonElement> toJson)
    {
        if (!_granted.Contains(needed))
        {
            return new DataValue(id, "denied");
        }

        var result = read();
        return result.Status switch
        {
            DataStatus.Ok when result.Value is { } value => new DataValue(id, "ok", toJson(value)),
            DataStatus.Denied => new DataValue(id, "denied"),
            _ => new DataValue(id, "unavailable"),
        };
    }

    // --- Recherches et actions des réglages ------------------------------------------------------------------------------------

    /// <summary>La liste d'un champ de recherche, demandée à l'hôte. Vide si l'hôte ne tourne pas, ne répond pas à temps ou s'arrête.</summary>
    internal async Task<IReadOnlyList<LookupResult>> LookupAsync(string fieldKey, string query, CancellationToken ct)
    {
        HostConnection? connection;
        lock (_gate)
        {
            connection = _session;
        }

        if (connection is null)
        {
            return [];
        }

        var reply = await connection.RequestAsync(id => new Lookup(id, fieldKey, query), LookupTimeout, _time, ct).ConfigureAwait(false);
        return reply is LookupReply lookup
            ? [.. lookup.Results.Take(MaxLookupResults).Select(result => new LookupResult(HostText.Clean(result.Label, 200), result.Value))]
            : [];
    }

    /// <summary>
    /// Lance l'action d'un champ de réglage dans l'hôte, avec les réglages de la fenêtre. Ces réglages font foi pour les connexions
    /// « Cet ordinateur » que l'action demande, le temps de l'action (voir <see cref="LocalRelay"/>).
    /// </summary>
    internal async Task<ActionResult> RunActionAsync(string actionKey, JsonElement settings, CancellationToken ct)
    {
        HostConnection? connection;
        lock (_gate)
        {
            connection = _session;
        }

        if (connection is null)
        {
            return new ActionResult(false, HostNotRunningText);
        }

        var instance = Guid.NewGuid();
        var forAction = settings.ValueKind == JsonValueKind.Object ? settings.Clone() : EmptySettings;
        lock (_gate)
        {
            _actionSettings[instance] = forAction;
        }

        HostMessage? reply;
        try
        {
            reply = await connection.RequestAsync(id => new RunAction(id, instance, actionKey, forAction), ActionTimeout, _time, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _actionSettings.Remove(instance);
            }
        }

        if (reply is ActionReply action)
        {
            return new ActionResult(action.Result.Ok, HostText.Clean(action.Result.Message, 300));
        }

        return new ActionResult(false, connection.Process.Exited.IsCompleted ? HostNotRunningText : RemoteModule.NotRespondingText + ".");
    }
}
