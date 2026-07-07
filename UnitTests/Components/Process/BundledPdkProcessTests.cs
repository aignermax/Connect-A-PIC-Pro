using System.IO;
using System.Linq;
using CAP_Core.Components.Process;
using CAP_DataAccess.Components.ComponentDraftMapper;
using Shouldly;
using Xunit;

namespace UnitTests.Components.Process;

/// <summary>
/// Pins that the bundled demo and SiEPIC EBeam PDKs declare a complete process
/// fingerprint (issue #570) and that, since both are 220nm SOI, they collapse
/// into a single process group.
/// </summary>
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
        // The gdsfactory-native SiN PDK (generated from cspdk) must load via the main path
        // despite having no nazcaFunction / nazcaOriginOffset (issue #570).
        var draft = new PdkLoader().LoadFromFile(Path.Combine(PdkDir, "cornerstone-sin-pdk.json"));

        draft.IsGdsFactoryBackend.ShouldBeTrue();
        draft.Components.ShouldNotBeEmpty();
        draft.Components.ShouldAllBe(c => !string.IsNullOrEmpty(c.GdsFactoryFunction));
        ProcessFingerprintFactory.From(draft).IsSpecified.ShouldBeTrue();
    }

    [Fact]
    public void CornerStoneSin_Mmi1x2_HasRealMultiWavelengthSMatrix_AndStraightPassesThrough()
    {
        // The SiN components carry real cspdk sax models (mmi) / lossless pass-through
        // (passives), not an all-zero (perfect-absorber) black-box (#570 review finding #1).
        var draft = new PdkLoader().LoadFromFile(Path.Combine(PdkDir, "cornerstone-sin-pdk.json"));

        var mmi = draft.Components.Single(c => c.GdsFactoryFunction == "cspdk.sin300.mmi1x2");
        mmi.SMatrix.ShouldNotBeNull();
        mmi.SMatrix!.WavelengthData.ShouldNotBeNull();
        mmi.SMatrix.WavelengthData!.Count.ShouldBeGreaterThan(1);            // multi-wavelength
        var at1550 = mmi.SMatrix.WavelengthData!.Single(w => w.WavelengthNm == 1550);
        at1550.Connections.ShouldNotBeEmpty();
        at1550.Connections.ShouldAllBe(c => c.Magnitude > 0.1);             // real split, not absorbed

        var straight = draft.Components.Single(c => c.GdsFactoryFunction == "cspdk.sin300.straight");
        straight.SMatrix!.Connections.ShouldContain(c => c.Magnitude == 1.0);  // lossless pass-through
    }

    [Fact]
    public void CornerStoneSin_GratingCoupler_HasWavelengthPeakedCouplingBand()
    {
        // Grating couplers carry the real cspdk sax model: a wavelength-dependent coupling band
        // that peaks near 1550 nm and rolls off at the edges — not a flat/black-box response (#665).
        var draft = new PdkLoader().LoadFromFile(Path.Combine(PdkDir, "cornerstone-sin-pdk.json"));
        var gc = draft.Components.Single(c => c.GdsFactoryFunction == "cspdk.sin300.grating_coupler_rectangular");

        gc.SMatrix.ShouldNotBeNull();
        var wl = gc.SMatrix!.WavelengthData!;
        double MagAt(int nm) => wl.Single(w => w.WavelengthNm == nm).Connections[0].Magnitude;
        MagAt(1550).ShouldBeGreaterThan(MagAt(1500));   // peak in-band
        MagAt(1550).ShouldBeGreaterThan(MagAt(1600));   // rolls off at the edges
    }

    [Fact]
    public void CornerStoneSin_RoutingCrossSection_FlowsFromPdkToComponentTemplate()
    {
        // The PDK declares its routing cross-section (xs_nc); it must reach the component
        // template so routed waveguides in a placed design use a cross-section that exists
        // under the activated cspdk PDK — the generic 'strip' does not (#570 field-test fix).
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

        groups.Count.ShouldBe(2);   // SOI (demo+siepic) + SiN (cornerstone)
        groups.ShouldContain(g => g.MemberPdkNames.Contains("CornerStone SiN 300nm")
                                  && g.MemberPdkNames.Count == 1);
    }
}
