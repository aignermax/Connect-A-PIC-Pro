using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Solvers.DockerSetup;
using CAP_Core.Solvers.Fdtd;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Solvers.DockerSetup;

/// <summary>
/// Verifies the guided "Set up FDTD" dialog ViewModel (issue #649): platform-aware
/// guidance branches, clipboard content, URL launcher invocation and the
/// "Check again" state transitions.
/// </summary>
public class DockerSetupViewModelTests
{
    private static FdtdAvailability NotInstalled() =>
        FdtdAvailability.Unavailable("Docker is not installed.", FdtdUnavailableReason.NotInstalled);

    private static FdtdAvailability EngineStopped() =>
        FdtdAvailability.Unavailable("Engine isn't running.", FdtdUnavailableReason.EngineNotRunning);

    private static DockerSetupViewModel CreateVm(
        bool isLinux,
        Func<CancellationToken, Task<FdtdAvailability>>? check = null,
        IUrlLauncher? launcher = null)
    {
        check ??= _ => Task.FromResult(NotInstalled());
        return new DockerSetupViewModel(check, launcher ?? Mock.Of<IUrlLauncher>(), isLinux);
    }

    [Fact]
    public void NotInstalled_OnLinux_ShowsCopyableInstallCommands()
    {
        var vm = CreateVm(isLinux: true);
        vm.Initialize(NotInstalled());

        vm.ShowLinuxInstall.ShouldBeTrue();
        vm.ShowDesktopInstall.ShouldBeFalse();
        vm.ShowLinuxStart.ShouldBeFalse();
        vm.ShowDesktopStart.ShouldBeFalse();
        vm.StatusMessage.ShouldBe("Docker is not installed.");
    }

    [Fact]
    public void NotInstalled_OnDesktopOs_ShowsDownloadButton()
    {
        var vm = CreateVm(isLinux: false);
        vm.Initialize(NotInstalled());

        vm.ShowDesktopInstall.ShouldBeTrue();
        vm.ShowLinuxInstall.ShouldBeFalse();
        vm.ShowLinuxStart.ShouldBeFalse();
        vm.ShowDesktopStart.ShouldBeFalse();
    }

    [Fact]
    public void EngineStopped_OnLinux_ShowsSystemctlStartCommand()
    {
        var vm = CreateVm(isLinux: true);
        vm.Initialize(EngineStopped());

        vm.ShowLinuxStart.ShouldBeTrue();
        vm.ShowLinuxInstall.ShouldBeFalse();
        vm.ShowDesktopStart.ShouldBeFalse();
    }

    [Fact]
    public void EngineStopped_OnDesktopOs_ShowsStartDockerDesktopHint()
    {
        var vm = CreateVm(isLinux: false);
        vm.Initialize(EngineStopped());

        vm.ShowDesktopStart.ShouldBeTrue();
        vm.ShowLinuxStart.ShouldBeFalse();
        vm.ShowDesktopInstall.ShouldBeFalse();
    }

    [Fact]
    public void UnknownReason_IsTreatedAsEngineStopped()
    {
        var vm = CreateVm(isLinux: true);
        vm.Initialize(FdtdAvailability.Unavailable("something is off"));

        vm.IsEngineStopped.ShouldBeTrue();
        vm.IsNotInstalled.ShouldBeFalse();
    }

    [Fact]
    public void OpenDownloadPage_InvokesUrlLauncherWithInstallPage()
    {
        var launcher = new Mock<IUrlLauncher>();
        var vm = CreateVm(isLinux: false, launcher: launcher.Object);

        vm.OpenDownloadPageCommand.Execute(null);

        launcher.Verify(l => l.Open(DockerSetupViewModel.DockerDesktopUrl), Times.Once);
    }

    [Fact]
    public async Task CopyInstallCommands_PutsBothLinuxCommandsOnClipboard()
    {
        var vm = CreateVm(isLinux: true);
        string? copied = null;
        vm.CopyToClipboard = text => { copied = text; return Task.CompletedTask; };

        await vm.CopyInstallCommandsCommand.ExecuteAsync(null);

        copied.ShouldNotBeNull();
        copied.ShouldContain("curl -fsSL https://get.docker.com | sudo sh");
        copied.ShouldContain("sudo usermod -aG docker $USER");
        vm.CopyFeedback.ShouldBe("Copied to clipboard.");
    }

    [Fact]
    public async Task CopyStartCommand_PutsSystemctlStartOnClipboard()
    {
        var vm = CreateVm(isLinux: true);
        string? copied = null;
        vm.CopyToClipboard = text => { copied = text; return Task.CompletedTask; };

        await vm.CopyStartCommandCommand.ExecuteAsync(null);

        copied.ShouldBe("sudo systemctl start docker");
    }

    [Fact]
    public async Task CheckAgain_WhenStillUnavailable_UpdatesStateWithoutClosing()
    {
        // Transition: user installed Docker (NotInstalled → EngineNotRunning).
        var vm = CreateVm(isLinux: true, check: _ => Task.FromResult(EngineStopped()));
        vm.Initialize(NotInstalled());
        var closeRequested = false;
        vm.CloseRequested += (_, _) => closeRequested = true;

        await vm.CheckAgainCommand.ExecuteAsync(null);

        closeRequested.ShouldBeFalse();
        vm.IsDockerAvailable.ShouldBeFalse();
        vm.IsNotInstalled.ShouldBeFalse();
        vm.IsEngineStopped.ShouldBeTrue();
        vm.StatusMessage.ShouldBe("Engine isn't running.");
        vm.IsChecking.ShouldBeFalse();
    }

    [Fact]
    public async Task CheckAgain_WhenAvailable_FlagsSuccessAndRequestsClose()
    {
        var vm = CreateVm(isLinux: true,
            check: _ => Task.FromResult(FdtdAvailability.Available("Docker engine 27.0 ready.")));
        vm.Initialize(EngineStopped());
        var closeRequested = false;
        vm.CloseRequested += (_, _) => closeRequested = true;

        await vm.CheckAgainCommand.ExecuteAsync(null);

        closeRequested.ShouldBeTrue();
        vm.IsDockerAvailable.ShouldBeTrue();
        vm.IsNotInstalled.ShouldBeFalse();
        vm.IsEngineStopped.ShouldBeFalse();
    }

    [Fact]
    public async Task CheckAgain_WhenProbeThrows_SurfacesErrorAndStaysOpen()
    {
        var vm = CreateVm(isLinux: true,
            check: _ => Task.FromException<FdtdAvailability>(new InvalidOperationException("boom")));
        vm.Initialize(NotInstalled());

        await vm.CheckAgainCommand.ExecuteAsync(null);

        vm.IsDockerAvailable.ShouldBeFalse();
        vm.StatusMessage.ShouldContain("boom");
        vm.IsChecking.ShouldBeFalse();
    }
}
