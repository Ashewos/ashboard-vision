using System.Text.Json.Nodes;

namespace Trofeo.Modules.Agents.Tests;

/// <summary>
/// Le retrait de l'ancienne barre d'état (<c>Trofeo.StatusLine.exe</c>) et le retour à la configuration d'avant. L'état
/// « installé » est posé à la main, tel que l'ancienne version du logiciel le laissait dans <c>settings.json</c>.
/// </summary>
public sealed class StatusLineInstallerTests : IDisposable
{
    private const string Exe = "C:/x/Trofeo.StatusLine.exe";

    private readonly string _home = Path.Combine(Path.GetTempPath(), "trofeo-inst-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
    }

    private string SettingsPath => Path.Combine(_home, "settings.json");
    private string PreviousPath => Path.Combine(_home, "trofeo-HB-dashboard", "statusline-previous.json");

    private StatusLineInstaller Installer() => new(_home);

    private void WriteSettings(string json)
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(SettingsPath, json);
    }

    private void WritePrevious(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PreviousPath)!);
        File.WriteAllText(PreviousPath, json);
    }

    private JsonObject ReadSettings() => (JsonObject)JsonNode.Parse(File.ReadAllText(SettingsPath))!;

    /// <summary>
    /// Ce que l'ancienne installation laissait : la commande de <c>settings.json</c> (enveloppant éventuellement l'ancienne, dans un
    /// tube) et la sauvegarde de l'ancien nœud <c>statusLine</c>. <paramref name="previous"/> est le JSON de l'ancien nœud, ou
    /// <c>null</c> s'il n'y en avait pas ; <paramref name="otherKeys"/> les autres clés du fichier, sans accolades.
    /// </summary>
    private void WriteInstalled(string? previous, string? otherKeys = null)
    {
        var node = previous is null ? null : JsonNode.Parse(previous);
        JsonNode statusLine;
        if (node is JsonObject old && old["type"]?.GetValue<string>() == "command" && old["command"]?.GetValue<string>() is { } oldCommand)
        {
            var chained = (JsonObject)old.DeepClone();
            chained["command"] = $"\"{Exe}\" --tee | ( {oldCommand} )";
            statusLine = chained;
        }
        else
        {
            statusLine = new JsonObject { ["type"] = "command", ["command"] = $"\"{Exe}\"" };
        }

        WriteSettings("{" + (otherKeys is null ? "" : otherKeys + ",") + "\"statusLine\":" + statusLine.ToJsonString() + "}");
        WritePrevious(new JsonObject { ["previous"] = node?.DeepClone() }.ToJsonString());
    }

    [Fact]
    public void IsInstalled_OurCommand_True()
    {
        WriteInstalled(null);

        Assert.True(Installer().IsInstalled());
    }

    [Fact]
    public void IsInstalled_IgnoresCase()
    {
        WriteSettings("""{"statusLine":{"type":"command","command":"\"c:/X/TROFEO.STATUSLINE.exe\""}}""");

        Assert.True(Installer().IsInstalled());
    }

    [Theory]
    [InlineData("")]
    [InlineData("""{"statusLine":{"type":"command","command":"bash ~/sl.sh"}}""")]
    [InlineData("""{"model":"opus"}""")]
    [InlineData("{pas du json")]
    public void IsInstalled_OtherwiseFalse(string content)
    {
        if (content.Length > 0) WriteSettings(content);

        Assert.False(Installer().IsInstalled());
    }

    [Fact]
    public void Uninstall_RestoresPreviousExactly()
    {
        const string original = """{"type":"command","command":"bash ~/sl.sh","padding":2,"refreshInterval":5}""";
        WriteInstalled(original, "\"model\":\"opus\",\"theme\":\"dark\"");

        var result = Installer().Uninstall();

        Assert.Equal(StatusLineInstallResult.Uninstalled, result);
        var root = ReadSettings();
        Assert.Equal(JsonNode.Parse(original)!.ToJsonString(), root["statusLine"]!.ToJsonString());
        Assert.Equal("opus", (string?)root["model"]);
        Assert.Equal("dark", (string?)root["theme"]);
        Assert.False(File.Exists(PreviousPath));
        Assert.False(Installer().IsInstalled());
    }

    [Fact]
    public void Uninstall_ChainedCompoundCommand_RestoresIt()
    {
        WriteInstalled("""{"type":"command","command":"cd ~/outils && ./sl.sh"}""");

        Assert.Equal(StatusLineInstallResult.Uninstalled, Installer().Uninstall());

        Assert.Equal("cd ~/outils && ./sl.sh", (string?)ReadSettings()["statusLine"]!["command"]);
    }

    [Fact]
    public void Uninstall_NonCommandPrevious_IsRestored()
    {
        WriteInstalled("\"simple\"");

        Assert.Equal(StatusLineInstallResult.Uninstalled, Installer().Uninstall());

        Assert.Equal("simple", (string?)ReadSettings()["statusLine"]);
    }

    [Fact]
    public void Uninstall_NoPrevious_RemovesStatusLine()
    {
        WriteInstalled(null, "\"model\":\"opus\"");

        var result = Installer().Uninstall();

        Assert.Equal(StatusLineInstallResult.Uninstalled, result);
        var root = ReadSettings();
        Assert.False(root.ContainsKey("statusLine"));
        Assert.Equal("opus", (string?)root["model"]);
        Assert.False(File.Exists(PreviousPath));
    }

    [Fact]
    public void Uninstall_WritesUtf8WithoutBom_AndLeavesNoTempFile()
    {
        WriteInstalled(null);

        Installer().Uninstall();

        Assert.NotEqual(0xEF, File.ReadAllBytes(SettingsPath)[0]);
        Assert.Empty(Directory.GetFiles(_home, "*.tmp"));
    }

    [Fact]
    public void Uninstall_Utf8BomAndComments_AreAccepted()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllBytes(SettingsPath,
            [0xEF, 0xBB, 0xBF, .. "{ // mon modèle\n \"model\": \"opus\", \"statusLine\": {\"type\":\"command\",\"command\":\"\\\"C:/x/Trofeo.StatusLine.exe\\\"\"}, }"u8]);

        Assert.Equal(StatusLineInstallResult.Uninstalled, Installer().Uninstall());

        Assert.Equal("opus", (string?)ReadSettings()["model"]);
    }

    [Fact]
    public void Uninstall_PreviousFileMissing_RemovesStatusLine()
    {
        WriteInstalled(null);
        File.Delete(PreviousPath);

        Assert.Equal(StatusLineInstallResult.Uninstalled, Installer().Uninstall());
        Assert.False(ReadSettings().ContainsKey("statusLine"));
    }

    [Fact]
    public void Uninstall_PreviousFileMissing_ChainedCommand_ChangesNothing()
    {
        WriteInstalled("""{"type":"command","command":"bash ~/sl.sh"}""");
        File.Delete(PreviousPath);
        var before = File.ReadAllBytes(SettingsPath);

        Assert.Equal(StatusLineInstallResult.InvalidSettings, Installer().Uninstall());
        Assert.Equal(before, File.ReadAllBytes(SettingsPath)); // la commande d'origine n'est pas jetée
        Assert.True(Installer().IsInstalled());
    }

    [Theory]
    [InlineData("""{"previous":null}""")]
    [InlineData("{}")]
    public void Uninstall_PreviousFileWithoutPrevious_ChainedCommand_ChangesNothing(string saved)
    {
        WriteInstalled("""{"type":"command","command":"bash ~/sl.sh"}""");
        WritePrevious(saved);
        var before = File.ReadAllBytes(SettingsPath);

        Assert.Equal(StatusLineInstallResult.InvalidSettings, Installer().Uninstall());
        Assert.Equal(before, File.ReadAllBytes(SettingsPath)); // la commande d'origine n'est pas jetée
        Assert.True(File.Exists(PreviousPath));
        Assert.True(Installer().IsInstalled());
    }

    [Fact]
    public void Uninstall_PreviousFileWithoutPrevious_UnchainedCommand_RemovesStatusLine()
    {
        WriteInstalled(null, "\"model\":\"opus\"");
        WritePrevious("{}");

        Assert.Equal(StatusLineInstallResult.Uninstalled, Installer().Uninstall());
        Assert.False(ReadSettings().ContainsKey("statusLine"));
    }

    [Fact]
    public void Uninstall_DuplicateKeysInSettingsOrPreviousFile_ChangesNothing()
    {
        WriteInstalled("""{"type":"command","command":"bash ~/sl.sh"}""");
        WritePrevious("""{"previous":null,"previous":null}""");
        var before = File.ReadAllBytes(SettingsPath);
        Assert.Equal(StatusLineInstallResult.InvalidSettings, Installer().Uninstall());
        Assert.Equal(before, File.ReadAllBytes(SettingsPath));

        File.WriteAllText(SettingsPath, """{"a":1,"a":2}""");
        Assert.Equal(StatusLineInstallResult.InvalidSettings, Installer().Uninstall());
    }

    [Theory]
    [InlineData("""{"model":"opus","model":"sonnet"}""")]
    [InlineData("""{"hooks":{"a":1,"a":2}}""")]
    [InlineData("""{"list":[{"a":1,"a":2}]}""")]
    public void Uninstall_DuplicateKeysAnywhere_ChangesNothing(string content)
    {
        WriteSettings(content);
        var before = File.ReadAllBytes(SettingsPath);

        Assert.Equal(StatusLineInstallResult.InvalidSettings, Installer().Uninstall());
        Assert.Equal(before, File.ReadAllBytes(SettingsPath));
        Assert.False(Installer().IsInstalled());
    }

    [Fact]
    public void Uninstall_CorruptPreviousFile_InvalidUtf8_ChangesNothing()
    {
        WriteInstalled("""{"type":"command","command":"bash ~/sl.sh"}""");
        File.WriteAllBytes(PreviousPath, [.. "{\"previous\":\"caf"u8, 0xE9, .. "\"}"u8]);
        var before = File.ReadAllBytes(SettingsPath);

        Assert.Equal(StatusLineInstallResult.InvalidSettings, Installer().Uninstall());
        Assert.Equal(before, File.ReadAllBytes(SettingsPath));
    }

    [Fact]
    public void Uninstall_InvalidUtf8Settings_ChangesNothing()
    {
        Directory.CreateDirectory(_home);
        byte[] bytes = [.. "{\"note\":\"caf"u8, 0xE9, .. "\"}"u8]; // 0xE9 isolé : Windows-1252, pas de l'UTF-8
        File.WriteAllBytes(SettingsPath, bytes);

        Assert.Equal(StatusLineInstallResult.InvalidSettings, Installer().Uninstall());
        Assert.Equal(bytes, File.ReadAllBytes(SettingsPath));
    }

    [Fact]
    public void Uninstall_Foreign_NotInstalled()
    {
        const string foreign = """{"statusLine":{"type":"command","command":"bash ~/sl.sh"}}""";
        WriteSettings(foreign);
        var before = File.ReadAllBytes(SettingsPath);

        var result = Installer().Uninstall();

        Assert.Equal(StatusLineInstallResult.NotInstalled, result);
        Assert.Equal(before, File.ReadAllBytes(SettingsPath));
    }

    [Fact]
    public void Uninstall_NoSettings_NotInstalled()
    {
        Assert.Equal(StatusLineInstallResult.NotInstalled, Installer().Uninstall());
    }

    [Theory]
    [InlineData("{pas du json")]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    [InlineData("\"texte\"")]
    [InlineData("")]
    public void Uninstall_InvalidSettings_ChangesNothing(string content)
    {
        WriteSettings(content);
        var before = File.ReadAllBytes(SettingsPath);

        Assert.Equal(StatusLineInstallResult.InvalidSettings, Installer().Uninstall());
        Assert.Equal(before, File.ReadAllBytes(SettingsPath));
    }

    [Fact]
    public void Uninstall_CorruptPreviousFile_ChangesNothing()
    {
        WriteInstalled("""{"type":"command","command":"bash ~/sl.sh"}""");
        WritePrevious("{abîmé");
        var before = File.ReadAllBytes(SettingsPath);

        Assert.Equal(StatusLineInstallResult.InvalidSettings, Installer().Uninstall());
        Assert.Equal(before, File.ReadAllBytes(SettingsPath));
        Assert.True(Installer().IsInstalled());
    }
}
