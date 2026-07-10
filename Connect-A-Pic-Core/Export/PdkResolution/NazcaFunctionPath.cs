namespace CAP_Core.Export.PdkResolution;

/// <summary>
/// Splits a PDK <c>nazcaFunction</c> string into a Python module name and a
/// bare function name, using the same mapping the PDK Offset Editor applies
/// before rendering (issue #515 keeps the batch check consistent with the
/// click-time preview path):
/// <list type="bullet">
///   <item><c>"demo.mmi2x2_dp"</c> / <c>"demo_pdk.ring_resonator"</c> — dotted
///     demofab notation; split at the last dot, canonicalise "demo_pdk" to "demo".</item>
///   <item><c>"ebeam_y_1550"</c> and other flat SiEPIC prefixes — owned by
///     the <c>siepic_ebeam_pdk</c> Python package.</item>
///   <item>Anything else — assume the bundled Nazca demofab PDK.</item>
/// </list>
/// </summary>
public static class NazcaFunctionPath
{
    /// <summary>Flat name prefixes owned by the SiEPIC EBeam PDK.</summary>
    private static readonly string[] SiepicPrefixes =
        { "ebeam_", "gc_", "ANT_", "crossing_", "taper_", "contra_" };

    /// <summary>
    /// Resolves a raw <c>nazcaFunction</c> string to a (module, function) pair.
    /// </summary>
    public static (string Module, string Function) Split(string? nazcaFunction)
    {
        if (string.IsNullOrWhiteSpace(nazcaFunction))
            return ("demo", "");

        var lastDot = nazcaFunction.LastIndexOf('.');
        if (lastDot > 0)
        {
            var prefix = nazcaFunction[..lastDot];
            var fn = nazcaFunction[(lastDot + 1)..];
            // 'demo_pdk.foo' appears in some Lunima PDK JSONs but the Python
            // package is nazca.demofab — canonicalise so the helper script
            // doesn't try to importlib a non-existent 'demo_pdk'.
            if (prefix == "demo_pdk") prefix = "demo";
            else if (prefix.StartsWith("demo_pdk.", StringComparison.Ordinal))
                prefix = "demo" + prefix["demo_pdk".Length..];
            return (prefix, fn);
        }

        // Case-insensitive: SiEPIC ships both ebeam_y_1550 and GC_TE_1550_… names.
        if (SiepicPrefixes.Any(p => nazcaFunction.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return ("siepic_ebeam_pdk", nazcaFunction);

        return ("demo", nazcaFunction);
    }
}
