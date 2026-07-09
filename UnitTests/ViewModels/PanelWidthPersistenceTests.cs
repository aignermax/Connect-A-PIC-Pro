using CAP.Avalonia.ViewModels.Panels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Analysis;
using CAP.Avalonia.ViewModels.Analysis.OnaAnalysis;
using CAP.Avalonia.ViewModels.Diagnostics;
using CAP.Avalonia.ViewModels.Hierarchy;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.AI;
using CAP.Avalonia.ViewModels.Properties;
using CAP.Avalonia.ViewModels.Properties.Editors;
using CAP.Avalonia.ViewModels.Export;
using CAP_Core.Export;
using Moq;
using CAP.Avalonia.Services;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_Core.Components.Creation;
using Shouldly;

namespace UnitTests.ViewModels;

/// <summary>
/// Unit tests for panel width persistence across left and right panels.
/// Tests that panel widths are saved and restored correctly.
/// Uses isolated test preferences to avoid polluting user settings.
/// </summary>
public class PanelWidthPersistenceTests : IDisposable
{
    private readonly DesignCanvasViewModel _canvas;
    private readonly GroupLibraryManager _libraryManager;
    private readonly PdkLoader _pdkLoader;
    private readonly UserPreferencesService _preferencesService;
    private readonly string _testPreferencesPath;

    public PanelWidthPersistenceTests()
    {
        _canvas = new DesignCanvasViewModel();
        _libraryManager = new GroupLibraryManager();
        _pdkLoader = new PdkLoader();

        // Use temporary file for test isolation
        _testPreferencesPath = Path.Combine(Path.GetTempPath(), $"test-preferences-{Guid.NewGuid()}.json");
        _preferencesService = new UserPreferencesService(_testPreferencesPath);
    }

    public void Dispose()
    {
        // Clean up test preferences file
        if (File.Exists(_testPreferencesPath))
        {
            File.Delete(_testPreferencesPath);
        }
    }

    /// <summary>Creates a LeftPanelViewModel with all required sub-VM dependencies.</summary>
    private LeftPanelViewModel CreateLeftPanelViewModel() =>
        new(_canvas, _libraryManager, _pdkLoader, _preferencesService,
            new HierarchyPanelViewModel(_canvas),
            new PdkManagerViewModel(),
            new ComponentLibraryViewModel(_libraryManager));

    /// <summary>Creates a RightPanelViewModel with all required sub-VM dependencies.</summary>
    private RightPanelViewModel CreateRightPanelViewModel() =>
        new(_canvas, _preferencesService,
            new ParameterSweepViewModel(),
            new RoutingDiagnosticsViewModel(),
            new DesignValidationViewModel(),
            new ComponentDimensionDiagnosticsViewModel(_canvas),
            new ComponentDimensionViewModel(),
            new ExportValidationViewModel(),
            new SMatrixPerformanceViewModel(),
            new CompressLayoutViewModel(),
            new GroupSMatrixViewModel(),
            new ArchitectureReportViewModel(),
            new PdkConsistencyViewModel(),
            new AiAssistantViewModel(Mock.Of<IAiService>(), _preferencesService),
            new OnaSweepViewModel(),
            new ComponentEditorFactory(new IComponentEditorProvider[]
            {
                new GenericComponentEditorProvider()
            }));

    [Fact]
    public void LeftPanelWidth_DefaultsTo220()
    {
        var vm = CreateLeftPanelViewModel();

        vm.LeftPanelWidth.Value.ShouldBe(220);
    }

    [Fact]
    public void RightPanelWidth_DefaultsTo250()
    {
        var vm = CreateRightPanelViewModel();

        vm.RightPanelWidth.Value.ShouldBe(250);
    }

    [Fact]
    public void LeftPanelWidth_ClampsToMinimum200()
    {
        var vm = CreateLeftPanelViewModel();

        vm.LeftPanelWidth = new Avalonia.Controls.GridLength(50); // Below minimum

        vm.LeftPanelWidth.Value.ShouldBe(200);
    }

    [Fact]
    public void LeftPanelWidth_ClampsToMaximum800()
    {
        var vm = CreateLeftPanelViewModel();

        vm.LeftPanelWidth = new Avalonia.Controls.GridLength(1000); // Above maximum

        vm.LeftPanelWidth.Value.ShouldBe(800);
    }

    [Fact]
    public void RightPanelWidth_ClampsToMinimum200()
    {
        var vm = CreateRightPanelViewModel();

        vm.RightPanelWidth = new Avalonia.Controls.GridLength(100); // Below minimum

        vm.RightPanelWidth.Value.ShouldBe(200);
    }

    [Fact]
    public void RightPanelWidth_ClampsToMaximum800()
    {
        var vm = CreateRightPanelViewModel();

        vm.RightPanelWidth = new Avalonia.Controls.GridLength(1000); // Above maximum

        vm.RightPanelWidth.Value.ShouldBe(800);
    }

    [Fact]
    public void LeftPanelWidth_PersistsToPreferences()
    {
        var vm = CreateLeftPanelViewModel();

        vm.LeftPanelWidth = new Avalonia.Controls.GridLength(350);

        _preferencesService.GetLeftPanelWidth().ShouldBe(350);
    }

    [Fact]
    public void RightPanelWidth_PersistsToPreferences()
    {
        var vm = CreateRightPanelViewModel();

        vm.RightPanelWidth = new Avalonia.Controls.GridLength(400);

        _preferencesService.GetRightPanelWidth().ShouldBe(400);
    }

    [Fact]
    public void LeftPanelWidth_RestoresFromPreferences()
    {
        // Set a custom width
        _preferencesService.SetLeftPanelWidth(300);

        // Create new ViewModel and initialize
        var vm = CreateLeftPanelViewModel();
        vm.Initialize();

        vm.LeftPanelWidth.Value.ShouldBe(300);
    }

    [Fact]
    public void RightPanelWidth_RestoresFromPreferences()
    {
        // Set a custom width
        _preferencesService.SetRightPanelWidth(450);

        // Create new ViewModel and initialize
        var vm = CreateRightPanelViewModel();
        vm.Initialize();

        vm.RightPanelWidth.Value.ShouldBe(450);
    }

    [Fact]
    public void LeftPanelWidth_RaisesPropertyChanged()
    {
        var vm = CreateLeftPanelViewModel();
        bool propertyChanged = false;

        vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(vm.LeftPanelWidth))
                propertyChanged = true;
        };

        vm.LeftPanelWidth = new Avalonia.Controls.GridLength(400);

        propertyChanged.ShouldBeTrue();
    }

    [Fact]
    public void RightPanelWidth_RaisesPropertyChanged()
    {
        var vm = CreateRightPanelViewModel();
        bool propertyChanged = false;

        vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(vm.RightPanelWidth))
                propertyChanged = true;
        };

        vm.RightPanelWidth = new Avalonia.Controls.GridLength(350);

        propertyChanged.ShouldBeTrue();
    }

    [Fact]
    public void BothPanels_CanHaveIndependentWidths()
    {
        var leftVm = CreateLeftPanelViewModel();
        var rightVm = CreateRightPanelViewModel();

        leftVm.LeftPanelWidth = new Avalonia.Controls.GridLength(300);
        rightVm.RightPanelWidth = new Avalonia.Controls.GridLength(500);

        leftVm.LeftPanelWidth.Value.ShouldBe(300);
        rightVm.RightPanelWidth.Value.ShouldBe(500);
        _preferencesService.GetLeftPanelWidth().ShouldBe(300);
        _preferencesService.GetRightPanelWidth().ShouldBe(500);
    }

    [Fact]
    public void LeftPanelWidth_AcceptsValidWidthInRange()
    {
        var vm = CreateLeftPanelViewModel();

        vm.LeftPanelWidth = new Avalonia.Controls.GridLength(350);

        vm.LeftPanelWidth.Value.ShouldBe(350);
    }

    [Fact]
    public void RightPanelWidth_AcceptsValidWidthInRange()
    {
        var vm = CreateRightPanelViewModel();

        vm.RightPanelWidth = new Avalonia.Controls.GridLength(400);

        vm.RightPanelWidth.Value.ShouldBe(400);
    }
}
