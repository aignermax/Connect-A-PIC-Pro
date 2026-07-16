using System.Collections.Generic;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Components.Process;

/// <summary>
/// Unit tests for <see cref="ProcessLayerConsistency.LayersConsistent"/> (issue #570 follow-up):
/// the layer-stack half of live process compatibility, on top of the fingerprint (material/
/// thickness/wavelength) check already covered by <see cref="ProcessCompatibilityTests"/>.
/// </summary>
public class LayerConsistencyTests
{
    private static ProcessDefinition WithLayers(params ProcessLayer[] layers) =>
        new() { Name = "Test", Layers = new List<ProcessLayer>(layers) };

    private static ProcessLayer Layer(string name, int layer, int datatype = 0) =>
        new() { Name = name, Layer = layer, Datatype = datatype };

    [Fact]
    public void SameLayerName_DifferentNumber_IsNotConsistent()
    {
        var a = WithLayers(Layer("NITRIDE", 203));
        var b = WithLayers(Layer("NITRIDE", 2030));

        ProcessLayerConsistency.LayersConsistent(a, b).ShouldBeFalse();
    }

    [Fact]
    public void SameLayerNameAndNumber_DifferentDatatype_IsNotConsistent()
    {
        var a = WithLayers(Layer("NITRIDE", 203, datatype: 0));
        var b = WithLayers(Layer("NITRIDE", 203, datatype: 1));

        ProcessLayerConsistency.LayersConsistent(a, b).ShouldBeFalse();
    }

    [Fact]
    public void AdditionalLayerOnOneSideOnly_IsConsistent()
    {
        // b adds a metal layer on top of an otherwise-identical stack (issue #734 workflow) —
        // additions must never make the process look incompatible.
        var a = WithLayers(Layer("NITRIDE", 203));
        var b = WithLayers(Layer("NITRIDE", 203), Layer("METAL-1", 11));

        ProcessLayerConsistency.LayersConsistent(a, b).ShouldBeTrue();
    }

    [Fact]
    public void DisjointLayerNames_AreConsistent()
    {
        var a = WithLayers(Layer("WAVEGUIDE", 1));
        var b = WithLayers(Layer("NITRIDE", 203));

        ProcessLayerConsistency.LayersConsistent(a, b).ShouldBeTrue();
    }

    [Fact]
    public void EmptyLayerLists_AreConsistent()
    {
        var a = WithLayers();
        var b = WithLayers(Layer("NITRIDE", 203));

        ProcessLayerConsistency.LayersConsistent(a, b).ShouldBeTrue();
        ProcessLayerConsistency.LayersConsistent(b, a).ShouldBeTrue();
    }

    [Fact]
    public void NullDefinitions_AreConsistent()
    {
        ProcessLayerConsistency.LayersConsistent(null, null).ShouldBeTrue();
        ProcessLayerConsistency.LayersConsistent(null, WithLayers(Layer("NITRIDE", 203))).ShouldBeTrue();
        ProcessLayerConsistency.LayersConsistent(WithLayers(Layer("NITRIDE", 203)), null).ShouldBeTrue();
    }

    [Fact]
    public void LayerNameComparison_IsCaseAndWhitespaceInsensitive()
    {
        var a = WithLayers(Layer("Nitride", 203));
        var b = WithLayers(Layer("  nitride  ", 203));

        ProcessLayerConsistency.LayersConsistent(a, b).ShouldBeTrue();

        var conflicting = WithLayers(Layer("  NITRIDE  ", 2030));
        ProcessLayerConsistency.LayersConsistent(a, conflicting).ShouldBeFalse();
    }

    [Fact]
    public void DuplicateLayerNameWithinOneDefinition_UsesFirstOccurrence()
    {
        // Internal duplication is a PDK-authoring concern, not a cross-process compatibility one —
        // first occurrence wins rather than requiring every duplicate pair to agree.
        var a = WithLayers(Layer("NITRIDE", 203), Layer("NITRIDE", 999));
        var matchesFirst = WithLayers(Layer("NITRIDE", 203));

        ProcessLayerConsistency.LayersConsistent(a, matchesFirst).ShouldBeTrue();
    }
}
