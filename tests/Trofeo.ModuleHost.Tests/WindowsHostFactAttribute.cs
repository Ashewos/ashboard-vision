namespace Trofeo.ModuleHost.Tests;

/// <summary>Un test qui lance l'hôte de module (un exécutable Windows) : ignoré ailleurs que sous Windows.</summary>
public sealed class WindowsHostFactAttribute : FactAttribute
{
    public WindowsHostFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "L'hôte de module n'existe que sous Windows.";
        }
    }
}

/// <summary>Comme <see cref="WindowsHostFactAttribute"/>, pour un test à paramètres.</summary>
public sealed class WindowsHostTheoryAttribute : TheoryAttribute
{
    public WindowsHostTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "L'hôte de module n'existe que sous Windows.";
        }
    }
}
