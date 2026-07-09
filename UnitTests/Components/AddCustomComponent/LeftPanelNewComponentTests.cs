using System;
using System.IO;
using System.Linq;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Hierarchy;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP.Avalonia.Services;
using CAP_Core.Components.Creation;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

/// <summary>
/// Covers <see cref="LeftPanelViewModel.RegisterSavedCustomComponent"/>: the headless-testable
/// half of the "add custom component" flow (the window itself cannot be opened in a unit test).
/// </summary>
public class LeftPanelNewComponentTests
{
    /// <summary>Builds a <see cref="LeftPanelViewModel"/> the same way <c>LeftPanelViewModelTests</c> does.</summary>
    private static LeftPanelViewModel CreateLeftPanelViewModel()
    {
        var canvas = new DesignCanvasViewModel();
        var libraryManager = new GroupLibraryManager();
        var pdkLoader = new PdkLoader();
        var preferencesPath = Path.Combine(Path.GetTempPath(), $"test-preferences-{Guid.NewGuid()}.json");
        var preferencesService = new UserPreferencesService(preferencesPath);

        return new LeftPanelViewModel(
            canvas, libraryManager, pdkLoader, preferencesService,
            new HierarchyPanelViewModel(canvas),
            new PdkManagerViewModel(),
            new ComponentLibraryViewModel(libraryManager));
    }

    [Fact]
    public void RegisterSavedCustomComponent_adds_template_to_the_library()
    {
        var vm = CreateLeftPanelViewModel();
        int before = vm.AllTemplates.Count;

        var draft = new PdkComponentDraft
        {
            Name = "My Coupler", Category = "Custom",
            GdsFactoryFunction = "cspdk.sin300.coupler",
            WidthMicrometers = 10, HeightMicrometers = 2
        };
        vm.RegisterSavedCustomComponent(draft, "My CornerStone Components", "C:/tmp/x.json");

        vm.AllTemplates.Count.ShouldBe(before + 1);
        vm.AllTemplates.ShouldContain(t => t.Name == "My Coupler");
    }
}
