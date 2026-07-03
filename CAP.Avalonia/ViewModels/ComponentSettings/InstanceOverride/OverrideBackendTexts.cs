namespace CAP.Avalonia.ViewModels.ComponentSettings.InstanceOverride;

/// <summary>
/// Backend-specific UI texts for the per-instance override editor (issue #637):
/// section title, hint, docs link, starter stub, showcase example and cheat sheet.
/// Keeps <see cref="InstanceNazcaCodeEditorViewModel"/> backend-agnostic — the
/// ViewModel just exposes the record matching the selected backend.
/// </summary>
/// <param name="SectionTitle">Header of the override editor section.</param>
/// <param name="EditorHint">One-line hint above the code editor.</param>
/// <param name="DocsButtonText">Caption for the external-docs button.</param>
/// <param name="DocsUrl">URL of the backend's reference documentation.</param>
/// <param name="Stub">Comment-only starter shown in an empty override editor.</param>
/// <param name="StarterExample">Runnable showcase snippet for the help flyout / Insert.</param>
/// <param name="CheatSheet">Compact list of the backend's most-used elements.</param>
public sealed record OverrideBackendTexts(
    string SectionTitle,
    string EditorHint,
    string DocsButtonText,
    string DocsUrl,
    string Stub,
    string StarterExample,
    string CheatSheet)
{
    /// <summary>Texts for the Nazca backend (the pre-#637 default).</summary>
    public static OverrideBackendTexts Nazca { get; } = new(
        SectionTitle: "Nazca Code (geometry only)",
        EditorHint: "Override — your own self-contained Nazca code. Run shows the real component until you define a component().",
        DocsButtonText: "Nazca docs ↗",
        DocsUrl: "https://nazca-design.org/manual/",
        Stub:
            "# Override this component's geometry with your own self-contained Nazca code.\n" +
            "# Until you define a component() below, Run Preview shows the real component.\n" +
            "# Example:\n" +
            "# import nazca as nd\n" +
            "# def component():\n" +
            "#     with nd.Cell() as C:\n" +
            "#         nd.strt(length=20).put()\n" +
            "#         return C\n",
        StarterExample: Services.NazcaCodeExamples.Complex,
        CheatSheet:
            "strt(length, width) · bend(radius, angle, width) · taper(length, width1, width2) · " +
            "ptaper(...) · euler(width, radius, angle) · sinebend(width, distance, offset) · " +
            "cobra(xya=(x,y,a), width1, width2) · ring(radius, width) · text(text, height, layer) · " +
            "Pin(name).put(x, y, angle)");

    /// <summary>Texts for the gdsfactory backend (issue #637).</summary>
    public static OverrideBackendTexts GdsFactory { get; } = new(
        SectionTitle: "gdsfactory Code (geometry only)",
        EditorHint: "Override — your own self-contained gdsfactory code defining component() that returns a gf.Component. Run Preview renders it via the gdsfactory environment.",
        DocsButtonText: "gdsfactory docs ↗",
        DocsUrl: "https://gdsfactory.github.io/gdsfactory/",
        Stub:
            "# Override this component's geometry with your own self-contained gdsfactory code.\n" +
            "# Define component() returning a gf.Component, then Run Preview.\n" +
            "# Example:\n" +
            "# import gdsfactory as gf\n" +
            "# def component():\n" +
            "#     c = gf.Component()\n" +
            "#     ref = c.add_ref(gf.components.straight(length=20))\n" +
            "#     c.add_ports(ref.ports)\n" +
            "#     return c\n",
        StarterExample: Services.GdsFactoryCodeExamples.Complex,
        CheatSheet:
            "gf.components.straight(length, width) · bend_euler(radius, angle) · bend_circular(radius, angle) · " +
            "taper(length, width1, width2) · mmi1x2() · mmi2x2() · ring_single(radius) · " +
            "c.add_ref(comp) · ref.connect('o1', other.ports['o2']) · c.add_port(name, port=...)");

    /// <summary>Returns the texts for the given backend selection.</summary>
    public static OverrideBackendTexts For(bool isGdsFactory) => isGdsFactory ? GdsFactory : Nazca;
}
