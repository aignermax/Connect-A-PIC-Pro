using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Core;
using CAP_Core.Components.Creation;
using CAP_Core.Routing;
using Shouldly;
using Xunit;

namespace UnitTests.ViewModels;

/// <summary>
/// Integration tests for ComponentLibraryViewModel.
/// Tests the interaction between Core (GroupLibraryManager) and ViewModel layers.
/// </summary>
public class ComponentLibraryViewModelTests : IDisposable
{
    private readonly string _testLibraryPath;
    private readonly GroupLibraryManager _libraryManager;
    private readonly ComponentLibraryViewModel _viewModel;

    public ComponentLibraryViewModelTests()
    {
        // The library StatusText is localized via LocalizationService.Instance, so pin English
        // to keep these assertions culture-independent regardless of the CI/dev OS language
        // (the only test that live-switches the shared instance is isolated in the
        // "LocalizationSingleton" collection, which disables cross-collection parallelization).
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);

        // Create a temporary directory for testing
        _testLibraryPath = Path.Combine(Path.GetTempPath(), $"ComponentLibraryVmTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testLibraryPath);

        _libraryManager = new GroupLibraryManager(_testLibraryPath);
        _viewModel = new ComponentLibraryViewModel(_libraryManager);
    }

    public void Dispose()
    {
        // Clean up test directory
        if (Directory.Exists(_testLibraryPath))
        {
            Directory.Delete(_testLibraryPath, true);
        }
    }

    [Fact]
    public void LoadGroups_PopulatesUserGroupsCollection()
    {
        // Arrange
        var group1 = CreateTestGroup("Group1", 2);
        var group2 = CreateTestGroup("Group2", 3);
        _libraryManager.SaveTemplate(group1, "User Group 1", null, "User");
        _libraryManager.SaveTemplate(group2, "User Group 2", null, "User");

        // Act
        _viewModel.LoadGroupsCommand.Execute(null);

        // Assert
        _viewModel.UserGroups.Count.ShouldBe(2);
        _viewModel.UserGroups.Select(vm => vm.Template.Name).ShouldContain("User Group 1");
        _viewModel.UserGroups.Select(vm => vm.Template.Name).ShouldContain("User Group 2");
    }

    [Fact]
    public void LoadGroups_PopulatesPdkGroupsCollection()
    {
        // Arrange
        var pdkGroup = CreateTestGroup("PdkGroup", 1);
        _libraryManager.SaveTemplate(pdkGroup, "PDK Macro 1", null, "PDK");

        // Act
        _viewModel.LoadGroupsCommand.Execute(null);

        // Assert
        _viewModel.PdkGroups.Count.ShouldBe(1);
        _viewModel.PdkGroups[0].Template.Name.ShouldBe("PDK Macro 1");
        _viewModel.PdkGroups[0].Template.Source.ShouldBe("PDK");
    }

    [Fact]
    public void RemoveTemplate_RemovesFromUserGroupsCollection()
    {
        // Arrange
        var group = CreateTestGroup("TestGroup", 2);
        var template = _libraryManager.SaveTemplate(group, "Test Group", null, "User");
        _viewModel.LoadGroupsCommand.Execute(null);

        _viewModel.UserGroups.Count.ShouldBe(1);

        // Act
        _viewModel.RemoveTemplateCommand.Execute(template);

        // Assert
        _viewModel.UserGroups.Count.ShouldBe(0);
        _libraryManager.Templates.Count.ShouldBe(0);
    }

    [Fact]
    public void RemoveTemplate_RemovesFromPdkGroupsCollection()
    {
        // Arrange
        var group = CreateTestGroup("PdkGroup", 1);
        var template = _libraryManager.SaveTemplate(group, "PDK Macro", null, "PDK");
        _viewModel.LoadGroupsCommand.Execute(null);

        _viewModel.PdkGroups.Count.ShouldBe(1);

        // Act
        _viewModel.RemoveTemplateCommand.Execute(template);

        // Assert
        _viewModel.PdkGroups.Count.ShouldBe(0);
        _libraryManager.Templates.Count.ShouldBe(0);
    }

    [Fact]
    public void RemoveTemplate_UpdatesStatusText()
    {
        // Arrange
        var group1 = CreateTestGroup("Group1", 1);
        var group2 = CreateTestGroup("Group2", 2);
        var template1 = _libraryManager.SaveTemplate(group1, "Group 1", null, "User");
        var template2 = _libraryManager.SaveTemplate(group2, "Group 2", null, "User");
        _viewModel.LoadGroupsCommand.Execute(null);

        // Counts are no longer surfaced in StatusText (the section labels + lists show
        // them); StatusText is empty while groups exist and only carries the empty hint.
        _viewModel.StatusText.ShouldBeEmpty();

        // Act
        _viewModel.RemoveTemplateCommand.Execute(template1);
        _viewModel.StatusText.ShouldBeEmpty();   // one group still present

        _viewModel.RemoveTemplateCommand.Execute(template2);

        // Assert
        _viewModel.StatusText.ShouldBe("No saved groups");   // empty → hint
    }

    [Fact]
    public void AddTemplate_AddsToUserGroupsCollection()
    {
        // Arrange
        var group = CreateTestGroup("NewGroup", 3);
        var template = _libraryManager.SaveTemplate(group, "New Template", "Test description", "User");

        _viewModel.UserGroups.Count.ShouldBe(0);

        // Act
        _viewModel.AddTemplate(template);

        // Assert
        _viewModel.UserGroups.Count.ShouldBe(1);
        _viewModel.UserGroups[0].Template.ShouldBe(template);
        _viewModel.StatusText.ShouldBeEmpty();   // counts not surfaced once a group exists
    }

    [Fact]
    public void AddTemplate_AddsToPdkGroupsCollection()
    {
        // Arrange
        var group = CreateTestGroup("PdkMacro", 2);
        var template = _libraryManager.SaveTemplate(group, "PDK Template", "PDK macro", "PDK");

        _viewModel.PdkGroups.Count.ShouldBe(0);

        // Act
        _viewModel.AddTemplate(template);

        // Assert
        _viewModel.PdkGroups.Count.ShouldBe(1);
        _viewModel.PdkGroups[0].Template.ShouldBe(template);
        _viewModel.StatusText.ShouldBeEmpty();   // counts not surfaced once a group exists
    }

    [Fact]
    public void DuplicateTemplate_CreatesNewTemplateInCollection()
    {
        // Arrange
        var group = CreateTestGroup("Original", 2);
        var template = _libraryManager.SaveTemplate(group, "Original Template", "Description", "User");
        template.TemplateGroup = group; // Ensure group is loaded
        _viewModel.LoadGroupsCommand.Execute(null);

        _viewModel.UserGroups.Count.ShouldBe(1);

        // Act
        _viewModel.DuplicateTemplateCommand.Execute(template);

        // Assert
        _viewModel.UserGroups.Count.ShouldBe(2);
        _viewModel.UserGroups[1].Template.Name.ShouldBe("Original Template_Copy");
    }

    [Fact]
    public void GroupTemplateItemViewModel_DeleteCommand_CallsParentRemove()
    {
        // Arrange
        var group = CreateTestGroup("TestGroup", 1);
        var template = _libraryManager.SaveTemplate(group, "Test Template", null, "User");
        _viewModel.LoadGroupsCommand.Execute(null);

        var itemVm = _viewModel.UserGroups[0];
        _viewModel.UserGroups.Count.ShouldBe(1);

        // Act
        itemVm.DeleteCommand.Execute(null);

        // Assert
        _viewModel.UserGroups.Count.ShouldBe(0);
        _libraryManager.Templates.Count.ShouldBe(0);
    }

    [Fact]
    public void GroupTemplateItemViewModel_HoverState_TogglesCorrectly()
    {
        // Arrange
        var group = CreateTestGroup("TestGroup", 1);
        var template = _libraryManager.SaveTemplate(group, "Test Template", null, "User");
        var itemVm = new GroupTemplateItemViewModel(template, _viewModel);

        itemVm.IsHovered.ShouldBeFalse();

        // Act - Simulate hover
        itemVm.IsHovered = true;

        // Assert
        itemVm.IsHovered.ShouldBeTrue();

        // Act - Simulate hover exit
        itemVm.IsHovered = false;

        // Assert
        itemVm.IsHovered.ShouldBeFalse();
    }

    [Fact]
    public void StatusText_ReflectsEmptyLibrary()
    {
        // Arrange & Act
        _viewModel.LoadGroupsCommand.Execute(null);

        // Assert
        _viewModel.StatusText.ShouldBe("No saved groups");
    }

    [Fact]
    public void StatusText_IsEmptyWhenGroupsPresent()
    {
        // Arrange
        var userGroup = CreateTestGroup("UserGroup", 1);
        var pdkGroup = CreateTestGroup("PdkGroup", 1);
        _libraryManager.SaveTemplate(userGroup, "User Group", null, "User");
        _libraryManager.SaveTemplate(pdkGroup, "PDK Macro", null, "PDK");

        // Act
        _viewModel.LoadGroupsCommand.Execute(null);

        // Assert — counts live in the section labels + lists now, not in StatusText
        _viewModel.StatusText.ShouldBeEmpty();
    }

    [Fact]
    public async Task RenameTemplate_UpdatesTemplateNameInCollection()
    {
        // Arrange - use AddTemplate to avoid stale reference after LoadGroupsCommand reload
        var group = CreateTestGroup("OriginalGroup", 2);
        var template = _libraryManager.SaveTemplate(group, "Original Name", "description", "User");
        template.TemplateGroup = group;
        _viewModel.AddTemplate(template);
        _viewModel.UserGroups.Count.ShouldBe(1);

        _viewModel.ShowRenameDialogAsync = _ => Task.FromResult<string?>("New Name");

        // Act
        await _viewModel.RenameTemplateCommand.ExecuteAsync(template);

        // Assert
        _viewModel.UserGroups.Count.ShouldBe(1);
        _viewModel.UserGroups[0].Template.Name.ShouldBe("New Name");
    }

    [Fact]
    public async Task RenameTemplate_DoesNothing_WhenDialogCancelled()
    {
        // Arrange
        var group = CreateTestGroup("MyGroup", 1);
        var template = _libraryManager.SaveTemplate(group, "My Template", null, "User");
        template.TemplateGroup = group;
        _viewModel.AddTemplate(template);

        _viewModel.ShowRenameDialogAsync = _ => Task.FromResult<string?>(null);

        // Act
        await _viewModel.RenameTemplateCommand.ExecuteAsync(template);

        // Assert - name unchanged
        _viewModel.UserGroups.Count.ShouldBe(1);
        _viewModel.UserGroups[0].Template.Name.ShouldBe("My Template");
    }

    [Fact]
    public async Task RenameTemplate_DoesNothing_WhenNameUnchanged()
    {
        // Arrange
        var group = CreateTestGroup("Group", 1);
        var template = _libraryManager.SaveTemplate(group, "Same Name", null, "User");
        template.TemplateGroup = group;
        _viewModel.AddTemplate(template);

        _viewModel.ShowRenameDialogAsync = _ => Task.FromResult<string?>("Same Name");

        // Act
        await _viewModel.RenameTemplateCommand.ExecuteAsync(template);

        // Assert - still same name, no duplicate
        _viewModel.UserGroups.Count.ShouldBe(1);
        _viewModel.UserGroups[0].Template.Name.ShouldBe("Same Name");
    }

    [Fact]
    public async Task RenameTemplate_DoesNothing_WhenNoDialogCallback()
    {
        // Arrange
        var group = CreateTestGroup("Group", 1);
        var template = _libraryManager.SaveTemplate(group, "Template", null, "User");
        template.TemplateGroup = group;
        _viewModel.AddTemplate(template);

        // ShowRenameDialogAsync is null by default

        // Act - should not throw
        await _viewModel.RenameTemplateCommand.ExecuteAsync(template);

        // Assert - unchanged
        _viewModel.UserGroups.Count.ShouldBe(1);
    }

    [Fact]
    public void RenameCommand_IsAvailableOnGroupTemplateItemViewModel()
    {
        // Arrange
        var group = CreateTestGroup("Group", 1);
        var template = _libraryManager.SaveTemplate(group, "Test", null, "User");
        var itemVm = new GroupTemplateItemViewModel(template, _viewModel);

        // Act & Assert
        itemVm.RenameCommand.ShouldNotBeNull();
        itemVm.RenameCommand.CanExecute(null).ShouldBeTrue();
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
                new Dictionary<int, CAP_Core.LightCalculation.SMatrix>(),
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
}
