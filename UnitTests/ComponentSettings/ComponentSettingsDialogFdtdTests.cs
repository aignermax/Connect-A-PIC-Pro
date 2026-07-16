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
    public async Task RecalculateSMatrix_WarnsAboutWavelengthsTheRunDidNotCover()
    {
        // #582: the test component is defined at 980/1310/1550 nm but the (fake)
        // FDTD run only returns 1550 → 980/1310 keep their old values and the
        // status must say so instead of leaving a silently mixed effective matrix.
        var service = new Mock<IFdtdSMatrixService>();
        service.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(FdtdAvailability.Available("ready"));
        service.Setup(s => s.SolveAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new FdtdSMatrixResult
               {
                   Success = true,
                   Ports = new[] { "in", "out" }, // match the component's pin names → applies cleanly
                   Wavelengths = new[] { 1.55 },
                   Entries = new[]
                   {
                       new FdtdSEntry { Key = "out@0,in@0", Values = new[] { new Complex(0.95, 0.0) } },
                       new FdtdSEntry { Key = "in@0,out@0", Values = new[] { new Complex(0.95, 0.0) } },
                   },
                   EnergySumPerInput = new Dictionary<string, double>(),
               });

        var vm = new ComponentSettingsDialogViewModel(
            Mock.Of<IFileDialogService>(),
            fdtdService: service.Object,
            fdtdRequestFactory: FakeFactory());
        vm.Configure("comp", "comp", "Comp", new Dictionary<string, ComponentSMatrixData>(),
            liveComponent: TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins());

        await vm.RecalculateSMatrixCommand.ExecuteAsync(null);

        vm.SolverStatus.ShouldContain("FDTD done");
        vm.SolverStatus.ShouldContain("Not covered");
        vm.SolverStatus.ShouldContain("980");
        vm.SolverStatus.ShouldContain("1310");
        vm.SolverStatus.ShouldNotContain("1550 nm"); // the covered wavelength is not stale
    }

    [Fact]
    public async Task RecalculateSMatrix_NoStaleWarning_WhenRunCoversAllWavelengths()
    {
        var service = new Mock<IFdtdSMatrixService>();
        service.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(FdtdAvailability.Available("ready"));
        service.Setup(s => s.SolveAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new FdtdSMatrixResult
               {
                   Success = true,
                   Ports = new[] { "in", "out" },
                   Wavelengths = new[] { 0.98, 1.31, 1.55 }, // covers every defined wavelength
                   Entries = new[]
                   {
                       new FdtdSEntry { Key = "out@0,in@0", Values = new[] { new Complex(0.9, 0.0), new Complex(0.9, 0.0), new Complex(0.9, 0.0) } },
                       new FdtdSEntry { Key = "in@0,out@0", Values = new[] { new Complex(0.9, 0.0), new Complex(0.9, 0.0), new Complex(0.9, 0.0) } },
                   },
                   EnergySumPerInput = new Dictionary<string, double>(),
               });

        var vm = new ComponentSettingsDialogViewModel(
            Mock.Of<IFileDialogService>(),
            fdtdService: service.Object,
            fdtdRequestFactory: FakeFactory());
        vm.Configure("comp", "comp", "Comp", new Dictionary<string, ComponentSMatrixData>(),
            liveComponent: TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins());

        await vm.RecalculateSMatrixCommand.ExecuteAsync(null);

        vm.SolverStatus.ShouldContain("FDTD done");
        vm.SolverStatus.ShouldNotContain("Not covered");
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
    public async Task RecalculateSMatrix_OnSuccess_PropagatesToTemplate_WhenSinkAccepts()
    {
        var service = new Mock<IFdtdSMatrixService>();
        service.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(FdtdAvailability.Available("ready"));
        service.Setup(s => s.SolveAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(SuccessResult());
        var store = new Dictionary<string, ComponentSMatrixData>();
        ComponentSMatrixData? propagatedData = null;

        var vm = new ComponentSettingsDialogViewModel(
            Mock.Of<IFileDialogService>(),
            fdtdService: service.Object,
            fdtdRequestFactory: FakeFactory());
        vm.Configure("comp", "comp", "Comp", store,
            liveComponent: TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins(),
            propagateToTemplate: data => { propagatedData = data; return true; });

        await vm.RecalculateSMatrixCommand.ExecuteAsync(null);

        // The sink receives exactly the stored data, so template + instance stay in sync.
        propagatedData.ShouldBeSameAs(store["comp"]);
        vm.SolverStatus.ShouldContain("all instances of this component type");
        vm.StatusText.ShouldContain("all instances of this component type");
    }

    [Fact]
    public async Task RecalculateSMatrix_OnSuccess_StaysInstanceScoped_WhenSinkDeclines()
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
            liveComponent: TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins(),
            propagateToTemplate: _ => false);

        await vm.RecalculateSMatrixCommand.ExecuteAsync(null);

        store.ShouldContainKey("comp");
        vm.SolverStatus.ShouldNotContain("all instances");
        vm.StatusText.ShouldNotContain("all instances");
    }

    [Fact]
    public async Task RecalculateSMatrix_OnFailure_DoesNotPropagateToTemplate()
    {
        var service = new Mock<IFdtdSMatrixService>();
        service.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(FdtdAvailability.Available("ready"));
        service.Setup(s => s.SolveAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(FdtdSMatrixResult.Fail("boom"));
        var propagateCalled = false;

        var vm = new ComponentSettingsDialogViewModel(
            Mock.Of<IFileDialogService>(),
            fdtdService: service.Object,
            fdtdRequestFactory: FakeFactory());
        vm.Configure("comp", "comp", "Comp", new Dictionary<string, ComponentSMatrixData>(),
            liveComponent: TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins(),
            propagateToTemplate: _ => propagateCalled = true);

        await vm.RecalculateSMatrixCommand.ExecuteAsync(null);

        propagateCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task RecalculateSMatrix_WhenGeometryExportThrows_ShowsTheActionableMessage_AndStoresNothing()
    {
        // The request factory throws with a user-actionable message (e.g. missing cspdk).
        // The dialog must show that message — not a generic dead-end — and must not solve.
        const string factoryMessage =
            "The foundry package 'cspdk' is not installed in the active Python environment — " +
            "open Settings → Python Environments and re-run Install to add it.";
        var service = new Mock<IFdtdSMatrixService>();
        service.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(FdtdAvailability.Available("ready"));
        var store = new Dictionary<string, ComponentSMatrixData>();

        var vm = new ComponentSettingsDialogViewModel(
            Mock.Of<IFileDialogService>(),
            fdtdService: service.Object,
            fdtdRequestFactory: (_, _) => throw new InvalidOperationException(factoryMessage));
        vm.Configure("comp", "comp", "Comp", store,
            liveComponent: TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins());

        await vm.RecalculateSMatrixCommand.ExecuteAsync(null);

        vm.SolverStatus.ShouldBe(factoryMessage);
        store.ShouldBeEmpty();
        vm.IsComputing.ShouldBeFalse();
        service.Verify(s => s.SolveAsync(It.IsAny<FdtdSMatrixRequest>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecalculateSMatrix_WhenFactoryReturnsNull_ShowsActionableStatus_NotAGenericDeadEnd()
    {
        var service = new Mock<IFdtdSMatrixService>();
        service.Setup(s => s.CheckAvailabilityAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(FdtdAvailability.Available("ready"));

        var vm = new ComponentSettingsDialogViewModel(
            Mock.Of<IFileDialogService>(),
            fdtdService: service.Object,
            fdtdRequestFactory: (_, _) => Task.FromResult<FdtdSMatrixRequest?>(null));
        vm.Configure("comp", "comp", "Comp", new Dictionary<string, ComponentSMatrixData>(),
            liveComponent: TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins());

        await vm.RecalculateSMatrixCommand.ExecuteAsync(null);

        vm.SolverStatus.ShouldContain("Could not export this component's geometry");
        vm.SolverStatus.ShouldContain("Settings → Python Environments"); // where to go next
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
