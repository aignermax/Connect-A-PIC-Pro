using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CAP_Core.Solvers.Fdtd;

namespace CAP.Avalonia.ViewModels.Components.AddCustomComponent;

/// <summary>
/// Live-progress and cancellation support for
/// <see cref="NewComponentViewModel.ComputeSMatrix"/>: runs the FDTD solve while keeping
/// <see cref="NewComponentViewModel.StatusText"/> alive with a once-per-second elapsed-time
/// heartbeat plus the latest Meep progress line (split out purely to keep
/// <c>NewComponentViewModel.Save.cs</c> under the project's line-count limit; still one
/// partial class, one responsibility).
/// </summary>
public partial class NewComponentViewModel
{
    /// <summary>Cancellation source for the FDTD solve currently in flight, if any.</summary>
    private CancellationTokenSource? _computeCts;

    /// <summary>
    /// Cancels a running <see cref="NewComponentViewModel.ComputeSMatrix"/> solve — e.g. when
    /// the host window is closed while FDTD is still running. A no-op if nothing is running.
    /// </summary>
    public void CancelCompute() => _computeCts?.Cancel();

    /// <summary>
    /// Runs the FDTD solve while keeping <see cref="NewComponentViewModel.StatusText"/> alive:
    /// a once-per-second elapsed-time heartbeat (so a multi-minute Docker image build/solve
    /// never looks frozen) plus the latest progress line streamed from Meep. The progress
    /// callback updates <see cref="NewComponentViewModel.StatusText"/> directly as soon as a
    /// line is reported — it does not wait for the heartbeat timer to tick, so the status is
    /// visible even where nothing pumps the UI dispatcher (e.g. in tests).
    /// </summary>
    private async Task<FdtdSMatrixResult> RunSolveWithLiveStatusAsync(FdtdSMatrixRequest request, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        const string baseMessage = "Running FDTD (Meep). First run builds the solver image (several minutes)";

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => StatusText = $"{baseMessage} — {stopwatch.Elapsed:m\\:ss} elapsed…";
        StatusText = $"{baseMessage}…";
        timer.Start();

        var progress = new Progress<string>(
            line => StatusText = $"FDTD running ({stopwatch.Elapsed:m\\:ss}): {Shorten(line)}");
        try
        {
            return await _fdtd!.SolveAsync(request, progress, ct);
        }
        finally
        {
            timer.Stop();
        }
    }

    /// <summary>Truncates a solver progress line so a single noisy line can't blow up the status bar.</summary>
    private static string Shorten(string s) => s.Length <= 80 ? s : s[..80] + "…";
}
