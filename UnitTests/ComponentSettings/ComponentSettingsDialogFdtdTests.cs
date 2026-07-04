using System.Numerics;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Solvers;
using CAP.Avalonia.ViewModels.ComponentSettings;
using CAP_Core.Components.Core;
using CAP_Core.Solvers.Fdtd;
using CAP_DataAccess.Persistence.PIR;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.ComponentSettings;

/// <summary>
/// Verifies the FDTD "Recalculate S-matrix" command on the Component Settings
/// dialog: gating, success (stores + status) and failure (status, no store).
/// </summary>
public class ComponentSettingsDialogFdtdTests
{
    private static Func<Component, CancellationToken, Task<FdtdSMatrixRequest?>> FakeFactory() =>
        (_, _) => Task.FromResult<FdtdSMatrixRequest?>(new FdtdSMatrixRequest
        {
            Ports = new[] { new FdtdPort { Name = "o1" }, new FdtdPort { Name = "o2" } },
        });

    private static FdtdSMatrixResult SuccessResult() => new()
    {
        Success = true,
        Is3D = false,
        Ports = new[] { "o1", "o2" },
        Wavelengths = new[] { 1.55 },
        Entries = new[]
        {
            new FdtdSEntry { Key = "o2@0,o1@0", Values = new[] { new Complex(0.95, 0.0) } },
            new FdtdSEntry { Key = "o1@0,o2@0", Values = new[] { new Complex(0.95, 0.0) } },
        },
        EnergySumPerInput = new Dictionary<string, double> { ["o1@0"] = 0.97, ["o2@0"] = 0.97 },
    };

    [Fact]
    public void CanRecalculate_IsFalse_WithoutFdtdWiring()
    {
        var vm = new ComponentSettingsDialogViewModel(Mock.Of<IFileDialogService>());
        vm.Configure("c", "c", "C", new Dictionary<string, ComponentSMatrixData>(),
            liveComponent: TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins());

        vm.CanRecalculate.ShouldBeFalse();
    }

    [Fact]
    public async Task RecalculateSMatrix_OnSuccess_StoresDataAndReportsStatus()
    {
        var service = new Mock<IFdtdSMatrixService>();
        service.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(FdtdAvailability.Available("ready"));
        service.Setup(s => s.SolveAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(SuccessResult());
        var store = new Dictionary<string, ComponentSMatrixData>();

        var vm = new ComponentSettingsDialogViewModel(
            Mock.Of<IFileDialogService>(),
            fdtdService: service.Object,
            fdtdRequestFactory: FakeFactory());
        vm.Configure("comp", "comp", "Comp", store,
            liveComponent: TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins());

        vm.CanRecalculate.ShouldBeTrue();
        await vm.RecalculateSMatrixCommand.ExecuteAsync(null);

        store.ShouldContainKey("comp");
        store["comp"].Wavelengths.ShouldContainKey("1550");
        vm.SolverStatus.ShouldContain("FDTD done");
        vm.IsComputing.ShouldBeFalse();
    }

    [Fact]
    public async Task RecalculateSMatrix_OnFailure_SurfacesHintAndStoresNothing()
    {
        var service = new Mock<IFdtdSMatrixService>();
        service.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(FdtdAvailability.Available("ready"));
        service.Setup(s => s.SolveAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(FdtdSMatrixResult.Fail("image build failed", missingDependency: "docker"));
        var store = new Dictionary<string, ComponentSMatrixData>();

        var vm = new ComponentSettingsDialogViewModel(
            Mock.Of<IFileDialogService>(),
            fdtdService: service.Object,
            fdtdRequestFactory: FakeFactory());
        vm.Configure("comp", "comp", "Comp", store,
            liveComponent: TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins());

        await vm.RecalculateSMatrixCommand.ExecuteAsync(null);

        store.ShouldNotContainKey("comp");
        vm.SolverStatus.ShouldContain("docker");
    }

    [Fact]
    public async Task RecalculateSMatrix_WhenDockerUnavailable_ShowsHintAndDoesNotSolve()
    {
        var service = new Mock<IFdtdSMatrixService>();
        service.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(FdtdAvailability.Unavailable("Docker is not installed. Install Docker Desktop."));
        var store = new Dictionary<string, ComponentSMatrixData>();

        var vm = new ComponentSettingsDialogViewModel(
            Mock.Of<IFileDialogService>(),
            fdtdService: service.Object,
            fdtdRequestFactory: FakeFactory());
        vm.Configure("comp", "comp", "Comp", store,
            liveComponent: TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins());

        await vm.RecalculateSMatrixCommand.ExecuteAsync(null);

        vm.SolverStatus.ShouldContain("Docker Desktop");
        store.ShouldNotContainKey("comp");
        vm.IsComputing.ShouldBeFalse();
        // The solver must not be invoked when the backend isn't available.
        service.Verify(s => s.SolveAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecalculateSMatrix_WhenDockerUnavailable_OpensGuidedSetupDialog()
    {
        var unavailable = FdtdAvailability.Unavailable(
            "Docker is not installed.", FdtdUnavailableReason.NotInstalled);
        var service = new Mock<IFdtdSMatrixService>();
        service.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(unavailable);
        var setupDialog = new Mock<IDockerSetupDialogService>();
        setupDialog.Setup(d => d.ShowAsync(
                It.IsAny<FdtdAvailability>(),
                It.IsAny<Func<CancellationToken, Task<FdtdAvailability>>>()))
            .ReturnsAsync(false); // user cancelled — Docker still missing

        var vm = new ComponentSettingsDialogViewModel(
            Mock.Of<IFileDialogService>(),
            fdtdService: service.Object,
            fdtdRequestFactory: FakeFactory(),
            dockerSetupDialog: setupDialog.Object);
        vm.Configure("comp", "comp", "Comp", new Dictionary<string, ComponentSMatrixData>(),
            liveComponent: TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins());

        await vm.RecalculateSMatrixCommand.ExecuteAsync(null);

        setupDialog.Verify(d => d.ShowAsync(
            unavailable,
            It.IsAny<Func<CancellationToken, Task<FdtdAvailability>>>()), Times.Once);
        service.Verify(s => s.SolveAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
        vm.SolverStatus.ShouldContain("Docker is not installed");
    }

    [Fact]
    public async Task RecalculateSMatrix_WhenSetupDialogFixesDocker_ContinuesRecompute()
    {
        var service = new Mock<IFdtdSMatrixService>();
        service.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(FdtdAvailability.Unavailable(
                   "Engine isn't running.", FdtdUnavailableReason.EngineNotRunning));
        service.Setup(s => s.SolveAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(SuccessResult());
        var setupDialog = new Mock<IDockerSetupDialogService>();
        setupDialog.Setup(d => d.ShowAsync(
                It.IsAny<FdtdAvailability>(),
                It.IsAny<Func<CancellationToken, Task<FdtdAvailability>>>()))
            .ReturnsAsync(true); // user started Docker; re-check succeeded
        var store = new Dictionary<string, ComponentSMatrixData>();

        var vm = new ComponentSettingsDialogViewModel(
            Mock.Of<IFileDialogService>(),
            fdtdService: service.Object,
            fdtdRequestFactory: FakeFactory(),
            dockerSetupDialog: setupDialog.Object);
        vm.Configure("comp", "comp", "Comp", store,
            liveComponent: TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins());

        await vm.RecalculateSMatrixCommand.ExecuteAsync(null);

        store.ShouldContainKey("comp");
        vm.SolverStatus.ShouldContain("FDTD done");
    }
}
