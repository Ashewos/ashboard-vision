using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using AshboardVision.Sdk;
using Microsoft.Win32.SafeHandles;

namespace Trofeo.Extensions.Sandbox;

/// <summary>
/// Ce qu'il faut pour lancer un programme enfermé.
/// </summary>
/// <param name="Executable">Chemin complet du programme.</param>
/// <param name="Arguments">Ligne de commande, sans le nom du programme.</param>
/// <param name="Profile">Le conteneur dans lequel il tourne.</param>
/// <param name="Granted">Les permissions accordées : seules <c>reseau-local</c> et <c>internet</c> ouvrent une capacité Windows.</param>
/// <param name="InheritedHandles">Les poignées (inscriptibles par héritage) que le processus reçoit, et aucune autre.</param>
/// <param name="Job">Le job dans lequel il est rangé avant de démarrer.</param>
/// <param name="StandardOutput">Si renseignée, la sortie standard et la sortie d'erreur du processus (poignée héritable, ajoutée à la liste).</param>
public sealed record SandboxStart(
    string Executable,
    string Arguments,
    AppContainerProfile Profile,
    IReadOnlyList<ModulePermission> Granted,
    IReadOnlyList<SafeHandle> InheritedHandles,
    ModuleJob Job,
    SafeHandle? StandardOutput = null);

/// <summary>
/// Un processus lancé dans un AppContainer (avec les capacités réseau accordées) et rangé dans un job object avant d'avoir
/// exécuté une seule instruction. Il ne reçoit que les poignées de <see cref="SandboxStart.InheritedHandles"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SandboxedProcess : IDisposable
{
    private const int StartfUseStdHandles = 0x00000100;

    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEvent _waitHandle;
    private readonly RegisteredWaitHandle _registration;
    private int _disposed;

    private SandboxedProcess(int id, SafeProcessHandle handle)
    {
        Id = id;
        Handle = handle;

        // Une attente enregistrée sur le processus (pas de minuteur, pas de fil bloqué) : elle se déclenche à sa fin.
        _waitHandle = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(handle.DangerousGetHandle(), ownsHandle: false) };
        _registration = ThreadPool.RegisterWaitForSingleObject(_waitHandle, (_, _) => OnExited(), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    /// <summary>Numéro du processus.</summary>
    public int Id { get; }

    /// <summary>Poignée du processus.</summary>
    public SafeProcessHandle Handle { get; }

    /// <summary>Se termine, avec le code de sortie, quand le processus s'arrête.</summary>
    public Task<int> Exited => _exited.Task;

    /// <summary>Lance le programme enfermé. Il est déjà dans le job quand il démarre.</summary>
    public static SandboxedProcess Start(SandboxStart start)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (!Path.IsPathFullyQualified(start.Executable))
        {
            throw new ArgumentException("Le chemin du programme doit être complet.", nameof(start));
        }

        var inherited = new List<SafeHandle>(start.InheritedHandles);
        if (start.StandardOutput is not null && !inherited.Contains(start.StandardOutput))
        {
            inherited.Add(start.StandardOutput);
        }

        var allocations = new List<IntPtr>();
        var attributeList = IntPtr.Zero;
        var attributeListInitialized = false;
        var restoreInheritance = new List<(IntPtr Handle, bool WasInheritable)>();
        try
        {
            var attributeCount = inherited.Count > 0 ? 2 : 1;

            // La liste d'attributs : taille, puis initialisation.
            nuint size = 0;
            Native.InitializeProcThreadAttributeList(IntPtr.Zero, attributeCount, 0, ref size); // échoue exprès : on veut la taille
            attributeList = Alloc(allocations, (int)size);
            Check(Native.InitializeProcThreadAttributeList(attributeList, attributeCount, 0, ref size));
            attributeListInitialized = true;

            // Les capacités du conteneur : seulement celles des permissions réseau accordées.
            var capabilityPointers = CapabilitySids(start.Granted, allocations);
            var capabilities = IntPtr.Zero;
            if (capabilityPointers.Count > 0)
            {
                capabilities = Alloc(allocations, capabilityPointers.Count * Marshal.SizeOf<Native.SidAndAttributes>());
                for (var i = 0; i < capabilityPointers.Count; i++)
                {
                    Marshal.StructureToPtr(new Native.SidAndAttributes { Sid = capabilityPointers[i], Attributes = Native.SeGroupEnabled },
                        capabilities + i * Marshal.SizeOf<Native.SidAndAttributes>(), false);
                }
            }

            var sidBytes = new byte[start.Profile.Sid.BinaryLength];
            start.Profile.Sid.GetBinaryForm(sidBytes, 0);
            var containerSid = Alloc(allocations, sidBytes.Length);
            Marshal.Copy(sidBytes, 0, containerSid, sidBytes.Length);

            var securityCapabilities = Alloc(allocations, Marshal.SizeOf<Native.SecurityCapabilities>());
            Marshal.StructureToPtr(new Native.SecurityCapabilities
            {
                AppContainerSid = containerSid,
                Capabilities = capabilities,
                CapabilityCount = (uint)capabilityPointers.Count,
            }, securityCapabilities, false);
            Check(Native.UpdateProcThreadAttribute(attributeList, 0, Native.ProcThreadAttributeSecurityCapabilities, securityCapabilities,
                (nuint)Marshal.SizeOf<Native.SecurityCapabilities>(), IntPtr.Zero, IntPtr.Zero));

            // La liste blanche des poignées héritées : rendues héritables le temps du lancement, puis remises comme elles étaient.
            if (inherited.Count > 0)
            {
                var list = Alloc(allocations, inherited.Count * IntPtr.Size);
                for (var i = 0; i < inherited.Count; i++)
                {
                    var raw = inherited[i].DangerousGetHandle();
                    Marshal.WriteIntPtr(list, i * IntPtr.Size, raw);
                    Check(Native.GetHandleInformation(raw, out var flags));
                    restoreInheritance.Add((raw, (flags & Native.HandleFlagInherit) != 0));
                    Check(Native.SetHandleInformation(raw, Native.HandleFlagInherit, Native.HandleFlagInherit));
                }

                Check(Native.UpdateProcThreadAttribute(attributeList, 0, Native.ProcThreadAttributeHandleList, list,
                    (nuint)(inherited.Count * IntPtr.Size), IntPtr.Zero, IntPtr.Zero));
            }

            var startupInfo = new Native.StartupInfoEx { AttributeList = attributeList };
            startupInfo.StartupInfo.Cb = (uint)Marshal.SizeOf<Native.StartupInfoEx>();
            if (start.StandardOutput is not null)
            {
                var output = start.StandardOutput.DangerousGetHandle();
                startupInfo.StartupInfo.Flags = StartfUseStdHandles;
                startupInfo.StartupInfo.StdOutput = output;
                startupInfo.StartupInfo.StdError = output;
            }

            var commandLine = ("\"" + start.Executable + "\"" + (start.Arguments.Length > 0 ? " " + start.Arguments : "") + "\0").ToCharArray();
            bool created;
            Native.ProcessInformation info;
            int error;
            unsafe
            {
                fixed (char* commandLinePointer = commandLine)
                {
                    created = Native.CreateProcess(start.Executable, commandLinePointer, IntPtr.Zero, IntPtr.Zero, inherited.Count > 0,
                        Native.ExtendedStartupInfoPresent | Native.CreateSuspended | Native.CreateNoWindow, IntPtr.Zero,
                        Path.GetDirectoryName(start.Executable)!, ref startupInfo, out info);
                    error = Marshal.GetLastWin32Error();
                }
            }

            if (!created)
            {
                throw new Win32Exception(error, $"Impossible de lancer {start.Executable} : {new Win32Exception(error).Message}");
            }

            var process = new SafeProcessHandle(info.Process, ownsHandle: true);
            try
            {
                // Dans le job AVANT la première instruction : si le job refuse, le processus meurt sans avoir rien fait.
                try
                {
                    start.Job.Assign(process);
                }
                catch
                {
                    Native.TerminateProcess(info.Process, 1);
                    throw;
                }

                if (Native.ResumeThread(info.Thread) == uint.MaxValue)
                {
                    var resumeError = Marshal.GetLastWin32Error();
                    Native.TerminateProcess(info.Process, 1);
                    throw new Win32Exception(resumeError);
                }
            }
            catch
            {
                process.Dispose();
                throw;
            }
            finally
            {
                Native.CloseHandle(info.Thread);
            }

            return new SandboxedProcess((int)info.ProcessId, process);
        }
        finally
        {
            foreach (var (handle, wasInheritable) in restoreInheritance)
            {
                if (!wasInheritable)
                {
                    Native.SetHandleInformation(handle, Native.HandleFlagInherit, 0);
                }
            }

            if (attributeListInitialized)
            {
                Native.DeleteProcThreadAttributeList(attributeList);
            }

            foreach (var pointer in allocations)
            {
                Marshal.FreeHGlobal(pointer);
            }

            GC.KeepAlive(inherited);
        }
    }

    /// <summary>
    /// Duplique une poignée de ce processus-ci dans le processus enfermé (même droits) et renvoie sa valeur, côté enfant.
    /// </summary>
    public IntPtr DuplicateInto(SafeHandle source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Check(Native.DuplicateHandle(Native.GetCurrentProcess(), source.DangerousGetHandle(), Handle.DangerousGetHandle(), out var target, 0,
            false, Native.DuplicateSameAccess));
        return target;
    }

    /// <summary>Arrête le processus (sans effet s'il est déjà terminé).</summary>
    public void Kill()
    {
        if (Volatile.Read(ref _disposed) == 0 && !Handle.IsClosed)
        {
            Native.TerminateProcess(Handle.DangerousGetHandle(), 1);
        }
    }

    /// <summary>
    /// Ferme les poignées de ce côté-ci. N'arrête pas le processus : c'est la fermeture du <see cref="ModuleJob"/> qui s'en
    /// charge (ou <see cref="Kill"/>).
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _registration.Unregister(null);
        _waitHandle.Dispose();
        Handle.Dispose();
    }

    private void OnExited()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            _exited.TrySetCanceled();
            return;
        }

        _exited.TrySetResult(Native.GetExitCodeProcess(Handle, out var code) ? unchecked((int)code) : -1);
    }

    private static List<IntPtr> CapabilitySids(IReadOnlyList<ModulePermission> granted, List<IntPtr> allocations)
    {
        var sids = new List<IntPtr>();
        foreach (var permission in granted.Distinct())
        {
            var type = permission switch
            {
                ModulePermission.Internet => WellKnownSidType.WinCapabilityInternetClientSid,
                ModulePermission.LocalNetwork => WellKnownSidType.WinCapabilityPrivateNetworkClientServerSid,
                _ => (WellKnownSidType?)null, // ce-pc, capteurs, musique : le service s'en charge, Windows n'y est pour rien
            };
            if (type is null)
            {
                continue;
            }

            var sid = new SecurityIdentifier(type.Value, null);
            var bytes = new byte[sid.BinaryLength];
            sid.GetBinaryForm(bytes, 0);
            var pointer = Alloc(allocations, bytes.Length);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            sids.Add(pointer);
        }

        return sids;
    }

    private static IntPtr Alloc(List<IntPtr> allocations, int size)
    {
        var pointer = Marshal.AllocHGlobal(size);
        allocations.Add(pointer);
        return pointer;
    }

    private static void Check(bool success)
    {
        if (!success)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }
}
