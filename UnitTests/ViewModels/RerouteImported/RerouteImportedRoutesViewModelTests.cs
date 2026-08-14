using CAP.Avalonia.Commands;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Canvas.RerouteImported;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using Shouldly;
using Xunit;

namespace UnitTests.ViewModels.RerouteImported;

/// <summary>
/// Integration tests for the "Re-route imported routes" feature: frozen imported
/// routes are counted (including those living inside component groups, as after a
/// standard GDS import), re-routed on demand through ONE undoable command with a
/// before/after delta report, hand-edited frozen routes are kept unchanged, and
/// undo restores the exact imported geometry.
/// </summary>
public class RerouteImportedRoutesViewModelTests
{
    private const double DetourOffsetMicrometers = 800;
    private const double LengthTolerance = 1.0;

    private static (PhysicalPin startPin, PhysicalPin endPin) AddComponentPair(
        DesignCanvasViewModel canvas, double offsetY)
    {
        var startComp = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        startComp.WidthMicrometers = 250;
        startComp.HeightMicrometers = 250;
        startComp.PhysicalX = 0;
        startComp.PhysicalY = offsetY;

        var endComp = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        endComp.WidthMicrometers = 250;
        endComp.HeightMicrometers = 250;
        endComp.PhysicalX = 400;
        endComp.PhysicalY = offsetY;

        canvas.AddComponent(startComp);
        canvas.AddComponent(endComp);

        return (startComp.PhysicalPins.First(p => p.Name == "out"),
                endComp.PhysicalPins.First(p => p.Name == "in"));
    }

    /// <summary>
    /// Builds an intentionally suboptimal imported route: a long U-detour dipping
    /// through the free gap between the two components (collision-free, so a frozen
    /// recalc keeps it), like the geometry a GDS import recovers verbatim.
    /// </summary>
    private static RoutedPath CreateDetourPath(PhysicalPin startPin, PhysicalPin endPin)
    {
        var (sx, sy) = startPin.GetAbsolutePosition();
        var (ex, ey) = endPin.GetAbsolutePosition();
        double downX = sx + (ex - sx) / 3;
        double upX = sx + 2 * (ex - sx) / 3;
        double detourY = sy + DetourOffsetMicrometers;

        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(sx, sy, downX, sy, 0));
        path.Segments.Add(new StraightSegment(downX, sy, downX, detourY, 90));
        path.Segments.Add(new StraightSegment(downX, detourY, upX, detourY, 0));
        path.Segments.Add(new StraightSegment(upX, detourY, upX, sy, 270));
        path.Segments.Add(new StraightSegment(upX, sy, ex, ey, 0));
        return path;
    }

    private const int DetourSegmentCount = 5;

    private static WaveguideConnectionViewModel ConnectFrozenImported(
        DesignCanvasViewModel canvas, PhysicalPin startPin, PhysicalPin endPin)
    {
        var vm = canvas.ConnectPinsWithCachedRoute(
            startPin, endPin, CreateDetourPath(startPin, endPin));
        vm.ShouldNotBeNull();
        vm!.Connection.IsRouteFrozen = true;
        return vm;
    }

    [Fact]
    public void Refresh_CountsEligibleAndHandEditedFrozenRoutes()
    {
        var canvas = new DesignCanvasViewModel();
        var (startA, endA) = AddComponentPair(canvas, 0);
        var (startB, endB) = AddComponentPair(canvas, 2000);
        ConnectFrozenImported(canvas, startA, endA);
        var edited = ConnectFrozenImported(canvas, startB, endB);
        edited.Connection.BendRadiusOverrides[0] = 25;

        var vm = new RerouteImportedRoutesViewModel(canvas, new CommandManager());
        vm.Refresh();

        vm.FrozenImportedCount.ShouldBe(1);
        vm.HandEditedFrozenCount.ShouldBe(1);
        vm.HasFrozenImportedRoutes.ShouldBeTrue();
        vm.HasHandEditedFrozenRoutes.ShouldBeTrue();
        vm.FrozenCountText.ShouldNotBeNullOrEmpty();
        vm.HandEditedKeptText.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task RerouteAll_ReplacesFrozenDetour_WithShorterLiveRoute_AndReportsDelta()
    {
        var canvas = new DesignCanvasViewModel();
        var (startPin, endPin) = AddComponentPair(canvas, 0);
        var connVm = ConnectFrozenImported(canvas, startPin, endPin);
        double importedLength = connVm.Connection.PathLengthMicrometers;

        var vm = new RerouteImportedRoutesViewModel(canvas, new CommandManager());
        vm.Refresh();
        vm.RerouteAllCommand.CanExecute(null).ShouldBeTrue();

        await vm.RerouteAllCommand.ExecuteAsync(null);

        connVm.Connection.IsRouteFrozen.ShouldBeFalse();
        connVm.Connection.RoutedPath.ShouldNotBeNull();
        connVm.Connection.PathLengthMicrometers.ShouldBeLessThan(importedLength / 2,
            "the live router must replace the imported U-detour with a direct route");
        vm.ResultText.ShouldNotBeNullOrEmpty();
        vm.FrozenImportedCount.ShouldBe(0);
    }

    [Fact]
    public async Task Undo_RestoresImportedGeometry_AndRefreezes_RedoReroutesAgain()
    {
        var canvas = new DesignCanvasViewModel();
        var (startPin, endPin) = AddComponentPair(canvas, 0);
        var connVm = ConnectFrozenImported(canvas, startPin, endPin);
        double importedLength = connVm.Connection.PathLengthMicrometers;

        var commandManager = new CommandManager();
        var vm = new RerouteImportedRoutesViewModel(canvas, commandManager);
        vm.Refresh();
        await vm.RerouteAllCommand.ExecuteAsync(null);
        double reroutedLength = connVm.Connection.PathLengthMicrometers;

        commandManager.Undo().ShouldBeTrue();
        await canvas.RecalculateRoutesAsync();

        connVm.Connection.IsRouteFrozen.ShouldBeTrue();
        connVm.Connection.RoutedPath.ShouldNotBeNull();
        connVm.Connection.RoutedPath!.Segments.Count.ShouldBe(DetourSegmentCount);
        connVm.Connection.PathLengthMicrometers.ShouldBe(importedLength, LengthTolerance);
        vm.FrozenImportedCount.ShouldBe(1);

        commandManager.Redo().ShouldBeTrue();
        await canvas.RecalculateRoutesAsync();

        connVm.Connection.IsRouteFrozen.ShouldBeFalse();
        connVm.Connection.PathLengthMicrometers.ShouldBe(reroutedLength, LengthTolerance);
    }

    [Fact]
    public async Task RerouteAll_NeverTouchesHandEditedFrozenRoutes()
    {
        var canvas = new DesignCanvasViewModel();
        var (startA, endA) = AddComponentPair(canvas, 0);
        var (startB, endB) = AddComponentPair(canvas, 2000);
        var eligible = ConnectFrozenImported(canvas, startA, endA);
        var edited = ConnectFrozenImported(canvas, startB, endB);
        edited.Connection.BendRadiusOverrides[0] = 25;
        double editedLength = edited.Connection.PathLengthMicrometers;

        var vm = new RerouteImportedRoutesViewModel(canvas, new CommandManager());
        vm.Refresh();
        await vm.RerouteAllCommand.ExecuteAsync(null);

        eligible.Connection.IsRouteFrozen.ShouldBeFalse();
        edited.Connection.IsRouteFrozen.ShouldBeTrue();
        edited.Connection.RoutedPath!.Segments.Count.ShouldBe(DetourSegmentCount);
        edited.Connection.PathLengthMicrometers.ShouldBe(editedLength, LengthTolerance);
        vm.HandEditedFrozenCount.ShouldBe(1);
    }

    [Fact]
    public void CreateGroup_AfterImport_CountsGroupInternalFrozenRoutes_AndPanelAppears()
    {
        // Mirrors the real GDS import flow: frozen imported connections are created,
        // then GdsPlacementExecutor.CreateGroup wraps everything in ONE group via
        // CreateGroupCommand, which moves the connections off the canvas into the
        // group as FrozenWaveguidePaths.
        var canvas = new DesignCanvasViewModel();
        var (startA, endA) = AddComponentPair(canvas, 0);
        var (startB, endB) = AddComponentPair(canvas, 2000);
        ConnectFrozenImported(canvas, startA, endA);
        var edited = ConnectFrozenImported(canvas, startB, endB);
        edited.Connection.BendRadiusOverrides[0] = 25;

        var commandManager = new CommandManager();
        var vm = new RerouteImportedRoutesViewModel(canvas, commandManager);
        var groupCandidates = canvas.Components.ToList();
        commandManager.ExecuteCommand(new CreateGroupCommand(canvas, groupCandidates, "ImportedTopCell"));

        canvas.Connections.ShouldBeEmpty("grouping moves internal connections into the group");
        vm.FrozenImportedCount.ShouldBe(0);
        vm.GroupedFrozenCount.ShouldBe(1, "the hand-edited frozen path must not be counted");
        vm.HasGroupedFrozenRoutes.ShouldBeTrue();
        vm.IsPanelVisible.ShouldBeTrue("the panel must appear right after a standard grouped import");
        vm.GroupedFrozenText.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Refresh_ClearsResultText_WhenCanvasIsCleared()
    {
        var canvas = new DesignCanvasViewModel();
        var (startPin, endPin) = AddComponentPair(canvas, 0);
        ConnectFrozenImported(canvas, startPin, endPin);

        var vm = new RerouteImportedRoutesViewModel(canvas, new CommandManager());
        vm.Refresh();
        await vm.RerouteAllCommand.ExecuteAsync(null);
        vm.ResultText.ShouldNotBeNullOrEmpty();

        canvas.Components.Clear();
        canvas.Connections.Clear();

        vm.ResultText.ShouldBe("");
        vm.IsPanelVisible.ShouldBeFalse("a new/switched design must not show a stale delta");
    }

    [Fact]
    public async Task Refresh_ReactsToFrozenFlip_AfterRoutingPass()
    {
        var canvas = new DesignCanvasViewModel();
        var (startPin, endPin) = AddComponentPair(canvas, 0);
        var connVm = ConnectFrozenImported(canvas, startPin, endPin);

        var vm = new RerouteImportedRoutesViewModel(canvas, new CommandManager());
        vm.Refresh();
        vm.FrozenImportedCount.ShouldBe(1);

        // A frozen flip outside any command (e.g. moving an endpoint unfreezes the
        // route during the next routing pass) must be picked up after that pass.
        connVm.Connection.IsRouteFrozen = false;
        await canvas.RecalculateRoutesAsync();

        vm.FrozenImportedCount.ShouldBe(0);
    }

    [Fact]
    public void RerouteSelectedCommand_CanExecute_OnlyForEligibleSelection()
    {
        var canvas = new DesignCanvasViewModel();
        var (startPin, endPin) = AddComponentPair(canvas, 0);
        var connVm = ConnectFrozenImported(canvas, startPin, endPin);

        var vm = new RerouteImportedRoutesViewModel(canvas, new CommandManager());
        vm.Refresh();

        vm.RerouteSelectedCommand.CanExecute(null).ShouldBeFalse();

        vm.SelectedConnection = connVm;
        vm.RerouteSelectedCommand.CanExecute(null).ShouldBeTrue();

        connVm.Connection.IsRouteFrozen = false;
        vm.SelectedConnection = null;
        vm.SelectedConnection = connVm;
        vm.RerouteSelectedCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task RerouteAll_AfterGroupedImport_ReplacesGroupInternalFrozenRoute_InPlace()
    {
        var canvas = new DesignCanvasViewModel();
        var (startPin, endPin) = AddComponentPair(canvas, 0);
        var connVm = ConnectFrozenImported(canvas, startPin, endPin);
        double importedLength = connVm.Connection.PathLengthMicrometers;

        var commandManager = new CommandManager();
        var vm = new RerouteImportedRoutesViewModel(canvas, commandManager);
        var groupCandidates = canvas.Components.ToList();
        commandManager.ExecuteCommand(new CreateGroupCommand(canvas, groupCandidates, "ImportedTopCell"));

        vm.Refresh();
        vm.FrozenImportedCount.ShouldBe(0);
        vm.GroupedFrozenCount.ShouldBe(1);

        await vm.RerouteAllCommand.ExecuteAsync(null);

        var group = canvas.Components.Single().Component.ShouldBeOfType<ComponentGroup>();
        group.InternalPaths.Count.ShouldBe(1);
        group.InternalPaths[0].Path.Segments.Count.ShouldNotBe(DetourSegmentCount,
            "the router must replace the imported U-detour with a direct route");
        group.InternalPaths[0].Path.TotalLengthMicrometers.ShouldBeLessThan(importedLength / 2);
        vm.GroupedFrozenCount.ShouldBe(0);
        vm.ResultText.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Undo_RestoresGroupInternalImportedGeometry_AndGroupMembership()
    {
        var canvas = new DesignCanvasViewModel();
        var (startPin, endPin) = AddComponentPair(canvas, 0);
        var connVm = ConnectFrozenImported(canvas, startPin, endPin);
        double importedLength = connVm.Connection.PathLengthMicrometers;

        var commandManager = new CommandManager();
        var vm = new RerouteImportedRoutesViewModel(canvas, commandManager);
        var groupCandidates = canvas.Components.ToList();
        commandManager.ExecuteCommand(new CreateGroupCommand(canvas, groupCandidates, "ImportedTopCell"));

        var group = canvas.Components.Single().Component.ShouldBeOfType<ComponentGroup>();
        var originalPathId = group.InternalPaths[0].PathId;

        await vm.RerouteAllCommand.ExecuteAsync(null);

        commandManager.Undo().ShouldBeTrue();
        await canvas.RecalculateRoutesAsync();

        group.InternalPaths.Count.ShouldBe(1);
        group.InternalPaths[0].Path.Segments.Count.ShouldBe(DetourSegmentCount);
        group.InternalPaths[0].Path.TotalLengthMicrometers.ShouldBe(importedLength, LengthTolerance);
        group.InternalPaths[0].PathId.ShouldBe(originalPathId);
        vm.GroupedFrozenCount.ShouldBe(1);
    }

    [Fact]
    public async Task RerouteAll_NeverTouchesHandEditedGroupInternalRoutes()
    {
        var canvas = new DesignCanvasViewModel();
        var (startA, endA) = AddComponentPair(canvas, 0);
        var (startB, endB) = AddComponentPair(canvas, 2000);
        var eligible = ConnectFrozenImported(canvas, startA, endA);
        var edited = ConnectFrozenImported(canvas, startB, endB);
        edited.Connection.BendRadiusOverrides[0] = 25;
        double editedLength = edited.Connection.PathLengthMicrometers;

        var commandManager = new CommandManager();
        var vm = new RerouteImportedRoutesViewModel(canvas, commandManager);
        var groupCandidates = canvas.Components.ToList();
        commandManager.ExecuteCommand(new CreateGroupCommand(canvas, groupCandidates, "ImportedTopCell"));

        await vm.RerouteAllCommand.ExecuteAsync(null);

        var group = canvas.Components.Single().Component.ShouldBeOfType<ComponentGroup>();
        var eligiblePath = group.InternalPaths
            .First(p => ReferenceEquals(p.StartPin, startA) && ReferenceEquals(p.EndPin, endA));
        var editedPath = group.InternalPaths
            .First(p => ReferenceEquals(p.StartPin, startB) && ReferenceEquals(p.EndPin, endB));

        eligiblePath.Path.Segments.Count.ShouldNotBe(DetourSegmentCount,
            "the eligible group-internal route must be re-routed");
        editedPath.Path.Segments.Count.ShouldBe(DetourSegmentCount,
            "the hand-edited group-internal route must be kept unchanged");
        editedPath.BendRadiusOverrides.Count.ShouldBe(1);
        editedPath.Path.TotalLengthMicrometers.ShouldBe(editedLength, LengthTolerance);
    }
}
