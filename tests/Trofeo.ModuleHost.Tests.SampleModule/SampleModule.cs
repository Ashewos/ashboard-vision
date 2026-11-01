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
                Services.Log.Info("http:" + await http.GetStringAsync("http://localhost:1234/", ct));
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
        }
    }

    public override void Render(SKCanvas canvas, ModuleRenderContext ctx)
    {
        if (_mode == "oomRender")
        {
            throw new OutOfMemoryException();
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
        if (query == "boom")
        {
            throw new InvalidOperationException("boom de la recherche");
        }

        using var document = JsonDocument.Parse($"\"{fieldKey}:{query}:{Services.Settings.ValueKind}\"");
        return Task.FromResult<IReadOnlyList<LookupResult>>([new LookupResult("Résultat " + query, document.RootElement.Clone())]);
    }

    public override Task<ActionResult> RunActionAsync(string actionKey, CancellationToken ct)
    {
        var mode = Services.Settings.ValueKind == JsonValueKind.Object && Services.Settings.TryGetProperty("mode", out var m) ? m.GetString() : "?";
        return Task.FromResult(new ActionResult(actionKey != "echec", $"action {actionKey} avec {mode}"));
    }

    public override ValueTask DisposeAsync()
    {
        _stopped = true;
        if (_mode == "dispose")
        {
            Services.Log.Info("disposed");
        }

        return ValueTask.CompletedTask;
    }
}
