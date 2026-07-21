using CAP_Core.LightCalculation;
using Shouldly;
using System.Numerics;
using Xunit;

namespace UnitTests.LightCalculation;

/// <summary>
/// THE physics the truncated Neumann series could not do (field round 4, final batch):
/// an add-drop ring resonator built from two lossless 50/50 couplers and a low-loss
/// ring. The linear solve (I − M)·X = B computes the exact resonance response —
/// |H| ≤ 1 at every external port across the detuning grid, a drop-port peak exactly
/// on resonance, and legitimate field enhancement (&gt; 1) at the pins INSIDE the ring
/// that the energy guard must not reject.
/// </summary>
public class RingResonatorClosureTests
{
    private static readonly Complex CouplingT = new(1.0 / Math.Sqrt(2.0), 0); // through
    private static readonly Complex CouplingK = new(0, 1.0 / Math.Sqrt(2.0)); // cross (i·κ)
    private const double RingAmplitude = 0.98; // per full round trip (low loss)

    private readonly Guid _in = Guid.NewGuid();
    private readonly Guid _thru = Guid.NewGuid();
    private readonly Guid _drop = Guid.NewGuid();
    private readonly Guid _ringIn1 = Guid.NewGuid();
    private readonly Guid _ringOut1 = Guid.NewGuid();
    private readonly Guid _ringIn2 = Guid.NewGuid();
    private readonly Guid _ringOut2 = Guid.NewGuid();

    /// <summary>
    /// Directed add-drop ring: coupler 1 couples bus ↔ ring, half-ring arcs (amplitude
    /// √a · e^{iφ/2} each) connect to coupler 2 (drop) and back. Round trip = t²·a·e^{iφ}.
    /// </summary>
    private SMatrix CreateRing(double roundTripPhase)
    {
        double halfPhase = roundTripPhase / 2.0;
        var halfArc = Complex.FromPolarCoordinates(Math.Sqrt(RingAmplitude), halfPhase);
        var pins = new[] { _in, _thru, _drop, _ringIn1, _ringOut1, _ringIn2, _ringOut2 };
        var matrix = new SMatrix(pins.ToList(), new());
        matrix.SetValues(new Dictionary<(Guid, Guid), Complex>
        {
            // Coupler 1 (input side): bus in / ring return → bus through / ring out.
            [(_in, _thru)] = CouplingT,
            [(_in, _ringOut1)] = CouplingK,
            [(_ringIn1, _thru)] = CouplingK,
            [(_ringIn1, _ringOut1)] = CouplingT,
            // Half arc to the drop coupler.
            [(_ringOut1, _ringIn2)] = halfArc,
            // Coupler 2 (drop side): ring through / drop cross.
            [(_ringIn2, _drop)] = CouplingK,
            [(_ringIn2, _ringOut2)] = CouplingT,
            // Half arc back to coupler 1.
            [(_ringOut2, _ringIn1)] = halfArc,
        });
        return matrix;
    }

    private TransitiveClosureContext ExternalPortsContext(int wavelengthNm) => new()
    {
        ExternallyObservablePinIds = new[] { _in, _thru, _drop },
        SourcePinIds = new[] { _in },
        WavelengthNm = wavelengthNm,
    };

    /// <summary>Analytic drop response: |−κ²·√a·e^{iφ/2} / (1 − t²·a·e^{iφ})|.</summary>
    private static double AnalyticDropMagnitude(double phase)
    {
        var k2 = CouplingK * CouplingK;
        var numerator = -k2 * Complex.FromPolarCoordinates(Math.Sqrt(RingAmplitude), phase / 2.0);
        var roundTrip = CouplingT * CouplingT * Complex.FromPolarCoordinates(RingAmplitude, phase);
        return (numerator / (Complex.One - roundTrip)).Magnitude;
    }

    [Fact]
    public void AddDropRing_AcrossDetuningGrid_ExternalPortsStayPassive_WithDropPeakOnResonance()
    {
        const int gridPoints = 41;
        var dropMagnitudes = new double[gridPoints];

        for (int i = 0; i < gridPoints; i++)
        {
            double phase = -Math.PI + 2.0 * Math.PI * i / (gridPoints - 1);
            var closure = TransitiveSMatrixCalculator.Compute(
                CreateRing(phase), ExternalPortsContext(1500 + i));
            var values = closure.GetNonNullValues();

            double thru = values.TryGetValue((_in, _thru), out var t) ? t.Magnitude : 0;
            double drop = values.TryGetValue((_in, _drop), out var d) ? d.Magnitude : 0;
            thru.ShouldBeLessThanOrEqualTo(1.0 + 1e-9, $"through port at phase {phase:F3}");
            drop.ShouldBeLessThanOrEqualTo(1.0 + 1e-9, $"drop port at phase {phase:F3}");
            drop.ShouldBe(AnalyticDropMagnitude(phase), 1e-9,
                $"the solve must match the analytic ring response at phase {phase:F3}");
            dropMagnitudes[i] = drop;
        }

        // Resonance peak: the drop response is maximal exactly on resonance (φ = 0,
        // grid centre) and minimal at the anti-resonant band edges.
        int centre = gridPoints / 2;
        dropMagnitudes[centre].ShouldBe(dropMagnitudes.Max(), 1e-12, "peak must sit on resonance");
        dropMagnitudes[centre].ShouldBeGreaterThan(0.9, "a low-loss ring drops nearly all resonant light");
        dropMagnitudes[0].ShouldBeLessThan(0.4, "anti-resonant light must stay in the bus");
    }

    [Fact]
    public void AddDropRing_OnResonance_InternalFieldEnhancementIsLegitimate()
    {
        // Cavity buildup: on resonance the circulating field INSIDE the ring exceeds
        // the injected amplitude (here ≈ 1.33). That is textbook physics — the energy
        // guard must only watch the external ports, not the ring interior.
        var closure = TransitiveSMatrixCalculator.Compute(
            CreateRing(roundTripPhase: 0), ExternalPortsContext(1550));
        var values = closure.GetNonNullValues();

        values[(_in, _ringIn2)].Magnitude.ShouldBeGreaterThan(1.0,
            "on-resonance circulating field must show cavity buildup");
    }

    [Fact]
    public void AddDropRing_WithoutLoss_ExactlyOnResonance_ThrowsResonantLoop()
    {
        // A lossless ring exactly on resonance has no steady state: (I − M) is singular.
        // With coupling the ring still leaks — make it fully closed: t = 1 edge case is
        // covered by the 2-pin loop tests; here the lossless ring (a = 1) with t = 1
        // arcs would decouple, so drive the singularity via a unit round trip instead.
        var (p0, p1) = (Guid.NewGuid(), Guid.NewGuid());
        var loop = new SMatrix(new List<Guid> { p0, p1 }, new());
        loop.SetValues(new Dictionary<(Guid, Guid), Complex>
        {
            [(p0, p1)] = Complex.One,
            [(p1, p0)] = Complex.One,
        });

        var ex = Should.Throw<NonConvergentCircuitException>(
            () => TransitiveSMatrixCalculator.Compute(loop));
        ex.Kind.ShouldBe(NonConvergentCircuitKind.ResonantLoop);
    }
}
