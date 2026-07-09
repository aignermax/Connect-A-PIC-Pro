using CAP_Core.Export.Netlist;
using Shouldly;

namespace UnitTests.Export.Netlist;

/// <summary>
/// The YAML writer must emit the gdsfactory netlist schema shape
/// (instances / placements / connections / ports) with invariant formatting (issue #687).
/// </summary>
public class GdsFactoryYamlNetlistWriterTests
{
    private static readonly IReadOnlyDictionary<string, string> NoSettings =
        new Dictionary<string, string>();

    private static NetlistDocument MakeDocument() =>
        new(
            "my_design",
            new[]
            {
                new NetlistInstance("A", "mmi1x2",
                    new Dictionary<string, string> { ["length"] = "10.5" }),
                new NetlistInstance("B", "bend_euler", NoSettings),
            },
            new[]
            {
                new NetlistPlacement("A", 0, 0, 0),
                new NetlistPlacement("B", 200.1234, -30, 90),
            },
            new[] { new NetlistConnection("A", "o2", "B", "o1", false) },
            new[] { new NetlistPort("A_o1", "A", "o1") });

    private static NetlistDocument EmptyDocument(string name) =>
        new(
            name,
            Array.Empty<NetlistInstance>(),
            Array.Empty<NetlistPlacement>(),
            Array.Empty<NetlistConnection>(),
            Array.Empty<NetlistPort>());

    [Fact]
    public void Write_FullDocument_ContainsAllSections()
    {
        var yaml = new GdsFactoryYamlNetlistWriter().Write(MakeDocument());

        yaml.ShouldContain("name: my_design");
        yaml.ShouldContain("instances:");
        yaml.ShouldContain("placements:");
        yaml.ShouldContain("connections:");
        yaml.ShouldContain("ports:");
    }

    [Fact]
    public void Write_Instance_EmitsComponentAndSettings()
    {
        var yaml = new GdsFactoryYamlNetlistWriter().Write(MakeDocument());

        yaml.ShouldContain("  A:");
        yaml.ShouldContain("    component: mmi1x2");
        yaml.ShouldContain("      length: 10.5");
    }

    [Fact]
    public void Write_Placement_UsesInvariantCultureDotDecimals()
    {
        var yaml = new GdsFactoryYamlNetlistWriter().Write(MakeDocument());

        // 200.1234 rounded to 3 decimals must use '.' regardless of OS locale.
        yaml.ShouldContain("x: 200.123");
        yaml.ShouldContain("y: -30");
        yaml.ShouldContain("rotation: 90");
        yaml.ShouldNotContain("200,123");
    }

    [Fact]
    public void Write_Connection_EmitsGdsFactoryPairSyntax()
    {
        var yaml = new GdsFactoryYamlNetlistWriter().Write(MakeDocument());

        yaml.ShouldContain("A,o2: B,o1");
    }

    [Fact]
    public void Write_ElectricalConnection_IsMarkedWithComment()
    {
        var doc = new NetlistDocument(
            "d",
            new[]
            {
                new NetlistInstance("A", "pad", NoSettings),
                new NetlistInstance("B", "pad", NoSettings),
            },
            Array.Empty<NetlistPlacement>(),
            new[] { new NetlistConnection("A", "p1", "B", "p1", true) },
            Array.Empty<NetlistPort>());

        var yaml = new GdsFactoryYamlNetlistWriter().Write(doc);

        yaml.ShouldContain("A,p1: B,p1  # electrical");
    }

    [Fact]
    public void Write_Port_EmitsInstanceCommaPin()
    {
        var yaml = new GdsFactoryYamlNetlistWriter().Write(MakeDocument());

        yaml.ShouldContain("A_o1: A,o1");
    }

    [Fact]
    public void Write_EmptyDocument_EmitsEmptyMappings()
    {
        var yaml = new GdsFactoryYamlNetlistWriter().Write(EmptyDocument("empty"));

        yaml.ShouldContain("instances: {}");
        yaml.ShouldContain("connections: {}");
        yaml.ShouldContain("ports: {}");
    }

    [Fact]
    public void Write_NonNumericSetting_IsQuoted()
    {
        var doc = new NetlistDocument(
            "d",
            new[]
            {
                new NetlistInstance("A", "wg",
                    new Dictionary<string, string> { ["name"] = "wg" }),
            },
            Array.Empty<NetlistPlacement>(),
            Array.Empty<NetlistConnection>(),
            Array.Empty<NetlistPort>());

        var yaml = new GdsFactoryYamlNetlistWriter().Write(doc);

        yaml.ShouldContain("name: 'wg'");
    }
}
