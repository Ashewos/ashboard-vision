using SkiaSharp;
using Trofeo.ModuleKit;

namespace Trofeo.Modules.Agents;

/// <summary>Tout ce que le dessin lit : le relevé, l'onglet affiché, l'état du module, ses réglages, la saveur et la mascotte.</summary>
internal sealed record AgentScene(
    AgentSnapshot? Snapshot, string? SelectedId, ModuleStatus Status, AgentSettings Settings, AgentFlavor Flavor, MascotAnimator Animator);

/// <summary>Un texte dessiné : tel qu'il apparaît (ellipsé), sa ligne de base, sa couleur, sa police et sa taille en pixels. Réservé aux tests.</summary>
internal sealed record DrawnRun(string Text, float Baseline, SKColor Color, SKTypeface Typeface, float Size);

/// <summary>
/// Dessine le module : onglets, en-tête, fil d'activité ou vue d'ensemble, barre du bas et mascotte. Sans état entre deux
/// images (hormis <see cref="DrawnTexts"/> et <see cref="DrawnRuns"/>, réservés aux tests) ; appelé sur le thread
/// d'affichage. Les tailles sont multipliées par <see cref="ModuleRenderContext.TextScale"/> et tout est découpé par les
/// limites du module.
/// </summary>
internal sealed class AgentPainter
{
    /// <summary>Orange du travail en cours (point d'onglet, tâche en cours).</summary>
    internal static readonly SKColor WorkingColor = SKColor.Parse("#E8875B");

    internal static readonly SKColor AddedColor = SKColor.Parse("#7CE0A3");
    internal static readonly SKColor RemovedColor = SKColor.Parse("#F28B82");

    private const string NoValue = "—";
    private const string Loading = "Chargement…";

    /// <summary>Si non <c>null</c>, chaque texte dessiné (tel qu'il apparaît, ellipsé) y est ajouté.</summary>
    internal List<string>? DrawnTexts { get; set; }

    /// <summary>Si non <c>null</c>, chaque texte dessiné y est ajouté avec sa ligne de base et sa couleur.</summary>
    internal List<DrawnRun>? DrawnRuns { get; set; }

    public void Draw(SKCanvas canvas, ModuleRenderContext ctx, AgentScene scene)
    {
        if (ctx.Bounds.Width <= 0f || ctx.Bounds.Height <= 0f)
        {
            return;
        }

        var layout = AgentLayout.Compute(ctx.Bounds, ctx.Size, ctx.TextScale);
        using var frame = new Frame(canvas, ctx, scene, layout, DrawnTexts, DrawnRuns);
        canvas.Save();
        canvas.ClipRect(ctx.Bounds);
        try
        {
            frame.Paint();
        }
        finally
        {
            canvas.Restore();
        }
    }

    /// <summary>Une image : le canevas, les dimensions et les polices de cette image.</summary>
    private sealed class Frame : IDisposable
    {
        private readonly SKCanvas _canvas;
        private readonly ModuleRenderContext _ctx;
        private readonly AgentScene _scene;
        private readonly AgentLayoutResult _layout;
        private readonly List<string>? _texts;
        private readonly List<DrawnRun>? _runs;
        private readonly Theme _theme;
        private readonly float _scale;
        private readonly Dictionary<float, SKFont> _fonts = [];
        private readonly Dictionary<float, SKFont> _feedFonts = [];

        public Frame(SKCanvas canvas, ModuleRenderContext ctx, AgentScene scene, AgentLayoutResult layout, List<string>? texts, List<DrawnRun>? runs)
        {
            _canvas = canvas;
            _ctx = ctx;
            _scene = scene;
            _layout = layout;
            _texts = texts;
            _runs = runs;
            _theme = ctx.Theme;
            _scale = ctx.TextScale;
        }

        public void Dispose()
        {
            foreach (var font in _fonts.Values)
            {
                font.Dispose();
            }

            foreach (var font in _feedFonts.Values)
            {
                font.Dispose();
            }
        }

        public void Paint()
        {
            var sessions = _scene.Snapshot?.Sessions ?? [];
            var selectedIndex = IndexOf(sessions, _scene.SelectedId);
            var selected = selectedIndex >= 0 ? sessions[selectedIndex] : null;
            var detail = selected is not null && _scene.Snapshot?.Selected is { } d && d.Id == selected.Id ? d : null;

            DrawRule();
            DrawTabs(sessions, selectedIndex);
            DrawContent(sessions, selected, detail);
            DrawBottomBar(detail);
        }

        // --- Outils ---

        private SKFont Font(float size)
        {
            if (!_fonts.TryGetValue(size, out var font))
            {
                font = ModuleDrawing.Font(_theme, size * _scale, subpixel: true);
                _fonts[size] = font;
            }

            return font;
        }

        /// <summary>
        /// Police du fil d'activité : <see cref="Theme.FeedTypeface"/> à <paramref name="size"/> × <see cref="Theme.FeedScale"/>
        /// (1 si l'agrandissement n'est pas un nombre fini positif). Si la ligne de texte dépasse alors la hauteur d'une ligne
        /// du fil, la taille est réduite pour qu'elle y tienne : deux lignes ne se chevauchent jamais.
        /// </summary>
        private SKFont FeedFont(float size)
        {
            if (!_feedFonts.TryGetValue(size, out var font))
            {
                var scale = float.IsFinite(_theme.FeedScale) && _theme.FeedScale > 0f ? _theme.FeedScale : 1f;
                font = new SKFont(_theme.FeedTypeface, size * scale * _scale) { Subpixel = true };
                var textHeight = font.Metrics.Descent - font.Metrics.Ascent;
                var room = AgentLayout.FeedLineHeight * _scale;
                if (textHeight > room && textHeight > 0f)
                {
                    font.Size *= room / textHeight;
                }

                _feedFonts[size] = font;
            }

            return font;
        }

        private static SKColor Opacity(SKColor color, float factor) => color.WithAlpha((byte)Math.Round(color.Alpha * factor));

        private static int IndexOf(IReadOnlyList<AgentSessionSummary> sessions, string? id)
        {
            for (var i = 0; id is not null && i < sessions.Count; i++)
            {
                if (sessions[i].Id == id)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>Ligne de base qui centre verticalement, sur <paramref name="midY"/>, un texte de taille <paramref name="size"/>.</summary>
        private float BaselineFor(float size, float midY, bool feed = false)
        {
            var metrics = (feed ? FeedFont(size) : Font(size)).Metrics;
            return midY - (metrics.Ascent + metrics.Descent) / 2f;
        }

        /// <summary>
        /// Dessine le texte (ellipsé à <paramref name="maxWidth"/>) centré verticalement sur <paramref name="midY"/>, taille
        /// <paramref name="size"/> (avant échelle). Rend la largeur dessinée.
        /// </summary>
        private float Text(string text, float x, float midY, SKTextAlign align, float size, SKColor color, float maxWidth, bool feed = false) =>
            TextOnBaseline(text, x, BaselineFor(size, midY, feed), align, size, color, maxWidth, feed);

        /// <summary>Comme <see cref="Text"/>, sur une ligne de base donnée (pour aligner deux tailles côte à côte).</summary>
        private float TextOnBaseline(string text, float x, float baseline, SKTextAlign align, float size, SKColor color, float maxWidth, bool feed = false)
        {
            if (maxWidth <= 0f || string.IsNullOrEmpty(text))
            {
                return 0f;
            }

            var font = feed ? FeedFont(size) : Font(size);
            var fitted = ModuleDrawing.Ellipsize(text, font, maxWidth);
            using var paint = new SKPaint { Color = color, IsAntialias = true };
            ModuleDrawing.DrawText(_canvas, fitted, x, baseline, align, font, paint);
            _texts?.Add(fitted);
            _runs?.Add(new DrawnRun(fitted, baseline, color, font.Typeface, font.Size));
            return ModuleDrawing.MeasureText(fitted, font);
        }

        private void Fill(SKRect rect, SKColor color)
        {
            using var paint = new SKPaint { Color = color, Style = SKPaintStyle.Fill };
            _canvas.DrawRect(rect, paint);
        }

        // --- Onglets ---

        private void DrawRule()
        {
            var bar = _layout.Bar;
            Fill(new SKRect(bar.Left, bar.Top - _scale, bar.Right, bar.Top), Opacity(_theme.Text, 0.10f));
        }

        private void DrawTabs(IReadOnlyList<AgentSessionSummary> sessions, int selectedIndex)
        {
            var capacity = _layout.TabCapacity;
            if (capacity <= 0 || sessions.Count == 0)
            {
                return;
            }

            // Trop de sessions : la dernière place devient « +N ». L'onglet affiché reste toujours visible.
            var overflow = sessions.Count > capacity;
            var visible = overflow ? capacity - 1 : sessions.Count;
            var first = overflow && selectedIndex >= visible ? selectedIndex - visible + 1 : 0;

            var tabs = _layout.Tabs;
            var height = AgentLayout.TabHeight * _scale;
            var step = (AgentLayout.TabHeight + AgentLayout.TabGap) * _scale;
            for (var slot = 0; slot < visible; slot++)
            {
                var top = tabs.Top + slot * step;
                DrawTab(new SKRect(tabs.Left, top, tabs.Right, top + height), sessions[first + slot], first + slot == selectedIndex);
            }

            if (overflow)
            {
                var top = tabs.Top + visible * step;
                var x = tabs.Left + (3f + 14f) * _scale;
                Text("+" + (sessions.Count - visible), x, top + height / 2f, SKTextAlign.Left, 22f, _theme.TextMuted, tabs.Right - x);
            }
        }

        private void DrawTab(SKRect rect, AgentSessionSummary session, bool shown)
        {
            var pixel = _theme.Shape == ThemeShape.Pixel;
            if (shown)
            {
                // L'aplat et le trait à gauche (Pixel : 4 px dans la couleur de mise en avant ; sinon 3 px dans l'accent), ou la
                // pastille de verre de « Liquid Glass avancé », que le service dessine sous le module.
                ModuleDrawing.DrawSelection(_canvas, rect, _ctx);
            }

            var dotColor = session.Activity switch
            {
                AgentActivity.Working => WorkingColor,
                AgentActivity.Waiting => _theme.Warning,
                _ => _theme.TextMuted,
            };
            var dotX = rect.Left + 18f * _scale;
            using (var dot = new SKPaint { Color = dotColor, IsAntialias = !pixel, Style = SKPaintStyle.Fill })
            {
                if (pixel)
                {
                    var half = 6f * _scale;
                    _canvas.DrawRect(new SKRect(dotX - half, rect.MidY - half, dotX + half, rect.MidY + half), dot);
                }
                else
                {
                    _canvas.DrawCircle(dotX, rect.MidY, 6f * _scale, dot);
                }
            }

            var nameX = dotX + 6f * _scale + 10f * _scale;
            Text(session.ProjectName, nameX, rect.MidY, SKTextAlign.Left, 22f, shown && pixel ? _theme.Highlight : _theme.Text, rect.Right - 8f * _scale - nameX);
        }

        // --- Contenu à droite des onglets ---

        private void DrawContent(IReadOnlyList<AgentSessionSummary> sessions, AgentSessionSummary? selected, AgentSessionDetail? detail)
        {
            var body = _layout.Body;
            if (_scene.Status.ActionMessage is { } message)
            {
                ModuleDrawing.DrawActionMessage(_canvas, body, _theme, _scale, message);
                _texts?.Add(message);
                return;
            }

            if (_scene.Snapshot is null)
            {
                // Hors ligne avant tout relevé réussi : il n'y a rien à attendre, on n'affiche pas un chargement sans fin.
                Centered(_scene.Status.Health == ModuleHealth.Offline ? _scene.Flavor.NoSessionText : Loading);
            }
            else if (sessions.Count == 0)
            {
                Centered(_scene.Flavor.NoSessionText);
            }
            else if (selected is null || detail is null)
            {
                Centered(Loading);
            }
            else
            {
                if (detail.CurrentTask is { } task)
                {
                    var header = _layout.Header;
                    Text(task, header.Left, header.MidY, SKTextAlign.Left, 26f, _theme.Highlight, header.Width);
                }

                if (_scene.Settings.View == AgentView.Overview)
                {
                    DrawOverview(selected, detail);
                }
                else
                {
                    DrawFeed(detail);
                }
            }
        }

        private void Centered(string text)
        {
            var body = _layout.Body;
            Text(text, body.MidX, body.MidY, SKTextAlign.Center, 24f, _theme.TextMuted, body.Width);
        }

        /// <summary>
        /// Le fil d'activité. Le plus récent est en haut (les autres sous lui) ou sur la dernière ligne entière du corps (les
        /// autres au-dessus, le haut restant vide s'il y a peu d'entrées). Avec le fondu, chaque ligne est plus pâle que la
        /// précédente en s'éloignant de la plus récente.
        /// </summary>
        private void DrawFeed(AgentSessionDetail detail)
        {
            var body = _layout.Body;
            var lineHeight = AgentLayout.FeedLineHeight * _scale;
            var toolWidth = 96f * _scale;
            var gap = 10f * _scale;
            var lines = Math.Min(_layout.FeedCapacity, detail.Feed.Count);
            var settings = _scene.Settings;
            for (var i = 0; i < lines; i++)
            {
                var entry = detail.Feed[detail.Feed.Count - 1 - i];
                var opacity = settings.FeedFade ? Math.Max(0.25f, 1f - 0.18f * i) : 1f;
                var slot = settings.FeedNewest == FeedNewest.Bottom ? _layout.FeedCapacity - 1 - i : i;
                var mid = body.Top + slot * lineHeight + lineHeight / 2f;

                // Les compteurs, collés à droite : « +12 » en vert, « −3 » en rouge.
                var right = body.Right;
                if (entry.Removed is > 0)
                {
                    right -= Text("−" + entry.Removed, right, mid, SKTextAlign.Right, 20f, Opacity(RemovedColor, opacity), 90f * _scale) + gap;
                }

                if (entry.Added is > 0)
                {
                    right -= Text("+" + entry.Added, right, mid, SKTextAlign.Right, 20f, Opacity(AddedColor, opacity), 90f * _scale) + gap;
                }

                Text(entry.Tool, body.Left, mid, SKTextAlign.Left, 22f, Opacity(_theme.Accent, opacity), toolWidth - 8f * _scale, feed: true);
                Text(entry.Target, body.Left + toolWidth, mid, SKTextAlign.Left, 22f, Opacity(_theme.Text, opacity), right - body.Left - toolWidth, feed: true);
            }
        }

        /// <summary>L'action en cours (si la session travaille), puis l'avancement et la liste des tâches.</summary>
        private void DrawOverview(AgentSessionSummary selected, AgentSessionDetail detail)
        {
            var body = _layout.Body;
            var y = body.Top;

            if (selected.Activity == AgentActivity.Working && detail.Feed.Count > 0)
            {
                var action = detail.Feed[^1];
                var line = AgentLayout.FeedLineHeight * _scale;
                Text($"{action.Tool} {action.Target}", body.Left, y + line / 2f, SKTextAlign.Left, 24f, _theme.Text, body.Width);
                y += line;
            }

            var tasks = detail.Tasks;
            if (tasks.Count == 0)
            {
                return;
            }

            var done = tasks.Count(t => t.State == AgentTaskState.Completed);
            var progressHeight = 30f * _scale;
            var label = Text($"Tâches {done}/{tasks.Count}", body.Left, y + progressHeight / 2f, SKTextAlign.Left, 18f, _theme.TextMuted, body.Width);
            var barLeft = body.Left + label + 12f * _scale;
            var barRight = Math.Min(body.Right, barLeft + 220f * _scale);
            var barHeight = 8f * _scale;
            ModuleDrawing.DrawBar(_canvas,
                ModuleDrawing.BarRect(new SKRect(barLeft, y + progressHeight / 2f - barHeight / 2f, barRight, y + progressHeight / 2f + barHeight / 2f), _theme),
                (float)done / tasks.Count, _theme.Accent, _theme);
            y += progressHeight;

            var rowHeight = 30f * _scale;
            var capacity = (int)Math.Floor((body.Bottom - y) / rowHeight + 1e-4);
            var overflow = tasks.Count > capacity;
            var shown = overflow ? Math.Max(0, capacity - 1) : tasks.Count;
            for (var i = 0; i < shown; i++)
            {
                DrawTask(tasks[i], body.Left, y + i * rowHeight, rowHeight, body.Right);
            }

            if (overflow && capacity >= 1)
            {
                Text("+" + (tasks.Count - shown), body.Left, y + shown * rowHeight + rowHeight / 2f, SKTextAlign.Left, 18f, _theme.TextMuted, body.Width);
            }
        }

        /// <summary>Une tâche : case vide (à faire), pleine (faite) ou avec un point orange (en cours), puis son texte.</summary>
        private void DrawTask(AgentTask task, float left, float top, float height, float right)
        {
            // La case est centrée sur la hauteur des capitales du texte (et non sur sa boîte, plus haute que les lettres).
            var side = 14f * _scale;
            var font = Font(22f);
            var capHeight = font.Metrics.CapHeight > 0f ? font.Metrics.CapHeight : 0.7f * font.Size;
            var centerY = BaselineFor(22f, top + height / 2f) - capHeight / 2f;
            var box = new SKRect(left, centerY - side / 2f, left + side, centerY + side / 2f);
            if (task.State == AgentTaskState.Completed)
            {
                Fill(box, _theme.Accent);
            }
            else
            {
                var stroke = Math.Max(1f, 1.5f * _scale);
                using var outline = new SKPaint { Color = _theme.TextMuted, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = stroke };
                _canvas.DrawRect(SKRect.Inflate(box, -stroke / 2f, -stroke / 2f), outline);
                if (task.State == AgentTaskState.InProgress)
                {
                    using var dot = new SKPaint { Color = WorkingColor, IsAntialias = true, Style = SKPaintStyle.Fill };
                    _canvas.DrawCircle(box.MidX, box.MidY, side * 0.28f, dot);
                }
            }

            var x = box.Right + 10f * _scale;
            var color = task.State == AgentTaskState.Completed ? _theme.TextMuted : _theme.Text;
            Text(task.Text, x, top + height / 2f, SKTextAlign.Left, 22f, color, right - x);
        }

        // --- Barre du bas ---

        private void DrawBottomBar(AgentSessionDetail? detail)
        {
            var bar = _layout.Bar;
            var mascot = _layout.Mascot;
            _scene.Animator.Draw(_canvas,
                new SKRect(MathF.Round(mascot.Left), MathF.Round(mascot.Top), MathF.Round(mascot.Right), MathF.Round(mascot.Bottom)));

            var fields = new List<(string Label, string Value)>
            {
                ("Contexte", detail?.ContextPercent is { } context && !double.IsNaN(context) ? AgentText.Percent(context) : NoValue),
                ("Durée", detail?.TaskStartedAt is { } started ? AgentText.Duration((detail.TaskFinishedAt ?? _ctx.Now) - started) : NoValue),
            };
            if (_ctx.Size.Columns >= 7)
            {
                fields.Add(("Modèle", detail is null ? NoValue : ModelNames.Display(detail.Model)));
            }

            // Le modèle (« GPT-5.1 Codex Max ») est plus long que les deux chiffres : il reçoit une part double.
            var weights = fields.Select(f => f.Label == "Modèle" ? 2f : 1f).ToArray();
            var gap = AgentLayout.ColumnGap * _scale;
            var fieldsLeft = mascot.Right + gap;

            // Deux blocs de quota de 170 px ; ils ne rétrécissent (jusqu'à 120 px) que si les champs manquent de place.
            var blockGap = 12f * _scale;
            var room = bar.Right - fieldsLeft - gap - weights.Sum() * 84f * _scale - blockGap;
            var blockWidth = Math.Clamp(room / 2f, 120f * _scale, 170f * _scale);
            var quotasLeft = bar.Right - 2f * blockWidth - blockGap;

            var unit = Math.Max(0f, quotasLeft - gap - fieldsLeft) / weights.Sum();
            var x = fieldsLeft;
            for (var i = 0; i < fields.Count; i++)
            {
                var width = unit * weights[i];
                Text(fields[i].Label, x, bar.Top + 17f * _scale, SKTextAlign.Left, 15f, _theme.TextMuted, width - 8f * _scale);
                Text(fields[i].Value, x, bar.Top + 43f * _scale, SKTextAlign.Left, 24f, _theme.Text, width - 8f * _scale);
                x += width;
            }

            var quotas = _scene.Snapshot?.Quotas;
            if (quotas is { FiveHour: null, Weekly: null } or null)
            {
                Text("Quotas indisponibles", bar.Right, bar.MidY, SKTextAlign.Right, 16f, _theme.TextMuted, bar.Right - quotasLeft);
                return;
            }

            DrawQuota("5 h", quotas.FiveHour, quotasLeft, blockWidth);
            DrawQuota("Hebdo", quotas.Weekly, bar.Right - blockWidth, blockWidth);
        }

        /// <summary>
        /// Un quota : libellé et pourcentage, barre, moment de remise à zéro. Une fenêtre dont la remise à zéro est passée n'a
        /// plus de valeur fiable : « — », barre vide et « reset passé ».
        /// </summary>
        private void DrawQuota(string label, QuotaWindow? window, float left, float width)
        {
            var bar = _layout.Bar;
            var expired = window?.ResetsAt is { } at && at <= _ctx.Now;
            var known = window is not null && !expired && !double.IsNaN(window.UsedPercent);
            var percent = known ? DisplayQuotaPercent(window!.UsedPercent) : 0d;
            var half = width / 2f;

            var baseline = BaselineFor(20f, bar.Top + 14f * _scale);
            TextOnBaseline(label, left, baseline, SKTextAlign.Left, 15f, _theme.TextMuted, half);
            TextOnBaseline(known ? AgentText.Percent(percent) : NoValue, left + width, baseline, SKTextAlign.Right, 20f, _theme.Text, half);

            var top = bar.Top + 27f * _scale;
            ModuleDrawing.DrawBar(_canvas, ModuleDrawing.BarRect(new SKRect(left, top, left + width, top + 8f * _scale), _theme),
                (float)(percent / 100d), _theme.Accent, _theme);

            if (window?.ResetsAt is { } resetsAt)
            {
                // Les deux dates sont comparées à l'heure locale du PC, composantes en main.
                var reset = AgentText.Reset(resetsAt.ToLocalTime(), _ctx.Now.ToLocalTime());
                Text(reset, left, bar.Top + 49f * _scale, SKTextAlign.Left, 14f, _theme.TextMuted, width);
            }
        }

        // Le contexte reste consommé ; seuls les quotas Codex sont convertis en restant.
        private double DisplayQuotaPercent(double used)
        {
            var bounded = Math.Clamp(used, 0d, 100d);
            return _scene.Flavor.DisplayRemainingQuotaPercent ? 100d - bounded : bounded;
        }
    }
}
