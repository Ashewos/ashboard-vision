using System.ComponentModel;
using Trofeo.Modules.Agents;

namespace Trofeo.Distribution.Tests;

/// <summary>Une clé <c>Run</c> en mémoire, pour que les tests n'écrivent pas dans le registre.</summary>
internal sealed class FakeRunKey : IRunKey
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Si renseignée, toute écriture lève cette exception (Windows refuse).</summary>
    public Exception? WriteFailure { get; init; }

    public string? Read(string name) => Values.GetValueOrDefault(name);

    public void Write(string name, string value)
    {
        if (WriteFailure is not null) throw WriteFailure;
        Values[name] = value;
    }

    public void Delete(string name) => Values.Remove(name);
}

/// <summary>
/// Un faux <c>claude</c> : note chaque commande, répond <paramref name="list"/> à <c>plugin list --json</c> et réussit le reste,
/// sauf les commandes dont <paramref name="failing"/> reconnaît les arguments. <paramref name="missing"/> : <c>claude</c> est introuvable.
/// </summary>
internal sealed class FakeClaude(string list = "[]", bool missing = false, Func<string, bool>? failing = null)
{
    public List<string> Calls { get; } = [];

    public Task<(int ExitCode, string Output)> Run(string file, string arguments, CancellationToken ct)
    {
        if (missing) throw new Win32Exception(2, "introuvable");
        Calls.Add(arguments);
        if (arguments == "plugin list --json") return Task.FromResult((0, list));
        return Task.FromResult(failing?.Invoke(arguments) == true ? (1, "boom") : (0, "ok"));
    }

    public QuotaModInstaller Installer(string? modsDir) => new(Run, modsDir);
}
