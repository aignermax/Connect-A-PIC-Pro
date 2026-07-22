using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.ComponentSettings;
using CAP_DataAccess.Persistence.PIR;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.ComponentSettings;

/// <summary>
/// End-to-end import tests that drive the dialog VM through its
/// <c>LoadFromFileCommand</c> with the real reference files in
/// <c>Tools/sparam-data/</c>. The synthetic VM tests in
/// <see cref="ComponentSettingsDialogViewModelTests"/> stub the importer; these
/// tests exercise the actual importer + converter + VM chain so a parser break
/// or VM wiring break that only manifests on real-world Lumerical files is
/// caught here instead of by the user.
///
/// Pinning bdc_te1550.sparam: the 4-port BDC file caused a crash during manual
/// testing where the dialog populated entries but the AXAML failed to render
/// due to a namespace-resolution issue in the delete button's binding cast.
/// The XAML fix lives in <c>ComponentSettingsDialog.axaml</c>; this test
/// guards the data path so we can be sure the VM ends up in a state the view
/// can actually render.
/// </summary>
public class ComponentSettingsDialogImportIntegrationTests
{
    // Status strings are now localized; pin English so assertions are locale-independent (runner is de_DE).
    public ComponentSettingsDialogImportIntegrationTests() =>
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);

    private static readonly string DataDir = FindRepoRelative("Tools", "sparam-data");

    [Fact]
    public async Task LoadFromFile_BdcTe1550_4PortFile_PopulatesEntriesAndStore()
    {
        var path = Path.Combine(DataDir, "bdc_te1550.sparam");
        File.Exists(path).ShouldBeTrue($"Reference file missing: {path}");

        var store = new Dictionary<string, ComponentSMatrixData>();
        var fileDialog = new Mock<IFileDialogService>();
        fileDialog
            .Setup(s => s.ShowOpenFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(path);

        var vm = new ComponentSettingsDialogViewModel(fileDialog.Object);
        vm.Configure("comp_bdc", "comp_bdc", "BDC TE 1550", store);

        await vm.LoadFromFileCommand.ExecuteAsync(null);

        // Import must populate the store under the entity key — the dialog
        // is wired against a live store and downstream consumers (project
        // save, override applicator) rely on the entry being present.
        store.ShouldContainKey("comp_bdc");
        var data = store["comp_bdc"];
        data.Wavelengths.ShouldNotBeEmpty();

        // 4-port BDC: every wavelength entry must be a 4×4 matrix with the
        // import-supplied port names so SMatrixOverrideApplicator can map
        // ports unambiguously without falling back to positional matching.
        foreach (var (_, entry) in data.Wavelengths)
        {
            entry.Rows.ShouldBe(4);
            entry.Cols.ShouldBe(4);
            entry.Real.Count.ShouldBe(16);
            entry.Imag.Count.ShouldBe(16);
            entry.PortNames.ShouldNotBeNull();
            entry.PortNames!.Count.ShouldBe(4);
        }

        // Dialog VM mirrors the store after import — these are the entries
        // the AXAML's ItemsControl will iterate. Empty here means the user
        // sees an empty dialog after a "successful" import (silent failure).
        vm.SMatrixEntries.Count.ShouldBe(data.Wavelengths.Count);
        vm.HasSMatrices.ShouldBeTrue();
        vm.IsImporting.ShouldBeFalse();
        vm.StatusText.ShouldContain("Imported");

        // Each entry must carry the data the delete-button row renders so the
        // AXAML binding paths (Dimensions, MagnitudePreview, PortNamesDisplay)
        // never hit nulls and the deferred DataTemplate build can complete.
        foreach (var entryVm in vm.SMatrixEntries)
        {
            entryVm.WavelengthLabel.ShouldNotBeNullOrEmpty();
            entryVm.Dimensions.ShouldBe("4 × 4");
            entryVm.PortNamesDisplay.ShouldNotBeNullOrEmpty();
            entryVm.MagnitudePreview.ShouldNotBeNullOrEmpty();
        }
    }

    [Fact]
    public async Task LoadFromFile_BdcTe1550_DeleteCommand_RemovesSingleEntry()
    {
        // Symmetric guard: the delete button is what the AXAML binding fix
        // touches. After a real import, deleting one wavelength must remove
        // exactly that entry from the store and the VM's collection.
        var path = Path.Combine(DataDir, "bdc_te1550.sparam");
        File.Exists(path).ShouldBeTrue($"Reference file missing: {path}");

        var store = new Dictionary<string, ComponentSMatrixData>();
        var fileDialog = new Mock<IFileDialogService>();
        fileDialog
            .Setup(s => s.ShowOpenFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(path);

        var vm = new ComponentSettingsDialogViewModel(fileDialog.Object);
        vm.Configure("comp_bdc", "comp_bdc", "BDC TE 1550", store);
        await vm.LoadFromFileCommand.ExecuteAsync(null);

        var totalBefore = vm.SMatrixEntries.Count;
        totalBefore.ShouldBeGreaterThan(1, "test needs at least 2 entries to verify selective deletion");

        var entryToDelete = vm.SMatrixEntries[0];
        var deletedKey = entryToDelete.WavelengthKey;

        vm.DeleteEntryCommand.Execute(entryToDelete);

        vm.SMatrixEntries.Count.ShouldBe(totalBefore - 1);
        store["comp_bdc"].Wavelengths.ShouldNotContainKey(deletedKey);
    }

    private static string FindRepoRelative(params string[] segments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Tools", "sparam-data")))
        {
            dir = dir.Parent;
        }
        if (dir == null) throw new InvalidOperationException("Could not locate repository root");
        return Path.Combine(new[] { dir.FullName }.Concat(segments).ToArray());
    }
}
