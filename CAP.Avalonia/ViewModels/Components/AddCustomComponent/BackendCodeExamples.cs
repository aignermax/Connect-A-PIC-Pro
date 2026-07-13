using CAP.Avalonia.Services.AddCustomComponent;

namespace CAP.Avalonia.ViewModels.Components.AddCustomComponent;

/// <summary>
/// The starter Python snippet shown for each <see cref="GeometryBackend"/> in the "New
/// Component" editor. Kept as named constants (rather than only inline in XAML) so
/// <see cref="NewComponentViewModel"/> can autoload the same text into the editor and later
/// recognize it as an untouched, auto-inserted example (as opposed to user-authored code).
/// The strings are the single source of truth; the XAML help-box literals mirror them.
/// </summary>
public static class BackendCodeExamples
{
    /// <summary>Starter snippet for <see cref="GeometryBackend.GdsFactory"/>.</summary>
    public const string GdsFactory = "import gdsfactory as gf\ncomponent = gf.components.mmi1x2()";

    /// <summary>Starter snippet for <see cref="GeometryBackend.Nazca"/>.</summary>
    public const string Nazca = "import nazca as nd\ncomponent = nd.Cell(name='my_component')";

    /// <summary>Returns the starter snippet matching <paramref name="backend"/>.</summary>
    public static string For(GeometryBackend backend) =>
        backend == GeometryBackend.GdsFactory ? GdsFactory : Nazca;
}
