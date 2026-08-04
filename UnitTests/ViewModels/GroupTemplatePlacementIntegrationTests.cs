using CAP.Avalonia.Commands;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Components.Core;
using CAP_Core.Components.Creation;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using Shouldly;
using Xunit;

namespace UnitTests.ViewModels;

/// <summary>
/// Integration tests for the group template placement workflow.
/// Tests the complete flow: selection in library → placement on canvas → undo/redo.
/// </summary>
public class GroupTemplatePlacementIntegrationTests : IDisposable
{
    private readonly string _testLibraryPath;
    private readonly GroupLibraryManager _libraryManager;
    private readonly DesignCanvasViewModel _canvas;
    private readonly ComponentLibraryViewModel _libraryViewModel;
    private readonly CanvasInteractionViewModel _canvasInteraction;
    private readonly CommandManager _commandManager;
    private readonly LeftPanelViewModel _leftPanel;

    public GroupTemplatePlacementIntegrationTests()
    {
        _testLibraryPath = Path.Combine(Path.GetTempPath(), $"PlacementIntegrationTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testLibraryPath);

        _libraryManager = new GroupLibraryManager(_testLibraryPath);
        _canvas = new DesignCanvasViewModel();
        _libraryViewModel = new ComponentLibraryViewModel(_libraryManager);
        _commandManager = new CommandManager();
        _canvasInteraction = new CanvasInteractionViewModel(_canvas, _commandManager, _libraryViewModel);
        _leftPanel = new LeftPanelViewModel(
            _canvas,
            _libraryManager,
            new CAP_DataAccess.Components.ComponentDraftMapper.PdkLoader(),
            new CAP.Avalonia.Services.UserPreferencesService(),
            new CAP.Avalonia.ViewModels.Hierarchy.HierarchyPanelViewModel(_canvas),
            new CAP.Avalonia.ViewModels.Library.PdkManagerViewModel(),
            new CAP.Avalonia.ViewModels.Library.ComponentLibraryViewModel(_libraryManager));

        // Wire up the callback (simulating MainViewModel wiring)
        _leftPanel.OnGroupTemplateSelected = template =>
        {
            _canvasInteraction.SelectedGroupTemplate = template;
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_testLibraryPath))
        {
            Directory.Delete(_testLibraryPath, true);
        }
    }

    [Fact]
    public void SelectGroupTemplate_SwitchesToPlaceGroupMode()
    {
        // Arrange
        var group = CreateTestGroup("TestGroup", 2);
        var template = _libraryManager.SaveTemplate(group, "Test Template");
        template.TemplateGroup = group;

        // Act
        _canvasInteraction.SelectedGroupTemplate = template;

        // Assert
        _canvasInteraction.CurrentMode.ShouldBe(InteractionMode.PlaceGroupTemplate);
        _canvasInteraction.SelectedGroupTemplate.ShouldBe(template);
    }

    [Fact]
    public void SelectGroupTemplate_DeselectsComponentTemplate()
    {
        // Arrange
        var componentTemplate = new ComponentTemplate { Name = "Component" };
        _canvasInteraction.SelectedTemplate = componentTemplate;

        var group = CreateTestGroup("TestGroup", 2);
        var groupTemplate = _libraryManager.SaveTemplate(group, "Group Template");
        groupTemplate.TemplateGroup = group;

        // Act
        _canvasInteraction.SelectedGroupTemplate = groupTemplate;

        // Assert
        _canvasInteraction.SelectedTemplate.ShouldBeNull();
        _canvasInteraction.SelectedGroupTemplate.ShouldBe(groupTemplate);
    }

    [Fact]
    public void SelectComponentTemplate_DeselectsGroupTemplate()
    {
        // Arrange
        var group = CreateTestGroup("TestGroup", 2);
        var groupTemplate = _libraryManager.SaveTemplate(group, "Group Template");
        groupTemplate.TemplateGroup = group;
        _canvasInteraction.SelectedGroupTemplate = groupTemplate;

        var componentTemplate = new ComponentTemplate { Name = "Component" };

        // Act
        _canvasInteraction.SelectedTemplate = componentTemplate;

        // Assert
        _canvasInteraction.SelectedGroupTemplate.ShouldBeNull();
        _canvasInteraction.SelectedTemplate.ShouldBe(componentTemplate);
    }

    [Fact]
    public void PlaceGroupTemplate_AddsGroupToCanvas()
    {
        // Arrange
        var group = CreateTestGroup("TestGroup", 3);
        var template = _libraryManager.SaveTemplate(group, "Test Template");
        template.TemplateGroup = group;
        _canvasInteraction.SelectedGroupTemplate = template;

        // Act
        _canvasInteraction.CanvasClicked(500, 500);

        // Assert
        _canvas.Components.Count.ShouldBe(1);

        var placedGroupVm = _canvas.Components[0];
        placedGroupVm.Component.ShouldBeOfType<ComponentGroup>();

        var placedGroup = (ComponentGroup)placedGroupVm.Component;
        placedGroup.ChildComponents.Count.ShouldBe(3);
        placedGroup.IsPrefab.ShouldBeFalse(); // Instance, not prefab
    }

    [Fact]
    public void UndoPlaceGroup_RemovesGroupFromCanvas()
    {
        // Arrange
        var group = CreateTestGroup("TestGroup", 2);
        var template = _libraryManager.SaveTemplate(group, "Test Template");
        template.TemplateGroup = group;
        _canvasInteraction.SelectedGroupTemplate = template;
        _canvasInteraction.CanvasClicked(500, 500);

        _canvas.Components.Count.ShouldBe(1);

        // Act
        _commandManager.Undo();

        // Assert
        _canvas.Components.Count.ShouldBe(0);
    }

    [Fact]
    public void RedoPlaceGroup_RestoresGroupToCanvas()
    {
        // Arrange
        var group = CreateTestGroup("TestGroup", 2);
        var template = _libraryManager.SaveTemplate(group, "Test Template");
        template.TemplateGroup = group;
        _canvasInteraction.SelectedGroupTemplate = template;
        _canvasInteraction.CanvasClicked(500, 500);
        _commandManager.Undo();

        // Act
        _commandManager.Redo();

        // Assert
        _canvas.Components.Count.ShouldBe(1);
        ((ComponentGroup)_canvas.Components[0].Component).ChildComponents.Count.ShouldBe(2);
    }

    [Fact]
    public void PlaceMultipleInstances_CreatesIndependentCopies()
    {
        // Arrange
        var group = CreateTestGroup("TestGroup", 2);
        var template = _libraryManager.SaveTemplate(group, "Test Template");
        template.TemplateGroup = group;
        _canvasInteraction.SelectedGroupTemplate = template;

        // Act - Place two instances
        _canvasInteraction.CanvasClicked(500, 500);
        _canvasInteraction.CanvasClicked(1000, 1000);

        // Assert
        _canvas.Components.Count.ShouldBe(2);

        var group1 = (ComponentGroup)_canvas.Components[0].Component;
        var group2 = (ComponentGroup)_canvas.Components[1].Component;

        // Verify they have different IDs (independent copies)
        group1.Identifier.ShouldNotBe(group2.Identifier);
        group1.ChildComponents[0].Identifier.ShouldNotBe(group2.ChildComponents[0].Identifier);
    }

    [Fact]
    public void SetSelectMode_ClearsGroupTemplateSelection()
    {
        // Arrange
        var group = CreateTestGroup("TestGroup", 2);
        var template = _libraryManager.SaveTemplate(group, "Test Template");
        template.TemplateGroup = group;
        _canvasInteraction.SelectedGroupTemplate = template;

        // Act
        _canvasInteraction.SetSelectModeCommand.Execute(null);

        // Assert
        _canvasInteraction.SelectedGroupTemplate.ShouldBeNull();
        _canvasInteraction.CurrentMode.ShouldBe(InteractionMode.Select);
    }

    [Fact]
    public void LeftPanel_SelectGroupTemplate_NotifiesCanvasInteraction()
    {
        // Arrange
        var group = CreateTestGroup("TestGroup", 2);
        var template = _libraryManager.SaveTemplate(group, "Test Template");
        template.TemplateGroup = group;

        GroupTemplate? selectedTemplate = null;
        _leftPanel.OnGroupTemplateSelected = t => selectedTemplate = t;

        // Act
        _leftPanel.SelectedGroupTemplate = template;

        // Assert
        selectedTemplate.ShouldBe(template);
    }

    [Fact]
    public void PlaceGroupWithInternalPaths_PreservesWaveguideConnections()
    {
        // Arrange
        var group = CreateTestGroupWithConnection("ConnectedGroup");
        var template = _libraryManager.SaveTemplate(group, "Connected Template");
        template.TemplateGroup = group;
        _canvasInteraction.SelectedGroupTemplate = template;

        // Act
        _canvasInteraction.CanvasClicked(500, 500);

        // Assert
        var placedGroup = (ComponentGroup)_canvas.Components[0].Component;
        placedGroup.InternalPaths.Count.ShouldBe(1);

        // Verify the path connects the cloned components, not the originals
        var path = placedGroup.InternalPaths[0];
        path.StartPin!.ParentComponent.ShouldBe(placedGroup.ChildComponents[0]);
        path.EndPin!.ParentComponent.ShouldBe(placedGroup.ChildComponents[1]);
    }

    [Fact]
    public void ClickGroupInLibrary_EntersPlacementMode()
    {
        // Arrange: Create and save a group template (simulating "Save as Prefab")
        var group = CreateTestGroup("MyCircuit", 3);
        var template = _libraryManager.SaveTemplate(group, "My Saved Group");
        template.TemplateGroup = group; // In-memory template (as created by SaveGroupAsPrefabCommand)
        _libraryViewModel.AddTemplate(template);

        // Act: Simulate clicking on the group in the Saved Groups panel
        // This is what happens when ListBox SelectedItem binding changes
        _leftPanel.SelectedGroupTemplate = template;

        // Assert: CanvasInteraction should enter placement mode
        _canvasInteraction.SelectedGroupTemplate.ShouldBe(template);
        _canvasInteraction.CurrentMode.ShouldBe(InteractionMode.PlaceGroupTemplate);

        // Verify placement works
        _canvasInteraction.CanvasClicked(500, 500);
        _canvas.Components.Count.ShouldBe(1);
        var placedGroup = (ComponentGroup)_canvas.Components[0].Component;
        placedGroup.ChildComponents.Count.ShouldBe(3);
    }

    /// <summary>
    /// Creates a test ComponentGroup with the specified number of child components.
    /// </summary>
    private ComponentGroup CreateTestGroup(string name, int childCount)
    {
        var group = new ComponentGroup(name)
        {
            PhysicalX = 0,
            PhysicalY = 0
        };

        for (int i = 0; i < childCount; i++)
        {
            var child = new Component(
                new Dictionary<int, SMatrix>(),
                new List<Slider>(),
                "test_component",
                "",
                new Part[1, 1] { { new Part() } },
                -1,
                $"comp_{i}_{Guid.NewGuid():N}",
                DiscreteRotation.R0,
                new List<PhysicalPin>
                {
                    new PhysicalPin
                    {
                        Name = "a0",
                        OffsetXMicrometers = 0,
                        OffsetYMicrometers = 0,
                        AngleDegrees = 180
                    },
                    new PhysicalPin
                    {
                        Name = "b0",
                        OffsetXMicrometers = 50,
                        OffsetYMicrometers = 0,
                        AngleDegrees = 0
                    }
                })
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

    /// <summary>
    /// Creates a test group with two components connected by a frozen path.
    /// </summary>
    private ComponentGroup CreateTestGroupWithConnection(string name)
    {
        var group = CreateTestGroup(name, 2);

        // Create a frozen path between the two components
        var comp1 = group.ChildComponents[0];
        var comp2 = group.ChildComponents[1];

        var frozenPath = new FrozenWaveguidePath
        {
            PathId = Guid.NewGuid(),
            StartPin = comp1.PhysicalPins[1], // b0
            EndPin = comp2.PhysicalPins[0],   // a0
            Path = new RoutedPath()
        };

        group.AddInternalPath(frozenPath);

        return group;
    }
}
