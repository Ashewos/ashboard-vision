using System.Diagnostics;
using Trofeo.ModuleKit;

namespace AshboardVision.Sdk.Testing;

/// <summary>Le journal d'un module en test : les messages vont à la sortie de trace du processus (la fenêtre de sortie de l'éditeur).</summary>
internal sealed class TestLog : IModuleLog
{
    public void Info(string message) => Trace.WriteLine("[module] " + message);

    public void Warning(string message) => Trace.WriteLine("[module] AVERTISSEMENT : " + message);

    public void Error(string message) => Trace.WriteLine("[module] ERREUR : " + message);
}
