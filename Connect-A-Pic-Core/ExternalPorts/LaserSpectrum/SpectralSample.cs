namespace CAP_Core.ExternalPorts.LaserSpectrum
{
    /// <summary>
    /// One weighted wavelength sample of a laser spectrum. The weights of all
    /// samples of a spectrum sum to 1, so multiplying a source's total power by
    /// <see cref="Weight"/> yields the optical power carried at this wavelength.
    /// </summary>
    /// <param name="WavelengthNm">Sample wavelength in nanometers.</param>
    /// <param name="Weight">Fraction of the total source power at this wavelength.</param>
    public readonly record struct SpectralSample(int WavelengthNm, double Weight);
}
