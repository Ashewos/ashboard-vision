namespace Trofeo.ModuleHost.Tests;

public sealed class ModuleLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loader-" + Guid.NewGuid().ToString("N")[..12]);

    public ModuleLoaderTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "module"));
        File.WriteAllText(Path.Combine(_root, "module", "Dep.dll"), "x");
        File.WriteAllText(Path.Combine(_root, "Dehors.dll"), "x");
    }

    public void Dispose() => Directory.Delete(_root, true);

    [WindowsHostFact]
    public void ResolveDependency_NamedFileInTheFolder_IsFound()
    {
        var found = ModuleLoader.ResolveDependency(Path.Combine(_root, "module"), "Dep");

        Assert.Equal(Path.Combine(_root, "module", "Dep.dll"), found);
    }

    [WindowsHostFact]
    public void ResolveDependency_MissingFile_IsNull() =>
        Assert.Null(ModuleLoader.ResolveDependency(Path.Combine(_root, "module"), "Absent"));

    [WindowsHostTheory]
    [InlineData(@"..\Dehors")]
    [InlineData("../Dehors")]
    [InlineData("..")]
    [InlineData(@"sous\Dep")]
    [InlineData("C:Dep")]
    [InlineData("")]
    public void ResolveDependency_NameThatWouldLeaveTheFolder_IsNull(string name) =>
        Assert.Null(ModuleLoader.ResolveDependency(Path.Combine(_root, "module"), name));

    [WindowsHostFact]
    public void ResolveDependency_AbsoluteName_IsNull() =>
        Assert.Null(ModuleLoader.ResolveDependency(Path.Combine(_root, "module"), Path.Combine(_root, "Dehors")));
}
