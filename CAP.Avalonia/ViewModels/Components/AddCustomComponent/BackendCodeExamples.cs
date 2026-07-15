using CAP.Avalonia.Services.AddCustomComponent;

namespace CAP.Avalonia.ViewModels.Components.AddCustomComponent;

public static class BackendCodeExamples
{
    public const string GdsFactory = "import gdsfactory as gf\ncomponent = gf.components.mmi1x2()";

    public const string Nazca = "import nazca as nd\n\ndef component():\n    with nd.Cell(name='my_component') as c:\n        nd.strt(length=20, width=0.5).put(0)\n        nd.Pin('a0').put(0, 0, 180)\n        nd.Pin('b0').put(20, 0, 0)\n    return c";

    public static string For(GeometryBackend backend) =>
        backend == GeometryBackend.GdsFactory ? GdsFactory : Nazca;
}
