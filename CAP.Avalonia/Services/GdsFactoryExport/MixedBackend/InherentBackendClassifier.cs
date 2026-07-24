using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Core;

namespace CAP.Avalonia.Services.GdsFactoryExport.MixedBackend;

/// <summary>The layout engine a component's geometry is natively defined in.</summary>
public enum InherentBackend
{
    /// <summary>Rendered by the gdsfactory exporter (gdsfactory-native PDKs, gdsfactory raw code).</summary>
    GdsFactory,

    /// <summary>Rendered by the Nazca exporter (nazca-native PDKs like SiEPIC/demo, nazca raw code).</summary>
    Nazca,
}

/// <summary>
/// Determines a placed component's INHERENT backend for the mixed-backend GDS export:
/// the <c>rawCodeBackend</c> for raw-code components, the PDK's native
/// backend otherwise. Explicitly NOT per-instance overrides — the inherent
/// backend is a property of the template/PDK, not of the placement.
/// Raw-code information lives only on the library <see cref="ComponentTemplate"/> (core
/// components carry no PDK source of their own), so classification resolves the template
/// with the same matching rules as <see cref="ComponentPdkSourceResolver"/>.
/// </summary>
public static class InherentBackendClassifier
{
    private const string GdsFactoryBackendName = "gdsfactory";

    /// <summary>
    /// Classifies <paramref name="component"/> by its inherent backend. A raw-code component
    /// follows its template's <c>rawCodeBackend</c>; otherwise any component carrying a
    /// gdsfactory factory name is gdsfactory-native and everything else (nazca PDKs,
    /// built-ins, stubs) is nazca-native — the same split <see cref="SimpleNazcaExporter"/>
    /// applies when it skips gdsfactory components.
    /// </summary>
    /// <param name="component">The placed core component.</param>
    /// <param name="library">The loaded component library; may be empty when unavailable.</param>
    public static InherentBackend Classify(Component component, IEnumerable<ComponentTemplate> library)
    {
        var template = ResolveTemplate(component, library);
        if (!string.IsNullOrEmpty(template?.RawCode))
            return string.Equals(template!.RawCodeBackend, GdsFactoryBackendName, StringComparison.OrdinalIgnoreCase)
                ? InherentBackend.GdsFactory
                : InherentBackend.Nazca;

        return string.IsNullOrEmpty(component.GdsFactoryFunction)
            ? InherentBackend.Nazca
            : InherentBackend.GdsFactory;
    }

    /// <summary>
    /// Finds the library template a placed component came from, using the same matching
    /// as <see cref="ComponentPdkSourceResolver"/>: the module-qualified gdsfactory
    /// factory name first (unique across PDKs), the Nazca function name otherwise
    /// (including the synthesized <c>nazca_&lt;name&gt;</c> fallback of raw-code components).
    /// </summary>
    private static ComponentTemplate? ResolveTemplate(
        Component component, IEnumerable<ComponentTemplate> library)
    {
        var templates = library as IReadOnlyCollection<ComponentTemplate> ?? library.ToList();
        if (!string.IsNullOrEmpty(component.GdsFactoryFunction))
        {
            var byGf = templates.FirstOrDefault(
                t => t.GdsFactoryFunction == component.GdsFactoryFunction);
            if (byGf != null)
                return byGf;
        }

        var nazcaFunc = component.NazcaFunctionName;
        if (string.IsNullOrEmpty(nazcaFunc))
            return null;

        return templates.FirstOrDefault(t =>
        {
            var templateFunc = t.NazcaFunctionName
                ?? $"nazca_{t.Name.ToLower().Replace(" ", "_")}";
            return templateFunc == nazcaFunc;
        });
    }
}
