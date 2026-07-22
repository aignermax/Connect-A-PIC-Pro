using System.Diagnostics;
using System.Numerics;
using CAP_Core.LightCalculation;
using CAP_Core.LightCalculation.TimeDomainSimulation;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.LightCalculation.TimeDomainSimulation;

/// <summary>
/// Tests for the transitive-closure integration of <see cref="ImpulseResponseBuilder"/>
/// (field round 4 review, findings [3]/[4]/[8]): the memory guard must reflect the REAL
/// pair count the closure produces (not a fixed estimate), the closure must be restricted
/// to the subgraph reachable from the active sources, and every distinct wavelength's
/// S-matrix must be built exactly once.
/// </summary>
public class ImpulseResponseBuilderClosureTests
{
    private const double CenterWavelengthNm = 1550;
    private const double SpanNm = 100;
    private const int NPoints = 64;

    private static SMatrix CreateMatrix(
        IReadOnlyList<Guid> pins, Dictionary<(Guid, Guid), Complex> transfers)
    {
        var matrix = new SMatrix(pins.ToList(), new());
        matrix.SetValues(transfers);
        return matrix;
    }

    private static Mock<ISystemMatrixBuilder> MockBuilderReturning(Func<SMatrix> factory)
    {
        var mockBuilder = new Mock<ISystemMatrixBuilder>();
        mockBuilder.Setup(b => b.GetSystemSMatrix(It.IsAny<int>())).Returns((int _) => factory());
        return mockBuilder;
    }

    [Fact]
    public void Build_ClosurePairCountExceedsMemoryLimit_FailsFastWithReduceNPoints()
    {
        // 36 all-to-all pins → 36² = 1296 closure pairs; at nPoints = 256 that exceeds the
        // 10 MB gate (1280 pairs). The old guard assumed a fixed 200 connections and let
        // this design run into an OOM-sized allocation.
        var pins = Enumerable.Range(0, 36).Select(_ => Guid.NewGuid()).ToList();
        var transfers = new Dictionary<(Guid, Guid), Complex>();
        foreach (var from in pins)
            foreach (var to in pins)
                transfers[(from, to)] = new Complex(0.01, 0);
        var builder = new ImpulseResponseBuilder(
            MockBuilderReturning(() => CreateMatrix(pins, transfers)).Object);

        var ex = Should.Throw<InvalidOperationException>(
            () => builder.Build(CenterWavelengthNm, SpanNm, nPoints: 256));
        ex.Message.ShouldContain("Reduce nPoints");
    }

    [Fact]
    public void Build_ActiveInputs_RestrictToTheReachableSubgraph()
    {
        // Two disconnected chains; only the first chain's input is active. The closure
        // must not spend work on — nor emit pairs from — the unreachable second chain.
        var (aIn, aOut, bIn, bOut) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (cIn, cOut) = (Guid.NewGuid(), Guid.NewGuid());
        var transfers = new Dictionary<(Guid, Guid), Complex>
        {
            { (aIn, aOut), Complex.One },
            { (aOut, bIn), new Complex(0.8, 0) },
            { (bIn, bOut), Complex.One },
            { (cIn, cOut), Complex.One },   // unrelated second circuit
        };
        var pins = new List<Guid> { aIn, aOut, bIn, bOut, cIn, cOut };
        var builder = new ImpulseResponseBuilder(
            MockBuilderReturning(() => CreateMatrix(pins, transfers)).Object);

        var responses = builder.Build(
            CenterWavelengthNm, SpanNm, NPoints, activeInputPinIds: new[] { aIn });

        responses.ShouldNotBeEmpty();
        responses.ShouldAllBe(r => r.InputPinId == aIn);
        responses.Select(r => r.OutputPinId).ShouldContain(bOut);
        responses.Select(r => r.OutputPinId).ShouldNotContain(cOut);
    }

    [Fact]
    public void Build_EachWavelengthMatrix_IsBuiltExactlyOnce()
    {
        var (pIn, pOut) = (Guid.NewGuid(), Guid.NewGuid());
        var calls = new List<int>();
        var mockBuilder = new Mock<ISystemMatrixBuilder>();
        mockBuilder.Setup(b => b.GetSystemSMatrix(It.IsAny<int>()))
            .Returns((int nm) =>
            {
                calls.Add(nm);
                return CreateMatrix(new[] { pIn, pOut },
                    new Dictionary<(Guid, Guid), Complex> { { (pIn, pOut), Complex.One } });
            });
        var builder = new ImpulseResponseBuilder(mockBuilder.Object);

        builder.Build(CenterWavelengthNm, SpanNm, NPoints);

        calls.Count.ShouldBe(calls.Distinct().Count(),
            "every distinct rounded wavelength must be built exactly once (cache seeded with the reference matrix)");
    }

    [Fact]
    public void Build_150PinChain_CompletesWithinSeconds_AndLightReachesTheFarEnd()
    {
        // Perf guard (finding [4]): a realistic pin count with an active-source-restricted
        // closure must stay in the seconds range, and the multi-hop light must still reach
        // the far end of the chain.
        var pins = Enumerable.Range(0, 150).Select(_ => Guid.NewGuid()).ToList();
        var transfers = new Dictionary<(Guid, Guid), Complex>();
        for (int i = 0; i < pins.Count - 1; i++)
            transfers[(pins[i], pins[i + 1])] = Complex.One;
        var builder = new ImpulseResponseBuilder(
            MockBuilderReturning(() => CreateMatrix(pins, transfers)).Object);

        var stopwatch = Stopwatch.StartNew();
        var responses = builder.Build(
            CenterWavelengthNm, SpanNm, NPoints, activeInputPinIds: new[] { pins[0] });
        stopwatch.Stop();

        var firstPin = pins[0];
        var lastPin = pins[pins.Count - 1];
        responses.ShouldContain(r => r.InputPinId == firstPin && r.OutputPinId == lastPin);
        // ~1 s standalone; the generous bound absorbs parallel-suite CPU contention while
        // still failing loudly on the minutes-long O(hops·n³)-per-wavelength regression.
        stopwatch.ElapsedMilliseconds.ShouldBeLessThan(15_000,
            $"150-pin chain took {stopwatch.ElapsedMilliseconds} ms");
    }
}
