using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Diagnostics;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Analysis;
using CAP_Core.Components.Core;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Creation;
using CAP_Core.Routing;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis;

/// <summary>
/// Per-connection DRC keying (issue #936) end-to-end through the validator and the
/// detectors: each connection's width/spacing limits come from its OWN endpoint pins'
/// PDKs — wired exactly like <c>MainViewModel.RunDesignChecks</c> wires the
/// <c>connectionDrcRuleProvider</c> — not from one design-wide rule set taken from the
/// active process' first member PDK. Complements the resolver-focused
/// <see cref="ConnectionDrcRuleResolverTests"/> and journey Step 4 in
/// Components/MultiProcessChipletJourneyTests.
/// </summary>
public class PerConnectionDrcValidationTests
{
    private const string CornerstonePdkFile = "cornerstone-sin-pdk.json";
    private const string SiepicPdkFile = "siepic-ebeam-pdk.json";
    private const int CornerstoneGdsLayer = 203;
    private const int SyntheticWgLayer = 1;

    [Fact]
    public void Playground_MixedCanvas_ChecksEachConnectionAgainstItsOwnPdk()
    {
        var cornerstone = LoadPdk(CornerstonePdkFile);
        var siepic = LoadPdk(SiepicPdkFile);
        var templates = new List<ComponentTemplate>
        {
            TemplateFor(cornerstone, "Coupler"),
            TemplateFor(cornerstone, "Straight"),
            TemplateFor(siepic, "Y-Branch 1550"),
        };
        var drafts = new PdkDraft[] { cornerstone, siepic };

        var coupler = Place(templates[0], 0, 0);
        var straight = Place(templates[1], 200, 0);
        var cornerstoneLink = Link(Pin(coupler, "o3"), Pin(straight, "o1"), routeWidthUm: 0.2);

        // Far away so the spacing detector sees no cross-pair interplay.
        var branchA = Place(templates[2], 0, 2000);
        var branchB = Place(templates[2], 200, 2000);
        var siepicLink = Link(Pin(branchA, "port 2"), Pin(branchB, "port 1"), routeWidthUm: 0.2);

        // A two-process canvas only exists in Playground (#935): no lock, no
        // canvas-wide rules — before #936 nothing was checked at all here.
        var panel = new DesignValidationViewModel();
        panel.RunValidation(
            new[] { cornerstoneLink, siepicLink },
            allComponents: new Component[] { coupler, straight, branchA, branchB },
            processLockActive: false,
            connectionDrcRuleProvider: Provider(templates, drafts));

        var findings = panel.Issues.Where(i => i.Type == DesignIssueType.WaveguideBelowMinWidth).ToList();
        findings.Count.ShouldBe(1, Describe(panel));
        findings[0].Connection.ShouldBe(cornerstoneLink,
            "the 0.2 µm Cornerstone route violates its own process' declared 0.25 µm minimum");
        findings[0].Description.ShouldContain("0.20 µm");
        findings[0].Description.ShouldContain("0.25 µm");
    }

    [Fact]
    public void CrossPdkConnection_FlaggedByTheDeclaringSide()
    {
        var cornerstone = LoadPdk(CornerstonePdkFile);
        var siepic = LoadPdk(SiepicPdkFile);
        var templates = new List<ComponentTemplate>
        {
            TemplateFor(cornerstone, "Straight"),
            TemplateFor(siepic, "Y-Branch 1550"),
        };
        var drafts = new PdkDraft[] { cornerstone, siepic };

        var straight = Place(templates[0], 0, 0);
        var branch = Place(templates[1], 200, 0);
        var crossLink = Link(Pin(straight, "o1"), Pin(branch, "port 1"), routeWidthUm: 0.2);
        crossLink.StartPin.Layer.ShouldBe(CornerstoneGdsLayer,
            "the Cornerstone pin carries the NITRIDE layer its process' rules cover");

        var panel = new DesignValidationViewModel();
        panel.RunValidation(
            new[] { crossLink },
            allComponents: new Component[] { straight, branch },
            processLockActive: false,
            connectionDrcRuleProvider: Provider(templates, drafts));

        var findings = panel.Issues.Where(i => i.Type == DesignIssueType.WaveguideBelowMinWidth).ToList();
        findings.Count.ShouldBe(1, Describe(panel));
        findings[0].Connection.ShouldBe(crossLink,
            "the Cornerstone endpoint declares 0.25 µm and covers its pin layer — SiEPIC's silence must not disarm the check");
    }

    [Fact]
    public void LockedCanvas_ConnectionNotCheckedAgainstForeignProcessLimits()
    {
        var processA = SyntheticProcess("ProcessA", minWidthUm: 0.5);
        var processB = SyntheticProcess("ProcessB", minWidthUm: 0.25);
        var drafts = new PdkDraft[]
        {
            new() { Name = "PDK-A", Process = processA },
            new() { Name = "PDK-B", Process = processB },
        };

        var connA = ConnectionOnSyntheticLayer(y: 0, widthUm: 0.3);
        var connB = ConnectionOnSyntheticLayer(y: 2000, widthUm: 0.3);
        var pdkByConnection = new Dictionary<WaveguideConnection, string>
        {
            [connA] = "PDK-A",
            [connB] = "PDK-B",
        };
        var canvasWideRules = processA.GetMinWaveguideWidthRules();
        canvasWideRules.Count.ShouldBe(1, "one optical cross-section declares one minimum");

        // Legacy behavior without the provider: every connection is checked against
        // the first member PDK's rules — connB is a false positive (the #936 finding).
        var legacyPanel = new DesignValidationViewModel();
        legacyPanel.RunValidation(
            new[] { connA, connB },
            minWaveguideWidthRules: canvasWideRules);
        legacyPanel.Issues.Count(i => i.Type == DesignIssueType.WaveguideBelowMinWidth)
            .ShouldBe(2, "canvas-wide keying checks both connections against process A's 0.5 µm minimum");

        var panel = new DesignValidationViewModel();
        panel.RunValidation(
            new[] { connA, connB },
            minWaveguideWidthRules: canvasWideRules,
            connectionDrcRuleProvider: connection =>
                ConnectionDrcRuleResolver.ResolveForEndpointPdkNames(
                    pdkByConnection[connection], pdkByConnection[connection], drafts));

        var findings = panel.Issues.Where(i => i.Type == DesignIssueType.WaveguideBelowMinWidth).ToList();
        findings.Count.ShouldBe(1, Describe(panel));
        findings[0].Connection.ShouldBe(connA,
            "0.3 µm violates process A's 0.5 µm minimum but respects process B's 0.25 µm — each connection answers to its own process");
    }

    [Fact]
    public void UnresolvableConnection_FallsBackToCanvasWideRules()
    {
        var cornerstone = LoadPdk(CornerstonePdkFile);
        var templates = new List<ComponentTemplate>
        {
            TemplateFor(cornerstone, "Coupler"),
            TemplateFor(cornerstone, "Straight"),
        };
        var coupler = Place(templates[0], 0, 0);
        var straight = Place(templates[1], 200, 0);
        var link = Link(Pin(coupler, "o3"), Pin(straight, "o1"), routeWidthUm: 0.2);

        var panel = new DesignValidationViewModel();
        panel.RunValidation(
            new[] { link },
            allComponents: new Component[] { coupler, straight },
            minWaveguideWidthRules: cornerstone.Process.GetMinWaveguideWidthRules(),
            connectionDrcRuleProvider: _ => null);

        var findings = panel.Issues.Where(i => i.Type == DesignIssueType.WaveguideBelowMinWidth).ToList();
        findings.Count.ShouldBe(1, Describe(panel));
        findings[0].Connection.ShouldBe(link,
            "no per-connection opinion (e.g. built-in components) keeps the canvas-wide rules — the #937 fallback pattern");
    }

    [Fact]
    public void Spacing_StricterSideGovernsThePair()
    {
        var detector = new WaveguideSpacingDetector();
        var connA = ClosePairMember(y: 0);
        var connB = ClosePairMember(y: 1.1); // edge-to-edge 0.1 µm at width 1.0

        var oneSideDeclares = detector.DetectViolations(
            new[] { connA, connB },
            Array.Empty<ComponentGroup>(),
            minWaveguideSpacingMicrometers: 0,
            spacingForConnection: c => c == connA ? 0.25 : 0);
        oneSideDeclares.Count.ShouldBe(1, "edge 0.1 µm violates the one declared 0.25 µm limit");
        oneSideDeclares[0].Description.ShouldContain("minimum 0.25 µm");

        var bothDeclare = detector.DetectViolations(
            new[] { connA, connB },
            Array.Empty<ComponentGroup>(),
            minWaveguideSpacingMicrometers: 0,
            spacingForConnection: c => c == connA ? 0.25 : 0.6);
        bothDeclare.Count.ShouldBe(1);
        bothDeclare[0].Description.Contains("minimum 0.60 µm").ShouldBeTrue(
            "the stricter of the two endpoint processes governs the pair");

        detector.DetectViolations(
            new[] { connA, connB },
            Array.Empty<ComponentGroup>(),
            minWaveguideSpacingMicrometers: 0,
            spacingForConnection: _ => 0.05)
            .ShouldBeEmpty("edge 0.1 µm respects a declared 0.05 µm limit — declared-looser stays silent too");
    }

    [Fact]
    public void Spacing_NoDeclaredLimitOnEitherSide_StaysSilent()
    {
        var detector = new WaveguideSpacingDetector();
        var connA = ClosePairMember(y: 0);
        var connB = ClosePairMember(y: 1.1);

        detector.DetectViolations(
            new[] { connA, connB },
            Array.Empty<ComponentGroup>(),
            minWaveguideSpacingMicrometers: 0,
            spacingForConnection: _ => 0)
            .ShouldBeEmpty("neither endpoint process declares a spacing — no invented values (#926)");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>The production wiring: resolve each endpoint pin's PDK name via the template library.</summary>
    private static Func<WaveguideConnection, ConnectionDrcRules?> Provider(
        IReadOnlyList<ComponentTemplate> templates, IReadOnlyList<PdkDraft> drafts)
    {
        string? PdkSourceOf(PhysicalPin? pin) =>
            pin?.ParentComponent is { } component
                ? ComponentPdkSourceResolver.Resolve(component, templates)
                : null;
        return connection => ConnectionDrcRuleResolver.ResolveForEndpointPdkNames(
            PdkSourceOf(connection.StartPin), PdkSourceOf(connection.EndPin), drafts);
    }

    private static WaveguideConnection ConnectionOnSyntheticLayer(double y, double widthUm)
    {
        var connection = WaveguideSpacingDetectorTestHelpers.CreateConnectionWithSegment(0, y, 100, y);
        connection.WidthMicrometers = widthUm;
        connection.StartPin.Layer = SyntheticWgLayer;
        connection.EndPin.Layer = SyntheticWgLayer;
        return connection;
    }

    private static WaveguideConnection ClosePairMember(double y)
    {
        var connection = WaveguideSpacingDetectorTestHelpers.CreateConnectionWithSegment(0, y, 100, y);
        connection.WidthMicrometers = 1.0;
        return connection;
    }

    private static ProcessDefinition SyntheticProcess(string name, double minWidthUm) =>
        new()
        {
            Name = name,
            Layers = new() { new ProcessLayer { Name = "WG", Layer = SyntheticWgLayer } },
            Xsections = new()
            {
                new ProcessXsection
                {
                    Name = "strip",
                    Kind = XsectionKind.Optical,
                    WidthUm = 1.0,
                    MinWidthUm = minWidthUm,
                    Layers = new() { "WG" },
                },
            },
        };

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

    private static ComponentTemplate TemplateFor(PdkDraft pdk, string componentName) =>
        PdkTemplateConverter.ConvertToTemplate(
            pdk.Components.First(c => c.Name == componentName),
            pdk.Name, pdk.NazcaModuleName, process: pdk.Process);

    private static Component Place(ComponentTemplate template, double x, double y) =>
        ComponentTemplates.CreateFromTemplate(template, x, y);

    private static PhysicalPin Pin(Component component, string name) =>
        component.PhysicalPins.First(p => p.Name == name);

    private static PdkDraft LoadPdk(string fileName) =>
        new PdkLoader().LoadFromFile(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PDKs", fileName));

    private static string Describe(DesignValidationViewModel panel) =>
        string.Join(" | ", panel.Issues.Select(i => $"{i.Type}: {i.Description}"));
}
