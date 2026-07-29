using CAP.Avalonia.Commands;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.Commands;

/// <summary>
/// Tests for copy/paste workflow (Ctrl+C / Ctrl+V).
/// Verifies that components can be copied, pasted with smart placement,
/// and automatically selected after paste.
/// </summary>
public class CopyPasteWorkflowTests
{
    /// <summary>
    /// Verifies that copying a single component stores it in the clipboard.
    /// </summary>
    [Fact]
    public void Copy_SingleComponent_StoresInClipboard()
    {
        var canvas = new DesignCanvasViewModel();
        var comp = CreateComponent(100, 50, 100, 100);
        var vm = canvas.AddComponent(comp);

        // Select the component
        canvas.Selection.SelectSingle(vm);

        // Copy to clipboard
        canvas.Clipboard.Copy(
            canvas.Selection.SelectedComponents.ToList(),
            canvas.Connections);

        canvas.Clipboard.HasContent.ShouldBeTrue("Clipboard should have content after copy");
    }

    /// <summary>
    /// Verifies that pasting creates a new component with an offset.
    /// </summary>
    [Fact]
    public void Paste_SingleComponent_CreatesNewComponentWithOffset()
    {
        var canvas = new DesignCanvasViewModel();
        var comp = CreateComponent(100, 50, 100, 100);
        var vm = canvas.AddComponent(comp);

        // Copy to clipboard
        canvas.Clipboard.Copy(
            new[] { vm },
            canvas.Connections);

        int initialCount = canvas.Components.Count;

        // Paste
        var cmd = new PasteComponentsCommand(canvas, canvas.Clipboard);
        cmd.Execute();

        canvas.Components.Count.ShouldBe(initialCount + 1, "One new component should be created");
        cmd.Result.ShouldNotBeNull("Paste result should contain pasted components");
        cmd.Result.Components.Count.ShouldBe(1, "Should paste exactly one component");

        var pastedVm = cmd.Result.Components[0];
        pastedVm.X.ShouldNotBe(vm.X, "Pasted component should have different X position");
        pastedVm.Y.ShouldNotBe(vm.Y, "Pasted component should have different Y position");
    }

    /// <summary>
    /// Verifies that pasting multiple components preserves their relative positions.
    /// </summary>
    [Fact]
    public void Paste_MultipleComponents_PreservesRelativePositions()
    {
        var canvas = new DesignCanvasViewModel();
        var comp1 = CreateComponent(100, 50, 100, 100);
        var comp2 = CreateComponent(100, 50, 300, 200);

        var vm1 = canvas.AddComponent(comp1);
        var vm2 = canvas.AddComponent(comp2);

        double deltaXOriginal = vm2.X - vm1.X;
        double deltaYOriginal = vm2.Y - vm1.Y;

        // Copy both components
        canvas.Clipboard.Copy(
            new[] { vm1, vm2 },
            canvas.Connections);

        // Paste
        var cmd = new PasteComponentsCommand(canvas, canvas.Clipboard);
        cmd.Execute();

        cmd.Result.ShouldNotBeNull();
        cmd.Result.Components.Count.ShouldBe(2, "Should paste both components");

        var pasted1 = cmd.Result.Components[0];
        var pasted2 = cmd.Result.Components[1];

        double deltaXPasted = pasted2.X - pasted1.X;
        double deltaYPasted = pasted2.Y - pasted1.Y;

        Math.Abs(deltaXPasted - deltaXOriginal).ShouldBeLessThan(1.0,
            "Relative X distance should be preserved");
        Math.Abs(deltaYPasted - deltaYOriginal).ShouldBeLessThan(1.0,
            "Relative Y distance should be preserved");
    }

    /// <summary>
    /// Verifies that pasting components with internal connections preserves those connections.
    /// </summary>
    [Fact]
    public void Paste_ConnectedComponents_PreservesConnections()
    {
        var canvas = new DesignCanvasViewModel();

        var comp1 = CreateComponentWithPins(100, 50, 100, 100);
        var comp2 = CreateComponentWithPins(100, 50, 300, 100);

        var vm1 = canvas.AddComponent(comp1);
        var vm2 = canvas.AddComponent(comp2);

        // Connect the components
        var pin1 = comp1.PhysicalPins[1]; // east0
        var pin2 = comp2.PhysicalPins[0]; // west0

        canvas.ConnectPins(pin1, pin2);

        int initialConnectionCount = canvas.Connections.Count;

        // Copy both components
        canvas.Clipboard.Copy(
            new[] { vm1, vm2 },
            canvas.Connections);

        // Paste
        var cmd = new PasteComponentsCommand(canvas, canvas.Clipboard);
        cmd.Execute();

        cmd.Result.ShouldNotBeNull();
        cmd.Result.Components.Count.ShouldBe(2, "Should paste both components");
        cmd.Result.Connections.Count.ShouldBe(1, "Should paste the internal connection");

        canvas.Connections.Count.ShouldBe(initialConnectionCount + 1,
            "Total connections should increase by 1");
    }

    /// <summary>
    /// Verifies that pasting a connection carries its routing settings — the connection
    /// type and the manually chosen bend radius — instead of resetting them to defaults
    /// (field report: copy/paste of an Auto connection with a 35 µm radius dropped it).
    /// </summary>
    [Fact]
    public void Paste_ConnectedComponents_PreservesConnectionSettings()
    {
        var canvas = new DesignCanvasViewModel();

        var comp1 = CreateComponentWithPins(100, 50, 100, 100);
        var comp2 = CreateComponentWithPins(100, 50, 300, 100);
        var vm1 = canvas.AddComponent(comp1);
        var vm2 = canvas.AddComponent(comp2);

        var sourceVm = canvas.ConnectPins(comp1.PhysicalPins[1], comp2.PhysicalPins[0]);
        sourceVm.ShouldNotBeNull();
        sourceVm.Connection.Type = WaveguideType.Bend;
        sourceVm.Connection.BendRadiusMicrometers = 35.0;
        sourceVm.Connection.WidthMicrometers = 1.25;

        canvas.Clipboard.Copy(new[] { vm1, vm2 }, canvas.Connections);
        var cmd = new PasteComponentsCommand(canvas, canvas.Clipboard);
        cmd.Execute();

        cmd.Result.ShouldNotBeNull();
        cmd.Result.Connections.Count.ShouldBe(1, "Should paste the internal connection");

        var pasted = cmd.Result.Connections[0].Connection;
        pasted.Type.ShouldBe(WaveguideType.Bend, "Connection type must survive paste");
        pasted.BendRadiusMicrometers.ShouldBe(35.0, "Manual bend radius must survive paste");
        pasted.WidthMicrometers.ShouldBe(1.25, "Waveguide width must survive paste");
    }

    /// <summary>
    /// The clipboard must snapshot a connection's geometry at copy time. If the source is
    /// re-routed after the copy, paste must still reproduce the copied route (translated onto
    /// the pasted pins) — a live reference would translate the source's newer, mismatched
    /// geometry and quietly fall back to a re-route on the next recalc.
    /// </summary>
    [Fact]
    public void Paste_ConnectedComponents_UsesRouteSnapshotFromCopyTime()
    {
        var canvas = new DesignCanvasViewModel();

        var comp1 = CreateComponentWithPins(100, 50, 100, 100);
        var comp2 = CreateComponentWithPins(100, 50, 300, 100);
        var vm1 = canvas.AddComponent(comp1);
        var vm2 = canvas.AddComponent(comp2);

        var copyTimeRoute = new RoutedPath();
        copyTimeRoute.Segments.Add(new StraightSegment(200, 125, 300, 125, 0));
        var sourceVm = canvas.ConnectPinsWithCachedRoute(
            comp1.PhysicalPins[1], comp2.PhysicalPins[0], copyTimeRoute);
        sourceVm.ShouldNotBeNull();

        canvas.Clipboard.Copy(new[] { vm1, vm2 }, canvas.Connections);

        // Source is re-routed to a very different geometry AFTER the copy.
        var laterRoute = new RoutedPath();
        laterRoute.Segments.Add(new StraightSegment(200, 900, 300, 900, 0));
        sourceVm.Connection.RestoreCachedPath(laterRoute);

        var cmd = new PasteComponentsCommand(canvas, canvas.Clipboard);
        cmd.Execute();

        var pasted = cmd.Result!.Connections[0].Connection;
        // 50 µm is the fixed paste offset; the copy-time Y was 125, the later one 900.
        pasted.RoutedPath!.Segments[0].StartPoint.Y.ShouldBe(175.0, 1e-9,
            "paste must reproduce the route captured at copy time, not the source's later geometry");
    }

    /// <summary>
    /// Verifies that undo removes pasted components and connections.
    /// </summary>
    [Fact]
    public void Paste_Undo_RemovesPastedComponents()
    {
        var canvas = new DesignCanvasViewModel();
        var comp = CreateComponent(100, 50, 100, 100);
        var vm = canvas.AddComponent(comp);

        canvas.Clipboard.Copy(
            new[] { vm },
            canvas.Connections);

        int initialCount = canvas.Components.Count;

        var cmd = new PasteComponentsCommand(canvas, canvas.Clipboard);
        cmd.Execute();

        canvas.Components.Count.ShouldBe(initialCount + 1);

        cmd.Undo();

        canvas.Components.Count.ShouldBe(initialCount,
            "Undo should remove pasted component");
    }

    /// <summary>
    /// Verifies that pasting when clipboard is empty returns null.
    /// </summary>
    [Fact]
    public void Paste_EmptyClipboard_ReturnsNull()
    {
        var canvas = new DesignCanvasViewModel();

        var cmd = new PasteComponentsCommand(canvas, canvas.Clipboard);
        cmd.Execute();

        cmd.Result.ShouldBeNull("Paste should return null when clipboard is empty");
    }

    /// <summary>
    /// Verifies that pasting at a specific cursor position places the component at that location.
    /// </summary>
    [Fact]
    public void Paste_AtCursorPosition_PlacesAtSpecifiedLocation()
    {
        var canvas = new DesignCanvasViewModel();

        // Place original component at (100, 100)
        var comp1 = CreateComponent(100, 50, 100, 100);
        var vm1 = canvas.AddComponent(comp1);

        // Copy it
        canvas.Clipboard.Copy(
            new[] { vm1 },
            canvas.Connections);

        // Paste at cursor position (300, 200)
        var targetX = 300.0;
        var targetY = 200.0;
        var cmd = new PasteComponentsCommand(canvas, canvas.Clipboard, targetX, targetY);
        cmd.Execute();

        cmd.Result.ShouldNotBeNull();
        var pastedVm = cmd.Result.Components[0];

        // The pasted component should be at the cursor position
        pastedVm.X.ShouldBe(targetX, "Pasted component should be at cursor X");
        pastedVm.Y.ShouldBe(targetY, "Pasted component should be at cursor Y");
    }

    /// <summary>
    /// Verifies that multiple paste operations create multiple copies.
    /// </summary>
    [Fact]
    public void Paste_MultipleTimes_CreatesMultipleCopies()
    {
        var canvas = new DesignCanvasViewModel();
        var comp = CreateComponent(100, 50, 100, 100);
        var vm = canvas.AddComponent(comp);

        canvas.Clipboard.Copy(
            new[] { vm },
            canvas.Connections);

        int initialCount = canvas.Components.Count;

        // Paste twice
        var cmd1 = new PasteComponentsCommand(canvas, canvas.Clipboard);
        cmd1.Execute();

        var cmd2 = new PasteComponentsCommand(canvas, canvas.Clipboard);
        cmd2.Execute();

        canvas.Components.Count.ShouldBe(initialCount + 2,
            "Should create 2 new components from 2 paste operations");
    }

    /// <summary>
    /// Verifies that copying and pasting a ComponentGroup preserves all child components.
    /// This is the integration test for issue #164.
    /// </summary>
    [Fact]
    public void Paste_ComponentGroup_CopiesAllChildComponents()
    {
        var canvas = new DesignCanvasViewModel();

        // Create a group with 2 child components
        var group = TestComponentFactory.CreateComponentGroup("TestGroup", addChildren: true);
        group.PhysicalX = 200;
        group.PhysicalY = 200;

        var groupVm = canvas.AddComponent(group);

        int initialComponentCount = canvas.Components.Count;
        int childCount = group.ChildComponents.Count;

        // Copy the group
        canvas.Clipboard.Copy(
            new[] { groupVm },
            canvas.Connections);

        // Paste the group
        var cmd = new PasteComponentsCommand(canvas, canvas.Clipboard);
        cmd.Execute();

        cmd.Result.ShouldNotBeNull("Paste should succeed");
        cmd.Result.Components.Count.ShouldBe(1, "Should paste exactly one component (the group)");

        var pastedGroupVm = cmd.Result.Components[0];
        pastedGroupVm.Component.ShouldBeOfType<ComponentGroup>("Pasted component should be a ComponentGroup");

        var pastedGroup = (ComponentGroup)pastedGroupVm.Component;

        // Verify child components were copied
        pastedGroup.ChildComponents.Count.ShouldBe(childCount,
            "Pasted group should have same number of children as original");

        // Verify children are different instances
        foreach (var pastedChild in pastedGroup.ChildComponents)
        {
            pastedChild.ShouldNotBeNull("Child component should not be null");
            group.ChildComponents.ShouldNotContain(pastedChild,
                "Pasted child should be a new instance, not a reference to original");
        }

        // Verify children have correct parent references
        foreach (var pastedChild in pastedGroup.ChildComponents)
        {
            pastedChild.ParentGroup.ShouldBe(pastedGroup,
                "Pasted child should reference the pasted group as parent");
        }
    }

    /// <summary>
    /// Verifies that pasting a group with internal connections preserves those connections.
    /// Note: This test verifies the structure is preserved, even though the internal paths
    /// list may be empty for groups created from TestComponentFactory.
    /// </summary>
    [Fact]
    public void Paste_ComponentGroupWithInternalPaths_PreservesGroupStructure()
    {
        var canvas = new DesignCanvasViewModel();

        // Create a simple group (internal paths would require more complex setup
        // with properly initialized components having matching logical/physical pins)
        var group = TestComponentFactory.CreateComponentGroup("ConnectedGroup", addChildren: true);
        group.PhysicalX = 100;
        group.PhysicalY = 100;

        int childCount = group.ChildComponents.Count;
        var groupVm = canvas.AddComponent(group);

        // Copy and paste
        canvas.Clipboard.Copy(new[] { groupVm }, canvas.Connections);
        var cmd = new PasteComponentsCommand(canvas, canvas.Clipboard);
        cmd.Execute();

        var pastedGroup = (ComponentGroup)cmd.Result.Components[0].Component;

        // Verify the group structure is preserved
        pastedGroup.ChildComponents.Count.ShouldBe(childCount,
            "Pasted group should have all children");

        // Verify children are independent copies
        foreach (var pastedChild in pastedGroup.ChildComponents)
        {
            group.ChildComponents.ShouldNotContain(pastedChild,
                "Pasted children should be new instances");
            pastedChild.ParentGroup.ShouldBe(pastedGroup,
                "Pasted children should reference the pasted group");
        }
    }

    /// <summary>
    /// Verifies that pasting a nested group (group within group) works correctly.
    /// </summary>
    [Fact]
    public void Paste_NestedComponentGroup_CopiesAllLevels()
    {
        var canvas = new DesignCanvasViewModel();

        var outerGroup = TestComponentFactory.CreateComponentGroup("OuterGroup", addChildren: false);
        var innerGroup = TestComponentFactory.CreateComponentGroup("InnerGroup", addChildren: true);
        outerGroup.AddChild(innerGroup);
        outerGroup.PhysicalX = 300;
        outerGroup.PhysicalY = 300;

        var groupVm = canvas.AddComponent(outerGroup);

        // Copy and paste
        canvas.Clipboard.Copy(new[] { groupVm }, canvas.Connections);
        var cmd = new PasteComponentsCommand(canvas, canvas.Clipboard);
        cmd.Execute();

        var pastedOuter = (ComponentGroup)cmd.Result.Components[0].Component;

        pastedOuter.ChildComponents.Count.ShouldBe(1, "Outer group should have one child");
        var pastedInner = pastedOuter.ChildComponents[0] as ComponentGroup;

        pastedInner.ShouldNotBeNull("Child should be a ComponentGroup");
        pastedInner.ChildComponents.Count.ShouldBe(2,
            "Inner group should have its 2 child components");
        pastedInner.ParentGroup.ShouldBe(pastedOuter,
            "Inner group should reference outer group as parent");
    }

    // =========================================================================
    // Integration tests for issue #271: Readable component names on paste
    // =========================================================================

    /// <summary>
    /// Verifies that pasted components get readable incremental names instead of GUIDs.
    /// This is the main integration test for issue #271.
    /// </summary>
    [Fact]
    public void Paste_Component_GeneratesReadableIncrementalName()
    {
        var canvas = new DesignCanvasViewModel();
        var comp = CreateComponent(100, 50, 100, 100);
        comp.Identifier = "MMI_1x2";
        var vm = canvas.AddComponent(comp);

        // Copy and paste
        canvas.Clipboard.Copy(new[] { vm }, canvas.Connections);
        var cmd = new PasteComponentsCommand(canvas, canvas.Clipboard);
        cmd.Execute();

        cmd.Result.ShouldNotBeNull();
        var pastedComp = cmd.Result.Components[0].Component;

        // Should have readable name without GUID
        pastedComp.Identifier.ShouldBe("MMI_1x2_1");
        pastedComp.Identifier.Length.ShouldBeLessThan(20);
        pastedComp.Identifier.ShouldNotContain("-"); // No GUID hyphens
    }

    /// <summary>
    /// Verifies that multiple paste operations create incrementing suffixes.
    /// </summary>
    [Fact]
    public void Paste_MultipleTimes_CreatesIncrementalSuffixes()
    {
        var canvas = new DesignCanvasViewModel();
        var comp = CreateComponent(100, 50, 100, 100);
        comp.Identifier = "Detector";
        var vm = canvas.AddComponent(comp);

        canvas.Clipboard.Copy(new[] { vm }, canvas.Connections);

        // Paste 3 times
        var cmd1 = new PasteComponentsCommand(canvas, canvas.Clipboard);
        cmd1.Execute();

        var cmd2 = new PasteComponentsCommand(canvas, canvas.Clipboard);
        cmd2.Execute();

        var cmd3 = new PasteComponentsCommand(canvas, canvas.Clipboard);
        cmd3.Execute();

        // Verify all components have incremental names
        canvas.Components.Count.ShouldBe(4); // Original + 3 copies
        canvas.Components[0].Component.Identifier.ShouldBe("Detector");
        canvas.Components[1].Component.Identifier.ShouldBe("Detector_1");
        canvas.Components[2].Component.Identifier.ShouldBe("Detector_2");
        canvas.Components[3].Component.Identifier.ShouldBe("Detector_3");
    }

    /// <summary>
    /// Verifies that pasted groups get readable names.
    /// </summary>
    [Fact]
    public void Paste_ComponentGroup_GeneratesReadableGroupName()
    {
        var canvas = new DesignCanvasViewModel();
        var group = TestComponentFactory.CreateComponentGroup("MyCircuit", addChildren: true);
        group.PhysicalX = 200;
        group.PhysicalY = 200;
        var groupVm = canvas.AddComponent(group);

        // Copy and paste
        canvas.Clipboard.Copy(new[] { groupVm }, canvas.Connections);
        var cmd = new PasteComponentsCommand(canvas, canvas.Clipboard);
        cmd.Execute();

        var pastedGroup = (ComponentGroup)cmd.Result.Components[0].Component;

        // Group should have readable name
        pastedGroup.Identifier.ShouldStartWith("group_MyCircuit_");
        pastedGroup.Identifier.Length.ShouldBeLessThan(30);
        pastedGroup.Identifier.ShouldNotContain("group_group_"); // No duplicate "group_"

        // Child components should also have readable names
        foreach (var child in pastedGroup.ChildComponents)
        {
            child.Identifier.Length.ShouldBeLessThan(50);
            child.Identifier.ShouldNotMatch(@"[0-9a-f]{32}"); // No full GUIDs
        }
    }

    /// <summary>
    /// Verifies that nested groups get readable names at all levels.
    /// </summary>
    [Fact]
    public void Paste_NestedGroup_AllLevelsGetReadableNames()
    {
        var canvas = new DesignCanvasViewModel();

        var outerGroup = TestComponentFactory.CreateComponentGroup("Outer", addChildren: false);
        var innerGroup = TestComponentFactory.CreateComponentGroup("Inner", addChildren: true);
        outerGroup.AddChild(innerGroup);
        outerGroup.PhysicalX = 100;
        outerGroup.PhysicalY = 100;

        var groupVm = canvas.AddComponent(outerGroup);

        // Copy and paste
        canvas.Clipboard.Copy(new[] { groupVm }, canvas.Connections);
        var cmd = new PasteComponentsCommand(canvas, canvas.Clipboard);
        cmd.Execute();

        var pastedOuter = (ComponentGroup)cmd.Result.Components[0].Component;
        var pastedInner = (ComponentGroup)pastedOuter.ChildComponents[0];

        // Outer group should have readable name
        pastedOuter.Identifier.ShouldStartWith("group_Outer_");
        pastedOuter.Identifier.Length.ShouldBeLessThan(30);

        // Inner group should have readable name
        pastedInner.Identifier.ShouldStartWith("group_Inner_");
        pastedInner.Identifier.Length.ShouldBeLessThan(30);

        // Inner group's children should have readable names
        foreach (var child in pastedInner.ChildComponents)
        {
            child.Identifier.Length.ShouldBeLessThan(50);
        }
    }

    /// <summary>
    /// Verifies that pasting multiple different components preserves uniqueness.
    /// </summary>
    [Fact]
    public void Paste_MultipleComponents_AllGetUniqueReadableNames()
    {
        var canvas = new DesignCanvasViewModel();

        var comp1 = CreateComponent(100, 50, 100, 100);
        comp1.Identifier = "Splitter";

        var comp2 = CreateComponent(100, 50, 300, 100);
        comp2.Identifier = "Combiner";

        var vm1 = canvas.AddComponent(comp1);
        var vm2 = canvas.AddComponent(comp2);

        // Copy both
        canvas.Clipboard.Copy(new[] { vm1, vm2 }, canvas.Connections);

        // Paste
        var cmd = new PasteComponentsCommand(canvas, canvas.Clipboard);
        cmd.Execute();

        // Verify both pasted components have readable names
        canvas.Components.Count.ShouldBe(4);
        canvas.Components[2].Component.Identifier.ShouldBe("Splitter_1");
        canvas.Components[3].Component.Identifier.ShouldBe("Combiner_1");
    }

    /// <summary>
    /// Verifies that undo/redo preserves the readable names (no regeneration).
    /// </summary>
    [Fact]
    public void Paste_UndoRedo_PreservesGeneratedNames()
    {
        var canvas = new DesignCanvasViewModel();
        var comp = CreateComponent(100, 50, 100, 100);
        comp.Identifier = "Filter";
        var vm = canvas.AddComponent(comp);

        canvas.Clipboard.Copy(new[] { vm }, canvas.Connections);

        var cmd = new PasteComponentsCommand(canvas, canvas.Clipboard);
        cmd.Execute();

        var originalPastedName = cmd.Result.Components[0].Component.Identifier;
        originalPastedName.ShouldBe("Filter_1");

        // Undo
        cmd.Undo();
        canvas.Components.Count.ShouldBe(1);

        // Redo
        cmd.Execute();
        canvas.Components.Count.ShouldBe(2);
        var redoPastedName = cmd.Result.Components[0].Component.Identifier;

        // Name should be preserved (same object instance)
        redoPastedName.ShouldBe(originalPastedName);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static Component CreateComponent(
        double widthMicrometers,
        double heightMicrometers,
        double x = 0,
        double y = 0)
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>
        {
            new("west0", 0, MatterType.Light, RectSide.Left),
            new("east0", 1, MatterType.Light, RectSide.Right),
        });

        var component = new Component(
            new Dictionary<int, CAP_Core.LightCalculation.SMatrix>(),
            new List<Slider>(),
            "test_component",
            "",
            parts,
            0,
            "TestComp",
            DiscreteRotation.R0);

        component.WidthMicrometers = widthMicrometers;
        component.HeightMicrometers = heightMicrometers;
        component.PhysicalX = x;
        component.PhysicalY = y;

        return component;
    }

    private static Component CreateComponentWithPins(
        double widthMicrometers,
        double heightMicrometers,
        double x,
        double y)
    {
        var physicalPins = new List<PhysicalPin>
        {
            new() { Name = "west0", OffsetXMicrometers = 0, OffsetYMicrometers = heightMicrometers / 2 },
            new() { Name = "east0", OffsetXMicrometers = widthMicrometers, OffsetYMicrometers = heightMicrometers / 2 },
        };

        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>
        {
            new("west0", 0, MatterType.Light, RectSide.Left),
            new("east0", 1, MatterType.Light, RectSide.Right),
        });

        var component = new Component(
            new Dictionary<int, CAP_Core.LightCalculation.SMatrix>(),
            new List<Slider>(),
            "test_component",
            "",
            parts,
            0,
            "TestComp",
            DiscreteRotation.R0,
            physicalPins);

        component.WidthMicrometers = widthMicrometers;
        component.HeightMicrometers = heightMicrometers;
        component.PhysicalX = x;
        component.PhysicalY = y;

        return component;
    }
}
