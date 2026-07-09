using System;

namespace CAP_Core.Components.Process;

/// <summary>
/// Decides whether two <see cref="ProcessFingerprint"/>s describe the same fabrication
/// process (issue #570). Core material and cladding must match exactly (case-insensitive);
/// core thickness and design wavelength must fall within a small tolerance.
/// </summary>
public static class ProcessCompatibility
{
    /// <summary>Max core-thickness difference (nm) still considered the same process.</summary>
    public const double CoreThicknessToleranceNm = 5;

    /// <summary>Max design-wavelength difference (nm) still considered the same process.</summary>
    public const int WavelengthToleranceNm = 40;

    /// <summary>Returns true when both fingerprints are specified and physically compatible.</summary>
    public static bool AreCompatible(ProcessFingerprint a, ProcessFingerprint b)
    {
        if (!a.IsSpecified || !b.IsSpecified)
            return false;

        if (!string.Equals(a.CoreMaterial, b.CoreMaterial, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.Equals(a.Cladding, b.Cladding, StringComparison.OrdinalIgnoreCase))
            return false;

        if (Math.Abs(a.CoreThicknessNm.Value - b.CoreThicknessNm.Value) > CoreThicknessToleranceNm)
            return false;

        return Math.Abs(a.DesignWavelengthNm - b.DesignWavelengthNm) <= WavelengthToleranceNm;
    }
}
