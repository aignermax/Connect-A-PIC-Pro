using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.LightCalculation;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using MathNet.Numerics.LinearAlgebra;
using Shouldly;
using System.Numerics;
using Xunit;

namespace UnitTests.LightCalculation;

/// <summary>
/// Guards the bundled-PDK parametric annotations (issue #831). Pins WHICH
/// components are parametric per PDK (so annotations cannot regress silently)
/// and sweeps every parameter of every parametric component across its full
/// slider range, asserting the S-matrix stays passive at every position —
/// including ratio extremes where fixed parasitic terms (reflection,
/// crosstalk) add to a near-lossless split arm.
/// </summary>
public class ParametricPdkCoverageTests
{
    private const double PassivityTolerance = TransitiveSMatrixCalculator.PassivityTolerance;

    /// <summary>Expected parametric components per bundled PDK file.</summary>
    private static readonly Dictionary<string, string[]> ExpectedParametric = new()
    {
        ["demo-pdk.json"] = new[]
        {
            "1x2 MMI Splitter", "2x2 MMI Coupler", "Directional Coupler",
            "Phase Shifter", "Y-Junction", "Straight Waveguide 100µm", "90° Bend",
        },
        ["cornerstone-sin-pdk.json"] = new[]
        {
            "Bend Euler", "Bend S", "Straight", "Taper",
        },
        ["siepic-ebeam-pdk.json"] = new[]
        {
            "MMI 1x2 TE 1550 3dB", "Y-Branch 895", "Y-Branch TE 1310",
            "Y-Branch Adiabatic", "Y-Branch Adiabatic 500nm",
            "SWG Splitter TE 1310", "SWG Splitter TE 1550",
            "MMI 2x2 50/50 TE 1310", "DC TE 895", "DC 2-1 TE 895",
        },
        ["tools-pdk.json"] = Array.Empty<string>(),
    };

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

    private static double LargestSingularValue(SMatrix matrix) =>
        Matrix<Complex>.Build.DenseOfMatrix(matrix.SMat).L2Norm();

    [Fact]
    public void BundledPdks_ParametricCoverage_MatchesExpectedComponentSet()
    {
        foreach (var pdkFile in Directory.EnumerateFiles(PdkDirectory(), "*.json"))
        {
            var pdk = new PdkLoader().LoadFromFile(pdkFile);
            var actual = pdk.Components
                .Where(c => c.SMatrix != null && ParametricSMatrixMapper.IsParametric(c.SMatrix))
                .Select(c => c.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            var expected = ExpectedParametric[Path.GetFileName(pdkFile)]
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            actual.ShouldBe(expected,
                $"parametric component set changed in {Path.GetFileName(pdkFile)} — " +
                "update ExpectedParametric AND the coverage table in docs/PDK_JSON_FORMAT.md");
        }
    }

    /// <summary>
    /// Passivity guard over the full parameter space: each slider is swept
    /// across five positions (min…max) while the others stay at their
    /// defaults, plus the all-min and all-max corners. A formula edit or a
    /// wrong insertion-loss floor that fabricates energy fails here instead
    /// of crashing a simulation run.
    /// </summary>
    [Fact]
    public async Task BundledPdks_EveryParametricComponent_IsPassiveAcrossParameterSpace()
    {
        var offenders = new List<string>();
        foreach (var pdkFile in Directory.EnumerateFiles(PdkDirectory(), "*.json"))
        {
            var pdk = new PdkLoader().LoadFromFile(pdkFile);
            foreach (var draft in pdk.Components)
            {
                if (draft.SMatrix == null || !ParametricSMatrixMapper.IsParametric(draft.SMatrix))
                    continue;

                var template = PdkTemplateConverter.ConvertToTemplate(
                    draft, Path.GetFileNameWithoutExtension(pdkFile), nazcaModuleName: null);

                foreach (var sliderValues in EnumerateSliderCombinations(template))
                {
                    var component = ComponentTemplates.CreateFromTemplate(template, 0, 0);
                    foreach (var (sliderNumber, value) in sliderValues)
                        component.GetSlider(sliderNumber)!.Value = value;

                    foreach (var (wavelengthNm, sMatrix) in component.WaveLengthToSMatrixMap)
                    {
                        await MaterializeFormulas(sMatrix);
                        double sigma = LargestSingularValue(sMatrix);
                        if (sigma > 1.0 + PassivityTolerance)
                        {
                            offenders.Add(
                                $"{Path.GetFileName(pdkFile)} :: {draft.Name} @ {wavelengthNm} nm, " +
                                $"sliders [{string.Join(", ", sliderValues.Select(s => $"{s.SliderNumber}={s.Value:F2}"))}]: " +
                                $"σ_max = {sigma:F6} (+{(sigma - 1) * 100:F3}%)");
                        }
                    }
                }
            }
        }

        offenders.ShouldBeEmpty(
            "every parametric bundled component must stay passive at every slider position. " +
            $"Offenders:\n{string.Join("\n", offenders)}");
    }

    /// <summary>
    /// Per slider-bound parameter: min/25%/50%/75%/max with the other
    /// parameters at their defaults, plus the all-min and all-max corners.
    /// </summary>
    private static IEnumerable<List<(int SliderNumber, double Value)>> EnumerateSliderCombinations(
        ComponentTemplate template)
    {
        var bound = template.ParameterDefinitions
            .Where(p => p.SliderNumber != null)
            .ToList();
        if (bound.Count == 0)
            yield break;

        List<(int SliderNumber, double Value)> AtDefaults() =>
            bound.Select(p => (p.SliderNumber!.Value, p.DefaultValue)).ToList();

        const int SweepSteps = 4;
        for (int paramIndex = 0; paramIndex < bound.Count; paramIndex++)
        {
            var param = bound[paramIndex];
            for (int step = 0; step <= SweepSteps; step++)
            {
                double value = param.MinValue + (param.MaxValue - param.MinValue) * step / SweepSteps;
                var combo = AtDefaults();
                combo[paramIndex] = (param.SliderNumber!.Value, value);
                yield return combo;
            }
        }

        yield return bound.Select(p => (p.SliderNumber!.Value, p.MinValue)).ToList();
        yield return bound.Select(p => (p.SliderNumber!.Value, p.MaxValue)).ToList();
    }

    /// <summary>
    /// Materializes formula-driven entries exactly as production does before
    /// the Neumann iteration reads the matrix.
    /// </summary>
    private static async Task MaterializeFormulas(SMatrix sMatrix)
    {
        var zeroInput = MathNet.Numerics.LinearAlgebra.Vector<Complex>.Build.Dense(sMatrix.PinReference.Count);
        await sMatrix.CalcFieldAtPinsAfterStepsAsync(zeroInput, 1, new CancellationTokenSource());
    }

    [Theory]
    [InlineData("demo-pdk.json", "Y-Junction", "in", "out1", 0.70711, 0)]
    [InlineData("demo-pdk.json", "2x2 MMI Coupler", "in1", "out2", 0.70711, 90)]
    [InlineData("siepic-ebeam-pdk.json", "MMI 2x2 50/50 TE 1310", "port 1", "port 3", 0.68310, -90)]
    [InlineData("siepic-ebeam-pdk.json", "Y-Branch TE 1310", "port 1", "port 2", 0.68310, -45)]
    [InlineData("cornerstone-sin-pdk.json", "Straight", "o1", "o2", 1.0, 0)]
    public void ParametricDefaults_EvaluateToPhysicallyExpectedAmplitudes(
        string pdkFileName, string componentName, string fromPin, string toPin,
        double expectedMagnitude, double expectedPhaseDeg)
    {
        var pdkFile = Path.Combine(PdkDirectory(), pdkFileName);
        var pdk = new PdkLoader().LoadFromFile(pdkFile);
        var draft = pdk.Components.Single(c => c.Name == componentName);

        var parametric = ParametricSMatrixMapper.MapToParametricSMatrix(draft.SMatrix!);
        var evaluated = parametric.EvaluateConnections()
            .Single(c => c.FromPin == fromPin && c.ToPin == toPin);

        evaluated.Value.Magnitude.ShouldBe(expectedMagnitude, 0.0001,
            $"{componentName} {fromPin}->{toPin} default amplitude");
        double phaseDeg = evaluated.Value.Phase * 180.0 / Math.PI;
        phaseDeg.ShouldBe(expectedPhaseDeg, 0.01,
            $"{componentName} {fromPin}->{toPin} default phase");
    }
}
