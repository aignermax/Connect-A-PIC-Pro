namespace CAP.Avalonia.Services.GdsFactoryExport;

/// <summary>How the gdsfactory export represents PDK components.</summary>
public enum GdsFactoryComponentMode
{
    /// <summary>
    /// Self-contained stub geometry from Lunima's dimensions and pins — mirrors the
    /// Nazca export; runs with a plain gdsfactory install, no PDK package needed.
    /// </summary>
    StandaloneStubs,

    /// <summary>
    /// Real ubcpdk (SiEPIC EBeam) cells where a mapping exists; components without a
    /// ubcpdk equivalent fall back to stub geometry.
    /// </summary>
    UbcPdkCells,
}

/// <summary>Options for a gdsfactory export run.</summary>
/// <param name="Mode">Component representation mode.</param>
public sealed record GdsFactoryExportOptions(GdsFactoryComponentMode Mode);
