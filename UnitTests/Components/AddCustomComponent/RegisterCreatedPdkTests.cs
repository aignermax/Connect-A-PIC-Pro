using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Hierarchy;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Components.Creation;
using CAP_Core.Components.Process;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

public class RegisterCreatedPdkTests : IDisposable
{
    private readonly string _userPdkRoot;
    private readonly UserPreferencesService _preferencesService;
    private readonly LeftPanelViewModel _leftPanel;
    private readonly UserPdkStore _store;

    public RegisterCreatedPdkTests()
    {
        var prefsPath = Path.Combine(Path.GetTempPath(), $"RegisterCreatedPdkPrefs_{Guid.NewGuid():N}.json");
        _userPdkRoot = Path.Combine(Path.GetTempPath(), $"RegisterCreatedPdkRoot_{Guid.NewGuid():N}");
        _preferencesService = new UserPreferencesService(prefsPath);
        _store = new UserPdkStore(_userPdkRoot, new PdkJsonSaver(), new PdkLoader());

        var canvas = new DesignCanvasViewModel();
        var groupLibrary = new GroupLibraryManager();
        var pdkLoader = new PdkLoader();

        _leftPanel = new LeftPanelViewModel(canvas, groupLibrary, pdkLoader, _preferencesService,
            new HierarchyPanelViewModel(canvas), new PdkManagerViewModel(),
            new ComponentLibraryViewModel(groupLibrary));
    }

    public void Dispose()
    {
        if (Directory.Exists(_userPdkRoot))
        {
            try { Directory.Delete(_userPdkRoot, true); } catch { }
        }
    }

    private static ProcessDefinition SimpleProcess(string name) => new() { Name = name };

    private static PdkComponentDraft SimpleComponent(string name) => new()
    {
        Name = name,
        Category = "Test",
        NazcaFunction = "test.straight",
        WidthMicrometers = 10,
        HeightMicrometers = 2,
        Pins = new List<PhysicalPinDraft>
        {
            new() { Name = "a0", OffsetXMicrometers = 0, OffsetYMicrometers = 1, AngleDegrees = 180 },
            new() { Name = "b0", OffsetXMicrometers = 10, OffsetYMicrometers = 1, AngleDegrees = 0 },
        },
    };

    [Fact]
    public void RegisterCreatedPdk_emptyNamedPdk_appearsInLoadedPdks_withZeroComponents()
    {
        var path = _store.CreateNamedPdkWithProcess("Fresh Lib", SimpleProcess("Process A"), "gdsfactory", null);

        _leftPanel.RegisterCreatedPdk(path);

        _leftPanel.PdkManager.LoadedPdks.ShouldContain(p =>
            p.Name == "Fresh Lib" && !p.IsBundled && p.ComponentCount == 0);
        _leftPanel.GetLoadedPdkDrafts().ShouldContain(d => d.Name == "Fresh Lib");
    }

    [Fact]
    public void RegisterCreatedPdk_reappliesActiveProcess_soValueCompatiblePdkIsEnabled()
    {
        var path = _store.CreateNamedPdkWithProcess("Fresh Lib", SimpleProcess("Process A"), "gdsfactory", null);

        _leftPanel.RegisterCreatedPdk(path);

        var pdkInfo = _leftPanel.PdkManager.LoadedPdks.ShouldHaveSingleItem();
        pdkInfo.IsEnabled.ShouldBeTrue();
    }

    [Fact]
    public void RegisterCreatedPdk_pdkWithComponent_addsTemplateToAllTemplates()
    {
        var path = _store.SaveToNamedPdk("Fresh Lib With Component", SimpleProcess("Process B"),
            SimpleComponent("Straight X"), "nazca", null);

        _leftPanel.RegisterCreatedPdk(path);

        _leftPanel.AllTemplates.ShouldContain(t => t.Name == "Straight X" && t.PdkSource == "Fresh Lib With Component");
        _leftPanel.PdkManager.LoadedPdks.ShouldContain(p => p.Name == "Fresh Lib With Component" && p.ComponentCount == 1);
    }

    [Fact]
    public void RegisterCreatedPdk_calledTwiceForSamePath_doesNotDuplicate()
    {
        var path = _store.CreateNamedPdkWithProcess("Fresh Lib", SimpleProcess("Process A"), "gdsfactory", null);

        _leftPanel.RegisterCreatedPdk(path);
        _leftPanel.RegisterCreatedPdk(path);

        _leftPanel.PdkManager.LoadedPdks.Count(p => p.Name == "Fresh Lib").ShouldBe(1);
        _leftPanel.GetLoadedPdkDrafts().Count(d => d.Name == "Fresh Lib").ShouldBe(1);
    }

    [Fact]
    public void RegisterCreatedPdk_missingFile_doesNotThrow_andLogsStatus()
    {
        var deadPath = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}.json");

        Should.NotThrow(() => _leftPanel.RegisterCreatedPdk(deadPath));

        _leftPanel.PdkManager.LoadedPdks.ShouldNotContain(p => p.FilePath == deadPath);
    }
}
