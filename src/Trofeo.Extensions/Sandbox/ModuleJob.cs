using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Trofeo.Extensions.Sandbox;

/// <summary>Ce qu'un module a consommé depuis le début de son job.</summary>
/// <param name="CpuTime">Temps processeur total (utilisateur et noyau).</param>
/// <param name="PeakProcessMemory">Plus grande mémoire engagée par un processus du job, en octets.</param>
/// <param name="MemoryLimitHit">Vrai si Windows a refusé de la mémoire à un processus parce qu'il touchait la limite (512 Mio).</param>
public readonly record struct JobUsage(TimeSpan CpuTime, long PeakProcessMemory, bool MemoryLimitHit);

/// <summary>
/// Le job object d'un module : mémoire plafonnée à 512 Mio par processus, 25 % du processeur au plus (plafond dur), un seul
/// processus (le module ne peut en lancer aucun autre), arrêt avec le job et en cas d'exception non rattrapée.
/// Ce sont des garde-fous contre les énormités, pas le repère de « consommation élevée ».
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ModuleJob : IDisposable
{
    /// <summary>Limite de mémoire d'un processus, en octets.</summary>
    public const long MemoryLimit = 512L * 1024 * 1024;

    /// <summary>Part du processeur de la machine, en centièmes de pour cent (2500 = 25 %).</summary>
    public const uint CpuRate = 2500;

    private readonly SafeFileHandle _job;
    private readonly SafeFileHandle _port;
    private readonly object _gate = new();
    private bool _memoryLimitHit;

    /// <summary>Crée le job, avec ses limites. Sans droits d'administrateur.</summary>
    public ModuleJob()
    {
        _job = Native.CreateJobObject(IntPtr.Zero, IntPtr.Zero);
        if (_job.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var limits = new Native.JobExtendedLimitInformation
            {
                BasicLimitInformation = new Native.JobBasicLimitInformation
                {
                    LimitFlags = Native.JobObjectLimitProcessMemory | Native.JobObjectLimitActiveProcess
                        | Native.JobObjectLimitKillOnJobClose | Native.JobObjectLimitDieOnUnhandledException,
                    ActiveProcessLimit = 1,
                },
                ProcessMemoryLimit = (nuint)MemoryLimit,
            };
            Check(Native.SetInformationJobObject(_job, Native.JobObjectExtendedLimitInformation, in limits,
                (uint)Marshal.SizeOf<Native.JobExtendedLimitInformation>()));

            var cpu = new Native.JobCpuRateControlInformation
            {
                ControlFlags = Native.JobObjectCpuRateControlEnable | Native.JobObjectCpuRateControlHardCap,
                CpuRate = CpuRate,
            };
            Check(Native.SetInformationJobObject(_job, Native.JobObjectCpuRateControlInformation, in cpu,
                (uint)Marshal.SizeOf<Native.JobCpuRateControlInformation>()));

            // Windows prévient le port quand un processus touche la limite de mémoire : c'est le seul signe fiable (le pic
            // ne dépasse jamais la limite, l'allocation est refusée avant). On le lit à la demande, dans Read : ni fil ni minuteur.
            _port = Native.CreateIoCompletionPort(new IntPtr(-1), IntPtr.Zero, 0, 1);
            if (_port.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var association = new Native.JobAssociateCompletionPort { CompletionKey = IntPtr.Zero, CompletionPort = _port.DangerousGetHandle() };
            Check(Native.SetInformationJobObject(_job, Native.JobObjectAssociateCompletionPortInformation, in association,
                (uint)Marshal.SizeOf<Native.JobAssociateCompletionPort>()));
        }
        catch
        {
            _job.Dispose();
            _port?.Dispose();
            throw;
        }
    }

    /// <summary>Le job (pour les tests et pour <see cref="SandboxedProcess"/>).</summary>
    internal SafeFileHandle Handle => _job;

    /// <summary>Range le processus dans le job. À faire avant qu'il ne démarre.</summary>
    public void Assign(SafeProcessHandle process)
    {
        Check(Native.AssignProcessToJobObject(_job, process));
    }

    /// <summary>Ce que le job a consommé jusqu'ici.</summary>
    public JobUsage Read()
    {
        lock (_gate)
        {
            while (Native.GetQueuedCompletionStatus(_port, out var message, out _, out _, 0))
            {
                if (message is Native.JobObjectMsgProcessMemoryLimit or Native.JobObjectMsgJobMemoryLimit)
                {
                    _memoryLimitHit = true;
                }
            }

            Check(Native.QueryInformationJobObject(_job, Native.JobObjectBasicAccountingInformation, out Native.JobBasicAccountingInformation accounting,
                (uint)Marshal.SizeOf<Native.JobBasicAccountingInformation>(), IntPtr.Zero));
            Check(Native.QueryInformationJobObject(_job, Native.JobObjectExtendedLimitInformation, out Native.JobExtendedLimitInformation extended,
                (uint)Marshal.SizeOf<Native.JobExtendedLimitInformation>(), IntPtr.Zero));

            // Les temps sont en centaines de nanosecondes.
            var cpu = TimeSpan.FromTicks(accounting.TotalUserTime + accounting.TotalKernelTime);
            return new JobUsage(cpu, (long)extended.PeakProcessMemoryUsed, _memoryLimitHit);
        }
    }

    /// <summary>Ferme le job : Windows arrête ce qui y tourne encore.</summary>
    public void Dispose()
    {
        _job.Dispose();
        _port.Dispose();
    }

    private static void Check(bool success)
    {
        if (!success)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }
}
