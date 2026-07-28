using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Core;

namespace CAP.Avalonia.ViewModels.Canvas.CrossingInsertion;

/// <summary>
/// A freshly instantiated PDK crossing component together with the template
/// metadata the canvas needs to create its <see cref="ComponentViewModel"/>
/// (so persistence and export treat it like a normally placed PDK component).
/// </summary>
/// <param name="Component">The crossing component instance (e.g. ebeam_crossing4).</param>
/// <param name="TemplateName">Display name of the source PDK template (e.g. "Crossing 4-Port").</param>
/// <param name="TemplatePdkSource">Name of the PDK the template comes from (e.g. "SiEPIC EBeam").</param>
public record CrossingComponentInstance(
    Component Component,
    string? TemplateName,
    string? TemplatePdkSource)
{
    /// <summary>Nazca function name of the PDK crossing component used for insertion.</summary>
    public const string CrossingNazcaFunctionName = "ebeam_crossing4";

    /// <summary>
    /// Finds the loaded PDK template of the crossing component, or null while
    /// no crossing template is available (e.g. PDK disabled).
    /// </summary>
    public static ComponentTemplate? FindCrossingTemplate(IEnumerable<ComponentTemplate> templates)
    {
        return templates.FirstOrDefault(t => string.Equals(
            t.NazcaFunctionName, CrossingNazcaFunctionName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Instantiates a fresh crossing component through the production PDK path
    /// (PDK JSON → <see cref="ComponentTemplate"/> → <see cref="ComponentTemplates.CreateFromTemplate"/>).
    /// Returns null while no crossing template is loaded.
    /// </summary>
    public static CrossingComponentInstance? CreateFromTemplates(IEnumerable<ComponentTemplate> templates)
    {
        var template = FindCrossingTemplate(templates);
        if (template == null) return null;

        var component = ComponentTemplates.CreateFromTemplate(template, 0, 0);
        return new CrossingComponentInstance(component, template.Name, template.PdkSource);
    }
}
