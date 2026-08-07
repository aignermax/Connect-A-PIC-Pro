namespace CAP.Avalonia.Services;

/// <summary>
/// Records the configuration applied to a single light source during simulation.
/// A source with a finite linewidth (Issue #819) is expanded into several
/// weighted wavelength samples listed in <see cref="SampleWavelengthsNm"/>.
/// </summary>
public class SourceConfigInfo
{
    public string ComponentId { get; }

    /// <summary>Center wavelength of the source in nm.</summary>
    public int WavelengthNm { get; }

    /// <summary>Total optical input power of the source (linear).</summary>
    public double InputPower { get; }

    /// <summary>True when the source spectrum spans more than one wavelength sample.</summary>
    public bool HasSpectralLinewidth { get; }

    /// <summary>All wavelength samples this source injects (just the center for ideal sources).</summary>
    public IReadOnlyList<int> SampleWavelengthsNm { get; }

    public SourceConfigInfo(
        string componentId,
        int wavelengthNm,
        double inputPower,
        IReadOnlyList<int>? sampleWavelengthsNm = null)
    {
        ComponentId = componentId;
        WavelengthNm = wavelengthNm;
        InputPower = inputPower;
        SampleWavelengthsNm = sampleWavelengthsNm ?? new[] { wavelengthNm };
        HasSpectralLinewidth = SampleWavelengthsNm.Count > 1;
    }
}
