using System.Text.Json;
using AshboardVision.Sdk;
using SkiaSharp;
using Trofeo.ModuleKit;

namespace Trofeo.ModuleHost.Tests.SampleModule;

/// <summary>
/// Un module qui fait ce que ses réglages lui disent (<c>mode</c>) et le raconte par le journal : les tests lisent les
/// messages « Log » de l'hôte. Remplit la moitié gauche de la couleur <c>color</c> (rouge par défaut) et la moitié droite de l'accent du thème.
/// </summary>
public sealed class SampleModule : CommunityModule
{
    private string _mode = "static";
    private SKColor _color = SKColors.Red;
    private int _frames;

    private bool _thrown;

    public SampleModule()
    {
        if (Environment.GetEnvironmentVariable("SAMPLE_OOM_CTOR") == "1")
        {
            throw new OutOfMemoryException();
        }
    }

    public override bool IsAnimating => _mode == "animated" && !_stopped;

    private volatile bool _stopped;

    public override async Task StartAsync(CancellationToken ct)
    {
        var settings = Services.Settings;
        if (settings.ValueKind == JsonValueKind.Object)
        {
            if (settings.TryGetProperty("mode", out var mode) && mode.GetString() is { } text)
            {
                _mode = text;
            }

            if (settings.TryGetProperty("color", out var color) && color.GetString() is { } hex)
            {
                _color = SKColor.Parse(hex);
            }

            if (settings.TryGetProperty("startDelayMs", out var delay) && delay.TryGetInt32(out var milliseconds))
            {
                // Un démarrage lent : la boucle de dessin de l'hôte ne part qu'après (les messages suivant l'ouverture arrivent avant).
                await Task.Delay(milliseconds, ct);
            }
        }

        Services.Log.Info("started:" + _mode);
        switch (_mode)
        {
            case "burst":
                // Dix demandes de dessin espacées : sans FrameDone, l'hôte ne doit jamais dépasser deux images.
                _ = Task.Run(async () =>
                {
                    for (var i = 0; i < 10; i++)
                    {
                        await Task.Delay(50);
                        Invalidate();
                    }
                });
                break;
            case "ticker":
                // Une minuterie propre au module : elle doit continuer même quand la case est cachée.
                _ = Task.Run(async () =>
                {
                    for (var i = 0; !_stopped; i++)
                    {
                        await Task.Delay(100);
                        Services.Log.Info("tick-" + i);
                    }
                });
                break;
            case "logburst":
                for (var i = 0; i < 100; i++)
                {
                    Services.Log.Info("burst-" + i);
                }

                break;
            case "has":
                Services.Log.Info("has:" + string.Join(",", Enum.GetValues<ModulePermission>().Where(Services.Has)));
                break;
            case "sensors":
            {
                var result = await Services.GetSensorsAsync(ct);
                Services.Log.Info(FormattableString.Invariant($"sensors:{result.Status}:{result.Value?.CpuName}:{result.Value?.Values.GetValueOrDefault("cpu.load")}"));
                break;
            }

            case "nowplaying":
            {
                var result = await Services.GetNowPlayingAsync(ct);
                Services.Log.Info($"nowplaying:{result.Status}:{result.Value?.Title}:{result.Value?.CoverPng?.Length}");
                break;
            }

            case "connect":
            {
                var result = await Services.ConnectLocalAsync(1234, ct);
                Services.Log.Info($"connect:{result.Status}");
                break;
            }

            case "http":
            {
                using var http = Services.CreateHttpClient();
                var url = Services.Settings.TryGetProperty("url", out var u) && u.GetString() is { } text ? text : "http://localhost:1234/";
                try
                {
                    Services.Log.Info("http:" + await http.GetStringAsync(url, ct));
                }
                catch (HttpRequestException ex)
                {
                    Services.Log.Info("http-error:" + ex.GetType().Name);
                }

                break;
            }

            case "secrets":
                Services.Secrets.Set("k", "v1");
                Services.Log.Info("secret:" + Services.Secrets.Get("k") + ":" + (Services.Secrets.Get("absent") ?? "null"));
                break;
            case "status":
                SetStatus(ModuleHealth.NotConfigured, "À régler");
                break;
            case "oomStart":
                throw new OutOfMemoryException();
            case "https":
            {
                try
                {
                    using var http = Services.CreateHttpClient("srv");
                    Services.Log.Info("https:" + await http.GetStringAsync("https://localhost:1234/", ct));
                }
                catch (HttpRequestException ex)
                {
                    Services.Log.Info("https-error:" + ex.GetType().Name);
                }

                break;
            }

            case "throwStart":
                throw new InvalidOperationException("boom au démarrage");
            case "crashWithoutSensors":
                if (!Services.Has(ModulePermission.Sensors))
                {
                    // Un module qui n'a pas prévu qu'on lui refuse sa permission : l'exception se perd sur un fil sans personne pour la rattraper,
                    // et le processus de l'hôte s'arrête (banc d'essai : « l'hôte ne doit pas s'arrêter »).
                    new Thread(() => throw new InvalidOperationException("Sans capteurs, je ne sais pas faire")) { IsBackground = true }.Start();
                }

                break;
        }
    }

    public override void Render(SKCanvas canvas, ModuleRenderContext ctx)
    {
        if (_mode == "oomRender")
        {
            throw new OutOfMemoryException();
        }

        if (_mode == "throwOnce" && _frames == 0 && !_thrown)
        {
            _thrown = true;
            throw new InvalidOperationException("boom unique");
        }

        if (_mode == "throwRender")
        {
            throw new InvalidOperationException("boom au dessin");
        }

        using var left = new SKPaint { Color = _color };
        using var right = new SKPaint { Color = ctx.Theme.Accent };
        canvas.DrawRect(0, 0, ctx.Bounds.Width / 2, ctx.Bounds.Height, left);
        canvas.DrawRect(ctx.Bounds.Width / 2, 0, ctx.Bounds.Width / 2, ctx.Bounds.Height, right);
        _frames++;
        if (_mode == "ctx")
        {
            Services.Log.Info(FormattableString.Invariant($"ctx:{ctx.Size.Columns}x{ctx.Size.Rows}:{ctx.Bounds.Width}x{ctx.Bounds.Height}:{ctx.TextScale}:{ctx.Theme.Id}"));
        }
    }

    public override Task<IReadOnlyList<LookupResult>> LookupAsync(string fieldKey, string query, CancellationToken ct)
    {
        if (query == "huge")
        {
            // Plus d'1 Mio de réponse : le protocole ne sait pas l'écrire.
            return Task.FromResult<IReadOnlyList<LookupResult>>([.. Enumerable.Range(0, 800).Select(i => new LookupResult(new string('x', 2000), default))]);
        }

        if (query == "boom")
        {
            throw new InvalidOperationException("boom de la recherche");
        }

        using var document = JsonDocument.Parse($"\"{fieldKey}:{query}:{Services.Settings.ValueKind}\"");
        return Task.FromResult<IReadOnlyList<LookupResult>>([new LookupResult("Résultat " + query, document.RootElement.Clone())]);
    }

    public override async Task<ActionResult> RunActionAsync(string actionKey, CancellationToken ct)
    {
        if (actionKey == "connect")
        {
            var result = await Services.ConnectLocalAsync(1234, ct);
            return new ActionResult(result.Status == DataStatus.Ok, "connect:" + result.Status);
        }

        var mode = Services.Settings.ValueKind == JsonValueKind.Object && Services.Settings.TryGetProperty("mode", out var m) ? m.GetString() : "?";
        return new ActionResult(actionKey != "echec", $"action {actionKey} avec {mode}");
    }

    public override async Task OnShortcutAsync(string shortcutId, CancellationToken ct)
    {
        // Le mode et l'identifiant dans le journal : prouvent que l'appel arrive sur l'instance de la case (ses réglages), pas sur une instance à part.
        Services.Log.Info($"shortcut:{shortcutId}:{_mode}");
        if (shortcutId == "boom")
        {
            throw new InvalidOperationException("boom du raccourci");
        }

        if (_mode == "slowShortcut")
        {
            await Task.Delay(200, ct);
        }
    }

    public override ValueTask DisposeAsync()
    {
        _stopped = true;
        if (_mode == "dispose")
        {
            Services.Log.Info("disposed");
        }

        if (_mode == "slowStop")
        {
            // Un module qui met plus de 5 secondes à se fermer (banc d'essai : « arrêt propre en moins de 5 s »).
            return new ValueTask(Task.Delay(TimeSpan.FromSeconds(30)));
        }

        return ValueTask.CompletedTask;
    }
}
