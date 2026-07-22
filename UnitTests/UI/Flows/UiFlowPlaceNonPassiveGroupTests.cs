using Avalonia;
using Avalonia.Headless.XUnit;
using CAP.Avalonia.Controls;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Components.Creation;
using CAP_Core.LightCalculation;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.UI.Flows;

/// <summary>
/// Field-crash repro through the REAL input pipeline (round-4 hotfix): a saved group
/// whose frozen child S-matrix fabricates energy is selected for placement and the user
/// clicks the canvas. Before the fix, <c>SingleHopPassivityChecker</c> threw through
/// <c>PlaceGroupTemplateCommand</c> → <c>CanvasClicked</c> → pointer handler into the
/// Avalonia dispatcher and the process died. Now: nothing is placed, no exception
/// escapes, and the user sees the guard's message.
/// </summary>
[Trait("Category", "UiFlows")]
[Collection("LocalizationSingleton")]
public class UiFlowPlaceNonPassiveGroupTests
{
    /// <summary>Wavelength (nm) of the poisoned matrix — matches the field stacktrace.</summary>
    private const int NonPassiveWavelengthNm = 1546;

    [AvaloniaFact]
    public void ClickPlacingNonPassiveGroup_doesNotCrash_placesNothing()
    {
        using var host = new UiFlowTestHost();
        var vm = host.Vm;
        var win = host.Window;

        // Arm group placement exactly like the left panel does when a saved group is
        // selected (OnSelectedGroupTemplateChanged switches to PlaceGroupTemplate mode).
        vm.CanvasInteraction.SelectedGroupTemplate = BuildNonPassiveTemplate("Field Crash Group");
        vm.CanvasInteraction.CurrentMode.ShouldBe(InteractionMode.PlaceGroupTemplate);

        var canvasControl = UiInput.Descendants<DesignCanvas>(win).First();
        Point CanvasPoint(double x, double y) =>
            canvasControl.TranslatePoint(
                new Point(x * canvasControl.Zoom + vm.Canvas.PanX, y * canvasControl.Zoom + vm.Canvas.PanY), win)!.Value;

        // The click that killed the app in the field — must be a clean no-op now.
        Should.NotThrow(() => UiInput.ClickAt(win, CanvasPoint(400, 300)));

        vm.Canvas.Components.Count.ShouldBe(0,
            $"the non-passive group must not be placed (status: {vm.StatusText})");
        vm.Canvas.AllPins.Count.ShouldBe(0, "no pins of the rejected group may remain");
        vm.StatusText.ShouldContain("NonPassive Child",
            customMessage: "the guard message must name the offending component");
    }

    /// <summary>
    /// One-child group whose child carries a 1.1-amplitude through matrix at 1546 nm —
    /// the in-memory equivalent of a template file with stale non-passive frozen data.
    /// </summary>
    private static GroupTemplate BuildNonPassiveTemplate(string name)
    {
        var child = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        child.HumanReadableName = "NonPassive Child";
        child.WidthMicrometers = 50;
        child.HeightMicrometers = 10;

        var leftPin = child.Parts[0, 0].GetPinAt(CAP_Core.Tiles.RectSide.Left);
        var rightPin = child.Parts[0, 0].GetPinAt(CAP_Core.Tiles.RectSide.Right);
        var matrix = new SMatrix(
            new List<Guid> { leftPin.IDInFlow, leftPin.IDOutFlow, rightPin.IDInFlow, rightPin.IDOutFlow },
            new());
        matrix.SetValues(new()
        {
            { (leftPin.IDInFlow, rightPin.IDOutFlow), 1.1 },
            { (rightPin.IDInFlow, leftPin.IDOutFlow), 1.1 },
        });
        child.WaveLengthToSMatrixMap.Clear();
        child.WaveLengthToSMatrixMap[NonPassiveWavelengthNm] = matrix;

        var group = new ComponentGroup(name)
        {
            PhysicalX = 0,
            PhysicalY = 0,
            WidthMicrometers = 50,
            HeightMicrometers = 10
        };
        group.AddChild(child);
        group.AddExternalPin(new GroupPin
        {
            Name = "GroupIn",
            InternalPin = child.PhysicalPins[0],
            RelativeX = 0,
            RelativeY = 5,
            AngleDegrees = 180
        });
        group.AddExternalPin(new GroupPin
        {
            Name = "GroupOut",
            InternalPin = child.PhysicalPins[1],
            RelativeX = 50,
            RelativeY = 5,
            AngleDegrees = 0
        });

        return new GroupTemplate
        {
            Name = name,
            Category = "User Groups",
            Source = "User",
            WidthMicrometers = group.WidthMicrometers,
            HeightMicrometers = group.HeightMicrometers,
            ComponentCount = 1,
            TemplateGroup = group
        };
    }
}
