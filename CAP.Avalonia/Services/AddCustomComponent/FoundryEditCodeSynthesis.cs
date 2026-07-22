using CAP.Avalonia.Services.GdsFactoryExport;

namespace CAP.Avalonia.Services.AddCustomComponent;

/// <summary>
/// Single resolver that turns a bundled foundry component's function reference into runnable
/// editor code plus the geometry backend that can execute it ("Edit Component" on a foundry
/// definition without stored raw code). One rule per bundled-PDK family, derived from how the
/// cell is REALLY instantiable in the managed Python environment:
/// <list type="bullet">
/// <item>gdsfactory-native (cspdk, module-qualified <c>GdsFactoryFunction</c>): cells are PDK
/// registry entries, not module attributes → <see cref="GdsFactoryPreviewCode.For"/>
/// (import + PDK.activate() + gf.get_component), the round-4 fix.</item>
/// <item>SiEPIC (<c>nazcaModuleName</c> "siepic_ebeam_pdk"): the siepic_ebeam_pdk package is
/// KLayout-based — it has NO cell attributes ("module 'siepic_ebeam_pdk' has no attribute
/// 'ebeam_adiabatic_te1550'", field round 6) and the canvas renders it via a klayout path the
/// raw-code editor cannot use. The cells ARE registered in the ubcpdk gdsfactory PDK, so the
/// editor code resolves them there (<see cref="UbcPdkCellMap"/>, the exporter's mapping).</item>
/// <item>Nazca PDKs (demo): a real Nazca module call via
/// <see cref="NazcaCodeTemplateBuilder"/>.</item>
/// </list>
/// </summary>
public static class FoundryEditCodeSynthesis
{
    /// <summary>
    /// Synthesizes (code, backend) for a foundry component reference, or null when no runnable
    /// editor code exists (no reference at all, or a KLayout-only SiEPIC fixed cell) — callers
    /// then open an honest empty editor instead of code that can only fail.
    /// </summary>
    /// <param name="gdsFactoryFunction">Module-qualified or bare gdsfactory cell reference.</param>
    /// <param name="nazcaModuleName">The PDK's module name from its JSON (e.g. "siepic_ebeam_pdk").</param>
    /// <param name="nazcaFunctionName">The component's PDK cell/function name.</param>
    /// <param name="nazcaParameters">Optional keyword-argument string for the Nazca call.</param>
    public static (string Code, GeometryBackend Backend)? For(
        string? gdsFactoryFunction, string? nazcaModuleName,
        string? nazcaFunctionName, string? nazcaParameters)
    {
        if (!string.IsNullOrWhiteSpace(gdsFactoryFunction))
        {
            // Bare (dotless) cell names have no PDK module to activate — resolve them
            // against whatever PDK the render script activates by default.
            var code = GdsFactoryPreviewCode.For(gdsFactoryFunction)
                ?? $"import gdsfactory as gf\ncomponent = gf.get_component('{gdsFactoryFunction}')\n";
            return (code, GeometryBackend.GdsFactory);
        }

        if (string.IsNullOrWhiteSpace(nazcaFunctionName))
            return null;

        if (IsSiepicModule(nazcaModuleName))
            return SynthesizeSiepic(nazcaFunctionName!);

        return (NazcaCodeTemplateBuilder.Build(nazcaModuleName, nazcaFunctionName, nazcaParameters),
                GeometryBackend.Nazca);
    }

    /// <summary>
    /// True for the SiEPIC EBeam PDK module — mirrors the render script's
    /// <c>_looks_like_siepic</c> predicate so both sides route the same components.
    /// </summary>
    private static bool IsSiepicModule(string? module) =>
        module != null && module.Trim().StartsWith("siepic", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// SiEPIC cells resolve through the ubcpdk gdsfactory registry (the same mapping the GDS
    /// exporter uses). Cells without a ubcpdk equivalent exist only as KLayout fixed cells —
    /// there is no runnable editor code for them, so null is returned.
    /// </summary>
    private static (string Code, GeometryBackend Backend)? SynthesizeSiepic(string nazcaFunction)
    {
        var cell = UbcPdkCellMap.MapToUbcPdkCell(nazcaFunction);
        if (cell is null)
            return null;

        var code = "import gdsfactory as gf\n"
                 + "import ubcpdk\n"
                 + GdsFactoryPdkContext.UbcPdkActivation + "\n"
                 + $"component = gf.get_component('{cell}')\n";
        return (code, GeometryBackend.GdsFactory);
    }
}
