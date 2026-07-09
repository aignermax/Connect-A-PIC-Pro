using System.Numerics;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.ComponentSettings;
using CAP_Core.Components.Core;
using CAP_Core.Solvers.Fdtd;
using CAP_DataAccess.Persistence.PIR;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.ComponentSettings;

/// <summary>
/// Verifies the cloud-cost confirmation flow of the FDTD recompute: paid
/// backends (Tidy3D) must pause after the estimate and only submit after an
/// explicit confirmation; cancelling must leave no stuck state.
/// </summary>
public class ComponentSettingsDialogCloudFlowTests
{
    /// <summary>Test double that is both a solver and a cost estimator, like Tidy3D.</summary>
    public abstract class CloudService : IFdtdSMatrixService, IFdtdCostEstimator
    {
        /// <inheritdoc/>
        public abstract Task<FdtdSMatrixResult> SolveAsync(
            FdtdSMatrixRequest request, IProgress<string>? progress = null, CancellationToken ct = default);

        /// <inheritdoc/>
        public abstract Task<FdtdAvailability> CheckAvailabilityAsync(CancellationToken ct = default);

        /// <inheritdoc/>
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
        NewDialog(CloudService service)
    {
        var store = new Dictionary<string, ComponentSMatrixData>();
        var vm = new ComponentSettingsDialogViewModel(
            Mock.Of<IFileDialogService>(),
            fdtdService: service,
            fdtdRequestFactory: FakeFactory());
        vm.Configure("comp", "comp", "Comp", store,
            liveComponent: TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins());
        return (vm, store);
    }

    [Fact]
    public async Task Recalculate_WithCloudBackend_PausesForConfirmationWithoutSolving()
    {
        var service = ReadyCloudService();
        var (vm, store) = NewDialog(service.Object);

        await vm.RecalculateSMatrixCommand.ExecuteAsync(null);

        vm.IsAwaitingCloudConfirmation.ShouldBeTrue();
        vm.CloudCostText.ShouldContain("1.50");
        vm.CloudCostText.ShouldContain("FlexCredits");
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
        var (vm, _) = NewDialog(service.Object);

        await vm.RecalculateSMatrixCommand.ExecuteAsync(null);

        vm.IsAwaitingCloudConfirmation.ShouldBeTrue();
        vm.CloudCostText.ShouldContain("server unreachable");
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
}
