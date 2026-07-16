using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Hierarchy;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core;
using CAP_Core.Components.Creation;
using CAP_Core.Components.Process;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

/// <summary>
/// #570 integrity of the fork-shadow reference role (PR #742 review, finding 0): a user fork
/// that shadows a bundled PDK inherits the foundry's layer-consistency reference authority
/// ONLY while its process is layer-consistent with the bundled original. A hand-edited fork
/// with renumbered layers must never become the reference and lock genuine foundry PDKs out.
/// </summary>
public class BundledPdkForkReferenceIntegrityTests : IDisposable
{
    private const string ForkedPdkName = "Foundry PDK";
    private const string OtherBundledPdkName = "Foundry Lib B";
    private const string Code = "import gdsfactory as gf\ncomponent = gf.components.coupler()";

    private readonly string _testPrefsPath;
    private readonly string _userPdkRoot;
    private readonly string _bundledDir;
    private readonly ErrorConsoleService _errorConsole = new();
    private readonly UserPreferencesService _preferencesService;

    public BundledPdkForkReferenceIntegrityTests()
    {
        var id = Guid.NewGuid().ToString("N");
        _testPrefsPath = Path.Combine(Path.GetTempPath(), $"ForkRefPrefs_{id}.json");
        _userPdkRoot = Path.Combine(Path.GetTempPath(), $"ForkRefRoot_{id}");
        _bundledDir = Path.Combine(Path.GetTempPath(), $"ForkRefBundled_{id}");
        _preferencesService = new UserPreferencesService(_testPrefsPath);
    }

    public void Dispose()
    {
        try { File.Delete(_testPrefsPath); } catch { }
        foreach (var dir in new[] { _userPdkRoot, _bundledDir })
        {
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }

    private static ProcessDefinition FullProcess(int waveguideLayer) => new()
    {
        Name = "Foundry Process",
        CoreThicknessNm = 222,
        Materials = new List<ProcessMaterial>
        {
            new() { Name = "Si", Role = "core" },
            new() { Name = "SiO2", Role = "cladding" },
        },
        Layers = new List<ProcessLayer> { new() { Name = "WAVEGUIDE", Layer = waveguideLayer, Datatype = 0 } },
    };

    private static PdkComponentDraft RawCodeComponent(string name) => new()
    {
        Name = name,
        Category = "Test",
        RawCode = Code,
        RawCodeBackend = "gdsfactory",
        WidthMicrometers = 10,
        HeightMicrometers = 2,
        Pins = new List<PhysicalPinDraft>
        {
            new() { Name = "o1", OffsetXMicrometers = 0, OffsetYMicrometers = 1, AngleDegrees = 180 },
            new() { Name = "o2", OffsetXMicrometers = 10, OffsetYMicrometers = 1, AngleDegrees = 0 },
        },
    };

    private void WriteBundledPdk(string fileName, string pdkName)
    {
        Directory.CreateDirectory(_bundledDir);
        new PdkJsonSaver().SaveToFile(new PdkDraft
        {
            Name = pdkName,
            Backend = "gdsfactory",
            Process = FullProcess(waveguideLayer: 1),
            Components = new List<PdkComponentDraft> { RawCodeComponent($"{pdkName} Comp") },
        }, Path.Combine(_bundledDir, fileName));
    }

    private (LeftPanelViewModel leftPanel, UserPdkStore store) CreateLeftPanelWithTwoBundledPdks()
    {
        WriteBundledPdk("foundry-pdk.json", ForkedPdkName);
        WriteBundledPdk("lib-b.json", OtherBundledPdkName);

        var canvas = new DesignCanvasViewModel();
        var libraryManager = new GroupLibraryManager();
        var store = new UserPdkStore(_userPdkRoot, new PdkJsonSaver(), new PdkLoader());
        var deps = new AddCustomComponentDependencies(
            new ComponentGeometryExtractor(
                new Mock<IComponentPreviewRenderer>().Object, new Mock<IComponentPreviewRenderer>().Object),
            Fdtd: null, UserPdkStore: store);

        var leftPanel = new LeftPanelViewModel(
            canvas, libraryManager, new PdkLoader(), _preferencesService,
            new HierarchyPanelViewModel(canvas), new PdkManagerViewModel(),
            new ComponentLibraryViewModel(libraryManager),
            errorConsole: _errorConsole, addCustomComponentDeps: deps);
        leftPanel.LoadBundledPdksFrom(_bundledDir);
        return (leftPanel, store);
    }

    [Fact]
    public async Task TamperedShadowFork_withRenumberedLayers_neverBecomesTheLayerReference()
    {
        // A fork whose layers were hand-renumbered (999 instead of the foundry's 1) must lose
        // the reference authority: the correct-layer PDKs stay live members and the tampered
        // fork itself falls out of the locked process — never the other way around.
        var (leftPanel, store) = CreateLeftPanelWithTwoBundledPdks();

        var correctComponent = RawCodeComponent("Correct Straight");
        var correctPath = store.SaveToNamedPdk("CorrectLib", FullProcess(waveguideLayer: 1),
            correctComponent, "gdsfactory", null);
        leftPanel.RegisterSavedCustomComponent(correctComponent, "CorrectLib", correctPath);

        store.SaveToNamedPdk(ForkedPdkName, FullProcess(waveguideLayer: 999),
            RawCodeComponent("Tampered Coupler"), "gdsfactory", null);
        await leftPanel.ReloadUserPdksAtStartupAsync(_userPdkRoot);

        leftPanel.PdkManager.LoadedPdks.Single(p => p.Name == ForkedPdkName)
            .ShadowsBundledPdk.ShouldBeTrue("sanity: the tampered fork shadows the bundled entry");

        var fingerprint = ProcessFingerprintFactory.From(
            leftPanel.GetLoadedPdkDrafts().First(d => d.Name == "CorrectLib"));
        fingerprint.IsSpecified.ShouldBeTrue("sanity: the process carries a full fingerprint");
        leftPanel.ApplyActiveProcess(new ActiveProcessSelection(
            DisplayName: "Foundry Process",
            Fingerprint: fingerprint,
            MemberPdkNames: new List<string> { ForkedPdkName, "CorrectLib" },
            IsPlayground: false));

        leftPanel.PdkManager.LoadedPdks.Single(p => p.Name == OtherBundledPdkName).IsEnabled.ShouldBeTrue(
            "the genuine bundled PDK with the correct foundry layer numbering must stay a member");
        leftPanel.PdkManager.LoadedPdks.Single(p => p.Name == "CorrectLib").IsEnabled.ShouldBeTrue(
            "the layer-correct custom PDK must stay a member");
        leftPanel.PdkManager.LoadedPdks.Single(p => p.Name == ForkedPdkName).IsEnabled.ShouldBeFalse(
            "the tampered fork loses the reference authority and falls out of the locked process");
    }

    [Fact]
    public async Task ConsistentShadowFork_stillInheritsTheReferenceRole()
    {
        // Regression guard for the legitimate case: a fork whose process is layer-consistent
        // with the bundled original keeps the foundry's reference role.
        var (leftPanel, store) = CreateLeftPanelWithTwoBundledPdks();

        store.SaveToNamedPdk(ForkedPdkName, FullProcess(waveguideLayer: 1),
            RawCodeComponent("Forked Coupler"), "gdsfactory", null);
        await leftPanel.ReloadUserPdksAtStartupAsync(_userPdkRoot);

        var fingerprint = ProcessFingerprintFactory.From(
            leftPanel.GetLoadedPdkDrafts().First(d => d.Name == ForkedPdkName));
        leftPanel.ApplyActiveProcess(new ActiveProcessSelection(
            DisplayName: "Foundry Process",
            Fingerprint: fingerprint,
            MemberPdkNames: new List<string> { ForkedPdkName },
            IsPlayground: false));

        leftPanel.PdkManager.LoadedPdks.Single(p => p.Name == ForkedPdkName).IsEnabled.ShouldBeTrue(
            "a layer-consistent fork inherits the foundry reference role and stays live");
        leftPanel.PdkManager.LoadedPdks.Single(p => p.Name == OtherBundledPdkName).IsEnabled.ShouldBeTrue(
            "the other genuine bundled PDK is layer-consistent and stays a member");
    }
}
