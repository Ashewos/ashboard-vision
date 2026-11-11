using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Trofeo.ModuleHost;

/// <summary>
/// Ce que l'hôte rattrape d'un module et ce qu'il laisse le tuer. Une exception d'une méthode du module met son instance en
/// erreur, sauf <see cref="OutOfMemoryException"/> : l'hôte tombe (le superviseur du service le relance et dit « mémoire dépassée »),
/// jamais un module qui continue dans un état inconnu.
/// </summary>
internal static class ModuleGuard
{
    /// <summary>
    /// Si l'exception est fatale (mémoire épuisée, même enveloppée dans une <see cref="TargetInvocationException"/> du constructeur
    /// ou une <see cref="AggregateException"/>), la relance sur un fil du pool où personne ne la rattrape : le processus
    /// s'arrête (code 0xE0434352), même quand l'appelant n'est qu'une tâche dont on n'observe pas l'échec. Renvoie vrai dans ce cas.
    /// </summary>
    public static bool CrashIfFatal(Exception exception)
    {
        if (FindFatal(exception) is not { } fatal)
        {
            return false;
        }

        ThreadPool.UnsafeQueueUserWorkItem(static state => ExceptionDispatchInfo.Capture((Exception)state!).Throw(), fatal, preferLocal: false);
        return true;
    }

    private static OutOfMemoryException? FindFatal(Exception? exception)
    {
        switch (exception)
        {
            case OutOfMemoryException memory:
                return memory;
            case AggregateException aggregate:
                foreach (var inner in aggregate.InnerExceptions)
                {
                    if (FindFatal(inner) is { } found)
                    {
                        return found;
                    }
                }

                return null;
            case TargetInvocationException invocation:
                return FindFatal(invocation.InnerException);
            default:
                return null;
        }
    }
}
