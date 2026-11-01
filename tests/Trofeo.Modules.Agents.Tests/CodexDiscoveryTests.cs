using Microsoft.Extensions.Time.Testing;

namespace Trofeo.Modules.Agents.Tests;

public sealed class CodexDiscoveryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 18, 0, 0, TimeSpan.Zero);
    private readonly string _home = Path.Combine(Path.GetTempPath(), "trofeo-codex-discovery-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(Now);

    public CodexDiscoveryTests()
    {
        Directory.CreateDirectory(_home);
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
    }

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private string Rollout(string id, int daysOld, DateTimeOffset modified, params string[] events)
    {
        var created = Now.AddDays(-daysOld);
        var directory = Path.Combine(_home, "sessions", created.ToString("yyyy"), created.ToString("MM"), created.ToString("dd"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"rollout-{id}.jsonl");
        File.WriteAllText(path, CodexTestLines.Meta(id, _home, created) + "\n" + string.Concat(events.Select(e => e + "\n")));
        File.SetLastWriteTimeUtc(path, modified.UtcDateTime);
        return path;
    }

    [Theory]
    [InlineData(3)]
    [InlineData(45)]
    [InlineData(400)]
    public void Source_ResumedConversation_InOldCreationFolder_IsDiscovered(int daysOld)
    {
        Rollout("resumed", daysOld, Now.AddMinutes(-1), CodexTestLines.Event("task_started", Now.AddMinutes(-1)));
        using var source = new CodexSource(_home, _time);

        Assert.Equal("resumed", Assert.Single(source.Poll(null, Now).Sessions).Id);
    }

    [Fact]
    public void Source_RecentEvent_WithStaleWindowsWriteTime_IsDiscovered()
    {
        Rollout("active", 1, Now.AddDays(-1), CodexTestLines.Event("task_started", Now.AddMinutes(-1)));
        using var source = new CodexSource(_home, _time);

        Assert.Equal("active", Assert.Single(source.Poll(null, Now).Sessions).Id);
        Assert.Equal(AgentActivity.Working, Assert.Single(source.Poll(null, Now).Sessions).Activity);
    }

    [Fact]
    public void Source_AppendWithUnchangedWindowsWriteTime_IsDiscoveredAfterAgingOut()
    {
        var stale = Now.AddDays(-1);
        var path = Rollout("growing", 10, stale, CodexTestLines.Event("task_complete", Now.AddMinutes(-1)));
        using var source = new CodexSource(_home, _time);
        Assert.Equal("growing", Assert.Single(source.Poll(null, Now).Sessions).Id);
        Assert.Empty(source.Poll(null, Now.AddHours(3)).Sessions);

        File.AppendAllText(path, CodexTestLines.Event("task_started", Now.AddHours(3)) + "\n");
        File.SetLastWriteTimeUtc(path, stale.UtcDateTime);

        // La conversation est sortie de la liste : elle revient au parcours complet suivant, une minute plus tard.
        var summary = Assert.Single(source.Poll(null, Now.AddHours(3).AddMinutes(1)).Sessions);
        Assert.Equal("growing", summary.Id);
        Assert.Equal(AgentActivity.Working, summary.Activity);
    }

    [Fact]
    public void Source_RecencyUsesNewestTimestamp_NotOnlyLastLineOrWindowsWriteTime()
    {
        Rollout("recent", 1, Now.AddDays(-1),
            CodexTestLines.Event("task_started", Now.AddMinutes(-1)),
            CodexTestLines.Event("token_count", Now.AddDays(-1)));
        using var source = new CodexSource(_home, _time);

        Assert.Equal("recent", Assert.Single(source.Poll(null, Now).Sessions).Id);
    }

    [Fact]
    public void Source_StaleMetadataAndEvents_StayExcluded()
    {
        Rollout("inactive", 10, Now.AddDays(-1), CodexTestLines.Event("task_complete", Now.AddHours(-3)));
        using var source = new CodexSource(_home, _time);

        Assert.Empty(source.Poll(null, Now).Sessions);
    }

    [Fact]
    public void Source_RecentWindowsWriteTime_RemainsAValidFallback()
    {
        Rollout("touched", 1, Now.AddMinutes(-1), CodexTestLines.Event("task_complete", Now.AddDays(-1)));
        using var source = new CodexSource(_home, _time);

        Assert.Equal("touched", Assert.Single(source.Poll(null, Now).Sessions).Id);
    }

    [Fact]
    public void Source_TwelveMostRecentConversations_AreRankedByRealEventTime()
    {
        for (var i = 0; i < 15; i++)
        {
            Rollout($"s{i:00}", 10, Now.AddDays(-1), CodexTestLines.Event("task_complete", Now.AddMinutes(-30 + i)));
        }
        using var source = new CodexSource(_home, _time);

        var ids = source.Poll(null, Now).Sessions.Select(s => s.Id).ToList();
        Assert.Equal(12, ids.Count);
        Assert.DoesNotContain("s00", ids);
        Assert.DoesNotContain("s02", ids);
        Assert.Contains("s03", ids);
        Assert.Contains("s14", ids);
    }

    [Fact]
    public void Source_InternalConversation_WithRecentEvents_StaysExcluded()
    {
        var path = Rollout("guardian", 10, Now.AddDays(-1));
        var meta = CodexTestLines.Line("session_meta", Now.AddDays(-10),
            """{"id":"guardian","source":{"subagent":{"thread_spawn":{}}}}""");
        File.WriteAllText(path, meta + "\n" + CodexTestLines.Event("task_started", Now) + "\n");
        File.SetLastWriteTimeUtc(path, Now.AddDays(-1).UtcDateTime);
        using var source = new CodexSource(_home, _time);

        Assert.Empty(source.Poll(null, Now).Sessions);
    }

    [Fact]
    public void Source_IncompleteRecentLine_IsNotUsedUntilItsNewlineArrives()
    {
        var stale = Now.AddDays(-1);
        var path = Rollout("partial", 1, stale);
        var line = CodexTestLines.Event("task_started", Now);
        File.AppendAllText(path, line);
        File.SetLastWriteTimeUtc(path, stale.UtcDateTime);
        using var source = new CodexSource(_home, _time);
        Assert.Empty(source.Poll(null, Now).Sessions);

        File.AppendAllText(path, "\n");
        File.SetLastWriteTimeUtc(path, stale.UtcDateTime);

        // Fichier non suivi dont la date Windows est ancienne : il revient au parcours complet suivant.
        Assert.Equal("partial", Assert.Single(source.Poll(null, Now.AddMinutes(1)).Sessions).Id);
    }

    [Fact]
    public void Source_TruncatedFile_DoesNotReuseItsPreviousRecentEvent()
    {
        var stale = Now.AddDays(-1);
        var path = Rollout("replaced", 1, stale, CodexTestLines.Event("task_started", Now));
        using var source = new CodexSource(_home, _time);
        Assert.Single(source.Poll(null, Now).Sessions);

        File.WriteAllText(path, CodexTestLines.Meta("replaced", _home, stale) + "\n");
        File.SetLastWriteTimeUtc(path, stale.UtcDateTime);

        Assert.Empty(source.Poll(null, Now).Sessions);
    }

    [Fact]
    public void Source_ResumedConversation_AlreadyFound_IsUpdatedAtNextPoll()
    {
        var stale = Now.AddDays(-1);
        var path = Rollout("resumed", 45, stale, CodexTestLines.Event("task_complete", Now.AddMinutes(-1)));
        using var source = new CodexSource(_home, _time);
        Assert.NotEqual(AgentActivity.Working, Assert.Single(source.Poll(null, Now).Sessions).Activity);

        File.AppendAllText(path, CodexTestLines.Event("task_started", Now.AddSeconds(1)) + "\n");
        File.SetLastWriteTimeUtc(path, stale.UtcDateTime);

        // Le relevé suivant (1 s plus tard) suffit : la conversation reprise est surveillée sans attendre le relevé profond.
        Assert.Equal(AgentActivity.Working, Assert.Single(source.Poll(null, Now.AddSeconds(1)).Sessions).Activity);
    }

    [Fact]
    public void Source_ResumedConversation_AppearingBetweenDeepScans_IsSeenAtNextDeepScan()
    {
        using var source = new CodexSource(_home, _time);
        Assert.Empty(source.Poll(null, Now).Sessions);

        Rollout("late", 45, Now.AddSeconds(10), CodexTestLines.Event("task_started", Now.AddSeconds(10)));

        Assert.Empty(source.Poll(null, Now.AddSeconds(30)).Sessions);
        Assert.Empty(source.Poll(null, Now.AddSeconds(59)).Sessions);
        Assert.Equal("late", Assert.Single(source.Poll(null, Now.AddSeconds(60)).Sessions).Id);
    }

    [Fact]
    public void Source_ConversationInRecentFolders_AppearingBetweenDeepScans_IsSeenAtNextPoll()
    {
        using var source = new CodexSource(_home, _time);
        Assert.Empty(source.Poll(null, Now).Sessions);

        Rollout("fresh", 0, Now.AddSeconds(1), CodexTestLines.Event("task_started", Now.AddSeconds(1)));

        Assert.Equal("fresh", Assert.Single(source.Poll(null, Now.AddSeconds(1)).Sessions).Id);
    }

    [Fact]
    public void Source_FastPoll_DoesNotOpenOldInactiveFiles()
    {
        var path = Rollout("dormant", 45, Now.AddDays(-1), CodexTestLines.Event("task_complete", Now.AddHours(-3)));
        using var source = new CodexSource(_home, _time);
        Assert.Empty(source.Poll(null, Now).Sessions);

        // Si le relevé rapide relisait les vieux dossiers, il verrait ce fichier devenu actif dès le relevé suivant.
        File.Delete(path);
        Rollout("dormant", 45, Now.AddSeconds(5), CodexTestLines.Event("task_started", Now.AddSeconds(5)));

        Assert.Empty(source.Poll(null, Now.AddSeconds(10)).Sessions);
        Assert.Equal("dormant", Assert.Single(source.Poll(null, Now.AddSeconds(60)).Sessions).Id);
    }

    [Fact]
    public void Source_ClockMovedBack_TriggersADeepScan()
    {
        using var source = new CodexSource(_home, _time);
        Assert.Empty(source.Poll(null, Now).Sessions);

        Rollout("early", 45, Now.AddMinutes(-1), CodexTestLines.Event("task_started", Now.AddMinutes(-1)));

        Assert.Equal("early", Assert.Single(source.Poll(null, Now.AddSeconds(-30)).Sessions).Id);
    }

    [Fact]
    public void Source_FastPoll_FiltersUntrackedRecentFolderFilesByWindowsWriteTime()
    {
        using var source = new CodexSource(_home, _time);
        Assert.Empty(source.Poll(null, Now).Sessions);

        // Dossier des 3 derniers jours, fichier non suivi : date Windows ancienne mais événement récent.
        Rollout("stale-date", 1, Now.AddDays(-1), CodexTestLines.Event("task_started", Now.AddSeconds(5)));

        // Le relevé rapide ne rouvre pas les fichiers non suivis dont la date est ancienne...
        Assert.Empty(source.Poll(null, Now.AddSeconds(10)).Sessions);
        // ... le parcours complet suivant les retrouve.
        Assert.Equal("stale-date", Assert.Single(source.Poll(null, Now.AddSeconds(60)).Sessions).Id);
    }
}
