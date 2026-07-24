using CAP_Core.Components.Core;

namespace CAP.Avalonia.Services.GdsFactoryExport;

/// <summary>
/// Resolves which PDK-activation statement a component's cell must be instantiated under.
/// A GDS is normally one fabrication process, but the Playground deliberately allows mixing;
/// a mixed-process export (field round 4) activates each component's own PDK immediately
/// before instantiating its cell, so every cell keeps its own process layer set — nothing is
/// silently remapped onto a foreign PDK's layers. The resulting file is inspection-only and
/// NOT manufacturable, which the export states loudly (dialog, Error Console, script header).
/// </summary>
public static class GdsFactoryPdkContext
{
    /// <summary>Activation of gdsfactory's generic PDK — used for self-contained stub
    /// geometry and as the baseline before the first placement of a mixed export.</summary>
    public const string GenericActivation = "gf.gpdk.PDK.activate()";

    /// <summary>Activation of the SiEPIC ubcpdk (requires <c>import ubcpdk</c>).</summary>
    public const string UbcPdkActivation = "ubcpdk.PDK.activate()";

    /// <summary>
    /// The Python activation statement under which <paramref name="comp"/>'s cell resolves:
    /// its own gdsfactory module's PDK, ubcpdk for a mapped SiEPIC cell, or the generic PDK
    /// for stub geometry.
    /// </summary>
    public static string ActivationOf(Component comp, GdsFactoryExportOptions options)
    {
        var module = ModuleOf(comp.GdsFactoryFunction);
        if (module != null)
            return module + ".PDK.activate()";
        if (UsesUbcPdkCell(comp, options))
            return UbcPdkActivation;
        return GenericActivation;
    }

    /// <summary>
    /// True when the component exports as a real ubcpdk (SiEPIC) cell: ubcpdk mode is on and
    /// its Nazca function maps to a ubcpdk cell name.
    /// </summary>
    public static bool UsesUbcPdkCell(Component comp, GdsFactoryExportOptions options) =>
        options.Mode == GdsFactoryComponentMode.UbcPdkCells
        && UbcPdkCellMap.MapToUbcPdkCell(comp.NazcaFunctionName) != null;

    /// <summary>
    /// The Python module part of a module-qualified gdsfactory function ("cspdk.sin300" from
    /// "cspdk.sin300.mmi1x2"), or null when the name is empty/bare. Single definition of the
    /// module-qualification rule, shared by the header import, the factory call, the
    /// stub-vs-factory decision, and the mixed-process activation so they can never
    /// disagree (#570 review).
    /// </summary>
    public static string? ModuleOf(string? gdsFactoryFunction) =>
        !string.IsNullOrEmpty(gdsFactoryFunction) && gdsFactoryFunction!.Contains('.')
            ? gdsFactoryFunction.Substring(0, gdsFactoryFunction.LastIndexOf('.'))
            : null;
}
