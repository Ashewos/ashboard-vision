using System.IO.Pipes;
using Microsoft.Win32.SafeHandles;

namespace Trofeo.ModuleHost;

/// <summary>
/// L'hôte de module : <c>Trofeo.ModuleHost.exe --in &lt;poignée&gt; --out &lt;poignée&gt;</c>. Le service le lance (enfermé) en lui
/// passant deux tubes anonymes ; il charge le module, lui donne son canevas, et s'arrête quand le tube d'entrée se ferme.
/// Codes de sortie : 0 fin normale, 2 arguments invalides, 3 arrêt après un <c>Failure</c>.
/// </summary>
internal static class Program
{
    private const int ExitUsage = 2;

    private static async Task<int> Main(string[] args)
    {
        if (!TryOpenPipes(args, out var input, out var output))
        {
            return ExitUsage;
        }

        int code;
        using (input)
        using (output)
        {
            code = await HostSession.RunAsync(input, output).ConfigureAwait(false);
        }

        // Le module a pu lancer des fils qui garderaient le processus vivant : la séance est finie, on sort.
        Environment.Exit(code);
        return code;
    }

    private static bool TryOpenPipes(string[] args, out Stream input, out Stream output)
    {
        input = output = Stream.Null;
        long inHandle = 0, outHandle = 0;
        var haveIn = false;
        var haveOut = false;
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--in" when long.TryParse(args[i + 1], out inHandle) && inHandle > 0:
                    haveIn = true;
                    break;
                case "--out" when long.TryParse(args[i + 1], out outHandle) && outHandle > 0:
                    haveOut = true;
                    break;
                default:
                    return false;
            }
        }

        if (!haveIn || !haveOut || args.Length != 4)
        {
            return false;
        }

        try
        {
            input = new AnonymousPipeClientStream(PipeDirection.In, new SafePipeHandle(new IntPtr(inHandle), ownsHandle: true));
            output = new AnonymousPipeClientStream(PipeDirection.Out, new SafePipeHandle(new IntPtr(outHandle), ownsHandle: true));
            return true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            input.Dispose();
            return false;
        }
    }
}
