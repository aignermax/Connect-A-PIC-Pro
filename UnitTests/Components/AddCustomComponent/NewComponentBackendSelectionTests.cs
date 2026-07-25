using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.Services.Solvers;
using CAP.Avalonia.ViewModels.Components.AddCustomComponent;
using CAP.Avalonia.ViewModels.Solvers;
using CAP_Core.Export;
using CAP_Core.Solvers.Fdtd;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

public class NewComponentBackendSelectionTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lunima-nc-backend-" + Guid.NewGuid().ToString("N"));
    private readonly string _prefsPath =
        Path.Combine(Path.GetTempPath(), $"prefs_{Guid.NewGuid():N}.json");

    // Status strings are localized; pin English so assertions are locale-independent (runner is de_DE).
    public NewComponentBackendSelectionTests() =>
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);

    // Test double that is both a solver and a cost estimator, like Tidy3D.
    public abstract class CloudService : IFdtdSMatrixService, IFdtdCostEstimator
    {
        public abstract Task<FdtdSMatrixResult> SolveAsync(
            FdtdSMatrixRequest request, IProgress<string>? progress = null, CancellationToken ct = default);

        public abstract Task<FdtdAvailability> CheckAvailabilityAsync(CancellationToken ct = default);

        public abstract Task<FdtdCostEstimate> EstimateCostAsync(
            FdtdSMatrixRequest request, CancellationToken ct = default);
    }

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
        EnergySumPerInput = new Dictionary<string, double> { ["o1@0"] = 0.9 },
    };

    private static Mock<CloudService> ReadyCloudService()
    {
        var service = new Mock<CloudService>();
        service.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(FdtdAvailability.Available("Tidy3D ready"));
        service.Setup(s => s.EstimateCostAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new FdtdCostEstimate
               { Success = true, EstimatedCredits = 1.5, SimulationCount = 2 });
        service.Setup(s => s.SolveAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(SuccessResult());
        return service;
    }

    private (NewComponentViewModel Vm, FdtdBackendSelectionViewModel Selection) Build(
        IFdtdSMatrixService meep, IFdtdSMatrixService tidy3d)
    {
        var nazca = new Mock<IComponentPreviewRenderer>();
        var gds = new Mock<IComponentPreviewRenderer>();
        gds.Setup(g => g.RenderRawCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok());
        var extractor = new ComponentGeometryExtractor(nazca.Object, gds.Object);
        var store = new UserPdkStore(_root, new PdkJsonSaver(), new PdkLoader());
        var process = new ProcessDefinition { Name = "P" };
        store.SaveToNamedPdk("My PDK", process, SeedComponent("seed"), "gdsfactory", null);
        var registry = new FdtdBackendRegistry(
            new Dictionary<FdtdBackendType, IFdtdSMatrixService>
            {
                [FdtdBackendType.MeepDocker] = meep,
                [FdtdBackendType.Tidy3D] = tidy3d,
            },
            new UserPreferencesService(_prefsPath));
        var vm = new NewComponentViewModel(
            extractor, meep, store, new List<ProcessDefinition> { process },
            fdtdBackendRegistry: registry);
        vm.ComponentName = "My Comp";
        vm.SelectedBackend = GeometryBackend.GdsFactory;
        vm.Code = "import gdsfactory as gf\ncomponent = gf.components.coupler()";
        return (vm, vm.BackendSelection!);
    }

    [Fact]
    public async Task Compute_RoutesThroughSelectedBackend_AndNamesItInProvenance()
    {
        var meep = new Mock<IFdtdSMatrixService>();
        var tidy3d = ReadyCloudService();
        var (vm, selection) = Build(meep.Object, tidy3d.Object);
        selection.SelectedBackend = FdtdBackendType.Tidy3D;

        vm.ComputeButtonLabel.ShouldContain("Tidy3D Cloud");
        await vm.RunPreviewCommand.ExecuteAsync(null);
        await vm.ComputeSMatrixCommand.ExecuteAsync(null);
        await vm.ConfirmCloudSubmitCommand.ExecuteAsync(null);

        tidy3d.Verify(s => s.SolveAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()), Times.Once);
        meep.Verify(s => s.SolveAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
        vm.SMatrixEntries.ShouldContain(e => e.SourceNote == "FDTD Tidy3D Cloud 2D");
    }

    [Fact]
    public async Task Compute_WithMeepSelected_SolvesDirectlyWithoutCloudGate()
    {
        var meep = new Mock<IFdtdSMatrixService>();
        meep.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdtdAvailability.Available("ready"));
        meep.Setup(s => s.SolveAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());
        var (vm, _) = Build(meep.Object, ReadyCloudService().Object);

        vm.ComputeButtonLabel.ShouldContain("Meep");
        await vm.RunPreviewCommand.ExecuteAsync(null);
        await vm.ComputeSMatrixCommand.ExecuteAsync(null);

        vm.IsAwaitingCloudConfirmation.ShouldBeFalse();
        meep.Verify(s => s.SolveAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()), Times.Once);
        vm.SMatrixEntries.ShouldContain(e => e.SourceNote == "FDTD Meep 2D");
    }

    [Fact]
    public async Task Tidy3dWithoutKey_DisablesComputeAndShowsHint_UntilFixed()
    {
        var meep = new Mock<IFdtdSMatrixService>();
        var tidy3d = ReadyCloudService();
        tidy3d.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(FdtdAvailability.Unavailable(
                  "No Tidy3D API key configured. Enter it in Settings → Tidy3D Cloud."));
        var (vm, selection) = Build(meep.Object, tidy3d.Object);
        selection.SelectedBackend = FdtdBackendType.Tidy3D;

        await vm.RefreshBackendAvailabilityAsync();

        vm.ComputeSMatrixCommand.CanExecute(null).ShouldBeFalse();
        selection.AvailabilityHint.ShouldContain("API key");
        selection.ShowMissingKeyLink.ShouldBeTrue();

        // Key entered → the backend reports ready → the button comes back.
        tidy3d.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(FdtdAvailability.Available("Tidy3D ready"));
        await vm.RefreshBackendAvailabilityAsync();

        vm.ComputeSMatrixCommand.CanExecute(null).ShouldBeTrue();
        selection.AvailabilityHint.ShouldBeEmpty();
    }

    [Fact]
    public async Task Compute_WithCloudBackend_PausesForConfirmationWithoutSolving()
    {
        var tidy3d = ReadyCloudService();
        var (vm, selection) = Build(new Mock<IFdtdSMatrixService>().Object, tidy3d.Object);
        selection.SelectedBackend = FdtdBackendType.Tidy3D;

        await vm.RunPreviewCommand.ExecuteAsync(null);
        await vm.ComputeSMatrixCommand.ExecuteAsync(null);

        vm.IsAwaitingCloudConfirmation.ShouldBeTrue();
        vm.CloudCostText.ShouldContain("FlexCredits");
        vm.CloudCostText.ShouldContain("2 cloud simulation(s)");
        vm.IsBusy.ShouldBeFalse();
        tidy3d.Verify(s => s.SolveAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
        vm.ComputeSMatrixCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task CancelCloudSubmit_ResetsStateWithoutSolving()
    {
        var tidy3d = ReadyCloudService();
        var (vm, selection) = Build(new Mock<IFdtdSMatrixService>().Object, tidy3d.Object);
        selection.SelectedBackend = FdtdBackendType.Tidy3D;
        await vm.RunPreviewCommand.ExecuteAsync(null);
        await vm.ComputeSMatrixCommand.ExecuteAsync(null);

        vm.CancelCloudSubmitCommand.Execute(null);

        vm.IsAwaitingCloudConfirmation.ShouldBeFalse();
        vm.StatusText.ShouldContain("no credits");
        tidy3d.Verify(s => s.SolveAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
        vm.ComputeSMatrixCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task EstimateFailure_StillOffersSubmission_AndConfirmSolves()
    {
        var tidy3d = ReadyCloudService();
        tidy3d.Setup(s => s.EstimateCostAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(FdtdCostEstimate.Fail("server unreachable"));
        var (vm, selection) = Build(new Mock<IFdtdSMatrixService>().Object, tidy3d.Object);
        selection.SelectedBackend = FdtdBackendType.Tidy3D;
        await vm.RunPreviewCommand.ExecuteAsync(null);
        await vm.ComputeSMatrixCommand.ExecuteAsync(null);

        vm.IsAwaitingCloudConfirmation.ShouldBeTrue();
        vm.CloudCostText.ShouldContain("server unreachable");

        await vm.ConfirmCloudSubmitCommand.ExecuteAsync(null);

        tidy3d.Verify(s => s.SolveAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()), Times.Once);
        vm.StatusText.ShouldContain("computed");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        if (File.Exists(_prefsPath)) File.Delete(_prefsPath);
    }
}
