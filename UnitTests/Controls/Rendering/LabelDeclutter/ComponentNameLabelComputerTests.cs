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
/// label at the current screen-space-clamped font size, resolves overlaps via
/// <see cref="LabelOverlapResolver"/>, and caches the (expensive) result until the content
/// signature actually changes — panning alone must never re-trigger it (see
/// <see cref="PanningAlone_DoesNotTriggerRebuild"/>). <see cref="AvaloniaFactAttribute"/> is
/// required because measuring text needs an initialized Avalonia font manager.
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

        var visible = computer.GetVisibleLabelIds(new[] { far, near }, hoveredComponentId: null, WideViewport, zoom: 1.0);

        visible.ShouldBe(new[] { far.Component.Id, near.Component.Id }, ignoreOrder: true);
    }

    [AvaloniaFact]
    public void OverlappingComponents_SelectedNameWinsOverUnselected()
    {
        var a = MakeComponent("aName", x: 0, y: 0);
        var b = MakeComponent("bName", x: 5, y: 0); // overlaps a's label
        b.IsSelected = true;
        var computer = new ComponentNameLabelComputer();

        var visible = computer.GetVisibleLabelIds(new[] { a, b }, hoveredComponentId: null, WideViewport, zoom: 1.0);

        visible.ShouldBe(new[] { b.Component.Id });
    }

    [AvaloniaFact]
    public void OverlappingComponents_HoveredNameWinsOverUnhoveredNormal()
    {
        var a = MakeComponent("aName", x: 0, y: 0);
        var b = MakeComponent("bName", x: 5, y: 0);
        var computer = new ComponentNameLabelComputer();

        var visible = computer.GetVisibleLabelIds(new[] { a, b }, b.Component.Id, WideViewport, zoom: 1.0);

        visible.ShouldBe(new[] { b.Component.Id });
    }

    [AvaloniaFact]
    public void ComponentGroups_AreNeverIncluded()
    {
        var group = new ComponentViewModel(new ComponentGroup("MyGroup") { PhysicalX = 0, PhysicalY = 0 });
        var plain = MakeComponent("plain", x: 500, y: 500);
        var computer = new ComponentNameLabelComputer();

        var visible = computer.GetVisibleLabelIds(new[] { group, plain }, hoveredComponentId: null, WideViewport, zoom: 1.0);

        visible.ShouldBe(new[] { plain.Component.Id });
    }

    [AvaloniaFact]
    public void ComponentFarOutsideViewport_IsCulled()
    {
        var offscreen = MakeComponent("offscreen", x: 100_000, y: 100_000);
        var computer = new ComponentNameLabelComputer();

        var visible = computer.GetVisibleLabelIds(new[] { offscreen }, hoveredComponentId: null, WideViewport, zoom: 1.0);

        visible.ShouldBeEmpty();
    }

    [AvaloniaFact]
    public void CullingUsesMeasuredLabelBounds_NotJustComponentFootprint()
    {
        // A small component's own footprint (x:[-20,-10]) sits entirely outside the viewport
        // (x:[0,50]), but its long name label — anchored just inside the footprint's left edge
        // and extending rightward by its measured text width — reaches into the viewport.
        // Culling against the footprint alone would wrongly drop a label that is genuinely
        // drawn on screen.
        var viewport = new Rect(0, 0, 50, 50);
        var comp = MakeComponent("VeryLongComponentNameThatExtendsFar", x: -20, y: 0, width: 10, height: 10);
        var computer = new ComponentNameLabelComputer();

        var visible = computer.GetVisibleLabelIds(new[] { comp }, hoveredComponentId: null, viewport, zoom: 1.0);

        visible.ShouldBe(new[] { comp.Component.Id });
    }

    [AvaloniaFact]
    public void MovingAComponent_TriggersRebuildAndUpdatesResult()
    {
        var comp = MakeComponent("mover", x: 0, y: 0);
        var computer = new ComponentNameLabelComputer();
        var components = new[] { comp };
        var farViewport = new Rect(190, -10, 20, 20);

        computer.GetVisibleLabelIds(components, hoveredComponentId: null, WideViewport, zoom: 1.0);
        computer.RebuildCount.ShouldBe(1);

        comp.X = 200;
        var after = computer.GetVisibleLabelIds(components, hoveredComponentId: null, farViewport, zoom: 1.0);

        computer.RebuildCount.ShouldBe(2, "a moved component changes the content signature and must trigger a rebuild");
        after.ShouldBe(new[] { comp.Component.Id });
    }

    [AvaloniaFact]
    public void RenamingAComponent_InvalidatesTheCache()
    {
        // Component.Identifier is a stable alias, but HumanReadableName is the user-facing,
        // editable display name (ComponentViewModel.Name) actually measured and drawn — a
        // rename must invalidate cached bounds/text or the old label lingers as stale overlap
        // input until an unrelated change happens to invalidate it.
        var comp = MakeComponent("original", x: 0, y: 0);
        var computer = new ComponentNameLabelComputer();
        var components = new[] { comp };

        computer.GetVisibleLabelIds(components, hoveredComponentId: null, WideViewport, zoom: 1.0);
        computer.RebuildCount.ShouldBe(1);

        comp.Component.HumanReadableName = "SomethingCompletelyDifferentAndMuchLonger";
        computer.GetVisibleLabelIds(components, hoveredComponentId: null, WideViewport, zoom: 1.0);

        computer.RebuildCount.ShouldBe(2, "renaming a component must invalidate the cached signature");
    }

    [AvaloniaFact]
    public void ResizingAComponent_InvalidatesTheCache()
    {
        var comp = MakeComponent("resizable", x: 0, y: 0);
        var computer = new ComponentNameLabelComputer();
        var components = new[] { comp };

        computer.GetVisibleLabelIds(components, hoveredComponentId: null, WideViewport, zoom: 1.0);
        computer.RebuildCount.ShouldBe(1);

        comp.Component.WidthMicrometers *= 2;
        computer.GetVisibleLabelIds(components, hoveredComponentId: null, WideViewport, zoom: 1.0);

        computer.RebuildCount.ShouldBe(2, "resizing a component must invalidate the cached signature");
    }

    [AvaloniaFact]
    public void RotatingAComponent_InvalidatesTheCache()
    {
        var comp = MakeComponent("rotatable", x: 0, y: 0);
        var computer = new ComponentNameLabelComputer();
        var components = new[] { comp };

        computer.GetVisibleLabelIds(components, hoveredComponentId: null, WideViewport, zoom: 1.0);
        computer.RebuildCount.ShouldBe(1);

        comp.Component.RotationDegrees = 90;
        computer.GetVisibleLabelIds(components, hoveredComponentId: null, WideViewport, zoom: 1.0);

        computer.RebuildCount.ShouldBe(2, "rotating a component must invalidate the cached signature");
    }

    [AvaloniaFact]
    public void PanningAlone_DoesNotTriggerRebuild()
    {
        // Two viewports of the same size and zoom, one a pure translation of the other: the
        // overlap RESULT is translation-invariant (relative component positions are unchanged),
        // so the expensive resolve must not rerun just because the visible world region shifted
        // — only the cheap per-frame viewport-intersection culling may differ.
        var a = MakeComponent("aName", x: 0, y: 0);
        var b = MakeComponent("bName", x: 5, y: 0);
        var computer = new ComponentNameLabelComputer();
        var components = new[] { a, b };
        var viewport1 = new Rect(-50, -50, 200, 200);
        var viewport2 = new Rect(-30, -20, 200, 200); // panned, same size/zoom

        var visible1 = computer.GetVisibleLabelIds(components, hoveredComponentId: null, viewport1, zoom: 1.0);
        computer.RebuildCount.ShouldBe(1);
        var visible2 = computer.GetVisibleLabelIds(components, hoveredComponentId: null, viewport2, zoom: 1.0);

        computer.RebuildCount.ShouldBe(1, "panning the viewport must not re-trigger the overlap sweep");
        visible2.ShouldBe(visible1, ignoreOrder: true);
    }

    [AvaloniaFact]
    public void SmallZoomChangeWithinQuantizationBucket_DoesNotTriggerRebuild()
    {
        var comp = MakeComponent("stable", x: 0, y: 0);
        var computer = new ComponentNameLabelComputer();
        var components = new[] { comp };

        computer.GetVisibleLabelIds(components, hoveredComponentId: null, WideViewport, zoom: 1.0);
        computer.RebuildCount.ShouldBe(1);

        computer.GetVisibleLabelIds(components, hoveredComponentId: null, WideViewport, zoom: 1.01);

        computer.RebuildCount.ShouldBe(1, "a sub-5% zoom change must stay within the same quantization bucket");
    }

    [AvaloniaFact]
    public void ZoomChangeCrossingQuantizationBucket_TriggersRebuild()
    {
        var comp = MakeComponent("stable", x: 0, y: 0);
        var computer = new ComponentNameLabelComputer();
        var components = new[] { comp };

        computer.GetVisibleLabelIds(components, hoveredComponentId: null, WideViewport, zoom: 1.0);
        computer.RebuildCount.ShouldBe(1);

        computer.GetVisibleLabelIds(components, hoveredComponentId: null, WideViewport, zoom: 3.0);

        computer.RebuildCount.ShouldBe(2, "a large zoom change must cross a 5% bucket and trigger a rebuild");
    }

    [AvaloniaFact]
    public void UnrelatedComponentMoving_ReusesTheUnchangedComponentsMeasuredText()
    {
        // Both components' text is measured once. Moving "beta" changes the content signature
        // (forces a rebuild), but "alpha"'s name and font size are unchanged, so its cached
        // FormattedText must be reused rather than re-measured — the fix for the double-measure
        // this computer used to cause every rebuild.
        var alpha = MakeComponent("Alpha", x: 0, y: 0);
        var beta = MakeComponent("Beta", x: 500, y: 500);
        var computer = new ComponentNameLabelComputer();
        var components = new[] { alpha, beta };

        computer.GetVisibleLabelIds(components, hoveredComponentId: null, WideViewport, zoom: 1.0);
        int textCountAfterFirstBuild = computer.MeasuredTextCount;

        beta.X = 600;
        computer.GetVisibleLabelIds(components, hoveredComponentId: null, WideViewport, zoom: 1.0);

        computer.RebuildCount.ShouldBe(2);
        computer.MeasuredTextCount.ShouldBe(textCountAfterFirstBuild,
            "alpha's (name, font size) text was already cached and must not be re-measured");
    }

    [AvaloniaFact]
    public void AtExtremeZoomOut_LabelRemainsVisibleAtClampedMinimumSize()
    {
        // A hard "hide below readability" cutoff used to make even a hovered/selected label
        // disappear at low zoom, breaking hover feedback and name-based orientation. The font
        // size now only clamps to a legible minimum — the label itself always stays visible.
        var comp = MakeComponent("stillHere", x: 0, y: 0);
        var computer = new ComponentNameLabelComputer();

        var visible = computer.GetVisibleLabelIds(new[] { comp }, hoveredComponentId: null, WideViewport, zoom: 0.05);

        visible.ShouldBe(new[] { comp.Component.Id });
        computer.TryGetLabelText(comp.Component.Id).ShouldNotBeNull();
    }

    [AvaloniaFact]
    public void TryGetLabelText_ReturnsTheSamePreMeasuredInstance_ForRepeatedCalls()
    {
        var comp = MakeComponent("cached", x: 0, y: 0);
        var computer = new ComponentNameLabelComputer();

        computer.GetVisibleLabelIds(new[] { comp }, hoveredComponentId: null, WideViewport, zoom: 1.0);
        var first = computer.TryGetLabelText(comp.Component.Id);
        var second = computer.TryGetLabelText(comp.Component.Id);

        first.ShouldNotBeNull();
        ReferenceEquals(first, second).ShouldBeTrue(
            "the renderer must draw the exact FormattedText this computer measured, not a fresh copy");
    }

    private static ComponentViewModel MakeComponent(
        string identifier, double x, double y, double? width = null, double? height = null)
    {
        var component = TestComponentFactory.CreateBasicComponent();
        component.Identifier = identifier;
        component.PhysicalX = x;
        component.PhysicalY = y;
        if (width.HasValue) component.WidthMicrometers = width.Value;
        if (height.HasValue) component.HeightMicrometers = height.Value;
        return new ComponentViewModel(component) { X = x, Y = y };
    }
}
