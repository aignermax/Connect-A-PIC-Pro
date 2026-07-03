namespace CAP.Avalonia.Services;

/// <summary>
/// Ready-to-run gdsfactory code examples for the per-instance code editor's quick
/// help (issue #637) — the gdsfactory counterpart of <see cref="NazcaCodeExamples"/>.
/// Each string is a self-contained snippet that defines <c>component()</c> returning
/// a <c>gf.Component</c>, matching the contract of
/// <c>scripts/render_gdsfactory_preview.py</c>.
/// </summary>
public static class GdsFactoryCodeExamples
{
    /// <summary>Minimal runnable starter.</summary>
    public const string Starter = """
        import gdsfactory as gf

        def component():
            c = gf.Component()
            ref = c.add_ref(gf.components.straight(length=20))
            c.add_ports(ref.ports)
            return c
        """;

    /// <summary>
    /// Showcase circuit chaining common gdsfactory elements (straight, bend,
    /// taper, MMI) with re-exposed ports. Port convention: gdsfactory port
    /// orientations point OUTWARD, matching the Nazca examples.
    /// </summary>
    public const string Complex = """
        import gdsfactory as gf

        # Showcase: common gdsfactory elements chained via connect(). Ports on the
        # outer component are re-exported with add_port so Lunima picks them up.
        # See https://gdsfactory.github.io/gdsfactory/ for the full component set.
        def component():
            c = gf.Component()
            s1 = c.add_ref(gf.components.straight(length=15))
            b1 = c.add_ref(gf.components.bend_euler(radius=10, angle=90))
            b1.connect('o1', s1.ports['o2'])
            t1 = c.add_ref(gf.components.taper(length=10, width1=0.5, width2=1.0))
            t1.connect('o1', b1.ports['o2'])
            c.add_port('in', port=s1.ports['o1'])
            c.add_port('out', port=t1.ports['o2'])
            return c
        """;
}
