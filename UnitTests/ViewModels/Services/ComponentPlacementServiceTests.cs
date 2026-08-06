using System.Collections.ObjectModel;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Canvas.Services;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using Shouldly;
using UnitTests.Helpers;

namespace UnitTests.ViewModels.Services;

/// <summary>
/// Unit tests for ComponentPlacementService - collision detection, placement, and movement.
/// </summary>
public class ComponentPlacementServiceTests
{
    private readonly ObservableCollection<ComponentViewModel> _components = new();
    private readonly ObservableCollection<WaveguideConnectionViewModel> _connections = new();
    private readonly ComponentPlacementService _service;

    public ComponentPlacementServiceTests()
    {
        _service = new ComponentPlacementService(_components, _connections);
    }

    [Fact]
    public void CanPlaceComponent_WithinBounds_ReturnsTrue()
    {
        var result = _service.CanPlaceComponent(100, 100, 50, 50);
        result.ShouldBeTrue();
    }

    [Fact]
    public void CanPlaceComponent_OutsideBounds_ReturnsFalse()
    {
        _service.CanPlaceComponent(-10, 0, 50, 50).ShouldBeFalse();
        _service.CanPlaceComponent(0, -10, 50, 50).ShouldBeFalse();
        _service.CanPlaceComponent(4980, 0, 50, 50).ShouldBeFalse();
        _service.CanPlaceComponent(0, 4980, 50, 50).ShouldBeFalse();
    }

    [Fact]
    public void CanPlaceComponent_OverlappingExisting_ReturnsFalse()
    {
        var comp = TestComponentFactory.CreateStraightWaveGuide();
        comp.PhysicalX = 100;
        comp.PhysicalY = 100;
        comp.WidthMicrometers = 50;
        comp.HeightMicrometers = 50;
        _components.Add(new ComponentViewModel(comp));

        // Overlapping position (within gap tolerance)
        _service.CanPlaceComponent(120, 120, 50, 50).ShouldBeFalse();
    }

    [Fact]
    public void CanPlaceComponent_ExcludesSelf_ReturnsTrue()
    {
        var comp = TestComponentFactory.CreateStraightWaveGuide();
        comp.PhysicalX = 100;
        comp.PhysicalY = 100;
        comp.WidthMicrometers = 50;
        comp.HeightMicrometers = 50;
        var vm = new ComponentViewModel(comp);
        _components.Add(vm);

        _service.CanPlaceComponent(100, 100, 50, 50, excludeComponent: vm).ShouldBeTrue();
    }

    [Fact]
    public void FindValidPlacement_ExactPositionFree_ReturnsExact()
    {
        var result = _service.FindValidPlacement(100, 100, 50, 50);
        result.ShouldNotBeNull();
        result.Value.x.ShouldBe(100);
        result.Value.y.ShouldBe(100);
    }

    [Fact]
    public void FindValidPlacement_ExactPositionBlocked_FindsAlternative()
    {
        var comp = TestComponentFactory.CreateStraightWaveGuide();
        comp.PhysicalX = 100;
        comp.PhysicalY = 100;
        comp.WidthMicrometers = 50;
        comp.HeightMicrometers = 50;
        _components.Add(new ComponentViewModel(comp));

        var result = _service.FindValidPlacement(100, 100, 50, 50);
        result.ShouldNotBeNull();
        // Should find a position that doesn't overlap
        _service.CanPlaceComponent(result.Value.x, result.Value.y, 50, 50).ShouldBeTrue();
    }

    [Fact]
    public void ChipBoundaries_AreConfigurable()
    {
        _service.ChipMinX = 0;
        _service.ChipMinY = 0;
        _service.ChipMaxX = 100;
        _service.ChipMaxY = 100;

        _service.CanPlaceComponent(0, 0, 50, 50).ShouldBeTrue();
        _service.CanPlaceComponent(60, 60, 50, 50).ShouldBeFalse();
    }

    [Fact]
    public void IsDragging_BypassesCollisionCheck_InMoveComponent()
    {
        var comp = TestComponentFactory.CreateStraightWaveGuide();
        comp.PhysicalX = 100;
        comp.PhysicalY = 100;
        comp.WidthMicrometers = 50;
        comp.HeightMicrometers = 50;
        var vm = new ComponentViewModel(comp);
        _components.Add(vm);

        var blocker = TestComponentFactory.CreateStraightWaveGuide();
        blocker.PhysicalX = 160;
        blocker.PhysicalY = 100;
        blocker.WidthMicrometers = 50;
        blocker.HeightMicrometers = 50;
        _components.Add(new ComponentViewModel(blocker));

        // Without dragging, can't move into overlapping position
        _service.IsDragging = false;
        var result = _service.MoveComponent(vm, 50, 0, false, null, null, null);
        result.ShouldBeFalse();

        // With dragging, movement is allowed (collision checked on drop)
        _service.IsDragging = true;
        result = _service.MoveComponent(vm, 50, 0, false, null, null, null);
        result.ShouldBeTrue();
    }

    [Fact]
    public void MoveComponent_UpdatesPhysicalPosition()
    {
        var comp = TestComponentFactory.CreateStraightWaveGuide();
        comp.PhysicalX = 100;
        comp.PhysicalY = 100;
        comp.WidthMicrometers = 50;
        comp.HeightMicrometers = 50;
        var vm = new ComponentViewModel(comp);
        _components.Add(vm);

        _service.MoveComponent(vm, 50, 30, false, null, null, null);

        vm.X.ShouldBe(150);
        vm.Y.ShouldBe(130);
        comp.PhysicalX.ShouldBe(150);
        comp.PhysicalY.ShouldBe(130);
    }

    [Fact]
    public void MoveComponent_LockedComponent_ReturnsFalse()
    {
        var comp = TestComponentFactory.CreateStraightWaveGuide();
        comp.PhysicalX = 100;
        comp.PhysicalY = 100;
        comp.IsLocked = true;
        var vm = new ComponentViewModel(comp);
        _components.Add(vm);

        var result = _service.MoveComponent(vm, 50, 0, false, null, null, null);
        result.ShouldBeFalse();
        vm.X.ShouldBe(100); // Position unchanged
    }

    // ── Pre-drag overlap grandfathering ──────────────────────────────────────

    [Fact]
    public void CanPlaceComponent_PreDragOverlapPartner_IsGrandfatheredDuringDragOnly()
    {
        // A stacked pair, as an exact GDS import leaves it: A and B genuinely overlap.
        var compA = TestComponentFactory.CreateStraightWaveGuide();
        compA.PhysicalX = 100;
        compA.PhysicalY = 100;
        compA.WidthMicrometers = 50;
        compA.HeightMicrometers = 50;
        var vmA = new ComponentViewModel(compA);
        _components.Add(vmA);

        var compB = TestComponentFactory.CreateStraightWaveGuide();
        compB.PhysicalX = 120; // overlaps A (A spans 100–150)
        compB.PhysicalY = 100;
        compB.WidthMicrometers = 50;
        compB.HeightMicrometers = 50;
        _components.Add(new ComponentViewModel(compB));

        _service.CanPlaceComponent(100, 100, 50, 50, excludeComponent: vmA).ShouldBeFalse(
            "without a drag there is no grandfathering — the overlap rejects");

        _service.BeginDrag(compA);
        _service.CanPlaceComponent(300, 300, 50, 50, excludeComponent: vmA).ShouldBeTrue(
            "a genuinely free spot stays placeable");
        _service.CanPlaceComponent(100, 100, 50, 50, excludeComponent: vmA).ShouldBeTrue(
            "re-drop onto the pre-drag overlap partner is grandfathered");
        _service.EndDrag();

        _service.CanPlaceComponent(100, 100, 50, 50, excludeComponent: vmA).ShouldBeFalse(
            "grandfathering ends with the drag");
    }

    [Fact]
    public void CanPlaceComponent_NewOverlapPartner_StillRejectedDuringDrag()
    {
        // A overlaps B pre-drag; C stands elsewhere — overlapping C is a NEW overlap.
        var compA = TestComponentFactory.CreateStraightWaveGuide();
        compA.PhysicalX = 100;
        compA.PhysicalY = 100;
        compA.WidthMicrometers = 50;
        compA.HeightMicrometers = 50;
        var vmA = new ComponentViewModel(compA);
        _components.Add(vmA);

        var compB = TestComponentFactory.CreateStraightWaveGuide();
        compB.PhysicalX = 120;
        compB.PhysicalY = 100;
        compB.WidthMicrometers = 50;
        compB.HeightMicrometers = 50;
        _components.Add(new ComponentViewModel(compB));

        var compC = TestComponentFactory.CreateStraightWaveGuide();
        compC.PhysicalX = 300;
        compC.PhysicalY = 300;
        compC.WidthMicrometers = 50;
        compC.HeightMicrometers = 50;
        _components.Add(new ComponentViewModel(compC));

        _service.BeginDrag(compA);
        _service.CanPlaceComponent(320, 300, 50, 50, excludeComponent: vmA).ShouldBeFalse(
            "C never overlapped A before the drag — the new overlap still rejects");
        _service.EndDrag();
    }

    [Fact]
    public void CanMoveComponentTo_StackedPair_ReDropOnOriginalPosition_Allowed()
    {
        var compA = TestComponentFactory.CreateStraightWaveGuide();
        compA.PhysicalX = 100;
        compA.PhysicalY = 100;
        compA.WidthMicrometers = 50;
        compA.HeightMicrometers = 50;
        var vmA = new ComponentViewModel(compA);
        _components.Add(vmA);

        var compB = TestComponentFactory.CreateStraightWaveGuide();
        compB.PhysicalX = 120;
        compB.PhysicalY = 100;
        compB.WidthMicrometers = 50;
        compB.HeightMicrometers = 50;
        _components.Add(new ComponentViewModel(compB));

        _service.BeginDrag(compA);
        _service.CanMoveComponentTo(vmA, 400, 400).ShouldBeTrue();
        _service.CanMoveComponentTo(vmA, 100, 100).ShouldBeTrue(
            "the drop check must allow restoring the pre-drag overlap");
        _service.EndDrag();
    }

    [Fact]
    public void CanMoveComponentTo_GroupMove_GrandfathersPreDragOverlap_RejectsNewOverlap()
    {
        // A group whose child is stacked onto an external component (exact import).
        var child = TestComponentFactory.CreateStraightWaveGuide();
        child.PhysicalX = 100;
        child.PhysicalY = 100;
        child.WidthMicrometers = 50;
        child.HeightMicrometers = 50;
        var group = new ComponentGroup("G") { PhysicalX = 100, PhysicalY = 100 };
        group.AddChild(child);
        var groupVm = new ComponentViewModel(group);
        _components.Add(groupVm);

        var stacked = TestComponentFactory.CreateStraightWaveGuide();
        stacked.PhysicalX = 120; // overlaps the group's child
        stacked.PhysicalY = 100;
        stacked.WidthMicrometers = 50;
        stacked.HeightMicrometers = 50;
        _components.Add(new ComponentViewModel(stacked));

        var elsewhere = TestComponentFactory.CreateStraightWaveGuide();
        elsewhere.PhysicalX = 400;
        elsewhere.PhysicalY = 400;
        elsewhere.WidthMicrometers = 50;
        elsewhere.HeightMicrometers = 50;
        _components.Add(new ComponentViewModel(elsewhere));

        _service.BeginDrag(group);
        _service.CanMoveComponentTo(groupVm, 100, 100).ShouldBeTrue(
            "delta zero restores the child's pre-drag overlap — grandfathered");
        _service.CanMoveComponentTo(groupVm, 400, 400).ShouldBeFalse(
            "moving the child onto a DIFFERENT component is a new overlap");
        _service.EndDrag();
        _service.CanMoveComponentTo(groupVm, 100, 100).ShouldBeFalse(
            "grandfathering ends with the drag");
    }
}
