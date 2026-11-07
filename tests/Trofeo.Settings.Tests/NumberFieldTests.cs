using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Trofeo.ModuleKit;
using Trofeo.Settings.Services;
using Trofeo.Settings.ViewModels.Fields;

namespace Trofeo.Settings.Tests;

/// <summary>Les champs <c>number</c> et <c>port</c> du formulaire d'un module : zone bornée, valeur proposée, message hors des bornes.</summary>
public sealed class NumberFieldTests
{
    private sealed class Context : IFieldContext
    {
        public string TypeId => "alex.demo";
        public TimeProvider Time { get; } = new FakeTimeProvider();
        public ISecretAccess Secrets { get; } = new FakeSecretAccess();
        public bool CanRunActions => true;
        public JsonElement Settings { get; set; } = JsonDocument.Parse("{}").RootElement.Clone();
        public int Edits { get; private set; }

        public Task EditAsync(Func<JsonElement, JsonElement> change)
        {
            var edited = change(Settings);
            if (edited.GetRawText() != Settings.GetRawText())
                Edits++;
            Settings = edited;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LookupResult>> LookupAsync(string fieldKey, string query) => Task.FromResult<IReadOnlyList<LookupResult>>([]);
        public Task<ActionResult> RunActionAsync(string actionKey) => Task.FromResult(new ActionResult(true, ""));
        public Task SecretChangedAsync() => Task.CompletedTask;
    }

    private static NumberFieldViewModel Field(Context context, SettingField field) =>
        FieldFactory.CreateForm(new SettingsSchema([field]), context).Field<NumberFieldViewModel>(field.Key);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Form_BuildsANumberFieldForNumberAndForPort_InsteadOfFailing()
    {
        var form = FieldFactory.CreateForm(new SettingsSchema(
        [
            new NumberField("count", "Nombre", 1, 10, 5),
            new PortField("port", "Port", 25565),
        ]), new Context());

        Assert.All(form.Fields, field => Assert.IsType<NumberFieldViewModel>(field));
        Assert.Equal(2, form.VisibleFields.Count);
    }

    [Fact]
    public async Task SettingsForm_NumberField_UsesDefault()
    {
        var context = new Context();
        var field = Field(context, new NumberField("count", "Nombre", 1, 10, 5));

        // Rien d'écrit : la zone propose la valeur par défaut, et la valider ne change rien.
        Assert.Equal("5", field.Text);
        Assert.False(field.IsDirty);
        field.Text = "5";
        await field.CommitAsync();
        Assert.Equal(0, context.Edits);
        Assert.False(field.HasError);

        // Une autre valeur s'écrit comme un nombre JSON, pas comme un texte.
        field.Text = " 7 ";
        await field.CommitAsync();
        Assert.Equal(JsonValueKind.Number, context.Settings.GetProperty("count").ValueKind);
        Assert.Equal(7, context.Settings.GetProperty("count").GetInt32());
        Assert.Equal("7", field.Text);

        // Une zone vidée revient à la valeur proposée : la clé disparaît.
        field.Text = "";
        await field.CommitAsync();
        Assert.False(context.Settings.TryGetProperty("count", out _));
        Assert.Equal("5", field.Text);
        Assert.False(field.HasError);
    }

    [Fact]
    public void SettingsForm_NumberField_ShowsTheStoredValue()
    {
        var field = Field(new Context { Settings = Json("""{"count":9}""") }, new NumberField("count", "Nombre", 1, 10, 5));

        Assert.Equal("9", field.Text);
        Assert.False(field.HasError);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("80.5")]
    [InlineData("80,5")]
    [InlineData("abc")]
    [InlineData("99999999999999999999")]
    public async Task SettingsForm_PortField_RejectsOutOfRange(string typed)
    {
        var context = new Context();
        var field = Field(context, new PortField("port", "Port du serveur", 25565));
        Assert.Equal("25565", field.Text);

        field.Text = typed;
        await field.CommitAsync();

        Assert.True(field.HasError);
        Assert.Contains("1", field.Error);
        Assert.Contains("65535", field.Error);
        Assert.Equal(0, context.Edits);           // rien n'est écrit
        Assert.Equal(typed, field.Text);          // et la saisie reste là, pour être corrigée
        Assert.True(field.IsDirty);

        // Corrigée, elle passe : le message disparaît.
        field.Text = "8123";
        await field.CommitAsync();
        Assert.False(field.HasError);
        Assert.Equal(8123, context.Settings.GetProperty("port").GetInt32());
    }

    [Theory]
    [InlineData("1")]
    [InlineData("65535")]
    public async Task SettingsForm_PortField_AcceptsTheBounds(string typed)
    {
        var context = new Context();
        var field = Field(context, new PortField("port", "Port", 25565));

        field.Text = typed;
        await field.CommitAsync();

        Assert.False(field.HasError);
        Assert.Equal(int.Parse(typed), context.Settings.GetProperty("port").GetInt32());
    }

    [Fact]
    public async Task SettingsForm_NumberField_UsesItsOwnBounds()
    {
        var context = new Context();
        var field = Field(context, new NumberField("count", "Nombre", 3, 10, 5));

        field.Text = "2";
        await field.CommitAsync();
        Assert.True(field.HasError);
        Assert.Contains("3", field.Error);
        Assert.Contains("10", field.Error);

        field.Text = "11";
        await field.CommitAsync();
        Assert.True(field.HasError);
        Assert.Equal(0, context.Edits);

        field.Text = "10";
        await field.CommitAsync();
        Assert.False(field.HasError);
        Assert.Equal(10, context.Settings.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task SettingsForm_PortField_ValueAlreadyWrittenAndEqualToTheDefault_IsKeptAsWritten()
    {
        // Le port choisi par l'utilisateur est celui que le service relaie : ce qu'il a écrit reste écrit.
        var context = new Context { Settings = Json("""{"port":25565}""") };
        var field = Field(context, new PortField("port", "Port", 25565));

        field.Text = "25565";
        await field.CommitAsync();

        Assert.Equal(0, context.Edits);
        Assert.Equal(25565, context.Settings.GetProperty("port").GetInt32());
    }

    [Fact]
    public void SettingsForm_StoredValueOutOfRange_IsShownWithItsMessage()
    {
        // Un fichier réglé à la main : la valeur est montrée telle quelle, avec le message, au lieu d'être cachée.
        var field = Field(new Context { Settings = Json("""{"port":99999}""") }, new PortField("port", "Port", 25565));

        Assert.Equal("99999", field.Text);
        Assert.True(field.HasError);
    }

    [Fact]
    public async Task SettingsForm_Refresh_KeepsATypedDraft()
    {
        var context = new Context();
        var field = Field(context, new PortField("port", "Port", 25565));
        field.Text = "80";

        field.Refresh();

        Assert.Equal("80", field.Text);
        Assert.True(field.IsDirty);
        await field.CommitPendingAsync();
        Assert.Equal(80, context.Settings.GetProperty("port").GetInt32());
    }
}
