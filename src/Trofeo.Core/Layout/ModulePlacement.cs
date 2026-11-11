using System.Text.Json;
using System.Text.Json.Serialization;
using Trofeo.ModuleKit;

namespace Trofeo.Core.Layout;

/// <summary>Instance d'un module posée sur le damier, avec ses réglages propres.</summary>
/// <param name="Name">Nom facultatif de la case (<see cref="LayoutView.MaxNameLength"/> caractères au plus) ; <c>null</c> : aucun.</param>
public sealed record ModulePlacement(
    Guid InstanceId,
    string TypeId,
    int Column,
    int Row,
    int Columns,
    int Rows,
    TextSize TextSize,
    JsonElement Settings,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null);
