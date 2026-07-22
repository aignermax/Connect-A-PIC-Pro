using CAP.Avalonia.Commands;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Components.Creation;
using CAP_Core.LightCalculation;
using Shouldly;
using Xunit;

namespace UnitTests.ViewModels.Panels;

/// <summary>
/// Field-crash repro (round-4 hotfix): placing a saved group whose frozen child
/// S-matrix is non-passive (e.g. stale FDTD/override data serialized into the
/// template) made <see cref="SingleHopPassivityChecker.ThrowIfNonPassive"/> throw
/// straight through <c>GroupLibraryManager.InstantiateTemplate</c> →
/// <c>PlaceGroupTemplateCommand</c> → <c>CanvasInteractionViewModel.CanvasClicked</c>
/// into the Avalonia dispatcher — killing the whole app. A physics guard must
/// never crash the app: the placement action aborts cleanly, nothing is placed,
/// and the guard's message lands in the Error Console.
/// </summary>
public class GroupPlacementPassivityGuardTests : IDisposable
{
    /// <summary>Wavelength (nm) at which the test child's S-matrix fabricates energy.</summary>
    private const int NonPassiveWavelengthNm = 1546;

    /// <summary>Through-amplitude of the non-passive child: σ_max = 1.1 &gt; 1.005 band.</summary>
    private const double NonPassiveAmplitude = 1.1;

    private readonly string _testLibraryPath;
    private readonly GroupLibraryManager _libraryManager;
    private readonly DesignCanvasViewModel _canvas;
    private readonly CommandManager _commandManager;
    private readonly ErrorConsoleService _errorConsole;
    private readonly CanvasInteractionViewModel _interaction;

    public GroupPlacementPassivityGuardTests()
    {
        _testLibraryPath = Path.Combine(
            Path.GetTempPath(), $"GroupPassivityGuardTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testLibraryPath);

        _libraryManager = new GroupLibraryManager(_testLibraryPath);
        _canvas = new DesignCanvasViewModel();
        _commandManager = new CommandManager();
        _errorConsole = new ErrorConsoleService();
        _interaction = new CanvasInteractionViewModel(
            _canvas, _commandManager, new ComponentLibraryViewModel(_libraryManager),
            errorConsole: _errorConsole);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testLibraryPath))
            Directory.Delete(_testLibraryPath, true);
    }

    [Fact]
    public void TryCreate_TemplateWithNonPassiveChild_ReturnsNullWithStructuredRejection()
    {
        var template = SaveNonPassiveTemplate("Bad Physics Group");

        PlaceGroupTemplateCommand? cmd = null;
        NonConvergentCircuitException? rejection = null;
        Should.NotThrow(() => cmd = PlaceGroupTemplateCommand.TryCreate(
            _canvas, _libraryManager, template, 500, 500, out rejection));

        cmd.ShouldBeNull("a non-passive template must not produce a placement command");
        rejection.ShouldNotBeNull();
        rejection!.Kind.ShouldBe(NonConvergentCircuitKind.NonPassiveComponent);
        rejection.ComponentName.ShouldNotBeNull();
        rejection.WavelengthNm.ShouldBe(NonPassiveWavelengthNm);
    }

    [Fact]
    public void CanvasClicked_PlacingNonPassiveGroup_DoesNotThrowAndAbortsCleanly()
    {
        _interaction.SelectedGroupTemplate = SaveNonPassiveTemplate("Crash Repro Group");
        string? status = null;
        _interaction.UpdateStatus = s => status = s;

        // The exact dispatcher entry point of the field crash — must never throw.
        Should.NotThrow(() => _interaction.CanvasClicked(500, 500));

        _canvas.Components.Count.ShouldBe(0, "the aborted placement must not leave a half-placed group");
        _canvas.AllPins.Count.ShouldBe(0, "no pins of the rejected group may remain on the canvas");
        _commandManager.CanUndo.ShouldBeFalse("an aborted placement must not touch the undo stack");
        status.ShouldNotBeNull("the user must see why nothing was placed");

        _errorConsole.Entries.ShouldNotBeEmpty("the passivity guard message must reach the Error Console");
        _errorConsole.Entries[^1].Message.ShouldContain("NonPassive Child");
    }

    [Fact]
    public void CanvasClicked_PlacingPassiveGroup_StillPlacesNormally()
    {
        _interaction.SelectedGroupTemplate = SaveTemplate("Good Group", throughAmplitude: 0.9);

        _interaction.CanvasClicked(500, 500);

        _canvas.Components.Count.ShouldBe(1, "a passive group must place exactly as before the guard");
        _commandManager.CanUndo.ShouldBeTrue();
        _errorConsole.Entries.ShouldBeEmpty();
    }

    private GroupTemplate SaveNonPassiveTemplate(string name) =>
        SaveTemplate(name, NonPassiveAmplitude);

    /// <summary>
    /// Builds a one-child group whose child carries a straight-through S-matrix with the
    /// given amplitude at <see cref="NonPassiveWavelengthNm"/> (mimicking stale frozen
    /// template data), exposes both child pins as group pins, saves it as a template and
    /// returns the template ready for placement.
    /// </summary>
    private GroupTemplate SaveTemplate(string name, double throughAmplitude)
    {
        var child = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        child.HumanReadableName = "NonPassive Child";
        child.WidthMicrometers = 50;
        child.HeightMicrometers = 10;

        var leftPin = child.Parts[0, 0].GetPinAt(CAP_Core.Tiles.RectSide.Left);
        var rightPin = child.Parts[0, 0].GetPinAt(CAP_Core.Tiles.RectSide.Right);
        var allPinIds = new List<Guid>
        {
            leftPin.IDInFlow, leftPin.IDOutFlow, rightPin.IDInFlow, rightPin.IDOutFlow
        };
        var matrix = new SMatrix(allPinIds, new());
        matrix.SetValues(new()
        {
            { (leftPin.IDInFlow, rightPin.IDOutFlow), throughAmplitude },
            { (rightPin.IDInFlow, leftPin.IDOutFlow), throughAmplitude },
        });
        child.WaveLengthToSMatrixMap.Clear();
        child.WaveLengthToSMatrixMap[NonPassiveWavelengthNm] = matrix;

        var group = new ComponentGroup(name)
        {
            PhysicalX = 0,
            PhysicalY = 0,
            WidthMicrometers = 50,
            HeightMicrometers = 10
        };
        group.AddChild(child);
        group.AddExternalPin(new GroupPin
        {
            Name = "GroupIn",
            InternalPin = child.PhysicalPins[0],
            RelativeX = 0,
            RelativeY = 5,
            AngleDegrees = 180
        });
        group.AddExternalPin(new GroupPin
        {
            Name = "GroupOut",
            InternalPin = child.PhysicalPins[1],
            RelativeX = 50,
            RelativeY = 5,
            AngleDegrees = 0
        });

        var template = _libraryManager.SaveTemplate(group, name);
        template.TemplateGroup = group;
        return template;
    }
}
