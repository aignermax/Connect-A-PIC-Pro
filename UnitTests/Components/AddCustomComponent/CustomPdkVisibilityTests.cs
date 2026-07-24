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

    private CanvasInteractionViewModel CreatePlacementInteraction(ActiveProcessSelection active)
    {
        var interaction = new CanvasInteractionViewModel(_canvas, new CommandManager());
        interaction.PlacementContext = new CAP_Core.Components.Process.PlacementPolicyContext(
            getActiveProcess: () => active,
            getProcessAgnosticPdkNames: () => _leftPanel.GetProcessAgnosticPdkNames(),
            resolveComponentPdkSource: _ => null,
            resolveLiveMemberPdkNames: () => _leftPanel.ResolveLiveMemberPdkNames(active));
        return interaction;
    }

    public void Dispose()
    {
        if (File.Exists(_testPrefsPath))
        {
            try { File.Delete(_testPrefsPath); } catch { }
        }
        if (Directory.Exists(_userPdkRoot))
        {
            try { Directory.Delete(_userPdkRoot, true); } catch { }
        }
    }

    private string DemoPdkName() =>
        _leftPanel.PdkManager.LoadedPdks.First(p => p.Name.Contains("Demo", StringComparison.OrdinalIgnoreCase)).Name;

    private ActiveProcessSelection ApplyDemoProcessLock()
    {
        var demoName = DemoPdkName();
        var demoDraft = _leftPanel.GetLoadedPdkDrafts().First(d => d.Name == demoName);
        var demoFingerprint = ProcessFingerprintFactory.From(demoDraft);
        demoFingerprint.IsSpecified.ShouldBeTrue("sanity: the bundled Demo PDK must declare a full process");

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

    [Fact]
    public void OverTolerancePdk_InSameCatalogGroupAsAllowedPdk_StaysBlocked()
    {
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

    [Fact]
    public void TwoMetalAugmentedPdks_ConflictingSharedLayerNumbers_SecondIsLockedOut()
    {
        ApplyDemoProcessLock();

        ProcessDefinition AugmentedProcess(int metalLayerNumber) => new()
        {
            Name = $"Augmented {metalLayerNumber}",
            CoreThicknessNm = 222,
            Materials = new List<ProcessMaterial>
            {
                new() { Name = "Si", Role = "core" },
                new() { Name = "SiO2", Role = "cladding" },
            },
            Layers = new List<ProcessLayer>
            {
                new() { Name = "WAVEGUIDE", Layer = 1, Datatype = 0 },
                new() { Name = "METAL2", Layer = metalLayerNumber, Datatype = 0 },
            },
        };

        var store = new UserPdkStore(_userPdkRoot, new PdkJsonSaver(), new PdkLoader());
        var componentA = SimpleComponent("MetalLibA Straight");
        var pathA = store.SaveToNamedPdk("MetalLibA", AugmentedProcess(12), componentA, "nazca", null);
        _leftPanel.RegisterSavedCustomComponent(componentA, "MetalLibA", pathA);

        var componentB = SimpleComponent("MetalLibB Straight");
        var pathB = store.SaveToNamedPdk("MetalLibB", AugmentedProcess(99), componentB, "nazca", null);
        _leftPanel.RegisterSavedCustomComponent(componentB, "MetalLibB", pathB);

        _leftPanel.PdkManager.LoadedPdks.Single(p => p.Name == "MetalLibA").IsEnabled.ShouldBeTrue(
            "the first-accepted metal-augmented PDK keeps its membership");
        var conflicting = _leftPanel.PdkManager.LoadedPdks.Single(p => p.Name == "MetalLibB");
        conflicting.IsEnabled.ShouldBeFalse(
            "a second PDK defining the same added layer NAME with a different GDS number must not join the same chip");
        conflicting.IsLockedByProcess.ShouldBeTrue();
    }

    [Fact]
    public void ReferencePdkNotLoaded_PairwiseClashWithLoadedMember_StillLockedOut()
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
        clashingPdk.IsEnabled.ShouldBeFalse(
            "a candidate that renumbers a layer another live member defines must stay locked out even without a snapshot reference");
        clashingPdk.IsLockedByProcess.ShouldBeTrue();
    }

    [Fact]
    public void RenumberedCustomPdkLoadedBeforeFoundry_ReferenceStaysFoundry_CustomFallsOut()
    {
        var preferencesService = new UserPreferencesService(_testPrefsPath);
        var canvas = new DesignCanvasViewModel();
        var groupLibrary = new GroupLibraryManager();
        var pdkLoader = new PdkLoader();
        var leftPanel = new LeftPanelViewModel(canvas, groupLibrary, pdkLoader, preferencesService,
            new HierarchyPanelViewModel(canvas), new PdkManagerViewModel(),
            new ComponentLibraryViewModel(groupLibrary));

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
        var component = SimpleComponent("MyCustom Straight");
        var path = store.SaveToNamedPdk("MyCustom", renumberedProcess, component, "nazca", null);

        leftPanel.RegisterSavedCustomComponent(component, "MyCustom", path);
        leftPanel.Initialize();

        var demoName = leftPanel.PdkManager.LoadedPdks
            .First(p => p.Name.Contains("Demo", StringComparison.OrdinalIgnoreCase)).Name;
        var demoDraft = leftPanel.GetLoadedPdkDrafts().First(d => d.Name == demoName);
        var demoFingerprint = ProcessFingerprintFactory.From(demoDraft);
        demoFingerprint.IsSpecified.ShouldBeTrue("sanity: the bundled Demo PDK must declare a full process");

        var active = new ActiveProcessSelection(
            DisplayName: "Demo Process",
            Fingerprint: demoFingerprint,
            MemberPdkNames: new List<string> { demoName, "MyCustom" },
            IsPlayground: false);

        leftPanel.ApplyActiveProcess(active);

        var demoPdk = leftPanel.PdkManager.LoadedPdks.Single(p => p.Name == demoName);
        demoPdk.IsEnabled.ShouldBeTrue(
            "the bundled Foundry PDK must never be locked out by a divergent custom snapshot member acting as reference");
        demoPdk.IsLockedByProcess.ShouldBeFalse();

        var customPdk = leftPanel.PdkManager.LoadedPdks.Single(p => p.Name == "MyCustom");
        customPdk.IsEnabled.ShouldBeFalse(
            "the renumbered custom PDK must fall out once the bundled Foundry PDK is correctly used as the layer-consistency reference");
        customPdk.IsLockedByProcess.ShouldBeTrue();
    }
}
