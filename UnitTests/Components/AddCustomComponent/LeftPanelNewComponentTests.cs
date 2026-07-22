using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Hierarchy;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP.Avalonia.Services;
using CAP_Core.Components.Creation;
using CAP_Core.Components.Process;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

/// <summary>
/// Covers <see cref="LeftPanelViewModel.RegisterSavedCustomComponent"/>: the headless-testable
/// half of the "add custom component" flow (the window itself cannot be opened in a unit test).
/// </summary>
public class LeftPanelNewComponentTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

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

    private static PdkComponentDraft SampleDraft() => new()
    {
        Name = "My Coupler", Category = "Custom",
        GdsFactoryFunction = "cspdk.sin300.coupler",
        WidthMicrometers = 10, HeightMicrometers = 2,
        Pins = new List<PhysicalPinDraft>
        {
            new() { Name = "o1", OffsetXMicrometers = 0, OffsetYMicrometers = 1, AngleDegrees = 180 },
            new() { Name = "o2", OffsetXMicrometers = 10, OffsetYMicrometers = 1, AngleDegrees = 0 },
        }
    };

    /// <summary>
    /// Writes a real user-PDK file for <paramref name="processName"/> containing the sample
    /// component (exactly as <see cref="UserPdkStore"/> does at runtime) and returns its
    /// on-disk path plus the PDK display name the store assigned it.
    /// </summary>
    private (string filePath, string pdkName, PdkComponentDraft draft) SaveUserPdk(string processName)
    {
        var root = Path.Combine(Path.GetTempPath(), $"lunima-lp-nc-{Guid.NewGuid():N}");
        _tempDirs.Add(root);
        var store = new UserPdkStore(root, new PdkJsonSaver(), new PdkLoader());
        var process = new ProcessDefinition { Name = processName };
        var draft = SampleDraft();
        var path = store.Save(process, draft, "gdsfactory", null);
        var pdkName = new PdkLoader().LoadFromFileForEditing(path).Name;
        return (path, pdkName, draft);
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs.Where(Directory.Exists))
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
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

    [Fact]
    public void RegisterSavedCustomComponent_forNonActiveProcess_isHiddenWhileProcessLocked()
    {
        var vm = CreateLeftPanelViewModel();
        var (filePath, pdkName, draft) = SaveUserPdk("OtherProc");

        // A different process is active and locked — its member set does NOT include the
        // user PDK we are about to save.
        vm.ApplyActiveProcess(new ActiveProcessSelection(
            DisplayName: "Active Process", Fingerprint: null,
            MemberPdkNames: new List<string> { "Some Foundry PDK" }, IsPlayground: false));

        vm.RegisterSavedCustomComponent(draft, pdkName, filePath);

        // Added to the catalog, but the active-process lock must keep it out of the visible list
        // (issue #570): a component saved for a non-active process cannot leak into the library.
        vm.AllTemplates.ShouldContain(t => t.Name == "My Coupler");
        vm.FilteredTemplates.ShouldNotContain(t => t.Name == "My Coupler");
    }

    [Fact]
    public void RegisterSavedCustomComponent_replacesTheStaleTemplate_afterAnEditSave()
    {
        // An edit-save must replace the stale template — otherwise "Show stored S-matrices"
        // keeps showing the pre-edit matrices and the library lists the component twice.
        var vm = CreateLeftPanelViewModel();
        var (filePath, pdkName, draft) = SaveUserPdk("Proc");
        vm.RegisterSavedCustomComponent(draft, pdkName, filePath);

        var updated = SampleDraft();
        updated.SMatrix = new PdkSMatrixDraft
        {
            WavelengthNm = 1550,
            WavelengthData = new List<WavelengthSMatrixEntry>
            {
                new()
                {
                    WavelengthNm = 1550,
                    Connections = new List<SMatrixConnection>
                    {
                        new() { FromPin = "o1", ToPin = "o2", Magnitude = 0.5, PhaseDegrees = 0 }
                    }
                }
            }
        };
        vm.RegisterSavedCustomComponent(updated, pdkName, filePath);

        var matches = vm.AllTemplates.Where(t => t.Name == "My Coupler" && t.PdkSource == pdkName).ToList();
        matches.Count.ShouldBe(1);
        matches[0].SourceDraft.ShouldBeSameAs(updated);
        matches[0].CreateWavelengthSMatrixMap.ShouldNotBeNull();
    }

    [Fact]
    public void RegisterSavedCustomComponent_forActiveProcess_appearsImmediately()
    {
        var vm = CreateLeftPanelViewModel();
        var (filePath, pdkName, draft) = SaveUserPdk("ActiveProc");

        // The active process's member set includes this user PDK, so its component must show up
        // right away — no manual re-enable needed.
        vm.ApplyActiveProcess(new ActiveProcessSelection(
            DisplayName: "Active Process", Fingerprint: null,
            MemberPdkNames: new List<string> { pdkName }, IsPlayground: false));

        vm.RegisterSavedCustomComponent(draft, pdkName, filePath);

        vm.FilteredTemplates.ShouldContain(t => t.Name == "My Coupler");
    }
}
