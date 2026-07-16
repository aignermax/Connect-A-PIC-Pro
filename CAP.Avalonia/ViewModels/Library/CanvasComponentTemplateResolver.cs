using System;
using System.Collections.Generic;
using System.Linq;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;

namespace CAP.Avalonia.ViewModels.Library;

/// <summary>
/// Resolves the PDK <see cref="ComponentTemplate"/> behind a placed canvas
/// <see cref="ComponentViewModel"/>, for routing the canvas "Edit Component…" context-menu
/// entry to the unified "Edit Component" editor (design 2026-07-16-pdk-ux-polish, T4) instead
/// of the retired per-instance <c>ComponentSettingsDialog</c> FDTD recompute path.
/// </summary>
public static class CanvasComponentTemplateResolver
{
    /// <summary>
    /// Matches <paramref name="compVm"/> against <paramref name="library"/> by PDK source
    /// (<c>TemplatePdkSource</c>, falling back to <paramref name="resolvePdkSource"/> — the same
    /// fallback used elsewhere, e.g. <c>WarnIfSavedProcessDivergedFromDesign</c>) and by
    /// <c>TemplateName</c>. Returns null when either is unavailable or no template matches both
    /// (e.g. the component's PDK was deleted after it was placed, or the VM never got a
    /// <c>TemplateName</c> in the first place) — the caller is expected to no-op with an error
    /// hint rather than crash.
    /// </summary>
    public static ComponentTemplate? Resolve(
        ComponentViewModel compVm,
        IEnumerable<ComponentTemplate> library,
        Func<Component, string?>? resolvePdkSource)
    {
        var pdkSource = compVm.TemplatePdkSource ?? resolvePdkSource?.Invoke(compVm.Component);
        if (pdkSource is null || compVm.TemplateName is null)
            return null;

        return library.FirstOrDefault(t => t.PdkSource == pdkSource && t.Name == compVm.TemplateName);
    }
}
