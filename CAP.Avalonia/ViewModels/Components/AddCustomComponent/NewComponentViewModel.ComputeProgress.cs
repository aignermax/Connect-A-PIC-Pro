using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CAP_Core.Solvers.Fdtd;

namespace CAP.Avalonia.ViewModels.Components.AddCustomComponent;

public partial class NewComponentViewModel
{
    private CancellationTokenSource? _computeCts;

    public void CancelCompute() => _computeCts?.Cancel();

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

    private static string Shorten(string s) => s.Length <= 80 ? s : s[..80] + "…";
}
