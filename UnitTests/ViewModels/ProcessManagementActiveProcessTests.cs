using System.Collections.Generic;
using System.Linq;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels;
using CAP_Core.Components.Process;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.ViewModels;

/// <summary>
/// Verifies the Fabrication Process details dialog derives its state from the design's
/// active process selection (issue #660): locked process, Playground, no process, and
/// a locked process whose member PDKs declare no process block.
/// </summary>
public class ProcessManagementActiveProcessTests
{
    private static ProcessManagementViewModel CreateVm() =>
        new(Mock.Of<IFileDialogService>());

    private static ProcessFingerprint SoiFingerprint() =>
        new("Si", 220, "SiO2", 1550, "Generic SOI 220nm");

    private static PdkDraft Draft(string name, ProcessDefinition? process) =>
        new() { Name = name, Process = process };

    [Fact]
    public void ShowActiveProcess_LockedProcess_ShowsNameFingerprintMembersAndData()
    {
        var vm = CreateVm();
        var active = new ActiveProcessSelection(
            "Generic SOI 220nm", SoiFingerprint(), new List<string> { "PdkA", "PdkB" }, IsPlayground: false);
        var pdks = new List<PdkDraft>
        {
            Draft("PdkA", new ProcessDefinition
            {
                Name = "SOI-220",
                Layers = { new ProcessLayer { Name = "WAVEGUIDE", Layer = 12 } },
                Materials = { new ProcessMaterial { Name = "Si", Role = "core" } },
            }),
            Draft("PdkB", new ProcessDefinition
            {
                Xsections = { new ProcessXsection { Name = "STRIP", WidthUm = 0.5 } },
            }),
        };

        vm.ShowActiveProcess(active, pdks);

        vm.ProcessName.ShouldBe("Generic SOI 220nm");
        vm.HasProcess.ShouldBeTrue();
        vm.IsPlaygroundState.ShouldBeFalse();
        vm.FingerprintSummary.ShouldContain("Si");
        vm.FingerprintSummary.ShouldContain("220 nm");
        vm.FingerprintSummary.ShouldContain("SiO2");
        vm.FingerprintSummary.ShouldContain("1550 nm");
        vm.MemberPdksText.ShouldBe("PdkA, PdkB");
        vm.Layers.Single().Name.ShouldBe("WAVEGUIDE");
        vm.Xsections.Single().Name.ShouldBe("STRIP");
        vm.Materials.Single().Name.ShouldBe("Si");
        vm.StatusText.ShouldContain("2 member PDK(s)");
    }

    [Fact]
    public void ShowActiveProcess_Playground_ShowsExplicitPlaygroundState()
    {
        var vm = CreateVm();

        vm.ShowActiveProcess(ActiveProcessSelection.Playground(), new List<PdkDraft>());

        vm.IsPlaygroundState.ShouldBeTrue();
        vm.HasProcess.ShouldBeFalse();
        vm.ProcessName.ShouldBe("Playground");
        vm.StatusText.ShouldContain("not manufacturable");
    }

    [Fact]
    public void ShowActiveProcess_NoSelection_ExplainsHowToPickOne()
    {
        var vm = CreateVm();

        vm.ShowActiveProcess(null, new List<PdkDraft>());

        vm.HasProcess.ShouldBeFalse();
        vm.IsPlaygroundState.ShouldBeFalse();
        vm.StatusText.ShouldContain("No process selected");
    }

    [Fact]
    public void ShowActiveProcess_MembersWithoutProcessBlock_ShowsFingerprintOnly()
    {
        var vm = CreateVm();
        var active = new ActiveProcessSelection(
            "Generic SOI 220nm", SoiFingerprint(), new List<string> { "PdkA" }, IsPlayground: false);

        vm.ShowActiveProcess(active, new List<PdkDraft> { Draft("PdkA", process: null) });

        vm.HasProcess.ShouldBeTrue();
        vm.FingerprintSummary.ShouldContain("220 nm");
        vm.Layers.ShouldBeEmpty();
        vm.StatusText.ShouldContain("no detailed process block");
    }

    [Fact]
    public void ShowActiveProcess_MemberPdkNotLoaded_IsSkippedGracefully()
    {
        var vm = CreateVm();
        var active = new ActiveProcessSelection(
            "Generic SOI 220nm", SoiFingerprint(), new List<string> { "Missing", "PdkA" }, IsPlayground: false);
        var pdks = new List<PdkDraft>
        {
            Draft("PdkA", new ProcessDefinition { Layers = { new ProcessLayer { Name = "WG" } } }),
        };

        vm.ShowActiveProcess(active, pdks);

        vm.HasProcess.ShouldBeTrue();
        vm.Layers.Single().Name.ShouldBe("WG");
        vm.StatusText.ShouldContain("1 member PDK(s)");
    }

    [Fact]
    public void ShowActiveProcess_CalledAgain_ReflectsNewSelectionWithoutLeftovers()
    {
        var vm = CreateVm();
        var soi = new ActiveProcessSelection(
            "Generic SOI 220nm", SoiFingerprint(), new List<string> { "PdkA" }, IsPlayground: false);
        var pdks = new List<PdkDraft>
        {
            Draft("PdkA", new ProcessDefinition { Layers = { new ProcessLayer { Name = "WG" } } }),
        };
        vm.ShowActiveProcess(soi, pdks);

        vm.ShowActiveProcess(ActiveProcessSelection.Playground(), pdks);

        vm.IsPlaygroundState.ShouldBeTrue();
        vm.HasProcess.ShouldBeFalse();
        vm.Layers.ShouldBeEmpty();
        vm.FingerprintSummary.ShouldBe(string.Empty);
        vm.MemberPdksText.ShouldBe(string.Empty);
    }

    [Fact]
    public void ShowActiveProcess_NullFingerprint_ExplainsMissingFingerprint()
    {
        var vm = CreateVm();
        var active = new ActiveProcessSelection(
            "Unspecified PDK", Fingerprint: null, new List<string> { "PdkA" }, IsPlayground: false);

        vm.ShowActiveProcess(active, new List<PdkDraft> { Draft("PdkA", process: null) });

        vm.HasProcess.ShouldBeTrue();
        vm.FingerprintSummary.ShouldContain("No fingerprint");
    }
}
