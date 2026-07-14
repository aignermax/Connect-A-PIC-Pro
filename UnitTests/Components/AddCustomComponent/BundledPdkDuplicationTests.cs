using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.AddCustomComponent;
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
/// "Duplicate as custom PDK" (issue #734): a bundled/foundry PDK is read-only since #733, so
/// extending its process (e.g. adding a metal cross-section) goes through a one-step duplicate
/// into a named custom PDK. These tests cover the duplication service itself (deep process copy,
/// backend/routing carry-over, name validation, source file untouched) and the acceptance flow:
/// the registered duplicate is value-compatible with the active process and immediately enabled.
/// </summary>
public class BundledPdkDuplicationTests : IDisposable
{
    private readonly string _userPdkRoot;
    private readonly string _sourcePdkPath;
    private readonly string _testPrefsPath;
    private readonly UserPdkStore _store;

    public BundledPdkDuplicationTests()
    {
        _userPdkRoot = Path.Combine(Path.GetTempPath(), $"BundledPdkDupUserPdks_{Guid.NewGuid():N}");
        _sourcePdkPath = Path.Combine(Path.GetTempPath(), $"BundledPdkDupSource_{Guid.NewGuid():N}.json");
        _testPrefsPath = Path.Combine(Path.GetTempPath(), $"BundledPdkDupPrefs_{Guid.NewGuid():N}.json");
        _store = new UserPdkStore(_userPdkRoot, new PdkJsonSaver(), new PdkLoader());
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_userPdkRoot)) Directory.Delete(_userPdkRoot, true); } catch { /* best effort */ }
        try { if (File.Exists(_sourcePdkPath)) File.Delete(_sourcePdkPath); } catch { /* best effort */ }
        try { if (File.Exists(_testPrefsPath)) File.Delete(_testPrefsPath); } catch { /* best effort */ }
    }

    /// <summary>A foundry-like source draft with a full process, backend and routing cross-section.</summary>
    private static PdkDraft FoundryDraft() => new()
    {
        Name = "SiEPIC EBeam (test)",
        Foundry = "SiEPIC",
        Backend = "gdsfactory",
        GdsFactoryRoutingCrossSection = "xs_test",
        Process = new ProcessDefinition
        {
            Name = "EBeam Process",
            Foundry = "SiEPIC",
            CoreThicknessNm = 220,
            Layers = new List<ProcessLayer>
            {
                new() { Name = "Si", Layer = 1, Datatype = 0 },
            },
            Xsections = new List<ProcessXsection>
            {
                new() { Name = "strip", Kind = XsectionKind.Optical, WidthUm = 0.5, Layers = new List<string> { "Si" } },
            },
            Materials = new List<ProcessMaterial>
            {
                new() { Name = "Si", Role = "core" },
                new() { Name = "SiO2", Role = "cladding" },
            },
        },
        Components = new(),
    };

    [Fact]
    public void Duplicate_CreatesNamedCustomPdk_WithProcessCopyAndBackendCarriedOver()
    {
        var source = FoundryDraft();

        var path = BundledPdkDuplicationService.Duplicate(_store, source, "My EBeam Copy");

        File.Exists(path).ShouldBeTrue();
        var created = new PdkLoader().LoadFromFileForEditing(path);
        created.Name.ShouldBe("My EBeam Copy");
        created.Backend.ShouldBe("gdsfactory");
        created.GdsFactoryRoutingCrossSection.ShouldBe("xs_test");
        created.Components.ShouldBeEmpty();
        created.Process.ShouldNotBeNull();
        created.Process!.Name.ShouldBe(source.Process!.Name);
        created.Process.CoreThicknessNm.ShouldBe(source.Process.CoreThicknessNm);
        created.Process.Xsections.Single().Name.ShouldBe("strip");
        ProcessCompatibility.AreCompatible(
                ProcessFingerprintFactory.From(created), ProcessFingerprintFactory.From(source))
            .ShouldBeTrue("the duplicate must be value-compatible with the foundry process");
    }

    [Fact]
    public void Duplicate_DeepCopiesProcess_SoEditingItNeverMutatesTheFoundryDraft()
    {
        var source = FoundryDraft();

        BundledPdkDuplicationService.Duplicate(_store, source, "Deep Copy Check");

        // The clone handed to the saver must never alias the loaded foundry draft's process:
        // the source's collections stay exactly as authored.
        source.Process!.Xsections.Count.ShouldBe(1);
        source.Process.Layers.Count.ShouldBe(1);
        source.Backend.ShouldBe("gdsfactory");
    }

    [Fact]
    public void Duplicate_LeavesSourcePdkFileByteIdentical()
    {
        var source = FoundryDraft();
        new PdkJsonSaver().SaveToFile(source, _sourcePdkPath);
        var loaded = new PdkLoader().LoadFromFileForEditing(_sourcePdkPath);
        var bytesBefore = File.ReadAllBytes(_sourcePdkPath);

        BundledPdkDuplicationService.Duplicate(_store, loaded, "Copy Of Foundry");

        File.ReadAllBytes(_sourcePdkPath).ShouldBe(bytesBefore,
            "duplicating must never write to the foundry PDK's own file");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Duplicate_WithEmptyName_Throws(string name)
    {
        Should.Throw<ArgumentException>(() =>
            BundledPdkDuplicationService.Duplicate(_store, FoundryDraft(), name));
    }

    [Fact]
    public void Duplicate_WithExistingName_Throws()
    {
        BundledPdkDuplicationService.Duplicate(_store, FoundryDraft(), "Taken Name");

        Should.Throw<InvalidOperationException>(() =>
                BundledPdkDuplicationService.Duplicate(_store, FoundryDraft(), "taken name"))
            .Message.ShouldContain("already exists");
    }

    [Fact]
    public void Duplicate_WithoutProcess_Throws()
    {
        var noProcess = new PdkDraft { Name = "Tools", Components = new() };

        Should.Throw<InvalidOperationException>(() =>
                BundledPdkDuplicationService.Duplicate(_store, noProcess, "Copy"))
            .Message.ShouldContain("no fabrication process");
    }

    /// <summary>
    /// Acceptance (issue #734): duplicating a bundled PDK while its process is the active,
    /// locked process yields a custom PDK that is registered immediately, value-compatible,
    /// and therefore enabled (not locked) — without saving any component into it first.
    /// </summary>
    [Fact]
    public void RegisteredDuplicate_OfActiveProcess_IsImmediatelyEnabled()
    {
        var leftPanel = BuildLeftPanel();
        var demoDraft = leftPanel.GetLoadedPdkDrafts()
            .First(d => d.Name.Contains("Demo", StringComparison.OrdinalIgnoreCase));
        var fingerprint = ProcessFingerprintFactory.From(demoDraft);
        fingerprint.IsSpecified.ShouldBeTrue("sanity: the bundled Demo PDK must declare a full process");
        leftPanel.ApplyActiveProcess(new ActiveProcessSelection(
            DisplayName: "Demo Process",
            Fingerprint: fingerprint,
            MemberPdkNames: new List<string> { demoDraft.Name },
            IsPlayground: false));

        var createdPath = BundledPdkDuplicationService.Duplicate(_store, demoDraft, "Demo (custom)");
        var registered = leftPanel.RegisterCreatedCustomPdk(createdPath);

        registered.ShouldNotBeNull();
        registered!.Name.ShouldBe("Demo (custom)");
        var row = leftPanel.PdkManager.LoadedPdks.Single(p => p.Name == "Demo (custom)");
        row.IsBundled.ShouldBeFalse("the duplicate is a user PDK, so it gets the per-PDK Edit… button");
        row.IsEnabled.ShouldBeTrue("a value-identical duplicate must be allowed under the active process lock");
        row.IsLockedByProcess.ShouldBeFalse();

        // Registering the same file again must not create a duplicate row.
        leftPanel.RegisterCreatedCustomPdk(createdPath).ShouldNotBeNull();
        leftPanel.PdkManager.LoadedPdks.Count(p => p.Name == "Demo (custom)").ShouldBe(1);
    }

    private LeftPanelViewModel BuildLeftPanel()
    {
        var canvas = new DesignCanvasViewModel();
        var groupLibrary = new GroupLibraryManager();
        var leftPanel = new LeftPanelViewModel(canvas, groupLibrary, new PdkLoader(),
            new UserPreferencesService(_testPrefsPath),
            new HierarchyPanelViewModel(canvas), new PdkManagerViewModel(),
            new ComponentLibraryViewModel(groupLibrary));
        leftPanel.Initialize();
        return leftPanel;
    }
}
