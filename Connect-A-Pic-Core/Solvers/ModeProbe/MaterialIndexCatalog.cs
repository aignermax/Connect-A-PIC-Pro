namespace CAP_Core.Solvers.ModeProbe;

/// <summary>
/// Refractive indices of common photonic platform materials near 1550 nm.
/// Used to translate the PDK process fingerprint's material names into the
/// core/cladding indices the mode solver needs.
/// </summary>
public static class MaterialIndexCatalog
{
    /// <summary>Silicon core index at 1550 nm.</summary>
    public const double SiliconIndex = 3.48;

    /// <summary>Silicon nitride (Si₃N₄) index at 1550 nm.</summary>
    public const double SiliconNitrideIndex = 2.00;

    /// <summary>Silica (SiO₂) cladding index at 1550 nm.</summary>
    public const double SilicaIndex = 1.44;

    /// <summary>Indium phosphide index at 1550 nm.</summary>
    public const double IndiumPhosphideIndex = 3.17;

    /// <summary>Lithium niobate (ordinary) index at 1550 nm.</summary>
    public const double LithiumNiobateIndex = 2.21;

    /// <summary>Air / vacuum index.</summary>
    public const double AirIndex = 1.00;

    /// <summary>
    /// Looks up the refractive index for a PDK material name (e.g. "Si", "SiN",
    /// "Si3N4", "SiO2", "Oxide", "InP", "LiNbO3", "Air"). Matching is
    /// case-insensitive. Returns false for unknown or empty names.
    /// </summary>
    public static bool TryGetIndex(string? materialName, out double index)
    {
        index = 0;
        if (string.IsNullOrWhiteSpace(materialName)) return false;

        var name = materialName.Trim().ToUpperInvariant();
        index = name switch
        {
            "SI" or "SILICON" or "C-SI" => SiliconIndex,
            "SIN" or "SI3N4" or "SINX" or "NITRIDE" or "SILICON NITRIDE" => SiliconNitrideIndex,
            "SIO2" or "OXIDE" or "SILICA" or "GLASS" => SilicaIndex,
            "INP" => IndiumPhosphideIndex,
            "LINBO3" or "LN" or "LNOI" => LithiumNiobateIndex,
            "AIR" or "VACUUM" => AirIndex,
            _ => 0,
        };
        return index > 0;
    }
}
