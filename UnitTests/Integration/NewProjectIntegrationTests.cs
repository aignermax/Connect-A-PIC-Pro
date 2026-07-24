using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP_Core.Components.Core;
using CAP_Core.Components.Creation;
using CAP_DataAccess.Components.ComponentDraftMapper;
using Moq;
using Shouldly;
using UnitTests.Helpers;

namespace UnitTests.Integration;

/// <summary>
/// Integration tests for NewProject workflow across ViewModels.
/// Tests the complete flow from MainViewModel through FileOperationsViewModel.
/// Uses an isolated temp preferences file so saves in these tests cannot
/// pollute the developer's real settings (e.g. the recent-projects list).
/// </summary>
public class NewProjectIntegrationTests : IDisposable
{
    private readonly SimulationService _simulationService;
    private readonly CommandManager _commandManager;
    private readonly UserPreferencesService _preferencesService;
    private readonly GroupLibraryManager _groupLibraryManager;
    private readonly string _testPreferencesPath;

    public NewProjectIntegrationTests()
    {
        _simulationService = new SimulationService();
        _commandManager = new CommandManager();
        _testPreferencesPath = Path.Combine(Path.GetTempPath(), $"test-newproject-prefs-{Guid.NewGuid()}.json");
        _preferencesService = new UserPreferencesService(_testPreferencesPath);
        _groupLibraryManager = new GroupLibraryManager();
    }

    public void Dispose()
    {
        if (File.Exists(_testPreferencesPath))
        {
            File.Delete(_testPreferencesPath);
        }
    }

    private MainViewModel CreateMainViewModel() =>
        MainViewModelTestHelper.CreateMainViewModel(
            simulationService: _simulationService,
            commandManager: _commandManager,
            preferencesService: _preferencesService,
            libraryManager: _groupLibraryManager);

    [Fact]
    public async Task MainViewModel_NewProject_ClearsCanvas()
    {
        var mainVm = CreateMainViewModel();

        // Add a component to the canvas
        var component = TestComponentFactory.CreateStraightWaveGuide();
        mainVm.Canvas.AddComponent(component, "TestTemplate");

        // Mark as saved
        mainVm.FileOperations.HasUnsavedChanges = false;

        // Execute NewProject
        await mainVm.NewProjectCommand.ExecuteAsync(null);

        // Verify canvas is cleared
        mainVm.Canvas.Components.Count.ShouldBe(0);
        mainVm.Canvas.Connections.Count.ShouldBe(0);
        mainVm.FileOperations.HasUnsavedChanges.ShouldBeFalse();
    }

    [Fact]
    public async Task MainViewModel_NewProject_PromptsWhenUnsaved()
    {
        var mainVm = CreateMainViewModel();

        var mockMessageBox = new Mock<IMessageBoxService>();
        mockMessageBox
            .Setup(m => m.ShowSavePromptAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(SavePromptResult.DontSave);

        mainVm.FileOperations.MessageBoxService = mockMessageBox.Object;

        // Add component and ensure it's marked as unsaved
        var component = TestComponentFactory.CreateStraightWaveGuide();
        mainVm.Canvas.AddComponent(component, "TestTemplate");
        mainVm.FileOperations.HasUnsavedChanges.ShouldBeTrue();

        // Execute NewProject
        await mainVm.NewProjectCommand.ExecuteAsync(null);

        // Verify dialog was shown
        mockMessageBox.Verify(
            m => m.ShowSavePromptAsync(
                It.Is<string>(s => s.Contains("save")),
                It.IsAny<string>()),
            Times.Once);

        // Canvas should be cleared
        mainVm.Canvas.Components.Count.ShouldBe(0);
    }

    [Fact]
    public async Task NewProject_PreservesCommandHistory_AfterClear()
    {
        var mainVm = CreateMainViewModel();

        // Add component
        var component = TestComponentFactory.CreateStraightWaveGuide();
        mainVm.Canvas.AddComponent(component, "TestTemplate");

        // Mark as saved
        mainVm.FileOperations.HasUnsavedChanges = false;

        // Execute NewProject
        await mainVm.NewProjectCommand.ExecuteAsync(null);

        // Command history should be cleared
        mainVm.CommandManager.CanUndo.ShouldBeFalse();
        mainVm.CommandManager.CanRedo.ShouldBeFalse();
    }

    [Fact]
    public async Task NewProject_ClearsConnections()
    {
        var mainVm = CreateMainViewModel();

        // Add two components with physical pins
        var comp1 = TestComponentFactory.CreateStraightWaveGuide();
        var comp2 = TestComponentFactory.CreateStraightWaveGuide();

        var vm1 = mainVm.Canvas.AddComponent(comp1, "Template1");
        var vm2 = mainVm.Canvas.AddComponent(comp2, "Template2");

        vm1.X = 0;
        vm1.Y = 0;
        vm2.X = 200;
        vm2.Y = 0;

        // Try to connect the components
        if (comp1.PhysicalPins.Count > 0 && comp2.PhysicalPins.Count > 0)
        {
            mainVm.Canvas.ConnectPins(comp1.PhysicalPins[0], comp2.PhysicalPins[0]);
        }

        // Mark as saved
        mainVm.FileOperations.HasUnsavedChanges = false;

        // Execute NewProject
        await mainVm.NewProjectCommand.ExecuteAsync(null);

        // Verify connections are cleared
        mainVm.Canvas.Connections.Count.ShouldBe(0);
    }

    [Fact]
    public async Task NewProject_ExitsGroupEditMode()
    {
        var mainVm = CreateMainViewModel();

        // Create a group with some components
        var comp1 = TestComponentFactory.CreateStraightWaveGuide();
        var comp2 = TestComponentFactory.CreateStraightWaveGuide();
        comp1.PhysicalX = 0;
        comp1.PhysicalY = 0;
        comp2.PhysicalX = 100;
        comp2.PhysicalY = 0;

        var group = new ComponentGroup("TestGroup");
        group.AddChild(comp1);
        group.AddChild(comp2);

        mainVm.Canvas.AddComponent(group);

        // Enter group edit mode
        mainVm.Canvas.EnterGroupEditMode(group);
        mainVm.Canvas.IsInGroupEditMode.ShouldBeTrue();

        // Mark as saved
        mainVm.FileOperations.HasUnsavedChanges = false;

        // Execute NewProject
        await mainVm.NewProjectCommand.ExecuteAsync(null);

        // Verify group edit mode is exited
        mainVm.Canvas.IsInGroupEditMode.ShouldBeFalse();
        mainVm.Canvas.CurrentEditGroup.ShouldBeNull();

        // Verify canvas is cleared
        mainVm.Canvas.Components.Count.ShouldBe(0);
        mainVm.Canvas.Connections.Count.ShouldBe(0);
    }

    [Fact]
    public async Task NewProject_ClearsCurrentFilePath_SaveOpensDialogInsteadOfOverwriting()
    {
        var mainVm = CreateMainViewModel();

        var tempFile = Path.Combine(Path.GetTempPath(), $"test_cap_{Guid.NewGuid()}.lun");

        try
        {
            var mockFileDialog = new Mock<IFileDialogService>();
            mockFileDialog
                .SetupSequence(d => d.ShowSaveFileDialogAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(tempFile)   // First call: initial save
                .ReturnsAsync(tempFile);  // Second call: save after NewProject (must open dialog)

            mainVm.FileOperations.FileDialogService = mockFileDialog.Object;
            mainVm.FileOperations.HasUnsavedChanges = false;

            // Step 1: Save the design — sets _currentFilePath
            await mainVm.FileOperations.SaveDesignCommand.ExecuteAsync(null);
            mockFileDialog.Verify(
                d => d.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
                Times.Once,
                "Expected file dialog to open on first save");

            // Step 2: Click New — must clear _currentFilePath
            await mainVm.NewProjectCommand.ExecuteAsync(null);

            mainVm.Canvas.Components.Count.ShouldBe(0);
            mainVm.FileOperations.HasUnsavedChanges.ShouldBeFalse();

            // Step 3: Save again — dialog MUST open (not silently overwrite the old file)
            await mainVm.FileOperations.SaveDesignCommand.ExecuteAsync(null);
            mockFileDialog.Verify(
                d => d.ShowSaveFileDialogAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
                Times.Exactly(2),
                "Expected file dialog to open again after NewProject (file path must have been cleared)");
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task NewProject_ExitsNestedGroupEditMode()
    {
        var mainVm = CreateMainViewModel();

        // Create nested groups
        var comp = TestComponentFactory.CreateStraightWaveGuide();
        var childGroup = new ComponentGroup("ChildGroup");
        childGroup.AddChild(comp);

        var parentGroup = new ComponentGroup("ParentGroup");
        parentGroup.AddChild(childGroup);

        mainVm.Canvas.AddComponent(parentGroup);

        // Enter nested group edit mode
        mainVm.Canvas.EnterGroupEditMode(parentGroup);
        mainVm.Canvas.EnterGroupEditMode(childGroup);
        mainVm.Canvas.IsInGroupEditMode.ShouldBeTrue();

        // Mark as saved
        mainVm.FileOperations.HasUnsavedChanges = false;

        // Execute NewProject
        await mainVm.NewProjectCommand.ExecuteAsync(null);

        // Verify all group edit modes are exited
        mainVm.Canvas.IsInGroupEditMode.ShouldBeFalse();
        mainVm.Canvas.CurrentEditGroup.ShouldBeNull();
        mainVm.Canvas.BreadcrumbPath.Count.ShouldBe(0);

        // Verify canvas is cleared
        mainVm.Canvas.Components.Count.ShouldBe(0);
    }
}
