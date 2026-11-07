using AshboardVision.Sdk;
using SkiaSharp;
using Trofeo.ModuleKit;

namespace Trofeo.ModuleHost.Tests.TwoClassesModule;

public sealed class FirstModule : CommunityModule
{
    public override void Render(SKCanvas canvas, ModuleRenderContext ctx)
    {
    }
}

public sealed class SecondModule : CommunityModule
{
    public override void Render(SKCanvas canvas, ModuleRenderContext ctx)
    {
    }
}
