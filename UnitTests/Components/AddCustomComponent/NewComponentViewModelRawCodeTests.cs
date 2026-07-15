using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.ViewModels.Components.AddCustomComponent;
using CAP_Core.Export;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

public class NewComponentViewModelRawCodeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lunima-nc-vm-raw-" + Guid.NewGuid().ToString("N"));

    private static NazcaPreviewResult Ok() => new()
    {
        Success = true, XMin = 0, YMin = 0, XMax = 8, YMax = 1.5,
        Pins = new List<NazcaPreviewPin> { new() { Name = "o1", X = 0, Y = 0.75, Angle = 180 }, new() { Name = "o2", X = 8, Y = 0.75, Angle = 0 } }
    };

    private static PdkComponentDraft SeedComponent(string n) => new()
    {
        Name = n, WidthMicrometers = 5, HeightMicrometers = 1,
        RawCode = "import gdsfactory as gf\ncomponent = gf.components.straight()", RawCodeBackend = "gdsfactory",
        Pins = new() { new PhysicalPinDraft { Name = "o1" }, new PhysicalPinDraft { Name = "o2" } }
    };

    private NewComponentViewModel Build()
    {
        var nazca = new Mock<IComponentPreviewRenderer>();
        var gds = new Mock<IComponentPreviewRenderer>();
        gds.Setup(g => g.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok());
        var extractor = new ComponentGeometryExtractor(nazca.Object, gds.Object);
        var store = new UserPdkStore(_root, new PdkJsonSaver(), new PdkLoader());
        var process = new ProcessDefinition { Name = "P" };
        store.SaveToNamedPdk("My PDK", process, SeedComponent("seed"), "gdsfactory", null);
        var vm = new NewComponentViewModel(extractor, fdtd: null, store,
            new List<ProcessDefinition> { process });
        vm.ComponentName = "My Raw Comp";
        return vm;
    }

    [Fact]
    public async Task OwnCodeMode_preview_and_save_writes_a_raw_code_draft()
    {
        var vm = Build();
        vm.SelectedBackend = GeometryBackend.GdsFactory;
        vm.Code = "import gdsfactory as gf\ncomponent = gf.components.mmi1x2()";

        await vm.RunPreviewCommand.ExecuteAsync(null);
        vm.HasPreview.ShouldBeTrue();
        await vm.SaveCommand.ExecuteAsync(null);

        vm.SavedDraft.ShouldNotBeNull();
        vm.SavedDraft!.RawCode.ShouldContain("gf.components.mmi1x2");
        vm.SavedDraft.RawCodeBackend.ShouldBe("gdsfactory");
        vm.SavedDraft.GdsFactoryFunction.ShouldBeNull();
        vm.SavedDraft.NazcaFunction.ShouldBeNull();
    }

    [Fact]
    public async Task LoadCodeFromFile_fills_Code_from_the_injected_picker()
    {
        var vm = Build();
        vm.PickPyFile = () => Task.FromResult<string?>("component = gf.components.straight()");

        await vm.LoadCodeFromFileCommand.ExecuteAsync(null);

        vm.Code.ShouldBe("component = gf.components.straight()");
    }

    [Fact]
    public async Task LoadCodeFromFile_is_a_noop_when_the_picker_returns_null()
    {
        var vm = Build();
        vm.Code = "unchanged";
        vm.PickPyFile = () => Task.FromResult<string?>(null);

        await vm.LoadCodeFromFileCommand.ExecuteAsync(null);

        vm.Code.ShouldBe("unchanged");
    }

    [Fact]
    public async Task LoadSMatrixFromFile_imports_and_shows_entries_then_saves_them()
    {
        var vm = Build();
        double freqGHz = 299_792_458.0 / 1550e-9 / 1e9;
        var s2p = "# GHz S MA R 50\n"
                  + freqGHz.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                  + " 0.05 10 0.95 80 0.95 80 0.05 10\n";
        var path = Path.Combine(_root, "coupler.s2p");
        await File.WriteAllTextAsync(path, s2p);
        vm.PickSMatrixFile = () => Task.FromResult<string?>(path);

        await vm.LoadSMatrixFromFileCommand.ExecuteAsync(null);

        vm.HasSMatrix.ShouldBeTrue();
        vm.SMatrixEntries.ShouldContain(e => e.WavelengthKey == "1550");

        await vm.SaveCommand.ExecuteAsync(null);
        vm.SavedDraft!.SMatrix.ShouldNotBeNull();
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
