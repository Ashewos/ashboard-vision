using Trofeo.Settings.Services;

namespace Trofeo.Settings.Tests;

public class SingleInstanceTests
{
    private static string Unique() => "trofeo-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void SecondLaunch_IsNotFirst()
    {
        var name = Unique();
        using var first = SingleInstance.Acquire(@"Local\" + name, @"Local\" + name + ".show", name + ".open");
        using var second = SingleInstance.Acquire(@"Local\" + name, @"Local\" + name + ".show", name + ".open");

        Assert.True(first.IsFirst);
        Assert.False(second.IsFirst);
    }

    [Fact]
    public async Task SecondLaunch_AsksFirstToComeForward()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "EventWaitHandle nommé : Windows seulement.");

        var name = Unique();
        using var first = SingleInstance.Acquire(@"Local\" + name, @"Local\" + name + ".show", name + ".open");
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.ShowRequested += () => asked.TrySetResult();

        using var second = SingleInstance.Acquire(@"Local\" + name, @"Local\" + name + ".show", name + ".open");

        await asked.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }
}
