using CAP_Core.Components.Process;
using Shouldly;
using Xunit;

namespace UnitTests.Components.Process;

public class ProcessCompatibilityTests
{
    private static ProcessFingerprint Fp(string? mat, double? thick, string? clad, int wl) =>
        new(mat, thick, clad, wl, ProcessName: null);

    [Fact]
    public void SameMaterialWithinTolerances_IsCompatible()
    {
        ProcessCompatibility.AreCompatible(
            Fp("Si", 220, "SiO2", 1550), Fp("si", 222, "sio2", 1560)).ShouldBeTrue();
    }

    [Fact]
    public void DifferentCoreMaterial_IsNotCompatible()
    {
        ProcessCompatibility.AreCompatible(
            Fp("Si", 220, "SiO2", 1550), Fp("SiN", 220, "SiO2", 1550)).ShouldBeFalse();
    }

    [Fact]
    public void ThicknessBeyondTolerance_IsNotCompatible()
    {
        ProcessCompatibility.AreCompatible(
            Fp("Si", 220, "SiO2", 1550), Fp("Si", 340, "SiO2", 1550)).ShouldBeFalse();
    }

    [Fact]
    public void WavelengthBeyondTolerance_IsNotCompatible()
    {
        ProcessCompatibility.AreCompatible(
            Fp("Si", 220, "SiO2", 1550), Fp("Si", 220, "SiO2", 1310)).ShouldBeFalse();
    }

    [Fact]
    public void UnspecifiedFingerprint_IsNeverCompatible()
    {
        var unspecified = Fp(null, null, null, 1550);
        ProcessCompatibility.AreCompatible(unspecified, unspecified).ShouldBeFalse();
        unspecified.IsSpecified.ShouldBeFalse();
    }
}
