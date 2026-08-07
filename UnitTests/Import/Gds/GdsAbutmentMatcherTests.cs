using CAP_DataAccess.Import.Gds;
using Shouldly;

namespace UnitTests.Import.Gds;

/// <summary>
/// Unit tests for <see cref="GdsAbutmentMatcher"/> with hand-built absolute
/// pins: coincidence within the (radial) tolerance, the opposing-angle rule
/// for instance pins (top-cell ports match on position alone), the
/// no-self-connection rule, one-partner-per-pin consumption with
/// first-match-wins ambiguity warnings, pre-consumed exclusions, and the
/// deterministic scan/result order. A seeded scenario field additionally pins
/// the spatial-grid candidate lookup to a brute-force reference scan: pairs,
/// warnings, and their order must be identical.
/// </summary>
public class GdsAbutmentMatcherTests
{
    private const double Tol = 0.5;

    private static GdsAbsolutePin Pin(string name, double x, double y, double angle = 0) =>
        new() { Name = name, XUm = x, YUm = y, AngleDegrees = angle };

    private static IReadOnlyList<GdsPinPair> Match(
        IReadOnlyList<IReadOnlyList<GdsAbsolutePin>> pinsPerInstance,
        IReadOnlyList<GdsAbsolutePin>? topPortPins = null,
        List<string>? warnings = null,
        IReadOnlySet<(int, int)>? preConsumedInstancePins = null,
        IReadOnlySet<int>? preConsumedPortIndexes = null) =>
        GdsAbutmentMatcher.Match(
            Enumerable.Range(0, pinsPerInstance.Count).Select(i => $"inst{i}").ToList(),
            pinsPerInstance,
            topPortPins ?? Array.Empty<GdsAbsolutePin>(),
            Tol,
            warnings ?? new List<string>(),
            preConsumedInstancePins,
            preConsumedPortIndexes);

    [Fact]
    public void Match_CoincidentOpposingPins_BecomeOnePair()
    {
        var pins = new IReadOnlyList<GdsAbsolutePin>[]
        {
            new[] { Pin("out", 10, 2, angle: 0) },
            new[] { Pin("in", 10, 2, angle: 180) },
        };

        var pair = Match(pins).ShouldHaveSingleItem();
        pair.A.InstanceIndex.ShouldBe(0);
        pair.A.PinName.ShouldBe("out");
        pair.B.InstanceIndex.ShouldBe(1);
        pair.B.PinName.ShouldBe("in");
        pair.XUm.ShouldBe(10.0, 1e-12);
        pair.YUm.ShouldBe(2.0, 1e-12);
        pair.IsRouteDerived.ShouldBeFalse();
    }

    [Fact]
    public void Match_OffsetWithinTolerance_PairsAtTheMidpoint()
    {
        var pins = new IReadOnlyList<GdsAbsolutePin>[]
        {
            new[] { Pin("out", 10, 2, angle: 0) },
            new[] { Pin("in", 10 + 0.8 * Tol, 2, angle: 180) },
        };

        var pair = Match(pins).ShouldHaveSingleItem();
        pair.XUm.ShouldBe(10 + 0.4 * Tol, 1e-12);
        pair.YUm.ShouldBe(2.0, 1e-12);
    }

    [Fact]
    public void Match_DistanceExactlyTolerance_Pairs()
    {
        // The coincidence test is inclusive (≤ tolerance).
        var pins = new IReadOnlyList<GdsAbsolutePin>[]
        {
            new[] { Pin("out", 10, 2, angle: 0) },
            new[] { Pin("in", 10 + Tol, 2, angle: 180) },
        };

        Match(pins).ShouldHaveSingleItem();
    }

    [Fact]
    public void Match_OffsetBeyondTolerance_NoPair()
    {
        var pins = new IReadOnlyList<GdsAbsolutePin>[]
        {
            new[] { Pin("out", 10, 2, angle: 0) },
            new[] { Pin("in", 10 + 1.1 * Tol, 2, angle: 180) },
        };

        Match(pins).ShouldBeEmpty();
    }

    [Fact]
    public void Match_DiagonalOffsetOutsideRadialTolerance_NoPair()
    {
        // 0.75·tol in BOTH axes: inside a square tolerance window on each axis
        // but outside the Euclidean disk (≈1.06·tol) — coincidence is radial,
        // and a bbox candidate prefilter must not widen it.
        var pins = new IReadOnlyList<GdsAbsolutePin>[]
        {
            new[] { Pin("out", 10, 2, angle: 0) },
            new[] { Pin("in", 10 + 0.75 * Tol, 2 + 0.75 * Tol, angle: 180) },
        };

        Match(pins).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(175.0, true)]
    [InlineData(175.5, true)]
    [InlineData(184.5, true)]
    [InlineData(174.0, false)]
    [InlineData(186.0, false)]
    [InlineData(90.0, false)]
    public void Match_OppositionWindow_IsOneEightyPlusMinusFive(double partnerAngle, bool pairs)
    {
        var pins = new IReadOnlyList<GdsAbsolutePin>[]
        {
            new[] { Pin("out", 10, 2, angle: 0) },
            new[] { Pin("in", 10, 2, angle: partnerAngle) },
        };

        Match(pins).Count.ShouldBe(pairs ? 1 : 0);
    }

    [Fact]
    public void Match_CoincidentPinsOfSameInstance_NeverPair()
    {
        // Abutment never self-connects — drawn feedback loops are the route
        // matcher's job.
        var pins = new IReadOnlyList<GdsAbsolutePin>[]
        {
            new[] { Pin("a", 5, 5, angle: 0), Pin("b", 5, 5, angle: 180) },
        };

        Match(pins).ShouldBeEmpty();
    }

    [Fact]
    public void Match_TopPortMatchesOnPositionAlone()
    {
        // Same outward angle as the pin — an instance pair would fail the
        // opposition rule, but port labels carry no reliable direction.
        var pins = new IReadOnlyList<GdsAbsolutePin>[]
        {
            new[] { Pin("out", 10, 2, angle: 0) },
        };
        var ports = new[] { Pin("o1", 10, 2, angle: 0) };

        var pair = Match(pins, ports).ShouldHaveSingleItem();
        pair.B.InstanceIndex.ShouldBe(-1);
        pair.B.IsTopLevelPort.ShouldBeTrue();
        pair.B.PinName.ShouldBe("o1");
    }

    [Fact]
    public void Match_InstanceCandidatePrecedesPort_WithAmbiguityWarning()
    {
        // An opposing instance pin AND a port coincide: the scan order puts
        // instance pins first, so the instance partner wins and the extra
        // candidate is surfaced as a warning; the port stays available.
        var pins = new IReadOnlyList<GdsAbsolutePin>[]
        {
            new[] { Pin("out", 10, 2, angle: 0) },
            new[] { Pin("in", 10, 2, angle: 180) },
        };
        var ports = new[] { Pin("o1", 10, 2, angle: 0) };
        var warnings = new List<string>();

        var pair = Match(pins, ports, warnings).ShouldHaveSingleItem();
        pair.B.InstanceIndex.ShouldBe(1);
        var warning = warnings.ShouldHaveSingleItem();
        warning.ShouldContain("2 abutment candidates");
        warning.ShouldContain("(instance 'inst1')");
        warning.ShouldContain("first match wins");
    }

    [Fact]
    public void Match_ThreeCoincidentPins_FirstMatchWinsAndThirdStaysUnpaired()
    {
        var pins = new IReadOnlyList<GdsAbsolutePin>[]
        {
            new[] { Pin("out", 10, 2, angle: 0) },
            new[] { Pin("in", 10, 2, angle: 180) },
            new[] { Pin("in", 10, 2, angle: 180) },
        };
        var warnings = new List<string>();

        var pair = Match(pins, warnings: warnings).ShouldHaveSingleItem();
        pair.B.InstanceIndex.ShouldBe(1, "the lower instance index is scanned first");
        warnings.ShouldHaveSingleItem().ShouldContain("2 abutment candidates");
    }

    [Fact]
    public void Match_ChainOfCoincidences_PairsInScanOrder()
    {
        var pins = new IReadOnlyList<GdsAbsolutePin>[]
        {
            new[] { Pin("out", 10, 0, angle: 0) },
            new[] { Pin("in", 10, 0, angle: 180), Pin("out", 20, 0, angle: 0) },
            new[] { Pin("in", 20, 0, angle: 180) },
        };

        var pairs = Match(pins);

        pairs.Count.ShouldBe(2);
        pairs[0].A.InstanceIndex.ShouldBe(0);
        pairs[0].B.InstanceIndex.ShouldBe(1);
        pairs[1].A.InstanceIndex.ShouldBe(1);
        pairs[1].B.InstanceIndex.ShouldBe(2);
    }

    [Fact]
    public void Match_PreConsumedPartner_IsInvisibleNotAmbiguous()
    {
        // inst1's pin was paired by route derivation: inst0.out must pair the
        // remaining inst2 candidate WITHOUT an ambiguity warning — consumed
        // pins never even count as candidates.
        var pins = new IReadOnlyList<GdsAbsolutePin>[]
        {
            new[] { Pin("out", 10, 2, angle: 0) },
            new[] { Pin("in", 10, 2, angle: 180) },
            new[] { Pin("in", 10, 2, angle: 180) },
        };
        var warnings = new List<string>();

        var pair = Match(pins, warnings: warnings,
            preConsumedInstancePins: new HashSet<(int, int)> { (1, 0) }).ShouldHaveSingleItem();

        pair.B.InstanceIndex.ShouldBe(2);
        warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Match_PreConsumedPort_IsExcluded()
    {
        var pins = new IReadOnlyList<GdsAbsolutePin>[]
        {
            new[] { Pin("out", 10, 2, angle: 0) },
        };
        var ports = new[] { Pin("o1", 10, 2, angle: 180) };

        Match(pins, ports, preConsumedPortIndexes: new HashSet<int> { 0 }).ShouldBeEmpty();
    }

    [Fact]
    public void Match_NoPins_ReturnsEmpty()
    {
        Match(Array.Empty<IReadOnlyList<GdsAbsolutePin>>()).ShouldBeEmpty();
    }

    [Fact]
    public void Match_SeededScenarioField_IdenticalToBruteForceReference()
    {
        // ~200 pins across isolated scenario spots (exact hits, boundary hits,
        // near-misses, opposition failures, ambiguities, port matches,
        // pre-consumed partners) plus dense unstructured clusters. The spatial
        // grid is a candidate prefilter only, so the pairs, the warnings, and
        // their ORDER must be identical to the brute-force reference scan.
        var random = new Random(20260806);
        var pinsPerInstance = new List<IReadOnlyList<GdsAbsolutePin>>();
        var topPorts = new List<GdsAbsolutePin>();
        var preConsumedPins = new HashSet<(int, int)>();
        int guaranteedPairs = 0, guaranteedWarnings = 0;

        int AddInstance(params GdsAbsolutePin[] pins)
        {
            pinsPerInstance.Add(pins);
            return pinsPerInstance.Count - 1;
        }

        // Spot pitch 20·tol: scenarios can never interact with each other.
        const double spotPitch = 20.0 * Tol;
        for (var n = 0; n < 80; n++)
        {
            double sx = n % 9 * spotPitch;
            double sy = n / 9 * spotPitch;
            double baseAngle = random.NextDouble() * 360.0;
            double jitterA = (random.NextDouble() - 0.5) * 4.0; // |jitterA − jitterB| ≤ 4° < 5°
            double jitterB = (random.NextDouble() - 0.5) * 4.0;
            double opposing = baseAngle + 180.0 + jitterB;
            switch (random.Next(10))
            {
                case 0: // exact coincidence, opposing within the window
                    AddInstance(Pin("out", sx, sy, baseAngle + jitterA));
                    AddInstance(Pin("in", sx, sy, opposing));
                    guaranteedPairs++;
                    break;
                case 1: // near-miss just past the tolerance
                    AddInstance(Pin("out", sx, sy, baseAngle));
                    AddInstance(Pin("in", sx + Tol * (1.01 + 0.4 * random.NextDouble()), sy, baseAngle + 180.0));
                    break;
                case 2: // random offset inside the disk, opposing within the window
                {
                    double distance = 0.95 * Tol * random.NextDouble();
                    double direction = random.NextDouble() * 2.0 * Math.PI;
                    AddInstance(Pin("out", sx, sy, baseAngle + jitterA));
                    AddInstance(Pin(
                        "in",
                        sx + distance * Math.Cos(direction),
                        sy + distance * Math.Sin(direction),
                        opposing));
                    guaranteedPairs++;
                    break;
                }
                case 3: // coincident but only 170° apart — opposition fails
                    AddInstance(Pin("out", sx, sy, baseAngle));
                    AddInstance(Pin("in", sx, sy, baseAngle + 170.0));
                    break;
                case 4: // two coincident opposing candidates — ambiguity, first wins
                    AddInstance(Pin("out", sx, sy, baseAngle + jitterA));
                    AddInstance(Pin("in", sx, sy, opposing));
                    AddInstance(Pin("in", sx, sy, opposing));
                    guaranteedPairs++;
                    guaranteedWarnings++;
                    break;
                case 5: // top-cell port within tolerance — no angle rule
                    AddInstance(Pin("out", sx, sy, baseAngle));
                    topPorts.Add(Pin($"o{topPorts.Count}", sx + 0.9 * Tol * random.NextDouble(), sy, baseAngle));
                    guaranteedPairs++;
                    break;
                case 6: // diagonal window-corner near-miss (outside the radial disk)
                    AddInstance(Pin("out", sx, sy, baseAngle));
                    AddInstance(Pin("in", sx + 0.75 * Tol, sy + 0.75 * Tol, baseAngle + 180.0));
                    break;
                case 7: // instance partner AND port coincide — instance wins, warned
                    AddInstance(Pin("out", sx, sy, baseAngle + jitterA));
                    AddInstance(Pin("in", sx, sy, opposing));
                    topPorts.Add(Pin($"o{topPorts.Count}", sx, sy, 0));
                    guaranteedPairs++;
                    guaranteedWarnings++;
                    break;
                case 8: // distance exactly the tolerance — inclusive boundary
                    AddInstance(Pin("out", sx, sy, baseAngle + jitterA));
                    AddInstance(Pin("in", sx + Tol, sy, opposing));
                    guaranteedPairs++;
                    break;
                case 9: // partner pre-consumed by route derivation — invisible
                    AddInstance(Pin("out", sx, sy, baseAngle + jitterA));
                    preConsumedPins.Add((AddInstance(Pin("in", sx, sy, opposing)), 0));
                    break;
            }
        }

        // Dense unstructured clusters (8 single-pin instances within 1.2·tol of
        // a shared center, random near-cardinal angles): no per-cluster
        // guarantees — the brute-force equality is the arbiter of whatever
        // consumption cascades they produce.
        for (var c = 0; c < 3; c++)
        {
            for (var p = 0; p < 8; p++)
            {
                double distance = 1.2 * Tol * random.NextDouble();
                double direction = random.NextDouble() * 2.0 * Math.PI;
                double angle = 90.0 * random.Next(4) + (random.NextDouble() - 0.5) * 14.0;
                AddInstance(Pin(
                    $"c{p}",
                    c * spotPitch + distance * Math.Cos(direction),
                    10 * spotPitch + distance * Math.Sin(direction),
                    angle));
            }
        }

        // A pre-consumed port must be invisible: no pair, no ambiguity.
        AddInstance(Pin("out", 9 * spotPitch, 10 * spotPitch, 0));
        topPorts.Add(Pin("oPre", 9 * spotPitch, 10 * spotPitch, 0));
        var preConsumedPorts = new HashSet<int> { topPorts.Count - 1 };

        var names = Enumerable.Range(0, pinsPerInstance.Count).Select(i => $"inst{i}").ToList();
        var expectedWarnings = new List<string>();
        var expected = BruteForceReference(
            names, pinsPerInstance, topPorts, Tol, expectedWarnings, preConsumedPins, preConsumedPorts);
        var actualWarnings = new List<string>();
        var actual = GdsAbutmentMatcher.Match(
            names, pinsPerInstance, topPorts, Tol, actualWarnings, preConsumedPins, preConsumedPorts);

        expected.Count.ShouldBeGreaterThanOrEqualTo(guaranteedPairs,
            "every guaranteed scenario must produce its pair");
        expectedWarnings.Count.ShouldBeGreaterThanOrEqualTo(guaranteedWarnings,
            "every ambiguity scenario must warn");
        actual.ShouldBe(expected, "same pairs in the same order as the brute-force scan");
        actualWarnings.ShouldBe(expectedWarnings);
    }

    private readonly record struct ReferenceCandidate(int InstanceIndex, int PinIndex, string PinName, bool IsPort);

    /// <summary>
    /// The pre-grid quadratic scan, kept verbatim as the semantic reference:
    /// the production matcher's spatial pruning must reproduce its pairs and
    /// warnings, in order.
    /// </summary>
    private static List<GdsPinPair> BruteForceReference(
        IReadOnlyList<string> instanceNames,
        IReadOnlyList<IReadOnlyList<GdsAbsolutePin>> pinsPerInstance,
        IReadOnlyList<GdsAbsolutePin> topPortPins,
        double toleranceUm,
        List<string> warnings,
        IReadOnlySet<(int, int)> preConsumedInstancePins,
        IReadOnlySet<int> preConsumedPortIndexes)
    {
        var pairs = new List<GdsPinPair>();
        var consumedInstancePins = pinsPerInstance.Select(pins => new bool[pins.Count]).ToArray();
        var consumedPorts = new bool[topPortPins.Count];
        foreach (var (instance, pin) in preConsumedInstancePins)
            consumedInstancePins[instance][pin] = true;
        foreach (var port in preConsumedPortIndexes)
            consumedPorts[port] = true;

        for (int i = 0; i < pinsPerInstance.Count; i++)
        {
            for (int k = 0; k < pinsPerInstance[i].Count; k++)
            {
                if (consumedInstancePins[i][k])
                    continue;

                var pin = pinsPerInstance[i][k];
                var candidates = new List<ReferenceCandidate>();
                for (int j = 0; j < pinsPerInstance.Count; j++)
                {
                    if (j == i)
                        continue;
                    for (int l = 0; l < pinsPerInstance[j].Count; l++)
                    {
                        if (consumedInstancePins[j][l])
                            continue;
                        var other = pinsPerInstance[j][l];
                        if (WithinTolerance(pin, other) && AnglesOppose(pin, other))
                            candidates.Add(new ReferenceCandidate(j, l, other.Name, IsPort: false));
                    }
                }
                for (int p = 0; p < topPortPins.Count; p++)
                {
                    if (!consumedPorts[p] && WithinTolerance(pin, topPortPins[p]))
                        candidates.Add(new ReferenceCandidate(-1, p, topPortPins[p].Name, IsPort: true));
                }
                if (candidates.Count == 0)
                    continue;

                var chosen = candidates[0];
                if (candidates.Count > 1)
                {
                    var partner = chosen.IsPort ? "top-cell port" : $"instance '{instanceNames[chosen.InstanceIndex]}'";
                    warnings.Add(
                        $"Pin '{pin.Name}' of instance '{instanceNames[i]}' has {candidates.Count} " +
                        $"abutment candidates within {toleranceUm} µm; connected to " +
                        $"'{chosen.PinName}' ({partner}) — first match wins.");
                }

                consumedInstancePins[i][k] = true;
                var partnerPin = chosen.IsPort
                    ? topPortPins[chosen.PinIndex]
                    : pinsPerInstance[chosen.InstanceIndex][chosen.PinIndex];
                if (chosen.IsPort)
                    consumedPorts[chosen.PinIndex] = true;
                else
                    consumedInstancePins[chosen.InstanceIndex][chosen.PinIndex] = true;

                pairs.Add(new GdsPinPair
                {
                    A = new GdsPinEndpoint { InstanceIndex = i, PinName = pin.Name },
                    B = new GdsPinEndpoint { InstanceIndex = chosen.InstanceIndex, PinName = chosen.PinName },
                    XUm = (pin.XUm + partnerPin.XUm) / 2.0,
                    YUm = (pin.YUm + partnerPin.YUm) / 2.0,
                });
            }
        }
        return pairs;

        bool WithinTolerance(GdsAbsolutePin a, GdsAbsolutePin b)
        {
            double dx = a.XUm - b.XUm;
            double dy = a.YUm - b.YUm;
            return dx * dx + dy * dy <= toleranceUm * toleranceUm;
        }

        static bool AnglesOppose(GdsAbsolutePin a, GdsAbsolutePin b) =>
            Math.Abs(GdsInstancePinProjector.Normalize180(a.AngleDegrees - b.AngleDegrees)) >= 180.0 - 5.0;
    }
}
