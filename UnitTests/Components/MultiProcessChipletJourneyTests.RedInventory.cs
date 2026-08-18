using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Diagnostics;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Analysis;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Components;

/// <summary>
/// Inventory stations of the multi-process journey (#933) that started documented-red —
/// all green now, each with the issue that fixed its single-process assumption. See
/// <see cref="MultiProcessChipletJourneyTests"/> for the full journey description.
/// (Step 3 turned green with the per-chiplet placement scope, issue #935; step 4
/// with the per-connection DRC rule sets, issue #936; step 5 with the per-connection
/// bend-radius floors, issue #937 shipped in #948; step 7 with the persisted
/// per-chiplet process binding, issue #938; step 8 with the per-process GDS export
/// interconnects, issue #939.)
/// </summary>
public partial class MultiProcessChipletJourneyTests
{
    [Fact]
    public void Step4_DrcLite_ChecksEachChipletAgainstItsOwnProcess()
    {
        var design = MultiProcessChipletJourneyDesign.BuildComposed();

        // A deliberately narrow styled route inside chiplet A's process scope:
        // 0.2 µm < Cornerstone's 0.25 µm foundry minimum (#924).
        var narrow = design.Canvas.ConnectPinsWithCachedRoute(
            MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletA, "cs_coupler_o4"),
            MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletA, "cs_mmi_o3"),
            MultiProcessChipletJourneyDesign.StraightPath(
                MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletA, "cs_coupler_o4"),
                MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletA, "cs_mmi_o3")));
        narrow.ShouldNotBeNull();
        narrow!.Connection.WidthMicrometers = 0.2;

        // A two-process design can only exist in Playground (#935), so RunDesignChecks
        // runs with processLockActive = false — and still checks per chiplet (#936):
        // the per-connection provider keys each connection's rules to its own endpoint
        // PDKs, wired here exactly like MainViewModel.RunDesignChecks wires it.
        var drafts = new List<PdkDraft> { design.Cornerstone, design.Siepic };
        string? PdkSourceOf(CAP_Core.Components.Core.PhysicalPin? pin) =>
            pin?.ParentComponent is { } component
                ? ComponentPdkSourceResolver.Resolve(component, design.Templates)
                : null;
        var panel = new DesignValidationViewModel();
        panel.RunValidation(
            design.Canvas.ConnectionManager.Connections,
            allComponents: design.Canvas.Components.Select(vm => vm.Component),
            processLockActive: false,
            connectionDrcRuleProvider: connection =>
                ConnectionDrcRuleResolver.ResolveForEndpointPdkNames(
                    PdkSourceOf(connection.StartPin), PdkSourceOf(connection.EndPin), drafts));

        panel.Issues.ShouldContain(
            i => i.Type == DesignIssueType.WaveguideBelowMinWidth && ReferenceEquals(i.Connection, narrow.Connection),
            "chiplet A must be checked against Cornerstone's 0.25 µm minimum (#936)");
        panel.Issues.Count(i => i.Type == DesignIssueType.WaveguideBelowMinWidth
                && i.Description.Contains("si_")).ShouldBe(0,
            "chiplet B must stay silent: SiEPIC declares no minWidthUm — no invented values (#926)");
    }

    [Fact]
    public void Step5_BendRadiusFloor_FollowsEachChipletsProcess()
    {
        var design = MultiProcessChipletJourneyDesign.BuildComposed();

        // The #948 production wiring, mirrored exactly like MainViewModel wires
        // RoutingOrchestrator.BuildConnectionProcessFloorProvider (#937): pass-start
        // snapshots of the templates and drafts, endpoint PDK sources resolved through
        // the production resolver. RecalculateRoutesAsync pushes the built provider onto
        // the router before every pass; the Playground canvas-wide floor (10 µm fallback)
        // only governs connections the per-connection provider has no opinion on.
        var templates = design.Templates.ToList();
        var drafts = new List<PdkDraft> { design.Cornerstone, design.Siepic };
        string? PdkSourceOf(CAP_Core.Components.Core.PhysicalPin pin) =>
            pin.ParentComponent is { } component
                ? ComponentPdkSourceResolver.Resolve(component, templates)
                : null;
        design.Canvas.Routing.BuildConnectionProcessFloorProvider = () =>
            (startPin, endPin) => WaveguideBendRadiusResolver.ResolveForEndpointPdkNames(
                PdkSourceOf(startPin), PdkSourceOf(endPin), drafts);

        var router = design.Canvas.Router;
        router.ProcessMinBendRadiusMicrometers = WaveguideBendRadiusResolver.FallbackMinimumMicrometers;
        router.ConnectionProcessFloorProvider = design.Canvas.Routing.BuildConnectionProcessFloorProvider();

        // A route inside chiplet A keeps the Cornerstone 30 µm foundry floor instead of
        // dropping to the Playground fallback.
        router.ResolveProcessFloorFor(
                MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletA, "cs_coupler_o1"),
                MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletA, "cs_coupler_o2"))
            .ShouldBe(MultiProcessChipletJourneyDesign.CornerstoneMinBendRadiusUm,
                "a connection inside chiplet A must honor Cornerstone's 30 µm floor (#937/#948)");

        // The cross-chiplet abutment is governed by the stricter side: Cornerstone's
        // 30 µm wins over SiEPIC's 5 µm.
        router.ResolveProcessFloorFor(
                MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletA, "cs_mmi_o2"),
                MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletB, "si_ybranch_port 1"))
            .ShouldBe(MultiProcessChipletJourneyDesign.CornerstoneMinBendRadiusUm,
                "the stricter endpoint process governs a cross-chiplet connection (#937/#948)");

        // A route inside chiplet B gets SiEPIC's declared 5 µm — the looser chiplet is
        // neither over-constrained to 30 µm nor under-floored by the 10 µm fallback.
        router.ResolveProcessFloorFor(
                MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletB, "si_ybranch_port 3"),
                MultiProcessChipletJourneyDesign.ExposedPin(design.ChipletB, "si_taper_port 2"))
            .ShouldBe(SiepicMinBendRadiusUm,
                "a connection inside chiplet B resolves SiEPIC's own 5 µm minimum (#937/#948)");
    }

    [Fact]
    public void Step8_GdsExport_EachChipletRoutesOnItsOwnProcessStack()
    {
        var design = MultiProcessChipletJourneyDesign.BuildComposed();
        var script = new SimpleNazcaExporter().Export(design.Canvas);

        // One interconnect per process cross-section on the canvas: each chiplet's
        // frozen wires route on their own process' width/radius/layer — resolved from
        // the endpoint pins' PDK stamps, not from one global user preference (#939).
        script.ShouldContain("Interconnect(width=1.2, radius=10, layer=203)"); // chiplet A: Cornerstone NITRIDE (xs_nc, 1.2 µm)
        script.ShouldContain("Interconnect(width=0.5, radius=10, layer=1)");   // chiplet B: SiEPIC WG (strip, 0.5 µm)
        script.ShouldContain("nd.strt(length=5.00, width=1.2, layer=203)");    // chiplet A's coupler→MMI wire
        script.ShouldContain("nd.strt(length=5.01, width=0.5, layer=1)");      // chiplet B's Y-branch→taper wire
    }
}
