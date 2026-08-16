using System.Numerics;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.Components.Core;
using CAP_Core.LightCalculation;
using CAP_Core.Routing;
using CAP_Core.Tiles;

namespace UnitTests.Analysis.LogicAnalysis;

/// <summary>
/// Numeric fixture circuits for the truth-table tests: the same self-contained
/// S-matrix templates as the #929 multi-chiplet journey (a lossless 50/50 coupler
/// with a +90° cross port and a unity-through waveguide), baked at one wavelength —
/// no PDK, no Nazca, fully deterministic.
/// </summary>
public static class LogicGateFixtureFactory
{
    /// <summary>Wavelength all fixture S-matrices are baked for (the standard red laser, 1550 nm).</summary>
    public static int WavelengthNm => StandardWaveLengths.RedNM;

    // Lossless 50/50 coupler: through port at 0°, cross port at +90° — the phase is
    // what makes the MZI recombination below interfere at all.
    private static readonly Complex Through = new(Math.Sqrt(0.5), 0);
    private static readonly Complex Cross = new(0, Math.Sqrt(0.5));

    private const double CouplerWidth = 250;
    private const double CouplerHeight = 80;
    private const double WaveguideLength = 100;
    private const double WaveguideThickness = 5;

    /// <summary>
    /// A single 50/50 coupler exposed as a two-input/one-output circuit: a = in1,
    /// b = in2, y = out1 (out2 stays unexposed). Any single input delivers half its
    /// power at y; both inputs recombine coherently into full power at y.
    /// </summary>
    public static ComponentGroup CreateCombinerGroup()
    {
        var coupler = CreateCoupler("combine");
        var group = new ComponentGroup("Combiner (OR candidate)");
        group.AddChild(coupler);
        group.AddExternalPin(Exposed("a", coupler, "in1"));
        group.AddExternalPin(Exposed("b", coupler, "in2"));
        group.AddExternalPin(Exposed("y", coupler, "out1"));
        return group;
    }

    /// <summary>
    /// A balanced Mach-Zehnder: a splitter, two equal unity-through arms and a
    /// recombiner, wired exactly like the composed chiplets in #929. Exposes
    /// in = split.in1, dark = combine.out1 (the extinguished through port) and
    /// bright = combine.out2 (the cross port that collects all the power).
    /// </summary>
    public static ComponentGroup CreateBalancedMziGroup()
    {
        var split = CreateCoupler("split");
        var arm1 = CreateWaveguide("arm1");
        arm1.PhysicalX = CouplerWidth + WaveguideThickness;
        arm1.PhysicalY = 7.5;
        var arm2 = CreateWaveguide("arm2");
        arm2.PhysicalX = CouplerWidth + WaveguideThickness;
        arm2.PhysicalY = 67.5;
        var combine = CreateCoupler("combine");
        combine.PhysicalX = CouplerWidth + 2 * WaveguideThickness + WaveguideLength;

        var group = new ComponentGroup("Balanced MZI");
        group.AddChild(split);
        group.AddChild(arm1);
        group.AddChild(arm2);
        group.AddChild(combine);
        ConnectLossless(group, Pin(split, "out1"), Pin(arm1, "a0"));
        ConnectLossless(group, Pin(split, "out2"), Pin(arm2, "a0"));
        ConnectLossless(group, Pin(arm1, "b0"), Pin(combine, "in1"));
        ConnectLossless(group, Pin(arm2, "b0"), Pin(combine, "in2"));
        group.AddExternalPin(Exposed("in", split, "in1"));
        group.AddExternalPin(Exposed("dark", combine, "out1"));
        group.AddExternalPin(Exposed("bright", combine, "out2"));
        return group;
    }

    /// <summary>
    /// Four independent unity-through waveguides exposed as in0..in3 / out0..out3:
    /// a 4-bit identity bus — the fixture for the maximum-width (16-row) extraction.
    /// </summary>
    public static ComponentGroup CreateFourBitBusGroup()
    {
        var group = new ComponentGroup("4-bit bus");
        for (var i = 0; i < TruthTableExtractor.MaxLogicInputs; i++)
        {
            var waveguide = CreateWaveguide($"lane{i}");
            waveguide.PhysicalX = 0;
            waveguide.PhysicalY = i * 20;
            group.AddChild(waveguide);
            group.AddExternalPin(Exposed($"in{i}", waveguide, "a0"));
            group.AddExternalPin(Exposed($"out{i}", waveguide, "b0"));
        }
        return group;
    }

    /// <summary>
    /// Persistence-capable template form of the combiner coupler: the same geometry
    /// and S-matrix as the raw fixture component, but as a <see cref="ComponentTemplate"/>
    /// so canvas-placed instances survive the .lun save/load round-trip.
    /// </summary>
    public static ComponentTemplate CreateCombinerTemplate() => new()
    {
        Name = "Logic Fixture Combiner",
        Category = "Fixture",
        NazcaFunctionName = "fixture.logic.combiner",
        PdkSource = "Logic Fixture PDK",
        WidthMicrometers = CouplerWidth,
        HeightMicrometers = CouplerHeight,
        PinDefinitions = new[]
        {
            new PinDefinition("in1", 0, 10, 180),
            new PinDefinition("in2", 0, 70, 180),
            new PinDefinition("out1", CouplerWidth, 10, 0),
            new PinDefinition("out2", CouplerWidth, 70, 0),
        },
        CreateWavelengthSMatrixMap = pins =>
        {
            var pinIds = pins.SelectMany(p => new[] { p.IDInFlow, p.IDOutFlow }).ToList();
            var matrix = new SMatrix(pinIds, new List<(Guid, double)>());
            matrix.SetValues(new Dictionary<(Guid, Guid), Complex>
            {
                { (pins[0].IDInFlow, pins[2].IDOutFlow), Through },
                { (pins[0].IDInFlow, pins[3].IDOutFlow), Cross },
                { (pins[1].IDInFlow, pins[2].IDOutFlow), Cross },
                { (pins[1].IDInFlow, pins[3].IDOutFlow), Through },
                { (pins[2].IDInFlow, pins[0].IDOutFlow), Through },
                { (pins[2].IDInFlow, pins[1].IDOutFlow), Cross },
                { (pins[3].IDInFlow, pins[0].IDOutFlow), Cross },
                { (pins[3].IDInFlow, pins[1].IDOutFlow), Through },
            });
            return new Dictionary<int, SMatrix> { { WavelengthNm, matrix } };
        },
    };

    /// <summary>
    /// Persistence-capable template form of the unity-through waveguide — see
    /// <see cref="CreateCombinerTemplate"/> for why the template form exists.
    /// </summary>
    public static ComponentTemplate CreateWaveguideTemplate() => new()
    {
        Name = "Logic Fixture Waveguide",
        Category = "Fixture",
        NazcaFunctionName = "fixture.logic.waveguide",
        PdkSource = "Logic Fixture PDK",
        WidthMicrometers = WaveguideLength,
        HeightMicrometers = WaveguideThickness,
        PinDefinitions = new[]
        {
            new PinDefinition("a0", 0, 2.5, 180),
            new PinDefinition("b0", WaveguideLength, 2.5, 0),
        },
        CreateWavelengthSMatrixMap = pins =>
        {
            var pinIds = pins.SelectMany(p => new[] { p.IDInFlow, p.IDOutFlow }).ToList();
            var matrix = new SMatrix(pinIds, new List<(Guid, double)>());
            matrix.SetValues(new Dictionary<(Guid, Guid), Complex>
            {
                { (pins[0].IDInFlow, pins[1].IDOutFlow), Complex.One },
                { (pins[1].IDInFlow, pins[0].IDOutFlow), Complex.One },
            });
            return new Dictionary<int, SMatrix> { { WavelengthNm, matrix } };
        },
    };

    /// <summary>Lossless 50/50 coupler: pins in1/in2 (left), out1/out2 (right).</summary>
    private static Component CreateCoupler(string identifier) => CreateComponent(
        identifier,
        CouplerWidth,
        CouplerHeight,
        new[]
        {
            ("in1", 0.0, 10.0, 180.0),
            ("in2", 0.0, 70.0, 180.0),
            ("out1", CouplerWidth, 10.0, 0.0),
            ("out2", CouplerWidth, 70.0, 0.0),
        },
        new Dictionary<(string From, string To), Complex>
        {
            { ("in1", "out1"), Through }, { ("in1", "out2"), Cross },
            { ("in2", "out1"), Cross }, { ("in2", "out2"), Through },
            { ("out1", "in1"), Through }, { ("out1", "in2"), Cross },
            { ("out2", "in1"), Cross }, { ("out2", "in2"), Through },
        });

    /// <summary>Unity-through waveguide: pins a0 (left), b0 (right).</summary>
    private static Component CreateWaveguide(string identifier) => CreateComponent(
        identifier,
        WaveguideLength,
        WaveguideThickness,
        new[]
        {
            ("a0", 0.0, 2.5, 180.0),
            ("b0", WaveguideLength, 2.5, 0.0),
        },
        new Dictionary<(string From, string To), Complex>
        {
            { ("a0", "b0"), Complex.One },
            { ("b0", "a0"), Complex.One },
        });

    /// <summary>
    /// Builds a component with baking-solved numeric S-matrix transfers: logical pins
    /// live in one-part-per-pin parts, physical pins bind to them by name.
    /// </summary>
    private static Component CreateComponent(
        string identifier,
        double widthMicrometers,
        double heightMicrometers,
        IReadOnlyList<(string Name, double OffsetX, double OffsetY, double AngleDegrees)> pinDefinitions,
        IReadOnlyDictionary<(string From, string To), Complex> transfers)
    {
        var parts = new Part[pinDefinitions.Count, 1];
        var logicalPins = new Dictionary<string, Pin>();
        for (var i = 0; i < pinDefinitions.Count; i++)
        {
            var pin = new Pin(pinDefinitions[i].Name, i, MatterType.Light, RectSide.Left);
            parts[i, 0] = new Part(new List<Pin> { pin });
            logicalPins[pin.Name] = pin;
        }

        var pinIds = logicalPins.Values.SelectMany(p => new[] { p.IDInFlow, p.IDOutFlow }).ToList();
        var matrix = new SMatrix(pinIds, new List<(Guid, double)>());
        matrix.SetValues(transfers.ToDictionary(
            t => (logicalPins[t.Key.From].IDInFlow, logicalPins[t.Key.To].IDOutFlow),
            t => t.Value));
        var matrices = new Dictionary<int, SMatrix> { { WavelengthNm, matrix } };

        var component = new Component(matrices, new(), $"fixture.{identifier}", "", parts, 0, identifier, DiscreteRotation.R0)
        {
            WidthMicrometers = widthMicrometers,
            HeightMicrometers = heightMicrometers,
        };
        foreach (var (name, offsetX, offsetY, angleDegrees) in pinDefinitions)
        {
            component.PhysicalPins.Add(new PhysicalPin
            {
                Name = name,
                ParentComponent = component,
                LogicalPin = logicalPins[name],
                OffsetXMicrometers = offsetX,
                OffsetYMicrometers = offsetY,
                AngleDegrees = angleDegrees,
            });
        }
        return component;
    }

    /// <summary>Freezes a lossless straight path between two pins inside the group.</summary>
    private static void ConnectLossless(ComponentGroup group, PhysicalPin from, PhysicalPin to)
    {
        var (x1, y1) = from.GetAbsolutePosition();
        var (x2, y2) = to.GetAbsolutePosition();
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(x1, y1, x2, y2, 0));
        group.AddInternalPath(new FrozenWaveguidePath
        {
            Path = path,
            StartPin = from,
            EndPin = to,
            PropagationLossDbPerCm = 0,
        });
    }

    /// <summary>Exposes one internal component pin of the group under an external name.</summary>
    private static GroupPin Exposed(string name, Component owner, string internalPinName) =>
        new() { Name = name, InternalPin = Pin(owner, internalPinName) };

    /// <summary>Looks up a physical pin on a fixture component.</summary>
    private static PhysicalPin Pin(Component component, string name) =>
        component.PhysicalPins.Single(p => p.Name == name);
}
