using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.ComponentSettings;
using CAP_DataAccess.Persistence.PIR;
using Moq;
using Shouldly;
using UnitTests;
using Xunit;

namespace UnitTests.ComponentSettings;

/// <summary>
/// Tests for <see cref="ComponentSettingsDialogViewModel"/>.
/// Verifies S-matrix entry display, deletion, configuration, and live-component application.
/// </summary>
public class ComponentSettingsDialogViewModelTests
{
    private static ComponentSettingsDialogViewModel NewVm() =>
        new(Mock.Of<IFileDialogService>());

    private static Dictionary<string, ComponentSMatrixData> MakeStore(
        string key,
        params string[] wavelengthNms)
    {
        var data = new ComponentSMatrixData { SourceNote = "Test" };

        foreach (var wl in wavelengthNms)
        {
            data.Wavelengths[wl] = new SMatrixWavelengthEntry
            {
                Rows = 2,
                Cols = 2,
                Real = new List<double> { 0.9, 0.1, 0.1, 0.9 },
                Imag = new List<double> { 0, 0, 0, 0 },
                PortNames = new List<string> { "port1", "port2" }
            };
        }

        return new Dictionary<string, ComponentSMatrixData> { [key] = data };
    }

    [Fact]
    public void Configure_WithExistingEntries_PopulatesSMatrixEntries()
    {
        var store = MakeStore("comp_1", "1550", "1310");
        var vm = NewVm();

        vm.Configure("comp_1", "comp_1", "My Component", store);

        vm.SMatrixEntries.Count.ShouldBe(2);
        vm.HasSMatrices.ShouldBeTrue();
        vm.Title.ShouldContain("My Component");
    }

    [Fact]
    public void Configure_WithNoMatchingKey_EmptyEntries()
    {
        var store = MakeStore("comp_1", "1550");
        var vm = NewVm();

        vm.Configure("comp_99", "comp_99", "Unknown", store);

        vm.SMatrixEntries.Count.ShouldBe(0);
        vm.HasSMatrices.ShouldBeFalse();
    }

    [Fact]
    public void Configure_DoesNotInvokeOnChangedOnInitialOpen()
    {
        // Initial Configure should not fire onChanged — observers (e.g. hierarchy panel
        // refresh) should react only to actual mutations from import/delete, not to
        // every dialog-open. Pinned to prevent regressing this hot-path optimisation.
        var store = MakeStore("comp_1", "1550");
        var vm = NewVm();
        int callCount = 0;

        vm.Configure("comp_1", "comp_1", "My Component", store, onChanged: () => callCount++);

        callCount.ShouldBe(0);
    }

    [Fact]
    public void DeleteEntryCommand_RemovesSpecificWavelength_KeepsOther()
    {
        var store = MakeStore("comp_1", "1550", "1310");
        var vm = NewVm();
        vm.Configure("comp_1", "comp_1", "My Component", store);

        var entryToDelete = vm.SMatrixEntries.First();
        var deletedKey = entryToDelete.WavelengthKey;
        var keptKey = vm.SMatrixEntries.Skip(1).First().WavelengthKey;

        vm.DeleteEntryCommand.Execute(entryToDelete);

        vm.SMatrixEntries.Count.ShouldBe(1);
        vm.SMatrixEntries[0].WavelengthKey.ShouldBe(keptKey);
        store["comp_1"].Wavelengths.ShouldNotContainKey(deletedKey);
        vm.StatusText.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void DeleteEntryCommand_LastEntry_RemovesKeyFromStore()
    {
        var store = MakeStore("comp_1", "1550");
        var vm = NewVm();
        vm.Configure("comp_1", "comp_1", "My Component", store);

        vm.DeleteEntryCommand.Execute(vm.SMatrixEntries[0]);

        store.ContainsKey("comp_1").ShouldBeFalse();
        vm.HasSMatrices.ShouldBeFalse();
    }

    [Fact]
    public void DeleteEntryCommand_LiveComponent_RemovesFromWavelengthMap()
    {
        // Symmetric with the import path: a deletion in the dialog must also be
        // reflected in the live component's WaveLengthToSMatrixMap, otherwise the
        // user clicks Delete, runs a simulation, and still gets the stale override.
        var store = MakeStore("comp_1", "1550");
        var liveComponent = TestComponentFactory.CreateSimpleTwoPortComponent();
        liveComponent.Identifier = "comp_1";
        // Pre-apply the override so the wavelength map contains 1550 nm.
        CAP.Avalonia.Services.SMatrixOverrideApplicator.Apply(liveComponent, store["comp_1"]);
        liveComponent.WaveLengthToSMatrixMap.ShouldContainKey(1550);

        var vm = NewVm();
        vm.Configure("comp_1", "comp_1", "My Component", store, liveComponent);

        vm.DeleteEntryCommand.Execute(vm.SMatrixEntries[0]);

        liveComponent.WaveLengthToSMatrixMap.ShouldNotContainKey(1550);
    }

    [Fact]
    public void Configure_RecalledTwice_ReflectsLatestStore()
    {
        var store = MakeStore("comp_1", "1550");
        var vm = NewVm();
        vm.Configure("comp_1", "comp_1", "My Component", store);

        store["comp_1"].Wavelengths["980"] = new SMatrixWavelengthEntry
        {
            Rows = 2, Cols = 2,
            Real = new List<double> { 0.8, 0.2, 0.2, 0.8 },
            Imag = new List<double> { 0, 0, 0, 0 }
        };

        vm.Configure("comp_1", "comp_1", "My Component", store);

        vm.SMatrixEntries.Count.ShouldBe(2);
    }

    [Fact]
    public void SMatrixEntryViewModel_MagnitudePreview_ShowsStrongestCouplings()
    {
        // Off-diagonal couplings (the engineering-meaningful values) — diagonals
        // are reflections ≈0 for passive devices, which is why we don't preview them.
        // Real layout: row-major, S[r=out, c=in].
        // S(out=0, in=0) = 0.05 reflection at port 1
        // S(out=0, in=1) = 0.95 transmission Port 2 → Port 1
        // S(out=1, in=0) = 0.95 transmission Port 1 → Port 2
        // S(out=1, in=1) = 0.05 reflection at port 2
        var entry = new SMatrixWavelengthEntry
        {
            Rows = 2,
            Cols = 2,
            Real = new List<double> { 0.05, 0.95, 0.95, 0.05 },
            Imag = new List<double> { 0, 0, 0, 0 },
            PortNames = new List<string> { "port1", "port2" }
        };

        var vm = new SMatrixEntryViewModel("1550", entry, "TestSource");

        vm.WavelengthLabel.ShouldBe("1550 nm");
        vm.Dimensions.ShouldBe("2 × 2");
        vm.PortNamesDisplay.ShouldBe("port1, port2");
        vm.MagnitudePreview.ShouldContain("P1→P2=0.950");
        vm.MagnitudePreview.ShouldContain("P2→P1=0.950");
        vm.MagnitudePreview.ShouldNotContain("|S11|");
        vm.SourceNote.ShouldBe("TestSource");
    }

    [Fact]
    public void SMatrixEntryViewModel_NoPortNames_ShowsFallbackText()
    {
        var entry = new SMatrixWavelengthEntry
        {
            Rows = 2,
            Cols = 2,
            Real = new List<double> { 1, 0, 0, 1 },
            Imag = new List<double> { 0, 0, 0, 0 }
        };

        var vm = new SMatrixEntryViewModel("1310", entry, null);

        vm.PortNamesDisplay.ShouldBe("(no port names)");
        vm.SourceNote.ShouldBeNull();
    }

    [Fact]
    public void LoadFromFile_UserCancels_NoMutationNoCrash()
    {
        // FileDialogService returns null when the user cancels — the command must exit
        // cleanly without touching the store or status text.
        var store = MakeStore("comp_1", "1550");
        var fileDialog = new Mock<IFileDialogService>();
        fileDialog
            .Setup(s => s.ShowOpenFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((string?)null);
        var vm = new ComponentSettingsDialogViewModel(fileDialog.Object);
        vm.Configure("comp_1", "comp_1", "My Component", store);
        var entryCountBefore = vm.SMatrixEntries.Count;

        vm.LoadFromFileCommand.Execute(null);

        vm.SMatrixEntries.Count.ShouldBe(entryCountBefore);
        vm.IsImporting.ShouldBeFalse();
    }

    [Fact]
    public void LoadFromFile_UnsupportedExtension_ReportsInStatus()
    {
        var store = MakeStore("comp_1", "1550");
        var tmp = Path.Combine(Path.GetTempPath(), $"unsupp_{Guid.NewGuid()}.unsupported");
        File.WriteAllText(tmp, "junk");
        try
        {
            var fileDialog = new Mock<IFileDialogService>();
            fileDialog
                .Setup(s => s.ShowOpenFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(tmp);
            var vm = new ComponentSettingsDialogViewModel(fileDialog.Object);
            vm.Configure("comp_1", "comp_1", "My Component", store);

            vm.LoadFromFileCommand.Execute(null);

            vm.StatusText.ShouldContain("Unsupported");
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}
