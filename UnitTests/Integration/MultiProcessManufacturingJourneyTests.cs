using CAP.Avalonia.ViewModels.Diagnostics;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Analysis;
using CAP_Core.Components.Core;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using UnitTests.Components;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Hard end-to-end scenario of the multi-process chain as ONE journey (issue #1010,
/// rung 6→7): two chiplets bound to two different fabrication processes on one canvas,
/// walked through per-chiplet DRC, the .lun round-trip, per-process GDS export and the
/// vendored CORNERSTONE pre-DRC deck. Every station has unit tests elsewhere
/// (#935/#936/#937/#938/#939); this class is the proof the whole road walks as one
/// journey — built once in <see cref="MultiProcessManufacturingJourneyFixture"/>, each
/// fact asserting one numbered step with step-named failure messages.
///
///   Step 1: both chiplets carry a process binding, derived through the same placement
///           policy code path the UI uses (#935/#938).
///   Step 2: each chiplet holds its small circuit; one cross-process link joins them.
///   Step 3: Design Validation checks each chiplet against its OWN rule set (#936) —
///           a width legal under SiEPIC is flagged under Cornerstone; the both-legal
///           configuration is clean.
///   Step 4: save → load keeps the process bindings and the geometry (#938).
///   Step 5: the GDS export routes each chiplet on its own process cross-section (#939).
///   Step 6 (gated): the executed export carries that geometry at the expected
///           coordinates (nazca-gated like the existing round-trip suites — CI runs it,
///           local suites skip it).
///   Step 7 (gated): the vendored CORNERSTONE SiN pre-DRC deck (#932) over the exported
///           GDS is pinned clean for the SiN chiplet's geometry.
/// </summary>
public partial class MultiProcessManufacturingJourneyTests
    : IClassFixture<MultiProcessManufacturingJourneyFixture>
{
    private const double PositionTolerance = 1e-9;

    private readonly MultiProcessManufacturingJourneyFixture _journey;

    /// <summary>Attaches the shared journey fixture.</summary>
    public MultiProcessManufacturingJourneyTests(MultiProcessManufacturingJourneyFixture journey) =>
        _journey = journey;

    [Fact]
    public void Step1_BindChiplets_EachChipletCarriesItsOwnProcess()
    {
        var bindingA = _journey.Design.ChipletA.ProcessBinding;
        bindingA.ShouldNotBeNull("Step 1: chiplet A must be bound to a fabrication process (#938)");
        bindingA!.IsPlayground.ShouldBeFalse(
            "Step 1: a bound chiplet is a first-class manufacturable state, not Playground");
        bindingA.MemberPdkNames.ShouldContain(_journey.Design.Cornerstone.Name,
            "Step 1: chiplet A fabricates in the Cornerstone SiN process");

        var bindingB = _journey.Design.ChipletB.ProcessBinding;
        bindingB.ShouldNotBeNull("Step 1: chiplet B must be bound to a fabrication process (#938)");
        bindingB!.IsPlayground.ShouldBeFalse(
            "Step 1: a bound chiplet is a first-class manufacturable state, not Playground");
        bindingB.MemberPdkNames.ShouldContain(_journey.Design.Siepic.Name,
            "Step 1: chiplet B fabricates in the SiEPIC EBeam process");
    }

    [Fact]
    public void Step2_PlaceAndConnect_SmallCircuitPerChiplet_PlusInterChipletLink()
    {
        var design = _journey.Design;
        design.ChipletA.ChildComponents.Count.ShouldBe(2, "Step 2: chiplet A owns coupler + MMI");
        design.ChipletA.InternalPaths.Count.ShouldBe(1, "Step 2: the coupler→MMI wire freezes into chiplet A");
        design.ChipletB.ChildComponents.Count.ShouldBe(2, "Step 2: chiplet B owns Y-branch + taper");
        design.ChipletB.InternalPaths.Count.ShouldBe(1, "Step 2: the Y-branch→taper wire freezes into chiplet B");

        design.Canvas.ConnectionManager.Connections.Count.ShouldBe(1,
            "Step 2: exactly the one inter-chiplet link exists at canvas level");
        var aOut = MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletA, "cs_mmi_o2");
        var bIn = MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletB, "si_ybranch_port 1");
        var (ax, ay) = aOut.GetAbsolutePosition();
        var (bx, by) = bIn.GetAbsolutePosition();
        bx.ShouldBe(ax, PositionTolerance, "Step 2: the edge-coupler path starts at a coincident pin pair (X)");
        by.ShouldBe(ay, PositionTolerance, "Step 2: the edge-coupler path starts at a coincident pin pair (Y)");

        aOut.WaveguideWidthMicrometers.ShouldBe(1.2, "Step 2: Cornerstone xs_nc width stamps chiplet A's pins");
        aOut.Layer.ShouldBe(MultiProcessChipletJourneyDesign.CornerstoneGdsLayer,
            "Step 2: Cornerstone NITRIDE layer stamps chiplet A's pins");
        bIn.WaveguideWidthMicrometers.ShouldBe(0.5, "Step 2: SiEPIC strip width stamps chiplet B's pins");
        bIn.Layer.ShouldBe(MultiProcessChipletJourneyDesign.SiepicGdsLayer,
            "Step 2: SiEPIC WG layer stamps chiplet B's pins");
    }

    [Fact]
    public void Step3_DesignValidation_EachChipletCheckedAgainstItsOwnRuleSet()
    {
        // (a) The journey design as built is the both-legal configuration: no
        // per-process rule fires. The only findings are the two documented pin
        // mismatches at the SiN↔SOI boundary — a real butt joint has a width/layer
        // step a physical edge coupler absorbs; that is honest physics, not dirt.
        var panel = RunValidation(_journey.Design.Canvas, _journey.Design.Templates, WithJourneyPdks(_journey.Design));
        var abutment = _journey.Design.Canvas.ConnectionManager.Connections.Single();
        panel.Issues.Count(i => i.Type is DesignIssueType.WaveguideBelowMinWidth
                or DesignIssueType.WaveguideSpacingViolation
                or DesignIssueType.BendRadiusBelowProcessMinimum)
            .ShouldBe(0, "Step 3: in the both-legal configuration no per-chiplet rule set fires");
        panel.Issues.ShouldAllBe(
            i => i.Type == DesignIssueType.PinMismatch && ReferenceEquals(i.Connection, abutment),
            "Step 3: the only remaining findings are the documented boundary pin mismatches");
        panel.Issues.Count.ShouldBe(2,
            "Step 3: one width + one layer mismatch at the inter-chiplet link, nothing else");

        // (b) Violation probe on a fresh copy (mutating the shared journey design would
        // contaminate the later steps): the SAME 0.2 µm route is flagged inside
        // chiplet A (Cornerstone's declared 0.25 µm minimum, #924) and legal inside
        // chiplet B (SiEPIC declares no minWidthUm — no invented values, #926).
        var probe = MultiProcessChipletJourneyDesign.BuildComposed();
        var narrowInA = AddStyledRoute(probe, probe.ChipletA, "cs_coupler_o4", "cs_mmi_o3");
        var narrowInB = AddStyledRoute(probe, probe.ChipletB, "si_ybranch_port 3", "si_taper_port 2");
        var probePanel = RunValidation(probe.Canvas, probe.Templates, WithJourneyPdks(probe));

        probePanel.Issues.ShouldContain(
            i => i.Type == DesignIssueType.WaveguideBelowMinWidth && ReferenceEquals(i.Connection, narrowInA),
            "Step 3: the 0.2 µm route inside chiplet A must trip Cornerstone's 0.25 µm rule (#936)");
        probePanel.Issues.ShouldNotContain(
            i => i.Type == DesignIssueType.WaveguideBelowMinWidth && ReferenceEquals(i.Connection, narrowInB),
            "Step 3: the identical route inside chiplet B stays legal under SiEPIC's rule set (#936)");
        probePanel.Issues.Where(i => i.Type == DesignIssueType.WaveguideBelowMinWidth)
            .ShouldAllBe(i => i.Description.Contains("0.25"),
                "Step 3: the flagged minimum is Cornerstone's declared value, not a global one");
    }

    [Fact]
    public void Step4_SaveLoad_ProcessBindingsAndGeometrySurvive()
    {
        _journey.SavedFileText.ShouldContain("\"ProcessBinding\"", Case.Sensitive,
            "Step 4: each chiplet's process binding must be written into the .lun (#938)");
        _journey.MigrationWarning.ShouldBeNull(
            "Step 4: the persisted bindings describe the design completely — no Playground migration (#938)");

        var loadedA = _journey.LoadedChipletA.ProcessBinding;
        loadedA.ShouldNotBeNull("Step 4: chiplet A's process binding survives the round-trip (#938)");
        loadedA!.MemberPdkNames.ShouldContain(_journey.Design.Cornerstone.Name,
            "Step 4: chiplet A reloads bound to the Cornerstone SiN process");
        var loadedB = _journey.LoadedChipletB.ProcessBinding;
        loadedB.ShouldNotBeNull("Step 4: chiplet B's process binding survives the round-trip (#938)");
        loadedB!.MemberPdkNames.ShouldContain(_journey.Design.Siepic.Name,
            "Step 4: chiplet B reloads bound to the SiEPIC EBeam process");

        _journey.LoadedCanvas.Connections.Count.ShouldBe(1,
            "Step 4: the inter-chiplet link survives the round-trip");
        foreach (var (pinName, (x, y)) in _journey.PinPositionsBeforeSave)
        {
            var loadedChiplet = _journey.LoadedChipletA.ExternalPins.Any(p => p.Name == pinName)
                ? _journey.LoadedChipletA
                : _journey.LoadedChipletB;
            var (loadedX, loadedY) = MultiProcessChipletJourneyDesign.ExposedPin(loadedChiplet, pinName)
                .GetAbsolutePosition();
            loadedX.ShouldBe(x, PositionTolerance, $"Step 4: pin '{pinName}' keeps its X position");
            loadedY.ShouldBe(y, PositionTolerance, $"Step 4: pin '{pinName}' keeps its Y position");
        }
    }

    [Fact]
    public void Step5_GdsExport_EachChipletRoutesOnItsOwnProcessCrossSection()
    {
        // Headless half of the export proof (#939/#960): one interconnect per process
        // cross-section, resolved from the endpoint pins' PDK stamps — never one global
        // user preference. Step 6 pins the same widths/layers in the executed GDS.
        var script = _journey.NazcaScript;
        script.ShouldContain("Interconnect(width=1.2, radius=10, layer=203)", Case.Sensitive,
            "Step 5: chiplet A routes on the Cornerstone NITRIDE cross-section (xs_nc, 1.2 µm)");
        script.ShouldContain("Interconnect(width=0.5, radius=10, layer=1)", Case.Sensitive,
            "Step 5: chiplet B routes on the SiEPIC WG cross-section (strip, 0.5 µm)");
        script.ShouldContain("nd.strt(length=5.00, width=1.2, layer=203)", Case.Sensitive,
            "Step 5: chiplet A's coupler→MMI wire carries the Cornerstone width/layer");
        script.ShouldContain("nd.strt(length=5.01, width=0.5, layer=1)", Case.Sensitive,
            "Step 5: chiplet B's Y-branch→taper wire carries the SiEPIC width/layer");
    }

    /// <summary>Adds a deliberately narrow (0.2 µm) styled route between two exposed chiplet pins.</summary>
    private static CAP_Core.Components.Connections.WaveguideConnection AddStyledRoute(
        MultiProcessChipletJourneyDesign design, ComponentGroup chiplet, string fromPin, string toPin)
    {
        var from = MultiProcessChipletJourneyDesign.ExposedPin(chiplet, fromPin);
        var to = MultiProcessChipletJourneyDesign.ExposedPin(chiplet, toPin);
        var route = design.Canvas.ConnectPinsWithCachedRoute(
            from, to, MultiProcessChipletJourneyDesign.StraightPath(from, to));
        route.ShouldNotBeNull($"the probe route {fromPin} -> {toPin} must be created");
        route!.Connection.WidthMicrometers = 0.2;
        return route.Connection;
    }

    /// <summary>
    /// Runs Design Validation wired exactly like <c>MainViewModel.RunDesignChecks</c>
    /// (#936): the per-connection provider keys every connection's rules to its own
    /// endpoint PDKs. Both chiplets' exposed pins are designated external ports — the
    /// dangling-pin check (#908) targets forgotten pins, not the chiplet interfaces.
    /// </summary>
    private static DesignValidationViewModel RunValidation(
        CAP.Avalonia.ViewModels.Canvas.DesignCanvasViewModel canvas,
        List<ComponentTemplate> templates,
        List<PdkDraft> drafts)
    {
        string? PdkSourceOf(PhysicalPin? pin) =>
            pin?.ParentComponent is { } component
                ? ComponentPdkSourceResolver.Resolve(component, templates)
                : null;
        var externalPortPins = canvas.Components
            .SelectMany(vm => vm.Component is ComponentGroup group
                ? group.ExternalPins
                : Enumerable.Empty<GroupPin>())
            .Select(pin => pin.InternalPin!)
            .ToList();
        var panel = new DesignValidationViewModel();
        panel.RunValidation(
            canvas.ConnectionManager.Connections,
            allComponents: canvas.Components.Select(vm => vm.Component),
            processLockActive: false,
            externalPortPins: externalPortPins,
            connectionDrcRuleProvider: connection =>
                ConnectionDrcRuleResolver.ResolveForEndpointPdkNames(
                    PdkSourceOf(connection.StartPin), PdkSourceOf(connection.EndPin), drafts));
        return panel;
    }

    /// <summary>The journey design's two bundled PDK drafts, in catalog order.</summary>
    private static List<PdkDraft> WithJourneyPdks(MultiProcessChipletJourneyDesign design) =>
        new() { design.Cornerstone, design.Siepic };
}
