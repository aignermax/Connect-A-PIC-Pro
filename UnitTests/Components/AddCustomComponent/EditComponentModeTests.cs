using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Components.AddCustomComponent;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Export;
using CAP_Core.Solvers.Fdtd;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

public class EditComponentModeTests : IDisposable
{
    static EditComponentModeTests() =>
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lunima-nc-vm-edit-" + Guid.NewGuid().ToString("N"));

    private static NazcaPreviewResult Ok() => new()
    {
        Success = true, XMin = 0, YMin = 0, XMax = 8, YMax = 1.5,
        Pins = new List<NazcaPreviewPin> { new() { Name = "o1", X = 0, Y = 0.75, Angle = 180 }, new() { Name = "o2", X = 8, Y = 0.75, Angle = 0 } }
    };

    private static PdkComponentDraft SeedComponent(string n, string rawCode) => new()
    {
        Name = n, WidthMicrometers = 5, HeightMicrometers = 1,
        RawCode = rawCode, RawCodeBackend = "gdsfactory",
        Pins = new() { new PhysicalPinDraft { Name = "o1" }, new PhysicalPinDraft { Name = "o2" } }
    };

    private UserPdkStore Store() => new(_root, new PdkJsonSaver(), new PdkLoader());

    /// <summary>Writes a process-agnostic PDK file into the managed root (the pre-#830 GDS-import shape).</summary>
    private string SeedProcessAgnosticPdk(UserPdkStore store, string pdkName, PdkComponentDraft component)
    {
        Directory.CreateDirectory(_root);
        var path = store.ResolveNamedPath(pdkName);
        new PdkJsonSaver().SaveToFile(new PdkDraft
        {
            Name = pdkName,
            Backend = "nazca",
            ProcessAgnostic = true,
            Components = new List<PdkComponentDraft> { component },
        }, path);
        return path;
    }

    private (NewComponentViewModel vm, Mock<IFdtdSMatrixService> fdtd) Build(
        UserPdkStore store, IReadOnlyList<ProcessDefinition> processes)
    {
        var nazca = new Mock<IComponentPreviewRenderer>();
        var gds = new Mock<IComponentPreviewRenderer>();
        gds.Setup(g => g.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok());
        var extractor = new ComponentGeometryExtractor(nazca.Object, gds.Object);
        var fdtd = new Mock<IFdtdSMatrixService>();
        var vm = new NewComponentViewModel(extractor, fdtd.Object, store, processes);
        return (vm, fdtd);
    }

    private (NewComponentViewModel vm, string filePath, string rawCode) BuildWithSeededPdk()
    {
        var store = Store();
        var process = new ProcessDefinition { Name = "SiN 300" };
        const string rawCode = "import gdsfactory as gf\ncomponent = gf.components.coupler()";
        var path = store.CreateNamedPdkWithProcess("Lib", process, "gdsfactory", null);
        store.AppendToExistingPdk(path, SeedComponent("comp1", rawCode));

        var (vm, _) = Build(store, new List<ProcessDefinition> { process });
        return (vm, path, rawCode);
    }

    private (NewComponentViewModel vm, string filePath, string rawCode) BuildWithTwoSeededComponents()
    {
        var store = Store();
        var process = new ProcessDefinition { Name = "SiN 300" };
        const string rawCode = "import gdsfactory as gf\ncomponent = gf.components.coupler()";
        var path = store.CreateNamedPdkWithProcess("Lib", process, "gdsfactory", null);
        store.AppendToExistingPdk(path, SeedComponent("comp1", rawCode));
        store.AppendToExistingPdk(path, SeedComponent("comp2", rawCode));

        var (vm, _) = Build(store, new List<ProcessDefinition> { process });
        return (vm, path, rawCode);
    }

    private static ComponentTemplate BuildTemplate(string rawCode) => new()
    {
        Name = "comp1",
        RawCode = rawCode,
        RawCodeBackend = "gdsfactory",
        PdkSource = "Lib",
    };

    [Fact]
    public void LoadForEdit_prefillsFieldsAndSelectsThePdk_withoutTriggeringTheNewPdkSentinel()
    {
        var (vm, _, rawCode) = BuildWithSeededPdk();
        var createNewPdkCalls = 0;
        vm.CreateNewPdk = () => { createNewPdkCalls++; return Task.FromResult<UserPdkInfo?>(null); };

        vm.LoadForEdit(BuildTemplate(rawCode));

        vm.IsEditMode.ShouldBeTrue();
        vm.ComponentName.ShouldBe("comp1");
        vm.Code.ShouldBe(rawCode);
        vm.SelectedBackend.ShouldBe(GeometryBackend.GdsFactory);
        vm.SelectedCustomPdk.ShouldNotBeNull();
        vm.SelectedCustomPdk!.Name.ShouldBe("Lib");
        createNewPdkCalls.ShouldBe(0);
    }

    [Fact]
    public void LoadForEdit_nazcaFunctionReference_synthesizesValidDefComponent()
    {
        var (vm, _, _) = BuildWithSeededPdk();
        var template = new ComponentTemplate
        {
            Name = "2x2 MMI", PdkSource = "Lib",
            NazcaFunctionName = "demo.mmi2x2_dp", RawCodeBackend = "nazca", RawCode = null,
        };

        vm.LoadForEdit(template);

        vm.SelectedBackend.ShouldBe(GeometryBackend.Nazca);
        vm.Code.ShouldContain("def component():");
        vm.Code.ShouldContain("nazca.demofab");
        vm.Code.ShouldContain("mmi2x2_dp");
        vm.Code.ShouldNotContain("component = demo");
    }

    [Fact]
    public async Task EditSave_renameWithinSamePdk_removesTheOriginal_noOrphan()
    {
        var store = Store();
        var process = new ProcessDefinition { Name = "SiN 300" };
        const string rawCode = "import gdsfactory as gf\ncomponent = gf.components.coupler()";
        var path = store.CreateNamedPdkWithProcess("Lib", process, "gdsfactory", null);
        store.AppendToExistingPdk(path, SeedComponent("comp1", rawCode));
        var (vm, _) = Build(store, new List<ProcessDefinition> { process });

        vm.LoadForEdit(BuildTemplate(rawCode));
        vm.ComponentName = "comp1Renamed";
        await vm.SaveCommand.ExecuteAsync(null);

        store.ComponentExistsInFile(path, "comp1Renamed").ShouldBeTrue();
        store.ComponentExistsInFile(path, "comp1").ShouldBeFalse();
    }

    [Fact]
    public void WindowTitle_and_SaveButtonLabel_reflectEditMode()
    {
        var (vm, _, rawCode) = BuildWithSeededPdk();

        vm.WindowTitle.ShouldBe("New Component");
        vm.SaveButtonLabel.ShouldBe("Save");

        vm.LoadForEdit(BuildTemplate(rawCode));

        vm.WindowTitle.ShouldBe("Edit Component: comp1");
        vm.SaveButtonLabel.ShouldBe("Save changes");
    }

    [Fact]
    public void WindowTitle_afterLoadForEdit_includesTheTemplatesComponentName()
    {
        var store = Store();
        var process = new ProcessDefinition { Name = "SiN 300" };
        const string rawCode = "import gdsfactory as gf\ncomponent = gf.components.coupler()";
        var path = store.CreateNamedPdkWithProcess("Lib", process, "gdsfactory", null);
        store.AppendToExistingPdk(path, SeedComponent("test3", rawCode));
        var (vm, _) = Build(store, new List<ProcessDefinition> { process });
        var template = new ComponentTemplate
        {
            Name = "test3",
            RawCode = rawCode,
            RawCodeBackend = "gdsfactory",
            PdkSource = "Lib",
        };

        vm.LoadForEdit(template);

        vm.WindowTitle.ShouldBe("Edit Component: test3");
    }

    [Fact]
    public void LoadForEdit_raisesPropertyChanged_forWindowTitle_soABoundTitleBarRefreshes()
    {
        var (vm, _, rawCode) = BuildWithSeededPdk();
        var raisedWindowTitle = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.WindowTitle)) raisedWindowTitle = true;
        };

        vm.LoadForEdit(BuildTemplate(rawCode));

        raisedWindowTitle.ShouldBeTrue();
        vm.WindowTitle.ShouldBe("Edit Component: comp1");
    }

    [Fact]
    public void LoadForEdit_exposesTheEditIdentity_forWindowDedup()
    {
        var (vm, filePath, rawCode) = BuildWithSeededPdk();

        vm.EditOriginalPdkKey.ShouldBeNull();
        vm.EditingOriginalName.ShouldBeNull();

        vm.LoadForEdit(BuildTemplate(rawCode));

        vm.EditOriginalPdkKey.ShouldBe(filePath);
        vm.EditingOriginalName.ShouldBe("comp1");
    }

    [Fact]
    public async Task Save_afterLoadForEdit_overwritesTheOriginalComponent_inPlace_notDuplicated()
    {
        var (vm, filePath, rawCode) = BuildWithSeededPdk();
        vm.LoadForEdit(BuildTemplate(rawCode));
        vm.ConfirmOverwrite = (_, _) => Task.FromResult(true);

        await vm.RunPreviewCommand.ExecuteAsync(null);
        await vm.SaveCommand.ExecuteAsync(null);

        vm.SavedDraft.ShouldNotBeNull();
        var pdk = new PdkLoader().LoadFromFileForEditing(filePath);
        pdk.Components.Count(c => c.Name == "comp1").ShouldBe(1);
        pdk.Components.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Save_afterLoadForEdit_neverCallsConfirmOverwrite_evenThoughTheNameAlwaysExists()
    {
        var (vm, _, rawCode) = BuildWithSeededPdk();
        vm.LoadForEdit(BuildTemplate(rawCode));
        var confirmCalls = 0;
        vm.ConfirmOverwrite = (_, _) => { confirmCalls++; return Task.FromResult(true); };

        await vm.RunPreviewCommand.ExecuteAsync(null);
        await vm.SaveCommand.ExecuteAsync(null);

        confirmCalls.ShouldBe(0);
        vm.SavedDraft.ShouldNotBeNull();
    }

    [Fact]
    public async Task Save_afterLoadForEdit_renamedOntoADifferentExistingComponent_stillPromptsAndAbortsWhenDeclined()
    {
        var (vm, filePath, rawCode) = BuildWithTwoSeededComponents();
        vm.LoadForEdit(BuildTemplate(rawCode));
        var confirmCalls = 0;
        vm.ConfirmOverwrite = (_, _) => { confirmCalls++; return Task.FromResult(false); };
        vm.ComponentName = "comp2";

        await vm.RunPreviewCommand.ExecuteAsync(null);
        await vm.SaveCommand.ExecuteAsync(null);

        confirmCalls.ShouldBe(1);
        vm.SavedDraft.ShouldBeNull();
        var pdk = new PdkLoader().LoadFromFileForEditing(filePath);
        pdk.Components.Count.ShouldBe(2);
        pdk.Components.Count(c => c.Name == "comp1").ShouldBe(1);
        pdk.Components.Count(c => c.Name == "comp2").ShouldBe(1);
    }

    [Fact]
    public void LoadForEdit_withNoMatchingCustomPdk_reportsStatusAndLeavesEditModeFalse()
    {
        var (vm, _, rawCode) = BuildWithSeededPdk();
        var template = BuildTemplate(rawCode);
        template.PdkSource = "Unknown Pdk";

        var loaded = vm.LoadForEdit(template);

        loaded.ShouldBeFalse();
        vm.IsEditMode.ShouldBeFalse();
        vm.StatusText.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void LoadForEdit_processAgnosticImportPdk_opensEditor_insteadOfRefusingAsNotCustom()
    {
        // Regression: GDS-import PDKs are process-agnostic user PDKs — the edit gate
        // wrongly refused them as "not a custom PDK" because they declare no process.
        var store = Store();
        const string rawCode = "import gdsfactory as gf\ncomponent = gf.components.coupler()";
        var pdkPath = SeedProcessAgnosticPdk(store, "GDS Import - demo", SeedComponent("comp1", rawCode));
        var (vm, _) = Build(store, new List<ProcessDefinition>());
        var template = BuildTemplate(rawCode);
        template.PdkSource = "GDS Import - demo";

        vm.LoadForEdit(template).ShouldBeTrue();

        vm.IsEditMode.ShouldBeTrue();
        vm.SelectedCustomPdk.ShouldNotBeNull();
        vm.SelectedCustomPdk!.FilePath.ShouldBe(pdkPath);
        vm.SelectedProcess.ShouldBeNull("a process-agnostic PDK declares no fabrication process");
        vm.StatusText.ShouldNotContain("not a custom PDK");
    }

    [Fact]
    public async Task Save_afterLoadForEdit_processAgnosticImportPdk_savesInPlace_keepingItProcessAgnostic()
    {
        var store = Store();
        const string rawCode = "import gdsfactory as gf\ncomponent = gf.components.coupler()";
        var pdkPath = SeedProcessAgnosticPdk(store, "GDS Import - demo", SeedComponent("comp1", rawCode));
        var (vm, _) = Build(store, new List<ProcessDefinition>());
        var template = BuildTemplate(rawCode);
        template.PdkSource = "GDS Import - demo";
        vm.LoadForEdit(template).ShouldBeTrue();

        await vm.RunPreviewCommand.ExecuteAsync(null);
        await vm.SaveCommand.ExecuteAsync(null);

        vm.SavedDraft.ShouldNotBeNull();
        vm.SavedFilePath.ShouldBe(pdkPath);
        var pdk = new PdkLoader().LoadFromFileForEditing(pdkPath);
        pdk.ProcessAgnostic.ShouldBeTrue();
        pdk.Process.ShouldBeNull();
        pdk.Components.ShouldHaveSingleItem().Name.ShouldBe("comp1");
    }

    [Fact]
    public void LoadForEdit_returnsTrue_onSuccess()
    {
        var (vm, _, rawCode) = BuildWithSeededPdk();

        vm.LoadForEdit(BuildTemplate(rawCode)).ShouldBeTrue();
    }

    [Fact]
    public void HasUnsavedEditChanges_falseAfterLoadForEdit_trueAfterUserEdits()
    {
        var (vm, _, rawCode) = BuildWithSeededPdk();
        vm.LoadForEdit(BuildTemplate(rawCode));

        vm.HasUnsavedEditChanges.ShouldBeFalse();

        vm.Code = rawCode + "\n# tweak";
        vm.HasUnsavedEditChanges.ShouldBeTrue();

        vm.Code = rawCode;
        vm.HasUnsavedEditChanges.ShouldBeFalse();

        vm.ComponentName = "renamed";
        vm.HasUnsavedEditChanges.ShouldBeTrue();
    }

    [Fact]
    public void HasUnsavedEditChanges_isFalse_outsideEditMode()
    {
        var (vm, _, _) = BuildWithSeededPdk();
        vm.Code = "anything";

        vm.HasUnsavedEditChanges.ShouldBeFalse();
    }

    [Fact]
    public void HasUnsavedEditChanges_tracksTheGeometryBackend()
    {
        // A backend switch is user input like any other — the
        // dedup's "no unsaved changes → adopt fresh state" guard must never throw it away.
        var (vm, _, rawCode) = BuildWithSeededPdk();
        vm.LoadForEdit(BuildTemplate(rawCode));

        vm.HasUnsavedEditChanges.ShouldBeFalse();

        vm.SelectedBackend = GeometryBackend.Nazca;
        vm.HasUnsavedEditChanges.ShouldBeTrue("switching the geometry backend is an unsaved user change");

        vm.SelectedBackend = GeometryBackend.GdsFactory;
        vm.HasUnsavedEditChanges.ShouldBeFalse();
    }

    [Fact]
    public void HasUnsavedEditChanges_tracksTheSelectedTargetPdk()
    {
        var store = Store();
        var process = new ProcessDefinition { Name = "SiN 300" };
        const string rawCode = "import gdsfactory as gf\ncomponent = gf.components.coupler()";
        var path = store.CreateNamedPdkWithProcess("Lib", process, "gdsfactory", null);
        store.AppendToExistingPdk(path, SeedComponent("comp1", rawCode));
        store.CreateNamedPdkWithProcess("Other Lib", process, "gdsfactory", null);
        var (vm, _) = Build(store, new List<ProcessDefinition> { process });
        vm.LoadForEdit(BuildTemplate(rawCode));

        vm.HasUnsavedEditChanges.ShouldBeFalse();

        vm.SelectedPdkChoice = vm.PdkChoices.First(c => !c.IsNewPdk && c.Pdk!.Name == "Other Lib");
        vm.HasUnsavedEditChanges.ShouldBeTrue("re-targeting the save to another PDK is an unsaved user change");
    }

    [Fact]
    public async Task Save_whenTheStoreThrows_reportsTheError_insteadOfSilentlySwallowingIt()
    {
        // A store failure (e.g. the target file vanished after a fork revert) must not fault
        // the AsyncRelayCommand silently — the Save button would look like it did nothing.
        var (vm, filePath, rawCode) = BuildWithSeededPdk();
        vm.LoadForEdit(BuildTemplate(rawCode));
        vm.ConfirmOverwrite = (_, _) => Task.FromResult(true);
        await vm.RunPreviewCommand.ExecuteAsync(null);
        File.Delete(filePath); // the save target disappears underneath the open editor

        await Should.NotThrowAsync(async () => await vm.SaveCommand.ExecuteAsync(null));

        vm.SavedDraft.ShouldBeNull();
        vm.StatusText.ShouldContain("Save failed");
        vm.IsBusy.ShouldBeFalse();
    }

    [Fact]
    public void LoadForEdit_foundryComponentWithoutRawCode_synthesizesPdkRegistryCode_notModuleAttributeCall()
    {
        // Field bug: the synthesized editor code for a CornerStone component was
        // "import cspdk\ncomponent = cspdk.sin300.coupler_straight()", which fails twice —
        // "import cspdk" doesn't load the sin300 submodule (AttributeError: module 'cspdk'
        // has no attribute 'sin300'), and cspdk cells are registered in the PDK registry,
        // not as module attributes. The synthesis must use the exporter/preview pattern.
        var (vm, _, _) = BuildWithSeededPdk();
        var template = BuildTemplate(null!);
        template.RawCode = null;
        template.GdsFactoryFunction = "cspdk.sin300.coupler_straight";

        vm.LoadForEdit(template);

        vm.SelectedBackend.ShouldBe(GeometryBackend.GdsFactory);
        vm.Code.ShouldContain("import cspdk.sin300");
        vm.Code.ShouldContain("cspdk.sin300.PDK.activate()");
        vm.Code.ShouldContain("gf.get_component('coupler_straight')");
        vm.Code.ShouldNotContain("cspdk.sin300.coupler_straight()");
    }

    [Fact]
    public void LoadForEdit_siepicComponentWithoutRawCode_synthesizesUbcPdkRegistryCode_notSiepicAttributeCall()
    {
        // Field bug round 6: "Edit Component" on the SiEPIC "Adiabatic Coupler TE 1550"
        // synthesized "import siepic_ebeam_pdk ... siepic_ebeam_pdk.ebeam_adiabatic_te1550()",
        // failing with "module 'siepic_ebeam_pdk' has no attribute 'ebeam_adiabatic_te1550'".
        // SiEPIC bundled components carry no GdsFactoryFunction (the round-4 fix never applied);
        // their cells resolve via the ubcpdk gdsfactory registry.
        var (vm, _, _) = BuildWithSeededPdk();
        var template = new ComponentTemplate
        {
            Name = "Adiabatic Coupler TE 1550", PdkSource = "Lib",
            NazcaModuleName = "siepic_ebeam_pdk", NazcaFunctionName = "ebeam_adiabatic_te1550",
            RawCode = null,
        };

        vm.LoadForEdit(template);

        vm.SelectedBackend.ShouldBe(GeometryBackend.GdsFactory);
        vm.Code.ShouldContain("import ubcpdk");
        vm.Code.ShouldContain("ubcpdk.PDK.activate()");
        vm.Code.ShouldContain("gf.get_component('ebeam_adiabatic_te1550')");
        vm.Code.ShouldNotContain("siepic_ebeam_pdk");
    }

    [Fact]
    public void LoadForEdit_siepicKlayoutOnlyCell_opensWithExplanatoryNote_insteadOfBrokenAttributeCode()
    {
        // The few SiEPIC cells without a ubcpdk registry equivalent are KLayout fixed cells —
        // there is no runnable editor code for them. An explanatory note plus the "no stored
        // code" status is honest; synthesized attribute code would only reproduce the AttributeError.
        var (vm, _, _) = BuildWithSeededPdk();
        var template = new ComponentTemplate
        {
            Name = "Contra-Directional Coupler", PdkSource = "Lib",
            NazcaModuleName = "siepic_ebeam_pdk", NazcaFunctionName = "contra_directional_coupler",
            RawCode = null,
        };

        vm.LoadForEdit(template);

        vm.Code.ShouldBe(LocalizationService.Instance.Translate("NewComponent.KLayoutOnlyCellNote"));
        vm.StatusText.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void LoadForEdit_foundryComponentWithBareCellName_resolvesViaGetComponent()
    {
        // A bare (dotless) gdsfactory cell has no PDK module to import/activate —
        // resolve it against the render script's default PDK instead of emitting
        // the nonsensical "import straight".
        var (vm, _, _) = BuildWithSeededPdk();
        var template = BuildTemplate(null!);
        template.RawCode = null;
        template.GdsFactoryFunction = "straight";

        vm.LoadForEdit(template);

        vm.Code.ShouldContain("gf.get_component('straight')");
        vm.Code.ShouldNotContain("import straight");
    }

    [Fact]
    public async Task RunPreview_missingFoundryPackage_showsActionableHint_andLogsRawErrorToConsole()
    {
        // A raw "ModuleNotFoundError: No module named 'cspdk'" from the render subprocess
        // is not actionable for the user — the status bar must point at the Python
        // Environments settings while the raw error goes to the Error Console.
        var store = Store();
        var process = new ProcessDefinition { Name = "SiN 300" };
        store.CreateNamedPdkWithProcess("Lib", process, "gdsfactory", null);
        var nazca = new Mock<IComponentPreviewRenderer>();
        var gds = new Mock<IComponentPreviewRenderer>();
        gds.Setup(g => g.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(NazcaPreviewResult.Fail("No module named 'cspdk'"));
        var extractor = new ComponentGeometryExtractor(nazca.Object, gds.Object);
        var errorConsole = new CAP_Core.ErrorConsoleService();
        var vm = new NewComponentViewModel(extractor, fdtd: null, store,
            new List<ProcessDefinition> { process }, errorConsole);
        vm.Code = "import cspdk.sin300\ncomponent = gf.get_component('coupler_straight')";

        await vm.RunPreviewCommand.ExecuteAsync(null);

        vm.StatusText.ShouldContain("cspdk");
        vm.StatusText.ShouldContain("Settings → Python Environments");
        errorConsole.Entries.ShouldContain(e => e.Message.Contains("No module named 'cspdk'"));
    }

    [Fact]
    public async Task RunPreview_recognisedFoundryError_consoleCarriesTheWindowMessagePlusRawDetail()
    {
        // Field wish (round 6): window status and Error Console must tell ONE story.
        // The window shows the actionable hint; the console carries the SAME message
        // plus the raw Python detail — not a different, raw-only text.
        var store = Store();
        var process = new ProcessDefinition { Name = "SiN 300" };
        store.CreateNamedPdkWithProcess("Lib", process, "gdsfactory", null);
        var nazca = new Mock<IComponentPreviewRenderer>();
        var gds = new Mock<IComponentPreviewRenderer>();
        gds.Setup(g => g.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(NazcaPreviewResult.Fail(
               "module 'siepic_ebeam_pdk' has no attribute 'ebeam_adiabatic_te1550'"));
        var errorConsole = new CAP_Core.ErrorConsoleService();
        var vm = new NewComponentViewModel(
            new ComponentGeometryExtractor(nazca.Object, gds.Object), fdtd: null, store,
            new List<ProcessDefinition> { process }, errorConsole);
        vm.Code = "component = gf.get_component('ebeam_adiabatic_te1550')";

        await vm.RunPreviewCommand.ExecuteAsync(null);

        var entry = errorConsole.Entries.ShouldHaveSingleItem();
        entry.Message.ShouldContain(vm.StatusText);   // same message as the window...
        entry.Message.ShouldContain(
            "module 'siepic_ebeam_pdk' has no attribute 'ebeam_adiabatic_te1550'"); // ...plus raw detail
    }

    [Fact]
    public async Task RunPreview_unrecognisedError_logsTheSameMessageToWindowAndConsole()
    {
        // Unrecognised errors previously reached only the status bar; the console stayed
        // silent, so window and console diverged. One source: both carry the same text.
        var store = Store();
        var process = new ProcessDefinition { Name = "SiN 300" };
        store.CreateNamedPdkWithProcess("Lib", process, "gdsfactory", null);
        var nazca = new Mock<IComponentPreviewRenderer>();
        var gds = new Mock<IComponentPreviewRenderer>();
        gds.Setup(g => g.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(NazcaPreviewResult.Fail("SyntaxError: invalid syntax (line 3)"));
        var errorConsole = new CAP_Core.ErrorConsoleService();
        var vm = new NewComponentViewModel(
            new ComponentGeometryExtractor(nazca.Object, gds.Object), fdtd: null, store,
            new List<ProcessDefinition> { process }, errorConsole);
        vm.Code = "def component(:";

        await vm.RunPreviewCommand.ExecuteAsync(null);

        vm.StatusText.ShouldBe("SyntaxError: invalid syntax (line 3)");
        var entry = errorConsole.Entries.ShouldHaveSingleItem();
        entry.Message.ShouldContain("SyntaxError: invalid syntax (line 3)");
    }

    [Fact]
    public void LoadForEdit_withNoStoredCode_showsExplanatoryNote_andReportsStatus()
    {
        var (vm, _, _) = BuildWithSeededPdk();
        var template = BuildTemplate(null!);
        template.RawCode = null;

        vm.LoadForEdit(template);

        // No KLayout cell here — the generic "nothing editable stored" note applies.
        vm.Code.ShouldBe(LocalizationService.Instance.Translate("NewComponent.NoEditableCodeNote"));
        vm.StatusText.ShouldNotBeNullOrWhiteSpace();
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
