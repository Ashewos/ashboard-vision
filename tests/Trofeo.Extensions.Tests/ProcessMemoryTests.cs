using System.Diagnostics;
using Trofeo.Extensions.Sandbox;

namespace Trofeo.Extensions.Tests;

// La mémoire privée d'un processus, lue sans aucun privilège de plus que la poignée que le service a déjà.
public sealed class ProcessMemoryTests
{
    [WindowsHostFact]
    public void PrivateBytes_OfTheCurrentProcess_IsCloseToWhatTheProcessClassSays()
    {
        using var self = Process.GetCurrentProcess();

        var measured = ProcessMemory.PrivateBytes(self.SafeHandle);

        Assert.NotNull(measured);
        self.Refresh();
        var reference = (double)self.PrivateMemorySize64;
        Assert.InRange(measured!.Value / reference, 0.5, 2.0);
        Assert.True(measured > 10L * 1024 * 1024); // un processus de test pèse bien plus que 10 Mio
    }

    [WindowsHostFact]
    public async Task PrivateBytes_OfAChildProcess_IsReadableWithTheHandleOfItsCreator()
    {
        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 > nul") { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            var measured = ProcessMemory.PrivateBytes(child.SafeHandle);

            Assert.True(measured > 0);
        }
        finally
        {
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
        }
    }

    [WindowsHostFact]
    public async Task PrivateBytes_OfAFinishedProcess_StillAnswersOrIsNull_NeverThrows()
    {
        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true })!;
        await child.WaitForExitAsync();

        var ex = Record.Exception(() => ProcessMemory.PrivateBytes(child.SafeHandle));

        Assert.Null(ex);
    }

    [WindowsHostFact]
    public void PrivateBytes_ClosedHandle_IsNull()
    {
        var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true })!;
        var handle = child.SafeHandle;
        child.WaitForExit();
        handle.Dispose();

        Assert.Null(ProcessMemory.PrivateBytes(handle));
    }
}
