namespace Trofeo.Modules.Agents.Tests;

public sealed class StatusLineFileTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "trofeo-sl-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static StatusLineRecord Sample(string id, DateTimeOffset at) => new(
        at, id, "claude-opus-5-5", 200000, 42.5,
        new CapturedQuota(31, new DateTimeOffset(2026, 10, 25, 15, 0, 0, TimeSpan.Zero)),
        null);

    [Fact]
    public void Read_ReadsWhatTheModWrites()
    {
        // Le JSON tel que mods/ashboard-quotas/hooks/register.ts l'écrit (JSON.stringify d'une capture).
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "abc-123.json");
        File.WriteAllText(path,
            """{"capturedAt":"2026-10-25T15:07:36.000Z","sessionId":"abc-123","modelId":"claude-opus-5-5","contextWindowSize":1000000,"contextUsedPercent":5,"fiveHour":{"usedPercent":40,"resetsAt":"2026-10-25T19:00:00.000Z"},"sevenDay":null}""");

        var record = StatusLineFile.Read(path);

        Assert.Equal(
            new StatusLineRecord(
                new DateTimeOffset(2026, 10, 25, 15, 7, 36, TimeSpan.Zero), "abc-123", "claude-opus-5-5", 1_000_000, 5,
                new CapturedQuota(40, new DateTimeOffset(2026, 10, 25, 19, 0, 0, TimeSpan.Zero)), null),
            record);
    }

    [Fact]
    public void Read_RoundTripsWhatTheTestsWrite()
    {
        var record = Sample("s-1", Now);
        CaptureFiles.Write(_dir, record);

        Assert.Equal(record, StatusLineFile.Read(Path.Combine(_dir, "s-1.json")));
    }

    [Theory]
    [InlineData("{pas du json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{"capturedAt":"2026-10-25T12:00:00Z","sessionId":"../evil"}""")]
    public void Read_CorruptOrInvalid_ReturnsNull(string content)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "x.json");
        File.WriteAllText(path, content);

        Assert.Null(StatusLineFile.Read(path));
    }

    [Fact]
    public void Read_MissingFile_ReturnsNull()
    {
        Assert.Null(StatusLineFile.Read(Path.Combine(_dir, "absent.json")));
    }

    [Fact]
    public void Read_WhileAnotherProcessReplacesTheFile_StillReads()
    {
        CaptureFiles.Write(_dir, Sample("s-1", Now));
        var path = Path.Combine(_dir, "s-1.json");

        // Le mod remplace le fichier pendant qu'on le lit : le partage large le permet.
        using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

        Assert.NotNull(StatusLineFile.Read(path));
    }

    [Fact]
    public void DeleteOlderThan_KeepsRecentAndRemovesOldAndCorrupt()
    {
        CaptureFiles.Write(_dir, Sample("recent", Now.AddMinutes(-10)));
        CaptureFiles.Write(_dir, Sample("old", Now.AddDays(-9)));
        File.WriteAllText(Path.Combine(_dir, "corrupt.json"), "{pas du json");
        File.SetLastWriteTimeUtc(Path.Combine(_dir, "corrupt.json"), Now.AddDays(-9).UtcDateTime); // illisible : sa date d'écriture fait foi

        StatusLineFile.DeleteOlderThan(_dir, Now, TimeSpan.FromDays(7));

        Assert.True(File.Exists(Path.Combine(_dir, "recent.json")));
        Assert.False(File.Exists(Path.Combine(_dir, "old.json")));
        Assert.False(File.Exists(Path.Combine(_dir, "corrupt.json")));
    }

    [Fact]
    public void DeleteOlderThan_MissingDirectory_DoesNothing()
    {
        StatusLineFile.DeleteOlderThan(Path.Combine(_dir, "absent"), Now, TimeSpan.FromDays(7));

        Assert.False(Directory.Exists(_dir));
    }

    [Theory]
    [InlineData("abc-123", true)]
    [InlineData("A", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("../evil", false)]
    [InlineData("a b", false)]
    [InlineData("a.json", false)]
    public void IsValidSessionId_AcceptsOnlyLettersDigitsAndDashes(string? id, bool expected)
    {
        Assert.Equal(expected, StatusLineFile.IsValidSessionId(id));
    }

    [Fact]
    public void IsValidSessionId_RejectsMoreThan64Characters()
    {
        Assert.True(StatusLineFile.IsValidSessionId(new string('a', 64)));
        Assert.False(StatusLineFile.IsValidSessionId(new string('a', 65)));
    }
}
