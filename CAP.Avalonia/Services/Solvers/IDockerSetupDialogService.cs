using CAP_Core.Solvers.Fdtd;

namespace CAP.Avalonia.Services.Solvers;

/// <summary>
/// Shows the guided "Set up FDTD" dialog when the Docker backend is unavailable,
/// letting the user install/start Docker and re-check without leaving the flow.
/// Abstracted so ViewModels stay unit-testable without Avalonia windows.
/// </summary>
public interface IDockerSetupDialogService
{
    /// <summary>
    /// Opens the setup dialog seeded with <paramref name="availability"/>.
    /// </summary>
    /// <param name="availability">The unavailable probe result that triggered the dialog.</param>
    /// <param name="recheck">Re-probe invoked by the dialog's "Check again" button.</param>
    /// <returns>True when Docker became available (caller should continue its run);
    /// false when the user cancelled while Docker was still unavailable.</returns>
    Task<bool> ShowAsync(
        FdtdAvailability availability,
        Func<CancellationToken, Task<FdtdAvailability>> recheck);
}
