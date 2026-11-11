using System.Text.Json.Serialization;

namespace Trofeo.Core.Shortcuts;

/// <summary>Un raccourci clavier global et ce qu'il déclenche.</summary>
/// <param name="Keys">Les touches, par exemple « Ctrl+Alt+Right » (voir <see cref="ShortcutKeys.TryParse"/>).</param>
/// <param name="Command">Ce qui est demandé.</param>
/// <param name="Index">Pour <see cref="ShortcutCommand.SelectTab"/> et <see cref="ShortcutCommand.SelectView"/> : rang, à partir de 0.</param>
/// <param name="Target">Instance visée (case) ; <c>null</c> : le premier module qui accepte les commandes.</param>
/// <param name="ModuleId">Pour <see cref="ShortcutCommand.Community"/> : le type du module complémentaire ; sinon <c>null</c> (non écrit).</param>
/// <param name="ShortcutId">Pour <see cref="ShortcutCommand.Community"/> : l'identifiant du raccourci déclaré par ce module ; sinon <c>null</c> (non écrit).</param>
public sealed record ShortcutBinding(
    string Keys, ShortcutCommand Command, int Index = 0, Guid? Target = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ModuleId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ShortcutId = null);
