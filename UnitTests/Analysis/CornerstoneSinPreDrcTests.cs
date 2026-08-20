using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Diagnostics;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Analysis;
using CAP_Core.Components.Core;
using CAP_Core.Components.Connections;
using CAP_Core.Routing;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis;

/// <summary>
/// Issue #920: the bundled CornerStone SiN 300nm PDK carries the foundry's public pre-DRC
/// limits and the existing DRC-lite rules fire with those limits instead of the generic
/// defaults. The numbers come from the public CORNERSTONE documents in the cspdk repo
/// (references/CORNERSTONE_Pre_DRC.pdf + the SiN 300nm MPW-13 Design Guidelines §5.4
/// Table 4 it defers to): 250 nm minimum gap and 250 nm minimum feature width on the
/// waveguide layer (GDS 203 NITRIDE); the 30 µm bend floor is cspdk.sin300's own tech
/// constant (<c>radius_min</c>). Everything runs through production machinery on the real
/// bundled PDK (pattern: <see cref="DrcLiteEndToEndJourneyTests"/>).
/// </summary>
public class CornerstoneSinPreDrcTests
{
    private const string CornerstonePdkFile = "cornerstone-sin-pdk.json";

    /// <summary>MPW-13 §5.4 Table 4: min. gap on GDS 203 NITRIDE (what the pre-DRC script flags).</summary>
    private const double FoundryMinSpacingUm = 0.25;
    /// <summary>MPW-13 §5.4 Table 4: min. feature size on GDS 203 NITRIDE.</summary>
    private const double FoundryMinWidthUm = 0.25;
    /// <summary>cspdk.sin300 tech constant (radius_nc/radius_no, applied as radius_min).</summary>
    private const double FoundryMinBendRadiusUm = 30.0;
    /// <summary>xs_nc standard design width — what DRC-lite stamps on every optical pin.</summary>
    private const double CornerstoneStripWidthUm = 1.2;

    [Fact]
    public void BundledPdk_DeclaresFoundryPreDrcValues_WithSources()
    {
        var process = LoadCornerstone().Process!;

        process.MinWaveguideSpacingUm.ShouldBe(FoundryMinSpacingUm);
        process.DrcSource.ShouldNotBeNullOrWhiteSpace();
        process.DrcSource.ShouldContain("MPW-13");
        process.DrcSource.ShouldContain("250 nm");

        var optical = process.Xsections.Where(x => x.Kind == XsectionKind.Optical).ToList();
        optical.Count.ShouldBe(2, "xs_nc and xs_no are the optical cross-sections");
        foreach (var xsection in optical)
        {
            xsection.MinWidthUm.ShouldBe(FoundryMinWidthUm);
            xsection.MinRadiusUm.ShouldBe(FoundryMinBendRadiusUm);
            xsection.DrcSource.ShouldNotBeNullOrWhiteSpace();
            xsection.DrcSource.ShouldContain("MPW-13");
        }
        process.Xsections.Where(x => x.Kind == XsectionKind.Metal)
            .ShouldAllBe(x => x.MinWidthUm == null, "min width is an optical DRC concept here");

        // The resolvers DRC-lite consumes return the foundry values, not the generic defaults.
        process.GetMinWaveguideSpacingMicrometersOrDefault().ShouldBe(FoundryMinSpacingUm);
        WaveguideBendRadiusResolver.Resolve(new ProcessDefinition?[] { process })
            .ShouldBe(FoundryMinBendRadiusUm);
    }

    [Fact]
    public void TooCloseSpacing_FiresWithFoundryLimit_CompliantPairStaysClean()
    {
        var pdk = LoadCornerstone();
        double minSpacing = pdk.Process.GetMinWaveguideSpacingMicrometersOrDefault();
        minSpacing.ShouldBe(FoundryMinSpacingUm);

        // Two parallel 1.2 µm strip routes, 1.3 µm centerline pitch → 0.10 µm edge-to-edge:
        // below the foundry 250 nm gap.
        var broken = BuildParallelPair(pdk, pitchUm: 1.3);
        var panel = new DesignValidationViewModel();
        panel.RunValidation(
            broken.Connections,
            allComponents: broken.Components,
            minWaveguideSpacingMicrometers: minSpacing);

        var spacing = panel.Issues.Where(i => i.Type == DesignIssueType.WaveguideSpacingViolation).ToList();
        spacing.Count.ShouldBe(1, Describe(panel));
        spacing[0].Description.ShouldContain("too close");
        spacing[0].Description.ShouldContain("distance 0.10");
        spacing[0].Description.ShouldContain("minimum 0.25");

        // Same pair respaced to 2.5 µm pitch → 1.3 µm edge: above the foundry floor, so DRC-lite
        // stays silent — under the former generic 2.0 µm default this would have fired.
        var compliant = BuildParallelPair(pdk, pitchUm: 2.5);
        var compliantPanel = new DesignValidationViewModel();
        compliantPanel.RunValidation(
            compliant.Connections,
            allComponents: compliant.Components,
            minWaveguideSpacingMicrometers: minSpacing);

        compliantPanel.Issues.Where(i => i.Type == DesignIssueType.WaveguideSpacingViolation)
            .ShouldBeEmpty(Describe(compliantPanel));
    }

    [Fact]
    public void TooNarrowWaveguide_IsCaughtByWidthMismatch_AgainstProcessStandardWidth()
    {
        var pdk = LoadCornerstone();
        var coupler = Place(pdk, "Coupler", 0, 0);
        var straight = Place(pdk, "Straight", 200, 0);

        var standardPin = Pin(coupler, "o3");
        standardPin.WaveguideWidthMicrometers.ShouldBe(CornerstoneStripWidthUm);
        standardPin.Layer.ShouldBe(203, "NITRIDE, resolved through the process layer stack");

        // A user-narrowed 0.2 µm waveguide pin: below the foundry 250 nm min. feature it
        // cannot butt-join the 1.2 µm process standard — DRC-lite reports the width step.
        var narrowPin = Pin(straight, "o1");
        narrowPin.WaveguideWidthMicrometers = 0.2;
        narrowPin.WaveguideWidthMicrometers!.Value.ShouldBeLessThan(FoundryMinWidthUm);

        var connection = new WaveguideConnection { StartPin = standardPin, EndPin = narrowPin };
        var panel = new DesignValidationViewModel();
        panel.RunValidation(
            new[] { connection },
            allComponents: new Component[] { coupler, straight },
            minWaveguideSpacingMicrometers: pdk.Process.GetMinWaveguideSpacingMicrometersOrDefault());

        var mismatches = panel.Issues.Where(i => i.Type == DesignIssueType.PinMismatch).ToList();
        mismatches.Count.ShouldBe(1, Describe(panel));
        mismatches[0].Description.ShouldContain("width mismatch");
        mismatches[0].Description.ShouldContain("0.2");
        mismatches[0].Description.ShouldContain("1.2");
    }

    private static (List<Component> Components, List<WaveguideConnection> Connections)
        BuildParallelPair(PdkDraft pdk, double pitchUm)
    {
        var components = new List<Component>
        {
            Place(pdk, "Straight", 0, 0),
            Place(pdk, "Straight", 210, 0),
            Place(pdk, "Straight", 0, pitchUm),
            Place(pdk, "Straight", 210, pitchUm),
        };
        var connections = new List<WaveguideConnection>
        {
            Link(Pin(components[0], "o2"), Pin(components[1], "o1")),
            Link(Pin(components[2], "o2"), Pin(components[3], "o1")),
        };
        return (components, connections);
    }

    /// <summary>
    /// Hand-built straight route standing in for a user-styled waveguide (the auto-router
    /// enforces spacing by construction, so a violation can only exist on a styled route).
    /// Carries the Cornerstone strip width so the detector measures true edge-to-edge gap.
    /// </summary>
    private static WaveguideConnection Link(PhysicalPin start, PhysicalPin end)
    {
        var (x1, y1) = start.GetAbsolutePosition();
        var (x2, y2) = end.GetAbsolutePosition();
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(x1, y1, x2, y2, start.GetAbsoluteAngle()));
        var connection = new WaveguideConnection { StartPin = start, EndPin = end };
        connection.RestoreCachedPath(path);
        connection.WidthMicrometers = CornerstoneStripWidthUm;
        return connection;
    }

    private static Component Place(PdkDraft pdk, string templateName, double x, double y)
    {
        var template = PdkTemplateConverter.ConvertToTemplate(
            pdk.Components.First(c => c.Name == templateName), pdk.Name, pdk.NazcaModuleName, process: pdk.Process);
        return ComponentTemplates.CreateFromTemplate(template, x, y);
    }

    private static PhysicalPin Pin(Component component, string name) =>
        component.PhysicalPins.First(p => p.Name == name);

    private static PdkDraft LoadCornerstone() =>
        new PdkLoader().LoadFromFile(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PDKs", CornerstonePdkFile));

    private static string Describe(DesignValidationViewModel panel) =>
        string.Join(" | ", panel.Issues.Select(i => $"{i.Type}: {i.Description}"));
}
