using CAP.Avalonia.Services.AddCustomComponent;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

/// <summary>
/// Covers <see cref="GeometryReference.ToGdsFactoryRawCode"/> and
/// <see cref="GeometryReference.QualifiedFunction"/> — the exact raw-code string emitted
/// for the module/no-module and with/without-parameters cases.
/// </summary>
public class GeometryReferenceTests
{
    [Fact]
    public void ToGdsFactoryRawCode_with_module_and_params_emits_import_and_kwargs()
    {
        var reference = new GeometryReference(GeometryBackend.GdsFactory, "cspdk.sin300", "coupler", "length=10");

        var code = reference.ToGdsFactoryRawCode();

        code.ShouldBe("import cspdk.sin300\ncomponent = cspdk.sin300.coupler(length=10)");
        code.ShouldContain("import cspdk.sin300");
        code.ShouldContain("component = cspdk.sin300.coupler(length=10)");
    }

    [Fact]
    public void ToGdsFactoryRawCode_with_module_and_null_params_emits_empty_parentheses()
    {
        var reference = new GeometryReference(GeometryBackend.GdsFactory, "cspdk.sin300", "coupler", null);

        var code = reference.ToGdsFactoryRawCode();

        code.ShouldBe("import cspdk.sin300\ncomponent = cspdk.sin300.coupler()");
        code.ShouldContain("component = cspdk.sin300.coupler()");
    }

    [Fact]
    public void ToGdsFactoryRawCode_with_empty_module_imports_gdsfactory_and_uses_qualified_function()
    {
        var reference = new GeometryReference(GeometryBackend.GdsFactory, null, "gf.components.straight", null);

        var code = reference.ToGdsFactoryRawCode();

        code.ShouldBe("import gdsfactory as gf\ncomponent = gf.components.straight()");
        code.ShouldContain("import gdsfactory as gf");
        code.ShouldContain("component = gf.components.straight()");
    }

    [Fact]
    public void QualifiedFunction_prefixes_module_when_set()
    {
        var reference = new GeometryReference(GeometryBackend.GdsFactory, "cspdk.sin300", "coupler", null);

        reference.QualifiedFunction.ShouldBe("cspdk.sin300.coupler");
    }

    [Fact]
    public void QualifiedFunction_is_bare_function_when_module_empty()
    {
        var reference = new GeometryReference(GeometryBackend.GdsFactory, null, "gf.components.straight", null);

        reference.QualifiedFunction.ShouldBe("gf.components.straight");
    }
}
