using System.Diagnostics;
using Avalonia.Threading;
using CAP_Core.Components.Core;
using CAP_Core.Solvers.Fdtd;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Solvers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.ComponentSettings;

/// <summary>
/// FDTD "Recalculate S-matrix" half of the dialog: instead of loading an
/// S-matrix from a file, compute it from the component's geometry with the
/// open-source Meep solver and feed the result through the same store-and-apply
/// path the file import uses.
/// </summary>
public partial class ComponentSettingsDialogViewModel
{
    private readonly IFdtdSMatrixService? _fdtdService;
    private readonly Func<Component, CancellationToken, Task<FdtdSMatrixRequest?>>? _fdtdRequestFactory;
    private readonly IDockerSetupDialogService? _dockerSetupDialog;
    private CancellationTokenSource? _recalcCts;

    /// <summary>True while an FDTD recompute is running.</summary>
    [ObservableProperty]
    private bool _isComputing;

    /// <summary>
    /// Live simulation/solver status shown in the dialog (provisioning, running,
    /// energy-conservation summary, or the error/setup hint on failure).
    /// </summary>
    [ObservableProperty]
    private string _solverStatus = string.Empty;

    /// <summary>
    /// True when FDTD recompute is wired up for this dialog instance (solver
    /// service + geometry factory present and a live component is configured).
    /// </summary>
    public bool CanRecalculate =>
        _fdtdService != null && _fdtdRequestFactory != null && _liveComponent != null;

    private bool CanRunRecalculate => CanRecalculate && !IsComputing && !IsImporting;

    partial void OnIsComputingChanged(bool value) => RecalculateSMatrixCommand.NotifyCanExecuteChanged();

    /// <summary>
    /// Cancels a running FDTD recompute. Called when the dialog is closed so the
    /// solve (and its Docker container) doesn't keep running in the background.
    /// </summary>
    public void CancelRecalculate() => _recalcCts?.Cancel();

    /// <summary>
    /// Recomputes this component's S-matrix from its geometry via FDTD and applies
    /// it like an import. Surfaces the raw solver error on failure — no silent fallback.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunRecalculate))]
    private async Task RecalculateSMatrix()
    {
        if (_fdtdService == null || _fdtdRequestFactory == null || _liveComponent == null || _storedSMatrices == null)
            return;

        IsComputing = true;
        _recalcCts = new CancellationTokenSource();

        try
        {
            // Fail fast with an actionable message if Docker isn't ready, before
            // exporting geometry / building images.
            SolverStatus = "Checking the FDTD solver (Docker)…";
            var availability = await _fdtdService.CheckAvailabilityAsync(_recalcCts.Token);
            if (!availability.IsAvailable)
            {
                SolverStatus = availability.Message;
                // Guided setup (issue #649): open the "Set up FDTD" dialog with
                // platform-specific install/start guidance and a re-check button.
                // Headless/test consumers without the dialog service keep the
                // plain error-string behaviour above.
                if (_dockerSetupDialog == null)
                    return;
                var ready = await _dockerSetupDialog.ShowAsync(
                    availability, ct => _fdtdService.CheckAvailabilityAsync(ct));
                if (!ready)
                    return;
                SolverStatus = "Docker is ready — continuing FDTD recompute…";
            }

            SolverStatus = "Preparing component geometry…";
            var request = await _fdtdRequestFactory(_liveComponent, _recalcCts.Token);
            if (request == null)
            {
                SolverStatus = "Could not export this component's geometry for FDTD.";
                return;
            }

            var result = await RunWithLiveStatusAsync(request, _recalcCts.Token);

            if (!result.Success)
            {
                // A user cancel (typically closing the dialog mid-run) comes back as a
                // failed result, not an OperationCanceledException. Closing a window is
                // intentional, not an error, so stay quiet — don't open the error console.
                if (_recalcCts?.IsCancellationRequested == true)
                {
                    SolverStatus = "FDTD recompute cancelled.";
                    NotifyCancelled();
                    return;
                }

                SolverStatus = result.MissingDependency != null
                    ? $"FDTD unavailable — '{result.MissingDependency}' is required. {result.Error}"
                    : $"FDTD failed: {result.Error}";
                _errorConsole?.LogError($"FDTD recompute failed for '{_displayName}': {result.Error}\n{result.RawStderr}");
                return;
            }

            var note = $"FDTD Meep {(result.Is3D ? "3D" : "2D")}";
            var data = FdtdSMatrixConverter.ToComponentSMatrixData(result, note);
            _storedSMatrices[_smatrixKey] = data;

            var applyResult = SMatrixOverrideApplicator.Apply(_liveComponent, data, _errorConsole);

            // Issue #580 E: when the instance geometry matches the template draft,
            // the caller-provided sink promotes the result to the template-scoped
            // (user-global) override so every instance of the type inherits it.
            var propagated = _propagateToTemplate?.Invoke(data) == true;
            SolverStatus = BuildSolverStatus(result, applyResult, propagated);
            StatusText = propagated
                ? $"Recomputed S-matrix via FDTD ({note}); applied to all instances of this component type."
                : $"Recomputed S-matrix via FDTD ({note}).";
            _notificationService?.ShowSuccess(propagated
                ? $"S-matrix for '{_displayName}' recomputed via {note} and applied to all instances of this type."
                : $"S-matrix for '{_displayName}' recomputed via {note} and applied.");
        }
        catch (OperationCanceledException)
        {
            SolverStatus = "FDTD recompute cancelled.";
            NotifyCancelled();
        }
        catch (Exception ex)
        {
            SolverStatus = $"FDTD error: {ex.Message}";
            _errorConsole?.LogError($"FDTD recompute crashed for '{_displayName}'", ex);
        }
        finally
        {
            IsComputing = false;
            _recalcCts?.Dispose();
            _recalcCts = null;
            RefreshEntries(notifyChanged: true);
        }
    }

    /// <summary>
    /// Runs the solver while keeping <see cref="SolverStatus"/> alive: a once-per-second
    /// elapsed-time heartbeat (so the long image build / FDTD run never looks frozen)
    /// plus the latest progress line streamed from Meep.
    /// </summary>
    private async Task<FdtdSMatrixResult> RunWithLiveStatusAsync(FdtdSMatrixRequest request, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        string? lastLine = null;
        const string baseMsg = "Running FDTD (Meep in Docker). First run builds the solver image (several minutes)";

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
            SolverStatus = lastLine == null
                ? $"{baseMsg} — {stopwatch.Elapsed:m\\:ss} elapsed…"
                : $"FDTD running ({stopwatch.Elapsed:m\\:ss}): {lastLine}";
        SolverStatus = $"{baseMsg}…";
        timer.Start();

        var progress = new Progress<string>(line => lastLine = Shorten(line));
        try
        {
            return await _fdtdService!.SolveAsync(request, progress, ct);
        }
        finally
        {
            timer.Stop();
        }
    }

    /// <summary>
    /// Cancelling (usually by closing the dialog) is intentional, so surface it
    /// as a transient toast on the main window — the dialog's status text may
    /// already be gone and the error console would be far too heavy (#586).
    /// </summary>
    private void NotifyCancelled() =>
        _notificationService?.ShowInfo($"FDTD recompute for '{_displayName}' cancelled.");

    private static string Shorten(string s) => s.Length <= 80 ? s : s[..80] + "…";

    private static string BuildSolverStatus(FdtdSMatrixResult result, ApplyResult? apply, bool propagatedToTemplate)
    {
        var worst = result.EnergySumPerInput.Count > 0 ? result.EnergySumPerInput.Values.Max() : 0.0;
        var energy = result.EnergySumPerInput.Count > 0 ? $" Energy Σ|S|² ≤ {worst:F3} per input." : "";
        var applied = apply == null ? "" : $" Applied {apply.Applied} wavelength(s).";
        var scope = propagatedToTemplate ? " Applied to all instances of this component type." : "";
        return $"FDTD done: {result.Wavelengths.Count} wavelength(s).{energy}{applied}{scope}";
    }
}
