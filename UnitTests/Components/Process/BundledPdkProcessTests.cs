using System.IO;
using System.Linq;
using CAP_Core.Components.Process;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Components.Process;

public class BundledPdkProcessTests
{
    private static string PdkDir =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PDKs");

    [Theory]
    [InlineData("demo-pdk.json")]
    [InlineData("siepic-ebeam-pdk.json")]
    public void BundledPdk_HasSpecifiedProcessFingerprint(string file)
    {
        var draft = new PdkLoader().LoadFromFile(Path.Combine(PdkDir, file));
        ProcessFingerprintFactory.From(draft).IsSpecified.ShouldBeTrue();
    }

    [Fact]
    public void DemoAndSiepic_ShareOneProcessGroup()
    {
        var loader = new PdkLoader();
        var entries = new[] { "demo-pdk.json", "siepic-ebeam-pdk.json" }
            .Select(f => loader.LoadFromFile(Path.Combine(PdkDir, f)))
            .Select(d => new PdkProcessEntry(d.Name, ProcessFingerprintFactory.From(d)));

        ProcessCatalog.BuildGroups(entries).Count.ShouldBe(1);
    }

    [Fact]
    public void CornerStoneSin_LoadsAsGdsFactoryBackend_WithComponents()
    {
        var draft = new PdkLoader().LoadFromFile(Path.Combine(PdkDir, "cornerstone-sin-pdk.json"));

        draft.IsGdsFactoryBackend.ShouldBeTrue();
        draft.Components.ShouldNotBeEmpty();
        draft.Components.ShouldAllBe(c => !string.IsNullOrEmpty(c.GdsFactoryFunction));
        ProcessFingerprintFactory.From(draft).IsSpecified.ShouldBeTrue();
    }

    [Fact]
    public void CornerStoneSin_EveryComponent_HasPlacementOffsets()
    {
        var draft = new PdkLoader().LoadFromFile(Path.Combine(PdkDir, "cornerstone-sin-pdk.json"));

        draft.Components.ShouldAllBe(c => c.NazcaOriginOffsetX != null && c.NazcaOriginOffsetY != null);
    }

    [Fact]
    public void CornerStoneSin_Mmi1x2_HasRealMultiWavelengthSMatrix_AndStraightPassesThrough()
    {
        var draft = new PdkLoader().LoadFromFile(Path.Combine(PdkDir, "cornerstone-sin-pdk.json"));

        var mmi = draft.Components.Single(c => c.GdsFactoryFunction == "cspdk.sin300.mmi1x2");
        mmi.SMatrix.ShouldNotBeNull();
        mmi.SMatrix!.WavelengthData.ShouldNotBeNull();
        mmi.SMatrix.WavelengthData!.Count.ShouldBeGreaterThan(1);
        var at1550 = mmi.SMatrix.WavelengthData!.Single(w => w.WavelengthNm == 1550);
        at1550.Connections.ShouldNotBeEmpty();
        at1550.Connections.ShouldAllBe(c => c.Magnitude > 0.1);

        var straight = draft.Components.Single(c => c.GdsFactoryFunction == "cspdk.sin300.straight");
        straight.SMatrix!.Connections.ShouldContain(c => c.Magnitude == 1.0);
    }

    [Theory]
    [InlineData("cspdk.sin300.grating_coupler_rectangular")]
    [InlineData("cspdk.sin300.grating_coupler_elliptical")]
    [InlineData("cspdk.sin300.coupler")]
    [InlineData("cspdk.sin300.coupler_straight")]
    [InlineData("cspdk.sin300.mzi")]
    public void CornerStoneSin_GratingsCouplerAndMzi_CarryRealMultiWavelengthSMatrices(
        string gdsFactoryFunction)
    {
        var draft = new PdkLoader().LoadFromFile(Path.Combine(PdkDir, "cornerstone-sin-pdk.json"));

        var comp = draft.Components.Single(c => c.GdsFactoryFunction == gdsFactoryFunction);
        comp.SMatrix.ShouldNotBeNull();
        comp.SMatrix!.WavelengthData.ShouldNotBeNull();
        comp.SMatrix.WavelengthData!.Count.ShouldBeGreaterThan(1);
        var at1550 = comp.SMatrix.WavelengthData.Single(w => w.WavelengthNm == 1550);
        at1550.Connections.ShouldNotBeEmpty();
        at1550.Connections.ShouldAllBe(c => c.Magnitude > 0 && c.Magnitude <= 1);
    }

    [Fact]
    public void CornerStoneSin_EveryComponent_CarriesAnSMatrix()
    {
        var draft = new PdkLoader().LoadFromFile(Path.Combine(PdkDir, "cornerstone-sin-pdk.json"));

        draft.Components.ShouldAllBe(c => c.SMatrix != null);
    }

    [Fact]
    public void CornerStoneSin_MultiWavelengthSMatrices_CoverTheCBandSweepRange()
    {
        var draft = new PdkLoader().LoadFromFile(Path.Combine(PdkDir, "cornerstone-sin-pdk.json"));

        foreach (var comp in draft.Components.Where(c => c.SMatrix?.WavelengthData is { Count: > 0 }))
        {
            var wavelengths = comp.SMatrix!.WavelengthData!.Select(w => w.WavelengthNm).ToList();
            wavelengths.Min().ShouldBeLessThanOrEqualTo(1500, comp.Name);
            wavelengths.Max().ShouldBeGreaterThanOrEqualTo(1600, comp.Name);
        }
    }

    [Fact]
    public void CornerStoneSin_CouplerStraight_PlacedComponent_UsesCspdkSMatrixOver1500To1600()
    {
        var draft = new PdkLoader().LoadFromFile(Path.Combine(PdkDir, "cornerstone-sin-pdk.json"));
        var comp = draft.Components.Single(
            c => c.GdsFactoryFunction == "cspdk.sin300.coupler_straight");

        var template = CAP.Avalonia.Services.PdkTemplateConverter.ConvertToTemplate(
            comp, draft.Name, draft.NazcaModuleName, draft.GdsFactoryRoutingCrossSection);
        var placed = CAP.Avalonia.ViewModels.Library.ComponentTemplates
            .CreateFromTemplate(template, 0, 0);

        placed.WaveLengthToSMatrixMap.Keys.Min().ShouldBe(1500);
        placed.WaveLengthToSMatrixMap.Keys.Max().ShouldBe(1600);
        placed.WaveLengthToSMatrixMap[1550].GetNonNullValues().Values
            .ShouldContain(v => v.Magnitude > 0.5);
    }

    [Fact]
    public void CornerStoneSin_GratingCoupler_CouplesWaveguideToFibre_PeakedAt1550()
    {
        var draft = new PdkLoader().LoadFromFile(Path.Combine(PdkDir, "cornerstone-sin-pdk.json"));
        var gc = draft.Components.Single(
            c => c.GdsFactoryFunction == "cspdk.sin300.grating_coupler_rectangular");

        double At(int wl) => gc.SMatrix!.WavelengthData!
            .Single(w => w.WavelengthNm == wl).Connections
            .Single(c => c.FromPin == "o1" && c.ToPin == "o2").Magnitude;

        At(1550).ShouldBeGreaterThan(At(1500) * 2);
        At(1550).ShouldBeGreaterThan(At(1600) * 2);
    }

    [Fact]
    public void CornerStoneSin_DirectionalCoupler_SplitsBothInputsAcrossBothOutputs()
    {
        var draft = new PdkLoader().LoadFromFile(Path.Combine(PdkDir, "cornerstone-sin-pdk.json"));
        var coupler = draft.Components.Single(c => c.GdsFactoryFunction == "cspdk.sin300.coupler");

        var at1550 = coupler.SMatrix!.WavelengthData!.Single(w => w.WavelengthNm == 1550);
        at1550.Connections.Count.ShouldBe(4);
        at1550.Connections.ShouldAllBe(c => c.Magnitude > 0.5 && c.Magnitude < 0.8);
    }

    [Fact]
    public void CornerStoneSin_Mzi_ShowsInterferenceFringesAcrossTheBand()
    {
        var draft = new PdkLoader().LoadFromFile(Path.Combine(PdkDir, "cornerstone-sin-pdk.json"));
        var mzi = draft.Components.Single(c => c.GdsFactoryFunction == "cspdk.sin300.mzi");

        var barMagnitudes = mzi.SMatrix!.WavelengthData!
            .Select(w => w.Connections.Single(c => c.FromPin == "o1" && c.ToPin == "o2").Magnitude)
            .ToList();

        barMagnitudes.Max().ShouldBeGreaterThan(0.8);
        barMagnitudes.Min().ShouldBeLessThan(0.1);
    }

    [Fact]
    public void CornerStoneSin_GratingCoupler_HasWavelengthPeakedCouplingBand()
    {
        var draft = new PdkLoader().LoadFromFile(Path.Combine(PdkDir, "cornerstone-sin-pdk.json"));
        var gc = draft.Components.Single(c => c.GdsFactoryFunction == "cspdk.sin300.grating_coupler_rectangular");

        gc.SMatrix.ShouldNotBeNull();
        var wl = gc.SMatrix!.WavelengthData!;
        double MagAt(int nm) => wl.Single(w => w.WavelengthNm == nm).Connections[0].Magnitude;
        MagAt(1550).ShouldBeGreaterThan(MagAt(1500));
        MagAt(1550).ShouldBeGreaterThan(MagAt(1600));
    }

    [Fact]
    public void CornerStoneSin_RoutingCrossSection_FlowsFromPdkToComponentTemplate()
    {
        var draft = new PdkLoader().LoadFromFile(Path.Combine(PdkDir, "cornerstone-sin-pdk.json"));
        draft.GdsFactoryRoutingCrossSection.ShouldBe("xs_nc");

        var comp = draft.Components.First(c => c.GdsFactoryFunction == "cspdk.sin300.mmi1x2");
        var template = CAP.Avalonia.Services.PdkTemplateConverter.ConvertToTemplate(
            comp, draft.Name, draft.NazcaModuleName, draft.GdsFactoryRoutingCrossSection);

        template.GdsFactoryRoutingCrossSection.ShouldBe("xs_nc");
    }

    [Fact]
    public void CornerStoneSin_IsADistinctProcessFromSoiPdks()
    {
        var loader = new PdkLoader();
        var entries = new[] { "demo-pdk.json", "siepic-ebeam-pdk.json", "cornerstone-sin-pdk.json" }
            .Select(f => loader.LoadFromFile(Path.Combine(PdkDir, f)))
            .Select(d => new PdkProcessEntry(d.Name, ProcessFingerprintFactory.From(d)))
            .ToList();

        var groups = ProcessCatalog.BuildGroups(entries);

        groups.Count.ShouldBe(2);
        groups.ShouldContain(g => g.MemberPdkNames.Contains("CornerStone SiN 300nm")
                                  && g.MemberPdkNames.Count == 1);
    }

    [Fact]
    public void SiepicEbeam_DeclaresRealLayerStackAndCrossSections()
    {
        var draft = new PdkLoader().LoadFromFile(Path.Combine(PdkDir, "siepic-ebeam-pdk.json"));
        var process = draft.Process!;

        process.Layers.ShouldNotBeEmpty();
        process.Layers.ShouldContain(l => l.Name == "WG" && l.Layer == 1);
        process.Layers.ShouldContain(l => l.Name == "M2_ROUTER");

        process.Xsections.ShouldNotBeEmpty();
        var strip = process.Xsections.Single(x => x.Name == "strip");
        strip.Kind.ShouldBe(XsectionKind.Optical);
        strip.WidthUm.ShouldBe(0.5);

        process.Xsections.ShouldContain(x => x.Kind == XsectionKind.Metal && x.WidthUm > 0);
    }

    [Fact]
    public void CornerStoneSin_DeclaresRealLayerStackAndCrossSections()
    {
        var draft = new PdkLoader().LoadFromFile(Path.Combine(PdkDir, "cornerstone-sin-pdk.json"));
        var process = draft.Process!;

        process.Layers.ShouldNotBeEmpty();
        process.Layers.ShouldContain(l => l.Name == "NITRIDE" && l.Layer == 203);

        process.Xsections.ShouldNotBeEmpty();
        var xsNc = process.Xsections.Single(x => x.Name == "xs_nc");
        xsNc.Kind.ShouldBe(XsectionKind.Optical);
        xsNc.WidthUm.ShouldBe(1.2);

        process.Xsections.ShouldContain(x => x.Kind == XsectionKind.Metal && x.WidthUm > 0);
    }
}
