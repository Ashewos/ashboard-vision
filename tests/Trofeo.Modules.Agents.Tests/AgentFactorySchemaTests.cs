using Trofeo.ModuleKit;

namespace Trofeo.Modules.Agents.Tests;

public class AgentFactorySchemaTests
{
    private static readonly string[] CommonKeys = ["view", "autoFollow", "feedFade", "feedNewest"];

    private static IConfigurableModuleFactory Claude() => new ClaudeCodeFactory(_ => throw new InvalidOperationException());

    private static IConfigurableModuleFactory Codex() => new CodexFactory(_ => throw new InvalidOperationException());

    private static void AssertCommonFields(IReadOnlyList<SettingField> fields)
    {
        Assert.Equal(CommonKeys, fields.Take(4).Select(f => f.Key));

        var view = Assert.IsType<ChoiceField>(fields[0]);
        Assert.Equal("Affichage", view.Label);
        Assert.Equal([new SettingOption("feed", "Fil d'activité"), new SettingOption("overview", "Vue d'ensemble")], view.Options);
        Assert.Equal("feed", view.Default);

        var follow = Assert.IsType<ToggleField>(fields[1]);
        Assert.Equal("Passer à la session qui t'attend", follow.Label);
        Assert.True(follow.Default);

        var fade = Assert.IsType<ToggleField>(fields[2]);
        Assert.Equal("Faire pâlir les lignes anciennes", fade.Label);
        Assert.False(fade.Default);

        var newest = Assert.IsType<ChoiceField>(fields[3]);
        Assert.Equal("Ligne la plus récente", newest.Label);
        Assert.Equal([new SettingOption("top", "En haut"), new SettingOption("bottom", "En bas, comme un terminal")], newest.Options);
        Assert.Equal("top", newest.Default);
    }

    [Fact]
    public void Agents_Schemas_AndAcceptsCommands()
    {
        var claude = Claude();
        var codex = Codex();

        AssertCommonFields(claude.Schema.Fields);
        AssertCommonFields(codex.Schema.Fields);

        Assert.Equal([.. CommonKeys, "install-quotas"], claude.Schema.Fields.Select(f => f.Key));
        Assert.Equal(CommonKeys, codex.Schema.Fields.Select(f => f.Key));

        var install = Assert.IsType<ActionField>(claude.Schema.Fields[4]);
        Assert.Equal("Installer le mod des quotas", install.Label);
        Assert.Equal(
            "Le mod ashboard-quotas sera ajouté à Claude Code pour ton compte (claude plugin install). "
            + "Tu pourras le retirer avec claude plugin uninstall ashboard-quotas@ashboard-vision.",
            install.Confirm);

        Assert.True(claude.Descriptor.AcceptsCommands);
        Assert.True(codex.Descriptor.AcceptsCommands);
    }

    [Fact]
    public async Task Claude_InstallQuotas_UsesTheInstaller()
    {
        var calls = new List<(string File, string Args)>();
        var installer = new QuotaModInstaller((file, args, _) =>
        {
            calls.Add((file, args));
            return Task.FromResult((0, ""));
        }, Path.GetTempPath());
        var factory = new ClaudeCodeFactory(_ => throw new InvalidOperationException(), installer);

        var result = await ((IConfigurableModuleFactory)factory).RunActionAsync("install-quotas", default, new NoSecrets(), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public async Task Claude_UnknownAction_IsRefused_AndCodexHasNoActions()
    {
        var unknown = await Claude().RunActionAsync("zzz", default, new NoSecrets(), CancellationToken.None);
        var codex = await Codex().RunActionAsync("install-quotas", default, new NoSecrets(), CancellationToken.None);

        Assert.False(unknown.Ok);
        Assert.False(codex.Ok);
    }

    private sealed class NoSecrets : ISecretStore
    {
        public string? Get(string key) => null;
        public void Set(string key, string? value) { }
    }
}
