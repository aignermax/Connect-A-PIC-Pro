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
