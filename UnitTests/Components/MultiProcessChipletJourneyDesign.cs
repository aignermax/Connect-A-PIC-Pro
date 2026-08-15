using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;

namespace UnitTests.Components;

/// <summary>
/// Builds the two-chiplet composition for the multi-process journey (#933):
/// chiplet A ("Splitter Chiplet") from the bundled CornerStone SiN 300nm PDK
/// (Coupler → Mmi1x2), chiplet B ("Receiver Chiplet") from the bundled SiEPIC
/// EBeam PDK (Y-Branch → Taper), each grouped on one shared canvas and composed
/// pin-to-pin (cs_mmi.o2 abuts si_ybranch.port 1, standing in for a later
/// edge-coupler pair). Everything goes through production machinery: the real
/// bundled PDK JSONs, real template conversion (pins carry PDK-stamped
/// width/layer, #913), real grouping and the real router abutment (#923) —
/// the same headless recipe as the single-process composition journey (#929,
/// <see cref="MultiChipletCompositionJourneyTests"/>), now across two
/// fabrication processes.
/// </summary>
internal sealed class MultiProcessChipletJourneyDesign
{
    public const string CornerstonePdkFile = "cornerstone-sin-pdk.json";
    public const string SiepicPdkFile = "siepic-ebeam-pdk.json";

    public const string ChipletAName = "Cornerstone Splitter Chiplet";
    public const string ChipletBName = "SiEPIC Receiver Chiplet";

    // S-matrix magnitudes at 1550 nm, straight from the bundled PDK JSONs.
    public const double CouplerThrough = 0.683101;   // Cornerstone Coupler o1→o3
    public const double MmiThrough = 0.683101;       // Cornerstone Mmi1x2 o1→o2
    public const double TaperThrough = 0.991384;     // SiEPIC Taper port1→port2

    /// <summary>Cornerstone xs_nc: MPW-13 §5.4 Table 4 min. feature size (#924/#926).</summary>
    public const double CornerstoneMinWidthUm = 0.25;
    /// <summary>Cornerstone xs_nc: cspdk.sin300 radius_min tech constant (#924).</summary>
    public const double CornerstoneMinBendRadiusUm = 30.0;
    /// <summary>Cornerstone NITRIDE waveguide layer.</summary>
    public const int CornerstoneGdsLayer = 203;
    /// <summary>SiEPIC WG waveguide layer.</summary>
    public const int SiepicGdsLayer = 1;

    /// <summary>Amplitude leaving chiplet A through the abutted arm (exact product, lossless wires).</summary>
    public const double ExpectedBoundaryAmplitude = CouplerThrough * MmiThrough;
    /// <summary>
    /// Amplitude at chiplet B's output: boundary × Y-branch in→arm × taper. The draft
    /// lists both directions per pair (0.69361/0.698147) and the converter mirrors them,
    /// so the expectation is the midpoint of both readings; the tolerance covers the
    /// spread plus the iterative solver's convergence noise.
    /// </summary>
    public const double ExpectedOutputAmplitude = 0.3219;
    /// <summary>Amplitude leaving the Y-branch's free arm (same reasoning as the output).</summary>
    public const double ExpectedFreeArmAmplitude = 0.3247;
    public const double SolverValueTolerance = 5e-3;

    private const int WavelengthNm = 1550;
    private const double WireGapMicrometers = 5;

    public static readonly string[] ChipletAPinNames =
        { "cs_coupler_o1", "cs_coupler_o2", "cs_coupler_o4", "cs_mmi_o2", "cs_mmi_o3" };
    public static readonly string[] ChipletBPinNames =
        { "si_ybranch_port 1", "si_ybranch_port 3", "si_taper_port 2" };

    private MultiProcessChipletJourneyDesign(
        DesignCanvasViewModel canvas,
        ComponentGroup chipletA,
        ComponentGroup chipletB,
        List<ComponentTemplate> templates,
        PdkDraft cornerstone,
        PdkDraft siepic)
    {
        Canvas = canvas;
        ChipletA = chipletA;
        ChipletB = chipletB;
        Templates = templates;
        Cornerstone = cornerstone;
        Siepic = siepic;
    }

    public DesignCanvasViewModel Canvas { get; }
    public ComponentGroup ChipletA { get; }
    public ComponentGroup ChipletB { get; }
    public List<ComponentTemplate> Templates { get; }
    public PdkDraft Cornerstone { get; }
    public PdkDraft Siepic { get; }

    /// <summary>Loads one bundled PDK JSON from the test output's PDKs folder.</summary>
    public static PdkDraft LoadPdk(string fileName)
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PDKs", fileName);
        return new PdkLoader().LoadFromFile(path);
    }

    /// <summary>Converts one component of the draft through the production PDK→template path.</summary>
    public static ComponentTemplate TemplateFor(PdkDraft pdk, string componentName) =>
        PdkTemplateConverter.ConvertToTemplate(
            pdk.Components.First(c => c.Name == componentName),
            pdk.Name,
            pdk.NazcaModuleName,
            pdk.GdsFactoryRoutingCrossSection,
            process: pdk.Process);

    /// <summary>The connectable canvas-side pin behind a group's exposed pin.</summary>
    public static PhysicalPin ExposedPin(ComponentGroup group, string pinName) =>
        group.ExternalPins.Single(p => p.Name == pinName).InternalPin!;

    /// <summary>
    /// Builds the full composition: both chiplets grouped, chiplet B aligned so its
    /// Y-branch input sits exactly on chiplet A's MMI output, and the coincident pin
    /// pair routed through the real router (#923 abutment).
    /// </summary>
    public static MultiProcessChipletJourneyDesign BuildComposed()
    {
        var cornerstone = LoadPdk(CornerstonePdkFile);
        var siepic = LoadPdk(SiepicPdkFile);
        var couplerTemplate = TemplateFor(cornerstone, "Coupler");
        var mmiTemplate = TemplateFor(cornerstone, "Mmi1x2");
        var yBranchTemplate = TemplateFor(siepic, "Y-Branch 1550");
        var taperTemplate = TemplateFor(siepic, "Taper TE 1550");

        var canvas = new DesignCanvasViewModel();

        // Chiplet A: Coupler at the origin, MMI with its input facing the coupler's
        // o3 output across a 5 µm gap (Coupler o3: (60, 0.6); MMI o1: (0, 6) rel).
        var coupler = Place(canvas, "cs_coupler", couplerTemplate, 0, 0);
        var mmi = Place(canvas, "cs_mmi", mmiTemplate, 65, 0.6 - 6);
        Wire(canvas, Pin(coupler, "o3"), Pin(mmi, "o1"));

        // Chiplet B, far away: Y-branch at (1000, 0), taper input facing port 2
        // (port 2: (14.9, 0.75) rel; Taper port 1: (0.01, 6) rel).
        var yBranch = Place(canvas, "si_ybranch", yBranchTemplate, 1000, 0);
        var taper = Place(canvas, "si_taper", taperTemplate, 1014.9 + WireGapMicrometers, 0.75 - 6);
        Wire(canvas, Pin(yBranch, "port 2"), Pin(taper, "port 1"));

        var chipletA = Group(canvas, ChipletAName, coupler, mmi);
        var chipletB = Group(canvas, ChipletBName, yBranch, taper);

        // Align chiplet B so its Y-branch input coincides with chiplet A's MMI output.
        var aOut = ExposedPin(chipletA, "cs_mmi_o2");
        var bIn = ExposedPin(chipletB, "si_ybranch_port 1");
        var (ax, ay) = aOut.GetAbsolutePosition();
        var (bx, by) = bIn.GetAbsolutePosition();
        chipletB.MoveGroup(ax - bx, ay - by);

        // #923 on group level, across the process boundary: coincident opposing pins.
        var path = canvas.Router.Route(aOut, bIn);
        path.IsBlockedFallback.ShouldBeFalse("the cross-process abutment must not fall back to a blocked route");
        path.IsValid.ShouldBeTrue("the cross-process abutment must be a valid route");
        var abutment = canvas.ConnectPinsWithCachedRoute(aOut, bIn, path);
        abutment.ShouldNotBeNull("the cross-process abutment must be created");
        abutment!.Connection.IsRouteFrozen = true;

        return new MultiProcessChipletJourneyDesign(
            canvas,
            chipletA,
            chipletB,
            new List<ComponentTemplate> { couplerTemplate, mmiTemplate, yBranchTemplate, taperTemplate },
            cornerstone,
            siepic);
    }

    private static Component Place(
        DesignCanvasViewModel canvas, string identifier, ComponentTemplate template, double x, double y)
    {
        var component = ComponentTemplates.CreateFromTemplate(template, x, y);
        component.Identifier = identifier;
        canvas.AddComponent(component, template.Name, template.PdkSource);
        return component;
    }

    /// <summary>Groups the given components (Ctrl+G equivalent) and returns the group.</summary>
    private static ComponentGroup Group(DesignCanvasViewModel canvas, string name, params Component[] children)
    {
        var command = new CreateGroupCommand(
            canvas,
            children.Select(c => canvas.Components.Single(vm => vm.Component == c)).ToList(),
            name);
        command.Execute();
        return command.CreatedGroup.ShouldNotBeNull($"grouping '{name}' must succeed");
    }

    /// <summary>
    /// Builds an explicit straight route between two pins — the deterministic geometry
    /// recipe the journey uses wherever a connection must not depend on router heuristics.
    /// </summary>
    public static RoutedPath StraightPath(PhysicalPin from, PhysicalPin to)
    {
        var (x1, y1) = from.GetAbsolutePosition();
        var (x2, y2) = to.GetAbsolutePosition();
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(x1, y1, x2, y2, 0));
        return path;
    }

    /// <summary>
    /// Connects two pins inside one chiplet with an explicit straight route, frozen so
    /// the group captures the deterministic geometry (same recipe as #929).
    /// </summary>
    private static void Wire(DesignCanvasViewModel canvas, PhysicalPin from, PhysicalPin to)
    {
        var connection = canvas.ConnectPinsWithCachedRoute(from, to, StraightPath(from, to));
        connection.ShouldNotBeNull($"route {from.Name} -> {to.Name} must be created");
        connection!.Connection.IsRouteFrozen = true;
    }

    private static PhysicalPin Pin(Component component, string pinName) =>
        component.PhysicalPins.Single(p => p.Name == pinName);
}
