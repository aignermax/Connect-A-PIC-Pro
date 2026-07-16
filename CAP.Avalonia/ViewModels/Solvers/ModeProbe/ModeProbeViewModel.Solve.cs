using System.Globalization;
using System.IO;
using CAP_Core.Solvers.ModeProbe;
using CAP_Core.Solvers.ModeSolver;
using CommunityToolkit.Mvvm.Input;

namespace CAP.Avalonia.ViewModels.Solvers.ModeProbe;

/// <summary>
/// Solve pipeline of the mode probe: cross-section resolution, backend call,
/// fundamental-mode extraction, MFD estimation, and fiber-overlap computation.
/// </summary>
public partial class ModeProbeViewModel
{
    private const double NanometersPerMicrometer = 1000.0;

    /// <summary>Solves the fundamental mode for the current target and inputs.</summary>
    [RelayCommand]
    private async Task Solve()
    {
        if (_target == null || IsInterferenceRegion) return;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        var crossSection = ResolveCrossSection();
        IsSolving = true;
        ResetResult();
        StatusText = "Solving…";

        try
        {
            var result = await _service.SolveAsync(BuildRequest(crossSection), _cts.Token);
            if (!result.Success && CanAutoInstall(result))
            {
                var retried = await InstallBackendAndRetry(result, crossSection, _cts.Token);
                if (retried == null) return; // install handled the status itself; do not retry/overwrite
                result = retried;
            }

            if (result.Success)
                ApplyResult(result, crossSection);
            else
                ApplyFailure(result);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
        }
        finally
        {
            IsSolving = false;
        }
    }

    private ProbeCrossSection ResolveCrossSection()
    {
        var crossSection = ProbeCrossSectionResolver.Resolve(
            _target!.WaveguideWidthMicrometers,
            GetActiveProcessFingerprint?.Invoke(),
            _defaults.LastManualCrossSection);

        IsGeometryAssumed = crossSection.IsGeometryAssumed;
        GeometrySourceText = crossSection.SourceDescription;
        CrossSectionText = string.Format(CultureInfo.InvariantCulture,
            "{0:0.###} × {1:0.###} µm · n {2:0.##}/{3:0.##}",
            crossSection.WidthMicrometers, crossSection.HeightMicrometers,
            crossSection.CoreIndex, crossSection.CladIndex);
        return crossSection;
    }

    private ModeSolverRequest BuildRequest(ProbeCrossSection crossSection)
    {
        if (!Enum.TryParse<ModeSolverBackend>(SelectedBackend, out var backend))
            backend = ModeSolverBackend.GdsfactoryModes;

        return new ModeSolverRequest
        {
            Width = crossSection.WidthMicrometers,
            Height = crossSection.HeightMicrometers,
            SlabHeight = crossSection.SlabHeightMicrometers,
            CoreIndex = crossSection.CoreIndex,
            CladIndex = crossSection.CladIndex,
            Wavelengths = new[] { WavelengthNm / NanometersPerMicrometer },
            Backend = backend,
            NumModes = 1, // the probe shows the fundamental mode slice
        };
    }

    private void ApplyResult(ModeSolverResult result, ProbeCrossSection crossSection)
    {
        var fundamental = result.Modes
            .OrderBy(m => m.ModeIndex)
            .FirstOrDefault();
        if (fundamental == null)
        {
            StatusText = "No guided mode found for this cross-section.";
            return;
        }

        NEff = fundamental.NEff;
        NGroup = fundamental.NGroup;
        Polarisation = fundamental.Polarisation;
        ModeFieldImage = DecodeImage(fundamental.ModeFieldPng);

        _modeMfd = ModeFieldEstimator.EstimateMfd(
            crossSection.WidthMicrometers, crossSection.HeightMicrometers,
            fundamental.NEff, crossSection.CladIndex,
            WavelengthNm / NanometersPerMicrometer);
        MfdText = _modeMfd is { } mfd
            ? string.Format(CultureInfo.InvariantCulture, "{0:0.00} × {1:0.00} µm", mfd.MfdX, mfd.MfdY)
            : "n/a (weakly guided)";

        UpdateFiberOverlap();
        HasResult = true;
        StatusText = $"Solved with {result.BackendUsed}.";
    }

    private void ApplyFailure(ModeSolverResult result)
    {
        StatusText = result.Error ?? "Solve failed.";
        if (!string.IsNullOrWhiteSpace(result.MissingBackend))
            StatusText += $"  →  pip install {result.MissingBackend} (Settings → Python environment)";
    }

    private bool CanAutoInstall(ModeSolverResult result) =>
        EnsureBackendAsync != null && !string.IsNullOrWhiteSpace(result.MissingBackend);

    /// <summary>
    /// Auto-installs the missing backend into a managed environment and, on success,
    /// re-runs the solve once against the freshly-activated interpreter. Returns the
    /// retry result to display, or <c>null</c> when it has already set an explanatory
    /// <see cref="ModeProbeViewModel.StatusText"/> (install failed/declined, or the
    /// backend is still unusable after install) and the caller should not overwrite it.
    /// </summary>
    private async Task<ModeSolverResult?> InstallBackendAndRetry(
        ModeSolverResult failure, ProbeCrossSection crossSection, CancellationToken ct)
    {
        var progress = new Progress<string>(m => StatusText = m);
        StatusText = $"Installing {failure.MissingBackend}… (first use can take a few minutes)";
        var installed = await EnsureBackendAsync!(failure.MissingBackend!, progress, ct);
        if (!installed)
        {
            StatusText = $"Auto-install of '{failure.MissingBackend}' did not complete — see "
                + $"Settings → Python environment for details, or run: pip install {failure.MissingBackend}";
            return null;
        }

        StatusText = "Backend installed — retrying…";
        var retry = await _service.SolveAsync(BuildRequest(crossSection), ct);
        if (!retry.Success && retry.MissingBackend == failure.MissingBackend)
        {
            // uv reported success but the backend still can't be imported (partial install,
            // wrong ABI, missing runtime config). Don't loop — say so plainly (#691 review).
            StatusText = $"Installed '{failure.MissingBackend}' but it is still unavailable — "
                + "check Settings → Python environment.";
            return null;
        }
        return retry;
    }

    /// <summary>Recomputes the fiber-overlap figures from the cached mode MFD.</summary>
    private void UpdateFiberOverlap()
    {
        if (!ShowFiberOverlap || _modeMfd is not { } mfd || FiberMfdUm <= 0)
        {
            OverlapPercent = 0;
            OverlapLossDb = 0;
            return;
        }
        var overlap = FiberOverlapCalculator.Compute(mfd.MfdX, mfd.MfdY, FiberMfdUm);
        OverlapPercent = overlap.EfficiencyPercent;
        OverlapLossDb = overlap.LossDb;
    }

    private static global::Avalonia.Media.Imaging.Bitmap? DecodeImage(string? base64Png)
    {
        if (string.IsNullOrWhiteSpace(base64Png)) return null;
        try
        {
            using var stream = new MemoryStream(Convert.FromBase64String(base64Png));
            return new global::Avalonia.Media.Imaging.Bitmap(stream);
        }
        catch (Exception)
        {
            // Malformed image data — bad base64 (FormatException) OR valid base64 that isn't a
            // decodable PNG (Avalonia/Skia throws its own type). Either way: show the numbers
            // without the slice picture instead of faulting the solve (#691 review).
            return null;
        }
    }
}
