using System;
using System.Collections.Generic;
using System.Linq;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;

namespace CAP.Avalonia.ViewModels.Library;

/// <summary>
/// Resolves the PDK <see cref="ComponentTemplate"/> behind a placed canvas component so context
/// menus can route to the unified "Edit Component" editor. When resolution fails, callers fall
/// back to the per-instance Component Settings dialog instead of reporting an error.
/// </summary>
public static class CanvasComponentTemplateResolver
{
    /// <summary>
    /// Matches by PDK source and template name, case-insensitively. Returns null when either is
    /// unavailable (ComponentGroups never carry a TemplateName) or no template matches both
    /// (e.g. the PDK was deleted after placement).
    /// </summary>
    public static ComponentTemplate? Resolve(
        ComponentViewModel compVm,
        IEnumerable<ComponentTemplate> library,
        Func<Component, string?>? resolvePdkSource)
    {
        var pdkSource = compVm.TemplatePdkSource ?? resolvePdkSource?.Invoke(compVm.Component);
        if (pdkSource is null || compVm.TemplateName is null)
            return null;

        return library.FirstOrDefault(t =>
            string.Equals(t.PdkSource, pdkSource, StringComparison.OrdinalIgnoreCase)
            && string.Equals(t.Name, compVm.TemplateName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Like <see cref="Resolve"/>, but additionally requires the template to be editable.</summary>
    public static ComponentTemplate? ResolveEditable(
        ComponentViewModel compVm,
        IEnumerable<ComponentTemplate> library,
        Func<Component, string?>? resolvePdkSource,
        Func<ComponentTemplate, bool> canEditTemplate)
    {
        var template = Resolve(compVm, library, resolvePdkSource);
        return template is not null && canEditTemplate(template) ? template : null;
    }
}
