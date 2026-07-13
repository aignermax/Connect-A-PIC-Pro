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
/// Regression tests for issue #726: the Fabrication Process editor must be able to SWITCH the
/// design's process selection (not just edit the definition), and must show the currently
/// active process preselected in the preset dropdown when reopened.
/// </summary>
public class ProcessManagementDesignProcessTests
{
    private static ProcessManagementViewModel CreateVm() =>
        new(Mock.Of<IFileDialogService>());

    private static PdkDraft SoiDraft(string name, string processName = "SOI-220", double thicknessNm = 220) =>
        new()
        {
            Name = name,
            DefaultWavelengthNm = 1550,
            Process = new ProcessDefinition
            {
                Name = processName,
                CoreThicknessNm = thicknessNm,
                Layers = { new ProcessLayer { Name = "WAVEGUIDE", Layer = 12 } },
                Materials =
                {
                    new ProcessMaterial { Name = "Si", Role = "core" },
                    new ProcessMaterial { Name = "SiO2", Role = "cladding" },
                },
            },
        };

    private static ProcessManagementViewModel OpenWithNoActiveProcess(
        IReadOnlyList<PdkDraft> pdks, out List<ActiveProcessSelection> applied)
    {
        var vm = CreateVm();
        var captured = new List<ActiveProcessSelection>();
        vm.ApplyActiveProcess = captured.Add;
        vm.ShowActiveProcess(active: null, pdks);
        applied = captured;
        return vm;
    }

    [Fact]
    public void SetAsDesignProcess_EmptyCanvas_AppliesSelectedPresetAsActiveProcess()
    {
        var vm = OpenWithNoActiveProcess(new List<PdkDraft> { SoiDraft("PdkA") }, out var applied);
        vm.SelectedPreset = vm.AvailablePresets.Single();

        vm.SetAsDesignProcessCommand.Execute(null);

        var selection = applied.ShouldHaveSingleItem();
        selection.IsPlayground.ShouldBeFalse();
        selection.DisplayName.ShouldBe("SOI-220");
        selection.MemberPdkNames.ShouldBe(new[] { "PdkA" });
        selection.Fingerprint.ShouldNotBeNull();
        selection.Fingerprint!.CoreMaterial.ShouldBe("Si");
        vm.StatusText.ShouldContain("SOI-220");
    }

    [Fact]
    public void SetAsDesignProcess_CompatiblePdks_JoinTheSameProcessLikeTheNewDesignPicker()
    {
        var pdks = new List<PdkDraft> { SoiDraft("PdkA"), SoiDraft("PdkB") };
        var vm = OpenWithNoActiveProcess(pdks, out var applied);
        vm.SelectedPreset = vm.AvailablePresets.First(p => p.Name == "PdkA");

        vm.SetAsDesignProcessCommand.Execute(null);

        var selection = applied.ShouldHaveSingleItem();
        selection.MemberPdkNames.ShouldBe(new[] { "PdkA", "PdkB" }, ignoreOrder: true);
    }

    [Fact]
    public void SetAsDesignProcess_ComponentsPlaced_RefusesAndExplains()
    {
        var vm = OpenWithNoActiveProcess(new List<PdkDraft> { SoiDraft("PdkA") }, out var applied);
        vm.PlacedComponentCountProvider = () => 3;
        vm.SelectedPreset = vm.AvailablePresets.Single();

        vm.SetAsDesignProcessCommand.Execute(null);

        applied.ShouldBeEmpty();
        vm.StatusText.ShouldContain("3 component(s)");
    }

    [Fact]
    public void SetAsDesignProcess_NoPresetPicked_ExplainsInsteadOfApplying()
    {
        var vm = OpenWithNoActiveProcess(new List<PdkDraft> { SoiDraft("PdkA") }, out var applied);

        vm.SetAsDesignProcessCommand.Execute(null);

        applied.ShouldBeEmpty();
        vm.StatusText.ShouldContain("preset dropdown");
    }

    [Fact]
    public void SetAsDesignProcess_NoApplyCallbackWired_ExplainsInsteadOfFailingSilently()
    {
        var vm = CreateVm();
        vm.ShowActiveProcess(active: null, new List<PdkDraft> { SoiDraft("PdkA") });
        vm.SelectedPreset = vm.AvailablePresets.Single();

        vm.SetAsDesignProcessCommand.Execute(null);

        vm.StatusText.ShouldContain("not available");
    }

    [Fact]
    public void SetAsDesignProcess_ProcessAgnosticPreset_FallsBackToSingletonSelection()
    {
        var toolPdk = SoiDraft("Tools");
        toolPdk.ProcessAgnostic = true;
        var vm = OpenWithNoActiveProcess(new List<PdkDraft> { toolPdk }, out var applied);
        vm.SelectedPreset = vm.AvailablePresets.Single();

        vm.SetAsDesignProcessCommand.Execute(null);

        var selection = applied.ShouldHaveSingleItem();
        selection.MemberPdkNames.ShouldBe(new[] { "Tools" });
        selection.IsPlayground.ShouldBeFalse();
    }

    [Fact]
    public void ShowActiveProcess_LockedProcess_PreselectsActivePresetInDropdown()
    {
        var vm = CreateVm();
        var pdks = new List<PdkDraft> { SoiDraft("PdkA"), SoiDraft("Other", "SiN-300", 300) };
        var active = new ActiveProcessSelection(
            "SOI-220", new ProcessFingerprint("Si", 220, "SiO2", 1550, "SOI-220"),
            new List<string> { "PdkA" }, IsPlayground: false);

        vm.ShowActiveProcess(active, pdks);

        vm.SelectedPreset.ShouldNotBeNull();
        vm.SelectedPreset!.Name.ShouldBe("PdkA");
        // Preselecting must not clobber the merged state with a preset reload.
        vm.ProcessName.ShouldBe("SOI-220");
        vm.Layers.Single().Name.ShouldBe("WAVEGUIDE");
    }

    [Fact]
    public void ShowActiveProcess_Reopened_ClearsStalePresetSelection()
    {
        var vm = CreateVm();
        var pdks = new List<PdkDraft> { SoiDraft("PdkA") };
        var active = new ActiveProcessSelection(
            "SOI-220", new ProcessFingerprint("Si", 220, "SiO2", 1550, "SOI-220"),
            new List<string> { "PdkA" }, IsPlayground: false);
        vm.ShowActiveProcess(active, pdks);

        vm.ShowActiveProcess(ActiveProcessSelection.Playground(), pdks);

        vm.SelectedPreset.ShouldBeNull();
    }
}
