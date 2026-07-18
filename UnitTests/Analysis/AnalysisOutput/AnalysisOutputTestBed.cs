using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;
using CAP_Core.Tiles;

namespace UnitTests.Analysis.AnalysisOutput;

/// <summary>
/// Shared setup for the analysis-output designation tests (#754): builds a canvas
/// with light-injecting couplers ("Grating Coupler" template ⇒ the ViewModel gets a
/// <c>LaserConfig</c>) whose physical pins carry light-typed logical pins, mirroring
/// <c>TransientCircuitFactoryLaserToggleTests</c>.
/// </summary>
internal static class AnalysisOutputTestBed
{
    /// <summary>Template name that classifies a component as a light-injecting coupler.</summary>
    public const string CouplerTemplate = "Grating Coupler";

    /// <summary>Adds a coupler at the given position (20×10 µm) to the canvas.</summary>
    public static ComponentViewModel AddCoupler(DesignCanvasViewModel canvas, double x = 0, double y = 0)
    {
        var component = CreateCouplerComponent();
        component.PhysicalX = x;
        component.PhysicalY = y;
        return canvas.AddComponent(component, CouplerTemplate);
    }

    /// <summary>Adds a plain (non-coupler) component at the given position to the canvas.</summary>
    public static ComponentViewModel AddPlainComponent(DesignCanvasViewModel canvas, double x = 0, double y = 0)
    {
        var component = TestComponentFactory.CreateStraightWaveGuide();
        component.Identifier = $"wg_{Guid.NewGuid():N}";
        component.PhysicalX = x;
        component.PhysicalY = y;
        component.WidthMicrometers = 20;
        component.HeightMicrometers = 10;
        return canvas.AddComponent(component);
    }

    /// <summary>Straight-waveguide component with light physical pins, sized 20×10 µm.</summary>
    public static Component CreateCouplerComponent()
    {
        var component = TestComponentFactory.CreateStraightWaveGuide();
        component.WidthMicrometers = 20;
        component.HeightMicrometers = 10;
        var west = component.Parts[0, 0].GetPinAt(RectSide.Left);
        var east = component.Parts[0, 0].GetPinAt(RectSide.Right);
        component.PhysicalPins.Add(new PhysicalPin
        {
            Name = "west0", ParentComponent = component,
            OffsetXMicrometers = 0, OffsetYMicrometers = 5, AngleDegrees = 180, LogicalPin = west
        });
        component.PhysicalPins.Add(new PhysicalPin
        {
            Name = "east0", ParentComponent = component,
            OffsetXMicrometers = 20, OffsetYMicrometers = 5, AngleDegrees = 0, LogicalPin = east
        });
        return component;
    }
}
