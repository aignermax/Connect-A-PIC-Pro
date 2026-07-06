using CAP.Avalonia.Commands;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Components.Creation;
using CAP_Core.Components.Process;
using CAP_Core.LightCalculation;
using CAP_Core.Tiles;
using Shouldly;
using Xunit;

namespace UnitTests.ViewModels.Panels;

/// <summary>
/// Enforces the single-process rule (issue #570) at the two points where a foreign-PDK
/// component could otherwise land on a process-locked design: interactive placement
/// (<see cref="CanvasInteractionViewModel.PlaceComponentAt"/> via <c>CanvasClicked</c>) and
/// paste (<see cref="CanvasInteractionViewModel.PasteSelected"/>).
/// </summary>
public class CanvasInteractionProcessEnforcementTests
{
    private static ActiveProcessSelection Soi(params string[] memberPdkNames) =>
        ActiveProcessSelection.ForGroup(new ProcessGroup(
            "SOI 220",
            new ProcessFingerprint("Si", 220, "SiO2", 1550, "SOI 220"),
            memberPdkNames));

    private static ComponentTemplate BuildTemplate(string pdkSource) => new()
    {
        Name = "TestComp",
        Category = "Test",
        PdkSource = pdkSource,
        WidthMicrometers = 10,
        HeightMicrometers = 10,
        PinDefinitions = new[] { new PinDefinition("a", 0, 5, 180) },
        CreateSMatrix = pins =>
        {
            var ids = pins.SelectMany(p => new[] { p.IDInFlow, p.IDOutFlow }).ToList();
            return new SMatrix(ids, new List<(Guid, double)>());
        }
    };

    private static (DesignCanvasViewModel canvas, CanvasInteractionViewModel interaction, CommandManager commandManager) CreateSetup()
    {
        var canvas = new DesignCanvasViewModel();
        var commandManager = new CommandManager();
        var interaction = new CanvasInteractionViewModel(canvas, commandManager);
        return (canvas, interaction, commandManager);
    }

    [Fact]
    public void PlaceComponentAt_ForeignProcessPdk_BlocksPlacementAndReportsStatus()
    {
        var (canvas, interaction, _) = CreateSetup();
        interaction.GetActiveProcess = () => Soi("Demo");
        interaction.GetProcessAgnosticPdkNames = () => Array.Empty<string>();

        interaction.SelectedTemplate = BuildTemplate("HHI-InP");

        string? status = null;
        interaction.UpdateStatus = s => status = s;

        interaction.CanvasClicked(100, 100);

        canvas.Components.Count.ShouldBe(0, "a foreign-process component must not be placed");
        status.ShouldNotBeNull();
        status!.ShouldContain("process");
    }

    [Fact]
    public void PlaceComponentAt_MemberPdk_IsPlaced()
    {
        var (canvas, interaction, _) = CreateSetup();
        interaction.GetActiveProcess = () => Soi("Demo");
        interaction.GetProcessAgnosticPdkNames = () => Array.Empty<string>();

        interaction.SelectedTemplate = BuildTemplate("Demo");

        interaction.CanvasClicked(100, 100);

        canvas.Components.Count.ShouldBe(1, "a member-PDK component must be placeable");
    }

    [Fact]
    public void PlaceComponentAt_BuiltIn_IsPlaced()
    {
        var (canvas, interaction, _) = CreateSetup();
        interaction.GetActiveProcess = () => Soi("Demo");
        interaction.GetProcessAgnosticPdkNames = () => Array.Empty<string>();

        interaction.SelectedTemplate = BuildTemplate("Built-in");

        interaction.CanvasClicked(100, 100);

        canvas.Components.Count.ShouldBe(1, "Built-in components are process-agnostic and always placeable");
    }

    [Fact]
    public void PlaceComponentAt_ProcessAgnosticToolPdk_IsPlaced()
    {
        var (canvas, interaction, _) = CreateSetup();
        interaction.GetActiveProcess = () => Soi("Demo");
        interaction.GetProcessAgnosticPdkNames = () => new[] { "Analysis Tools" };

        interaction.SelectedTemplate = BuildTemplate("Analysis Tools");

        interaction.CanvasClicked(100, 100);

        canvas.Components.Count.ShouldBe(1, "process-agnostic tool PDKs stay placeable under any active process");
    }

    [Fact]
    public void PlaceComponentAt_NoActiveProcess_AllowsAnyPdk()
    {
        var (canvas, interaction, _) = CreateSetup();
        // GetActiveProcess left unwired (null) — mirrors a fresh/Playground design.
        interaction.SelectedTemplate = BuildTemplate("HHI-InP");

        interaction.CanvasClicked(100, 100);

        canvas.Components.Count.ShouldBe(1, "with no active process locked in, any PDK is allowed");
    }

    [Fact]
    public void PlaceComponentAt_BlockedPlacement_DoesNotTouchUndoStack()
    {
        var (canvas, interaction, commandManager) = CreateSetup();
        interaction.GetActiveProcess = () => Soi("Demo");
        interaction.GetProcessAgnosticPdkNames = () => Array.Empty<string>();
        interaction.SelectedTemplate = BuildTemplate("HHI-InP");

        interaction.CanvasClicked(100, 100);

        canvas.Components.Count.ShouldBe(0);
        // Nothing was pushed onto the undo stack for the blocked attempt.
        commandManager.CanUndo.ShouldBeFalse();
    }

    [Fact]
    public void PasteSelected_ClipboardHasForeignProcessComponent_BlocksWholePasteAndReportsStatus()
    {
        var (canvas, interaction, _) = CreateSetup();

        // Place a component tagged with a foreign PDK directly on the canvas (bypassing the
        // placement guard under test), then copy it so the clipboard carries that PDK source.
        var comp = CreateComponent(10, 10);
        var vm = canvas.AddComponent(comp, "TestTemplate", "HHI-InP");
        canvas.Selection.SelectSingle(vm);

        interaction.CopySelectedCommand.Execute(null);
        canvas.Clipboard.HasContent.ShouldBeTrue();

        interaction.GetActiveProcess = () => Soi("Demo");
        interaction.GetProcessAgnosticPdkNames = () => Array.Empty<string>();

        string? status = null;
        interaction.UpdateStatus = s => status = s;

        int countBeforePaste = canvas.Components.Count;
        interaction.PasteSelected();

        canvas.Components.Count.ShouldBe(countBeforePaste, "the whole paste must be blocked, adding nothing");
        status.ShouldNotBeNull();
        status!.ShouldContain("1");
        status.ShouldContain("SOI 220");
    }

    [Fact]
    public void PasteSelected_ClipboardHasMemberPdkComponent_Pastes()
    {
        var (canvas, interaction, _) = CreateSetup();

        var comp = CreateComponent(10, 10);
        var vm = canvas.AddComponent(comp, "TestTemplate", "Demo");
        canvas.Selection.SelectSingle(vm);

        interaction.CopySelectedCommand.Execute(null);

        interaction.GetActiveProcess = () => Soi("Demo");
        interaction.GetProcessAgnosticPdkNames = () => Array.Empty<string>();

        int countBeforePaste = canvas.Components.Count;
        interaction.PasteSelected();

        canvas.Components.Count.ShouldBe(countBeforePaste + 1, "a member-PDK clipboard entry must paste normally");
    }

    [Fact]
    public void PlaceGroupTemplateAt_ChildResolvesToForeignProcessPdk_BlocksPlacementAndReportsStatus()
    {
        var canvas = new DesignCanvasViewModel();
        var commandManager = new CommandManager();
        var libraryVm = new ComponentLibraryViewModel(new GroupLibraryManager());
        var interaction = new CanvasInteractionViewModel(canvas, commandManager, libraryVm);

        interaction.GetActiveProcess = () => Soi("Demo");
        interaction.GetProcessAgnosticPdkNames = () => Array.Empty<string>();
        // Group templates record no PDK source — every child resolves through the library,
        // here to a PDK that is not a member of the active process.
        interaction.ResolveComponentPdkSource = _ => "HHI-InP";

        var group = TestComponentFactory.CreateComponentGroup("ForeignGroup", addChildren: true);
        interaction.SelectedGroupTemplate = new GroupTemplate
        {
            Name = "ForeignGroup",
            TemplateGroup = group,
            ComponentCount = group.ChildComponents.Count
        };

        string? status = null;
        interaction.UpdateStatus = s => status = s;

        interaction.CanvasClicked(100, 100);

        canvas.Components.Count.ShouldBe(0, "a group with a foreign-process child must not be placed");
        commandManager.CanUndo.ShouldBeFalse("the blocked attempt must not touch the undo stack");
        status.ShouldNotBeNull();
        status!.ShouldContain("process");
    }

    private static Component CreateComponent(double width, double height)
    {
        var parts = new Part[1, 1];
        parts[0, 0] = new Part(new List<Pin>
        {
            new("west0", 0, MatterType.Light, RectSide.Left),
            new("east0", 1, MatterType.Light, RectSide.Right),
        });

        var component = new Component(
            new Dictionary<int, SMatrix>(),
            new List<Slider>(),
            "test_component",
            "",
            parts,
            0,
            "TestComp",
            DiscreteRotation.R0);

        component.WidthMicrometers = width;
        component.HeightMicrometers = height;
        component.PhysicalX = 0;
        component.PhysicalY = 0;

        return component;
    }
}
