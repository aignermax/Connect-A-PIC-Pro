using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Routing;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace UnitTests.Analysis;

/// <summary>
/// Builds the deliberately broken / fixed / demo designs for the DRC-lite end-to-end
/// journey test (#915). Everything goes through production machinery: components are
/// instantiated from the bundled PDK JSONs (pins carry PDK-stamped width/layer, #913)
/// and the bend connection is routed by the real <see cref="WaveguideRouter"/> with a
/// process-derived bend-radius floor (no test-side property stuffing). Hand-built
/// straight paths stand in for user-styled routes — the only way a spacing violation
/// can exist, since the auto-router enforces spacing by construction.
/// </summary>
internal sealed class DrcLiteJourneyDesign
{
    private const string SiepicPdkFile = "siepic-ebeam-pdk.json";
    private const string CornerstonePdkFile = "cornerstone-sin-pdk.json";
    private const string DemoPdkFile = "demo-pdk.json";

    private DrcLiteJourneyDesign(
        List<Component> components,
        List<WaveguideConnection> connections,
        Dictionary<Component, string?> pdkSourceByComponent,
        IReadOnlyCollection<string> enabledPdkNames,
        double minWaveguideSpacingMicrometers)
    {
        Components = components;
        Connections = connections;
        PdkSourceByComponent = pdkSourceByComponent;
        EnabledPdkNames = enabledPdkNames;
        MinWaveguideSpacingMicrometers = minWaveguideSpacingMicrometers;
    }

    public List<Component> Components { get; }
    public List<WaveguideConnection> Connections { get; }
    public Dictionary<Component, string?> PdkSourceByComponent { get; }
    public IReadOnlyCollection<string> EnabledPdkNames { get; }
    public double MinWaveguideSpacingMicrometers { get; }

    /// <summary>The one deliberately dangling pin (Cornerstone coupler o2), for attribution assertions.</summary>
    public PhysicalPin? DanglingPin { get; private set; }
    /// <summary>The cross-PDK connection (SiEPIC Y-branch → Cornerstone coupler), expected to mismatch ×2.</summary>
    public WaveguideConnection? MismatchConnection { get; private set; }
    /// <summary>First route of the too-close parallel pair (edge-to-edge 1.0 µm &lt; 2.0 µm minimum).</summary>
    public WaveguideConnection? SpacingConnectionA { get; private set; }
    /// <summary>Second route of the too-close parallel pair.</summary>
    public WaveguideConnection? SpacingConnectionB { get; private set; }
    /// <summary>The router-produced connection whose bends fall below the Cornerstone process floor (30 µm).</summary>
    public WaveguideConnection? BendConnection { get; private set; }

    /// <summary>
    /// Broken design: one dangling pin, one cross-PDK connection (width 0.5 vs 1.2 µm,
    /// layer 1 vs 203), two parallel styled routes 1.0 µm apart edge-to-edge, and one
    /// tight route the Cornerstone bend floor (30 µm) cannot honor. All other pins are
    /// connected so exactly one UnconnectedPin finding is expected.
    /// </summary>
    public static DrcLiteJourneyDesign BuildBroken()
    {
        var siepic = LoadPdk(SiepicPdkFile);
        var cornerstone = LoadPdk(CornerstonePdkFile);
        var design = new DrcLiteJourneyDesign(
            new List<Component>(), new List<WaveguideConnection>(), new Dictionary<Component, string?>(),
            new[] { siepic.Name, cornerstone.Name },
            siepic.Process.GetMinWaveguideSpacingMicrometersOrDefault());

        // Core cluster: grating coupler → Y-branch; one output crosses PDKs into the
        // Cornerstone coupler (mismatch), the coupler's o2 input is left dangling. The
        // coupler's remaining pins feed a Cornerstone-only Straight loop — keeping the
        // downstream chain single-PDK is what keeps the mismatch count at exactly ×2.
        var gc = design.Place(siepic, "Grating Coupler TE 1550", 0, 0);
        var yb = design.Place(siepic, "Y-Branch 1550", 60, 10.2);
        var cs1 = design.Place(cornerstone, "Coupler", 200, 0);
        var stA = design.Place(cornerstone, "Straight", 400, 0);
        var stB = design.Place(cornerstone, "Straight", 400, 4);
        var term1 = design.Place(siepic, "Terminator TE 1550", 90, 30);

        design.Link(Pin(gc, "port 2"), Pin(yb, "port 1"));
        design.MismatchConnection = design.Link(Pin(yb, "port 2"), Pin(cs1, "o1"));
        design.DanglingPin = Pin(cs1, "o2");
        design.Link(Pin(yb, "port 3"), Pin(term1, "port 1"));
        design.Link(Pin(cs1, "o3"), Pin(stA, "o1"));
        design.Link(Pin(cs1, "o4"), Pin(stB, "o1"));
        design.Link(Pin(stA, "o2"), Pin(stB, "o2"));

        // Spacing pair: two parallel styled straights with 1.0 µm edge-to-edge clearance.
        var dwA = design.Place(siepic, "Disconnected Waveguide TE 1550", 39.1, 119.25);
        var dwB = design.Place(siepic, "Disconnected Waveguide TE 1550", 239.1, 119.25);
        var dwC = design.Place(siepic, "Disconnected Waveguide TE 1550", 39.1, 120.75);
        var dwD = design.Place(siepic, "Disconnected Waveguide TE 1550", 239.1, 120.75);
        design.SpacingConnectionA = design.Link(Pin(dwA, "port 1"), Pin(dwB, "port 1"));
        design.SpacingConnectionB = design.Link(Pin(dwC, "port 1"), Pin(dwD, "port 1"));

        AddBendCluster(design, siepic, cornerstone, rightX: 350);
        return design;
    }

    /// <summary>
    /// The same design repaired: single-PDK core (SiEPIC only, so the former mismatch
    /// connection now matches), every pin connected, the parallel pair respaced to
    /// 19.5 µm edge-to-edge, and the tight route given enough room for 30 µm bends.
    /// </summary>
    public static DrcLiteJourneyDesign BuildFixed()
    {
        var siepic = LoadPdk(SiepicPdkFile);
        var design = new DrcLiteJourneyDesign(
            new List<Component>(), new List<WaveguideConnection>(), new Dictionary<Component, string?>(),
            new[] { siepic.Name },
            siepic.Process.GetMinWaveguideSpacingMicrometersOrDefault());

        var gc = design.Place(siepic, "Grating Coupler TE 1550", 0, 0);
        var yb = design.Place(siepic, "Y-Branch 1550", 60, 10.2);
        var dc = design.Place(siepic, "Directional Coupler TE 1550", 200, 0);
        var taper = design.Place(siepic, "Taper TE 1550", 400, 0);
        var termA = design.Place(siepic, "Terminator TE 1550", 470, 3.5);
        var termB = design.Place(siepic, "Terminator TE 1550", 300, 0);

        design.Link(Pin(gc, "port 2"), Pin(yb, "port 1"));
        design.Link(Pin(yb, "port 2"), Pin(dc, "port 1"));
        design.Link(Pin(yb, "port 3"), Pin(dc, "port 2"));
        design.Link(Pin(dc, "port 3"), Pin(taper, "port 1"));
        design.Link(Pin(taper, "port 2"), Pin(termA, "port 1"));
        design.Link(Pin(dc, "port 4"), Pin(termB, "port 1"));

        var dwA = design.Place(siepic, "Disconnected Waveguide TE 1550", 39.1, 119.25);
        var dwB = design.Place(siepic, "Disconnected Waveguide TE 1550", 239.1, 119.25);
        var dwC = design.Place(siepic, "Disconnected Waveguide TE 1550", 39.1, 139.25);
        var dwD = design.Place(siepic, "Disconnected Waveguide TE 1550", 239.1, 139.25);
        design.Link(Pin(dwA, "port 1"), Pin(dwB, "port 1"));
        design.Link(Pin(dwC, "port 1"), Pin(dwD, "port 1"));

        AddBendCluster(design, siepic, cornerstone: null, rightX: 700);
        return design;
    }

    /// <summary>
    /// Playground/demo-process analogue of the cross-PDK connection: the demo PDK
    /// declares no optical cross-section, so its pins carry no width/layer and the
    /// pin-mismatch rule must stay silent (#913 null-guard) while PDK-independent
    /// rules (dangling pins) still fire.
    /// </summary>
    public static DrcLiteJourneyDesign BuildDemoPlayground()
    {
        var demo = LoadPdk(DemoPdkFile);
        var design = new DrcLiteJourneyDesign(
            new List<Component>(), new List<WaveguideConnection>(), new Dictionary<Component, string?>(),
            new[] { demo.Name },
            minWaveguideSpacingMicrometers: 0);

        var yj = design.Place(demo, "Y-Junction", 0, 0);
        var sw = design.Place(demo, "Straight Waveguide 100µm", 200, 22.5);
        design.Link(Pin(yj, "out1"), Pin(sw, "a0"));
        return design;
    }

    /// <summary>
    /// Adds the tight-neighbor bend pair plus its fully-connected feeders. The bend
    /// connection itself is routed by the real router under the Cornerstone process
    /// floor (resolved through production code); at rightX = 350 the 40 µm diagonal
    /// gap cannot fit 30 µm bends (violation flagged), at 700 µm there is room.
    /// </summary>
    private static void AddBendCluster(DrcLiteJourneyDesign design, PdkDraft siepic, PdkDraft? cornerstone, double rightX)
    {
        var left = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        left.PhysicalX = 60;
        left.PhysicalY = 300;
        var right = TestComponentFactory.CreateStraightWaveGuideWithPhysicalPins();
        right.PhysicalX = rightX;
        right.PhysicalY = 340;
        var gc2 = design.Place(siepic, "Grating Coupler TE 1550", 0, 411.3);
        var term5 = design.Place(siepic, "Terminator TE 1550", rightX + 270, 462.5);

        design.Components.Add(left);
        design.Components.Add(right);
        design.PdkSourceByComponent[left] = null;
        design.PdkSourceByComponent[right] = null;

        var floor = cornerstone is null
            ? 0
            : WaveguideBendRadiusResolver.Resolve(new ProcessDefinition?[] { cornerstone.Process });
        design.BendConnection = RouteWithProcessFloor(Pin(left, "out"), Pin(right, "in"), left, right, floor);
        design.Connections.Add(design.BendConnection);
        design.Link(Pin(gc2, "port 2"), Pin(left, "in"));
        design.Link(Pin(right, "out"), Pin(term5, "port 1"));
    }

    private static WaveguideConnection RouteWithProcessFloor(
        PhysicalPin start, PhysicalPin end, Component left, Component right, double processFloor)
    {
        var router = new WaveguideRouter { ProcessMinBendRadiusMicrometers = processFloor };
        router.InitializePathfindingGrid(-100, -100, 1100, 900, new[] { left, right });
        var manager = new WaveguideConnectionManager(router);
        var connection = new WaveguideConnection { StartPin = start, EndPin = end };
        manager.AddExistingConnection(connection);
        manager.RecalculateAllTransmissions();
        return connection;
    }

    private static PdkDraft LoadPdk(string fileName)
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PDKs", fileName);
        return new PdkLoader().LoadFromFile(path);
    }

    private Component Place(PdkDraft pdk, string templateName, double x, double y)
    {
        var template = PdkTemplateConverter.ConvertToTemplate(
            pdk.Components.First(c => c.Name == templateName), pdk.Name, pdk.NazcaModuleName, process: pdk.Process);
        var component = ComponentTemplates.CreateFromTemplate(template, x, y);
        Components.Add(component);
        PdkSourceByComponent[component] = pdk.Name;
        return component;
    }

    private WaveguideConnection Link(PhysicalPin start, PhysicalPin end)
    {
        var (x1, y1) = start.GetAbsolutePosition();
        var (x2, y2) = end.GetAbsolutePosition();
        var path = new RoutedPath();
        path.Segments.Add(new StraightSegment(x1, y1, x2, y2, start.GetAbsoluteAngle()));
        var connection = new WaveguideConnection { StartPin = start, EndPin = end };
        connection.RestoreCachedPath(path);
        Connections.Add(connection);
        return connection;
    }

    private static PhysicalPin Pin(Component component, string name) =>
        component.PhysicalPins.First(p => p.Name == name);
}
