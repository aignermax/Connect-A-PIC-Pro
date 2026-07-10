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

/// <summary>
/// Covers <see cref="NewComponentViewModel"/>'s own-code mode: pasting/loading raw Python
/// source instead of a module/function reference, previewing it via
/// <see cref="GeometryReference.RawCode"/>, and saving it as a raw-code draft (never a
/// fabricated module/function reference).
/// </summary>
public class NewComponentViewModelRawCodeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lunima-nc-vm-raw-" + Guid.NewGuid().ToString("N"));

    private static NazcaPreviewResult Ok() => new()
    {
        Success = true, XMin = 0, YMin = 0, XMax = 8, YMax = 1.5,
        Pins = new List<NazcaPreviewPin> { new() { Name = "o1", X = 0, Y = 0.75, Angle = 180 }, new() { Name = "o2", X = 8, Y = 0.75, Angle = 0 } }
    };

    private NewComponentViewModel Build()
    {
        var nazca = new Mock<IComponentPreviewRenderer>();
        var gds = new Mock<IComponentPreviewRenderer>();
        gds.Setup(g => g.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok());
        var extractor = new ComponentGeometryExtractor(nazca.Object, gds.Object);
        var store = new UserPdkStore(_root, new PdkJsonSaver(), new PdkLoader());
        var vm = new NewComponentViewModel(extractor, fdtd: null, store,
            new List<ProcessDefinition> { new() { Name = "P" } });
        vm.ComponentName = "My Raw Comp";
        vm.SelectedProcess = vm.Processes[0];
        vm.NewPdkName = "My PDK"; // no custom PDKs exist yet -> IsNewPdk defaults true, needs a name
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

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
