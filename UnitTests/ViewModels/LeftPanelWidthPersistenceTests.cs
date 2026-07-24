using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Hierarchy;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Components.Creation;
using CAP_DataAccess.Components.ComponentDraftMapper;
using Shouldly;
using Xunit;

namespace UnitTests.ViewModels;

/// <summary>
/// Unit tests for left panel width persistence.
/// Tests that panel width is saved and restored correctly.
/// </summary>
public class LeftPanelWidthPersistenceTests : IDisposable
{
    private readonly string _testPrefsPath;
    private readonly UserPreferencesService _preferencesService;

    public LeftPanelWidthPersistenceTests()
    {
        // Create temp directory for test preferences
        _testPrefsPath = Path.Combine(Path.GetTempPath(), $"LeftPanelPrefs_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testPrefsPath);

        // Create preferences service with an isolated test file path
        var prefsFile = Path.Combine(_testPrefsPath, "user-preferences.json");
        _preferencesService = new UserPreferencesService(prefsFile);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testPrefsPath))
        {
            try
            {
                Directory.Delete(_testPrefsPath, true);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }

    [Fact]
    public void LeftPanelWidth_DefaultsTo220()
    {
        // Arrange & Act
        var width = _preferencesService.GetLeftPanelWidth();

        // Assert
        width.ShouldBe(220);
    }

    [Fact]
    public void SetLeftPanelWidth_SavesAndRestores()
    {
        // Arrange
        var testWidth = 350.0;

        // Act
        _preferencesService.SetLeftPanelWidth(testWidth);
        var restored = _preferencesService.GetLeftPanelWidth();

        // Assert
        restored.ShouldBe(testWidth);
    }

    [Fact]
    public void LeftPanelViewModel_InitializesWithSavedWidth()
    {
        // Arrange
        var testWidth = 400.0;
        _preferencesService.SetLeftPanelWidth(testWidth);

        var canvas = new CAP.Avalonia.ViewModels.Canvas.DesignCanvasViewModel();
        var groupLibrary = new GroupLibraryManager();
        var pdkLoader = new PdkLoader();

        // Act
        var leftPanel = new LeftPanelViewModel(canvas, groupLibrary, pdkLoader, _preferencesService,
            new HierarchyPanelViewModel(canvas), new PdkManagerViewModel(),
            new ComponentLibraryViewModel(groupLibrary));
        leftPanel.Initialize();

        // Assert
        leftPanel.LeftPanelWidth.Value.ShouldBe(testWidth);
    }

    [Fact]
    public void LeftPanelWidth_ClampsToMinimum200()
    {
        // Arrange
        var canvas = new CAP.Avalonia.ViewModels.Canvas.DesignCanvasViewModel();
        var groupLibrary = new GroupLibraryManager();
        var pdkLoader = new PdkLoader();
        var leftPanel = new LeftPanelViewModel(canvas, groupLibrary, pdkLoader, _preferencesService,
            new HierarchyPanelViewModel(canvas), new PdkManagerViewModel(),
            new ComponentLibraryViewModel(groupLibrary));

        // Act - try to set below minimum
        leftPanel.LeftPanelWidth = new Avalonia.Controls.GridLength(100);

        // Assert - should be clamped to minimum
        leftPanel.LeftPanelWidth.Value.ShouldBeGreaterThanOrEqualTo(200);
    }

    [Fact]
    public void LeftPanelWidth_ClampsToMaximum800()
    {
        // Arrange
        var canvas = new CAP.Avalonia.ViewModels.Canvas.DesignCanvasViewModel();
        var groupLibrary = new GroupLibraryManager();
        var pdkLoader = new PdkLoader();
        var leftPanel = new LeftPanelViewModel(canvas, groupLibrary, pdkLoader, _preferencesService,
            new HierarchyPanelViewModel(canvas), new PdkManagerViewModel(),
            new ComponentLibraryViewModel(groupLibrary));

        // Act - try to set above maximum
        leftPanel.LeftPanelWidth = new Avalonia.Controls.GridLength(1000);

        // Assert - should be clamped to maximum
        leftPanel.LeftPanelWidth.Value.ShouldBeLessThanOrEqualTo(800);
    }

    [Fact]
    public void LeftPanelWidth_ChangeTriggersSave()
    {
        // Arrange
        var canvas = new CAP.Avalonia.ViewModels.Canvas.DesignCanvasViewModel();
        var groupLibrary = new GroupLibraryManager();
        var pdkLoader = new PdkLoader();
        var leftPanel = new LeftPanelViewModel(canvas, groupLibrary, pdkLoader, _preferencesService,
            new HierarchyPanelViewModel(canvas), new PdkManagerViewModel(),
            new ComponentLibraryViewModel(groupLibrary));
        leftPanel.Initialize();

        // Act
        leftPanel.LeftPanelWidth = new Avalonia.Controls.GridLength(300);

        // Assert - verify it was saved by creating new service and reading
        var savedWidth = _preferencesService.GetLeftPanelWidth();
        savedWidth.ShouldBe(300);
    }
}
