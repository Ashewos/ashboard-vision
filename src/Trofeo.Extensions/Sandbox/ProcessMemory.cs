using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace Trofeo.Extensions.Sandbox;

/// <summary>
/// La mémoire privée d'un processus (ce que montre le Gestionnaire des tâches), lue avec la poignée que le service a reçue en le
/// lançant : aucun privilège de plus. Le job object ne donne que le pic, pas la mémoire du moment.
/// </summary>
internal static class ProcessMemory
{
    /// <summary>
    /// La mémoire privée engagée, en octets ; <c>null</c> si elle ne se lit pas (poignée fermée, processus déjà disparu). Ne lève jamais.
    /// </summary>
    public static long? PrivateBytes(SafeProcessHandle process)
    {
        if (process is null || process.IsInvalid || process.IsClosed)
        {
            return null;
        }

        try
        {
            var counters = new Native.ProcessMemoryCountersEx { Cb = (uint)Marshal.SizeOf<Native.ProcessMemoryCountersEx>() };
            return Native.GetProcessMemoryInfo(process, ref counters, counters.Cb) ? (long)counters.PrivateUsage : null;
        }
        catch (ObjectDisposedException)
        {
            return null; // fermée pendant l'appel
        }
    }
}
