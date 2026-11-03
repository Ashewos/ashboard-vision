using Trofeo.Control;
using Trofeo.Core.Layout;
using Trofeo.ModuleKit;

namespace Trofeo.Settings.Services;

/// <summary>Le moteur d'édition de la disposition (<see cref="LayoutEditor"/>), monté sur les types de modules que le service annonce.</summary>
public static class LayoutEditors
{
    public static LayoutEditor From(IEnumerable<ModuleTypeInfo> types) => new(
        types.GroupBy(type => type.TypeId).ToDictionary(
            group => group.Key,
            group => new ModuleDescriptor(group.First().TypeId, group.First().Label(), group.First().MinSize, group.First().Presets, group.First().AcceptsCommands)));
}
