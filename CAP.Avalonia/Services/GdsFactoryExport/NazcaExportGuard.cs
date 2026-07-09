using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;

namespace CAP.Avalonia.Services.GdsFactoryExport;

/// <summary>
/// Detects gdsfactory-native components in a design before a Nazca export. Such components
/// (a module-qualified <see cref="Component.GdsFactoryFunction"/>, e.g. "cspdk.sin300.mmi1x2")
/// cannot be expressed in a Nazca script and would be silently omitted — so the Nazca export
/// asks the user to confirm (or switch to the gdsfactory export) rather than dropping them
/// quietly (#570 field test).
/// </summary>
public static class NazcaExportGuard
{
    /// <summary>
    /// Returns every gdsfactory-native component in the design (walking group children), i.e.
    /// components carrying a module-qualified <see cref="Component.GdsFactoryFunction"/>. Empty
    /// for a pure Nazca design.
    /// </summary>
    public static IReadOnlyList<Component> CollectGdsFactoryNativeComponents(DesignCanvasViewModel canvas)
    {
        var result = new List<Component>();
        foreach (var vm in canvas.Components)
        {
            var comp = vm.Component;
            if (comp is ComponentGroup group)
            {
                foreach (var child in group.GetAllComponentsRecursive())
                    if (IsGdsFactoryNative(child))
                        result.Add(child);
            }
            else if (IsGdsFactoryNative(comp))
            {
                result.Add(comp);
            }
        }
        return result;
    }

    /// <summary>True when the component carries a module-qualified gdsfactory factory name.</summary>
    private static bool IsGdsFactoryNative(Component c) =>
        !string.IsNullOrWhiteSpace(c.GdsFactoryFunction) && c.GdsFactoryFunction!.Contains('.');
}
