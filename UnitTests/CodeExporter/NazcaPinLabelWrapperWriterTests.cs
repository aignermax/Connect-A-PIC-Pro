using CAP.Avalonia.Services;
using CAP.Avalonia.Services.GdsImport;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using Shouldly;
using Xunit;

namespace UnitTests.CodeExporter;

/// <summary>
/// Tests for <see cref="NazcaPinLabelWrapperWriter"/>'s wrapper-cell naming
/// (issue #811): the cell is named after the component's TEMPLATE, so a
/// user-renamed instance still exports a cell that resolves back to the
/// original library template on GDS re-import.
/// </summary>
public class NazcaPinLabelWrapperWriterTests
{
    [Fact]
    public void Export_TwoCopiesOneRenamed_ShareTemplateNamedCellThatResolvesBack()
    {
        // Two copies of the bundled Photodetector; the second was "renamed by
        // the user" (identifier AND display name — the clipboard/rename paths
        // change both). Pre-fix the copy exported a wrapper cell named
        // 'Photodetector_2', which no template resolves.
        var templates = TestPdkLoader.LoadAllTemplates();
        var template = templates.First(t => t.Name == "Photodetector" && t.PdkSource == "Demo PDK");
        var canvas = new DesignCanvasViewModel();

        var original = ComponentTemplates.CreateFromTemplate(template, 100, 100);
        original.Identifier = "Photodetector_1";
        var copy = ComponentTemplates.CreateFromTemplate(template, 300, 100);
        copy.Identifier = "Photodetector_2";
        copy.HumanReadableName = "Photodetector_2";
        canvas.AddComponent(original, template.Name, template.PdkSource);
        canvas.AddComponent(copy, template.Name, template.PdkSource);

        var script = new SimpleNazcaExporter().Export(canvas, library: templates);

        // ONE shared wrapper cell, named after the TEMPLATE — the rename is
        // invisible to the export.
        CountOccurrences(script, "with nd.Cell(name='Photodetector')").ShouldBe(1);
        script.ShouldNotContain("name='Photodetector_2'");
        CountOccurrences(script, "lunima_pinwrap_Photodetector().put('org'").ShouldBe(2,
            "both copies place the shared template-named wrapper cell");

        // The exported cell name resolves straight back to the original template.
        var resolver = GdsTemplateResolver.BuildKnownComponentResolver(templates);
        var known = resolver("Photodetector");
        known.ShouldNotBeNull();
        known.Identifier.ShouldBe("Photodetector");
        known.PdkSource.ShouldBe("Demo PDK");
    }

    [Fact]
    public void Export_WithoutLibrary_FallsBackToInstanceDisplayName()
    {
        // No library at the export call site: the wrapper cell keeps the
        // historical display-name naming (a renamed copy then names its cell
        // after itself — the pre-fix behavior, retained as the fallback).
        var templates = TestPdkLoader.LoadAllTemplates();
        var template = templates.First(t => t.Name == "Photodetector" && t.PdkSource == "Demo PDK");
        var canvas = new DesignCanvasViewModel();
        var copy = ComponentTemplates.CreateFromTemplate(template, 100, 100);
        copy.Identifier = "Photodetector_2";
        copy.HumanReadableName = "Photodetector_2";
        canvas.AddComponent(copy, template.Name, template.PdkSource);

        var script = new SimpleNazcaExporter().Export(canvas);

        script.ShouldContain("with nd.Cell(name='Photodetector_2')");
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }
}
