using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Library;
using CAP_Core.Components.Core;
using CAP_Core.Components.PinKinds;
using CAP_Core.Components.Process;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Components.Process;

/// <summary>
/// Tests for <see cref="ProcessOpticalDefaultsResolver"/> and the waveguide
/// width/layer population in <see cref="PdkTemplateConverter.ConvertToTemplate"/>:
/// per-pin draft values win, the process' default optical cross-section fills
/// optical pins, and absent PDK data keeps the values null (DRC-lite guard).
/// </summary>
public class ProcessOpticalDefaultsResolverTests
{
    private static ProcessDefinition SoiProcess() => new()
    {
        Name = "Generic SOI",
        Layers =
        {
            new ProcessLayer { Name = "WG", Layer = 1, Datatype = 0 },
            new ProcessLayer { Name = "M1", Layer = 11, Datatype = 0 },
        },
        Xsections =
        {
            new ProcessXsection { Name = "metal", Kind = XsectionKind.Metal, WidthUm = 10, Layers = { "M1" } },
            new ProcessXsection { Name = "strip", Kind = XsectionKind.Optical, WidthUm = 0.5, Layers = { "WG" } },
        },
    };

    [Fact]
    public void Resolve_FirstOpticalXsection_ProvidesWidthAndLayer()
    {
        var (width, layer) = ProcessOpticalDefaultsResolver.Resolve(SoiProcess());

        width.ShouldBe(0.5);
        layer.ShouldBe(1);
    }

    [Fact]
    public void Resolve_MultipleOpticalXsections_FirstOneWins()
    {
        var process = SoiProcess();
        process.Xsections.Add(new ProcessXsection
        {
            Name = "rib", Kind = XsectionKind.Optical, WidthUm = 0.45, Layers = { "WG" }
        });

        var (width, _) = ProcessOpticalDefaultsResolver.Resolve(process);

        width.ShouldBe(0.5);
    }

    [Fact]
    public void Resolve_NoOpticalXsection_YieldsNulls()
    {
        var process = SoiProcess();
        process.Xsections.RemoveAll(x => x.Kind == XsectionKind.Optical);

        var (width, layer) = ProcessOpticalDefaultsResolver.Resolve(process);

        width.ShouldBeNull();
        layer.ShouldBeNull();
    }

    [Fact]
    public void Resolve_UnknownXsectionLayerName_YieldsNullLayer()
    {
        var process = SoiProcess();
        process.Xsections.First(x => x.Kind == XsectionKind.Optical).Layers.Clear();

        var (width, layer) = ProcessOpticalDefaultsResolver.Resolve(process);

        width.ShouldBe(0.5);
        layer.ShouldBeNull();
    }

    [Fact]
    public void Resolve_NullProcess_YieldsNulls()
    {
        var (width, layer) = ProcessOpticalDefaultsResolver.Resolve((ProcessDefinition?)null);

        width.ShouldBeNull();
        layer.ShouldBeNull();
    }

    [Fact]
    public void Resolve_ActiveProcessSelection_ResolvesAcrossMemberPdks()
    {
        var drafts = new List<PdkDraft> { new() { Name = "My PDK", Process = SoiProcess() } };
        var active = new ActiveProcessSelection("SOI", null, new List<string> { "My PDK" }, IsPlayground: false);

        var (width, layer) = ProcessOpticalDefaultsResolver.Resolve(active, drafts);

        width.ShouldBe(0.5);
        layer.ShouldBe(1);
    }

    [Fact]
    public void Resolve_PlaygroundSelection_YieldsNulls()
    {
        var drafts = new List<PdkDraft> { new() { Name = "My PDK", Process = SoiProcess() } };

        var (width, layer) = ProcessOpticalDefaultsResolver.Resolve(ActiveProcessSelection.Playground(), drafts);

        width.ShouldBeNull();
        layer.ShouldBeNull();
    }

    private static PdkComponentDraft ComponentDraft(params PhysicalPinDraft[] pins) => new()
    {
        Name = "Cell",
        WidthMicrometers = 10,
        HeightMicrometers = 4,
        Pins = pins.ToList(),
    };

    [Fact]
    public void ConvertToTemplate_OpticalPinWithoutValue_InheritsProcessDefault()
    {
        var draft = ComponentDraft(new PhysicalPinDraft { Name = "o1" });

        var template = PdkTemplateConverter.ConvertToTemplate(draft, "P", null, process: SoiProcess());

        template.PinDefinitions[0].WaveguideWidthMicrometers.ShouldBe(0.5);
        template.PinDefinitions[0].Layer.ShouldBe(1);
    }

    [Fact]
    public void ConvertToTemplate_PerPinValues_WinOverProcessDefault()
    {
        var draft = ComponentDraft(new PhysicalPinDraft
        {
            Name = "o1", WaveguideWidthMicrometers = 3.0, Layer = 99
        });

        var template = PdkTemplateConverter.ConvertToTemplate(draft, "P", null, process: SoiProcess());

        template.PinDefinitions[0].WaveguideWidthMicrometers.ShouldBe(3.0);
        template.PinDefinitions[0].Layer.ShouldBe(99);
    }

    [Fact]
    public void ConvertToTemplate_ElectricalPin_GetsNoProcessDefault()
    {
        var draft = ComponentDraft(new PhysicalPinDraft
        {
            Name = "e1", PinKind = PinKindHelper.ElectricalKindName
        });

        var template = PdkTemplateConverter.ConvertToTemplate(draft, "P", null, process: SoiProcess());

        template.PinDefinitions[0].Kind.ShouldBe(MatterType.Electricity);
        template.PinDefinitions[0].WaveguideWidthMicrometers.ShouldBeNull();
        template.PinDefinitions[0].Layer.ShouldBeNull();
    }

    [Fact]
    public void ConvertToTemplate_NoProcess_KeepsValuesNull()
    {
        var draft = ComponentDraft(new PhysicalPinDraft { Name = "o1" });

        var template = PdkTemplateConverter.ConvertToTemplate(draft, "P", null);

        template.PinDefinitions[0].WaveguideWidthMicrometers.ShouldBeNull();
        template.PinDefinitions[0].Layer.ShouldBeNull();
    }

    [Fact]
    public void CreateFromTemplate_StampsWidthAndLayerOnPhysicalPins()
    {
        var draft = ComponentDraft(
            new PhysicalPinDraft { Name = "o1", AngleDegrees = 180 },
            new PhysicalPinDraft { Name = "o2", AngleDegrees = 0 });
        var template = PdkTemplateConverter.ConvertToTemplate(draft, "P", null, process: SoiProcess());

        var component = ComponentTemplates.CreateFromTemplate(template, 0, 0);

        component.PhysicalPins.Count.ShouldBe(2);
        foreach (var pin in component.PhysicalPins)
        {
            pin.WaveguideWidthMicrometers.ShouldBe(0.5);
            pin.Layer.ShouldBe(1);
        }
    }
}
