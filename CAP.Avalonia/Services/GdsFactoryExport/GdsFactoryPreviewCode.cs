namespace CAP.Avalonia.Services.GdsFactoryExport;

/// <summary>
/// Builds the raw gdsfactory Python that <c>render_gdsfactory_preview.py</c> consumes for a
/// gdsfactory-native PDK component. The script looks for a module-scope <c>component</c>
/// variable, so we import + activate the component's PDK module and resolve the cell from the
/// active PDK registry — the same path the exporter uses (<see cref="GdsFactoryExporter"/>).
/// A gdsfactory-native component carries a module-qualified <c>GdsFactoryFunction</c>
/// (e.g. "cspdk.sin300.mmi1x2") instead of a Nazca function, so the Nazca preview path
/// produces nothing; this gives those components a real geometry preview (#570).
/// </summary>
public static class GdsFactoryPreviewCode
{
    /// <summary>
    /// Returns the preview code for a module-qualified gdsfactory function, or null when the
    /// name is empty or bare (dotless) — a bare name has no importable PDK module to activate,
    /// so no preview can be rendered.
    /// </summary>
    public static string? For(string? gdsFactoryFunction)
    {
        if (string.IsNullOrEmpty(gdsFactoryFunction) || !gdsFactoryFunction!.Contains('.'))
            return null;

        var lastDot = gdsFactoryFunction.LastIndexOf('.');
        var module = gdsFactoryFunction.Substring(0, lastDot);
        var cell = gdsFactoryFunction.Substring(lastDot + 1);

        return $"import gdsfactory as gf\n"
             + $"import {module}\n"
             + $"{module}.PDK.activate()\n"
             + $"component = gf.get_component('{cell}')\n";
    }
}
