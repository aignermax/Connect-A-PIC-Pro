using CAP.Avalonia.Services.GdsFactoryExport;
using Shouldly;

namespace UnitTests.Export.GdsFactoryExport;

/// <summary>
/// Tests for the gdsfactory preview-code builder: it turns a component's
/// <c>GdsFactoryFunction</c> (e.g. "cspdk.sin300.mmi1x2") into the raw code that
/// <c>render_gdsfactory_preview.py</c> expects — import + activate the PDK, then
/// assign <c>component</c> from the PDK registry (#570 preview fix).
/// </summary>
public class GdsFactoryPreviewCodeTests
{
    [Fact]
    public void For_ModuleQualifiedFunction_ImportsActivatesAndResolvesTheCell()
    {
        var code = GdsFactoryPreviewCode.For("cspdk.sin300.mmi1x2");

        code.ShouldNotBeNull();
        code!.ShouldContain("import cspdk.sin300");
        code.ShouldContain("cspdk.sin300.PDK.activate()");
        code.ShouldContain("component = gf.get_component('mmi1x2')");   // render script looks for `component`
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("straight")]   // bare name: no importable module → cannot render
    public void For_EmptyOrBareName_ReturnsNull(string? function)
    {
        GdsFactoryPreviewCode.For(function).ShouldBeNull();
    }
}
