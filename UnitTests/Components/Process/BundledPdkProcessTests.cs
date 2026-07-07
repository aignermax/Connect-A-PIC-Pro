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
        // despite having no nazcaFunction (issue #570).
        var draft = new PdkLoader().LoadFromFile(Path.Combine(PdkDir, "cornerstone-sin-pdk.json"));

        draft.IsGdsFactoryBackend.ShouldBeTrue();
        draft.Components.ShouldNotBeEmpty();
        draft.Components.ShouldAllBe(c => !string.IsNullOrEmpty(c.GdsFactoryFunction));
        ProcessFingerprintFactory.From(draft).IsSpecified.ShouldBeTrue();
    }

    [Fact]
    public void CornerStoneSin_EveryComponent_HasPlacementOffsets()
    {
        // gdsfactory cells are port-anchored (origin at o1), not bbox-corner-anchored, so the PDK
        // must carry nazcaOriginOffset for every component or placement lands cells off their
        // routed waveguides. An earlier generated JSON shipped with zero offsets (#570 field test).
        var draft = new PdkLoader().LoadFromFile(Path.Combine(PdkDir, "cornerstone-sin-pdk.json"));

        draft.Components.ShouldAllBe(c => c.NazcaOriginOffsetX != null && c.NazcaOriginOffsetY != null);
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

    [Theory]
    [InlineData("cspdk.sin300.grating_coupler_rectangular")]
    [InlineData("cspdk.sin300.grating_coupler_elliptical")]
    [InlineData("cspdk.sin300.coupler")]
    [InlineData("cspdk.sin300.mzi")]
    public void CornerStoneSin_GratingsCouplerAndMzi_CarryRealMultiWavelengthSMatrices(
        string gdsFactoryFunction)
    {
        // Follow-up to the mmi/passive models: gratings, the 2x2 directional coupler and the
        // MZI must carry real (non-black-box) S-matrices from cspdk's sax models (#665).
        var draft = new PdkLoader().LoadFromFile(Path.Combine(PdkDir, "cornerstone-sin-pdk.json"));

        var comp = draft.Components.Single(c => c.GdsFactoryFunction == gdsFactoryFunction);
        comp.SMatrix.ShouldNotBeNull();
        comp.SMatrix!.WavelengthData.ShouldNotBeNull();
        comp.SMatrix.WavelengthData!.Count.ShouldBeGreaterThan(1);           // multi-wavelength
        var at1550 = comp.SMatrix.WavelengthData.Single(w => w.WavelengthNm == 1550);
        at1550.Connections.ShouldNotBeEmpty();
        at1550.Connections.ShouldAllBe(c => c.Magnitude > 0 && c.Magnitude <= 1);
    }

    [Fact]
    public void CornerStoneSin_GratingCoupler_CouplesWaveguideToFibre_PeakedAt1550()
    {
        // Fibre-port convention (#665): o1 is the in-plane waveguide port, o2 the
        // out-of-plane fibre port; the coupling band must peak at the 1550 nm design
        // wavelength and roll off towards the band edges (Gaussian coupling band).
        var draft = new PdkLoader().LoadFromFile(Path.Combine(PdkDir, "cornerstone-sin-pdk.json"));
        var gc = draft.Components.Single(
            c => c.GdsFactoryFunction == "cspdk.sin300.grating_coupler_rectangular");

        double At(int wl) => gc.SMatrix!.WavelengthData!
            .Single(w => w.WavelengthNm == wl).Connections
            .Single(c => c.FromPin == "o1" && c.ToPin == "o2").Magnitude;

        At(1550).ShouldBeGreaterThan(At(1500) * 2);   // wavelength-selective, not flat
        At(1550).ShouldBeGreaterThan(At(1600) * 2);
    }

    [Fact]
    public void CornerStoneSin_DirectionalCoupler_SplitsBothInputsAcrossBothOutputs()
    {
        var draft = new PdkLoader().LoadFromFile(Path.Combine(PdkDir, "cornerstone-sin-pdk.json"));
        var coupler = draft.Components.Single(c => c.GdsFactoryFunction == "cspdk.sin300.coupler");

        var at1550 = coupler.SMatrix!.WavelengthData!.Single(w => w.WavelengthNm == 1550);
        // 2x2: forward transfers o1/o2 → o3/o4, all four carrying a real ~50/50 split.
        at1550.Connections.Count.ShouldBe(4);
        at1550.Connections.ShouldAllBe(c => c.Magnitude > 0.5 && c.Magnitude < 0.8);
    }

    [Fact]
    public void CornerStoneSin_Mzi_ShowsInterferenceFringesAcrossTheBand()
    {
        // The composed netlist model (splitter/combiner + unequal arms) must produce
        // wavelength-dependent interference, not a flat transfer.
        var draft = new PdkLoader().LoadFromFile(Path.Combine(PdkDir, "cornerstone-sin-pdk.json"));
        var mzi = draft.Components.Single(c => c.GdsFactoryFunction == "cspdk.sin300.mzi");

        var barMagnitudes = mzi.SMatrix!.WavelengthData!
            .Select(w => w.Connections.Single(c => c.FromPin == "o1" && c.ToPin == "o2").Magnitude)
            .ToList();

        barMagnitudes.Max().ShouldBeGreaterThan(0.8);   // near-constructive somewhere in band
        barMagnitudes.Min().ShouldBeLessThan(0.1);      // near-destructive somewhere in band
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
