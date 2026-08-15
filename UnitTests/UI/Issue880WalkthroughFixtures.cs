using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.Services.GdsImport;

namespace UnitTests.UI;


/// <summary>Shared placement fixtures for the issue #880 walkthrough scenes.</summary>
internal static class WalkthroughTemplates
{
    /// <summary>10×4 µm two-port waveguide, pins in(0,2,180°)/out(10,2,0°).</summary>
    public static ComponentTemplate Waveguide() => new()
    {
        Name = "wg",
        Category = "Test",
        PdkSource = "testpdk",
        WidthMicrometers = 10,
        HeightMicrometers = 4,
        PinDefinitions = new[]
        {
            new PinDefinition("in", 0, 2, 180),
            new PinDefinition("out", 10, 2, 0),
        },
        CreateSMatrix = pins => new CAP_Core.LightCalculation.SMatrix(
            pins.SelectMany(p => new[] { p.IDInFlow, p.IDOutFlow }).ToList(),
            new List<(Guid, double)>()),
    };

    /// <summary>100×100 µm blocker with its only pin buried at the center.</summary>
    public static ComponentTemplate Trap() => new()
    {
        Name = "trap",
        Category = "Test",
        PdkSource = "testpdk",
        WidthMicrometers = 100,
        HeightMicrometers = 100,
        PinDefinitions = new[] { new PinDefinition("port", 50, 50, 0) },
        CreateSMatrix = pins => new CAP_Core.LightCalculation.SMatrix(
            pins.SelectMany(p => new[] { p.IDInFlow, p.IDOutFlow }).ToList(),
            new List<(Guid, double)>()),
    };

    /// <summary>A placement instruction for the given template instance.</summary>
    public static GdsPlacementInstruction Placement(
        string instanceName, double x, double y, string identifier = "wg") => new()
    {
        InstanceName = instanceName,
        ComponentIdentifier = identifier,
        PdkSource = "testpdk",
        XUm = x,
        YUm = y,
    };
}
