using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.ViewModels.Components.AddCustomComponent;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

/// <summary>
/// Covers the render contract (see <c>scripts/render_component_preview.py</c>,
/// <c>_build_cell_from_code_file</c>) for the Nazca starter snippet in
/// <see cref="BackendCodeExamples"/>: the preview renderer requires either a
/// <c>component()</c> function returning a Nazca cell, or a module-level <c>cell</c>
/// variable. The old <c>component = nd.Cell(...)</c> form satisfies neither and fails
/// the preview render.
/// </summary>
public class BackendCodeExamplesNazcaTests
{
    [Fact]
    public void Nazca_example_defines_a_component_function()
    {
        BackendCodeExamples.Nazca.ShouldContain("def component()");
    }

    [Fact]
    public void Nazca_example_builds_an_nd_Cell()
    {
        BackendCodeExamples.Nazca.ShouldContain("nd.Cell");
    }

    [Fact]
    public void Nazca_example_returns_the_cell()
    {
        BackendCodeExamples.Nazca.ShouldContain("return");
    }

    [Fact]
    public void Nazca_example_does_not_use_the_old_module_level_assignment_form()
    {
        BackendCodeExamples.Nazca.ShouldNotContain("component = nd.Cell");
    }

    [Fact]
    public void For_Nazca_backend_returns_the_Nazca_constant()
    {
        BackendCodeExamples.For(GeometryBackend.Nazca).ShouldBe(BackendCodeExamples.Nazca);
    }
}
