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
/// Closes the grouping bypass of single-process enforcement (issue #653): groups carry no
/// PdkSource, so their children must be checked individually when a group template is placed
/// (<see cref="CanvasInteractionViewModel.PlaceGroupTemplateAt"/> via <c>CanvasClicked</c>)
/// and when a copied group is pasted (<see cref="CanvasInteractionViewModel.PasteSelected"/>).
/// </summary>
public class GroupProcessEnforcementTests : IDisposable
{
    private readonly string _testLibraryPath;
    private readonly GroupLibraryManager _libraryManager;
    private readonly DesignCanvasViewModel _canvas;
    private readonly CanvasInteractionViewModel _interaction;
    private readonly CommandManager _commandManager;

    public GroupProcessEnforcementTests()
    {
        _testLibraryPath = Path.Combine(Path.GetTempPath(), $"GroupProcessEnforcementTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testLibraryPath);

        _libraryManager = new GroupLibraryManager(_testLibraryPath);
        _canvas = new DesignCanvasViewModel();
        _commandManager = new CommandManager();
        _interaction = new CanvasInteractionViewModel(
            _canvas, _commandManager, new ComponentLibraryViewModel(_libraryManager));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testLibraryPath))
            Directory.Delete(_testLibraryPath, true);
    }

    private static ActiveProcessSelection Soi(params string[] memberPdkNames) =>
        ActiveProcessSelection.ForGroup(new ProcessGroup(
            "SOI 220",
            new ProcessFingerprint("Si", 220, "SiO2", 1550, "SOI 220"),
            memberPdkNames));

    /// <summary>Maps a child's NazcaFunctionName to a PDK source, mimicking the library lookup.</summary>
    private static string? ResolveByNazcaFunction(Component component) => component.NazcaFunctionName switch
    {
        "member_func" => "Demo",
        "foreign_func" => "HHI-InP",
        _ => null
    };

    private void LockToSoiWithResolver()
    {
        _interaction.GetActiveProcess = () => Soi("Demo");
        _interaction.GetProcessAgnosticPdkNames = () => Array.Empty<string>();
        _interaction.ResolveComponentPdkSource = ResolveByNazcaFunction;
    }

    [Fact]
    public void PlaceGroupTemplate_WithForeignProcessChild_BlocksPlacementAndReportsStatus()
    {
        LockToSoiWithResolver();
        SelectGroupTemplate("InP Mixer", "member_func", "foreign_func");

        string? status = null;
        _interaction.UpdateStatus = s => status = s;

        _interaction.CanvasClicked(500, 500);

        _canvas.Components.Count.ShouldBe(0, "a group with a foreign-process child must not be placed");
        _commandManager.CanUndo.ShouldBeFalse("a blocked placement must not touch the undo stack");
        status.ShouldNotBeNull();
        status!.ShouldContain("HHI-InP");
        status.ShouldContain("SOI 220");
    }

    [Fact]
    public void PlaceGroupTemplate_AllChildrenFromMemberPdk_IsPlaced()
    {
        LockToSoiWithResolver();
        SelectGroupTemplate("SOI Pair", "member_func", "member_func");

        _interaction.CanvasClicked(500, 500);

        _canvas.Components.Count.ShouldBe(1, "a group whose children are all member-PDK must place normally");
    }

    [Fact]
    public void PlaceGroupTemplate_NoResolverWired_IsPlaced()
    {
        // Without a resolver, child sources resolve to null (built-in) — legacy behavior.
        _interaction.GetActiveProcess = () => Soi("Demo");
        _interaction.GetProcessAgnosticPdkNames = () => Array.Empty<string>();
        SelectGroupTemplate("Unknown Group", "foreign_func");

        _interaction.CanvasClicked(500, 500);

        _canvas.Components.Count.ShouldBe(1);
    }

    [Fact]
    public void PlaceGroupTemplate_NoActiveProcess_AllowsForeignChildren()
    {
        _interaction.ResolveComponentPdkSource = ResolveByNazcaFunction;
        SelectGroupTemplate("Playground Group", "foreign_func");

        _interaction.CanvasClicked(500, 500);

        _canvas.Components.Count.ShouldBe(1, "with no active process, any group is placeable");
    }

    [Fact]
    public void PasteSelected_CopiedGroupWithForeignChild_BlocksWholePaste()
    {
        // Place the mixed group directly (bypassing the placement guard under test), copy it.
        var group = BuildGroup("Mixed", "member_func", "foreign_func");
        var vm = _canvas.AddComponent(group);
        _canvas.Selection.SelectSingle(vm);
        _interaction.CopySelectedCommand.Execute(null);

        LockToSoiWithResolver();
        string? status = null;
        _interaction.UpdateStatus = s => status = s;

        int countBefore = _canvas.Components.Count;
        _interaction.PasteSelected();

        _canvas.Components.Count.ShouldBe(countBefore, "pasting a group with a foreign-process child must be blocked");
        status.ShouldNotBeNull();
        status!.ShouldContain("SOI 220");
    }

    [Fact]
    public void PasteSelected_CopiedGroupWithMemberChildrenOnly_Pastes()
    {
        var group = BuildGroup("Members", "member_func", "member_func");
        var vm = _canvas.AddComponent(group);
        _canvas.Selection.SelectSingle(vm);
        _interaction.CopySelectedCommand.Execute(null);

        LockToSoiWithResolver();

        int countBefore = _canvas.Components.Count;
        _interaction.PasteSelected();

        _canvas.Components.Count.ShouldBe(countBefore + 1, "a member-only group must paste normally");
    }

    [Fact]
    public void PlaceGroupTemplate_ForeignChildInNestedGroup_IsAlsoBlocked()
    {
        LockToSoiWithResolver();

        var outer = BuildGroup("Outer", "member_func");
        var nested = BuildGroup("Nested", "foreign_func");
        outer.AddChild(nested);
        SelectBuiltGroup(outer, "Outer Template");

        _interaction.CanvasClicked(500, 500);

        _canvas.Components.Count.ShouldBe(0, "foreign children hidden inside nested groups must be found");
    }

    private void SelectGroupTemplate(string name, params string[] childNazcaFunctions)
    {
        var group = BuildGroup(name, childNazcaFunctions);
        SelectBuiltGroup(group, name);
    }

    private void SelectBuiltGroup(ComponentGroup group, string templateName)
    {
        var template = _libraryManager.SaveTemplate(group, templateName);
        template.TemplateGroup = group;
        _interaction.SelectedGroupTemplate = template;
    }

    private static ComponentGroup BuildGroup(string name, params string[] childNazcaFunctions)
    {
        var group = new ComponentGroup(name) { PhysicalX = 0, PhysicalY = 0 };

        for (int i = 0; i < childNazcaFunctions.Length; i++)
        {
            var child = new Component(
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
            };
            group.AddChild(child);
        }

        return group;
    }
}
