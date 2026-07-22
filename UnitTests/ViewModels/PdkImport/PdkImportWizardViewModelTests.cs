using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.PdkImport;
using CAP_Core.Export;
using Shouldly;
using Xunit;

namespace UnitTests.ViewModels.PdkImport;

/// <summary>
/// Unit tests for the PDK Import Wizard ViewModel and supporting types.
/// Issue #476: PDK Import Wizard with Python/Nazca parser and AI-assisted error correction.
/// </summary>
public class PdkImportWizardViewModelTests
{
    public PdkImportWizardViewModelTests()
    {
        // StatusText is localized via LocalizationService.Instance, so pin English to keep the
        // substring assertions below culture-independent regardless of the CI/dev OS language
        // (the only test that live-switches the shared instance is isolated in the
        // "LocalizationSingleton" collection, which disables cross-collection parallelization).
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
    }

    // ── ComponentParseResultViewModel ────────────────────────────────────────

    [Fact]
    public void ComponentParseResult_WithPins_ShowsGreenStatus()
    {
        var geometry = MakeGeometry("ebeam_y_1550", pinCount: 3);
        var vm = new ComponentParseResultViewModel(geometry);

        vm.StatusText.ShouldContain("3 pin");
        vm.StatusColor.ShouldBe("#4CAF50");
        vm.HasWarnings.ShouldBeFalse();
        vm.IsSelected.ShouldBeTrue();
    }

    [Fact]
    public void ComponentParseResult_WithNoPins_ShowsWarning()
    {
        var geometry = MakeGeometry("bare_wg", pinCount: 0);
        var vm = new ComponentParseResultViewModel(geometry);

        vm.StatusText.ShouldContain("No pins");
        vm.StatusColor.ShouldBe("#FF9800");
        vm.HasWarnings.ShouldBeTrue();
    }

    [Fact]
    public void ComponentParseResult_DimensionsText_FormattedCorrectly()
    {
        var geometry = MakeGeometry("comp", pinCount: 1);
        geometry.WidthMicrometers = 10.5;
        geometry.HeightMicrometers = 25.3;
        var vm = new ComponentParseResultViewModel(geometry);

        vm.DimensionsText.ShouldContain("10.5");
        vm.DimensionsText.ShouldContain("25.3");
        vm.DimensionsText.ShouldContain("µm");
    }

    [Fact]
    public void ComponentParseResult_IsSelected_DefaultsToTrue()
    {
        var geometry = MakeGeometry("comp", pinCount: 2);
        var vm = new ComponentParseResultViewModel(geometry);
        vm.IsSelected.ShouldBeTrue();
    }

    [Fact]
    public void ComponentParseResult_NullGeometry_ThrowsArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() => new ComponentParseResultViewModel(null!));
    }

    // ── PdkImportWizardViewModel ─────────────────────────────────────────────

    [Fact]
    public void WizardViewModel_InitialState_IsParsingStep()
    {
        var vm = MakeWizardVm("test.py");

        vm.CurrentStep.ShouldBe(WizardStep.Parsing);
        vm.IsParsingStep.ShouldBeTrue();
        vm.IsReviewStep.ShouldBeFalse();
        vm.IsSaveStep.ShouldBeFalse();
    }

    [Fact]
    public void WizardViewModel_DefaultPdkName_IsFilenameWithoutExtension()
    {
        var vm = MakeWizardVm("/some/path/my_module.py");
        vm.PdkName.ShouldBe("my_module");
    }

    [Fact]
    public void WizardViewModel_DefaultOutputPath_IsJsonExtension()
    {
        var vm = MakeWizardVm("/some/path/my_module.py");
        vm.OutputPath.ShouldEndWith(".json");
        vm.OutputPath.ShouldNotEndWith(".py");
    }

    [Fact]
    public void WizardViewModel_ProceedToSave_AdvancesToSaveStep()
    {
        var vm = MakeWizardVm("test.py");
        vm.ProceedToSaveCommand.Execute(null);

        vm.CurrentStep.ShouldBe(WizardStep.Save);
        vm.IsSaveStep.ShouldBeTrue();
        vm.IsReviewStep.ShouldBeFalse();
    }

    [Fact]
    public void WizardViewModel_BackToReview_ReturnsToReviewStep()
    {
        var vm = MakeWizardVm("test.py");
        vm.ProceedToSaveCommand.Execute(null); // → Save
        vm.BackToReviewCommand.Execute(null);  // ← Review

        vm.CurrentStep.ShouldBe(WizardStep.Review);
        vm.IsReviewStep.ShouldBeTrue();
        vm.IsSaveStep.ShouldBeFalse();
    }

    [Fact]
    public void WizardViewModel_Cancel_InvokesCancelledCallback()
    {
        var vm = MakeWizardVm("test.py");
        var cancelled = false;
        vm.OnCancelled = () => cancelled = true;

        vm.CancelCommand.Execute(null);

        cancelled.ShouldBeTrue();
    }

    [Fact]
    public void WizardViewModel_NullFilePath_ThrowsArgumentNullException()
    {
        var prefs = new UserPreferencesService(Path.GetTempFileName());
        var importService = new PdkImportService(prefs);
        Should.Throw<ArgumentNullException>(() => new PdkImportWizardViewModel(null!, importService));
    }

    [Fact]
    public void WizardViewModel_NullImportService_ThrowsArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() => new PdkImportWizardViewModel("test.py", null!));
    }

    // ── PdkImportService ─────────────────────────────────────────────────────

    [Fact]
    public void PdkImportService_ConvertToPdkDraft_MapsAllComponents()
    {
        var prefs = new UserPreferencesService(Path.GetTempFileName());
        var service = new PdkImportService(prefs);

        var parseResult = new PdkParseResult
        {
            Name = "Test PDK",
            NazcaModuleName = "test_pdk",
            DefaultWavelengthNm = 1550,
            Components = new List<ParsedComponentGeometry>
            {
                MakeGeometry("comp_a", pinCount: 2),
                MakeGeometry("comp_b", pinCount: 3),
            }
        };

        var draft = service.ConvertToPdkDraft(parseResult);

        draft.Name.ShouldBe("Test PDK");
        draft.NazcaModuleName.ShouldBe("test_pdk");
        draft.Components.Count.ShouldBe(2);
        draft.Components[0].Name.ShouldBe("comp_a");
        draft.Components[1].Name.ShouldBe("comp_b");
    }

    [Fact]
    public void PdkImportService_ConvertToPdkDraft_UsesFallbackNameWhenEmpty()
    {
        var prefs = new UserPreferencesService(Path.GetTempFileName());
        var service = new PdkImportService(prefs);

        var parseResult = new PdkParseResult
        {
            Name = "",
            NazcaModuleName = "my_pdk",
        };

        var draft = service.ConvertToPdkDraft(parseResult);

        draft.Name.ShouldBe("my_pdk");
    }

    [Fact]
    public void PdkImportService_ConvertToPdkDraft_MapsPinsCorrectly()
    {
        var prefs = new UserPreferencesService(Path.GetTempFileName());
        var service = new PdkImportService(prefs);

        var geometry = new ParsedComponentGeometry
        {
            Name = "test_comp",
            Category = "Couplers",
            NazcaFunction = "test_comp",
            WidthMicrometers = 10,
            HeightMicrometers = 5,
            Pins = new List<ParsedPinGeometry>
            {
                new ParsedPinGeometry { Name = "a0", OffsetXMicrometers = 0, OffsetYMicrometers = 2.5, AngleDegrees = 180 },
                new ParsedPinGeometry { Name = "b0", OffsetXMicrometers = 10, OffsetYMicrometers = 2.5, AngleDegrees = 0 },
            }
        };

        var draft = service.ConvertToPdkDraft(new PdkParseResult
        {
            Name = "t",
            Components = new List<ParsedComponentGeometry> { geometry }
        });

        var comp = draft.Components[0];
        comp.Pins.Count.ShouldBe(2);
        comp.Pins[0].Name.ShouldBe("a0");
        comp.Pins[0].AngleDegrees.ShouldBe(180);
        comp.Pins[1].Name.ShouldBe("b0");
        comp.Pins[1].AngleDegrees.ShouldBe(0);
    }

    [Fact]
    public async Task PdkImportService_SaveToJsonAsync_WritesValidJson()
    {
        var prefs = new UserPreferencesService(Path.GetTempFileName());
        var service = new PdkImportService(prefs);
        var outputPath = Path.Combine(Path.GetTempPath(), $"pdk_test_{Guid.NewGuid()}.json");

        try
        {
            var draft = service.ConvertToPdkDraft(new PdkParseResult
            {
                Name = "Save Test PDK",
                NazcaModuleName = "save_test",
                Components = new List<ParsedComponentGeometry>
                {
                    MakeGeometry("test_comp", pinCount: 1),
                }
            });

            await service.SaveToJsonAsync(draft, outputPath);

            File.Exists(outputPath).ShouldBeTrue();
            var content = await File.ReadAllTextAsync(outputPath);
            content.ShouldContain("\"name\"");
            content.ShouldContain("Save Test PDK");
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    // ── FilterToSelected (real internal method) ──────────────────────────────

    [Fact]
    public void FilterToSelected_ExcludesDeselectedComponents()
    {
        var vm = MakeWizardVm("test.py");
        var g1 = MakeGeometry("comp_a", pinCount: 2);
        var g2 = MakeGeometry("comp_b", pinCount: 0);
        vm.ParsedComponents.Add(new ComponentParseResultViewModel(g1));
        vm.ParsedComponents.Add(new ComponentParseResultViewModel(g2) { IsSelected = false });

        var result = new PdkParseResult { Components = new() { g1, g2 } };
        var filtered = vm.FilterToSelected(result);

        filtered.Components.Select(c => c.Name).ShouldBe(new[] { "comp_a" });
    }

    [Fact]
    public void FilterToSelected_WithDuplicateNames_RespectsIndividualSelection()
    {
        // Regression: parametric Nazca cells can emit two components with the
        // same Name. A name-based HashSet filter would either include both
        // when one is unchecked, or exclude both when one is checked. The
        // filter must key on positional identity instead.
        var vm = MakeWizardVm("test.py");
        var g1 = MakeGeometry("dup", pinCount: 2);
        var g2 = MakeGeometry("dup", pinCount: 2);
        vm.ParsedComponents.Add(new ComponentParseResultViewModel(g1) { IsSelected = true });
        vm.ParsedComponents.Add(new ComponentParseResultViewModel(g2) { IsSelected = false });

        var result = new PdkParseResult { Components = new() { g1, g2 } };
        var filtered = vm.FilterToSelected(result);

        filtered.Components.Count.ShouldBe(1);
        filtered.Components[0].ShouldBeSameAs(g1);
    }

    // ── SaveAndLoad guards ───────────────────────────────────────────────────

    [Fact]
    public async Task SaveAndLoad_BeforeParseCompletes_IsNoOpWithStatus()
    {
        var vm = MakeWizardVm("test.py");
        vm.OutputPath = Path.GetTempFileName();
        bool completed = false;
        vm.OnCompleted = _ => completed = true;

        await vm.SaveAndLoadCommand.ExecuteAsync(null);

        completed.ShouldBeFalse();
        vm.StatusText.ShouldContain("Parsing");
    }

    [Fact]
    public async Task SaveAndLoad_WithBlankOutputPath_IsNoOpWithStatus()
    {
        var vm = MakeWizardVm("test.py");
        vm.ParseResultForTesting = new PdkParseResult();
        vm.OutputPath = "";
        bool completed = false;
        vm.OnCompleted = _ => completed = true;

        await vm.SaveAndLoadCommand.ExecuteAsync(null);

        completed.ShouldBeFalse();
        vm.StatusText.ShouldContain("output path");
    }

    [Fact]
    public async Task SaveAndLoad_WithNoComponentsSelected_IsNoOpWithStatus()
    {
        var vm = MakeWizardVm("test.py");
        vm.ParseResultForTesting = new PdkParseResult();
        vm.OutputPath = Path.GetTempFileName();
        var g = MakeGeometry("only", pinCount: 1);
        vm.ParsedComponents.Add(new ComponentParseResultViewModel(g) { IsSelected = false });
        bool completed = false;
        vm.OnCompleted = _ => completed = true;

        await vm.SaveAndLoadCommand.ExecuteAsync(null);

        completed.ShouldBeFalse();
        vm.StatusText.ShouldContain("at least one component");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ParsedComponentGeometry MakeGeometry(string name, int pinCount)
    {
        var geometry = new ParsedComponentGeometry
        {
            Name = name,
            Category = "Test",
            NazcaFunction = name,
            WidthMicrometers = 10,
            HeightMicrometers = 5,
        };
        for (int i = 0; i < pinCount; i++)
        {
            geometry.Pins.Add(new ParsedPinGeometry
            {
                Name = $"pin{i}",
                OffsetXMicrometers = i * 2.0,
                OffsetYMicrometers = 2.5,
                AngleDegrees = i == 0 ? 180 : 0,
            });
        }
        return geometry;
    }

    private static PdkImportWizardViewModel MakeWizardVm(string pyFilePath)
    {
        var prefs = new UserPreferencesService(Path.GetTempFileName());
        var importService = new PdkImportService(prefs);
        return new PdkImportWizardViewModel(pyFilePath, importService);
    }
}
