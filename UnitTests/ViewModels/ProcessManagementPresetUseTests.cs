using System.Collections.Generic;
using System.Linq;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels;
using CAP.Avalonia.ViewModels.Process;
using CAP_Core.Components.Process;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.ViewModels;

/// <summary>
/// Verifies "Use preset" USE-semantics in the Fabrication Process window (issue #696):
/// picking a preset sets it as the design's active process, edits become design-only
/// overrides, and a reopened dialog restores the preset + overrides state.
/// </summary>
public class ProcessManagementPresetUseTests
{
    private static ProcessManagementViewModel CreateVm() => new(Mock.Of<IFileDialogService>());

    private static PdkDraft SinPreset() => new()
    {
        Name = "CornerStone SiN",
        DefaultWavelengthNm = 1550,
        Process = new ProcessDefinition
        {
            Name = "CornerStone SiN 300nm",
            CoreThicknessNm = 300,
            Layers = { new ProcessLayer { Name = "NITRIDE", Layer = 203 } },
            Xsections = { new ProcessXsection { Name = "xs_nc", Kind = XsectionKind.Optical, WidthUm = 1.2 } },
            Materials =
            {
                new ProcessMaterial { Name = "SiN", Role = "core" },
                new ProcessMaterial { Name = "SiO2", Role = "cladding" },
            },
        },
    };

    [Fact]
    public void PickingAPreset_SetsItAsTheDesignsActiveProcess()
    {
        var vm = CreateVm();
        var preset = SinPreset();
        vm.SetAvailablePresets(new[] { preset });
        ActiveProcessSelection? applied = null;
        string? appliedPdkName = null;
        vm.UseAsDesignProcess = (selection, pdkName) => { applied = selection; appliedPdkName = pdkName; };

        vm.SelectedPreset = preset;

        applied.ShouldNotBeNull("picking a preset must USE it as the design's process, not just edit it");
        applied!.IsPlayground.ShouldBeFalse();
        applied.DisplayName.ShouldBe("CornerStone SiN 300nm");
        applied.MemberPdkNames.ShouldBe(new[] { "CornerStone SiN" });
        appliedPdkName.ShouldBe("CornerStone SiN");
        vm.IsPresetInUse.ShouldBeTrue();
        vm.PresetStateText.ShouldContain("CornerStone SiN");
        vm.StatusText.ShouldContain("Now using preset");
    }

    [Fact]
    public void PickingAPreset_ShowsItsProcessInTheEditor()
    {
        var vm = CreateVm();
        var preset = SinPreset();
        vm.SetAvailablePresets(new[] { preset });

        vm.SelectedPreset = preset;

        vm.HasProcess.ShouldBeTrue();
        vm.ProcessName.ShouldBe("CornerStone SiN 300nm");
        vm.Layers.ShouldContain(l => l.Name == "NITRIDE");
        vm.Xsections.ShouldContain(x => x.Name == "xs_nc");
    }

    [Fact]
    public void EditingAField_CommitsADesignOverride_AndUpdatesTheBanner()
    {
        var vm = CreateVm();
        var preset = SinPreset();
        vm.SetAvailablePresets(new[] { preset });
        IReadOnlyList<ProcessPropertyOverrideData>? committed = null;
        vm.CommitOverrides = (_, overrides) => committed = overrides;
        vm.SelectedPreset = preset;

        vm.Xsections.Single(x => x.Name == "xs_nc").WidthUm = 1.5;
        vm.RefreshOverrideSummary();

        var o = committed.ShouldNotBeNull().ShouldHaveSingleItem();
        o.RowName.ShouldBe("xs_nc");
        o.Property.ShouldBe(nameof(ProcessXsection.WidthUm));
        o.Value.ShouldBe("1.5");
        vm.PresetStateText.ShouldContain("1 property overridden");
        // The preset's own definition must stay untouched (USE, not edit).
        preset.Process!.Xsections.Single().WidthUm.ShouldBe(1.2);
    }

    [Fact]
    public void RefreshOverrideSummary_NothingChanged_DoesNotRecommit()
    {
        var vm = CreateVm();
        var preset = SinPreset();
        vm.SetAvailablePresets(new[] { preset });
        var commits = 0;
        vm.CommitOverrides = (_, _) => commits++;
        vm.SelectedPreset = preset;

        vm.RefreshOverrideSummary();
        vm.RefreshOverrideSummary();

        commits.ShouldBe(0, "an unchanged editor must not re-commit (and re-dirty) the design");
    }

    [Fact]
    public void RefreshOverrideSummary_WithoutPreset_IsANoOp()
    {
        var vm = CreateVm();
        var active = new ActiveProcessSelection(
            "SOI", new ProcessFingerprint("Si", 220, "SiO2", 1550, "SOI"),
            new List<string> { "PdkA" }, IsPlayground: false);
        vm.ShowActiveProcess(active, new List<PdkDraft> { SinPreset() });
        var commits = 0;
        vm.CommitOverrides = (_, _) => commits++;

        vm.RefreshOverrideSummary();

        commits.ShouldBe(0);
        vm.IsPresetInUse.ShouldBeFalse();
    }

    [Fact]
    public void Reopen_WithStoredPresetAndOverrides_RestoresEffectiveStateAndBanner()
    {
        var vm = CreateVm();
        var preset = SinPreset();
        var selection = new ActiveProcessSelection(
            "CornerStone SiN 300nm", new ProcessFingerprint("SiN", 300, "SiO2", 1550, "CornerStone SiN 300nm"),
            new List<string> { "CornerStone SiN" }, IsPlayground: false);
        var overrides = new List<ProcessPropertyOverrideData>
        {
            new()
            {
                Section = ProcessPropertyOverrideData.XsectionsSection, RowName = "xs_nc",
                Property = nameof(ProcessXsection.WidthUm), Value = "1.5",
            },
        };

        vm.ShowActiveProcess(selection, new List<PdkDraft> { preset }, "CornerStone SiN", overrides);

        vm.HasProcess.ShouldBeTrue();
        vm.ProcessName.ShouldBe("CornerStone SiN 300nm");
        vm.Xsections.Single(x => x.Name == "xs_nc").WidthUm.ShouldBe(1.5, "the editor shows the EFFECTIVE process");
        vm.IsPresetInUse.ShouldBeTrue();
        vm.PresetStateText.ShouldContain("1 property overridden");
    }

    [Fact]
    public void Reopen_ThenRefreshWithoutEdits_DoesNotRecommitStoredOverrides()
    {
        var vm = CreateVm();
        var preset = SinPreset();
        var selection = new ActiveProcessSelection(
            "CornerStone SiN 300nm", new ProcessFingerprint("SiN", 300, "SiO2", 1550, "CornerStone SiN 300nm"),
            new List<string> { "CornerStone SiN" }, IsPlayground: false);
        var overrides = new List<ProcessPropertyOverrideData>
        {
            new()
            {
                Section = ProcessPropertyOverrideData.XsectionsSection, RowName = "xs_nc",
                Property = nameof(ProcessXsection.WidthUm), Value = "1.5",
            },
        };
        var commits = 0;
        vm.CommitOverrides = (_, _) => commits++;

        vm.ShowActiveProcess(selection, new List<PdkDraft> { preset }, "CornerStone SiN", overrides);
        vm.RefreshOverrideSummary();

        commits.ShouldBe(0, "reopening a dialog without edits must not re-dirty the design");
    }
}
