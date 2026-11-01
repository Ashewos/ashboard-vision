using SkiaSharp;
using Trofeo.Control;
using Trofeo.ModuleKit;

namespace Trofeo.Settings.Tests;

/// <summary>Des réponses du service toutes prêtes, pour les tests de la fenêtre.</summary>
public static class SampleData
{
    public static Welcome Welcome() => new("0.5.0", ControlPipe.ProtocolVersion, Types());

    private static ModuleTypeInfo Type(string id, string name, GridSize min, GridSize compact, GridSize ideal, GridSize large,
        SettingsSchema? schema = null, bool acceptsCommands = false) =>
        new(id, name, min, new Dictionary<SizePreset, GridSize>
        {
            [SizePreset.Compact] = compact,
            [SizePreset.Ideal] = ideal,
            [SizePreset.Large] = large,
        }, acceptsCommands, schema ?? SettingsSchema.Empty);

    /// <summary>Les six types du service, avec leurs noms, leurs tailles et leurs schémas de réglages réels.</summary>
    public static IReadOnlyList<ModuleTypeInfo> Types() =>
    [
        Type("claudecode", "Claude Code", new(6, 3), new(6, 3), new(8, 4), new(10, 4), new SettingsSchema(
        [
            .. AgentFields(),
            new ActionField("install-quotas", "Installer le mod des quotas")
            {
                Confirm = "Le mod ashboard-quotas sera ajouté à Claude Code pour ton compte (claude plugin install). "
                    + "Tu pourras le retirer avec claude plugin uninstall ashboard-quotas@ashboard-vision.",
            },
        ]), acceptsCommands: true),
        Type("codex", "Codex", new(6, 3), new(6, 3), new(8, 4), new(10, 4), new SettingsSchema(AgentFields()), acceptsCommands: true),
        Type("monitoring", "Monitoring PC", new(3, 2), new(4, 2), new(6, 2), new(6, 4), MonitoringSchema),
        Type("nowplaying", "En écoute", new(3, 1), new(3, 1), new(4, 2), new(6, 2)),
        Type("clock", "Heure, date et météo", new(3, 1), new(3, 1), new(4, 2), new(6, 2), ClockSchema),
        Type("network", "Réseau", new(3, 1), new(3, 1), new(4, 2), new(6, 2), NetworkSchema),
    ];

    // Les schémas ci-dessous sont copiés des vraies fabriques (ClockFactory, NetworkFactory, AgentSchema, MonitoringFactory).

    private static readonly FieldCondition IfUniFi = new("source", "unifi");

    public static SettingsSchema ClockSchema { get; } = new(
    [
        new ChoiceField("format", "Format de l'heure",
            [new SettingOption("24h", "24 h"), new SettingOption("12h", "12 h (AM/PM)")], "24h"),
        new ChoiceField("unit", "Unité", [new SettingOption("c", "°C"), new SettingOption("f", "°F")], "c"),
        new LookupField("location", "Ville de la météo")
        {
            Placeholder = "Cherche une ville",
            EmptyLabel = "Pas de météo",
        },
    ]);

    public static SettingsSchema NetworkSchema { get; } = new(
    [
        new ChoiceField("source", "Source du débit",
            [new SettingOption("pc", "Ce PC"), new SettingOption("freebox", "Freebox"), new SettingOption("unifi", "UniFi")], "pc"),
        new TextField("unifi.host", "Adresse de la console") { Placeholder = "192.168.1.1", VisibleWhen = IfUniFi },
        new SecretField("unifi.apiKey", "Clé API de la console") { VisibleWhen = IfUniFi },
        new ActionField("forget-certificate", "Oublier le certificat de la console")
        {
            Confirm = "La prochaine connexion fera confiance au certificat présenté. Ne le fais qu'après un renouvellement que tu connais.",
            VisibleWhen = IfUniFi,
        },
    ]);

    public static SettingsSchema MonitoringSchema { get; } = new(
    [
        new TextField("title", "Titre", MaxLength: 60) { Placeholder = "Nom du matériel détecté" },
        new ListField("rows", "Lignes affichées",
            [
                new SettingOption("cpu", "Processeur"),
                new SettingOption("gpu", "Carte graphique"),
                new SettingOption("ram", "Mémoire"),
                new SettingOption("vram", "Mémoire vidéo"),
                new SettingOption("clock", "Fréquences"),
                new SettingOption("power", "Consommation"),
                new SettingOption("fan", "Ventilateur"),
                new SettingOption("disk", "Disque"),
            ],
            ["cpu", "gpu", "ram"]),
        new LookupField("gpu", "Carte graphique surveillée", Searchable: false)
        {
            EmptyLabel = "Automatique (celle qui a le plus de mémoire)",
        },
    ]);

    private static IReadOnlyList<SettingField> AgentFields() =>
    [
        new ChoiceField("view", "Affichage",
            [new SettingOption("feed", "Fil d'activité"), new SettingOption("overview", "Vue d'ensemble")], "feed"),
        new ToggleField("autoFollow", "Passer à la session qui t'attend", true),
        new ToggleField("feedFade", "Faire pâlir les lignes anciennes", false),
        new ChoiceField("feedNewest", "Ligne la plus récente",
            [new SettingOption("top", "En haut"), new SettingOption("bottom", "En bas, comme un terminal")], "top"),
    ];

    public static ServiceStatus Status(LinkScreenState screen = LinkScreenState.Ready, bool emulated = false) => new(
        new ScreenInfo(screen, null), emulated, [],
        new ProcessInfo(1_000_000, 2_000_000, 1.5), new FrameInfo(10, 4, 1), new SensorInfo(true, true, true));

    /// <summary>Une image de l'écran (JPEG 1920 × 462) d'une couleur unie.</summary>
    public static byte[] Jpeg(SKColor color)
    {
        using var bitmap = new SKBitmap(1920, 462);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Jpeg, 90).ToArray();
    }
}
