using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;

// Sonde de l'enfermement : Trofeo.SandboxProbe <action> <argument>.
//   read <fichier>      lit le fichier
//   write <dossier>     crée puis relit un fichier dans le dossier
//   tcp <hôte> <port>   ouvre une connexion TCP
//   spawn <exe>         lance le programme et attend qu'il se termine sans erreur
//   env <nom>           affiche « OK <valeur> » si la variable d'environnement existe, sinon « DENIED absent »
//   sleep <ms>          attend, puis répond OK
//   alloc <Mo>          alloue (mémoire native) et touche ce nombre de Mio
//   allocgc <Mo>        pareil avec la mémoire gérée de .NET (celle d'un vrai module)
// Affiche « OK » ou « DENIED <code> » et se termine par 0 dans les deux cas ; un code de sortie non nul ne
// veut dire qu'un mauvais appel (ou, pour alloc, un processus arrêté par Windows avant d'avoir répondu).
if (args.Length < 2)
{
    Console.Error.WriteLine("Usage : Trofeo.SandboxProbe <read|write|tcp|spawn|env|sleep|alloc|allocgc> <argument> [port]");
    return 2;
}

try
{
    switch (args[0])
    {
        case "read":
            _ = File.ReadAllBytes(args[1]);
            break;
        case "write":
            var file = Path.Combine(args[1], "sonde-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(file, "ok");
            _ = File.ReadAllText(file);
            File.Delete(file);
            break;
        case "tcp" when args.Length >= 3:
            using (var client = new TcpClient())
            {
                client.ConnectAsync(args[1], int.Parse(args[2])).Wait(TimeSpan.FromSeconds(5));
                if (!client.Connected)
                {
                    Console.WriteLine("DENIED timeout");
                    return 0;
                }
            }

            break;
        case "spawn":
            using (var child = Process.Start(new ProcessStartInfo(args[1]) { CreateNoWindow = true, UseShellExecute = false })
                   ?? throw new InvalidOperationException("Process.Start n'a rien renvoyé."))
            {
                if (!child.WaitForExit(5000))
                {
                    child.Kill();
                    Console.WriteLine("DENIED timeout");
                    return 0;
                }

                if (child.ExitCode != 0)
                {
                    Console.WriteLine($"DENIED {child.ExitCode}");
                    return 0;
                }
            }

            break;
        case "env":
            if (Environment.GetEnvironmentVariable(args[1]) is { } value)
            {
                Console.WriteLine("OK " + value);
                return 0;
            }

            Console.WriteLine("DENIED absent");
            return 0;
        case "sleep":
            Thread.Sleep(int.Parse(args[1]));
            break;
        case "alloc":
            AllocateAndTouch(long.Parse(args[1]) * 1024 * 1024);
            break;
        case "allocgc":
            var kept = new List<byte[]>();
            for (var i = 0; i < long.Parse(args[1]); i++)
            {
                var block = new byte[1024 * 1024];
                for (var j = 0; j < block.Length; j += 4096)
                {
                    block[j] = 1;
                }

                kept.Add(block);
            }

            break;
        default:
            Console.Error.WriteLine("Action inconnue : " + args[0]);
            return 2;
    }

    Console.WriteLine("OK");
}
// Une mémoire refusée (alloc) n'est pas rattrapée : le processus tombe, comme un module qui dépasse sa limite.
catch (Exception e) when (e is not OutOfMemoryException)
{
    var inner = e is AggregateException aggregate ? aggregate.GetBaseException() : e;
    Console.WriteLine("DENIED " + CodeOf(inner));
}

return 0;

static int CodeOf(Exception e) => e switch
{
    UnauthorizedAccessException => 5,
    SocketException socket => socket.ErrorCode,
    Win32Exception win32 => win32.NativeErrorCode,
    _ => e.HResult,
};

// Mémoire native, par blocs d'un Mio : on touche chaque page pour qu'elle soit réellement engagée. Si Windows refuse,
// l'exception n'est pas rattrapée et le processus s'arrête (c'est ce que le test attend).
static unsafe void AllocateAndTouch(long bytes)
{
    const int block = 1024 * 1024;
    for (long done = 0; done < bytes; done += block)
    {
        var p = (byte*)NativeMemory.Alloc(block);
        for (var i = 0; i < block; i += 4096)
        {
            p[i] = 1;
        }
    }
}
