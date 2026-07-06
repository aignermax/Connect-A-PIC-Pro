using System.Collections.Generic;
using System.Linq;
using CAP_Core.Components.Core;

namespace CAP.Avalonia.ViewModels.Library;

/// <summary>
/// Resolves the PDK source of a placed core <see cref="Component"/> by matching its
/// <c>NazcaFunctionName</c> against the loaded component library. Core components carry no
/// PDK source of their own — only <see cref="ComponentTemplate"/>s do — so this lookup is the
/// single shared way to recover it (used for saving designs and for single-process
/// enforcement over group children, issues #570 / #653).
/// </summary>
public static class ComponentPdkSourceResolver
{
    /// <summary>
    /// Returns the <see cref="ComponentTemplate.PdkSource"/> of the library template whose
    /// Nazca function name matches <paramref name="component"/>, or null when no match exists
    /// (e.g. user groups or components whose PDK is not loaded).
    /// </summary>
    public static string? Resolve(Component component, IEnumerable<ComponentTemplate> library)
    {
        var nazcaFunc = component.NazcaFunctionName;
        if (string.IsNullOrEmpty(nazcaFunc))
            return null;

        var match = library.FirstOrDefault(t =>
        {
            var templateFunc = t.NazcaFunctionName
                ?? $"nazca_{t.Name.ToLower().Replace(" ", "_")}";
            return templateFunc == nazcaFunc;
        });
        return match?.PdkSource;
    }
}
