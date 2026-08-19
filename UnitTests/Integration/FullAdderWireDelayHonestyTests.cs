using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Analysis.LogicAnalysis;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Rung-4 honesty test for the inter-gate wire delays (issue #1037, feature #1020/#1027):
/// the shipped <c>examples/Logic Gate Full Adder.lun</c> wires every gate pin-to-pin over
/// zero-length routes, so the E2E journey (#1022/#1030) exercises
/// <see cref="LogicNetworkEvaluator.WireDelaysPicoseconds"/> only with zeros. Here the
/// example loads through the real load path, the critical path's first gate moves a
/// substantial distance away through the real canvas move API, the stretched wire re-routes
/// through the real router, and the re-assembled network must carry a non-zero wire delay
/// for the moved edge — equal to the connection's routed length × n_g / c, recomputed
/// independently from the canvas connection — and the critical path must grow by exactly
/// that delay, the moved edge being the path's only changed edge. A save → load round trip
/// through the real persistence path must keep the non-zero delay, ruling out a regression
/// where <see cref="WaveguideConnection.PathLengthMicrometers"/> comes back zero on loaded
/// designs while every suite stays green.
/// </summary>
public class FullAdderWireDelayHonestyTests : IClassFixture<FullAdderWireDelayHonestyTests.MovedFullAdder>
{
    private const double Tolerance = 1e-9;

    /// <summary>Move delta: well clear of every gate row while staying inside the 5000 µm chip.</summary>
    private const double MoveDeltaX = -50;
    private const double MoveDeltaY = 3300;

    private readonly MovedFullAdder _fixture;

    /// <summary>Attaches the shared moved-design fixture.</summary>
    public FullAdderWireDelayHonestyTests(MovedFullAdder fixture) => _fixture = fixture;

    [Fact]
    public void WireDelay_GateMovedOnLoadedDesign_FiresNonZeroDelay_AndCriticalPathGrowsExactly()
    {
        var movedEdge = _fixture.MovedEdge;
        movedEdge.Value.ShouldBeGreaterThan(0,
            "the moved edge must carry a non-zero wire delay — zeros everywhere would mean " +
            "the routed path length silently came back zero on a loaded design");

        var expected = _fixture.MovedConnection.PathLengthMicrometers
            * GateDelayCalculator.DefaultGroupIndex
            / GateDelayCalculator.SpeedOfLightMicrometersPerPicosecond;
        movedEdge.Value.ShouldBe(expected, Tolerance,
            "the wire delay must equal the connection's routed length × n_g / c, recomputed " +
            "independently from the canvas connection");

        _fixture.MovedNetwork.CriticalPathGateIds.ShouldBe(
            _fixture.BaselineNetwork.CriticalPathGateIds,
            "moving one gate must not change which gates form the critical path");
        var baselineEdgeDelay = _fixture.BaselineNetwork.WireDelaysPicoseconds[_fixture.MovedEdge.Key];
        baselineEdgeDelay.ShouldBe(0,
            "the shipped example's pin-to-pin wires are all zero-length — the premise this test pins");
        _fixture.MovedNetwork.CriticalPathDelayPicoseconds.ShouldBe(
            _fixture.BaselineNetwork.CriticalPathDelayPicoseconds + movedEdge.Value,
            Tolerance,
            "the critical path grows by exactly the moved edge's wire delay — it is the only " +
            "edge on the path whose wire changed");
    }

    [Fact]
    public async Task WireDelay_MovedDesignSavedAndReloaded_KeepsNonZeroDelay()
    {
        var savedPath = await SaveFixtureToTempFile();
        try
        {
            var reloadedCanvas = await LogicGateHalfAdderExampleTests.LoadCanvas(savedPath);
            var reloaded = await LogicGateFullAdderExampleTests.AssembleNetwork(reloadedCanvas);

            reloaded.WireDelaysPicoseconds.ContainsKey(_fixture.MovedEdge.Key).ShouldBeTrue(
                "the moved edge must still exist after the save → load round trip");
            reloaded.WireDelaysPicoseconds[_fixture.MovedEdge.Key].ShouldBeGreaterThan(0,
                "the non-zero wire delay must survive the real save → load path");
            reloaded.WireDelaysPicoseconds[_fixture.MovedEdge.Key].ShouldBe(
                _fixture.MovedEdge.Value, Tolerance,
                "the delay must come back identical after persistence, not re-derived as zero");
        }
        finally
        {
            if (File.Exists(savedPath)) File.Delete(savedPath);
        }
    }

    /// <summary>Saves the fixture's moved canvas through the real save path and returns the file path.</summary>
    private async Task<string> SaveFixtureToTempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wire-delay-honesty-{Guid.NewGuid():N}.lun");
        var saveVm = LogicGateHalfAdderExampleTests.CreateFileOperations(_fixture.Canvas);
        var dialog = new Mock<IFileDialogService>();
        dialog.Setup(f => f.ShowSaveFileDialogAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(path);
        saveVm.FileDialogService = dialog.Object;
        await saveVm.SaveDesignAsCommand.ExecuteAsync(null);
        File.Exists(path).ShouldBeTrue("the real save path must write the temp .lun");
        return path;
    }

    /// <summary>
    /// Shared fixture: loads the shipped full adder through the real load path, assembles the
    /// baseline network, then moves the critical path's first gate through the real canvas
    /// move API, re-routes the stretched wire through the real router, and re-assembles.
    /// The move uses the drag path (BeginDrag → MoveComponent) so no fire-and-forget
    /// canvas-wide re-route races the assembly; the one stretched wire is re-routed
    /// synchronously through the canvas's real router — the same
    /// <see cref="WaveguideConnection.RecalculateTransmission"/> entry point the routing
    /// pass calls per connection.
    /// </summary>
    public class MovedFullAdder : IAsyncLifetime
    {
        private const string ExampleFileName = "Logic Gate Full Adder.lun";

        /// <summary>The loaded canvas after the move and re-route.</summary>
        public DesignCanvasViewModel Canvas { get; private set; } = null!;

        /// <summary>The network assembled before the move (zero wire delays).</summary>
        public LogicNetworkEvaluator BaselineNetwork { get; private set; } = null!;

        /// <summary>The network assembled after the move and re-route.</summary>
        public LogicNetworkEvaluator MovedNetwork { get; private set; } = null!;

        /// <summary>The canvas connection whose routed path the move stretched.</summary>
        public WaveguideConnection MovedConnection { get; private set; } = null!;

        /// <summary>The moved edge's entry in the re-assembled network's wire delay map.</summary>
        public KeyValuePair<LogicWireEdge, double> MovedEdge { get; private set; }

        /// <summary>Loads, assembles the baseline, moves the first critical-path gate, re-routes, re-assembles.</summary>
        public async Task InitializeAsync()
        {
            var path = Path.Combine(ExampleDesignFilesTests.ExamplesDirectory(), ExampleFileName);
            Canvas = await LogicGateHalfAdderExampleTests.LoadCanvas(path);
            BaselineNetwork = await LogicGateFullAdderExampleTests.AssembleNetwork(Canvas);

            var gateName = MoveFirstCriticalPathGate();
            MovedConnection = FindGateConnection(gateName);
            MovedConnection.RecalculateTransmission(Canvas.Router);
            MovedNetwork = await LogicGateFullAdderExampleTests.AssembleNetwork(Canvas);
            MovedEdge = MovedNetwork.WireDelaysPicoseconds
                .Single(pair => pair.Key.Source.GateId == gateName);
        }

        /// <summary>
        /// Moves the critical path's first gate far away from its successor through the real
        /// move API, so exactly one on-path edge — its output wire — gets a stretched route.
        /// Returns the moved gate's name.
        /// </summary>
        private string MoveFirstCriticalPathGate()
        {
            var gateName = BaselineNetwork.CriticalPathGateIds[0];
            var gateVm = Canvas.Components.Single(c => c.Component is ComponentGroup group
                && group.GroupName == gateName);
            var beforeX = gateVm.X;
            var beforeY = gateVm.Y;
            Canvas.BeginDragComponent(gateVm);
            Canvas.MoveComponent(gateVm, MoveDeltaX, MoveDeltaY);
            (Math.Abs(gateVm.X - beforeX - MoveDeltaX) < 0.001
                && Math.Abs(gateVm.Y - beforeY - MoveDeltaY) < 0.001).ShouldBeTrue(
                $"the move of gate '{gateName}' must reach the requested position — a rejected " +
                "move would silently leave the wire delay at zero");
            return gateName;
        }

        /// <summary>
        /// Finds the single canvas connection incident to a gate group: the first critical-path
        /// gate is driven only by network inputs, so its one design wire is the output edge the
        /// move lies on.
        /// </summary>
        private WaveguideConnection FindGateConnection(string gateName)
        {
            var group = Canvas.Components.Select(c => c.Component).OfType<ComponentGroup>()
                .Single(g => g.GroupName == gateName);
            return Canvas.Connections.Select(c => c.Connection)
                .Single(c => ResolvesToGroup(c.StartPin!, group) || ResolvesToGroup(c.EndPin!, group));
        }

        /// <summary>
        /// True when the wire endpoint belongs to the gate group — directly (internal pin) or
        /// behind the group's synced external pin, mirroring the load path's endpoint binding.
        /// </summary>
        private static bool ResolvesToGroup(PhysicalPin pin, ComponentGroup group) =>
            ReferenceEquals(pin.ParentComponent, group)
            || group.ExternalPins.Any(external => ReferenceEquals(external.InternalPin, pin));

        /// <summary>No shared state to release.</summary>
        public Task DisposeAsync() => Task.CompletedTask;
    }
}
