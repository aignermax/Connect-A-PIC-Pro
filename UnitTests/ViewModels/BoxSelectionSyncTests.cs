using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Hierarchy;
using Shouldly;
using UnitTests;
using Xunit;

namespace UnitTests.ViewModels;

/// <summary>
/// Tests that box selection (rubber band) on the canvas is mirrored into the
/// hierarchy panel as a multi-node highlight, and that hierarchy-driven
/// single selection keeps working after a box selection.
/// Regression tests for the field bug where box-selected components were not
/// highlighted in the hierarchy panel.
/// </summary>
public class BoxSelectionSyncTests
{
    private static (DesignCanvasViewModel canvas, HierarchyPanelViewModel hierarchy,
        ComponentViewModel vm1, ComponentViewModel vm2, ComponentViewModel vm3) CreateSetup()
    {
        var canvas = new DesignCanvasViewModel();
        var hierarchy = new HierarchyPanelViewModel(canvas);

        var comps = new ComponentViewModel[3];
        for (int i = 0; i < 3; i++)
        {
            var comp = TestComponentFactory.CreateStraightWaveGuide();
            comp.PhysicalX = i * 1000;
            comp.PhysicalY = 100;
            comp.WidthMicrometers = 50;
            comp.HeightMicrometers = 50;
            comps[i] = canvas.AddComponent(comp, $"Waveguide{i + 1}");
        }

        hierarchy.RebuildTree();
        return (canvas, hierarchy, comps[0], comps[1], comps[2]);
    }

    /// <summary>Simulates the rubber-band release over the given rectangle.</summary>
    private static void BoxSelect(DesignCanvasViewModel canvas,
        double minX, double minY, double maxX, double maxY)
    {
        canvas.Selection.SelectInRectangle(canvas.Components, minX, minY, maxX, maxY);
    }

    [Fact]
    public void BoxSelection_OverThreeComponents_HierarchyHighlightsAllThree()
    {
        var (canvas, hierarchy, _, _, _) = CreateSetup();

        BoxSelect(canvas, -10, -10, 5000, 5000);

        canvas.Selection.SelectedComponents.Count.ShouldBe(3);
        hierarchy.RootNodes[0].IsSelected.ShouldBeTrue();
        hierarchy.RootNodes[1].IsSelected.ShouldBeTrue();
        hierarchy.RootNodes[2].IsSelected.ShouldBeTrue();
    }

    [Fact]
    public void BoxSelection_HierarchyMirror_DoesNotCollapseCanvasMultiSelection()
    {
        var (canvas, _, vm1, vm2, vm3) = CreateSetup();

        BoxSelect(canvas, -10, -10, 5000, 5000);

        // The hierarchy mirroring must not push a single-select back to the canvas.
        canvas.Selection.SelectedComponents.Count.ShouldBe(3);
        vm1.IsSelected.ShouldBeTrue();
        vm2.IsSelected.ShouldBeTrue();
        vm3.IsSelected.ShouldBeTrue();
    }

    [Fact]
    public void BoxSelection_OverSingleComponent_HierarchyHighlightsExactlyThatNode()
    {
        var (canvas, hierarchy, _, _, _) = CreateSetup();

        // Rectangle only covers the first component (x 0..50).
        BoxSelect(canvas, -10, -10, 300, 300);

        canvas.Selection.SelectedComponents.Count.ShouldBe(1);
        hierarchy.RootNodes[0].IsSelected.ShouldBeTrue();
        hierarchy.RootNodes[1].IsSelected.ShouldBeFalse();
        hierarchy.RootNodes[2].IsSelected.ShouldBeFalse();
    }

    [Fact]
    public void HierarchyClick_AfterBoxSelection_ReducesToSingleSelection()
    {
        var (canvas, hierarchy, _, vm2, _) = CreateSetup();
        BoxSelect(canvas, -10, -10, 5000, 5000);

        hierarchy.RootNodes[1].SelectCommand.Execute(null);

        canvas.SelectedComponent.ShouldBe(vm2);
        canvas.Selection.SelectedComponents.ShouldBe(new[] { vm2 });
        hierarchy.RootNodes[0].IsSelected.ShouldBeFalse();
        hierarchy.RootNodes[1].IsSelected.ShouldBeTrue();
        hierarchy.RootNodes[2].IsSelected.ShouldBeFalse();
    }

    [Fact]
    public void CanvasSingleSelection_AfterBoxSelection_HierarchyShowsOnlyThatNode()
    {
        var (canvas, hierarchy, _, vm2, _) = CreateSetup();
        BoxSelect(canvas, -10, -10, 5000, 5000);

        // A plain click on the canvas selects a single component.
        canvas.Selection.SelectSingle(vm2);
        canvas.SelectedComponent = vm2;

        hierarchy.RootNodes[0].IsSelected.ShouldBeFalse();
        hierarchy.RootNodes[1].IsSelected.ShouldBeTrue();
        hierarchy.RootNodes[2].IsSelected.ShouldBeFalse();
    }
}
