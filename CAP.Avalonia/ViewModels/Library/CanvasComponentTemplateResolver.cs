using System;
using System.Collections.Generic;
using System.Linq;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;

namespace CAP.Avalonia.ViewModels.Library;

/// <summary>
/// Resolves the PDK <see cref="ComponentTemplate"/> behind a placed canvas
/// <see cref="ComponentViewModel"/>, for routing the component context-menu entries (canvas and
/// hierarchy panel) to the unified "Edit Component" editor (design 2026-07-16-pdk-ux-polish, T4)
/// when possible. When resolution fails, callers fall back to the classic per-instance
/// Component Settings dialog instead of reporting an error.
/// </summary>
public static class CanvasComponentTemplateResolver
{
    /// <summary>
    /// Matches <paramref name="compVm"/> against <paramref name="library"/> by PDK source
    /// (<c>TemplatePdkSource</c>, falling back to <paramref name="resolvePdkSource"/> — the same
    /// fallback used elsewhere, e.g. <c>WarnIfSavedProcessDivergedFromDesign</c>) and by
    /// <c>TemplateName</c>, both case-insensitively like the neighboring PDK/component-name
    /// matchers. Returns null when either is unavailable or no template matches both
    /// (e.g. the component's PDK was deleted after it was placed, or the VM never got a
    /// <c>TemplateName</c> in the first place — ComponentGroups never do).
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

    /// <summary>
    /// Resolves like <see cref="Resolve"/> but additionally requires the template to be editable
    /// (<paramref name="canEditTemplate"/> — i.e. <c>LeftPanelViewModel.CanEditTemplate</c>).
    /// Returns null when the unified "Edit Component" editor cannot handle this component; the
    /// caller then falls back to the per-instance Component Settings dialog (the pre-#742
    /// behavior), which keeps ComponentGroups and template-less legacy instances working
    /// (S-matrix view) instead of surfacing a misleading "template no longer available" error.
    /// </summary>
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
