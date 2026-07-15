using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.ViewModels.Components.AddCustomComponent;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Export;
using CAP_Core.Components.Process;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

public class NewComponentViewModelMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lunima-nc-mig-" + Guid.NewGuid().ToString("N"));

    private static NazcaPreviewResult Ok() => new()
    {
        Success = true, XMin = 0, YMin = 0, XMax = 8, YMax = 1.5,
        Pins = new List<NazcaPreviewPin>
        {
            new() { Name = "o1", X = 0, Y = 0.75, Angle = 180 },
            new() { Name = "o2", X = 8, Y = 0.75, Angle = 0 }
        }
    };

    private static PdkComponentDraft Widget() => new()
    {
        Name = "Widget", WidthMicrometers = 5, HeightMicrometers = 1,
        RawCode = "import gdsfactory as gf\ncomponent = gf.components.straight()", RawCodeBackend = "gdsfactory",
        Pins = new() { new PhysicalPinDraft { Name = "o1" }, new PhysicalPinDraft { Name = "o2" } }
    };

    private (NewComponentViewModel vm, UserPdkStore store) BuildWithTwoPdks(string processB)
    {
        var nazca = new Mock<IComponentPreviewRenderer>();
        var gds = new Mock<IComponentPreviewRenderer>();
        gds.Setup(g => g.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok());
        var extractor = new ComponentGeometryExtractor(nazca.Object, gds.Object);
        var store = new UserPdkStore(_root, new PdkJsonSaver(), new PdkLoader());

        var procA = new ProcessDefinition { Name = "SiN" };
        var procB = new ProcessDefinition { Name = processB };
        store.SaveToNamedPdk("PDK A", procA, Widget(), "gdsfactory", null);
        store.SaveToNamedPdk("PDK B", procB, new PdkComponentDraft
        {
            Name = "Filler", WidthMicrometers = 5, HeightMicrometers = 1,
            RawCode = "component = gf.components.straight()", RawCodeBackend = "gdsfactory",
            Pins = new() { new PhysicalPinDraft { Name = "o1" }, new PhysicalPinDraft { Name = "o2" } }
        }, "gdsfactory", null);

        var vm = new NewComponentViewModel(extractor, fdtd: null, store,
            new List<ProcessDefinition> { procA, procB });
        return (vm, store);
    }

    private static void SelectPdk(NewComponentViewModel vm, string name) =>
        vm.SelectedPdkChoice = vm.PdkChoices.First(c => !c.IsNewPdk && c.Pdk!.Name == name);

    private static ComponentTemplate WidgetTemplateInPdkA() => new()
    {
        Name = "Widget", PdkSource = "PDK A",
        RawCode = "import gdsfactory as gf\ncomponent = gf.components.straight()", RawCodeBackend = "gdsfactory",
        IsCustom = true
    };

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public async Task SwitchingPdk_sameProcess_movesComponent()
    {
        var (vm, store) = BuildWithTwoPdks(processB: "SiN");
        vm.LoadForEdit(WidgetTemplateInPdkA());
        vm.IsEditMode.ShouldBeTrue();

        SelectPdk(vm, "PDK B");
        await vm.SaveCommand.ExecuteAsync(null);

        vm.MigratedFromPdkName.ShouldBe("PDK A");
        var pdkA = store.ListCustomPdks().First(p => p.Name == "PDK A");
        var pdkB = store.ListCustomPdks().First(p => p.Name == "PDK B");
        store.ComponentExistsInFile(pdkA.FilePath, "Widget").ShouldBeFalse();
        store.ComponentExistsInFile(pdkB.FilePath, "Widget").ShouldBeTrue();
    }

    [Fact]
    public async Task SwitchingPdk_differentProcess_refusesAndKeepsOriginal()
    {
        var (vm, store) = BuildWithTwoPdks(processB: "SOI");
        vm.LoadForEdit(WidgetTemplateInPdkA());

        SelectPdk(vm, "PDK B");
        await vm.SaveCommand.ExecuteAsync(null);

        vm.MigratedFromPdkName.ShouldBeNull();
        vm.StatusText.ShouldContain("different fabrication");
        var pdkA = store.ListCustomPdks().First(p => p.Name == "PDK A");
        store.ComponentExistsInFile(pdkA.FilePath, "Widget").ShouldBeTrue();
    }
}
