using System.Collections.Generic;
using System.Linq;
using CAP.Avalonia.ViewModels.Process;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.ViewModels.Process;

/// <summary>
/// Verifies the design-override diff/apply for preset fabrication processes (issue #696):
/// unchanged presets produce no overrides, edited properties round-trip with invariant
/// formatting, and added/removed rows are represented and re-applied correctly.
/// </summary>
public class ProcessOverrideDiffTests
{
    private static ProcessDefinition Preset() => new()
    {
        Name = "SOI-220",
        Layers = { new ProcessLayer { Name = "WAVEGUIDE", Layer = 12, Datatype = 0, Description = "core" } },
        Xsections =
        {
            new ProcessXsection
            {
                Name = "STRIP", Kind = XsectionKind.Optical, WidthUm = 0.5,
                MinRadiusUm = 5, RecommendedRadiusUm = 10, Layers = { "WAVEGUIDE" },
            },
        },
        Materials = { new ProcessMaterial { Name = "Si", Role = "core" } },
    };

    [Fact]
    public void Diff_IdenticalDefinitions_ReturnsNoOverrides()
    {
        ProcessOverrideDiff.Diff(Preset(), Preset()).ShouldBeEmpty();
    }

    [Fact]
    public void Diff_EditedWidth_ReturnsSingleInvariantOverride()
    {
        var edited = Preset();
        edited.Xsections[0].WidthUm = 1.2;

        var overrides = ProcessOverrideDiff.Diff(Preset(), edited);

        var o = overrides.ShouldHaveSingleItem();
        o.Section.ShouldBe(ProcessPropertyOverrideData.XsectionsSection);
        o.RowName.ShouldBe("STRIP");
        o.Property.ShouldBe(nameof(ProcessXsection.WidthUm));
        o.Value.ShouldBe("1.2", "values must be invariant-culture formatted");
    }

    [Fact]
    public void Apply_EditedProperties_ReproducesTheEditedDefinition()
    {
        var edited = Preset();
        edited.Xsections[0].WidthUm = 1.2;
        edited.Xsections[0].Kind = XsectionKind.Metal;
        edited.Layers[0].Layer = 99;
        edited.Materials[0].Role = "cladding";

        var overrides = ProcessOverrideDiff.Diff(Preset(), edited);
        var effective = ProcessOverrideDiff.Apply(Preset(), overrides);

        effective.Xsections[0].WidthUm.ShouldBe(1.2);
        effective.Xsections[0].Kind.ShouldBe(XsectionKind.Metal);
        effective.Layers[0].Layer.ShouldBe(99);
        effective.Materials[0].Role.ShouldBe("cladding");
        ProcessOverrideDiff.Diff(edited, effective).ShouldBeEmpty("apply(diff) must reproduce the edited state");
    }

    [Fact]
    public void Apply_DoesNotMutateThePreset()
    {
        var preset = Preset();
        var overrides = new List<ProcessPropertyOverrideData>
        {
            new()
            {
                Section = ProcessPropertyOverrideData.XsectionsSection, RowName = "STRIP",
                Property = nameof(ProcessXsection.WidthUm), Value = "3.3",
            },
        };

        ProcessOverrideDiff.Apply(preset, overrides);

        preset.Xsections[0].WidthUm.ShouldBe(0.5, "the preset baseline must stay untouched");
    }

    [Fact]
    public void DiffAndApply_AddedRow_RoundTrips()
    {
        var edited = Preset();
        edited.Xsections.Add(new ProcessXsection { Name = "metal", Kind = XsectionKind.Metal, WidthUm = 10 });

        var overrides = ProcessOverrideDiff.Diff(Preset(), edited);
        overrides.ShouldContain(o => o.Property == ProcessPropertyOverrideData.RowAdded && o.RowName == "metal");

        var effective = ProcessOverrideDiff.Apply(Preset(), overrides);
        var metal = effective.Xsections.Single(x => x.Name == "metal");
        metal.Kind.ShouldBe(XsectionKind.Metal);
        metal.WidthUm.ShouldBe(10);
    }

    [Fact]
    public void DiffAndApply_RemovedRow_RoundTrips()
    {
        var edited = Preset();
        edited.Layers.Clear();

        var overrides = ProcessOverrideDiff.Diff(Preset(), edited);
        overrides.ShouldContain(o => o.Property == ProcessPropertyOverrideData.RowRemoved && o.RowName == "WAVEGUIDE");

        ProcessOverrideDiff.Apply(Preset(), overrides).Layers.ShouldBeEmpty();
    }
}
