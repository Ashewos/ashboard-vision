using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AshboardVision.Sdk;
using SkiaSharp;
using Trofeo.Core.Rendering;
using Trofeo.Extensions;
using Trofeo.Extensions.Consumption;
using Trofeo.Extensions.Sandbox;
using Trofeo.Extensions.Service;
using Trofeo.ModuleKit;

namespace AshboardVision.Sdk.Tool;

/// <summary>
/// <c>ashboard-module check [dossier] [--out dossier]</c> : essaie un module comme le logiciel le lance, dans l'ordre : manifeste et
/// compatibilité, démarrage enfermé (première image en moins de 10 s), une image PNG par taille et par thème, une passe par permission
/// refusée (l'hôte ne doit pas s'arrêter), la mesure (temps de dessin, processeur, mémoire) et l'arrêt propre en moins de 5 s.
/// <para>
/// Le module tourne dans son propre conteneur d'essai (<c>ashboard-vision.check.&lt;id&gt;</c>), supprimé à la fin quoi qu'il arrive. L'essai
/// ne lit ni n'écrit rien du logiciel installé : ni <c>modules.json</c>, ni <c>%AppData%\ashboard-vision</c>, ni le coffre à secrets. Il n'appelle
/// aucun réseau (seul le module peut le faire, avec ses permissions).
/// </para>
/// </summary>
internal static class CheckCommand
{
    /// <summary>Le dossier des images quand <c>--out</c> n'est pas donné, dans le dossier où on est.</summary>
    public const string DefaultOutputFolder = "check-out";

    /// <summary>La durée de la mesure par défaut.</summary>
    public static readonly TimeSpan DefaultMeasure = TimeSpan.FromSeconds(30);

    /// <summary>Le délai de la première image et de chaque image de l'essai : celui qui fait dire à une case « Le module ne répond pas ».</summary>
    private static readonly TimeSpan FrameTimeout = RemoteModule.ResponseTimeout;

    /// <summary>Le temps pendant lequel on regarde si l'hôte tient, une fois la première image arrivée, quand une permission est refusée.</summary>
    private static readonly TimeSpan Observation = TimeSpan.FromSeconds(2);

    /// <summary>Le rythme des demandes de dessin pendant la mesure.</summary>
    private static readonly TimeSpan DrawInterval = TimeSpan.FromMilliseconds(250);

    private static readonly string[] ThemeIds = ["classique", "liquid-glass", "pixel"];

    public static async Task<int> RunAsync(DirectoryInfo? directory, DirectoryInfo? output, TimeSpan measure, TextWriter writer, CancellationToken ct)
    {
        var report = new CheckReport(writer);
        var folder = ModuleFolder.Resolve(directory);

        // 1. Le manifeste.
        var (manifest, errors) = ModuleFolder.ReadManifest(folder);
        if (manifest is null)
        {
            foreach (var error in errors)
            {
                report.Failure("Manifeste : " + error, "le manifeste n'est pas valide");
            }

            return report.Conclude();
        }

        report.Ok($"Manifeste : {manifest.Id} {manifest.Version} ({manifest.Name})");
        if (!File.Exists(Path.Combine(folder, manifest.Entry)))
        {
            report.Failure(
                $"Module : la DLL « {manifest.Entry} » est introuvable dans le dossier. Construis ton module, puis mets manifest.json à côté de sa DLL.",
                $"la DLL « {manifest.Entry} » est introuvable");
            return report.Conclude();
        }

        // 2. La compatibilité avec le SDK du logiciel.
        var compatibility = Compatibility.Check(manifest.Sdk);
        if (compatibility != CompatibilityKind.Compatible)
        {
            report.Failure($"Compatibilité : {Compatibility.Message(compatibility)}", $"le module demande le SDK {manifest.Sdk}, Ashboard Vision parle le {Compatibility.Current}");
            return report.Conclude();
        }

        report.Ok($"Compatibilité : le module demande le SDK {manifest.Sdk}, Ashboard Vision parle le {Compatibility.Current}.");

        var host = Path.Combine(AppContext.BaseDirectory, "host", "Trofeo.ModuleHost.exe");
        if (!File.Exists(host))
        {
            report.Failure($"Hôte de module introuvable ({host}) : réinstalle l'outil.", "l'hôte de module de l'outil est introuvable");
            return report.Conclude();
        }

        // Le dossier des images est vérifié (et créé) avant tout lancement : un --out qui résiste donne une conclusion, pas une trace.
        var outputFolder = ModuleFolder.PrepareOutputFolder(output?.FullName ?? DefaultOutputFolder, out var outputError);
        if (outputFolder is null)
        {
            report.Failure($"Images : {outputError}", "le dossier des images (--out) n'est pas utilisable");
            return report.Conclude();
        }

        using var cleanup = new ContainerCleanup(manifest.Id, writer);
        try
        {
            await RunSandboxedAsync(new Plan(folder, manifest, host, outputFolder, measure), report, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            report.Failure("Essai interrompu.");
        }

        return report.Conclude();
    }

    private sealed record Plan(string Folder, ModuleManifest Manifest, string Host, string OutputFolder, TimeSpan Measure);

    private static async Task RunSandboxedAsync(Plan plan, CheckReport report, CancellationToken ct)
    {
        var manifest = plan.Manifest;
        var log = new CheckLog();
        try
        {
            AppContainerProfile.EnsureForCheck(manifest.Id);
        }
        catch (Exception ex) when (ex is COMException or Win32Exception or ArgumentException or PlatformNotSupportedException
                                       or UnauthorizedAccessException or IOException)
        {
            report.Failure($"Enfermement : le conteneur d'essai ne se crée pas ({ex.Message})", "le conteneur d'essai ne se crée pas");
            return;
        }

        var settings = ModuleFolder.DefaultSettings(manifest.Settings);
        var sizes = ModuleFolder.Sizes(manifest);
        var themes = ThemeIds.Select(id => Themes.Find(id) ?? throw new InvalidOperationException($"Thème « {id} » introuvable.")).ToArray();
        var classique = themes[0];

        // 3. Le démarrage enfermé, avec les permissions déclarées.
        await using var main = new CheckSession(plan.Folder, manifest, manifest.Permissions, plan.Host, log);
        var clock = Stopwatch.StartNew();
        var anchor = main.OpenCase(settings);
        using (anchor.Draw(sizes[0], classique))
        {
        }

        var first = await anchor.WaitForFrameAsync(0, FrameTimeout, ct).ConfigureAwait(false);
        if (!first.IsFrame)
        {
            ReportNoFrame(report, "Démarrage enfermé", first, FrameTimeout);
            report.AddJournal(log.Lines);
            return;
        }

        var granted = manifest.Permissions.Count == 0
            ? "sans permission"
            : "avec " + string.Join(", ", manifest.Permissions.Select(ModulePermissions.ToManifest));
        report.Ok($"Démarrage enfermé : première image en {Seconds(clock.Elapsed)} s ({granted}).");

        // 4. Une image par taille et par thème.
        if (!await WriteImagesAsync(plan, main, settings, sizes, themes, report, ct).ConfigureAwait(false))
        {
            report.AddJournal(log.Lines);
            return;
        }

        // 5. Une passe par permission refusée.
        foreach (var permission in manifest.Permissions)
        {
            await RefusePermissionAsync(plan, permission, settings, sizes[0], classique, report, ct).ConfigureAwait(false);
        }

        // 6. La mesure.
        var measureSize = manifest.Presets.TryGetValue(SizePreset.Ideal, out var ideal) ? ideal : manifest.MinSize;
        if (!await MeasureAsync(plan, main, anchor, measureSize, classique, report, ct).ConfigureAwait(false))
        {
            report.AddJournal(log.Lines);
            return;
        }

        // 7. L'arrêt propre.
        var stop = await main.StopAsync(ct).ConfigureAwait(false);
        if (stop.Clean)
        {
            report.Ok($"Arrêt propre : l'hôte s'est arrêté en {Seconds(stop.Elapsed)} s.");
        }
        else
        {
            report.Failure($"Arrêt : {stop.Problem}.", "le module ne s'arrête pas proprement en 5 s");
            report.AddJournal(log.Lines);
        }
    }

    /// <summary>Dessine chaque taille avec chaque thème, dans une case neuve à chaque fois, et écrit les PNG. Faux si une image manque (l'essai s'arrête là).</summary>
    private static async Task<bool> WriteImagesAsync(
        Plan plan, CheckSession session, System.Text.Json.JsonElement settings, IReadOnlyList<GridSize> sizes, ThemeDefinition[] themes,
        CheckReport report, CancellationToken ct)
    {
        var written = 0;
        foreach (var theme in themes)
        {
            // L'apparence part avant l'ouverture de chaque case : l'hôte la reçoit avant l'ouverture, donc la première image est dans ce thème.
            session.SetAppearance(theme.Theme.Id);
            foreach (var size in sizes)
            {
                var label = $"{size.Columns}×{size.Rows} en {theme.Theme.Id}";
                var checkCase = session.OpenCase(settings);
                try
                {
                    using (checkCase.Draw(size, theme))
                    {
                    }

                    var outcome = await checkCase.WaitForFrameAsync(0, FrameTimeout, ct).ConfigureAwait(false);
                    if (!outcome.IsFrame)
                    {
                        ReportNoFrame(report, $"Image {label}", outcome, FrameTimeout);
                        return false;
                    }

                    using var frame = checkCase.Draw(size, theme);
                    var path = Path.Combine(plan.OutputFolder, $"{plan.Manifest.Id}_{theme.Theme.Id}_{size.Columns}x{size.Rows}.png");
                    File.WriteAllBytes(path, Compose(frame, theme.Theme));
                    written++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    report.Failure($"Images : le dossier « {plan.OutputFolder} » ne s'écrit pas ({ex.Message})", "les images ne s'écrivent pas");
                    return false;
                }
                finally
                {
                    session.CloseCase(checkCase);
                }
            }
        }

        report.Ok($"Images : {written} images PNG écrites dans « {plan.OutputFolder} » ({sizes.Count} taille{(sizes.Count > 1 ? "s" : "")} × {themes.Length} thèmes).");
        return true;
    }

    /// <summary>L'image du module posée sur le fond et le panneau du thème, pour qu'on la lise comme à l'écran (le module ne dessine que son contenu).</summary>
    private static byte[] Compose(SKBitmap frame, Theme theme)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(theme.Background);
            using var panel = new SKPaint { Color = theme.Panel.WithAlpha((byte)Math.Round(255 * Math.Clamp(theme.PanelOpacity, 0f, 1f))), IsAntialias = true };
            canvas.DrawRoundRect(new SKRect(0, 0, frame.Width, frame.Height), theme.CornerRadius, theme.CornerRadius, panel);
            canvas.DrawBitmap(frame, 0, 0);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Lance le module sans une permission et regarde si l'hôte s'arrête : l'utilisateur peut refuser n'importe laquelle.</summary>
    private static async Task RefusePermissionAsync(
        Plan plan, ModulePermission permission, System.Text.Json.JsonElement settings, GridSize size, ThemeDefinition theme,
        CheckReport report, CancellationToken ct)
    {
        var name = ModulePermissions.ToManifest(permission);
        var log = new CheckLog();
        var remaining = plan.Manifest.Permissions.Where(other => other != permission).ToArray();
        await using var session = new CheckSession(plan.Folder, plan.Manifest, remaining, plan.Host, log);
        using var checkCase = session.OpenCase(settings);
        using (checkCase.Draw(size, theme))
        {
        }

        var outcome = await checkCase.WaitForFrameAsync(0, FrameTimeout, ct).ConfigureAwait(false);
        if (outcome.Kind == FrameOutcomeKind.Cancelled)
        {
            throw new OperationCanceledException(ct);
        }

        // Le module a répondu (une image, ou un état « hors ligne » avec son message) : on regarde encore un peu s'il tient.
        if (outcome.Kind is FrameOutcomeKind.Frame or FrameOutcomeKind.ModuleError)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < Observation && session.Recorder.UnexpectedExitCode is null)
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
            }

            if (session.Recorder.UnexpectedExitCode is { } stopped)
            {
                outcome = new FrameOutcome(FrameOutcomeKind.HostStopped, ExitCode: stopped);
            }
        }

        switch (outcome.Kind)
        {
            case FrameOutcomeKind.HostStopped:
                report.Failure(
                    $"Sans la permission « {name} » : l'hôte du module s'arrête (code {outcome.ExitCode:X8}). L'utilisateur peut refuser n'importe quelle permission : le module doit s'en passer.",
                    $"le module s'arrête quand la permission « {name} » est refusée");
                report.AddJournal(log.Lines);
                break;
            case FrameOutcomeKind.Timeout:
                report.Warning($"Sans la permission « {name} » : l'hôte tient, mais le module ne dessine rien en {FrameTimeout.TotalSeconds:0} s (la case afficherait « {RemoteModule.NotRespondingText} »).");
                break;
            case FrameOutcomeKind.ModuleError:
                report.Ok($"Sans la permission « {name} » : le module continue de tourner et dit : « {outcome.Message} ».");
                break;
            default:
                report.Ok($"Sans la permission « {name} » : le module continue de tourner.");
                break;
        }
    }

    /// <summary>
    /// Mesure le module pendant <see cref="Plan.Measure"/> : quatre demandes de dessin par seconde (un changement d'apparence, que l'hôte
    /// redessine), chacune chronométrée jusqu'à l'image reçue ; le temps processeur et la mémoire privée de l'hôte viennent du job object.
    /// Faux si l'hôte s'arrête pendant la mesure.
    /// </summary>
    private static async Task<bool> MeasureAsync(
        Plan plan, CheckSession session, CheckCase checkCase, GridSize size, ThemeDefinition theme, CheckReport report, CancellationToken ct)
    {
        // On mesure à la taille idéale du module, en Classique.
        session.SetAppearance(theme.Theme.Id);
        var before = checkCase.Module.FramesReceived;
        using (checkCase.Draw(size, theme))
        {
        }

        var settle = await checkCase.WaitForFrameAsync(before, FrameTimeout, ct).ConfigureAwait(false);
        if (!settle.IsFrame)
        {
            ReportNoFrame(report, "Mesure", settle, FrameTimeout);
            return false;
        }

        var start = session.Measure();
        var peak = start?.PrivateBytes ?? 0;
        var draws = new List<double>();
        var missed = 0;
        var clock = Stopwatch.StartNew();
        for (var i = 0; clock.Elapsed < plan.Measure; i++)
        {
            ct.ThrowIfCancellationRequested();
            var lap = Stopwatch.StartNew();
            var after = checkCase.Module.FramesReceived;
            session.SetAppearance(theme.Theme.Id, i % 2 == 0 ? 0.5f : 0.6f);
            var outcome = await checkCase.WaitForFrameAsync(after, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            switch (outcome.Kind)
            {
                case FrameOutcomeKind.Frame:
                    draws.Add(lap.Elapsed.TotalMilliseconds);
                    break;
                case FrameOutcomeKind.Timeout:
                    missed++;
                    break;
                case FrameOutcomeKind.Cancelled:
                    throw new OperationCanceledException(ct);
                default:
                    ReportNoFrame(report, "Mesure", outcome, FrameTimeout);
                    return false;
            }

            if (session.Measure() is { } sample)
            {
                peak = Math.Max(peak, sample.PrivateBytes);
            }

            if (lap.Elapsed < DrawInterval)
            {
                await Task.Delay(DrawInterval - lap.Elapsed, ct).ConfigureAwait(false);
            }
        }

        var end = session.Measure();
        var elapsed = clock.Elapsed;
        if (start is not { } from || end is not { } to)
        {
            report.Failure("Mesure : l'hôte du module ne répond plus aux mesures.", "l'hôte du module s'est arrêté pendant la mesure");
            return false;
        }

        peak = Math.Max(peak, to.PrivateBytes);
        if (draws.Count == 0)
        {
            report.Failure($"Dessin : le module n'a répondu à aucune des {missed} demandes de dessin.", "le module ne répond pas aux demandes de dessin");
            return false;
        }

        var average = draws.Average();
        report.Ok($"Dessin : {Number(average)} ms en moyenne, {Number(draws.Max())} ms au plus ({draws.Count} dessins demandés, de la demande à l'image reçue).");
        if (missed > 0)
        {
            report.Warning($"Dessin : {missed} demande{(missed > 1 ? "s" : "")} sans image en 2 s.");
        }

        var cpu = Math.Clamp((to.Usage.CpuTime - from.Usage.CpuTime).TotalSeconds / (elapsed.TotalSeconds * Environment.ProcessorCount) * 100, 0, 100);
        var seconds = Seconds(elapsed);
        report.Line(
            cpu > ConsumptionMeter.HighCpuPercent,
            $"Processeur : {Number(cpu)} % de la machine en moyenne sur {seconds} s",
            $"Processeur : {Number(cpu)} % de la machine en moyenne sur {seconds} s, au-dessus du repère de {ConsumptionMeter.HighCpuPercent:0} % à partir duquel le logiciel signale une consommation élevée.");

        var mebibytes = peak / (1024.0 * 1024.0);
        report.Line(
            peak > ConsumptionMeter.HighMemoryBytes,
            $"Mémoire : {Number(mebibytes)} Mio au plus (mémoire privée de l'hôte)",
            $"Mémoire : {Number(mebibytes)} Mio au plus, au-dessus du repère de {ConsumptionMeter.HighMemoryBytes / (1024 * 1024)} Mio à partir duquel le logiciel signale une consommation élevée (la limite dure est de {ModuleJob.MemoryLimit / (1024 * 1024)} Mio).");
        return true;
    }

    private static void ReportNoFrame(CheckReport report, string what, FrameOutcome outcome, TimeSpan timeout)
    {
        switch (outcome.Kind)
        {
            case FrameOutcomeKind.HostStopped:
                report.Failure($"{what} : l'hôte du module s'est arrêté (code {outcome.ExitCode:X8}).", "l'hôte du module s'arrête");
                break;
            case FrameOutcomeKind.ModuleError:
                report.Failure($"{what} : le module est en erreur : {outcome.Message}", "le module est en erreur");
                break;
            case FrameOutcomeKind.Cancelled:
                throw new OperationCanceledException();
            default:
                report.Failure($"{what} : aucune image en {timeout.TotalSeconds:0} s.", $"le module n'envoie aucune image en {timeout.TotalSeconds:0} s");
                break;
        }
    }

    private static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.0", CheckReport.French);

    private static string Number(double value) => value.ToString("0.#", CheckReport.French);
}
