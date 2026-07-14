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
/// Covers task 1 of the PDK-process UX refinement (#729 follow-up): <c>Save</c> no longer
/// requires a prior explicit <see cref="NewComponentViewModel.RunPreview"/> click — it renders
/// and validates the current code itself via the private <c>EnsurePreviewAsync</c> helper. A
/// render failure (e.g. a Python syntax error) is reported through
/// <see cref="NewComponentViewModel.StatusText"/> and aborts the save, exactly like a failed
/// explicit preview always did. A preview already rendered by an explicit click is reused
/// verbatim — Save never re-renders on top of a still-valid preview.
/// </summary>
public class SaveWithoutPreviewTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lunima-nc-vm-save-no-preview-" + Guid.NewGuid().ToString("N"));

    private static NazcaPreviewResult Ok() => new()
    {
        Success = true, XMin = 0, YMin = 0, XMax = 10, YMax = 2,
        Polygons = new List<NazcaPreviewPolygon>
        {
            new() { Layer = 1, Vertices = new List<(double, double)> { (0, 0), (10, 0), (10, 2), (0, 2) } }
        },
        Pins = new List<NazcaPreviewPin> { new() { Name = "o1", X = 0, Y = 1, Angle = 180 }, new() { Name = "o2", X = 10, Y = 1, Angle = 0 } }
    };

    private static PdkComponentDraft SeedComponent(string n) => new()
    {
        Name = n, WidthMicrometers = 5, HeightMicrometers = 1,
        RawCode = "import gdsfactory as gf\ncomponent = gf.components.straight()", RawCodeBackend = "gdsfactory",
        Pins = new() { new PhysicalPinDraft { Name = "o1" }, new PhysicalPinDraft { Name = "o2" } }
    };

    private (NewComponentViewModel vm, Mock<IComponentPreviewRenderer> gds) Build()
    {
        var nazca = new Mock<IComponentPreviewRenderer>();
        var gds = new Mock<IComponentPreviewRenderer>();
        gds.Setup(g => g.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok());
        var extractor = new ComponentGeometryExtractor(nazca.Object, gds.Object);
        var store = new UserPdkStore(_root, new PdkJsonSaver(), new PdkLoader());
        var process = new ProcessDefinition { Name = "P" };
        store.SaveToNamedPdk("My PDK", process, SeedComponent("seed"), "gdsfactory", null);
        var vm = new NewComponentViewModel(extractor, fdtd: null, store, new List<ProcessDefinition> { process });
        vm.ComponentName = "My Comp";
        vm.SelectedBackend = GeometryBackend.GdsFactory;
        vm.Code = "import gdsfactory as gf\ncomponent = gf.components.coupler()";
        return (vm, gds);
    }

    // (A) No preceding Preview click: CanExecute is already true (PDK selected, not busy), and
    // Save renders the code itself and persists the result.
    [Fact]
    public async Task Save_withoutAPriorPreviewClick_rendersItselfAndSaves()
    {
        var (vm, gds) = Build();

        vm.HasPreview.ShouldBeFalse(); // no Preview click happened
        vm.SaveCommand.CanExecute(null).ShouldBeTrue();

        await vm.SaveCommand.ExecuteAsync(null);

        vm.SavedDraft.ShouldNotBeNull();
        vm.SavedFilePath.ShouldNotBeNullOrEmpty();
        gds.Verify(g => g.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // (B) A render failure (e.g. a Python syntax error) surfaces via StatusText and aborts the
    // save — never silently, never with a fabricated/black-box draft in its place.
    [Fact]
    public async Task Save_whenTheCodeFailsToRender_reportsTheErrorAndDoesNotSave()
    {
        var nazca = new Mock<IComponentPreviewRenderer>();
        var gds = new Mock<IComponentPreviewRenderer>();
        gds.Setup(g => g.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NazcaPreviewResult.Fail("SyntaxError: invalid syntax"));
        var extractor = new ComponentGeometryExtractor(nazca.Object, gds.Object);
        var store = new UserPdkStore(_root, new PdkJsonSaver(), new PdkLoader());
        var process = new ProcessDefinition { Name = "P" };
        store.SaveToNamedPdk("My PDK", process, SeedComponent("seed"), "gdsfactory", null);
        var vm = new NewComponentViewModel(extractor, fdtd: null, store, new List<ProcessDefinition> { process });
        vm.ComponentName = "My Comp";
        vm.SelectedBackend = GeometryBackend.GdsFactory;
        vm.Code = "def broken(:\n    pass";

        await vm.SaveCommand.ExecuteAsync(null);

        vm.SavedDraft.ShouldBeNull();
        vm.StatusText.ShouldContain("SyntaxError");
    }

    // (C) A preview already rendered by an explicit click is reused, not re-rendered: the
    // renderer is invoked exactly once across both the Preview click and the subsequent Save.
    [Fact]
    public async Task Save_afterAnExplicitPreview_reusesItWithoutRerendering()
    {
        var (vm, gds) = Build();

        await vm.RunPreviewCommand.ExecuteAsync(null);
        vm.HasPreview.ShouldBeTrue();

        await vm.SaveCommand.ExecuteAsync(null);

        vm.SavedDraft.ShouldNotBeNull();
        gds.Verify(g => g.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
