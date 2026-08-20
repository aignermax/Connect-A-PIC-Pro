using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components;
using CAP_Core.Components.Core;
using CAP_Core.Export;
using UnitTests.Routing;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Shared journey fixture for <see cref="Issue704GdsExportHonestyTests"/>: rebuilds
/// the tight neighbouring-port repro geometry (<c>overlappingwaveguides.lun</c>,
/// constants from <see cref="Routing.Issue704ReproRoutingTests"/>) on a real
/// canvas, routes it with the same router configuration the #704 repro tests use,
/// exports the nazca script once and remembers the routed connections' pin
/// endpoints and component footprints in exported coordinates — so the geometry
/// checker can blame offenders by name instead of asserting raw pluralities.
/// </summary>
public sealed class Issue704GdsExportHonestyJourneyFixture : IAsyncLifetime
{
    /// <summary>Temp working directory for the GDS export.</summary>
    public string WorkDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "issue704-gds-honesty-" + Guid.NewGuid().ToString("N"));

    /// <summary>The canvas the repro layout lives on.</summary>
    public DesignCanvasViewModel Canvas { get; private set; } = null!;

    /// <summary>The nazca export script of the repro layout.</summary>
    public string NazcaScript { get; private set; } = null!;

    /// <summary>The routed connections, mapped to pin endpoints in exported coordinates.</summary>
    public List<ExportedWaveguideOverlapAnalyzer.Connection> Connections { get; private set; } = null!;

    /// <summary>Footprints of every placed component, in exported coordinates.</summary>
    public List<ExportedWaveguideOverlapAnalyzer.BoundingBox> ComponentFootprints { get; private set; } = null!;

    /// <summary>Builds the layout, routes it, exports the script.</summary>
    public async Task InitializeAsync()
    {
        var mzi8 = Issue704ReproCircuit.CreateMzi("MZI_8", 374.34455820950575, 218.3565233418277);
        var mzi9 = Issue704ReproCircuit.CreateMzi("MZI_9", 236.5708507637589, 649.767215289101);
        var taper = Issue704ReproCircuit.CreateTaper("Taper_5",
            Issue704ReproCircuit.TaperPinX, Issue704ReproCircuit.TaperPinY);

        Canvas = new DesignCanvasViewModel();
        Canvas.AddComponent(mzi8);
        Canvas.AddComponent(mzi9);
        Canvas.AddComponent(taper);

        Canvas.ConnectPins(
            Issue704ReproCircuit.Pin(mzi8, "o3"), Issue704ReproCircuit.Pin(mzi9, "o3"));
        Canvas.ConnectPins(
            Issue704ReproCircuit.Pin(taper, "o1"), Issue704ReproCircuit.Pin(mzi9, "o2"));

        // Same bounds as the #704 route-level repro tests (the design fits inside).
        Canvas.InitializeAStarRouting(0, 0, 1200, 1000);
        await Canvas.RecalculateRoutesAsync();

        Connections = Canvas.Connections
            .Select(vm => {
                var conn = vm.Connection;
                var description = ExportableConnections.Describe(conn.StartPin, conn.EndPin);
                return new ExportedWaveguideOverlapAnalyzer.Connection(
                    description,
                    ToEndpoint(conn.StartPin, description),
                    ToEndpoint(conn.EndPin, description));
            })
            .ToList();

        ComponentFootprints = Canvas.Components
            .Select(form => Footprint(form.Component))
            .ToList();

        NazcaScript = new SimpleNazcaExporter().Export(Canvas);
    }

    /// <summary>Removes the temp working directory.</summary>
    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(WorkDirectory)) Directory.Delete(WorkDirectory, recursive: true);
        }
        catch
        {
            // temp cleanup is best effort
        }
        return Task.CompletedTask;
    }

    /// <summary>Maps one pin to its expected exported endpoint coordinates.</summary>
    private static ExportedWaveguideOverlapAnalyzer.Endpoint ToEndpoint(PhysicalPin pin, string connectionName)
    {
        var (x, y) = NazcaCoordinateMapper.GetPinNazcaPosition(pin);
        var pinName = $"{pin.ParentComponent?.Identifier ?? "?"}.{pin.Name}";
        return new ExportedWaveguideOverlapAnalyzer.Endpoint(connectionName, pinName, x, y);
    }

    /// <summary>The component's footprint rectangle in exported coordinates.</summary>
    private static ExportedWaveguideOverlapAnalyzer.BoundingBox Footprint(Component component)
    {
        double minX = component.PhysicalX;
        double maxX = component.PhysicalX + component.WidthMicrometers;
        // App coordinates are Y-down, exported coordinates are Y-up — the box flips with it.
        double nazcaTopY = -component.PhysicalY;
        double nazcaBottomY = -(component.PhysicalY + component.HeightMicrometers);
        return new ExportedWaveguideOverlapAnalyzer.BoundingBox(minX, maxX, nazcaBottomY, nazcaTopY);
    }
}
