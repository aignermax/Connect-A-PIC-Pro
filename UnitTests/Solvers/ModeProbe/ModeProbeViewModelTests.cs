using CAP.Avalonia.ViewModels.Solvers.ModeProbe;
using CAP_Core.Components.Process;
using CAP_Core.Solvers.ModeProbe;
using CAP_Core.Solvers.ModeSolver;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Solvers.ModeProbe;

public class ModeProbeViewModelTests
{
    private static readonly ProcessFingerprint SoiProcess = new(
        CoreMaterial: "Si", CoreThicknessNm: 220, Cladding: "SiO2",
        DesignWavelengthNm: 1550, ProcessName: "SOI 220");

    private static ModeSolverResult SuccessResult(double nEff = 2.4) => new()
    {
        Success = true,
        BackendUsed = "femwell",
        Modes = new[]
        {
            new ModeSolverModeEntry
            {
                Wavelength = 1.55, ModeIndex = 0, NEff = nEff,
                NGroup = 4.2, Polarisation = "TE",
            },
        },
    };

    private static (ModeProbeViewModel Vm, Mock<IModeSolverService> Service) CreateVm(
        ModeSolverResult? result = null)
    {
        var service = new Mock<IModeSolverService>();
        service.Setup(s => s.SolveAsync(It.IsAny<ModeSolverRequest>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(result ?? SuccessResult());

        var vm = new ModeProbeViewModel(service.Object, new CrossSectionDefaultsStore())
        {
            GetActiveProcessFingerprint = () => SoiProcess,
            GetSimulationWavelengthNm = () => 1310,
        };
        return (vm, service);
    }

    [Fact]
    public void ConnectionProbe_AutoSolvesWithoutManualGeometry()
    {
        var (vm, service) = CreateVm();

        vm.Open(ProbeTarget.ForConnection(0.5, 12.3), 100, 200);

        vm.IsOpen.ShouldBeTrue();
        vm.PanelX.ShouldBe(100);
        vm.PanelY.ShouldBe(200);
        vm.HasResult.ShouldBeTrue();
        vm.NEff.ShouldBe(2.4);
        vm.NGroup.ShouldBe(4.2);
        vm.Polarisation.ShouldBe("TE");
        vm.MfdText.ShouldContain("µm");
        vm.IsGeometryAssumed.ShouldBeFalse(); // width + full PDK: nothing assumed
        vm.CrossSectionText.ShouldContain("0.5");
        vm.WavelengthNm.ShouldBe(1310); // picked up from simulation wavelength

        service.Verify(s => s.SolveAsync(
            It.Is<ModeSolverRequest>(r =>
                r.Width == 0.5 &&
                Math.Abs(r.Height - 0.22) < 1e-9 &&
                r.NumModes == 1 &&
                Math.Abs(r.Wavelengths[0] - 1.31) < 1e-9),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void NoProcess_FlagsGeometryAssumed()
    {
        var (vm, _) = CreateVm();
        vm.GetActiveProcessFingerprint = () => null;

        vm.Open(ProbeTarget.ForConnection(0.5, 10), 0, 0);

        vm.IsGeometryAssumed.ShouldBeTrue();
        vm.GeometrySourceText.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void CouplerProbe_ShowsFiberOverlap()
    {
        var (vm, _) = CreateVm();

        vm.Open(ProbeTarget.ForComponent("Grating Coupler TE 1550", 0.5), 0, 0);

        vm.ShowFiberOverlap.ShouldBeTrue();
        vm.IsInterferenceRegion.ShouldBeFalse();
        vm.OverlapPercent.ShouldBeGreaterThan(0);
        vm.OverlapPercent.ShouldBeLessThan(100);
        vm.OverlapLossDb.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void FiberMfdChange_RecomputesOverlap()
    {
        var (vm, _) = CreateVm();
        vm.Open(ProbeTarget.ForComponent("Edge Coupler", 0.5), 0, 0);
        var lossAtDefault = vm.OverlapLossDb;

        vm.FiberMfdUm = 3.0; // lensed fiber: much better matched to a small mode

        vm.OverlapLossDb.ShouldBeLessThan(lossAtDefault);
    }

    [Fact]
    public void InterferenceRegion_ShowsNoticeAndSkipsSolve()
    {
        var (vm, service) = CreateVm();

        vm.Open(ProbeTarget.ForComponent("MMI 1x2", 0.5), 0, 0);

        vm.IsInterferenceRegion.ShouldBeTrue();
        vm.HasResult.ShouldBeFalse();
        vm.StatusText.ShouldContain("FDTD");
        service.Verify(s => s.SolveAsync(
            It.IsAny<ModeSolverRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void MissingBackend_ProducesActionableMessage()
    {
        var (vm, _) = CreateVm(new ModeSolverResult
        {
            Success = false,
            Error = "Backend 'tidy3d' is not installed.",
            MissingBackend = "tidy3d",
        });

        vm.Open(ProbeTarget.ForConnection(0.5, 10), 0, 0);

        vm.HasResult.ShouldBeFalse();
        vm.StatusText.ShouldContain("pip install tidy3d");
    }

    [Fact]
    public void MissingBackend_WithInstallHook_InstallsAndRetries()
    {
        var service = new Mock<IModeSolverService>();
        service.SetupSequence(s => s.SolveAsync(It.IsAny<ModeSolverRequest>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new ModeSolverResult
               {
                   Success = false, Error = "backend missing", MissingBackend = "gdsfactory[femwell]",
               })
               .ReturnsAsync(SuccessResult()); // retry after install succeeds

        string? installed = null;
        var vm = new ModeProbeViewModel(service.Object, new CrossSectionDefaultsStore())
        {
            GetActiveProcessFingerprint = () => SoiProcess,
            EnsureBackendAsync = (pkg, _, _) => { installed = pkg; return Task.FromResult(true); },
        };

        vm.Open(ProbeTarget.ForConnection(0.5, 10), 0, 0);

        installed.ShouldBe("gdsfactory[femwell]");
        vm.HasResult.ShouldBeTrue();
        vm.NEff.ShouldBe(2.4);
        service.Verify(s => s.SolveAsync(
            It.IsAny<ModeSolverRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public void MissingBackend_InstallDeclined_KeepsActionableMessage()
    {
        var vm = new ModeProbeViewModel(
            CreateVm(new ModeSolverResult
            {
                Success = false, Error = "backend missing", MissingBackend = "tidy3d",
            }).Service.Object,
            new CrossSectionDefaultsStore())
        {
            GetActiveProcessFingerprint = () => SoiProcess,
            EnsureBackendAsync = (_, _, _) => Task.FromResult(false), // install fails/declined
        };

        vm.Open(ProbeTarget.ForConnection(0.5, 10), 0, 0);

        vm.HasResult.ShouldBeFalse();
        vm.StatusText.ShouldContain("pip install tidy3d");
    }

    [Fact]
    public void Close_HidesPanel()
    {
        var (vm, _) = CreateVm();
        vm.Open(ProbeTarget.ForConnection(0.5, 10), 0, 0);

        vm.CloseCommand.Execute(null);

        vm.IsOpen.ShouldBeFalse();
    }
}
