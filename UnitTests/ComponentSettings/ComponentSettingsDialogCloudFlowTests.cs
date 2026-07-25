using System.Numerics;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.Services.Solvers;
using CAP.Avalonia.ViewModels.ComponentSettings;
using CAP.Avalonia.ViewModels.Solvers;
using CAP_Core.Components.Core;
using CAP_Core.Solvers.Fdtd;
using CAP_DataAccess.Persistence.PIR;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.ComponentSettings;

public class ComponentSettingsDialogCloudFlowTests : IDisposable
{
    private readonly string _prefsPath =
        Path.Combine(Path.GetTempPath(), $"prefs_{Guid.NewGuid():N}.json");

    // Status strings are localized; pin English so assertions are locale-independent (runner is de_DE).
    public ComponentSettingsDialogCloudFlowTests() =>
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);

    public void Dispose()
    {
        if (File.Exists(_prefsPath)) File.Delete(_prefsPath);
    }

    // Test double that is both a solver and a cost estimator, like Tidy3D.
    public abstract class CloudService : IFdtdSMatrixService, IFdtdCostEstimator
    {
        public abstract Task<FdtdSMatrixResult> SolveAsync(
            FdtdSMatrixRequest request, IProgress<string>? progress = null, CancellationToken ct = default);

        public abstract Task<FdtdAvailability> CheckAvailabilityAsync(CancellationToken ct = default);

        public abstract Task<FdtdCostEstimate> EstimateCostAsync(
            FdtdSMatrixRequest request, CancellationToken ct = default);
    }

    private static Func<Component, CancellationToken, Task<FdtdSMatrixRequest?>> FakeFactory() =>
        (_, _) => Task.FromResult<FdtdSMatrixRequest?>(new FdtdSMatrixRequest
        {
            Ports = new[] { new FdtdPort { Name = "o1" }, new FdtdPort { Name = "o2" } },
        });

    private static FdtdSMatrixResult SuccessResult() => new()
    {
        Success = true,
        Is3D = true,
        Ports = new[] { "o1", "o2" },
        Wavelengths = new[] { 1.55 },
        Entries = new[]
        {
            new FdtdSEntry { Key = "o2@0,o1@0", Values = new[] { new Complex(0.95, 0.0) } },
            new FdtdSEntry { Key = "o1@0,o2@0", Values = new[] { new Complex(0.95, 0.0) } },
        },
        EnergySumPerInput = new Dictionary<string, double> { ["o1@0"] = 0.97 },
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

    private static (ComponentSettingsDialogViewModel Vm, Dictionary<string, ComponentSMatrixData> Store)
        NewDialog(IFdtdSMatrixService service, FdtdBackendSelectionViewModel? backendSelection = null)
    {
        var store = new Dictionary<string, ComponentSMatrixData>();
        var vm = new ComponentSettingsDialogViewModel(
            Mock.Of<IFileDialogService>(),
            fdtdService: service,
            fdtdRequestFactory: FakeFactory(),
            backendSelection: backendSelection);
        vm.Configure("comp", "comp", "Comp", store,
            liveComponent: TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins());
        return (vm, store);
    }

    private FdtdBackendSelectionViewModel NewSelection(
        IFdtdSMatrixService meep, IFdtdSMatrixService tidy3d) =>
        new(new FdtdBackendRegistry(
            new Dictionary<FdtdBackendType, IFdtdSMatrixService>
            {
                [FdtdBackendType.MeepDocker] = meep,
                [FdtdBackendType.Tidy3D] = tidy3d,
            },
            new UserPreferencesService(_prefsPath)));

    [Fact]
    public async Task Recalculate_WithCloudBackend_PausesForConfirmationWithoutSolving()
    {
        var service = ReadyCloudService();
        var (vm, store) = NewDialog(service.Object);

        await vm.RecalculateSMatrixCommand.ExecuteAsync(null);

        vm.IsAwaitingCloudConfirmation.ShouldBeTrue();
        vm.CloudCostText.ShouldContain("FlexCredits");
        vm.CloudCostText.ShouldContain("2 cloud simulation(s)");
        vm.IsComputing.ShouldBeFalse();
        store.ShouldBeEmpty();
        service.Verify(s => s.SolveAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
        // The recompute button stays disabled until the pending job is resolved.
        vm.RecalculateSMatrixCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task Recalculate_WhenEstimateFails_StillOffersSubmissionWithHonestText()
    {
        var service = ReadyCloudService();
        service.Setup(s => s.EstimateCostAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(FdtdCostEstimate.Fail("server unreachable"));
        var (vm, store) = NewDialog(service.Object);

        await vm.RecalculateSMatrixCommand.ExecuteAsync(null);

        vm.IsAwaitingCloudConfirmation.ShouldBeTrue();
        vm.CloudCostText.ShouldContain("server unreachable");

        // A failed estimate must never block submission.
        await vm.ConfirmCloudSubmitCommand.ExecuteAsync(null);

        store.ShouldContainKey("comp");
        service.Verify(s => s.SolveAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConfirmCloudSubmit_SolvesAndAppliesResult()
    {
        var service = ReadyCloudService();
        var (vm, store) = NewDialog(service.Object);
        await vm.RecalculateSMatrixCommand.ExecuteAsync(null);

        await vm.ConfirmCloudSubmitCommand.ExecuteAsync(null);

        vm.IsAwaitingCloudConfirmation.ShouldBeFalse();
        vm.IsComputing.ShouldBeFalse();
        store.ShouldContainKey("comp");
        store["comp"].Wavelengths.ShouldContainKey("1550");
        vm.SolverStatus.ShouldContain("FDTD done");
        service.Verify(s => s.SolveAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancelCloudSubmit_ResetsStateWithoutSolving()
    {
        var service = ReadyCloudService();
        var (vm, store) = NewDialog(service.Object);
        await vm.RecalculateSMatrixCommand.ExecuteAsync(null);

        vm.CancelCloudSubmitCommand.Execute(null);

        vm.IsAwaitingCloudConfirmation.ShouldBeFalse();
        vm.IsComputing.ShouldBeFalse();
        store.ShouldBeEmpty();
        vm.SolverStatus.ShouldContain("no credits");
        service.Verify(s => s.SolveAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
        // The recompute button is usable again after dismissing.
        vm.RecalculateSMatrixCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public async Task Recalculate_WhenCloudBackendUnavailable_ExplainsAndNeverEstimates()
    {
        var service = ReadyCloudService();
        service.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(FdtdAvailability.Unavailable(
                   "No Tidy3D API key configured. Enter it in Settings → Tidy3D Cloud."));
        var (vm, store) = NewDialog(service.Object);

        await vm.RecalculateSMatrixCommand.ExecuteAsync(null);

        vm.SolverStatus.ShouldContain("Tidy3D Cloud");
        vm.IsAwaitingCloudConfirmation.ShouldBeFalse();
        store.ShouldBeEmpty();
        service.Verify(s => s.EstimateCostAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Recalculate_WithBackendSelection_RoutesThroughSelectedBackend()
    {
        var meep = new Mock<IFdtdSMatrixService>();
        var tidy3d = ReadyCloudService();
        var selection = NewSelection(meep.Object, tidy3d.Object);
        selection.SelectedBackend = FdtdBackendType.Tidy3D;
        var (vm, store) = NewDialog(meep.Object, selection);

        vm.RecalculateButtonLabel.ShouldContain("Tidy3D");
        await vm.RecalculateSMatrixCommand.ExecuteAsync(null);
        await vm.ConfirmCloudSubmitCommand.ExecuteAsync(null);

        store.ShouldContainKey("comp");
        store["comp"].SourceNote.ShouldBe("FDTD Tidy3D 3D");
        tidy3d.Verify(s => s.SolveAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()), Times.Once);
        meep.Verify(s => s.SolveAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Tidy3dWithoutKey_DisablesRunAndShowsHint_UntilFixed()
    {
        var meep = new Mock<IFdtdSMatrixService>();
        var tidy3d = ReadyCloudService();
        tidy3d.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(FdtdAvailability.Unavailable(
                  "No Tidy3D API key configured. Enter it in Settings → Tidy3D Cloud."));
        var selection = NewSelection(meep.Object, tidy3d.Object);
        selection.SelectedBackend = FdtdBackendType.Tidy3D;
        var (vm, _) = NewDialog(meep.Object, selection);

        await vm.RefreshBackendAvailabilityAsync();

        vm.RecalculateSMatrixCommand.CanExecute(null).ShouldBeFalse();
        selection.AvailabilityHint.ShouldContain("API key");
        selection.ShowMissingKeyLink.ShouldBeTrue();

        // Key entered → the backend reports ready → the button comes back.
        tidy3d.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
              .ReturnsAsync(FdtdAvailability.Available("Tidy3D ready"));
        await vm.RefreshBackendAvailabilityAsync();

        vm.RecalculateSMatrixCommand.CanExecute(null).ShouldBeTrue();
        selection.AvailabilityHint.ShouldBeEmpty();
    }

    [Fact]
    public async Task Recalculate_WithBackendSelection_MeepUnavailable_DoesNotEstimate()
    {
        var meep = new Mock<IFdtdSMatrixService>();
        meep.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdtdAvailability.Unavailable("Docker is not running."));
        var tidy3d = ReadyCloudService();
        var selection = NewSelection(meep.Object, tidy3d.Object);
        var (vm, store) = NewDialog(meep.Object, selection);

        await vm.RecalculateSMatrixCommand.ExecuteAsync(null);

        vm.SolverStatus.ShouldContain("Docker");
        vm.IsAwaitingCloudConfirmation.ShouldBeFalse();
        store.ShouldBeEmpty();
        tidy3d.Verify(s => s.EstimateCostAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GuidedSetupSuccess_ClearsUnavailableState_AndSolves()
    {
        var meep = new Mock<IFdtdSMatrixService>();
        meep.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(FdtdAvailability.Unavailable(
                "Engine isn't running.", FdtdUnavailableReason.EngineNotRunning));
        meep.Setup(s => s.SolveAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());
        var setupDialog = new Mock<IDockerSetupDialogService>();
        setupDialog.Setup(d => d.ShowAsync(
                It.IsAny<FdtdAvailability>(),
                It.IsAny<Func<CancellationToken, Task<FdtdAvailability>>>()))
            .ReturnsAsync(true);
        var selection = NewSelection(meep.Object, ReadyCloudService().Object);
        var store = new Dictionary<string, ComponentSMatrixData>();
        var vm = new ComponentSettingsDialogViewModel(
            Mock.Of<IFileDialogService>(),
            fdtdService: meep.Object,
            fdtdRequestFactory: FakeFactory(),
            dockerSetupDialog: setupDialog.Object,
            backendSelection: selection);
        vm.Configure("comp", "comp", "Comp", store,
            liveComponent: TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins());

        await vm.RecalculateSMatrixCommand.ExecuteAsync(null);

        store.ShouldContainKey("comp");
        selection.IsCurrentBackendUnavailable.ShouldBeFalse();
        vm.RecalculateSMatrixCommand.CanExecute(null).ShouldBeTrue();
    }
}
