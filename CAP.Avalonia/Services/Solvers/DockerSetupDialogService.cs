using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CAP.Avalonia.ViewModels.Solvers.DockerSetup;
using CAP.Avalonia.Views.Dialogs;
using CAP_Core.Solvers.Fdtd;

namespace CAP.Avalonia.Services.Solvers;

/// <summary>
/// Avalonia implementation of <see cref="IDockerSetupDialogService"/>: spins up
/// a modal <see cref="DockerSetupDialog"/> rooted at the application's main
/// window and awaits its result.
/// </summary>
public class DockerSetupDialogService : IDockerSetupDialogService
{
    private readonly IUrlLauncher _urlLauncher;

    /// <summary>Initialises the service with the shared URL launcher.</summary>
    public DockerSetupDialogService(IUrlLauncher urlLauncher)
    {
        _urlLauncher = urlLauncher;
    }

    /// <inheritdoc/>
    public async Task<bool> ShowAsync(
        FdtdAvailability availability,
        Func<CancellationToken, Task<FdtdAvailability>> recheck)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return false;
        if (desktop.MainWindow is not { } owner)
            return false;

        var vm = new DockerSetupViewModel(recheck, _urlLauncher);
        vm.Initialize(availability);

        var dialog = new DockerSetupDialog { DataContext = vm };
        return await dialog.ShowDialog<bool>(owner);
    }
}
