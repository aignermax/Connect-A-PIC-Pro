using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Core;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// Shared fixture behind <see cref="GdsMziElectricalRoundTripTests"/>: rebuilds the
/// user's MZI design WITH ELECTRICAL CONNECTIONS (the "das hat bei mir nicht
/// geklappt" report) on a fresh canvas — ten components from the two bundled PDKs
/// (Demo PDK: "1x2 MMI Splitter", "Phase Shifter", "Straight Waveguide 100µm",
/// "2x2 MMI Coupler", 2× "Photodetector"; SiEPIC EBeam PDK: 4× "Bond Pad") at his
/// exact coordinates/rotations, wired with his six waveguide connections plus his
/// four electrical (metal) connections, all routed for real.
/// <para>
/// Design-build mapping: his netlist pin names match the bundled templates
/// verbatim (<c>in/out1/out2</c> on the splitter, <c>in/out/elec1/elec2</c> on the
/// phase shifter, <c>a0/b0</c> on the straight, <c>in1/in2/out1/out2</c> on the
/// combiner, <c>in/anode/cathode</c> on the detectors, <c>elec</c> on the bond
/// pads), so no substitution was needed. The phase shifter's
/// (<c>length=500</c>) and the straight's (<c>length=100</c>) settings are exactly
/// the PDK defaults, so the plain template instances already carry them. His three
/// external ports (<c>splitter.in</c>, <c>phase_shifter.elec1/elec2</c>) are NOT
/// modeled: they are a simulation concept, and the Nazca export only writes
/// top-cell port labels for grating/edge couplers — this design has none, so
/// external ports leave no trace in the GDS either way (same conclusion the
/// user-design fixture reaches).
/// </para>
/// </summary>
internal static class GdsMziElectricalFixture
{
    /// <summary>
    /// Rebuilds the user's MZI design on a fresh canvas: the ten components at his
    /// exact coordinates (three bond pads rotated 180°), instantiated from the REAL
    /// bundled PDK templates, then his six optical and four electrical connections,
    /// routed for real (the A* grid is initialized around the design's extent like
    /// the app does).
    /// </summary>
    public static DesignCanvasViewModel BuildMziCanvas()
    {
        var templates = TestPdkLoader.LoadAllTemplates();
        var canvas = new DesignCanvasViewModel();

        Component Place(string templateName, string pdk, string identifier, double x, double y, double rotation = 0)
        {
            var template = templates.First(t => t.Name == templateName && t.PdkSource == pdk);
            var component = ComponentTemplates.CreateFromTemplate(template, x, y);
            component.Identifier = identifier;
            // Rotate like the app's RotateComponentCommand (90° CCW steps about the
            // box centre, pin offsets included) — a bare RotationDegrees assignment
            // would leave the pin offsets unrotated.
            for (var quarterTurns = (int)Math.Round(rotation / 90.0); quarterTurns > 0; quarterTurns--)
                CAP.Avalonia.Commands.RotateComponentCommand.ApplyModelRotation90(component);
            canvas.AddComponent(component, templateName, pdk);
            return component;
        }

        const string demo = "Demo PDK";
        const string siepic = "SiEPIC EBeam PDK";
        // His coordinates, verbatim from the exported netlist.
        var splitter = Place("1x2 MMI Splitter", demo, "mzi_splitter", 689.958, -512.468);
        var phaseShifter = Place("Phase Shifter", demo, "mzi_phase_shifter", 840.962, -510.468);
        var referenceArm = Place("Straight Waveguide 100µm", demo, "mzi_reference_arm", 1003.253, -556.429);
        var combiner = Place("2x2 MMI Coupler", demo, "mzi_combiner", 1403.605, -514.468);
        var detectorBar = Place("Photodetector", demo, "mzi_detector_bar", 1664.788, -437.302);
        var detectorCross = Place("Photodetector", demo, "mzi_detector_cross", 1678.265, -529.500);
        var pad34 = Place("Bond Pad", siepic, "Bond_Pad_34", 1759.788, -335.813, 180);
        var pad35 = Place("Bond Pad", siepic, "Bond_Pad_35", 1673.265, -638.244);
        var pad36 = Place("Bond Pad", siepic, "Bond_Pad_36", 1922.255, -411.724, 180);
        var pad37 = Place("Bond Pad", siepic, "Bond_Pad_37", 1928.522, -557.244, 180);

        PhysicalPin Pin(Component c, string name) => c.PhysicalPins.First(p => p.Name == name);

        // His six optical connections, verbatim.
        canvas.ConnectPins(Pin(splitter, "out1"), Pin(phaseShifter, "in"));
        canvas.ConnectPins(Pin(splitter, "out2"), Pin(referenceArm, "a0"));
        canvas.ConnectPins(Pin(phaseShifter, "out"), Pin(combiner, "in1"));
        canvas.ConnectPins(Pin(referenceArm, "b0"), Pin(combiner, "in2"));
        canvas.ConnectPins(Pin(combiner, "out1"), Pin(detectorBar, "in"));
        canvas.ConnectPins(Pin(combiner, "out2"), Pin(detectorCross, "in"));

        // His four ELECTRICAL connections — metal traces between electrical pins
        // (the canvas routes them like any connection; the metal rendering/export
        // is driven by the pin kinds, issue #682).
        canvas.ConnectPins(Pin(detectorBar, "anode"), Pin(pad34, "elec"));
        canvas.ConnectPins(Pin(detectorBar, "cathode"), Pin(pad36, "elec"));
        canvas.ConnectPins(Pin(pad35, "elec"), Pin(detectorCross, "cathode"));
        canvas.ConnectPins(Pin(pad37, "elec"), Pin(detectorCross, "anode"));

        // The app always routes on an initialized grid; the default bounds
        // (-100..5100) would not cover this design's negative-Y extent.
        canvas.InitializeAStarRouting(600, -900, 2200, -100);
        canvas.RecalculateRoutesAsync().GetAwaiter().GetResult();
        return canvas;
    }
}
