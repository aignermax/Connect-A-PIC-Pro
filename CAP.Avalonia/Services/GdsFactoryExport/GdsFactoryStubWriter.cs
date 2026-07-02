using System.Globalization;
using System.Text;
using CAP_Core.Components.Core;
using CAP_Core.Export;

namespace CAP.Avalonia.Services.GdsFactoryExport;

/// <summary>
/// Emits self-contained gdsfactory component factories ("stubs") from Lunima's stored
/// dimensions and pins — the same geometry contract as the Nazca stub export, so both
/// backends produce coinciding layouts: polygon <c>[-ox, oy-H] .. [W-ox, oy]</c> around
/// the cell origin, ports at <c>(OffsetX-ox, oy-OffsetY)</c> with orientation
/// <c>-AngleDegrees</c>.
/// </summary>
public static class GdsFactoryStubWriter
{
    /// <summary>Returns the sanitized Python factory name for a component.</summary>
    public static string StubFunctionName(Component comp)
    {
        var key = string.IsNullOrEmpty(comp.NazcaFunctionName)
            ? comp.Identifier
            : comp.NazcaFunctionName;
        return "stub_" + System.Text.RegularExpressions.Regex.Replace(key, @"[^a-zA-Z0-9_]", "_");
    }

    /// <summary>
    /// Appends the stub factory for <paramref name="comp"/> unless one with the same
    /// name was already generated. Parametric straights get a length-parameterised
    /// factory (real waveguide geometry); everything else a rectangle with ports.
    /// </summary>
    public static void AppendStub(StringBuilder sb, Component comp, HashSet<string> generated)
    {
        var name = StubFunctionName(comp);
        if (!generated.Add(name))
            return;

        var ci = CultureInfo.InvariantCulture;
        if (NazcaCoordinateMapper.IsParametricStraight(comp.NazcaFunctionName, comp.NazcaFunctionParameters))
            AppendParametricStraightStub(sb, name, comp, ci);
        else
            AppendStandardStub(sb, name, comp, ci);
    }

    /// <summary>
    /// Length-parameterised straight-waveguide factory, mirroring the Nazca parametric
    /// stub: the waveguide centre line sits at the mapper's stub anchor relative to the
    /// first pin, ports at x=0 and x=length.
    /// </summary>
    private static void AppendParametricStraightStub(
        StringBuilder sb, string name, Component comp, CultureInfo ci)
    {
        var (_, anchorY) = NazcaCoordinateMapper.GetStubAnchor(comp);
        var firstPin = comp.PhysicalPins.FirstOrDefault();
        var firstPinY = firstPin != null
            ? NazcaCoordinateMapper.GetUnrotatedPinOffset(comp, firstPin).OffsetY
            : 0;
        var strtY = NazcaCoordinateMapper.NormalizeZero(anchorY - firstPinY).ToString("F2", ci);

        sb.AppendLine($"def {name}(length=100, width=WG_WIDTH) -> gf.Component:");
        sb.AppendLine($"    \"\"\"Parametric straight stub for {comp.NazcaFunctionName}.\"\"\"");
        sb.AppendLine("    comp = gf.Component()");
        sb.AppendLine("    seg = comp.add_ref(gf.components.straight(length=length, width=width))");
        sb.AppendLine($"    seg.move((0, {strtY}))");

        foreach (var pin in comp.PhysicalPins)
        {
            var (uox, uoy) = NazcaCoordinateMapper.GetUnrotatedPinOffset(comp, pin);
            var py = NazcaCoordinateMapper.NormalizeZero(anchorY - uoy).ToString("F2", ci);
            var pa = NazcaCoordinateMapper.NormalizeZero(-pin.AngleDegrees).ToString("F0", ci);
            var px = uox == 0 ? "0" : "length";
            sb.AppendLine($"    comp.add_port(name='{pin.Name}', center=({px}, {py}), "
                          + $"orientation={pa}, width=width, layer=(1, 0))");
        }

        sb.AppendLine("    return comp");
        sb.AppendLine();
    }

    /// <summary>Rectangle-with-ports factory for a non-parametric component.</summary>
    private static void AppendStandardStub(StringBuilder sb, string name, Component comp, CultureInfo ci)
    {
        var w = comp.WidthMicrometers;
        var h = comp.HeightMicrometers;
        double ox = comp.NazcaOriginOffsetX;
        double oy = comp.NazcaOriginOffsetY;

        var x0 = NazcaCoordinateMapper.NormalizeZero(-ox).ToString("F2", ci);
        var y0 = NazcaCoordinateMapper.NormalizeZero(oy - h).ToString("F2", ci);
        var x1 = NazcaCoordinateMapper.NormalizeZero(w - ox).ToString("F2", ci);
        var y1 = NazcaCoordinateMapper.NormalizeZero(oy).ToString("F2", ci);

        sb.AppendLine($"def {name}() -> gf.Component:");
        sb.AppendLine($"    \"\"\"Stub for {comp.NazcaFunctionName} ({w}x{h} um).\"\"\"");
        sb.AppendLine("    comp = gf.Component()");
        sb.AppendLine($"    comp.add_polygon([({x0}, {y0}), ({x1}, {y0}), ({x1}, {y1}), ({x0}, {y1})], layer=(1, 0))");

        foreach (var pin in comp.PhysicalPins)
        {
            var px = NazcaCoordinateMapper.NormalizeZero(pin.OffsetXMicrometers - ox).ToString("F2", ci);
            var py = NazcaCoordinateMapper.NormalizeZero(oy - pin.OffsetYMicrometers).ToString("F2", ci);
            var pa = NazcaCoordinateMapper.NormalizeZero(-pin.AngleDegrees).ToString("F0", ci);
            sb.AppendLine($"    comp.add_port(name='{pin.Name}', center=({px}, {py}), "
                          + $"orientation={pa}, width=WG_WIDTH, layer=(1, 0))");
        }

        sb.AppendLine("    return comp");
        sb.AppendLine();
    }
}
