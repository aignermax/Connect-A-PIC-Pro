using CAP_Core.Components.Process;
using CAP_Core.Routing.MetalRouting;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Routing.MetalRouting;

/// <summary>
/// Tests for <see cref="MetalRoutingSpecFactory"/> — deriving trace width, GDS metal
/// layer, and the waveguide-crossing policy from the active process (issue #682).
/// </summary>
public class MetalRoutingSpecFactoryTests
{
    [Fact]
    public void FromDefinitions_MetalXsection_SuppliesWidthAndLayer()
    {
        var definition = new ProcessDefinition
        {
            Name = "Demo",
            Layers = new List<ProcessLayer>
            {
                new() { Name = "WAVEGUIDE", Layer = 1, Datatype = 0 },
                new() { Name = "METAL1", Layer = 11, Datatype = 2 }
            },
            Xsections = new List<ProcessXsection>
            {
                new() { Name = "Strip", Kind = XsectionKind.Optical, WidthUm = 0.45 },
                new() { Name = "MetalDC", Kind = XsectionKind.Metal, WidthUm = 8.5, Layers = new List<string> { "METAL1" } }
            }
        };

        var spec = MetalRoutingSpecFactory.FromDefinitions(new[] { definition });

        spec.TraceWidthMicrometers.ShouldBe(8.5);
        spec.MetalGdsLayer.ShouldBe(11);
        spec.MetalGdsDatatype.ShouldBe(2);
        spec.CrossingPolicy.ShouldBe(ElectricalCrossingPolicy.DirectCrossingAllowed);
    }

    [Fact]
    public void FromDefinitions_NoMetalXsection_FallsBackToDefault()
    {
        var definition = new ProcessDefinition
        {
            Name = "OpticalOnly",
            Xsections = new List<ProcessXsection>
            {
                new() { Name = "Strip", Kind = XsectionKind.Optical, WidthUm = 0.45 }
            }
        };

        var spec = MetalRoutingSpecFactory.FromDefinitions(new[] { definition });

        spec.TraceWidthMicrometers.ShouldBe(MetalRoutingSpec.DefaultTraceWidthMicrometers);
        spec.MetalGdsLayer.ShouldBe(MetalRoutingSpec.DefaultMetalGdsLayer);
    }

    [Fact]
    public void FromDefinitions_BridgeRequiredFlag_MapsToPolicy()
    {
        var definition = new ProcessDefinition { Name = "Bridgy", ElectricalBridgeRequired = true };

        var spec = MetalRoutingSpecFactory.FromDefinitions(new[] { definition });

        spec.CrossingPolicy.ShouldBe(ElectricalCrossingPolicy.BridgeRequired);
    }

    [Fact]
    public void FromDefinitions_AnyMemberRequiresBridge_PolicyIsBridgeRequired()
    {
        var relaxed = new ProcessDefinition { Name = "A", ElectricalBridgeRequired = false };
        var strict = new ProcessDefinition { Name = "B", ElectricalBridgeRequired = true };

        var spec = MetalRoutingSpecFactory.FromDefinitions(new[] { relaxed, strict });

        spec.CrossingPolicy.ShouldBe(ElectricalCrossingPolicy.BridgeRequired);
    }

    [Fact]
    public void FromDefinitions_MetalXsectionWithUnknownLayerName_FallsBackToDefaultLayer()
    {
        var definition = new ProcessDefinition
        {
            Name = "Demo",
            Xsections = new List<ProcessXsection>
            {
                new() { Name = "MetalDC", Kind = XsectionKind.Metal, WidthUm = 5, Layers = new List<string> { "MISSING" } }
            }
        };

        var spec = MetalRoutingSpecFactory.FromDefinitions(new[] { definition });

        spec.TraceWidthMicrometers.ShouldBe(5);
        spec.MetalGdsLayer.ShouldBe(MetalRoutingSpec.DefaultMetalGdsLayer);
        spec.MetalGdsDatatype.ShouldBe(MetalRoutingSpec.DefaultMetalGdsDatatype);
    }

    [Fact]
    public void FromDefinitions_ZeroWidthMetalXsection_FallsBackToDefaultWidth()
    {
        var definition = new ProcessDefinition
        {
            Name = "Demo",
            Xsections = new List<ProcessXsection>
            {
                new() { Name = "MetalDC", Kind = XsectionKind.Metal, WidthUm = 0 }
            }
        };

        var spec = MetalRoutingSpecFactory.FromDefinitions(new[] { definition });

        spec.TraceWidthMicrometers.ShouldBe(MetalRoutingSpec.DefaultTraceWidthMicrometers);
    }

    [Fact]
    public void FromActiveProcess_NullSelection_ReturnsDefault()
    {
        var spec = MetalRoutingSpecFactory.FromActiveProcess(null, new List<PdkDraft>());

        spec.ShouldBe(MetalRoutingSpec.Default);
    }

    [Fact]
    public void FromActiveProcess_ResolvesMemberPdkByNameCaseInsensitive()
    {
        var pdk = new PdkDraft
        {
            Name = "Demo PDK",
            Process = new ProcessDefinition
            {
                Name = "Demo",
                Layers = new List<ProcessLayer> { new() { Name = "METAL1", Layer = 11 } },
                Xsections = new List<ProcessXsection>
                {
                    new() { Name = "MetalDC", Kind = XsectionKind.Metal, WidthUm = 10, Layers = new List<string> { "METAL1" } }
                }
            }
        };
        var active = new ActiveProcessSelection(
            "Demo", Fingerprint: null, MemberPdkNames: new List<string> { "demo pdk" }, IsPlayground: false);

        var spec = MetalRoutingSpecFactory.FromActiveProcess(active, new List<PdkDraft> { pdk });

        spec.TraceWidthMicrometers.ShouldBe(10);
        spec.MetalGdsLayer.ShouldBe(11);
    }
}
