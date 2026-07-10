using CAP_Core.Components.Process;
using CAP_Core.Solvers.ModeProbe;
using Shouldly;
using Xunit;

namespace UnitTests.Solvers.ModeProbe;

public class ProbeCrossSectionResolverTests
{
    private static readonly ProcessFingerprint SoiProcess = new(
        CoreMaterial: "Si", CoreThicknessNm: 220, Cladding: "SiO2",
        DesignWavelengthNm: 1550, ProcessName: "SOI 220");

    [Fact]
    public void ConnectionWidthAndFullPdk_NothingAssumed()
    {
        var result = ProbeCrossSectionResolver.Resolve(0.5, SoiProcess, ProbeCrossSection.Default);

        result.WidthMicrometers.ShouldBe(0.5);
        result.HeightMicrometers.ShouldBe(0.22, 1e-9);
        result.CoreIndex.ShouldBe(MaterialIndexCatalog.SiliconIndex);
        result.CladIndex.ShouldBe(MaterialIndexCatalog.SilicaIndex);
        result.IsGeometryAssumed.ShouldBeFalse();
        result.SourceDescription.ShouldContain("connection");
        result.SourceDescription.ShouldContain("PDK");
    }

    [Fact]
    public void MissingConnectionWidth_FallsBackAndFlagsAssumed()
    {
        var fallback = ProbeCrossSection.Default with { WidthMicrometers = 0.7 };

        var result = ProbeCrossSectionResolver.Resolve(null, SoiProcess, fallback);

        result.WidthMicrometers.ShouldBe(0.7);
        result.IsGeometryAssumed.ShouldBeTrue();
        result.SourceDescription.ShouldContain("width assumed");
    }

    [Fact]
    public void NoProcess_UsesFallbackEverywhereAndFlagsAssumed()
    {
        var fallback = new ProbeCrossSection(0.6, 0.3, 0.05, 2.0, 1.45, true, "manual");

        var result = ProbeCrossSectionResolver.Resolve(null, null, fallback);

        result.WidthMicrometers.ShouldBe(0.6);
        result.HeightMicrometers.ShouldBe(0.3);
        result.SlabHeightMicrometers.ShouldBe(0.05);
        result.CoreIndex.ShouldBe(2.0);
        result.CladIndex.ShouldBe(1.45);
        result.IsGeometryAssumed.ShouldBeTrue();
    }

    [Fact]
    public void UnknownCoreMaterial_FallsBackToFallbackIndicesAndFlagsAssumed()
    {
        var exotic = SoiProcess with { CoreMaterial = "Unobtainium" };

        var result = ProbeCrossSectionResolver.Resolve(0.5, exotic, ProbeCrossSection.Default);

        result.CoreIndex.ShouldBe(ProbeCrossSection.Default.CoreIndex);
        result.CladIndex.ShouldBe(MaterialIndexCatalog.SilicaIndex); // known clad still used
        result.IsGeometryAssumed.ShouldBeTrue();
    }

    [Fact]
    public void SiNProcess_ResolvesNitrideIndexAndThickness()
    {
        var sin = new ProcessFingerprint("SiN", 400, "SiO2", 1550, null);

        var result = ProbeCrossSectionResolver.Resolve(1.0, sin, ProbeCrossSection.Default);

        result.HeightMicrometers.ShouldBe(0.4, 1e-9);
        result.CoreIndex.ShouldBe(MaterialIndexCatalog.SiliconNitrideIndex);
        result.IsGeometryAssumed.ShouldBeFalse();
    }

    [Theory]
    [InlineData("Si", MaterialIndexCatalog.SiliconIndex)]
    [InlineData("silicon", MaterialIndexCatalog.SiliconIndex)]
    [InlineData("Si3N4", MaterialIndexCatalog.SiliconNitrideIndex)]
    [InlineData("SiO2", MaterialIndexCatalog.SilicaIndex)]
    [InlineData("InP", MaterialIndexCatalog.IndiumPhosphideIndex)]
    [InlineData("LiNbO3", MaterialIndexCatalog.LithiumNiobateIndex)]
    [InlineData("air", MaterialIndexCatalog.AirIndex)]
    public void MaterialCatalog_KnowsCommonPlatforms(string material, double expected)
    {
        MaterialIndexCatalog.TryGetIndex(material, out var index).ShouldBeTrue();
        index.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Unobtainium")]
    public void MaterialCatalog_RejectsUnknown(string? material)
    {
        MaterialIndexCatalog.TryGetIndex(material, out _).ShouldBeFalse();
    }
}
