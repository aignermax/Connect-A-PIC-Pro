using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.ViewModels.Components.AddCustomComponent;
using CAP_Core.Export;
using CAP_Core.Solvers.Fdtd;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

public class ComputeProgressTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lunima-nc-progress-" + Guid.NewGuid().ToString("N"));

    private static NazcaPreviewResult Ok() => new()
    {
        Success = true, XMin = 0, YMin = 0, XMax = 10, YMax = 2,
        Polygons = new List<NazcaPreviewPolygon>
        {
            new() { Layer = 1, Vertices = new List<(double, double)> { (0, 0), (10, 0), (10, 2), (0, 2) } }
        },
        Pins = new List<NazcaPreviewPin>
        {
            new() { Name = "o1", X = 0, Y = 1, Angle = 180 },
            new() { Name = "o2", X = 10, Y = 1, Angle = 0 }
        }
    };

    private static PdkComponentDraft SeedComponent(string n) => new()
    {
        Name = n, WidthMicrometers = 5, HeightMicrometers = 1,
        RawCode = "import gdsfactory as gf\ncomponent = gf.components.straight()", RawCodeBackend = "gdsfactory",
        Pins = new() { new PhysicalPinDraft { Name = "o1" }, new PhysicalPinDraft { Name = "o2" } }
    };

    private static FdtdSMatrixResult SuccessResult() => new()
    {
        Success = true,
        Ports = new[] { "o1", "o2" },
        Wavelengths = new[] { 1.55 },
        Entries = new[]
        {
            new FdtdSEntry { Key = "o2@0,o1@0", Values = new[] { new Complex(0.95, 0.0) } },
            new FdtdSEntry { Key = "o1@0,o2@0", Values = new[] { new Complex(0.95, 0.0) } },
        },
        EnergySumPerInput = new Dictionary<string, double> { ["o1@0"] = 0.9, ["o2@0"] = 0.9 },
    };

    private (NewComponentViewModel vm, Mock<IFdtdSMatrixService> fdtd) Build()
    {
        var nazca = new Mock<IComponentPreviewRenderer>();
        var gds = new Mock<IComponentPreviewRenderer>();
        gds.Setup(g => g.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok());
        var extractor = new ComponentGeometryExtractor(nazca.Object, gds.Object);
        var fdtd = new Mock<IFdtdSMatrixService>();
        var store = new UserPdkStore(_root, new PdkJsonSaver(), new PdkLoader());
        var process = new ProcessDefinition { Name = "P" };
        store.SaveToNamedPdk("My PDK", process, SeedComponent("seed"), "gdsfactory", null);
        var vm = new NewComponentViewModel(extractor, fdtd.Object, store, new List<ProcessDefinition> { process });
        vm.ComponentName = "My Comp";
        vm.SelectedBackend = GeometryBackend.GdsFactory;
        vm.Code = "import gdsfactory as gf\ncomponent = gf.components.coupler()";
        return (vm, fdtd);
    }

    [Fact]
    public async Task ComputeSMatrix_passes_a_non_null_progress_sink_to_the_solver()
    {
        var (vm, fdtd) = Build();
        fdtd.Setup(f => f.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdtdAvailability.Available(""));
        fdtd.Setup(f => f.SolveAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());

        await vm.RunPreviewCommand.ExecuteAsync(null);
        await vm.ComputeSMatrixCommand.ExecuteAsync(null);

        fdtd.Verify(f => f.SolveAsync(
                It.IsAny<FdtdSMatrixRequest>(),
                It.Is<IProgress<string>?>(p => p != null),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ComputeSMatrix_surfaces_a_reported_progress_line_in_StatusText()
    {
        var original = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineSynchronizationContext());
        try
        {
            var (vm, fdtd) = Build();
            fdtd.Setup(f => f.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(FdtdAvailability.Available(""));
            fdtd.Setup(f => f.SolveAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
                .Callback<FdtdSMatrixRequest, IProgress<string>?, CancellationToken>((_, progress, _) =>
                    progress?.Report("Meep step 50%"))
                .ReturnsAsync(SuccessResult());

            var seenStatuses = new List<string>();
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(NewComponentViewModel.StatusText)) seenStatuses.Add(vm.StatusText);
            };

            await vm.RunPreviewCommand.ExecuteAsync(null);
            await vm.ComputeSMatrixCommand.ExecuteAsync(null);

            seenStatuses.ShouldContain(s => s.Contains("Meep step 50%"));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(original);
        }
    }

    [Fact]
    public async Task ComputeSMatrix_failure_still_reports_the_raw_error_and_clears_the_model()
    {
        var (vm, fdtd) = Build();
        fdtd.Setup(f => f.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdtdAvailability.Available(""));
        fdtd.Setup(f => f.SolveAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdtdSMatrixResult.Fail("solver blew up"));

        await vm.RunPreviewCommand.ExecuteAsync(null);
        await vm.ComputeSMatrixCommand.ExecuteAsync(null);
        await vm.SaveCommand.ExecuteAsync(null);

        vm.StatusText.ShouldContain("solver blew up");
        vm.SavedDraft!.SMatrix.ShouldBeNull();
    }

    [Fact]
    public void CancelCompute_before_any_run_is_a_no_op()
    {
        var (vm, _) = Build();
        Should.NotThrow(() => vm.CancelCompute());
    }

    private sealed class InlineSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
