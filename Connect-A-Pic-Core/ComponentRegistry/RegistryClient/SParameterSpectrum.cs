using System.Numerics;
using System.Text.Json.Serialization;

namespace CAP_Core.ComponentRegistry.RegistryClient;

/// <summary>
/// A sampled S-parameter spectrum artifact from the photonic registry:
/// a wavelength grid plus one complex-valued trace per port pair.
/// Wire format: <c>{ wavelength_um: [...], s: [{ from, to, re: [...], im: [...] }] }</c>.
/// </summary>
public class SParameterSpectrum
{
    /// <summary>Wavelength sample points in micrometers.</summary>
    [JsonPropertyName("wavelength_um")]
    public List<double> WavelengthUm { get; set; } = new();

    /// <summary>Per-port-pair sampled complex S-parameters.</summary>
    [JsonPropertyName("s")]
    public List<SParameterTrace> S { get; set; } = new();

    /// <summary>
    /// Finds the trace from port <paramref name="from"/> to port <paramref name="to"/>,
    /// or null when the spectrum contains no such port pair.
    /// </summary>
    public SParameterTrace? FindTrace(string from, string to) =>
        S.FirstOrDefault(t => t.From == from && t.To == to);
}

/// <summary>Sampled complex S-parameter values for one port pair.</summary>
public class SParameterTrace
{
    /// <summary>Source port name, e.g. <c>o1</c>.</summary>
    [JsonPropertyName("from")]
    public string From { get; set; } = "";

    /// <summary>Destination port name, e.g. <c>o2</c>.</summary>
    [JsonPropertyName("to")]
    public string To { get; set; } = "";

    /// <summary>Real parts, one value per wavelength sample.</summary>
    [JsonPropertyName("re")]
    public List<double> Re { get; set; } = new();

    /// <summary>Imaginary parts, one value per wavelength sample.</summary>
    [JsonPropertyName("im")]
    public List<double> Im { get; set; } = new();

    /// <summary>
    /// Combines <see cref="Re"/> and <see cref="Im"/> into complex samples
    /// usable for plotting. Throws when the two arrays differ in length.
    /// </summary>
    public Complex[] ToComplexArray()
    {
        if (Re.Count != Im.Count)
            throw new InvalidDataException(
                $"S-parameter trace {From}->{To} has {Re.Count} real but {Im.Count} imaginary samples.");

        var result = new Complex[Re.Count];
        for (int i = 0; i < Re.Count; i++)
            result[i] = new Complex(Re[i], Im[i]);
        return result;
    }
}
