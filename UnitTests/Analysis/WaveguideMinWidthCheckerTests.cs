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
/// The DRC-lite min-width rule flags optical waveguide routes whose effective width
/// falls below the fabrication minimum (<c>minWidthUm</c>) of the associated
/// cross-section of the active process. Everything runs through production machinery
/// on the real bundled PDKs (pattern: <see cref="CornerstoneSinPreDrcTests"/>):
/// components are instantiated from the bundled PDK JSON so pins carry the
/// PDK-stamped width/layer the rule associates cross-sections with. A PDK that
/// declares no <c>minWidthUm</c> stays silent.
/// </summary>
public class WaveguideMinWidthCheckerTests
{
    private const string CornerstonePdkFile = "cornerstone-sin-pdk.json";
    private const string SiepicPdkFile = "siepic-ebeam-pdk.json";

    /// <summary>MPW-13 §5.4 Table 4: min. feature size on GDS 203 NITRIDE.</summary>
    private const double FoundryMinWidthUm = 0.25;
    /// <summary>xs_nc standard design width — what the PDK stamps on every optical pin.</summary>
    private const double CornerstoneStripWidthUm = 1.2;

    [Fact]
    public void Resolver_BuildsRulesFromDeclaredOpticalXsections_Only()
    {
        var process = LoadPdk(CornerstonePdkFile).Process!;

        var rules = process.GetMinWaveguideWidthRules();

        rules.Count.ShouldBe(2, "xs_nc and xs_no declare minWidthUm; the metal cross-sections do not");
        foreach (var rule in rules)
        {
            rule.MinWidthMicrometers.ShouldBe(FoundryMinWidthUm);
            rule.GdsLayers.ShouldBe(new[] { 203 }, "NITRIDE, resolved through the process layer stack");
            rule.DrcSource.ShouldNotBeNullOrWhiteSpace();
            rule.DrcSource.ShouldContain("MPW-13");
        }

        LoadPdk(SiepicPdkFile).Process.GetMinWaveguideWidthRules()
            .ShouldBeEmpty("SiEPIC declares no minWidthUm — no fallback values are invented");
        ((ProcessDefinition?)null).GetMinWaveguideWidthRules()
            .ShouldBeEmpty("no active process resolves to no rules");

        var metalOnly = new ProcessDefinition
        {
            Layers = new() { new ProcessLayer { Name = "M1", Layer = 11 } },
            Xsections = new()
            {
                new ProcessXsection
                {
                    Name = "metal_routing", Kind = XsectionKind.Metal,
                    WidthUm = 10, MinWidthUm = 2.0, Layers = new() { "M1" },
                },
            },
        };
        metalOnly.GetMinWaveguideWidthRules()
            .ShouldBeEmpty("min width is resolved from optical cross-sections only");

        var unknownLayer = new ProcessDefinition
        {
            Layers = new() { new ProcessLayer { Name = "WG", Layer = 1 } },
            Xsections = new()
            {
                new ProcessXsection
                {
                    Name = "strip", Kind = XsectionKind.Optical,
                    WidthUm = 0.5, MinWidthUm = 0.25, Layers = new() { "NOT_IN_STACK" },
                },
            },
        };
        unknownLayer.GetMinWaveguideWidthRules()
            .ShouldBeEmpty("a cross-section whose layers are unknown to the stack cannot be associated");
    }

    [Fact]
    public void NarrowRoute_FiresWithFoundryLimit_NamingWidthMinimumAndSource()
    {
        var pdk = LoadPdk(CornerstonePdkFile);
        var (components, connection) = BuildCornerstonePair(pdk, routeWidthUm: 0.2);

        var panel = RunPanel(pdk, components, connection);

        var findings = panel.Issues.Where(i => i.Type == DesignIssueType.WaveguideBelowMinWidth).ToList();
        findings.Count.ShouldBe(1, Describe(panel));
        findings[0].Connection.ShouldBe(connection);
        findings[0].Description.ShouldContain("0.20 µm");
        findings[0].Description.ShouldContain("0.25 µm");
        findings[0].Description.ShouldContain("MPW-13"); // the finding names the drcSource of the declared limit
    }

    [Fact]
    public void StandardWidthRoute_AndRouteExactlyAtMinimum_StayClean()
    {
        var pdk = LoadPdk(CornerstonePdkFile);

        var (standardComponents, standardConnection) = BuildCornerstonePair(pdk, routeWidthUm: CornerstoneStripWidthUm);
        RunPanel(pdk, standardComponents, standardConnection).Issues
            .Count(i => i.Type == DesignIssueType.WaveguideBelowMinWidth)
            .ShouldBe(0, "the 1.2 µm process standard width is far above the 250 nm floor");

        var (atMinComponents, atMinConnection) = BuildCornerstonePair(pdk, routeWidthUm: FoundryMinWidthUm);
        RunPanel(pdk, atMinComponents, atMinConnection).Issues
            .Count(i => i.Type == DesignIssueType.WaveguideBelowMinWidth)
            .ShouldBe(0, "a width exactly at the minimum is compliant");
    }

    [Fact]
    public void NarrowedEndpointPin_FiresOnPinEvidence()
    {
        var pdk = LoadPdk(CornerstonePdkFile);
        var (components, connection) = BuildCornerstonePair(pdk, routeWidthUm: CornerstoneStripWidthUm);
        connection.EndPin.WaveguideWidthMicrometers = 0.2;

        var panel = RunPanel(pdk, components, connection);

        var findings = panel.Issues.Where(i => i.Type == DesignIssueType.WaveguideBelowMinWidth).ToList();
        findings.Count.ShouldBe(1, Describe(panel));
        findings[0].Description.ShouldContain("0.20 µm");
        findings[0].Description.ShouldContain("0.25 µm");
    }

    [Fact]
    public void PdkWithoutMinWidth_AndMissingRules_StaySilent()
    {
        var siepic = LoadPdk(SiepicPdkFile);
        siepic.Process.GetMinWaveguideWidthRules().ShouldBeEmpty();

        var template = PdkTemplateConverter.ConvertToTemplate(
            siepic.Components.First(c => c.Name == "Y-Branch 1550"),
            siepic.Name, siepic.NazcaModuleName, process: siepic.Process);
        var branchA = ComponentTemplates.CreateFromTemplate(template, 0, 0);
        var branchB = ComponentTemplates.CreateFromTemplate(template, 200, 0);
        var connection = Link(
            branchA.PhysicalPins.First(p => p.Name == "port 2"),
            branchB.PhysicalPins.First(p => p.Name == "port 1"),
            routeWidthUm: 0.2);

        var panel = new DesignValidationViewModel();
        panel.RunValidation(
            new[] { connection },
            allComponents: new Component[] { branchA, branchB },
            minWaveguideWidthRules: siepic.Process.GetMinWaveguideWidthRules());
        panel.Issues.Count(i => i.Type == DesignIssueType.WaveguideBelowMinWidth)
            .ShouldBe(0, "a PDK without minWidthUm declares no limit — the rule must not fire");

        var cornerstone = LoadPdk(CornerstonePdkFile);
        var (components, narrowConnection) = BuildCornerstonePair(cornerstone, routeWidthUm: 0.2);
        var silentPanel = new DesignValidationViewModel();
        silentPanel.RunValidation(new[] { narrowConnection }, allComponents: components);
        silentPanel.Issues.Count(i => i.Type == DesignIssueType.WaveguideBelowMinWidth)
            .ShouldBe(0, "without the active process' rules there is nothing to check against (no guessing)");
    }

    private static DesignValidationViewModel RunPanel(
        PdkDraft pdk, List<Component> components, WaveguideConnection connection)
    {
        var panel = new DesignValidationViewModel();
        panel.RunValidation(
            new[] { connection },
            allComponents: components,
            minWaveguideWidthRules: pdk.Process.GetMinWaveguideWidthRules());
        return panel;
    }

    /// <summary>
    /// A Cornerstone coupler output routed into a straight waveguide — the same
    /// production-machinery pair <see cref="CornerstoneSinPreDrcTests"/> uses, with a
    /// hand-built straight route of the requested width standing in for a user-styled
    /// waveguide.
    /// </summary>
    private static (List<Component> Components, WaveguideConnection Connection)
        BuildCornerstonePair(PdkDraft pdk, double routeWidthUm)
    {
        var components = new List<Component>
        {
            Place(pdk, "Coupler", 0, 0),
            Place(pdk, "Straight", 200, 0),
        };
        var connection = Link(
            Pin(components[0], "o3"), Pin(components[1], "o1"), routeWidthUm);
        connection.StartPin.WaveguideWidthMicrometers.ShouldBe(CornerstoneStripWidthUm);
        connection.StartPin.Layer.ShouldBe(203, "NITRIDE — the layer the rule associates the xs_nc/xs_no minimum with");
        return (components, connection);
    }

    private static WaveguideConnection Link(PhysicalPin start, PhysicalPin end, double routeWidthUm)
    {
        var (x1, y1) = start.GetAbsolutePosition();
        var (x2, y2) = end.GetAbsolutePosition();
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(x1, y1, x2, y2, start.GetAbsoluteAngle()));
        var connection = new WaveguideConnection { StartPin = start, EndPin = end };
        connection.RestoreCachedPath(path);
        connection.WidthMicrometers = routeWidthUm;
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

    private static PdkDraft LoadPdk(string fileName) =>
        new PdkLoader().LoadFromFile(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PDKs", fileName));

    private static string Describe(DesignValidationViewModel panel) =>
        string.Join(" | ", panel.Issues.Select(i => $"{i.Type}: {i.Description}"));
}
