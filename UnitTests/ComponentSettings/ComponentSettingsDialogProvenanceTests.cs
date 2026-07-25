using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.ComponentSettings;
using CAP.Avalonia.ViewModels.Library;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using CAP_DataAccess.Persistence.PIR;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.ComponentSettings;

public class ComponentSettingsDialogProvenanceTests
{
    public ComponentSettingsDialogProvenanceTests() =>
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);

    private static PdkComponentDraft DraftWithProvenance(string? sourceNote) => new()
    {
        Name = "My Comp",
        Category = "Test",
        RawCode = "import gdsfactory as gf\ncomponent = gf.components.coupler()",
        RawCodeBackend = "gdsfactory",
        WidthMicrometers = 10,
        HeightMicrometers = 2,
        Pins = new() { new PhysicalPinDraft { Name = "o1" }, new PhysicalPinDraft { Name = "o2" } },
        SMatrix = new PdkSMatrixDraft
        {
            WavelengthNm = 1550,
            SourceNote = sourceNote,
            WavelengthData = new List<WavelengthSMatrixEntry>
            {
                new()
                {
                    WavelengthNm = 1550,
                    Connections = new List<SMatrixConnection>
                    {
                        new() { FromPin = "o1", ToPin = "o2", Magnitude = 0.95, PhaseDegrees = 0 },
                    }
                }
            }
        }
    };

    private static ComponentTemplate TemplateFor(PdkComponentDraft draft) =>
        CAP.Avalonia.Services.PdkTemplateConverter.ConvertToTemplate(draft, "User PDK", null);

    private static ComponentSettingsDialogViewModel NewDialog(
        ComponentTemplate template,
        Dictionary<string, ComponentSMatrixData> store,
        Func<Task<ComponentTemplate?>>? resetToPdkOriginal = null)
    {
        var instance = ComponentTemplates.CreateFromTemplate(template, 0, 0);
        var vm = new ComponentSettingsDialogViewModel(
            Mock.Of<IFileDialogService>(),
            resetToPdkOriginal: resetToPdkOriginal);
        vm.Configure("key", "key", "My Comp", store,
            liveComponent: null,
            isUserGlobalScope: true,
            effectiveSMatrices: instance.WaveLengthToSMatrixMap,
            effectivePins: instance.PhysicalPins
                .Where(pp => pp.LogicalPin != null)
                .Select(pp => pp.LogicalPin!)
                .ToList(),
            template: template);
        return vm;
    }

    [Fact]
    public void UserSourcedDraft_showsItsProvenanceUnderTheEffectiveSection()
    {
        var vm = NewDialog(TemplateFor(DraftWithProvenance("FDTD Tidy3D Cloud 2D")),
            new Dictionary<string, ComponentSMatrixData>());

        vm.EffectiveProvenanceText.ShouldContain("FDTD Tidy3D Cloud 2D");
    }

    [Fact]
    public void BundledOriginalDraft_showsNoProvenance()
    {
        var vm = NewDialog(TemplateFor(DraftWithProvenance(null)),
            new Dictionary<string, ComponentSMatrixData>());

        vm.EffectiveProvenanceText.ShouldBeEmpty();
        vm.CanResetToPdkOriginal.ShouldBeFalse();
    }

    [Fact]
    public void OverrideNote_winsOverDraftNote()
    {
        var store = new Dictionary<string, ComponentSMatrixData>
        {
            ["key"] = new()
            {
                SourceNote = "FDTD Meep 2D",
                Wavelengths = new()
                {
                    ["1550"] = new SMatrixWavelengthEntry { Rows = 2, Cols = 2 }
                }
            }
        };
        var vm = NewDialog(TemplateFor(DraftWithProvenance("FDTD Tidy3D Cloud 2D")), store);

        vm.EffectiveProvenanceText.ShouldContain("FDTD Meep 2D");
    }

    [Fact]
    public void ResetButton_onlyAppears_forUserSourcedDraftsWithAWiredRoute()
    {
        var draft = DraftWithProvenance("FDTD Tidy3D Cloud 2D");

        NewDialog(TemplateFor(draft), new Dictionary<string, ComponentSMatrixData>())
            .CanResetToPdkOriginal.ShouldBeFalse("no reset route injected");

        NewDialog(TemplateFor(draft), new Dictionary<string, ComponentSMatrixData>(),
                resetToPdkOriginal: () => Task.FromResult<ComponentTemplate?>(null))
            .CanResetToPdkOriginal.ShouldBeTrue();
    }

    [Fact]
    public async Task ResetToPdkOriginal_restoresTheOriginal_andClearsTheProvenance()
    {
        var restored = TemplateFor(DraftWithProvenance(null));
        var resetCalled = false;
        var vm = NewDialog(
            TemplateFor(DraftWithProvenance("FDTD Tidy3D Cloud 2D")),
            new Dictionary<string, ComponentSMatrixData>(),
            resetToPdkOriginal: () =>
            {
                resetCalled = true;
                return Task.FromResult<ComponentTemplate?>(restored);
            });

        await vm.ResetToPdkOriginalCommand.ExecuteAsync(null);

        resetCalled.ShouldBeTrue();
        vm.EffectiveProvenanceText.ShouldBeEmpty();
        vm.CanResetToPdkOriginal.ShouldBeFalse();
        vm.StatusText.ShouldContain("restored");
        vm.EffectiveEntries.ShouldNotBeEmpty("the section rebuilds from the restored template");
    }

    [Fact]
    public async Task ResetToPdkOriginal_withoutAResult_keepsTheCurrentState()
    {
        var vm = NewDialog(
            TemplateFor(DraftWithProvenance("FDTD Tidy3D Cloud 2D")),
            new Dictionary<string, ComponentSMatrixData>(),
            resetToPdkOriginal: () => Task.FromResult<ComponentTemplate?>(null));

        await vm.ResetToPdkOriginalCommand.ExecuteAsync(null);

        vm.EffectiveProvenanceText.ShouldContain("FDTD Tidy3D Cloud 2D");
    }
}
