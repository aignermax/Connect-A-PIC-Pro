using CAP.Avalonia.Services.GdsImport;
using CAP_DataAccess.Import.Gds;
using Shouldly;
using Xunit;

namespace UnitTests.Services.GdsImport;

/// <summary>
/// Tests for <see cref="GdsPlacementPlan.FromOutcome"/>: the data-only plan the
/// UI layer executes on the canvas (positions, rotations, reflection notes,
/// connection endpoints, top-cell-port flags, group name).
/// </summary>
public class GdsPlacementPlanTests
{
    private const double Tolerance = 1e-9;

    private static GdsImportOutcome Outcome(
        IReadOnlyList<GdsPlacedInstance> instances,
        IReadOnlyList<GdsPinPair> connections,
        IReadOnlyList<GdsRegisteredComponent>? registered = null) => new()
    {
        TopCellName = "TOP",
        Mode = GdsHierarchyImportMode.ExplodeHierarchy,
        RegisteredComponents = registered ?? Array.Empty<GdsRegisteredComponent>(),
        Instances = instances,
        Connections = connections,
        Warnings = new[] { "some import warning" },
        Infos = new[] { "some import info" },
        UserPdkName = "GDS Import - circuit",
        UserPdkPath = "/pdks/gds-import-circuit.json",
        GdsFileName = "circuit.gds",
    };

    [Fact]
    public void Plan_RotatedReflectedInstance_CarriesTransformWithoutMirrorNote()
    {
        var outcome = Outcome(
            new[]
            {
                new GdsPlacedInstance
                {
                    InstanceName = "wgA#0",
                    CellDraftName = "wgA",
                    PositionXUm = 12.5,
                    PositionYUm = 40,
                    RotationDegrees = 270,
                    Reflected = true,
                },
            },
            Array.Empty<GdsPinPair>(),
            new[] { new GdsRegisteredComponent("wgA", "wgA") });

        var plan = GdsPlacementPlan.FromOutcome(outcome);

        var placement = plan.Placements.ShouldHaveSingleItem();
        placement.InstanceName.ShouldBe("wgA#0");
        placement.ComponentIdentifier.ShouldBe("wgA");
        placement.PdkSource.ShouldBe("GDS Import - circuit");
        placement.IsImportedDraft.ShouldBeTrue();
        placement.XUm.ShouldBe(12.5, Tolerance);
        placement.YUm.ShouldBe(40, Tolerance);
        placement.RotationDegrees.ShouldBe(270, Tolerance);
        placement.Reflected.ShouldBeTrue();
        placement.Warning.ShouldBeNull(
            "no per-instance mirror note — the importer's transform-aggregated " +
            "STRANS warning already covers every mirrored instance's cell");
    }

    [Fact]
    public void Plan_KnownComponentInstance_ReferencesExistingPdkComponent()
    {
        var outcome = Outcome(
            new[]
            {
                new GdsPlacedInstance
                {
                    InstanceName = "mmi1x2#0",
                    KnownComponentIdentifier = "mmi1x2",
                    PdkSource = "demo-pdk",
                    PositionXUm = 0,
                    PositionYUm = 0,
                },
            },
            Array.Empty<GdsPinPair>());

        var placement = GdsPlacementPlan.FromOutcome(outcome).Placements.ShouldHaveSingleItem();
        placement.ComponentIdentifier.ShouldBe("mmi1x2");
        placement.PdkSource.ShouldBe("demo-pdk");
        placement.IsImportedDraft.ShouldBeFalse();
        placement.Warning.ShouldBeNull();
    }

    [Fact]
    public void Plan_UnregisteredDraftInstance_IsFlaggedNotPlaceable()
    {
        var outcome = Outcome(
            new[]
            {
                new GdsPlacedInstance { InstanceName = "blob#0", CellDraftName = "blob" },
            },
            Array.Empty<GdsPinPair>());

        var placement = GdsPlacementPlan.FromOutcome(outcome).Placements.ShouldHaveSingleItem();
        placement.ComponentIdentifier.ShouldBeNull();
        placement.PdkSource.ShouldBeNull();
        placement.Warning.ShouldContain("not registered");
    }

    [Fact]
    public void Plan_RouteDerivedConnection_CarriesSourcePolygons()
    {
        var stripe = new GdsOutlinePolygon
        {
            Layer = 1,
            DataType = 0,
            Points = new[] { new GdsOutlinePoint(10, 1.75), new GdsOutlinePoint(20, 2.25) },
        };
        var outcome = Outcome(
            new[]
            {
                new GdsPlacedInstance { InstanceName = "wgA#0", CellDraftName = "wgA" },
                new GdsPlacedInstance { InstanceName = "wgB#0", CellDraftName = "wgB" },
            },
            new[]
            {
                new GdsPinPair
                {
                    A = new GdsPinEndpoint { InstanceIndex = 0, PinName = "out" },
                    B = new GdsPinEndpoint { InstanceIndex = 1, PinName = "in" },
                    IsRouteDerived = true,
                    SourcePolygons = new[] { stripe },
                },
            },
            new[]
            {
                new GdsRegisteredComponent("wgA", "wgA"),
                new GdsRegisteredComponent("wgB", "wgB"),
            });

        var connection = GdsPlacementPlan.FromOutcome(outcome).Connections.ShouldHaveSingleItem();
        connection.IsRouteDerived.ShouldBeTrue();
        connection.SourcePolygons.ShouldHaveSingleItem().ShouldBe(stripe,
            "the drawn geometry rides the plan so the executor can attach it as a frozen cached route");
    }

    [Fact]
    public void Plan_Connections_MapInstanceIndexesAndPinNames()
    {
        var outcome = Outcome(
            new[]
            {
                new GdsPlacedInstance { InstanceName = "wgA#0", CellDraftName = "wgA" },
                new GdsPlacedInstance { InstanceName = "wgB#0", CellDraftName = "wgB" },
            },
            new[]
            {
                new GdsPinPair
                {
                    A = new GdsPinEndpoint { InstanceIndex = 0, PinName = "out" },
                    B = new GdsPinEndpoint { InstanceIndex = 1, PinName = "in" },
                    XUm = 10,
                    YUm = 2,
                },
            },
            new[]
            {
                new GdsRegisteredComponent("wgA", "wgA"),
                new GdsRegisteredComponent("wgB", "wgB"),
            });

        var plan = GdsPlacementPlan.FromOutcome(outcome);

        var connection = plan.Connections.ShouldHaveSingleItem();
        connection.A.InstanceIndex.ShouldBe(0);
        connection.A.PinName.ShouldBe("out");
        connection.B.InstanceIndex.ShouldBe(1);
        connection.B.PinName.ShouldBe("in");
        connection.A.IsTopLevelPort.ShouldBeFalse();
        connection.B.IsTopLevelPort.ShouldBeFalse();
        connection.InvolvesTopLevelPort.ShouldBeFalse();
        connection.Note.ShouldBeNull();
        connection.XUm.ShouldBe(10, Tolerance);
        connection.YUm.ShouldBe(2, Tolerance);
    }

    [Fact]
    public void Plan_TopCellPortConnection_IsFlaggedToLeaveFree()
    {
        var outcome = Outcome(
            new[]
            {
                new GdsPlacedInstance { InstanceName = "wgA#0", CellDraftName = "wgA" },
            },
            new[]
            {
                new GdsPinPair
                {
                    A = new GdsPinEndpoint { InstanceIndex = 0, PinName = "in" },
                    B = new GdsPinEndpoint { InstanceIndex = -1, PinName = "o1" },
                    XUm = 0,
                    YUm = 2,
                },
            },
            new[] { new GdsRegisteredComponent("wgA", "wgA") });

        var connection = GdsPlacementPlan.FromOutcome(outcome).Connections.ShouldHaveSingleItem();
        connection.A.IsTopLevelPort.ShouldBeFalse();
        connection.B.IsTopLevelPort.ShouldBeTrue();
        connection.B.PinName.ShouldBe("o1");
        connection.InvolvesTopLevelPort.ShouldBeTrue();
        connection.Note.ShouldContain("top-cell port");
    }

    [Fact]
    public void Plan_AllPlacementsShareOneGroupNamedAfterTheTopCell()
    {
        var outcome = Outcome(
            new[]
            {
                new GdsPlacedInstance { InstanceName = "a#0", CellDraftName = "a" },
                new GdsPlacedInstance { InstanceName = "b#0", CellDraftName = "b" },
            },
            Array.Empty<GdsPinPair>(),
            new[]
            {
                new GdsRegisteredComponent("a", "a"),
                new GdsRegisteredComponent("b", "b"),
            });

        var plan = GdsPlacementPlan.FromOutcome(outcome);

        plan.GroupName.ShouldBe("TOP");
        plan.Warnings.ShouldBe(new[] { "some import warning" });
        plan.Infos.ShouldBe(new[] { "some import info" });
    }
}
