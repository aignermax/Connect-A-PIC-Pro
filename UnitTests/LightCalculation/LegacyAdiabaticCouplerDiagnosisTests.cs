using CAP.Avalonia.Services;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Tiles;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using MathNet.Numerics.LinearAlgebra;
using Shouldly;
using System.Numerics;
using Xunit;

namespace UnitTests.LightCalculation;

/// <summary>
/// Pins the field-round-4 diagnosis of |H| = 1.060 on the loop-free chain
/// GC → 2× Adiabatic Coupler → GC: the bundled adiabatic-coupler matrix used to be an
/// EXACTLY unitary 50/50 block (|S| = 1/√2 on every coupling path) with additive
/// 0.02∠180° back-reflection and 0.01 crosstalk. No phase choice can make that passive —
/// the largest singular value is provably ≈ 1.0214 (+2.14% energy per pass), and two
/// couplers in series push the chain closure above |H| = 1. Interpolation is innocent
/// (convex combination of stops cannot raise magnitudes; the coupler has a single stop).
/// The bundled data is fixed (coupling 1/√2 → 0.69, ≈ the PDK's own measured Y-branch);
/// these tests keep the legacy values inline so the diagnosis stays reproducible.
/// </summary>
public class LegacyAdiabaticCouplerDiagnosisTests
{
    private const double LegacyCoupling = 0.7071067811865476; // exactly 1/√2
    private const double LegacyReflection = 0.02;             // ∠180°
    private const double LegacyCrosstalk = 0.01;              // ∠0°

    /// <summary>Legacy adiabatic-coupler draft exactly as shipped before the data fix.</summary>
    private static PdkSMatrixDraft LegacyAdiabaticCouplerDraft()
    {
        var connections = new List<SMatrixConnection>();
        void Add(string from, string to, double magnitude, double phaseDegrees) =>
            connections.Add(new SMatrixConnection
            {
                FromPin = from, ToPin = to, Magnitude = magnitude, PhaseDegrees = phaseDegrees,
            });

        Add("port 1", "port 1", LegacyReflection, 180); Add("port 1", "port 2", LegacyCrosstalk, 0);
        Add("port 1", "port 3", LegacyCoupling, -90); Add("port 1", "port 4", LegacyCoupling, 0);
        Add("port 2", "port 1", LegacyCrosstalk, 0); Add("port 2", "port 2", LegacyReflection, 180);
        Add("port 2", "port 3", LegacyCoupling, 0); Add("port 2", "port 4", LegacyCoupling, -90);
        Add("port 3", "port 1", LegacyCoupling, -90); Add("port 3", "port 2", LegacyCoupling, 0);
        Add("port 3", "port 3", LegacyReflection, 180); Add("port 3", "port 4", LegacyCrosstalk, 0);
        Add("port 4", "port 1", LegacyCoupling, 0); Add("port 4", "port 2", LegacyCoupling, -90);
        Add("port 4", "port 3", LegacyCrosstalk, 0); Add("port 4", "port 4", LegacyReflection, 180);

        return new PdkSMatrixDraft { WavelengthNm = 1550, Connections = connections };
    }

    private static List<Pin> CreatePins(params string[] names) =>
        names.Select((name, i) => new Pin(name, i, MatterType.Light, RectSide.Left)).ToList();

    [Fact]
    public void LegacyAdiabaticCoupler_IsNonPassiveByTwoPercent_AndTheCheckNamesIt()
    {
        var pins = CreatePins("port 1", "port 2", "port 3", "port 4");
        var matrix = PdkTemplateConverter.CreateSMatrixFromPdk(pins, LegacyAdiabaticCouplerDraft());

        double sigma = Matrix<Complex>.Build.DenseOfMatrix(matrix.SMat).L2Norm();
        sigma.ShouldBe(1.0214, 0.0005, "ideal unitary 50/50 + additive parasitics is provably non-passive");

        var owners = matrix.PinReference.Keys.ToDictionary(id => id, _ => "Adiabatic Coupler TE 1550");
        var ex = Should.Throw<NonConvergentCircuitException>(() =>
            TransitiveSMatrixCalculator.Compute(matrix, new TransitiveClosureContext
            {
                PinOwnerNames = owners,
                WavelengthNm = 1550,
            }));

        ex.Kind.ShouldBe(NonConvergentCircuitKind.NonPassiveComponent);
        ex.ComponentName.ShouldBe("Adiabatic Coupler TE 1550");
        ex.WavelengthNm.ShouldBe(1550);
        ex.ExcessPercent!.Value.ShouldBe(2.14, 0.05);
    }

    [Fact]
    public void LegacyUserChain_GcCouplerCouplerGc_FabricatesEnergyAboveOne()
    {
        // The user's loop-free field chain, rebuilt with the legacy data: two adiabatic
        // couplers cascaded arm-to-arm (port 3 → port 1, port 4 → port 2 — the physical
        // way to chain them). The unitary 50/50 parts recombine coherently to a perfect
        // |H| = 1 transfer, and the GC back-reflections re-inject light into BOTH
        // coupler ports at once — partially exciting the σ = 1.0214 direction — so the
        // closure exceeds 1 on a loop-free chain (here +0.25%; the excess grows with
        // every reflective interface and arm-phase alignment of the concrete layout, up
        // to the σ-product of the involved blocks — the field build reported +6.0%).
        var merged = BuildUserChain(LegacyAdiabaticCouplerDraft());

        var ex = Should.Throw<NonConvergentCircuitException>(
            () => TransitiveSMatrixCalculator.Compute(merged, new TransitiveClosureContext
            {
                WavelengthNm = 1550,
            }));

        ex.Kind.ShouldBe(NonConvergentCircuitKind.EnergyFabricated);
        ex.ExcessPercent!.Value.ShouldBeGreaterThan(0.1,
            "the legacy data must fabricate measurable energy on the loop-free chain " +
            "(closure max here: +0.25%; the field layout reported +6.0%)");
    }

    [Fact]
    public void FixedUserChain_WithCorrectedBundledData_StaysPassiveAndSimulates()
    {
        // Same chain with the CORRECTED bundled coupler data (coupling 0.69) and the
        // PRODUCTION guard scope (the GC pins are the circuit's external ports, as
        // TransientCircuitFactory declares them): the solve succeeds and every transfer
        // between external ports honours |H| ≤ 1 — the user's chain simulates again.
        // Pins BETWEEN the two reflective GCs may legitimately exceed 1 slightly
        // (weak cavity buildup) — that is physics, not fabricated energy.
        var (merged, externalPins) = BuildUserChainWithPorts(CorrectedAdiabaticCouplerDraft());

        var closure = TransitiveSMatrixCalculator.Compute(merged, new TransitiveClosureContext
        {
            ExternallyObservablePinIds = externalPins,
            WavelengthNm = 1550,
        });

        var externalSet = new HashSet<Guid>(externalPins);
        double maxExternal = closure.GetNonNullValues()
            .Where(kv => externalSet.Contains(kv.Key.PinIdStart) && externalSet.Contains(kv.Key.PinIdEnd))
            .Max(kv => kv.Value.Magnitude);
        maxExternal.ShouldBeLessThanOrEqualTo(1.0 + 1e-6);
    }

    /// <summary>Corrected draft: identical to legacy except coupling 1/√2 → 0.69.</summary>
    private static PdkSMatrixDraft CorrectedAdiabaticCouplerDraft()
    {
        var draft = LegacyAdiabaticCouplerDraft();
        foreach (var connection in draft.Connections.Where(c => c.Magnitude == LegacyCoupling))
            connection.Magnitude = 0.69;
        return draft;
    }

    /// <summary>GC → AC → AC → GC with both coupler arms connected (field topology).</summary>
    private static SMatrix BuildUserChain(PdkSMatrixDraft couplerDraft) =>
        BuildUserChainWithPorts(couplerDraft).Matrix;

    /// <summary>Chain plus the GC pin flow ids (the circuit's external ports).</summary>
    private static (SMatrix Matrix, Guid[] ExternalPinIds) BuildUserChainWithPorts(
        PdkSMatrixDraft couplerDraft)
    {
        var gc1 = CreatePins("gc1 port 2");
        var gc2 = CreatePins("gc2 port 2");
        var ac1 = CreatePins("port 1", "port 2", "port 3", "port 4");
        var ac2 = CreatePins("port 1", "port 2", "port 3", "port 4");

        PdkSMatrixDraft GcDraft(string pinName) => new()
        {
            WavelengthNm = 1550,
            Connections = new List<SMatrixConnection>
            {
                new() { FromPin = pinName, ToPin = pinName, Magnitude = 0.1, PhaseDegrees = 180 },
            },
        };

        var matrices = new List<SMatrix>
        {
            PdkTemplateConverter.CreateSMatrixFromPdk(gc1, GcDraft("gc1 port 2")),
            PdkTemplateConverter.CreateSMatrixFromPdk(ac1, couplerDraft),
            PdkTemplateConverter.CreateSMatrixFromPdk(ac2, couplerDraft),
            PdkTemplateConverter.CreateSMatrixFromPdk(gc2, GcDraft("gc2 port 2")),
            CreateWaveguides(
                (gc1[0], ac1[0]),
                (ac1[2], ac2[0]), (ac1[3], ac2[1]),
                (ac2[2], gc2[0])),
        };
        var externalPins = new[]
        {
            gc1[0].IDInFlow, gc1[0].IDOutFlow,
            gc2[0].IDInFlow, gc2[0].IDOutFlow,
        };
        return (SMatrix.CreateSystemSMatrix(matrices), externalPins);
    }

    /// <summary>Bidirectional unit waveguide transfers between pin pairs.</summary>
    private static SMatrix CreateWaveguides(params (Pin A, Pin B)[] links)
    {
        var pinIds = links
            .SelectMany(l => new[] { l.A.IDInFlow, l.A.IDOutFlow, l.B.IDInFlow, l.B.IDOutFlow })
            .Distinct()
            .ToList();
        var matrix = new SMatrix(pinIds, new());
        var transfers = new Dictionary<(Guid, Guid), Complex>();
        foreach (var (a, b) in links)
        {
            transfers[(a.IDOutFlow, b.IDInFlow)] = Complex.One;
            transfers[(b.IDOutFlow, a.IDInFlow)] = Complex.One;
        }
        matrix.SetValues(transfers);
        return matrix;
    }
}
