using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Core;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

/// <summary>
/// Persistence and flow tests for <c>outlinePolygons</c>: the field must survive
/// the PDK JSON round-trip and reach the placed <see cref="Component"/> through
/// template conversion, while staying optional for all existing PDKs.
/// </summary>
public class OutlinePolygonPersistenceTests
{
    private const string PdkJsonWithOutlines = """
        {
          "fileFormatVersion": 1,
          "name": "GDS Import PDK",
          "components": [
            {
              "name": "Imported Cell",
              "category": "Imported",
              "nazcaFunction": "nazca_imported_cell",
              "widthMicrometers": 20,
              "heightMicrometers": 10,
              "nazcaOriginOffsetX": 0,
              "nazcaOriginOffsetY": 5,
              "outlinePolygons": [
                {
                  "layer": 1,
                  "dataType": 0,
                  "points": [
                    { "x": 0,  "y": 4 },
                    { "x": 20, "y": 4 },
                    { "x": 20, "y": 6 },
                    { "x": 0,  "y": 6 },
                    { "x": 0,  "y": 4 }
                  ]
                }
              ],
              "pins": [
                { "name": "a0", "offsetXMicrometers": 0,  "offsetYMicrometers": 5, "angleDegrees": 180 },
                { "name": "b0", "offsetXMicrometers": 20, "offsetYMicrometers": 5, "angleDegrees": 0 }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void PdkLoader_parses_outline_polygons_from_json()
    {
        var pdk = new PdkLoader().LoadFromJson(PdkJsonWithOutlines);

        var outlines = pdk.Components[0].OutlinePolygons;
        outlines.ShouldNotBeNull();
        var polygon = outlines.ShouldHaveSingleItem();
        polygon.Layer.ShouldBe(1);
        polygon.DataType.ShouldBe(0);
        polygon.Points.Count.ShouldBe(5);
        polygon.Points[1].ShouldBe(new OutlinePoint(20, 4));
        // Closed ring: first point repeated at the end.
        polygon.Points[^1].ShouldBe(polygon.Points[0]);
    }

    [Fact]
    public void PdkLoader_tolerates_missing_outline_polygons()
    {
        const string json = """
            {
              "fileFormatVersion": 1,
              "name": "Legacy PDK",
              "components": [
                {
                  "name": "Legacy Cell",
                  "nazcaFunction": "pdk.cell",
                  "widthMicrometers": 10,
                  "heightMicrometers": 5,
                  "nazcaOriginOffsetX": 0,
                  "nazcaOriginOffsetY": 2.5,
                  "pins": [ { "name": "a0", "offsetXMicrometers": 0, "offsetYMicrometers": 2.5, "angleDegrees": 180 } ]
                }
              ]
            }
            """;

        var pdk = new PdkLoader().LoadFromJson(json);

        pdk.Components[0].OutlinePolygons.ShouldBeNull();
    }

    [Fact]
    public void OutlinePolygons_roundtrip_through_saver_and_loader()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lunima-outlines-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "p.json");
        try
        {
            var pdk = new PdkDraft
            {
                Name = "P",
                Components = new()
                {
                    new PdkComponentDraft
                    {
                        Name = "Cell", WidthMicrometers = 20, HeightMicrometers = 10,
                        Pins = new() { new PhysicalPinDraft { Name = "a0" } },
                        OutlinePolygons = new()
                        {
                            new OutlinePolygon
                            {
                                Layer = 3, DataType = 1,
                                Points = new[]
                                {
                                    new OutlinePoint(0, 0), new OutlinePoint(20, 0),
                                    new OutlinePoint(20, 10), new OutlinePoint(0, 0)
                                }
                            }
                        }
                    }
                }
            };

            new PdkJsonSaver().SaveToFile(pdk, path);
            var reloaded = new PdkLoader().LoadFromFileForEditing(path);

            var polygon = reloaded.Components[0].OutlinePolygons.ShouldNotBeNull().ShouldHaveSingleItem();
            polygon.Layer.ShouldBe(3);
            polygon.DataType.ShouldBe(1);
            polygon.Points.Count.ShouldBe(4);
            polygon.Points[2].ShouldBe(new OutlinePoint(20, 10));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Saver_omits_outline_polygons_when_null()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lunima-outlines-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "p.json");
        try
        {
            var pdk = new PdkDraft
            {
                Name = "P",
                Components = new()
                {
                    new PdkComponentDraft
                    {
                        Name = "Cell", WidthMicrometers = 10, HeightMicrometers = 5,
                        Pins = new() { new PhysicalPinDraft { Name = "a0" } }
                    }
                }
            };

            new PdkJsonSaver().SaveToFile(pdk, path);

            File.ReadAllText(path).ShouldNotContain("outlinePolygons");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ConvertToTemplate_carries_outlines_onto_the_template()
    {
        var template = PdkTemplateConverter.ConvertToTemplate(BuildDraftWithOutlines(), "P", null);

        var polygon = template.OutlinePolygons.ShouldNotBeNull().ShouldHaveSingleItem();
        polygon.Points.Count.ShouldBe(4);
    }

    [Fact]
    public void CreateFromTemplate_carries_outlines_onto_the_component()
    {
        var template = PdkTemplateConverter.ConvertToTemplate(BuildDraftWithOutlines(), "P", null);

        var component = ComponentTemplates.CreateFromTemplate(template, 100, 50);

        // Same immutable list instance — the renderer's geometry cache keys on it.
        component.OutlinePolygons.ShouldBeSameAs(template.OutlinePolygons);
    }

    [Fact]
    public void Clone_preserves_outline_polygons()
    {
        var template = PdkTemplateConverter.ConvertToTemplate(BuildDraftWithOutlines(), "P", null);
        var component = ComponentTemplates.CreateFromTemplate(template, 100, 50);

        var clone = (Component)component.Clone();

        clone.OutlinePolygons.ShouldBeSameAs(component.OutlinePolygons);
    }

    [Fact]
    public void CreateFromTemplate_without_outlines_leaves_component_without_outlines()
    {
        var draft = new PdkComponentDraft
        {
            Name = "Plain Cell", WidthMicrometers = 10, HeightMicrometers = 5,
            NazcaFunction = "pdk.plain",
            Pins = new() { new PhysicalPinDraft { Name = "a0" } }
        };
        var template = PdkTemplateConverter.ConvertToTemplate(draft, "P", null);

        var component = ComponentTemplates.CreateFromTemplate(template, 0, 0);

        // Fallback condition in ComponentRenderer is OutlinePolygons is { Count: > 0 }.
        component.OutlinePolygons.ShouldBeNull();
    }

    private static PdkComponentDraft BuildDraftWithOutlines() => new()
    {
        Name = "Imported Cell",
        WidthMicrometers = 20,
        HeightMicrometers = 10,
        NazcaFunction = "nazca_imported_cell",
        Pins = new()
        {
            new PhysicalPinDraft { Name = "a0", OffsetXMicrometers = 0, OffsetYMicrometers = 5, AngleDegrees = 180 },
            new PhysicalPinDraft { Name = "b0", OffsetXMicrometers = 20, OffsetYMicrometers = 5, AngleDegrees = 0 }
        },
        OutlinePolygons = new()
        {
            new OutlinePolygon
            {
                Layer = 1,
                DataType = 0,
                Points = new[]
                {
                    new OutlinePoint(0, 4), new OutlinePoint(20, 4),
                    new OutlinePoint(20, 6), new OutlinePoint(0, 4)
                }
            }
        }
    };
}
