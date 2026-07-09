namespace CAP_Core.Analysis.EyeDiagram;

/// <summary>
/// Additive Gaussian receiver-noise model for direct detection: photodiode shot
/// noise, thermal (Johnson) noise of the load, and laser RIN. All contributions
/// are computed as photocurrent standard deviations and converted back to
/// optical-power units via the responsivity, so they can be combined directly
/// with the optical eye-diagram amplitudes.
/// </summary>
public class NoiseModel
{
    private const double ElementaryChargeCoulomb = 1.602176634e-19;
    private const double BoltzmannJoulePerKelvin = 1.380649e-23;
    private const double DecibelsPerDecade = 10.0;

    /// <summary>Photodiode responsivity in A/W (typical InGaAs PIN ≈ 0.8).</summary>
    public double ResponsivityAPerW { get; init; } = 0.8;

    /// <summary>Receiver electrical bandwidth in Hz (typically ≈ 0.75 × bit rate).</summary>
    public double BandwidthHz { get; init; } = 18.75e9;

    /// <summary>Laser relative intensity noise in dB/Hz (typical DFB ≈ −145).</summary>
    public double RinDbPerHz { get; init; } = -145;

    /// <summary>Receiver temperature in Kelvin.</summary>
    public double TemperatureKelvin { get; init; } = 300;

    /// <summary>Transimpedance / load resistance in Ohm.</summary>
    public double LoadResistanceOhm { get; init; } = 50;

    /// <summary>Shot-noise photocurrent σ in A at the given optical power: σ² = 2·q·R·P·B.</summary>
    /// <param name="opticalPowerW">Received optical power in W (clamped at 0).</param>
    public double ShotNoiseSigmaAmpere(double opticalPowerW)
        => Math.Sqrt(2 * ElementaryChargeCoulomb * ResponsivityAPerW * Math.Max(opticalPowerW, 0) * BandwidthHz);

    /// <summary>Thermal-noise photocurrent σ in A: σ² = 4·k·T·B / R_load.</summary>
    public double ThermalNoiseSigmaAmpere()
        => Math.Sqrt(4 * BoltzmannJoulePerKelvin * TemperatureKelvin * BandwidthHz / LoadResistanceOhm);

    /// <summary>RIN photocurrent σ in A: σ² = 10^(RIN/10) · (R·P)² · B.</summary>
    /// <param name="opticalPowerW">Received optical power in W (clamped at 0).</param>
    public double RinNoiseSigmaAmpere(double opticalPowerW)
    {
        double photocurrent = ResponsivityAPerW * Math.Max(opticalPowerW, 0);
        return Math.Sqrt(Math.Pow(10, RinDbPerHz / DecibelsPerDecade) * photocurrent * photocurrent * BandwidthHz);
    }

    /// <summary>
    /// Total noise σ expressed in optical-power units (W): the quadrature sum of
    /// shot, thermal, and RIN photocurrent noise divided by the responsivity.
    /// </summary>
    /// <param name="opticalPowerW">Received optical power in W at the sampling instant.</param>
    public double TotalSigmaOpticalPower(double opticalPowerW)
    {
        double shot = ShotNoiseSigmaAmpere(opticalPowerW);
        double thermal = ThermalNoiseSigmaAmpere();
        double rin = RinNoiseSigmaAmpere(opticalPowerW);
        return Math.Sqrt(shot * shot + thermal * thermal + rin * rin) / ResponsivityAPerW;
    }
}
