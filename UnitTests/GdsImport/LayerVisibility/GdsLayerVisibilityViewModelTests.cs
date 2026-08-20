using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CAP.Avalonia.Services.GdsImport.LayerVisibility;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.GdsImport.LayerVisibility;
using Shouldly;
using Xunit;

namespace UnitTests.GdsImport.LayerVisibility;

/// <summary>
/// The Imported Layers panel ViewModel (issue #858): rows mirror the canvas'
/// (layer, datatype) census, edits flow into the display state and mark the
/// design dirty, and load restores without dirtying.
/// </summary>
public class GdsLayerVisibilityViewModelTests
{
    [Fact]
    public void Refresh_BuildsOneRowPerPair_WithShapeCounts()
    {
        var (vm, _) = CreateWithTwoLayerComponent();

        vm.Refresh();

        vm.HasLayers.ShouldBeTrue();
        vm.Rows.Select(r => (r.Layer, r.DataType)).ShouldBe(new[] { (1, 0), (11, 0) });
        vm.Rows[0].DisplayName.ShouldBe("Layer 1/0");
        vm.Rows[0].ShapeCountText.ShouldBe("(1)");
        vm.Rows.ShouldAllBe(r => r.IsVisible && r.OpacityPercent == 100.0);
    }

    [Fact]
    public void Refresh_EmptyCanvas_HasNoLayers()
    {
        var vm = new GdsLayerVisibilityViewModel(new DesignCanvasViewModel());

        vm.Refresh();

        vm.HasLayers.ShouldBeFalse();
        vm.Rows.ShouldBeEmpty();
    }

    [Fact]
    public void HidingARow_UpdatesState_AndMarksDesignDirty()
    {
        var (vm, edits) = CreateWithTwoLayerComponent();
        vm.Refresh();

        vm.Rows[1].IsVisible = false;

        vm.State.EffectiveOpacity(11, 0).ShouldBe(0.0);
        vm.State.EffectiveOpacity(1, 0).ShouldBe(1.0);
        edits().ShouldBe(1);
    }

    [Fact]
    public void ChangingOpacity_UpdatesState()
    {
        var (vm, _) = CreateWithTwoLayerComponent();
        vm.Refresh();

        vm.Rows[0].OpacityPercent = 25.0;

        vm.State.EffectiveOpacity(1, 0).ShouldBe(0.25);
    }

    [Fact]
    public void EditingState_RequestsCanvasRepaint()
    {
        var canvas = new DesignCanvasViewModel();
        int repaints = 0;
        canvas.RepaintRequested = () => repaints++;
        var vm = new GdsLayerVisibilityViewModel(canvas);

        vm.State.Set(11, 0, isVisible: false, opacity: 1.0);

        repaints.ShouldBe(1);
    }

    [Fact]
    public void ShowAllLayers_ResetsStateAndRows_OnceDirty()
    {
        var (vm, edits) = CreateWithTwoLayerComponent();
        vm.Refresh();
        vm.ShowAllLayersCommand.Execute(null);
        edits().ShouldBe(0, "already all-default → no dirtying no-op reset");

        vm.Rows[1].IsVisible = false;
        vm.ShowAllLayersCommand.Execute(null);

        vm.State.CaptureForSave().ShouldBeNull();
        vm.Rows.ShouldAllBe(r => r.IsVisible);
        edits().ShouldBe(2, "one edit for the hide, one for the reset");
    }

    [Fact]
    public void Restore_AppliesLoadedSettingsToRows_WithoutDirtying()
    {
        var (vm, edits) = CreateWithTwoLayerComponent();

        vm.Restore(new[]
        {
            new GdsLayerVisibilityData { Layer = 11, DataType = 0, IsVisible = false, Opacity = 1.0 },
            new GdsLayerVisibilityData { Layer = 1, DataType = 0, IsVisible = true, Opacity = 0.5 },
        });

        vm.Rows[0].IsVisible.ShouldBeTrue();
        vm.Rows[0].OpacityPercent.ShouldBe(50.0);
        vm.Rows[1].IsVisible.ShouldBeFalse();
        edits().ShouldBe(0, "loading a design must not mark it dirty");
    }

    [Fact]
    public void ClearForNewDesign_DropsAllOverrides()
    {
        var (vm, _) = CreateWithTwoLayerComponent();
        vm.Refresh();
        vm.Rows[0].IsVisible = false;

        vm.ClearForNewDesign();

        vm.State.CaptureForSave().ShouldBeNull();
        vm.Rows.ShouldAllBe(r => r.IsVisible);
    }

    [AvaloniaFact]
    public void CanvasFrozenPathsChange_RefreshesRows()
    {
        var canvas = new DesignCanvasViewModel();
        var vm = new GdsLayerVisibilityViewModel(canvas);
        vm.Refresh();
        vm.HasLayers.ShouldBeFalse();

        canvas.CanvasFrozenPaths.Add(new CAP.Avalonia.ViewModels.Canvas.CanvasFrozenPathViewModel(
            LayerVisibilityTestComponents.CreateFrozenPath(31, 5)));
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);

        vm.HasLayers.ShouldBeTrue();
        vm.Rows.Select(r => (r.Layer, r.DataType)).ShouldBe(new[] { (31, 5) });
    }

    /// <summary>A VM over a canvas holding one component with outlines on (1,0) and (11,0),
    /// plus a counter of <c>SettingsEdited</c> callbacks.</summary>
    private static (GdsLayerVisibilityViewModel Vm, Func<int> Edits) CreateWithTwoLayerComponent()
    {
        var canvas = new DesignCanvasViewModel();
        canvas.AddComponent(LayerVisibilityTestComponents.CreateWithOutlines((1, 0), (11, 0)));
        var vm = new GdsLayerVisibilityViewModel(canvas);
        int edits = 0;
        vm.SettingsEdited = () => edits++;
        return (vm, () => edits);
    }
}
