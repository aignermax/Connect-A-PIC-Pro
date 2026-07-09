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
/// Covers the v2 scope of <see cref="NewComponentViewModel"/> (#701): nazca custom
/// components with derived NazcaOriginOffset, raw-code authoring, and the selectable
/// S-matrix source (black box / FDTD / lossless 2-port ideal) — always without invented
/// physics. Saved files must pass the strict <see cref="PdkLoader.LoadFromFile"/> path.
/// </summary>
public class NewComponentViewModelV2Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lunima-nc-v2-" + Guid.NewGuid().ToString("N"));

    /// <summary>A render whose bbox does not start at the origin, so offset derivation is visible.</summary>
    private static NazcaPreviewResult Ok() => new()
    {
        Success = true, XMin = -3, YMin = 3, XMax = 7, YMax = 5,
        Pins = new List<NazcaPreviewPin> { new() { Name = "o1", X = -3, Y = 4, Angle = 180 }, new() { Name = "o2", X = 7, Y = 4, Angle = 0 } }
    };

    private readonly Mock<IComponentPreviewRenderer> _nazca = new();
    private readonly Mock<IComponentPreviewRenderer> _gds = new();

    private NewComponentViewModel Build()
    {
        _nazca.Setup(n => n.RenderAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok());
        _nazca.Setup(n => n.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok());
        _gds.Setup(g => g.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok());
        var extractor = new ComponentGeometryExtractor(_nazca.Object, _gds.Object);
        var store = new UserPdkStore(_root, new PdkJsonSaver(), new PdkLoader());
        var vm = new NewComponentViewModel(extractor, null, store,
            new List<ProcessDefinition> { new() { Name = "P" } });
        vm.ComponentName = "My Comp";
        vm.SelectedProcess = vm.Processes[0];
        return vm;
    }

    [Fact]
    public async Task Nazca_save_derives_offsets_and_passes_the_strict_loader()
    {
        var vm = Build();
        vm.SelectedBackend = GeometryBackend.Nazca;
        vm.Module = "mylib"; vm.Function = "mmi";

        await vm.RunPreviewCommand.ExecuteAsync(null);
        await vm.SaveCommand.ExecuteAsync(null);

        vm.SavedDraft.ShouldNotBeNull();
        vm.SavedDraft!.NazcaFunction.ShouldBe("mylib.mmi");
        vm.SavedDraft.NazcaOriginOffsetX.ShouldBe(3);   // -XMin
        vm.SavedDraft.NazcaOriginOffsetY.ShouldBe(5);   // YMax

        // The saved user PDK must pass the strict (non-editing) load path.
        var store = new UserPdkStore(_root, new PdkJsonSaver(), new PdkLoader());
        var path = store.ResolvePath(vm.SelectedProcess!);
        var reloaded = new PdkLoader().LoadFromFile(path);
        reloaded.Components.ShouldContain(c => c.Name == "My Comp");
    }

    [Fact]
    public async Task RawCode_preview_renders_via_the_rawcode_pipeline_and_save_carries_the_code()
    {
        var vm = Build();
        vm.SelectedBackend = GeometryBackend.Nazca;
        vm.UseRawCode = true;
        vm.RawCode = "with nd.Cell('c') as c: pass";

        await vm.RunPreviewCommand.ExecuteAsync(null);
        await vm.SaveCommand.ExecuteAsync(null);

        _nazca.Verify(n => n.RenderRawCodeAsync("with nd.Cell('c') as c: pass", It.IsAny<CancellationToken>()), Times.Once);
        vm.SavedDraft.ShouldNotBeNull();
        vm.SavedDraft!.RawCode.ShouldBe("with nd.Cell('c') as c: pass");
        vm.SavedDraft.RawCodeBackend.ShouldBe("nazca");
        vm.SavedDraft.NazcaOriginOffsetX.ShouldBe(3);
        vm.SavedDraft.NazcaOriginOffsetY.ShouldBe(5);

        // Raw-code components must also pass the strict loader (no function reference).
        var store = new UserPdkStore(_root, new PdkJsonSaver(), new PdkLoader());
        var reloaded = new PdkLoader().LoadFromFile(store.ResolvePath(vm.SelectedProcess!));
        reloaded.Components.ShouldContain(c => c.RawCode != null);
    }

    [Fact]
    public async Task Toggling_rawcode_mode_invalidates_the_preview()
    {
        var vm = Build();
        vm.SelectedBackend = GeometryBackend.GdsFactory;
        vm.Module = "m"; vm.Function = "f";
        await vm.RunPreviewCommand.ExecuteAsync(null);
        vm.HasPreview.ShouldBeTrue();

        vm.UseRawCode = true;

        vm.HasPreview.ShouldBeFalse();
        vm.SaveCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task Lossless_ideal_saves_the_exact_unit_passthrough()
    {
        var vm = Build();
        vm.SelectedBackend = GeometryBackend.GdsFactory;
        vm.Module = "m"; vm.Function = "f";
        vm.SelectedSMatrixOption = SMatrixSourceOption.For(SMatrixSource.LosslessTwoPort);

        await vm.RunPreviewCommand.ExecuteAsync(null);
        await vm.SaveCommand.ExecuteAsync(null);

        vm.SavedDraft.ShouldNotBeNull();
        var sMatrix = vm.SavedDraft!.SMatrix.ShouldNotBeNull();
        sMatrix.Connections!.Count.ShouldBe(2);
        sMatrix.Connections[0].Magnitude.ShouldBe(1.0);
        vm.StatusText.ShouldContain("lossless");
    }

    [Fact]
    public async Task Fdtd_choice_without_a_computed_result_aborts_the_save()
    {
        var vm = Build();
        vm.SelectedBackend = GeometryBackend.GdsFactory;
        vm.Module = "m"; vm.Function = "f";
        vm.SelectedSMatrixOption = SMatrixSourceOption.For(SMatrixSource.Fdtd);

        await vm.RunPreviewCommand.ExecuteAsync(null);
        await vm.SaveCommand.ExecuteAsync(null);

        vm.SavedDraft.ShouldBeNull();               // no silent black-box downgrade
        vm.StatusText.ShouldContain("Compute S-Matrix");
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
