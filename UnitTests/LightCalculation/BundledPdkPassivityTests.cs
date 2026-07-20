using CAP.Avalonia.Services;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Tiles;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using MathNet.Numerics.LinearAlgebra;
using Shouldly;
using System.Numerics;
using Xunit;

namespace UnitTests.LightCalculation;

/// <summary>
/// Field round 4, final batch: a plain feed-forward chain (GC → 2× adiabatic coupler →
/// GC) tripped the energy guard with |H| = 1.060. Linear λ-interpolation is a convex
/// combination and cannot raise magnitudes, so the excess had to come from the RAW
/// bundled data — these tests pin the diagnosis (the hand-authored adiabatic-coupler
/// matrices combined an exactly-unitary 50/50 block with additive 2%/1% parasitic
/// reflection/crosstalk, which provably pushes the largest singular value to ≈ 1.0214)
/// and guard the corrected data: every fixed S-matrix shipped in a bundled PDK must be
/// passive at every wavelength stop.
/// </summary>
public class BundledPdkPassivityTests
{
    private const double PassivityTolerance = 1e-6;

    /// <summary>
    /// Converted measurement sets (multi-wavelength data from vendor .sparam files) carry
    /// genuine measurement/fit noise that can overshoot passivity slightly (worst bundled
    /// case: Broadband DC TE 1550, +0.45%). The data is NOT silently normalized — the
    /// runtime pre-check (<see cref="SingleHopPassivityChecker"/>) names such a component
    /// the moment it is simulated. This band only pins the KNOWN noise level so a future
    /// data regression (or a new hand-authored error) still fails the sweep.
    /// </summary>
    private const double MeasuredDataNoiseBand = 0.005;

    /// <summary>Walks up from the test binary to the repo checkout containing the PDKs.</summary>
    private static string PdkDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "CAP-DataAccess", "PDKs");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("CAP-DataAccess/PDKs not found above test binaries.");
    }

    private static List<Pin> CreatePins(PdkComponentDraft component) =>
        component.Pins
            .Select((pin, index) => new Pin(pin.Name, index, MatterType.Light, RectSide.Left))
            .ToList();

    private static double LargestSingularValue(SMatrix matrix) =>
        Matrix<Complex>.Build.DenseOfMatrix(matrix.SMat).L2Norm();

    [Fact]
    public void BundledPdks_EveryFixedComponentSMatrix_IsPassive()
    {
        var offenders = new List<string>();
        foreach (var pdkFile in Directory.EnumerateFiles(PdkDirectory(), "*.json"))
        {
            var pdk = new PdkLoader().LoadFromFile(pdkFile);
            foreach (var component in pdk.Components)
            {
                if (component.SMatrix == null || ParametricSMatrixMapper.IsParametric(component.SMatrix))
                    continue;

                // Hand-authored single-stop matrices must be exactly passive; converted
                // measurement sets (wavelengthData) get the documented noise band.
                double tolerance = component.SMatrix.WavelengthData is { Count: > 0 }
                    ? MeasuredDataNoiseBand
                    : PassivityTolerance;

                var pins = CreatePins(component);
                foreach (var (wavelengthNm, draft) in EnumerateWavelengthStops(component.SMatrix))
                {
                    double sigma = LargestSingularValue(
                        PdkTemplateConverter.CreateSMatrixFromPdk(pins, draft));
                    if (sigma > 1.0 + tolerance)
                    {
                        offenders.Add(
                            $"{Path.GetFileName(pdkFile)} :: {component.Name} @ {wavelengthNm} nm: σ_max = {sigma:F4} (+{(sigma - 1) * 100:F2}%)");
                    }
                }
            }
        }

        offenders.ShouldBeEmpty(
            "every bundled fixed S-matrix must be passive (σ_max ≤ 1); non-passive data fabricates " +
            $"energy in every simulation. Offenders:\n{string.Join("\n", offenders)}");
    }

    /// <summary>
    /// Mirrors <see cref="PdkTemplateConverter.ConvertToTemplate"/>: with wavelengthData
    /// present only those stops are used; otherwise the base connections at the base λ.
    /// </summary>
    private static IEnumerable<(int WavelengthNm, PdkSMatrixDraft Draft)> EnumerateWavelengthStops(
        PdkSMatrixDraft sMatrix)
    {
        if (sMatrix.WavelengthData is { Count: > 0 } wavelengthData)
        {
            foreach (var entry in wavelengthData)
            {
                yield return (entry.WavelengthNm, new PdkSMatrixDraft
                {
                    WavelengthNm = entry.WavelengthNm,
                    Connections = entry.Connections,
                });
            }
            yield break;
        }
        yield return (sMatrix.WavelengthNm, sMatrix);
    }
}
