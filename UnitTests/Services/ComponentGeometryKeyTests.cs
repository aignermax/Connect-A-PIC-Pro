// UnitTests/Services/ComponentGeometryKeyTests.cs
using CAP.Avalonia.Services;
using CAP_Core.Components.Core;
using Shouldly;
using UnitTests;
using Xunit;

namespace UnitTests.Services;

public class ComponentGeometryKeyTests
{
    private static Component Wg(string module, string function, string parameters)
    {
        var c = TestComponentFactory.CreateStraightWaveGuide();
        c.NazcaModuleName = module;
        c.NazcaFunctionName = function;
        c.NazcaFunctionParameters = parameters;
        return c;
    }

    [Fact]
    public void SameModuleFunctionParameters_SameKey()
    {
        var a = ComponentGeometryKey.For(Wg("siepic", "ebeam_dc", "Lc=5"));
        var b = ComponentGeometryKey.For(Wg("siepic", "ebeam_dc", "Lc=5"));
        a.ShouldBe(b);
    }

    [Fact]
    public void DifferentParameters_DifferentKey()
    {
        var a = ComponentGeometryKey.For(Wg("siepic", "ebeam_dc", "Lc=5"));
        var b = ComponentGeometryKey.For(Wg("siepic", "ebeam_dc", "Lc=9"));
        a.ShouldNotBe(b);
    }

    [Fact]
    public void GeometryKey_HasGeoPrefix()
    {
        ComponentGeometryKey.For(Wg("m", "f", "p")).ShouldStartWith("geo:");
    }

    [Fact]
    public void ClonedComponent_SharesGeometryKeyWithOriginal()
    {
        // The user-facing guarantee behind geometry-scoped overrides: a copy/paste
        // (Component.Clone) is geometrically identical, so it must map to the SAME key —
        // otherwise the copy can't inherit the original's recomputed S-matrix override.
        var original = Wg("siepic_ebeam_pdk", "ebeam_crossing4", "");
        var clone = (Component)original.Clone();

        ComponentGeometryKey.For(clone)
            .ShouldBe(ComponentGeometryKey.For(original));
    }
}
