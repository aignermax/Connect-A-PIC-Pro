using CAP.Avalonia.ViewModels.Solvers;
using CAP_Core.Solvers.ModeSolver;
using Moq;
using Shouldly;

namespace UnitTests.Solvers.ModeSolver;

/// <summary>
/// Covers the manual mode-solver dialog's missing-backend auto-install flow (#691):
/// install-then-retry on success, and the actionable message when the install is
/// declined/fails.
/// </summary>
public class ModeSolverViewModelTests
{
    private static ModeSolverResult Success() => new()
    {
        Success = true,
        BackendUsed = "femwell",
        Modes = new[]
        {
            new ModeSolverModeEntry { Wavelength = 1.55, ModeIndex = 0, NEff = 2.4, NGroup = 4.2, Polarisation = "TE" },
        },
    };

    private static ModeSolverResult BackendMissing(string pkg) => new()
    {
        Success = false, Error = "backend missing", MissingBackend = pkg,
    };

    [Fact]
    public async Task MissingBackend_WithInstallHook_InstallsAndRetries()
    {
        var service = new Mock<IModeSolverService>();
        service.SetupSequence(s => s.SolveAsync(It.IsAny<ModeSolverRequest>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(BackendMissing("gdsfactory[femwell]"))
               .ReturnsAsync(Success());

        string? installed = null;
        var vm = new ModeSolverViewModel(service.Object)
        {
            EnsureBackendAsync = (pkg, _, _) => { installed = pkg; return Task.FromResult(true); },
        };

        await vm.SolveCommand.ExecuteAsync(null);

        installed.ShouldBe("gdsfactory[femwell]");
        vm.HasResult.ShouldBeTrue();
        vm.Modes.Count.ShouldBe(1);
        service.Verify(s => s.SolveAsync(
            It.IsAny<ModeSolverRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task MissingBackend_InstallDeclined_KeepsActionableMessageAndDoesNotRetry()
    {
        var service = new Mock<IModeSolverService>();
        service.Setup(s => s.SolveAsync(It.IsAny<ModeSolverRequest>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(BackendMissing("tidy3d"));

        var vm = new ModeSolverViewModel(service.Object)
        {
            EnsureBackendAsync = (_, _, _) => Task.FromResult(false), // install failed/declined
        };

        await vm.SolveCommand.ExecuteAsync(null);

        vm.HasResult.ShouldBeFalse();
        vm.StatusText.ShouldContain("pip install tidy3d");
        service.Verify(s => s.SolveAsync(
            It.IsAny<ModeSolverRequest>(), It.IsAny<CancellationToken>()), Times.Once); // no retry
    }

    [Fact]
    public async Task MissingBackend_NoHook_ShowsPipHint()
    {
        var service = new Mock<IModeSolverService>();
        service.Setup(s => s.SolveAsync(It.IsAny<ModeSolverRequest>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(BackendMissing("EMpy"));

        var vm = new ModeSolverViewModel(service.Object); // EnsureBackendAsync unset

        await vm.SolveCommand.ExecuteAsync(null);

        vm.HasResult.ShouldBeFalse();
        vm.StatusText.ShouldContain("pip install EMpy");
    }
}
