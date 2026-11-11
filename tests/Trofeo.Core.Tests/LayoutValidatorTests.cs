using System.Text.Json;
using Trofeo.Core.Layout;
using Trofeo.ModuleKit;

namespace Trofeo.Core.Tests;

public class LayoutValidatorTests
{
    private static readonly IReadOnlyDictionary<string, ModuleDescriptor> Types =
        new Dictionary<string, ModuleDescriptor>
        {
            ["clock"] = new("clock", "Heure", new GridSize(4, 2),
                new Dictionary<SizePreset, GridSize> { [SizePreset.Ideal] = new GridSize(4, 2) }),
        };

    private static ModulePlacement Placement(Guid id, int column, int row, int columns, int rows, string typeId = "clock") =>
        new(id, typeId, column, row, columns, rows, TextSize.Normal, JsonDocument.Parse("{}").RootElement.Clone());

    private static readonly IReadOnlyList<ModulePlacement> Existing = [Placement(Guid.NewGuid(), 12, 0, 4, 2)];

    [Fact]
    public void Check_OutOfBounds()
    {
        var candidate = Placement(Guid.NewGuid(), 14, 2, 4, 2);
        Assert.Equal(PlacementError.OutOfBounds, LayoutValidator.Check(Existing, candidate, Types));
    }

    [Fact]
    public void Check_IntOverflow_IsOutOfBounds()
    {
        var candidate = Placement(Guid.NewGuid(), int.MaxValue, 0, 4, 2);
        Assert.Equal(PlacementError.OutOfBounds, LayoutValidator.Check(Existing, candidate, Types));

        var rows = Placement(Guid.NewGuid(), 0, int.MaxValue, 4, 2);
        Assert.Equal(PlacementError.OutOfBounds, LayoutValidator.Check(Existing, rows, Types));
    }

    [Fact]
    public void Check_Overlap()
    {
        var candidate = Placement(Guid.NewGuid(), 11, 0, 4, 2);
        Assert.Equal(PlacementError.Overlap, LayoutValidator.Check(Existing, candidate, Types));
    }

    [Fact]
    public void Check_BelowMin()
    {
        var candidate = Placement(Guid.NewGuid(), 0, 0, 2, 2);
        Assert.Equal(PlacementError.BelowMinSize, LayoutValidator.Check(Existing, candidate, Types));
    }

    [Fact]
    public void Check_MovingSelf_IsAllowed()
    {
        var self = Existing[0];
        var moved = self with { Column = 11 };
        Assert.Equal(PlacementError.None, LayoutValidator.Check(Existing, moved, Types));
    }

    [Fact]
    public void Check_UnknownType()
    {
        var candidate = Placement(Guid.NewGuid(), 0, 0, 4, 2, typeId: "inconnu");
        Assert.Equal(PlacementError.UnknownType, LayoutValidator.Check(Existing, candidate, Types));
    }

    [Fact]
    public void Check_FreeSpot_IsAllowed()
    {
        var candidate = Placement(Guid.NewGuid(), 0, 0, 4, 2);
        Assert.Equal(PlacementError.None, LayoutValidator.Check(Existing, candidate, Types));
    }
}
