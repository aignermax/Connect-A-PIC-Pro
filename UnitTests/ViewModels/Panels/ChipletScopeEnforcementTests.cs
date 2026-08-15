using CAP.Avalonia.Commands;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Components.Core;
using CAP_Core.Components.Creation;
using CAP_Core.Components.Process;
using CAP_Core.LightCalculation;
using Shouldly;
using Xunit;

namespace UnitTests.ViewModels.Panels;

/// <summary>
/// End-to-end wiring for per-chiplet process scope (issue #935): the placement and paste
/// guards in <see cref="CanvasInteractionViewModel"/> must resolve the chiplet under the
/// drop point and check against <see cref="ComponentGroup.FabricationProcess"/> when one is
/// bound, falling back to the canvas-global active process for ungrouped content.
/// </summary>
public class ChipletScopeEnforcementTests
{
    private static ActiveProcessSelection Soi(params string[] memberPdkNames) =>
        ActiveProcessSelection.ForGroup(new ProcessGroup(
            "SOI 220",
            new ProcessFingerprint("Si", 220, "SiO2", 1550, "SOI 220"),
            memberPdkNames));

    private static ActiveProcessSelection InP(params string[] memberPdkNames) =>
        ActiveProcessSelection.ForGroup(new ProcessGroup(
            "HHI-InP",
            new ProcessFingerprint("InP", 400, "InP", 1550, "HHI-InP"),
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

    private static PlacementPolicyContext Context(ActiveProcessSelection? canvasActive) =>
        new(() => canvasActive,
            () => Array.Empty<string>(),
            _ => null);

    private static (DesignCanvasViewModel canvas, CanvasInteractionViewModel interaction) CreateSetup()
    {
        var canvas = new DesignCanvasViewModel();
        var interaction = new CanvasInteractionViewModel(canvas, new CommandManager());
        return (canvas, interaction);
    }

    /// <summary>Adds a chiplet covering (0,0)-(200,200) with the given process binding.</summary>
    private static ComponentGroup AddChiplet(
        DesignCanvasViewModel canvas, ActiveProcessSelection? process, string name = "Chiplet")
    {
        var chiplet = new ComponentGroup(name)
        {
            PhysicalX = 0,
            PhysicalY = 0,
            WidthMicrometers = 200,
            HeightMicrometers = 200,
            FabricationProcess = process
        };
        canvas.AddComponent(chiplet);
        return chiplet;
    }

    [Fact]
    public void PlaceComponent_OntoInpChiplet_UnderSoiCanvas_AllowsInpMember()
    {
        var (canvas, interaction) = CreateSetup();
        AddChiplet(canvas, InP("InP-Lib"));
        interaction.PlacementContext = Context(Soi("Demo"));
        interaction.SelectedTemplate = BuildTemplate("InP-Lib");

        interaction.CanvasClicked(100, 100);

        canvas.Components.Count.ShouldBe(2,
            "an InP member must drop into the InP chiplet even though the canvas is locked to SOI");
    }

    [Fact]
    public void PlaceComponent_OntoInpChiplet_BlocksCanvasMemberForeignToChiplet()
    {
        var (canvas, interaction) = CreateSetup();
        AddChiplet(canvas, InP("InP-Lib"));
        interaction.PlacementContext = Context(Soi("Demo"));
        interaction.SelectedTemplate = BuildTemplate("Demo");

        string? status = null;
        interaction.UpdateStatus = s => status = s;

        interaction.CanvasClicked(100, 100);

        canvas.Components.Count.ShouldBe(1,
            "an SOI member must not pollute the InP chiplet, even though the canvas allows it");
        status.ShouldNotBeNull();
        status!.ShouldContain("HHI-InP");
    }

    [Fact]
    public void PlaceComponent_BesideChiplet_UsesCanvasGlobalRule()
    {
        var (canvas, interaction) = CreateSetup();
        AddChiplet(canvas, InP("InP-Lib"));
        interaction.PlacementContext = Context(Soi("Demo"));
        interaction.SelectedTemplate = BuildTemplate("InP-Lib");

        string? status = null;
        interaction.UpdateStatus = s => status = s;

        // Drop well outside the chiplet bounds (200,200): the canvas-global SOI lock applies.
        interaction.CanvasClicked(500, 500);

        canvas.Components.Count.ShouldBe(1,
            "outside the chiplet the InP component is foreign to the SOI canvas and must be blocked");
        status.ShouldNotBeNull();
        status!.ShouldContain("SOI 220");
    }

    [Fact]
    public void PlaceComponent_OntoChipletWithoutBinding_UsesCanvasGlobalRule()
    {
        var (canvas, interaction) = CreateSetup();
        AddChiplet(canvas, process: null);
        interaction.PlacementContext = Context(Soi("Demo"));
        interaction.SelectedTemplate = BuildTemplate("InP-Lib");

        string? status = null;
        interaction.UpdateStatus = s => status = s;

        interaction.CanvasClicked(100, 100);

        canvas.Components.Count.ShouldBe(1,
            "a chiplet with no process binding falls back to the canvas-global check");
        status.ShouldNotBeNull();
        status!.ShouldContain("SOI 220");
    }

    [Fact]
    public void PlaceComponent_OntoUnboundInnerGroupNestedInBoundChiplet_UsesOuterChipletScope()
    {
        var (canvas, interaction) = CreateSetup();

        // Outer chiplet (InP) contains an unbound inner group; a drop onto the inner group
        // must still be checked against the outer chiplet's InP process, not the canvas.
        var outer = new ComponentGroup("Outer InP chiplet")
        {
            PhysicalX = 0,
            PhysicalY = 0,
            WidthMicrometers = 200,
            HeightMicrometers = 200,
            FabricationProcess = InP("InP-Lib")
        };
        var inner = new ComponentGroup("Inner unbound group")
        {
            PhysicalX = 50,
            PhysicalY = 50,
            WidthMicrometers = 100,
            HeightMicrometers = 100
        };
        outer.AddChild(inner);
        canvas.AddComponent(outer);

        interaction.PlacementContext = Context(Soi("Demo"));
        interaction.SelectedTemplate = BuildTemplate("InP-Lib");

        interaction.CanvasClicked(100, 100);

        canvas.Components.Count.ShouldBe(2,
            "a drop onto the unbound inner group must defer to the outer InP chiplet's binding");
    }

    [Fact]
    public void PasteSelected_IntoChipletAtTarget_UsesChipletScope()
    {
        var (canvas, interaction) = CreateSetup();
        AddChiplet(canvas, InP("InP-Lib"));

        // Copy a component whose PDK matches the chiplet but not the canvas; without the
        // chiplet scope the paste guard would reject it under the SOI canvas lock.
        var template = BuildTemplate("InP-Lib");
        var component = new Component(
            new Dictionary<int, SMatrix>(),
            new List<Slider>(),
            "inp_func",
            "",
            new Part[1, 1] { { new Part() } },
            -1,
            $"comp_{Guid.NewGuid():N}",
            DiscreteRotation.R0,
            new List<PhysicalPin>())
        {
            PhysicalX = 500,
            PhysicalY = 500,
            WidthMicrometers = 10,
            HeightMicrometers = 10
        };
        var vm = canvas.AddComponent(component, template.Name, template.PdkSource);
        canvas.Selection.SelectSingle(vm);
        interaction.CopySelectedCommand.Execute(null);

        interaction.PlacementContext = Context(Soi("Demo"));
        canvas.Clipboard.PdkSourceResolver = _ => "InP-Lib";

        int countBefore = canvas.Components.Count;
        interaction.PasteSelected(targetX: 100, targetY: 100);

        canvas.Components.Count.ShouldBe(countBefore + 1,
            "pasting an InP member into the InP chiplet must succeed even under an SOI canvas");
    }
}
