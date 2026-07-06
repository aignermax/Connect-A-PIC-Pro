namespace CAP_Core.Components.Process;

/// <summary>
/// The physical identity of a fabrication process, derived from a PDK (issue #570).
/// Two PDKs are usable on the same chip when their fingerprints are compatible.
/// </summary>
/// <param name="CoreMaterial">Waveguide core material name (e.g. "Si", "SiN"); null if unspecified.</param>
/// <param name="CoreThicknessNm">Core layer thickness in nm; null if unspecified.</param>
/// <param name="Cladding">Cladding material name (e.g. "SiO2"); null if unspecified.</param>
/// <param name="DesignWavelengthNm">Representative design wavelength in nm.</param>
/// <param name="ProcessName">Human-readable process label for display; not used for matching.</param>
public sealed record ProcessFingerprint(
    string? CoreMaterial,
    double? CoreThicknessNm,
    string? Cladding,
    int DesignWavelengthNm,
    string? ProcessName)
{
    /// <summary>True when the fingerprint carries enough physical data to group by (core material present).</summary>
    public bool IsSpecified => !string.IsNullOrWhiteSpace(CoreMaterial);
}
