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
/// Per-chiplet process scoping at the placement surfaces (issue #935): on a canvas locked
/// to one process, a uniformly foreign-process group places as its own chiplet (and gets
/// the derived process pinned as its <see cref="ComponentGroup.ProcessBinding"/>), drops
/// onto a bound chiplet resolve against the chiplet's process, and a copied chiplet pastes
/// across the process boundary — while loose foreign components and mixed groups stay
/// blocked by the canvas lock.
/// </summary>
public class ChipletPlacementTests : IDisposable
{
    private static readonly ProcessGroup SoiGroup = new(
        "SOI 220", new ProcessFingerprint("Si", 220, "SiO2", 1550, "SOI 220"), new[] { "Demo" });
    private static readonly ProcessGroup InPGroup = new(
        "InP", new ProcessFingerprint("InP", 300, "SiO2", 1550, "InP"), new[] { "HHI-InP" });
    private static readonly IReadOnlyList<ProcessGroup> Catalog = new[] { SoiGroup, InPGroup };

    private readonly string _testLibraryPath;
    private readonly GroupLibraryManager _libraryManager;
    private readonly DesignCanvasViewModel _canvas;
    private readonly CanvasInteractionViewModel _interaction;

    public ChipletPlacementTests()
    {
        _testLibraryPath = Path.Combine(Path.GetTempPath(), $"ChipletPlacementTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testLibraryPath);

        _libraryManager = new GroupLibraryManager(_testLibraryPath);
        _canvas = new DesignCanvasViewModel();
        _interaction = new CanvasInteractionViewModel(
            _canvas, new CommandManager(), new ComponentLibraryViewModel(_libraryManager));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testLibraryPath))
            Directory.Delete(_testLibraryPath, true);
    }

    private static ActiveProcessSelection Soi() => ActiveProcessSelection.ForGroup(SoiGroup);

    /// <summary>Maps a child's NazcaFunctionName to a PDK source, mimicking the library lookup.</summary>
    private static string? ResolveByNazcaFunction(Component component) => component.NazcaFunctionName switch
    {
        "member_func" => "Demo",
        "foreign_func" => "HHI-InP",
        _ => null
    };

    private void LockToSoi()
    {
        _interaction.PlacementContext = new PlacementPolicyContext(
            () => Soi(),
            () => Array.Empty<string>(),
            component => ResolveByNazcaFunction(component),
            getProcessCatalog: () => Catalog);
        _canvas.Clipboard.PdkSourceResolver = ResolveByNazcaFunction;
    }

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

    private void SelectGroupTemplate(string name, params string[] childNazcaFunctions)
    {
        var group = BuildGroup(name, childNazcaFunctions);
        var template = _libraryManager.SaveTemplate(group, name);
        template.TemplateGroup = group;
        _interaction.SelectedGroupTemplate = template;
    }

    private static ComponentGroup BuildGroup(string name, params string[] childNazcaFunctions)
    {
        var group = new ComponentGroup(name) { PhysicalX = 0, PhysicalY = 0 };
        for (int i = 0; i < childNazcaFunctions.Length; i++)
        {
            group.AddChild(new Component(
                new Dictionary<int, SMatrix>(),
                new List<Slider>(),
                childNazcaFunctions[i],
                "",
                new Part[1, 1] { { new Part() } },
                -1,
                $"comp_{i}_{Guid.NewGuid():N}",
                DiscreteRotation.R0,
                new List<PhysicalPin>())
            {
                PhysicalX = i * 100,
                PhysicalY = 0,
                WidthMicrometers = 50,
                HeightMicrometers = 30
            });
        }
        return group;
    }

    [Fact]
    public void PlaceGroupTemplate_UniformForeignGroup_OnLockedCanvas_PlacesAsBoundChiplet()
    {
        LockToSoi();
        SelectGroupTemplate("InP Chiplet", "foreign_func", "foreign_func");

        _interaction.CanvasClicked(500, 500);

        _canvas.Components.Count.ShouldBe(1,
            "a uniformly foreign-process group is placeable as its own chiplet");
        var placed = (ComponentGroup)_canvas.Components.Single().Component;
        placed.ProcessBinding.ShouldNotBeNull("the placed instance carries its chiplet process binding");
        placed.ProcessBinding!.DisplayName.ShouldBe("InP");
        placed.ProcessBinding.IsPlayground.ShouldBeFalse("a bound chiplet is manufacturable, not Playground");
    }

    [Fact]
    public void PlaceGroupTemplate_MixedGroup_OnLockedCanvas_StaysBlocked()
    {
        LockToSoi();
        SelectGroupTemplate("Mixed", "member_func", "foreign_func");

        string? status = null;
        _interaction.UpdateStatus = s => status = s;

        _interaction.CanvasClicked(500, 500);

        _canvas.Components.Count.ShouldBe(0, "a group no single process can fabricate is no chiplet");
        status.ShouldNotBeNull();
        status!.ShouldContain("HHI-InP");
    }

    [Fact]
    public void PlaceGroupTemplate_MemberGroup_GetsPinnedToCanvasProcess()
    {
        LockToSoi();
        SelectGroupTemplate("SOI Pair", "member_func", "member_func");

        _interaction.CanvasClicked(500, 500);

        _canvas.Components.Count.ShouldBe(1);
        var placed = (ComponentGroup)_canvas.Components.Single().Component;
        placed.ProcessBinding.ShouldNotBeNull();
        placed.ProcessBinding!.DisplayName.ShouldBe("SOI 220");
    }

    [Fact]
    public void PlaceComponent_OntoBoundChiplet_ResolvesAgainstChipletProcess()
    {
        LockToSoi();
        SelectGroupTemplate("InP Chiplet", "foreign_func", "foreign_func");
        _interaction.CanvasClicked(500, 500);
        _canvas.Components.Count.ShouldBe(1);

        var chipletVm = _canvas.Components.Single();
        double cx = chipletVm.X + chipletVm.Width / 2;
        double cy = chipletVm.Y + chipletVm.Height / 2;

        // Canvas member, but foreign to the chiplet: the chiplet's process rejects it.
        string? status = null;
        _interaction.UpdateStatus = s => status = s;
        _interaction.SelectedTemplate = BuildTemplate("Demo");
        _interaction.CanvasClicked(cx, cy);

        _canvas.Components.Count.ShouldBe(1, "canvas-member content is foreign to the chiplet");
        status.ShouldNotBeNull();
        status!.ShouldContain("InP Chiplet");

        // Foreign to the canvas, but matching the chiplet: the process check must pass
        // (geometry may still displace the drop — only the policy verdict is asserted here).
        _interaction.SelectedTemplate = BuildTemplate("HHI-InP");
        _interaction.CanvasClicked(cx, cy);

        status.ShouldNotBeNull();
        status!.Contains("fabricates").ShouldBeFalse(
            "the chiplet's own process must accept the drop at the policy level");
    }

    [Fact]
    public void PasteSelected_CopiedForeignChiplet_OnLockedCanvas_PastesAsChiplet()
    {
        var group = BuildGroup("InP Chiplet", "foreign_func", "foreign_func");
        var vm = _canvas.AddComponent(group);
        _canvas.Selection.SelectSingle(vm);
        _interaction.CopySelectedCommand.Execute(null);

        LockToSoi();

        int countBefore = _canvas.Components.Count;
        _interaction.PasteSelected();

        _canvas.Components.Count.ShouldBe(countBefore + 1,
            "a copied chiplet keeps its own process scope across paste");
    }

    [Fact]
    public void PasteSelected_LooseForeignComponent_OnLockedCanvas_StaysBlocked()
    {
        var group = BuildGroup("carrier", "foreign_func");
        var looseChild = group.ChildComponents.Single();
        var vm = _canvas.AddComponent(looseChild, "TestTemplate", "HHI-InP");
        _canvas.Selection.SelectSingle(vm);
        _interaction.CopySelectedCommand.Execute(null);

        LockToSoi();
        string? status = null;
        _interaction.UpdateStatus = s => status = s;

        int countBefore = _canvas.Components.Count;
        _interaction.PasteSelected();

        _canvas.Components.Count.ShouldBe(countBefore,
            "a loose foreign component must not slip through as a pseudo-chiplet");
        status.ShouldNotBeNull();
        status!.ShouldContain("SOI 220");
    }
}
