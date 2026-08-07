namespace CAP_Core.Components.Process;

/// <summary>
/// Fabrication tolerances of a process: the 1-sigma deviations of the
/// waveguide cross-section that Monte-Carlo variance analysis samples from. Declared
/// per process in the PDK (issue #570); processes without a declaration fall back to
/// typical silicon-photonics MPW values.
/// </summary>
/// <param name="WidthSigmaNm">1-sigma deviation of the waveguide core width in nm.</param>
/// <param name="ThicknessSigmaNm">1-sigma deviation of the core layer thickness in nm.</param>
public sealed record ProcessTolerances(double WidthSigmaNm, double ThicknessSigmaNm)
{
    /// <summary>Typical MPW lithography width deviation (1 sigma, nm).</summary>
    public const double DefaultWidthSigmaNm = 10;

    /// <summary>Typical SOI wafer thickness deviation (1 sigma, nm).</summary>
    public const double DefaultThicknessSigmaNm = 5;

    /// <summary>The fallback tolerances used when a PDK declares none.</summary>
    public static ProcessTolerances Default { get; } =
        new(DefaultWidthSigmaNm, DefaultThicknessSigmaNm);
}
