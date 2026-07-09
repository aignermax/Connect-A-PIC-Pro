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
    string? TemplatePdkSource);
