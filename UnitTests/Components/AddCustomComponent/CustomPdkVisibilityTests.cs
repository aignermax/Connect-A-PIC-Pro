using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CAP.Avalonia.Commands;
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
/// Reproduces and verifies the fix for the by-name-vs-by-value process lock bug: a newly
/// registered custom PDK whose fabrication process is VALUE-compatible with the active process
/// (same core material/cladding within tolerance) must become visible/enabled immediately, even
/// though it cannot possibly be present in the active process's persisted
/// <see cref="ActiveProcessSelection.MemberPdkNames"/> snapshot (that snapshot predates the new
/// PDK's existence). A value-INCOMPATIBLE custom PDK must remain locked out (no regression).
/// </summary>
public class CustomPdkVisibilityTests : IDisposable
{
    private readonly string _testPrefsPath;
    private readonly string _userPdkRoot;
    private readonly LeftPanelViewModel _leftPanel;
    private readonly DesignCanvasViewModel _canvas;

    public CustomPdkVisibilityTests()
    {
        _testPrefsPath = Path.Combine(Path.GetTempPath(), $"CustomPdkVisibilityPrefs_{Guid.NewGuid():N}.json");
        _userPdkRoot = Path.Combine(Path.GetTempPath(), $"CustomPdkVisibilityUserPdks_{Guid.NewGuid():N}");

        var preferencesService = new UserPreferencesService(_testPrefsPath);
        _canvas = new DesignCanvasViewModel();
        var groupLibrary = new GroupLibraryManager();
        var pdkLoader = new PdkLoader();

        _leftPanel = new LeftPanelViewModel(_canvas, groupLibrary, pdkLoader, preferencesService,
            new HierarchyPanelViewModel(_canvas), new PdkManagerViewModel(),
            new ComponentLibraryViewModel(groupLibrary));
        _leftPanel.Initialize();
    }

    /// <summary>
    /// Wires a <see cref="CanvasInteractionViewModel"/> the way <c>MainViewModel</c> does for
    /// the placement guard (issue placement-livemembers): active process + live by-value
    /// member set both sourced from <paramref name="active"/> and the live PDK catalog, so the
    /// placement path sees exactly what the library-filter lock already sees (#732).
    /// </summary>
    private CanvasInteractionViewModel CreatePlacementInteraction(ActiveProcessSelection active)
    {
        var interaction = new CanvasInteractionViewModel(_canvas, new CommandManager());
        interaction.GetActiveProcess = () => active;
        interaction.GetProcessAgnosticPdkNames = () => _leftPanel.GetProcessAgnosticPdkNames();
        interaction.GetLiveMemberPdkNames = () => _leftPanel.ResolveLiveMemberPdkNames(active);
        return interaction;
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

    private string DemoPdkName() =>
        _leftPanel.PdkManager.LoadedPdks.First(p => p.Name.Contains("Demo", StringComparison.OrdinalIgnoreCase)).Name;

    /// <summary>
    /// Locks the library to a real process built from the bundled Demo PDK's own fingerprint.
    /// Returns the applied selection so callers can reuse the identical snapshot (e.g. to feed
    /// <see cref="LeftPanelViewModel.ResolveLiveMemberPdkNames"/> the same way <c>MainViewModel</c> does).
    /// </summary>
    private ActiveProcessSelection ApplyDemoProcessLock()
    {
        var demoName = DemoPdkName();
        var demoDraft = _leftPanel.GetLoadedPdkDrafts().First(d => d.Name == demoName);
        var demoFingerprint = ProcessFingerprintFactory.From(demoDraft);
        demoFingerprint.IsSpecified.ShouldBeTrue("sanity: the bundled Demo PDK must declare a full process");

        // Mirrors a persisted design snapshot: at the time it was saved, only the Demo PDK
        // belonged to this process — a custom PDK registered afterward cannot be in it.
        var active = new ActiveProcessSelection(
            DisplayName: "Demo Process",
            Fingerprint: demoFingerprint,
            MemberPdkNames: new List<string> { demoName },
            IsPlayground: false);

        _leftPanel.ApplyActiveProcess(active);
        return active;
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

    [Fact]
    public void ValueCompatibleCustomPdk_BecomesEnabledAndVisible_AfterReapply()
    {
        ApplyDemoProcessLock();

        // A value-compatible process: same core/cladding materials as Demo (Si/SiO2), thickness
        // within the ±5 nm tolerance (222 vs 220), default wavelength within ±40 nm (inherits 1550).
        var compatibleProcess = new ProcessDefinition
        {
            Name = "MyLib Process",
            CoreThicknessNm = 222,
            Materials = new List<ProcessMaterial>
            {
                new() { Name = "Si", Role = "core" },
                new() { Name = "SiO2", Role = "cladding" },
            },
        };

        var store = new UserPdkStore(_userPdkRoot, new PdkJsonSaver(), new PdkLoader());
        var component = SimpleComponent("MyLib Straight");
        var path = store.SaveToNamedPdk("MyLib", compatibleProcess, component, "nazca", null);

        _leftPanel.RegisterSavedCustomComponent(component, "MyLib", path);

        var myLibPdk = _leftPanel.PdkManager.LoadedPdks.Single(p => p.Name == "MyLib");
        myLibPdk.IsEnabled.ShouldBeTrue("a value-compatible custom PDK must be allowed under the active process lock");
        myLibPdk.IsLockedByProcess.ShouldBeFalse();

        _leftPanel.FilteredTemplates.ShouldContain(t => t.PdkSource == "MyLib",
            "the value-compatible PDK's component must appear in the filtered library");
    }

    [Fact]
    public void ValueIncompatibleCustomPdk_StaysLockedAndFiltered()
    {
        ApplyDemoProcessLock();

        // A value-INCOMPATIBLE process: different core material (Si3N4 vs Demo's Si).
        var foreignProcess = new ProcessDefinition
        {
            Name = "Foreign Process",
            CoreThicknessNm = 300,
            Materials = new List<ProcessMaterial>
            {
                new() { Name = "Si3N4", Role = "core" },
                new() { Name = "SiO2", Role = "cladding" },
            },
        };

        var store = new UserPdkStore(_userPdkRoot, new PdkJsonSaver(), new PdkLoader());
        var component = SimpleComponent("ForeignLib Straight");
        var path = store.SaveToNamedPdk("ForeignLib", foreignProcess, component, "nazca", null);

        _leftPanel.RegisterSavedCustomComponent(component, "ForeignLib", path);

        var foreignPdk = _leftPanel.PdkManager.LoadedPdks.Single(p => p.Name == "ForeignLib");
        foreignPdk.IsEnabled.ShouldBeFalse("a value-incompatible custom PDK must remain blocked by the active process lock");
        foreignPdk.IsLockedByProcess.ShouldBeTrue();

        _leftPanel.FilteredTemplates.ShouldNotContain(t => t.PdkSource == "ForeignLib",
            "the value-incompatible PDK's component must not leak into the filtered library");
    }

    /// <summary>
    /// Reproduces the field bug directly at the placement guard (not just the library filter):
    /// a component from a value-compatible custom PDK registered after the process was saved
    /// must be placeable via <see cref="CanvasInteractionViewModel.PlaceComponentAt"/>, using the
    /// live member set from <see cref="LeftPanelViewModel.ResolveLiveMemberPdkNames"/> rather than the
    /// stale <see cref="ActiveProcessSelection.MemberPdkNames"/> snapshot.
    /// </summary>
    [Fact]
    public void ValueCompatibleCustomPdk_IsPlaceable_ViaCanvasInteractionViewModel()
    {
        var active = ApplyDemoProcessLock();

        var compatibleProcess = new ProcessDefinition
        {
            Name = "MyLib Process",
            CoreThicknessNm = 222,
            Materials = new List<ProcessMaterial>
            {
                new() { Name = "Si", Role = "core" },
                new() { Name = "SiO2", Role = "cladding" },
            },
        };

        var store = new UserPdkStore(_userPdkRoot, new PdkJsonSaver(), new PdkLoader());
        var component = SimpleComponent("MyLib Straight");
        var path = store.SaveToNamedPdk("MyLib", compatibleProcess, component, "nazca", null);
        _leftPanel.RegisterSavedCustomComponent(component, "MyLib", path);

        var template = _leftPanel.AllTemplates.Single(t => t.PdkSource == "MyLib");
        var interaction = CreatePlacementInteraction(active);
        interaction.SelectedTemplate = template;

        interaction.CanvasClicked(100, 100);

        _canvas.Components.Count.ShouldBe(1,
            "a value-compatible custom PDK registered after the process snapshot was taken must be placeable");
    }

    /// <summary>Mirror of the above with a value-INCOMPATIBLE custom PDK — must stay blocked.</summary>
    [Fact]
    public void ValueIncompatibleCustomPdk_IsBlocked_ViaCanvasInteractionViewModel()
    {
        var active = ApplyDemoProcessLock();

        var foreignProcess = new ProcessDefinition
        {
            Name = "Foreign Process",
            CoreThicknessNm = 300,
            Materials = new List<ProcessMaterial>
            {
                new() { Name = "Si3N4", Role = "core" },
                new() { Name = "SiO2", Role = "cladding" },
            },
        };

        var store = new UserPdkStore(_userPdkRoot, new PdkJsonSaver(), new PdkLoader());
        var component = SimpleComponent("ForeignLib Straight");
        var path = store.SaveToNamedPdk("ForeignLib", foreignProcess, component, "nazca", null);
        _leftPanel.RegisterSavedCustomComponent(component, "ForeignLib", path);

        var template = _leftPanel.AllTemplates.Single(t => t.PdkSource == "ForeignLib");
        var interaction = CreatePlacementInteraction(active);
        interaction.SelectedTemplate = template;

        string? status = null;
        interaction.UpdateStatus = s => status = s;
        interaction.CanvasClicked(100, 100);

        _canvas.Components.Count.ShouldBe(0, "a value-incompatible custom PDK must remain blocked at placement");
        status.ShouldNotBeNull();
        status!.ShouldContain("process");
    }

    /// <summary>
    /// Guards the non-transitive-tolerance edge case: process compatibility is a tolerance band
    /// (thickness ±5 nm), so two PDKs can share a catalog <c>ProcessGroup</c> (pairwise within
    /// tolerance of EACH OTHER) while only one is within tolerance of the active process. The lock
    /// must be computed per-PDK against the active fingerprint directly, never via a group
    /// representative — otherwise a PDK that is over-tolerance to the active process would ride
    /// into the allowed set on its group-mate's coat-tails (issue #570 violation).
    /// </summary>
    [Fact]
    public void OverTolerancePdk_InSameCatalogGroupAsAllowedPdk_StaysBlocked()
    {
        // Active process at 213 nm (its own defining PDK is NOT loaded — the case where the
        // stale snapshot would be useless anyway). Both custom PDKs below share core/cladding
        // and are within ±5 nm of EACH OTHER (218 vs 222 = 4 nm) so the catalog groups them
        // together — but only 218 nm is within ±5 nm of the active 213 nm (222 nm is 9 nm off).
        var active = new ActiveProcessSelection(
            DisplayName: "213 nm Process",
            Fingerprint: new ProcessFingerprint("Si", 213, "SiO2", 1550, "213 nm Process"),
            MemberPdkNames: new List<string>(),
            IsPlayground: false);
        _leftPanel.ApplyActiveProcess(active);

        var store = new UserPdkStore(_userPdkRoot, new PdkJsonSaver(), new PdkLoader());
        var inTol = SimpleComponent("InTol Straight");
        var outTol = SimpleComponent("OutTol Straight");
        var inTolPath = store.SaveToNamedPdk("InTolLib", ProcessAt(218), inTol, "nazca", null);
        var outTolPath = store.SaveToNamedPdk("OutTolLib", ProcessAt(222), outTol, "nazca", null);

        _leftPanel.RegisterSavedCustomComponent(inTol, "InTolLib", inTolPath);
        _leftPanel.RegisterSavedCustomComponent(outTol, "OutTolLib", outTolPath);

        var inTolPdk = _leftPanel.PdkManager.LoadedPdks.Single(p => p.Name == "InTolLib");
        inTolPdk.IsEnabled.ShouldBeTrue("218 nm is within ±5 nm of the active 213 nm process");
        inTolPdk.IsLockedByProcess.ShouldBeFalse();

        var outTolPdk = _leftPanel.PdkManager.LoadedPdks.Single(p => p.Name == "OutTolLib");
        outTolPdk.IsEnabled.ShouldBeFalse("222 nm is 9 nm off the active 213 nm process — over tolerance, even though it shares a catalog group with the allowed 218 nm PDK");
        outTolPdk.IsLockedByProcess.ShouldBeTrue();

        _leftPanel.FilteredTemplates.ShouldContain(t => t.PdkSource == "InTolLib");
        _leftPanel.FilteredTemplates.ShouldNotContain(t => t.PdkSource == "OutTolLib");
    }

    private static ProcessDefinition ProcessAt(double thicknessNm) => new()
    {
        Name = $"Si {thicknessNm} nm",
        CoreThicknessNm = thicknessNm,
        Materials = new List<ProcessMaterial>
        {
            new() { Name = "Si", Role = "core" },
            new() { Name = "SiO2", Role = "cladding" },
        },
    };

    /// <summary>
    /// Reproduces the #570 follow-up bug: a custom PDK's WAVEGUIDE layer was renumbered (the
    /// Demo PDK defines it as layer 1) so it is still fingerprint-compatible (same Si/SiO2
    /// materials, thickness within tolerance) but mixing it with the Demo PDK on one chip would
    /// produce a chip with two different "WAVEGUIDE" GDS layer numbers — unmanufacturable. The
    /// layer-stack check must keep it out of the live member set even though the fingerprint
    /// alone would have allowed it.
    /// </summary>
    [Fact]
    public void RenumberedLayerCustomPdk_ValueCompatibleButLayersDiverge_StaysLockedAndFiltered()
    {
        ApplyDemoProcessLock();

        var renumberedProcess = new ProcessDefinition
        {
            Name = "Renumbered Process",
            CoreThicknessNm = 222,
            Materials = new List<ProcessMaterial>
            {
                new() { Name = "Si", Role = "core" },
                new() { Name = "SiO2", Role = "cladding" },
            },
            Layers = new List<ProcessLayer>
            {
                new() { Name = "WAVEGUIDE", Layer = 999, Datatype = 0 },
            },
        };

        var store = new UserPdkStore(_userPdkRoot, new PdkJsonSaver(), new PdkLoader());
        var component = SimpleComponent("RenumberedLib Straight");
        var path = store.SaveToNamedPdk("RenumberedLib", renumberedProcess, component, "nazca", null);

        _leftPanel.RegisterSavedCustomComponent(component, "RenumberedLib", path);

        var renumberedPdk = _leftPanel.PdkManager.LoadedPdks.Single(p => p.Name == "RenumberedLib");
        renumberedPdk.IsEnabled.ShouldBeFalse(
            "same WAVEGUIDE layer NAME but a different GDS layer number must not be treated as the same process");
        renumberedPdk.IsLockedByProcess.ShouldBeTrue();

        _leftPanel.FilteredTemplates.ShouldNotContain(t => t.PdkSource == "RenumberedLib",
            "the layer-renumbered PDK's component must not leak into the filtered library");
    }

    /// <summary>Mirror of the renumbered case at the placement guard (the #736 path).</summary>
    [Fact]
    public void RenumberedLayerCustomPdk_IsBlocked_ViaCanvasInteractionViewModel()
    {
        var active = ApplyDemoProcessLock();

        var renumberedProcess = new ProcessDefinition
        {
            Name = "Renumbered Process",
            CoreThicknessNm = 222,
            Materials = new List<ProcessMaterial>
            {
                new() { Name = "Si", Role = "core" },
                new() { Name = "SiO2", Role = "cladding" },
            },
            Layers = new List<ProcessLayer>
            {
                new() { Name = "WAVEGUIDE", Layer = 999, Datatype = 0 },
            },
        };

        var store = new UserPdkStore(_userPdkRoot, new PdkJsonSaver(), new PdkLoader());
        var component = SimpleComponent("RenumberedLib Straight");
        var path = store.SaveToNamedPdk("RenumberedLib", renumberedProcess, component, "nazca", null);
        _leftPanel.RegisterSavedCustomComponent(component, "RenumberedLib", path);

        var template = _leftPanel.AllTemplates.Single(t => t.PdkSource == "RenumberedLib");
        var interaction = CreatePlacementInteraction(active);
        interaction.SelectedTemplate = template;

        interaction.CanvasClicked(100, 100);

        _canvas.Components.Count.ShouldBe(0, "a layer-renumbered custom PDK must remain blocked at placement");
    }

    /// <summary>
    /// The #734 metal-addition workflow must keep working: a custom PDK that matches the Demo
    /// PDK's WAVEGUIDE layer exactly and only ADDS a new metal layer must stay a live member.
    /// </summary>
    [Fact]
    public void MetalAugmentedCustomPdk_MatchingSharedLayer_StaysEnabledAndVisible()
    {
        ApplyDemoProcessLock();

        var augmentedProcess = new ProcessDefinition
        {
            Name = "Augmented Process",
            CoreThicknessNm = 222,
            Materials = new List<ProcessMaterial>
            {
                new() { Name = "Si", Role = "core" },
                new() { Name = "SiO2", Role = "cladding" },
            },
            Layers = new List<ProcessLayer>
            {
                new() { Name = "WAVEGUIDE", Layer = 1, Datatype = 0 },
                new() { Name = "METAL2", Layer = 12, Datatype = 0 },
            },
        };

        var store = new UserPdkStore(_userPdkRoot, new PdkJsonSaver(), new PdkLoader());
        var component = SimpleComponent("AugmentedLib Straight");
        var path = store.SaveToNamedPdk("AugmentedLib", augmentedProcess, component, "nazca", null);

        _leftPanel.RegisterSavedCustomComponent(component, "AugmentedLib", path);

        var augmentedPdk = _leftPanel.PdkManager.LoadedPdks.Single(p => p.Name == "AugmentedLib");
        augmentedPdk.IsEnabled.ShouldBeTrue(
            "matching shared layers plus one additional metal layer must still count as the same process");
        augmentedPdk.IsLockedByProcess.ShouldBeFalse();

        _leftPanel.FilteredTemplates.ShouldContain(t => t.PdkSource == "AugmentedLib");
    }

    /// <summary>
    /// When the reference PDK (the process's own defining PDK, per the snapshot
    /// <see cref="ActiveProcessSelection.MemberPdkNames"/>) is not currently loaded, the layer
    /// check must be skipped entirely — behavior stays fingerprint-only, exactly as before this
    /// feature existed. A layer-clashing candidate must NOT be penalized when there is nothing
    /// loaded to compare it against.
    /// </summary>
    [Fact]
    public void ReferencePdkNotLoaded_FallsBackToFingerprintOnly_LayerClashIgnored()
    {
        var active = new ActiveProcessSelection(
            DisplayName: "Unloaded Foundry Process",
            Fingerprint: new CAP_Core.Components.Process.ProcessFingerprint("Si", 220, "SiO2", 1550, "Unloaded Foundry Process"),
            MemberPdkNames: new List<string> { "SomeUnloadedFoundryPdk" },
            IsPlayground: false);
        _leftPanel.ApplyActiveProcess(active);

        var clashingProcess = new ProcessDefinition
        {
            Name = "Clashing Process",
            CoreThicknessNm = 222,
            Materials = new List<ProcessMaterial>
            {
                new() { Name = "Si", Role = "core" },
                new() { Name = "SiO2", Role = "cladding" },
            },
            Layers = new List<ProcessLayer>
            {
                new() { Name = "WAVEGUIDE", Layer = 999, Datatype = 0 },
            },
        };

        var store = new UserPdkStore(_userPdkRoot, new PdkJsonSaver(), new PdkLoader());
        var component = SimpleComponent("ClashingLib Straight");
        var path = store.SaveToNamedPdk("ClashingLib", clashingProcess, component, "nazca", null);

        _leftPanel.RegisterSavedCustomComponent(component, "ClashingLib", path);

        var clashingPdk = _leftPanel.PdkManager.LoadedPdks.Single(p => p.Name == "ClashingLib");
        clashingPdk.IsEnabled.ShouldBeTrue(
            "with no reference PDK loaded, fingerprint compatibility alone must still unlock the candidate");
        clashingPdk.IsLockedByProcess.ShouldBeFalse();
    }
}
