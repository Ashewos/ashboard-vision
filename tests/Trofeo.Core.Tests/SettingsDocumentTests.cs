using System.Text.Json;
using Trofeo.ModuleKit;

namespace Trofeo.Core.Tests;

public class SettingsDocumentTests
{
    private static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static readonly SettingOption[] Options = [new("pc", "Ce PC"), new("unifi", "UniFi")];

    [Fact]
    public void Set_NestedKey_CreatesObjects()
    {
        var result = SettingsDocument.Set(Json("{}"), "unifi.host", Json("\"1.2.3.4\""));
        Assert.Equal("""{"unifi":{"host":"1.2.3.4"}}""", result.GetRawText());
        Assert.Equal("1.2.3.4", SettingsDocument.GetString(result, "unifi.host"));
    }

    [Fact]
    public void Set_Null_RemovesKey_KeepsOthers()
    {
        var input = Json("""{"unifi":{"host":"h","port":1},"a":true}""");
        var result = SettingsDocument.Set(input, "unifi.host", null);
        Assert.Equal("""{"unifi":{"port":1},"a":true}""", result.GetRawText());
        // L'entrée n'est pas modifiée.
        Assert.Equal("""{"unifi":{"host":"h","port":1},"a":true}""", input.GetRawText());
        // Supprimer une clé absente ne casse rien.
        Assert.Equal("""{"a":true}""", SettingsDocument.Set(Json("""{"a":true}"""), "x.y", null).GetRawText());
    }

    [Fact]
    public void Set_UndefinedValue_ActsLikeNull()
    {
        var input = Json("""{"a":1,"b":{"c":2}}""");

        Assert.Equal("""{"b":{"c":2}}""", SettingsDocument.Set(input, "a", default(JsonElement)).GetRawText());
        Assert.Equal("""{"a":1,"b":{}}""", SettingsDocument.Set(input, "b.c", default(JsonElement)).GetRawText());
        Assert.Equal("""{"a":1,"b":{"c":2}}""", SettingsDocument.Set(input, "x.y", default(JsonElement)).GetRawText());
    }

    [Fact]
    public void Set_OnNonObjectRoot_StartsFromEmpty()
    {
        Assert.Equal("""{"a":1}""", SettingsDocument.Set(Json("[1,2]"), "a", Json("1")).GetRawText());
        Assert.Equal("""{"a":1}""", SettingsDocument.Set(default, "a", Json("1")).GetRawText());
        // Un intermédiaire qui n'est pas un objet est remplacé.
        Assert.Equal("""{"a":{"b":2}}""", SettingsDocument.Set(Json("""{"a":5}"""), "a.b", Json("2")).GetRawText());
    }

    [Fact]
    public void Get_MissingOrWrongShape_IsNull()
    {
        var s = Json("""{"a":{"b":"x"},"n":3,"l":["p","q",1]}""");
        Assert.Null(SettingsDocument.Get(s, "zzz"));
        Assert.Null(SettingsDocument.Get(s, "a.b.c"));
        Assert.Null(SettingsDocument.Get(s, "n.x"));
        Assert.Null(SettingsDocument.Get(Json("[1]"), "a"));
        Assert.Null(SettingsDocument.Get(default, "a"));
        Assert.Null(SettingsDocument.GetString(s, "n"));
        Assert.Null(SettingsDocument.GetBool(s, "a.b"));
        Assert.Empty(SettingsDocument.GetStrings(s, "n"));
        Assert.Empty(SettingsDocument.GetStrings(s, "missing"));

        Assert.Equal("x", SettingsDocument.GetString(s, "a.b"));
        Assert.Equal(3, SettingsDocument.Get(s, "n")!.Value.GetInt32());
        Assert.Equal(["p", "q"], SettingsDocument.GetStrings(s, "l"));
        Assert.True(SettingsDocument.GetBool(Json("""{"t":true}"""), "t"));
        Assert.False(SettingsDocument.GetBool(Json("""{"t":false}"""), "t"));
    }

    [Fact]
    public void Get_And_Set_ReturnClonesSurvivingDisposal()
    {
        JsonElement got;
        JsonElement set;
        using (var doc = JsonDocument.Parse("""{"a":{"b":1}}"""))
        {
            got = SettingsDocument.Get(doc.RootElement, "a")!.Value;
            set = SettingsDocument.Set(doc.RootElement, "c", Json("2"));
        }
        Assert.Equal("""{"b":1}""", got.GetRawText());
        Assert.Equal("""{"a":{"b":1},"c":2}""", set.GetRawText());
    }

    [Fact]
    public void IsVisible_UsesDefaultWhenConditionKeyAbsent()
    {
        var schema = new SettingsSchema(
        [
            new ChoiceField("source", "Source", Options, "pc"),
            new ToggleField("compact", "Compact", true),
            new TextField("note", "Note"),
            new SecretField("key", "Clé"),
        ]);
        var host = new TextField("host", "Hôte") { VisibleWhen = new FieldCondition("source", "unifi") };
        var empty = Json("{}");

        Assert.False(SettingsDocument.IsVisible(host, empty, schema));
        Assert.True(SettingsDocument.IsVisible(host, Json("""{"source":"unifi"}"""), schema));
        Assert.False(SettingsDocument.IsVisible(host, Json("""{"source":"pc"}"""), schema));

        var onPc = host with { VisibleWhen = new FieldCondition("source", "pc") };
        Assert.True(SettingsDocument.IsVisible(onPc, empty, schema));

        // Toggle : Default rendu "true"/"false".
        Assert.True(SettingsDocument.IsVisible(host with { VisibleWhen = new FieldCondition("compact", "true") }, empty, schema));
        Assert.False(SettingsDocument.IsVisible(host with { VisibleWhen = new FieldCondition("compact", "false") }, empty, schema));
        Assert.True(SettingsDocument.IsVisible(host with { VisibleWhen = new FieldCondition("compact", "false") }, Json("""{"compact":false}"""), schema));

        // Texte absent : "".
        Assert.True(SettingsDocument.IsVisible(host with { VisibleWhen = new FieldCondition("note", "") }, empty, schema));
        Assert.False(SettingsDocument.IsVisible(host with { VisibleWhen = new FieldCondition("note", "x") }, empty, schema));

        // Nombre et port : le défaut s'écrit comme un nombre JSON.
        var numeric = new SettingsSchema([new NumberField("days", "Jours", 1, 30, 7), new PortField("port", "Port", 8123)]);
        Assert.True(SettingsDocument.IsVisible(host with { VisibleWhen = new FieldCondition("days", "7") }, empty, numeric));
        Assert.False(SettingsDocument.IsVisible(host with { VisibleWhen = new FieldCondition("days", "8") }, empty, numeric));
        Assert.True(SettingsDocument.IsVisible(host with { VisibleWhen = new FieldCondition("port", "8123") }, empty, numeric));
        Assert.True(SettingsDocument.IsVisible(host with { VisibleWhen = new FieldCondition("days", "9") }, Json("""{"days":9}"""), numeric));

        // Autres genres ou champ inconnu : absent = différent.
        Assert.False(SettingsDocument.IsVisible(host with { VisibleWhen = new FieldCondition("key", "") }, empty, schema));
        Assert.False(SettingsDocument.IsVisible(host with { VisibleWhen = new FieldCondition("nope", "") }, empty, schema));

        // Sans condition : visible.
        Assert.True(SettingsDocument.IsVisible(new TextField("h", "H"), empty, schema));
    }
}
