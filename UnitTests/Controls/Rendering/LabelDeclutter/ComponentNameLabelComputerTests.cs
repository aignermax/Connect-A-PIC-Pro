using Avalonia;
using Avalonia.Headless.XUnit;
using CAP.Avalonia.Controls.Rendering.LabelDeclutter;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;
using Shouldly;
using UnitTests.Helpers;
using Xunit;

namespace UnitTests.Controls.Rendering.LabelDeclutter;

/// <summary>
/// Tests for <see cref="ComponentNameLabelComputer"/>: measures each simple component's name
/// label at the current screen-space-capped font size, resolves overlaps via
/// <see cref="LabelOverlapResolver"/>, and caches the result until something that could change
/// it (position, hover, selection, viewport, zoom) actually changes — <see cref="AvaloniaFactAttribute"/>
/// is required because measuring text needs an initialized Avalonia font manager.
/// </summary>
public class ComponentNameLabelComputerTests
{
    private static readonly Rect WideViewport = new(-1000, -1000, 4000, 4000);

    [AvaloniaFact]
    public void NonOverlappingComponents_BothNamesVisible()
    {
        var far = MakeComponent("far", x: 1000, y: 1000);
        var near = MakeComponent("near", x: 0, y: 0);
        var computer = new ComponentNameLabelComputer();

        var visible = computer.GetVisibleLabelIds(new[] { far, near }, hoveredComponent: null, WideViewport, zoom: 1.0);

        visible.ShouldBe(new[] { "far", "near" }, ignoreOrder: true);
    }

    [AvaloniaFact]
    public void OverlappingComponents_SelectedNameWinsOverUnselected()
    {
        var zzz = MakeComponent("zzz", x: 0, y: 0);
        var aaa = MakeComponent("aaa", x: 5, y: 0); // overlaps zzz's label, would win the ordinal tie-break
        aaa.IsSelected = false;
        zzz.IsSelected = true; // but selection outranks the tie-break
        var computer = new ComponentNameLabelComputer();

        var visible = computer.GetVisibleLabelIds(new[] { zzz, aaa }, hoveredComponent: null, WideViewport, zoom: 1.0);

        visible.ShouldBe(new[] { "zzz" });
    }

    [AvaloniaFact]
    public void OverlappingComponents_HoveredNameWinsOverUnhoveredNormal()
    {
        var zzz = MakeComponent("zzz", x: 0, y: 0);
        var aaa = MakeComponent("aaa", x: 5, y: 0);
        var computer = new ComponentNameLabelComputer();

        var visible = computer.GetVisibleLabelIds(new[] { zzz, aaa }, hoveredComponent: aaa, WideViewport, zoom: 1.0);

        visible.ShouldBe(new[] { "aaa" });
    }

    [AvaloniaFact]
    public void ComponentGroups_AreNeverIncluded()
    {
        var group = new ComponentViewModel(new ComponentGroup("MyGroup") { PhysicalX = 0, PhysicalY = 0 });
        var plain = MakeComponent("plain", x: 500, y: 500);
        var computer = new ComponentNameLabelComputer();

        var visible = computer.GetVisibleLabelIds(new[] { group, plain }, hoveredComponent: null, WideViewport, zoom: 1.0);

        visible.ShouldBe(new[] { "plain" });
    }

    [AvaloniaFact]
    public void ComponentOutsideViewport_IsCulledEvenWithoutOverlap()
    {
        var offscreen = MakeComponent("offscreen", x: 100_000, y: 100_000);
        var computer = new ComponentNameLabelComputer();

        var visible = computer.GetVisibleLabelIds(new[] { offscreen }, hoveredComponent: null, WideViewport, zoom: 1.0);

        visible.ShouldBeEmpty();
    }

    [AvaloniaFact]
    public void RepeatedCall_WithUnchangedInputs_ReturnsCachedResult()
    {
        var comp = MakeComponent("stable", x: 0, y: 0);
        var computer = new ComponentNameLabelComputer();
        var components = new[] { comp };

        var first = computer.GetVisibleLabelIds(components, hoveredComponent: null, WideViewport, zoom: 1.0);
        var second = computer.GetVisibleLabelIds(components, hoveredComponent: null, WideViewport, zoom: 1.0);

        ReferenceEquals(first, second).ShouldBeTrue(
            "an unchanged frame must reuse the cached visibility set rather than re-measuring text");
    }

    [AvaloniaFact]
    public void MovingAComponent_InvalidatesTheCache()
    {
        var comp = MakeComponent("mover", x: 0, y: 0);
        var computer = new ComponentNameLabelComputer();
        var components = new[] { comp };

        var before = computer.GetVisibleLabelIds(components, hoveredComponent: null, WideViewport, zoom: 1.0);
        comp.X = 200;
        var after = computer.GetVisibleLabelIds(components, hoveredComponent: null, WideViewport, zoom: 1.0);

        ReferenceEquals(before, after).ShouldBeFalse("a moved component must invalidate the cached result");
        after.ShouldBe(new[] { "mover" });
    }

    [AvaloniaFact]
    public void AtExtremeZoomOut_LabelsAreHiddenEntirely_NotJustCapped()
    {
        var comp = MakeComponent("shrunk", x: 0, y: 0);
        var computer = new ComponentNameLabelComputer();

        var visible = computer.GetVisibleLabelIds(new[] { comp }, hoveredComponent: null, WideViewport, zoom: 0.1);

        visible.ShouldBeEmpty();
    }

    private static ComponentViewModel MakeComponent(string identifier, double x, double y)
    {
        var component = TestComponentFactory.CreateBasicComponent();
        component.Identifier = identifier;
        component.PhysicalX = x;
        component.PhysicalY = y;
        return new ComponentViewModel(component) { X = x, Y = y };
    }
}
