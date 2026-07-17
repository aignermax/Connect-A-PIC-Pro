using System.IO;
using System.Linq;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.ViewModels;

/// <summary>
/// Verifies the Fabrication Process ViewModel loads a process into editable
/// collections and imports one from a PDK folder via the file dialog.
/// </summary>
public class ProcessManagementViewModelTests
{
    /// <summary>Status messages are localized (issue #749); pin English so substring
    /// assertions stay culture-independent regardless of the CI/dev OS language.</summary>
    public ProcessManagementViewModelTests()
    {
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
    }

    [Fact]
    public void Load_PopulatesCollectionsAndSetsHasProcess()
    {
        var vm = new ProcessManagementViewModel(Mock.Of<IFileDialogService>());
        var process = new ProcessDefinition
        {
            Name = "P1",
            Layers = { new ProcessLayer { Name = "WG", Layer = 12 } },
            Xsections = { new ProcessXsection { Name = "E1700", WidthUm = 2 } },
        };

        vm.Load(process);

        vm.ProcessName.ShouldBe("P1");
        vm.HasProcess.ShouldBeTrue();
        vm.Layers.Count.ShouldBe(1);
        vm.Xsections.Single().Name.ShouldBe("E1700");
        vm.ToProcess().Layers.Single().Layer.ShouldBe(12);
    }

    [Fact]
    public async Task ImportFromPdk_ReadsCsvTablesFromSelectedFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pmvm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "table_layers.csv"),
                "layer_name,layer,datatype,field,description\nWAVEGUIDE,12,0,Light,Passive WG\n");
            File.WriteAllText(Path.Combine(dir, "table_xsections.csv"),
                "origin,xsection,xsection_foundry,stub,description\nfab,E1700,E1700,E1700Stub,Optical\n");

            var dialog = new Mock<IFileDialogService>();
            dialog.Setup(d => d.ShowOpenFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
                  .ReturnsAsync(Path.Combine(dir, "table_layers.csv"));

            var vm = new ProcessManagementViewModel(dialog.Object);
            await vm.ImportFromPdkCommand.ExecuteAsync(null);

            vm.HasProcess.ShouldBeTrue();
            vm.Layers.Single().Name.ShouldBe("WAVEGUIDE");
            vm.Xsections.Single().Name.ShouldBe("E1700");
            vm.StatusText.ShouldContain("Imported");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Merge_AccumulatesComplementaryImports_AndEnrichesXsections()
    {
        var vm = new ProcessManagementViewModel(Mock.Of<IFileDialogService>());

        // uPDK-style: cross-section width, no layers.
        vm.Merge(new ProcessDefinition
        {
            Name = "HHI",
            Xsections = { new ProcessXsection { Name = "E1700", WidthUm = 2.3 } },
        });
        // CSV-style: layer stack + bend radius, generic width.
        vm.Merge(new ProcessDefinition
        {
            Layers = { new ProcessLayer { Name = "WAVEGUIDE", Layer = 12 } },
            Xsections = { new ProcessXsection { Name = "E1700", MinRadiusUm = 150 } },
        });

        vm.ProcessName.ShouldBe("HHI");
        vm.Layers.Count.ShouldBe(1);                 // layer added by the CSV import
        vm.Xsections.Count.ShouldBe(1);              // same cross-section, not duplicated
        var e1700 = vm.Xsections.Single();
        e1700.WidthUm.ShouldBe(2.3);                 // kept from uPDK
        e1700.MinRadiusUm.ShouldBe(150);             // enriched from CSV
    }

    [Fact]
    public async Task ImportFromPdk_Cancelled_LeavesProcessUnloaded()
    {
        var dialog = new Mock<IFileDialogService>();
        dialog.Setup(d => d.ShowOpenFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
              .ReturnsAsync((string?)null);

        var vm = new ProcessManagementViewModel(dialog.Object);
        await vm.ImportFromPdkCommand.ExecuteAsync(null);

        vm.HasProcess.ShouldBeFalse();
    }

    [Fact]
    public void SetAvailablePresets_OnlyListsPdksWithAProcessBlock()
    {
        var vm = new ProcessManagementViewModel(Mock.Of<IFileDialogService>());
        var withProcess = new PdkDraft { Name = "SiEPIC EBeam PDK", Process = new ProcessDefinition { Name = "SOI-220" } };
        var withoutProcess = new PdkDraft { Name = "Analysis Tools", Process = null };

        vm.SetAvailablePresets(new[] { withProcess, withoutProcess });

        vm.AvailablePresets.ShouldContain(withProcess);
        vm.AvailablePresets.ShouldNotContain(withoutProcess);
    }

    [Fact]
    public void SelectingAPreset_LoadsItsProcessIntoTheEditor()
    {
        var vm = new ProcessManagementViewModel(Mock.Of<IFileDialogService>());
        var preset = new PdkDraft
        {
            Name = "CornerStone SiN 300nm",
            Process = new ProcessDefinition
            {
                Name = "CornerStone SiN 300nm",
                Layers = { new ProcessLayer { Name = "NITRIDE", Layer = 203 } },
                Xsections = { new ProcessXsection { Name = "xs_nc", Kind = XsectionKind.Optical, WidthUm = 1.2 } },
            },
        };
        vm.SetAvailablePresets(new[] { preset });

        vm.SelectedPreset = preset;

        vm.HasProcess.ShouldBeTrue();
        vm.ProcessName.ShouldBe("CornerStone SiN 300nm");
        vm.Layers.ShouldContain(l => l.Name == "NITRIDE");
        vm.Xsections.ShouldContain(x => x.Name == "xs_nc");
        vm.StatusText.ShouldContain("CornerStone SiN 300nm");
    }
}
