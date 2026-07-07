using System.Numerics;
using System.Text.Json.Serialization;

namespace CAP_Core.ComponentRegistry;

/// <summary>
/// Sampled complex S-parameter spectra of a registry artifact: a shared
/// wavelength grid plus one complex spectrum per port pair.
/// </summary>
public sealed class SParameterSpectrum
{
    /// <summary>Gets the wavelength sample grid in micrometers.</summary>
    [JsonPropertyName("wavelength_um")]
    public List<double> WavelengthUm { get; init; } = new();

    /// <summary>Gets the per-port-pair spectra.</summary>
    public List<SParameterEntry> S { get; init; } = new();

    /// <summary>
    /// Returns the complex spectrum from <paramref name="fromPort"/> to
    /// <paramref name="toPort"/>, or null if the artifact contains no such entry.
    /// The array is index-aligned with <see cref="WavelengthUm"/>.
    /// </summary>
    public Complex[]? GetSpectrum(string fromPort, string toPort)
    {
        var entry = S.FirstOrDefault(e => e.From == fromPort && e.To == toPort);
        if (entry == null || entry.Re.Count != WavelengthUm.Count || entry.Im.Count != WavelengthUm.Count)
            return null;

        var result = new Complex[WavelengthUm.Count];
        for (int i = 0; i < result.Length; i++)
            result[i] = new Complex(entry.Re[i], entry.Im[i]);
        return result;
    }
}

/// <summary>One S-parameter spectrum between a pair of ports, split into real and imaginary samples.</summary>
public sealed class SParameterEntry
{
    /// <summary>Gets the source port name.</summary>
    public string From { get; init; } = "";

    /// <summary>Gets the destination port name.</summary>
    public string To { get; init; } = "";

    /// <summary>Gets the real parts, index-aligned with the wavelength grid.</summary>
    public List<double> Re { get; init; } = new();

    /// <summary>Gets the imaginary parts, index-aligned with the wavelength grid.</summary>
    public List<double> Im { get; init; } = new();
}
