using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Library;
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
    private const double PassivityTolerance = TransitiveSMatrixCalculator.PassivityTolerance;

    /// <summary>
    /// Converted measurement sets (multi-wavelength data from vendor .sparam files) carry
    /// genuine measurement/fit noise that can overshoot passivity slightly (worst bundled
    /// case: Broadband DC TE 1550, +0.45%). The data is NOT silently normalized — the
    /// runtime pre-check (<see cref="SingleHopPassivityChecker"/>) warns about such a
    /// component (and tolerates exactly this band) the moment it is simulated. SINGLE
    /// SOURCE OF TRUTH with the runtime: a future data regression above the band fails
    /// this sweep AND aborts every run.
    /// </summary>
    private const double MeasuredDataNoiseBand = SingleHopPassivityChecker.MeasuredDataNoiseBand;

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
                var template = PdkTemplateConverter.ConvertToTemplate(
                    component, Path.GetFileNameWithoutExtension(pdkFile), nazcaModuleName: null);
                foreach (var (wavelengthNm, matrix) in EnumerateProductionMatrices(template, component, pins))
                {
                    double sigma = LargestSingularValue(matrix);
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
    /// Instantiates the matrices EXACTLY as production does — through the delegates
    /// <see cref="PdkTemplateConverter.ConvertToTemplate"/> wires onto the template
    /// (review finding [6]: no test-local mirror of the stop selection). Multi-stop
    /// measurement sets come from <c>CreateWavelengthSMatrixMap</c>, single-stop
    /// hand-authored matrices from <c>CreateSMatrix</c> at the draft's base λ.
    /// </summary>
    private static IEnumerable<(int WavelengthNm, SMatrix Matrix)> EnumerateProductionMatrices(
        ComponentTemplate template, PdkComponentDraft component, List<Pin> pins)
    {
        if (template.CreateWavelengthSMatrixMap is { } createMap)
        {
            foreach (var (wavelengthNm, matrix) in createMap(pins))
                yield return (wavelengthNm, matrix);
            yield break;
        }
        if (template.CreateSMatrix is { } createSingle)
            yield return (component.SMatrix!.WavelengthNm, createSingle(pins));
    }
}
