using System.Text.Json;
using Trofeo.ModuleKit;

namespace Trofeo.Core.Tests;

public class SettingsSchemaTests
{
    private static readonly SettingOption[] Options = [new("pc", "Ce PC"), new("unifi", "UniFi")];

    private static SettingsSchema Sample() => new(
    [
        new ChoiceField("source", "Source", Options, "pc") { Help = "D'où viennent les chiffres." },
        new ToggleField("compact", "Affichage compact", true) { VisibleWhen = new FieldCondition("source", "unifi") },
        new TextField("unifi.host", "Adresse", 40) { Placeholder = "192.168.1.1" },
        new ListField("lines", "Lignes", Options, ["unifi", "pc"]),
        new SecretField("unifi.key", "Clé d'API"),
        new LookupField("site", "Site", false) { Placeholder = "Choisis", LabelPath = "title", EmptyLabel = "Aucun" },
        new ActionField("test", "Tester la connexion") { Confirm = "Sûr ?" },
    ]);

    private sealed class MinimalFactory : IConfigurableModuleFactory
    {
        public ModuleDescriptor Descriptor => new("x", "X", new GridSize(1, 1), new Dictionary<SizePreset, GridSize>());
        public SettingsSchema Schema => SettingsSchema.Empty;
        public IModule Create(ModuleContext context) => throw new NotSupportedException();
    }

    private sealed class NoSecrets : ISecretStore
    {
        public string? Get(string key) => null;
        public void Set(string key, string? value) { }
    }

    [Fact]
    public void Schema_RoundTripsThroughJson_WithKinds()
    {
        var schema = Sample();
        var json = JsonSerializer.Serialize(schema);

        using var doc = JsonDocument.Parse(json);
        var kinds = doc.RootElement.GetProperty("Fields").EnumerateArray()
            .Select(f => f.GetProperty("kind").GetString()!).ToArray();
        Assert.Equal(["choice", "toggle", "text", "list", "secret", "lookup", "action"], kinds);

        var back = JsonSerializer.Deserialize<SettingsSchema>(json)!;
        Assert.Equal(schema.Fields.Count, back.Fields.Count);
        for (var i = 0; i < schema.Fields.Count; i++)
            Assert.Equal(schema.Fields[i].GetType(), back.Fields[i].GetType());

        // Les listes d'un enregistrement se comparent par référence : on compare donc le JSON re-sérialisé.
        Assert.Equal(json, JsonSerializer.Serialize(back));

        var choice = Assert.IsType<ChoiceField>(back.Fields[0]);
        Assert.Equal("pc", choice.Default);
        Assert.Equal("D'où viennent les chiffres.", choice.Help);
        Assert.Equal(new SettingOption("unifi", "UniFi"), choice.Options[1]);
        Assert.Equal(new FieldCondition("source", "unifi"), back.Fields[1].VisibleWhen);
        var lookup = Assert.IsType<LookupField>(back.Fields[5]);
        Assert.False(lookup.Searchable);
        Assert.Equal("title", lookup.LabelPath);
        Assert.Equal("Aucun", lookup.EmptyLabel);
        Assert.Equal("Sûr ?", Assert.IsType<ActionField>(back.Fields[6]).Confirm);
    }

    [Fact]
    public void Schema_NumberAndPortFields_RoundTripAsJson()
    {
        var schema = new SettingsSchema(
        [
            new NumberField("days", "Jours", 1, 30, 7) { Help = "Entre 1 et 30." },
            new PortField("port", "Port", 25565) { VisibleWhen = new FieldCondition("days", "7") },
        ]);
        var json = JsonSerializer.Serialize(schema);

        using var doc = JsonDocument.Parse(json);
        var kinds = doc.RootElement.GetProperty("Fields").EnumerateArray()
            .Select(f => f.GetProperty("kind").GetString()!).ToArray();
        Assert.Equal(["number", "port"], kinds);

        var back = JsonSerializer.Deserialize<SettingsSchema>(json)!;
        var number = Assert.IsType<NumberField>(back.Fields[0]);
        Assert.Equal((1, 30, 7), (number.Min, number.Max, number.Default));
        Assert.Equal("Entre 1 et 30.", number.Help);
        var port = Assert.IsType<PortField>(back.Fields[1]);
        Assert.Equal(25565, port.Default);
        Assert.Equal(new FieldCondition("days", "7"), port.VisibleWhen);
        Assert.Equal(json, JsonSerializer.Serialize(back));
    }

    [Fact]
    public async Task Factory_DefaultLookupAndAction_AreHarmless()
    {
        IConfigurableModuleFactory factory = new MinimalFactory();

        var lookup = await factory.LookupAsync("site", "", CancellationToken.None);
        Assert.Empty(lookup);

        using var doc = JsonDocument.Parse("{}");
        var result = await factory.RunActionAsync("test", doc.RootElement, new NoSecrets(), CancellationToken.None);
        Assert.Equal(new ActionResult(false, "Action inconnue."), result);
    }

    [Fact]
    public void Descriptor_AcceptsCommands_DefaultsToFalse()
    {
        var presets = new Dictionary<SizePreset, GridSize>();
        Assert.False(new ModuleDescriptor("x", "X", new GridSize(1, 1), presets).AcceptsCommands);
        Assert.True(new ModuleDescriptor("x", "X", new GridSize(1, 1), presets, true).AcceptsCommands);
    }
}
