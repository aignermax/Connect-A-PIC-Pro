using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Export.Netlist;
using CAP_Core.Routing;
using CAP_Core.Tiles;
using Shouldly;

namespace UnitTests.Export.Netlist;

/// <summary>
/// The netlist deriver must turn placed components into instances, routed connections
/// into topology edges, and unconnected pins into top-level ports (issue #687).
/// </summary>
public class NetlistDeriverTests
{
    private static Component MakeComponent(
        string name, double x, MatterType pinKind = MatterType.Light, params string[] pinNames)
    {
        var comp = TestComponentFactory.CreateBasicComponent();
        comp.Identifier = name;
        comp.NazcaFunctionName = "demo_pdk.mmi";
        comp.PhysicalX = x;
        comp.PhysicalY = 0;
        comp.WidthMicrometers = 50;
        comp.HeightMicrometers = 30;
        foreach (var pinName in pinNames)
        {
            comp.PhysicalPins.Add(new PhysicalPin
            {
                Name = pinName,
                ParentComponent = comp,
                OffsetXMicrometers = 0,
                OffsetYMicrometers = 0,
                AngleDegrees = 180,
                LogicalPin = new Pin(pinName, 0, pinKind, RectSide.Left),
            });
        }
        return comp;
    }

    private static WaveguideConnection Connect(Component a, string pinA, Component b, string pinB) =>
        new()
        {
            StartPin = a.PhysicalPins.First(p => p.Name == pinA),
            EndPin = b.PhysicalPins.First(p => p.Name == pinB),
        };

    [Fact]
    public void Derive_TwoConnectedComponents_ProducesInstancesConnectionAndPorts()
    {
        var a = MakeComponent("A", 0, MatterType.Light, "o1", "o2");
        var b = MakeComponent("B", 200, MatterType.Light, "o1", "o2");
        var conn = Connect(a, "o2", b, "o1");

        var netlist = new NetlistDeriver().Derive(new[] { a, b }, new[] { conn });

        netlist.Instances.Select(i => i.Name).ShouldBe(new[] { "A", "B" });
        netlist.Connections.ShouldHaveSingleItem();
        netlist.Connections[0].ShouldBe(new NetlistConnection("A", "o2", "B", "o1", false));
        // The two pins not consumed by the connection become top-level ports.
        netlist.Ports.Select(p => p.Name).ShouldBe(new[] { "A_o1", "B_o2" });
    }

    [Fact]
    public void Derive_GdsFactoryBackedComponent_UsesCellNameAsComponentRef()
    {
        var comp = MakeComponent("A", 0);
        comp.GdsFactoryFunction = "cspdk.sin300.mmi1x2";

        var netlist = new NetlistDeriver().Derive(new[] { comp }, Array.Empty<WaveguideConnection>());

        netlist.Instances[0].ComponentRef.ShouldBe("mmi1x2");
    }

    [Fact]
    public void Derive_NazcaComponent_UsesNazcaFunctionNameAsComponentRef()
    {
        var comp = MakeComponent("A", 0);

        var netlist = new NetlistDeriver().Derive(new[] { comp }, Array.Empty<WaveguideConnection>());

        netlist.Instances[0].ComponentRef.ShouldBe("demo_pdk.mmi");
    }

    [Fact]
    public void Derive_DuplicateAndUnsafeNames_AreUniquifiedAndSanitized()
    {
        var a = MakeComponent("MMI 1x2", 0);
        var b = MakeComponent("MMI 1x2", 100);

        var netlist = new NetlistDeriver().Derive(new[] { a, b }, Array.Empty<WaveguideConnection>());

        netlist.Instances.Select(i => i.Name).ShouldBe(new[] { "MMI_1x2", "MMI_1x2_2" });
    }

    [Fact]
    public void Derive_ReverseDuplicateConnection_IsDeduplicated()
    {
        var a = MakeComponent("A", 0, MatterType.Light, "o1");
        var b = MakeComponent("B", 200, MatterType.Light, "o1");
        var forward = Connect(a, "o1", b, "o1");
        var reverse = Connect(b, "o1", a, "o1");

        var netlist = new NetlistDeriver().Derive(new[] { a, b }, new[] { forward, reverse });

        netlist.Connections.Count.ShouldBe(1);
    }

    [Fact]
    public void Derive_ElectricalConnection_IsFlaggedElectrical()
    {
        var a = MakeComponent("A", 0, MatterType.Electricity, "p1");
        var b = MakeComponent("B", 200, MatterType.Electricity, "p1");

        var netlist = new NetlistDeriver().Derive(
            new[] { a, b }, new[] { Connect(a, "p1", b, "p1") });

        netlist.Connections[0].IsElectrical.ShouldBeTrue();
    }

    [Fact]
    public void Derive_AnalysisTool_IsExcludedFromInstancesAndConnections()
    {
        var a = MakeComponent("A", 0, MatterType.Light, "o1");
        var analyzer = MakeComponent("ONA", 200, MatterType.Light, "o1");
        analyzer.NazcaFunctionName = Component.AnalysisToolNazcaSentinel;

        var netlist = new NetlistDeriver().Derive(
            new[] { a, analyzer }, new[] { Connect(a, "o1", analyzer, "o1") });

        netlist.Instances.Select(i => i.Name).ShouldBe(new[] { "A" });
        netlist.Connections.ShouldBeEmpty();
        // The pin that was wired to the analyzer surfaces as an open port.
        netlist.Ports.Select(p => p.Name).ShouldBe(new[] { "A_o1" });
    }

    [Fact]
    public void Derive_GroupWithInternalPath_FlattensChildrenAndKeepsInternalEdge()
    {
        var childA = MakeComponent("ChildA", 0, MatterType.Light, "o1");
        var childB = MakeComponent("ChildB", 100, MatterType.Light, "o1");
        var group = new ComponentGroup("Group1");
        group.AddChild(childA);
        group.AddChild(childB);
        group.AddInternalPath(new FrozenWaveguidePath
        {
            Path = new RoutedPath(),
            StartPin = childA.PhysicalPins[0],
            EndPin = childB.PhysicalPins[0],
        });

        var netlist = new NetlistDeriver().Derive(
            new Component[] { group }, Array.Empty<WaveguideConnection>());

        netlist.Instances.Select(i => i.Name).ShouldBe(new[] { "ChildA", "ChildB" });
        netlist.Connections.ShouldHaveSingleItem();
        netlist.Connections[0].InstanceA.ShouldBe("ChildA");
        netlist.Connections[0].InstanceB.ShouldBe("ChildB");
    }

    [Fact]
    public void Derive_ParseableParameters_BecomeInstanceSettings()
    {
        var comp = MakeComponent("A", 0);
        comp.NazcaFunctionParameters = "length=10.5, name='wg'";

        var netlist = new NetlistDeriver().Derive(new[] { comp }, Array.Empty<WaveguideConnection>());

        netlist.Instances[0].Settings["length"].ShouldBe("10.5");
        netlist.Instances[0].Settings["name"].ShouldBe("wg");
    }

    [Fact]
    public void Derive_BlankDesignName_FallsBackToDefault()
    {
        var netlist = new NetlistDeriver().Derive(
            Array.Empty<Component>(), Array.Empty<WaveguideConnection>(), "  ");

        netlist.Name.ShouldBe(NetlistDeriver.DefaultDesignName);
    }
}
