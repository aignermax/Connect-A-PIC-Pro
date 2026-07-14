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

/// <summary>
/// End-to-end chain across Tasks 1-5 of the PDK-lifecycle work (issue #700 family): a user-authored
/// PDK directory is replayed at startup (<see cref="LeftPanelViewModel.ReloadUserPdksAtStartupAsync"/>,
/// LC-T1), the active-process lock then separates a value-and-layer-compatible PDK from a
/// value-compatible-but-layer-renumbered one in both the live membership set
/// (<see cref="LeftPanelViewModel.ResolveLiveMemberPdkNames"/>, LC-T3/T4) and the placement guard
/// (<see cref="SingleProcessPolicy.CheckPlacement"/>), and finally the compatible PDK is deleted to
/// trash and unregistered (<see cref="UserPdkStore.MoveToTrash"/> + <see cref="LeftPanelViewModel.UnregisterPdk"/>,
/// LC-T5). Each stage is covered in isolation by <c>UserPdkStartupReloadTests</c>,
/// <c>CustomPdkVisibilityTests</c>, and <c>PdkTrashDeleteTests</c> respectively — this test does not
/// repeat their per-branch coverage, it only proves the chain of state carries correctly from one
/// stage into the next (one PDK loaded at startup is the same PDK later found live-compatible and
/// later still trashed).
/// </summary>
public class PdkLifecycleFlowTests : IDisposable
{
    private readonly string _testPrefsPath;
    private readonly string _userPdkRoot;
    private readonly LeftPanelViewModel _leftPanel;

    public PdkLifecycleFlowTests()
    {
        _testPrefsPath = Path.Combine(Path.GetTempPath(), $"PdkLifecycleFlowPrefs_{Guid.NewGuid():N}.json");
        _userPdkRoot = Path.Combine(Path.GetTempPath(), $"PdkLifecycleFlowRoot_{Guid.NewGuid():N}");

        var preferencesService = new UserPreferencesService(_testPrefsPath);
        var canvas = new DesignCanvasViewModel();
        var groupLibrary = new GroupLibraryManager();
        var pdkLoader = new PdkLoader();

        // Initialize() loads the bundled Demo PDK up front (mirrors CustomPdkVisibilityTests) so
        // it is available both as the process-lock anchor in stage (b) and as the bundled
        // layer-consistency reference ResolveLiveMemberPdkNames prefers.
        _leftPanel = new LeftPanelViewModel(canvas, groupLibrary, pdkLoader, preferencesService,
            new HierarchyPanelViewModel(canvas), new PdkManagerViewModel(),
            new ComponentLibraryViewModel(groupLibrary));
        _leftPanel.Initialize();
    }

    public void Dispose()
    {
        if (File.Exists(_testPrefsPath))
        {
            try { File.Delete(_testPrefsPath); } catch { /* best effort */ }
        }
        if (Directory.Exists(_userPdkRoot))
        {
            try { Directory.Delete(_userPdkRoot, true); } catch { /* best effort */ }
        }
    }

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

    /// <summary>Same core/cladding + thickness tolerance band as the bundled Demo PDK (Si/SiO2, 222 vs 220 nm).</summary>
    private static ProcessDefinition CompatibleProcess() => new()
    {
        Name = "Compatible Process",
        CoreThicknessNm = 222,
        Materials = new List<ProcessMaterial>
        {
            new() { Name = "Si", Role = "core" },
            new() { Name = "SiO2", Role = "cladding" },
        },
    };

    /// <summary>Value-compatible with Demo but its WAVEGUIDE layer is renumbered (999 vs Demo's 1) — layer-divergent.</summary>
    private static ProcessDefinition RenumberedProcess() => new()
    {
        Name = "Renumbered Process",
        CoreThicknessNm = 222,
        Materials = new List<ProcessMaterial>
        {
            new() { Name = "Si", Role = "core" },
            new() { Name = "SiO2", Role = "cladding" },
        },
        Layers = new List<ProcessLayer> { new() { Name = "WAVEGUIDE", Layer = 999, Datatype = 0 } },
    };

    [Fact]
    public async Task StartupReload_thenProcessLock_thenTrashDelete_carriesPdkStateAcrossAllThreeStages()
    {
        // ----- Fixture: two custom PDK JSONs sitting directly in the user-pdks root, as if from
        // a previous session, before the app (or this test) ever registers them in memory. -----
        var store = new UserPdkStore(_userPdkRoot, new PdkJsonSaver(), new PdkLoader());
        var compatiblePath = store.SaveToNamedPdk("CompatibleLib", CompatibleProcess(), SimpleComponent("Compatible Straight"), "nazca", null);
        var renumberedPath = store.SaveToNamedPdk("RenumberedLib", RenumberedProcess(), SimpleComponent("Renumbered Straight"), "nazca", null);

        // ----- Stage (a): startup reload registers both dir-scanned PDKs (LC-T1). -----
        await _leftPanel.ReloadUserPdksAtStartupAsync(_userPdkRoot);

        new[] { "CompatibleLib", "RenumberedLib" }
            .All(name => _leftPanel.PdkManager.LoadedPdks.Any(p => p.Name == name && !p.IsBundled))
            .ShouldBeTrue("both PDKs found on disk at startup must be registered into the library, exactly like a manual import");

        // ----- Stage (b): locking the design to the (bundled) Demo process must separate the two
        // reloaded PDKs — value-and-layer compatible stays live, layer-renumbered falls out — both
        // in the live membership set (library filter) and at the placement guard (LC-T3/T4). -----
        var demoName = _leftPanel.PdkManager.LoadedPdks.First(p => p.Name.Contains("Demo", StringComparison.OrdinalIgnoreCase)).Name;
        var demoDraft = _leftPanel.GetLoadedPdkDrafts().First(d => d.Name == demoName);
        var demoFingerprint = ProcessFingerprintFactory.From(demoDraft);
        demoFingerprint.IsSpecified.ShouldBeTrue("sanity: the bundled Demo PDK must declare a full process");

        var active = new ActiveProcessSelection(
            DisplayName: "Demo Process",
            Fingerprint: demoFingerprint,
            MemberPdkNames: new List<string> { demoName },
            IsPlayground: false);
        _leftPanel.ApplyActiveProcess(active);

        var liveMembers = _leftPanel.ResolveLiveMemberPdkNames(active);
        var processAgnostic = _leftPanel.GetProcessAgnosticPdkNames();
        var compatibleAllowed = SingleProcessPolicy.CheckPlacement(active, "CompatibleLib", processAgnostic, liveMembers).IsAllowed;
        var renumberedAllowed = SingleProcessPolicy.CheckPlacement(active, "RenumberedLib", processAgnostic, liveMembers).IsAllowed;

        (liveMembers.Contains("CompatibleLib") && !liveMembers.Contains("RenumberedLib")
            && compatibleAllowed && !renumberedAllowed)
            .ShouldBeTrue("the value-and-layer-compatible PDK must stay a live member and remain placeable, while the " +
                          "layer-renumbered one (same fingerprint, different GDS layer numbers) must be excluded and blocked");

        // ----- Stage (c): trashing the still-compatible PDK removes it from the library and moves
        // its file into .trash, leaving the original path gone (LC-T5). -----
        var trashedPath = store.MoveToTrash(compatiblePath);
        var unregistered = _leftPanel.UnregisterPdk(compatiblePath);

        (unregistered
            && !_leftPanel.PdkManager.LoadedPdks.Any(p => p.Name == "CompatibleLib")
            && !_leftPanel.AllTemplates.Any(t => t.PdkSource == "CompatibleLib")
            && !File.Exists(compatiblePath)
            && File.Exists(trashedPath))
            .ShouldBeTrue("the trashed PDK must disappear from the loaded library and templates while its file survives under .trash, " +
                          "and the original path must no longer exist");

        // The unrelated, layer-renumbered PDK must be untouched by deleting a different PDK.
        renumberedPath.ShouldNotBeNull();
        File.Exists(renumberedPath).ShouldBeTrue();
    }
}
