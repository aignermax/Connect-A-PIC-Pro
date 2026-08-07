using System.Text.Json;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Export;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using CAP_DataAccess.Persistence;
using Moq;
using Shouldly;
using System.Collections.ObjectModel;

namespace UnitTests.Integration;

/// <summary>
/// Integration tests for ComponentGroup persistence through the File → Save/Open workflow.
/// Tests the complete user path: create components → connect → group → save → new → open → verify.
/// Covers issue #248: ComponentGroups not persisted in File → Save/Open workflow.
/// </summary>
public class DesignFileGroupPersistenceTests
{
    private readonly ObservableCollection<ComponentTemplate> _library;

    public DesignFileGroupPersistenceTests()
    {
        _library = new ObservableCollection<ComponentTemplate>(
            TestPdkLoader.LoadAllTemplates());
    }

    /// <summary>
    /// Core test for issue #248: Save a design with a ComponentGroup, clear, reload, verify group.
    /// </summary>
    [Fact]
    public async Task SaveAndLoad_DesignWithComponentGroup_PreservesGroup()
    {
        // Arrange
        var (saveVm, saveCanvas) = CreateFileOperationsSetup();
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_group_{Guid.NewGuid()}.cappro");

        try
        {
            // Create two MMI components on canvas
            var mmiTemplate = _library.First(t => t.Name == "1x2 MMI Splitter");
            var comp1 = ComponentTemplates.CreateFromTemplate(mmiTemplate, 100, 100);
            comp1.Identifier = "mmi_1";
            var comp2 = ComponentTemplates.CreateFromTemplate(mmiTemplate, 300, 100);
            comp2.Identifier = "mmi_2";

            var vm1 = saveCanvas.AddComponent(comp1, mmiTemplate.Name);
            var vm2 = saveCanvas.AddComponent(comp2, mmiTemplate.Name);

            // Create a group with internal connection
            var group = new ComponentGroup("TestSaveGroup")
            {
                PhysicalX = 100,
                PhysicalY = 100,
                Description = "Group for save/load test"
            };
            group.AddChild(comp1);
            group.AddChild(comp2);

            // Add frozen internal path
            var routedPath = new RoutedPath();
            routedPath.Segments.Add(new StraightSegment(180, 125.5, 300, 127.5, 0));
            var frozenPath = new FrozenWaveguidePath
            {
                Path = routedPath,
                StartPin = comp1.PhysicalPins.First(p => p.Name == "out1"),
                EndPin = comp2.PhysicalPins.First(p => p.Name == "in")
            };
            group.AddInternalPath(frozenPath);

            // Add external pin
            var externalPin = new GroupPin
            {
                Name = "ext_input",
                InternalPin = comp1.PhysicalPins.First(p => p.Name == "in"),
                RelativeX = 0,
                RelativeY = 27.5,
                AngleDegrees = 180
            };
            group.AddExternalPin(externalPin);

            // Replace individual components with the group on canvas
            saveCanvas.Components.Clear();
            saveCanvas.AddComponent(group);

            // Act - Save to file
            var mockDialog = new Mock<IFileDialogService>();
            mockDialog.Setup(f => f.ShowSaveFileDialogAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(tempFile);
            saveVm.FileDialogService = mockDialog.Object;

            await saveVm.SaveDesignAsCommand.ExecuteAsync(null);

            // Verify file was written
            File.Exists(tempFile).ShouldBeTrue("Design file should be created");
            var json = await File.ReadAllTextAsync(tempFile);
            json.ShouldContain("TestSaveGroup");
            json.ShouldContain("mmi_1");
            json.ShouldContain("mmi_2");

            // Act - Create new VM and load
            var (loadVm, loadCanvas) = CreateFileOperationsSetup();
            var loadDialog = new Mock<IFileDialogService>();
            loadDialog.Setup(f => f.ShowOpenFileDialogAsync(
                It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(tempFile);
            loadVm.FileDialogService = loadDialog.Object;

            await loadVm.LoadDesignCommand.ExecuteAsync(null);

            // Assert - Group loaded correctly
            loadCanvas.Components.Count.ShouldBeGreaterThan(0,
                "Canvas should have components after loading");

            var groupVm = loadCanvas.Components.FirstOrDefault(
                c => c.Component is ComponentGroup);
            groupVm.ShouldNotBeNull("A ComponentGroup should be loaded");

            var loadedGroup = (ComponentGroup)groupVm!.Component;
            loadedGroup.GroupName.ShouldBe("TestSaveGroup");
            loadedGroup.Description.ShouldBe("Group for save/load test");
            loadedGroup.ChildComponents.Count.ShouldBe(2);

            // Verify child components
            loadedGroup.ChildComponents
                .Any(c => c.Identifier == "mmi_1").ShouldBeTrue();
            loadedGroup.ChildComponents
                .Any(c => c.Identifier == "mmi_2").ShouldBeTrue();

            // Verify internal path preserved
            loadedGroup.InternalPaths.Count.ShouldBe(1);
            var loadedPath = loadedGroup.InternalPaths[0];
            loadedPath.Path.Segments.Count.ShouldBe(1);
            loadedPath.StartPin.ShouldNotBeNull();
            loadedPath.EndPin.ShouldNotBeNull();
            loadedPath.StartPin.Name.ShouldBe("out1");
            loadedPath.EndPin.Name.ShouldBe("in");

            // Verify external pin preserved
            loadedGroup.ExternalPins.Count.ShouldBe(1);
            loadedGroup.ExternalPins[0].Name.ShouldBe("ext_input");
            loadedGroup.ExternalPins[0].AngleDegrees.ShouldBe(180);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    /// <summary>
    /// Verifies that standalone components and groups can coexist in the same file.
    /// </summary>
    [Fact]
    public async Task SaveAndLoad_MixedDesign_PreservesBothGroupsAndComponents()
    {
        var (saveVm, saveCanvas) = CreateFileOperationsSetup();
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_mixed_{Guid.NewGuid()}.cappro");

        try
        {
            // Add a standalone component
            var detTemplate = _library.First(t => t.Name == "Photodetector");
            var standalone = ComponentTemplates.CreateFromTemplate(detTemplate, 600, 100);
            standalone.Identifier = "standalone_det";
            saveCanvas.AddComponent(standalone, detTemplate.Name);

            // Add a group
            var mmiTemplate = _library.First(t => t.Name == "1x2 MMI Splitter");
            var child1 = ComponentTemplates.CreateFromTemplate(mmiTemplate, 100, 100);
            child1.Identifier = "group_child_1";
            var child2 = ComponentTemplates.CreateFromTemplate(mmiTemplate, 300, 100);
            child2.Identifier = "group_child_2";

            var group = new ComponentGroup("MixedGroup")
            {
                PhysicalX = 100,
                PhysicalY = 100
            };
            group.AddChild(child1);
            group.AddChild(child2);
            saveCanvas.AddComponent(group);

            // Save
            var mockDialog = new Mock<IFileDialogService>();
            mockDialog.Setup(f => f.ShowSaveFileDialogAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(tempFile);
            saveVm.FileDialogService = mockDialog.Object;
            await saveVm.SaveDesignAsCommand.ExecuteAsync(null);

            // Load into fresh canvas
            var (loadVm, loadCanvas) = CreateFileOperationsSetup();
            var loadDialog = new Mock<IFileDialogService>();
            loadDialog.Setup(f => f.ShowOpenFileDialogAsync(
                It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(tempFile);
            loadVm.FileDialogService = loadDialog.Object;
            await loadVm.LoadDesignCommand.ExecuteAsync(null);

            // Assert - Both standalone and group loaded
            loadCanvas.Components.Count.ShouldBe(2);

            var standaloneVm = loadCanvas.Components.FirstOrDefault(
                c => c.Component is not ComponentGroup);
            standaloneVm.ShouldNotBeNull("Standalone component should be loaded");

            var groupVm = loadCanvas.Components.FirstOrDefault(
                c => c.Component is ComponentGroup);
            groupVm.ShouldNotBeNull("Group should be loaded");

            var loadedGroup = (ComponentGroup)groupVm!.Component;
            loadedGroup.GroupName.ShouldBe("MixedGroup");
            loadedGroup.ChildComponents.Count.ShouldBe(2);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    /// <summary>
    /// Verifies that v2.0 files without the optional Groups property load normally.
    /// </summary>
    [Fact]
    public async Task LoadDesign_V2FileWithoutGroups_LoadsNormally()
    {
        var (loadVm, loadCanvas) = CreateFileOperationsSetup();
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_compat_{Guid.NewGuid()}.cappro");

        try
        {
            // Valid v2 file omitting the optional Groups property
            var data = new
            {
                FormatVersion = "2.0",
                Components = new[]
                {
                    new
                    {
                        TemplateName = "Photodetector",
                        X = 100.0,
                        Y = 200.0,
                        Identifier = "det_1",
                        Rotation = 0
                    }
                },
                Connections = Array.Empty<object>()
            };

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            await File.WriteAllTextAsync(tempFile, json);

            var loadDialog = new Mock<IFileDialogService>();
            loadDialog.Setup(f => f.ShowOpenFileDialogAsync(
                It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(tempFile);
            loadVm.FileDialogService = loadDialog.Object;
            await loadVm.LoadDesignCommand.ExecuteAsync(null);

            loadCanvas.Components.Count.ShouldBe(1);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    /// <summary>
    /// Verifies that legacy files without FormatVersion load successfully but log a loud warning.
    /// </summary>
    [Fact]
    public async Task LoadDesign_LegacyFileWithoutFormatVersion_LoadsAndWarns()
    {
        var errorConsole = new CAP_Core.ErrorConsoleService();
        var (loadVm, loadCanvas) = CreateFileOperationsSetup(errorConsole);
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_legacy_{Guid.NewGuid()}.cappro");

        try
        {
            var legacyData = new
            {
                Components = new[]
                {
                    new { TemplateName = "Photodetector", X = 0.0, Y = 0.0, Identifier = "legacy_1", Rotation = 0 }
                },
                Connections = Array.Empty<object>()
            };

            var json = JsonSerializer.Serialize(legacyData, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(tempFile, json);

            var loadDialog = new Mock<IFileDialogService>();
            loadDialog.Setup(f => f.ShowOpenFileDialogAsync(
                It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(tempFile);
            loadVm.FileDialogService = loadDialog.Object;
            await loadVm.LoadDesignCommand.ExecuteAsync(null);

            // Legacy file loads its components
            loadCanvas.Components.Count.ShouldBe(1);

            // Loud warning was logged for the missing/wrong FormatVersion
            var warnings = errorConsole.Entries
                .Where(e => e.Level == CAP_Contracts.Logger.LogLevel.Warn)
                .ToList();
            warnings.ShouldNotBeEmpty();
            warnings[0].Message.ShouldContain("Legacy .lun file");
            warnings[0].Message.ShouldContain("2.0");
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    /// <summary>
    /// Verifies that group JSON contains all required group data.
    /// </summary>
    [Fact]
    public async Task SaveDesign_WithGroup_JsonContainsGroupStructure()
    {
        var (saveVm, saveCanvas) = CreateFileOperationsSetup();
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_json_{Guid.NewGuid()}.cappro");

        try
        {
            var mmiTemplate = _library.First(t => t.Name == "1x2 MMI Splitter");
            var comp1 = ComponentTemplates.CreateFromTemplate(mmiTemplate, 0, 0);
            comp1.Identifier = "json_comp_1";

            var group = new ComponentGroup("JsonTestGroup")
            {
                PhysicalX = 0,
                PhysicalY = 0,
                Description = "Verify JSON structure"
            };
            group.AddChild(comp1);

            // Add path with bend segment
            var path = new RoutedPath();
            path.Segments.Add(new StraightSegment(0, 0, 20, 0, 0));
            path.Segments.Add(new BendSegment(20, 10, 10, 0, 90));
            group.AddInternalPath(new FrozenWaveguidePath
            {
                Path = path,
                StartPin = comp1.PhysicalPins[0],
                EndPin = comp1.PhysicalPins[0] // Self-loop for test
            });

            saveCanvas.AddComponent(group);

            var mockDialog = new Mock<IFileDialogService>();
            mockDialog.Setup(f => f.ShowSaveFileDialogAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(tempFile);
            saveVm.FileDialogService = mockDialog.Object;
            await saveVm.SaveDesignAsCommand.ExecuteAsync(null);

            var json = await File.ReadAllTextAsync(tempFile);

            // Verify JSON structure
            json.ShouldContain("Groups");
            json.ShouldContain("GroupDto");
            json.ShouldContain("JsonTestGroup");
            json.ShouldContain("ChildComponents");
            json.ShouldContain("json_comp_1");
            json.ShouldContain("InternalPaths");
            json.ShouldContain("Segments");
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    /// <summary>
    /// Test for issue #253: Deeply nested groups (super-super-groups) should be saved and loaded correctly.
    /// Tests 3 levels of nesting: components -> group -> super-group -> super-super-group.
    /// </summary>
    [Fact]
    public async Task SaveAndLoad_DeeplyNestedGroups_PreservesFullHierarchy()
    {
        // Arrange
        var (saveVm, saveCanvas) = CreateFileOperationsSetup();
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_nested_{Guid.NewGuid()}.cappro");

        try
        {
            // Level 1: Create 2 base MMI components (will be in Group A)
            var mmiTemplate = _library.First(t => t.Name == "1x2 MMI Splitter");
            var comp1 = ComponentTemplates.CreateFromTemplate(mmiTemplate, 100, 100);
            comp1.Identifier = "comp_1";
            var comp2 = ComponentTemplates.CreateFromTemplate(mmiTemplate, 300, 100);
            comp2.Identifier = "comp_2";

            // Level 2: Create Group A with 2 components
            var groupA = new ComponentGroup("GroupA")
            {
                PhysicalX = 100,
                PhysicalY = 100,
                Description = "Base group level 1"
            };
            groupA.AddChild(comp1);
            groupA.AddChild(comp2);

            // Add internal connection in Group A
            var pathA = new RoutedPath();
            pathA.Segments.Add(new StraightSegment(180, 125.5, 300, 127.5, 0));
            groupA.AddInternalPath(new FrozenWaveguidePath
            {
                Path = pathA,
                StartPin = comp1.PhysicalPins.First(p => p.Name == "out1"),
                EndPin = comp2.PhysicalPins.First(p => p.Name == "in")
            });

            // Level 2: Create Group B (copy of Group A structure)
            var comp3 = ComponentTemplates.CreateFromTemplate(mmiTemplate, 500, 100);
            comp3.Identifier = "comp_3";
            var comp4 = ComponentTemplates.CreateFromTemplate(mmiTemplate, 700, 100);
            comp4.Identifier = "comp_4";

            var groupB = new ComponentGroup("GroupB")
            {
                PhysicalX = 500,
                PhysicalY = 100,
                Description = "Base group level 1 copy"
            };
            groupB.AddChild(comp3);
            groupB.AddChild(comp4);

            // Level 3: Create Super-Group C (contains Groups A and B)
            var superGroupC = new ComponentGroup("SuperGroupC")
            {
                PhysicalX = 100,
                PhysicalY = 100,
                Description = "Super-group level 2"
            };
            superGroupC.AddChild(groupA);
            superGroupC.AddChild(groupB);

            // Level 3: Create Super-Group D (copy structure)
            var comp5 = ComponentTemplates.CreateFromTemplate(mmiTemplate, 100, 400);
            comp5.Identifier = "comp_5";
            var comp6 = ComponentTemplates.CreateFromTemplate(mmiTemplate, 300, 400);
            comp6.Identifier = "comp_6";

            var groupE = new ComponentGroup("GroupE")
            {
                PhysicalX = 100,
                PhysicalY = 400
            };
            groupE.AddChild(comp5);
            groupE.AddChild(comp6);

            var comp7 = ComponentTemplates.CreateFromTemplate(mmiTemplate, 500, 400);
            comp7.Identifier = "comp_7";
            var comp8 = ComponentTemplates.CreateFromTemplate(mmiTemplate, 700, 400);
            comp8.Identifier = "comp_8";

            var groupF = new ComponentGroup("GroupF")
            {
                PhysicalX = 500,
                PhysicalY = 400
            };
            groupF.AddChild(comp7);
            groupF.AddChild(comp8);

            var superGroupD = new ComponentGroup("SuperGroupD")
            {
                PhysicalX = 100,
                PhysicalY = 400,
                Description = "Super-group level 2 copy"
            };
            superGroupD.AddChild(groupE);
            superGroupD.AddChild(groupF);

            // Level 4: Create Super-Super-Group E (contains Super-Groups C and D)
            var superSuperGroupE = new ComponentGroup("SuperSuperGroupE")
            {
                PhysicalX = 50,
                PhysicalY = 50,
                Description = "Super-super-group level 3 - deeply nested"
            };
            superSuperGroupE.AddChild(superGroupC);
            superSuperGroupE.AddChild(superGroupD);

            // Add only the top-level super-super-group to canvas
            saveCanvas.AddComponent(superSuperGroupE);

            // Act - Save to file
            var mockDialog = new Mock<IFileDialogService>();
            mockDialog.Setup(f => f.ShowSaveFileDialogAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(tempFile);
            saveVm.FileDialogService = mockDialog.Object;

            await saveVm.SaveDesignAsCommand.ExecuteAsync(null);

            // Verify file was written
            File.Exists(tempFile).ShouldBeTrue("Design file should be created");
            var json = await File.ReadAllTextAsync(tempFile);

            // Verify all groups are in the JSON
            json.ShouldContain("GroupA");
            json.ShouldContain("GroupB");
            json.ShouldContain("SuperGroupC");
            json.ShouldContain("SuperGroupD");
            json.ShouldContain("SuperSuperGroupE");

            // Verify all base components are in the JSON
            json.ShouldContain("comp_1");
            json.ShouldContain("comp_2");
            json.ShouldContain("comp_3");
            json.ShouldContain("comp_4");

            // Act - Create new VM and load
            var (loadVm, loadCanvas) = CreateFileOperationsSetup();
            var loadDialog = new Mock<IFileDialogService>();
            loadDialog.Setup(f => f.ShowOpenFileDialogAsync(
                It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(tempFile);
            loadVm.FileDialogService = loadDialog.Object;

            await loadVm.LoadDesignCommand.ExecuteAsync(null);

            // Assert - Canvas should have exactly 1 top-level component (the super-super-group)
            loadCanvas.Components.Count.ShouldBe(1,
                "Canvas should have exactly one top-level super-super-group");

            var topLevelVm = loadCanvas.Components[0];
            topLevelVm.Component.ShouldBeOfType<ComponentGroup>();

            var loadedSuperSuperGroup = (ComponentGroup)topLevelVm.Component;
            loadedSuperSuperGroup.GroupName.ShouldBe("SuperSuperGroupE");
            loadedSuperSuperGroup.Description.ShouldBe("Super-super-group level 3 - deeply nested");

            // Verify level 3: Super-Super-Group should contain 2 super-groups
            loadedSuperSuperGroup.ChildComponents.Count.ShouldBe(2);
            loadedSuperSuperGroup.ChildComponents.ShouldAllBe(c => c is ComponentGroup);

            var loadedSuperGroupC = loadedSuperSuperGroup.ChildComponents
                .OfType<ComponentGroup>()
                .FirstOrDefault(g => g.GroupName == "SuperGroupC");
            loadedSuperGroupC.ShouldNotBeNull("SuperGroupC should be loaded");
            loadedSuperGroupC!.Description.ShouldBe("Super-group level 2");

            var loadedSuperGroupD = loadedSuperSuperGroup.ChildComponents
                .OfType<ComponentGroup>()
                .FirstOrDefault(g => g.GroupName == "SuperGroupD");
            loadedSuperGroupD.ShouldNotBeNull("SuperGroupD should be loaded");

            // Verify level 2: Super-Groups should contain base groups
            loadedSuperGroupC.ChildComponents.Count.ShouldBe(2);
            loadedSuperGroupC.ChildComponents.ShouldAllBe(c => c is ComponentGroup);

            var loadedGroupA = loadedSuperGroupC.ChildComponents
                .OfType<ComponentGroup>()
                .FirstOrDefault(g => g.GroupName == "GroupA");
            loadedGroupA.ShouldNotBeNull("GroupA should be loaded");
            loadedGroupA!.Description.ShouldBe("Base group level 1");

            var loadedGroupB = loadedSuperGroupC.ChildComponents
                .OfType<ComponentGroup>()
                .FirstOrDefault(g => g.GroupName == "GroupB");
            loadedGroupB.ShouldNotBeNull("GroupB should be loaded");

            // Verify level 1: Base groups should contain components (not groups)
            loadedGroupA.ChildComponents.Count.ShouldBe(2);
            loadedGroupA.ChildComponents.ShouldAllBe(c => !(c is ComponentGroup));

            loadedGroupA.ChildComponents
                .Any(c => c.Identifier == "comp_1").ShouldBeTrue();
            loadedGroupA.ChildComponents
                .Any(c => c.Identifier == "comp_2").ShouldBeTrue();

            // Verify internal paths are preserved
            loadedGroupA.InternalPaths.Count.ShouldBe(1);
            var loadedPath = loadedGroupA.InternalPaths[0];
            loadedPath.StartPin!.Name.ShouldBe("out1");
            loadedPath.EndPin!.Name.ShouldBe("in");
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    /// <summary>
    /// Creates a FileOperationsViewModel with real component library for testing.
    /// </summary>
    private (FileOperationsViewModel vm, DesignCanvasViewModel canvas) CreateFileOperationsSetup(
        CAP_Core.ErrorConsoleService? errorConsole = null)
    {
        var canvas = new DesignCanvasViewModel();
        var commandManager = new CommandManager();
        var nazcaExporter = new SimpleNazcaExporter();
        var gdsExport = new GdsExportViewModel(new CAP_Core.Export.GdsExportService());
        var photonTorchExport = new CAP.Avalonia.ViewModels.Export.PhotonTorchExportViewModel(
            new CAP_Core.Export.PhotonTorchExporter(), canvas);

        var vm = new FileOperationsViewModel(
            canvas, commandManager, nazcaExporter, new CAP_Core.Export.SaxExporter(), _library, gdsExport, photonTorchExport, null!, errorConsole);

        return (vm, canvas);
    }
}
