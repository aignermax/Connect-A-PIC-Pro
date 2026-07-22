using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.ComponentSettings;
using CAP_DataAccess.Persistence.PIR;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.ComponentSettings;

/// <summary>
/// Tests for the dialog VM's port-mapping integration in the LoadFromFile flow.
/// Each test feeds a hand-crafted Lumerical-style file with generic port names
/// (<c>port 1, port 2, …</c>) and verifies the mapping path resolves correctly
/// against a component whose pins use semantic names (<c>in, out1, …</c>).
/// </summary>
public class PortMappingDialogIntegrationTests : IDisposable
{
    private readonly string _tempSparamPath;

    public PortMappingDialogIntegrationTests()
    {
        // Status strings are now localized; pin English so assertions are locale-independent (runner is de_DE).
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
        _tempSparamPath = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid()}.sparam");
        WriteThreePortSplitterFile(_tempSparamPath);
    }

    public void Dispose()
    {
        if (File.Exists(_tempSparamPath)) File.Delete(_tempSparamPath);
    }

    private static void WriteThreePortSplitterFile(string path)
    {
        // 1×2 splitter: ports 1, 2, 3. Generic names like a real Lumerical file.
        // Single wavelength is enough to exercise the mapping path.
        File.WriteAllText(path, string.Join("\n", new[]
        {
            "('port 1','TE',1,'port 1',1,'transmission')", "(1,3)", "1.93414e+14\t0.05\t0.0",
            "('port 2','TE',1,'port 1',1,'transmission')", "(1,3)", "1.93414e+14\t0.7\t0.0",
            "('port 3','TE',1,'port 1',1,'transmission')", "(1,3)", "1.93414e+14\t0.7\t0.0",
            "('port 1','TE',1,'port 2',1,'transmission')", "(1,3)", "1.93414e+14\t0.7\t0.0",
            "('port 2','TE',1,'port 2',1,'transmission')", "(1,3)", "1.93414e+14\t0.0\t0.0",
            "('port 3','TE',1,'port 2',1,'transmission')", "(1,3)", "1.93414e+14\t0.0\t0.0",
            "('port 1','TE',1,'port 3',1,'transmission')", "(1,3)", "1.93414e+14\t0.7\t0.0",
            "('port 2','TE',1,'port 3',1,'transmission')", "(1,3)", "1.93414e+14\t0.0\t0.0",
            "('port 3','TE',1,'port 3',1,'transmission')", "(1,3)", "1.93414e+14\t0.0\t0.0",
        }));
    }

    [Fact]
    public async Task LoadFromFile_ImportedNamesMatchPins_NoMappingDialogShown()
    {
        // Negative case — when the file's port names already match the pin
        // names (test fixture won't, but we simulate with a custom pin list),
        // the dialog service must not be invoked at all.
        var dialogService = new Mock<IPortMappingDialogService>(MockBehavior.Strict);

        var fileDialog = new Mock<IFileDialogService>();
        fileDialog
            .Setup(s => s.ShowOpenFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(_tempSparamPath);

        var vm = new ComponentSettingsDialogViewModel(
            fileDialog.Object,
            errorConsole: null,
            importers: null,
            portMappingDialog: dialogService.Object);

        var store = new Dictionary<string, ComponentSMatrixData>();
        // Pin list intentionally matches the imported names — no dialog needed.
        vm.Configure(
            "comp_1", "comp_1", "MyComp", store,
            availablePinNames: new[] { "port 1", "port 2", "port 3" });

        await vm.LoadFromFileCommand.ExecuteAsync(null);

        store.ShouldContainKey("comp_1");
        dialogService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LoadFromFile_ImportedNamesDontMatch_DialogReturnsMapping_AppliesIt()
    {
        // The realistic case from the bug report: a 3-port splitter file with
        // generic "port 1/2/3" names imported against a component with
        // semantic "in/out1/out2" pins. Dialog stub returns the user's mapping;
        // the resulting override must use the semantic names.
        var dialogService = new Mock<IPortMappingDialogService>();
        dialogService
            .Setup(s => s.ShowAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyList<string>>()))
            .ReturnsAsync(new Dictionary<string, string>
            {
                ["port 1"] = "in",
                ["port 2"] = "out1",
                ["port 3"] = "out2",
            });

        var fileDialog = new Mock<IFileDialogService>();
        fileDialog
            .Setup(s => s.ShowOpenFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(_tempSparamPath);

        var vm = new ComponentSettingsDialogViewModel(
            fileDialog.Object,
            errorConsole: null,
            importers: null,
            portMappingDialog: dialogService.Object);

        var store = new Dictionary<string, ComponentSMatrixData>();
        vm.Configure(
            "comp_1", "comp_1", "1×2 Splitter", store,
            availablePinNames: new[] { "in", "out1", "out2" });

        await vm.LoadFromFileCommand.ExecuteAsync(null);

        store.ShouldContainKey("comp_1");
        var data = store["comp_1"];
        data.Wavelengths.ShouldNotBeEmpty();

        // Stored entries carry the semantic pin names — the SMatrixOverrideApplicator
        // can now resolve these against the component without skipping wavelengths.
        foreach (var (_, entry) in data.Wavelengths)
        {
            entry.PortNames.ShouldNotBeNull();
            entry.PortNames!.ShouldBe(new[] { "in", "out1", "out2" });
        }
    }

    [Fact]
    public async Task LoadFromFile_DialogCancelled_NoOverrideStored_StatusExplains()
    {
        // Cancel must abort cleanly — no half-stored data, no crash, and a
        // status message that tells the user why nothing happened.
        var dialogService = new Mock<IPortMappingDialogService>();
        dialogService
            .Setup(s => s.ShowAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyList<string>>()))
            .ReturnsAsync((IReadOnlyDictionary<string, string>?)null);

        var fileDialog = new Mock<IFileDialogService>();
        fileDialog
            .Setup(s => s.ShowOpenFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(_tempSparamPath);

        var vm = new ComponentSettingsDialogViewModel(
            fileDialog.Object,
            errorConsole: null,
            importers: null,
            portMappingDialog: dialogService.Object);

        var store = new Dictionary<string, ComponentSMatrixData>();
        vm.Configure(
            "comp_1", "comp_1", "1×2 Splitter", store,
            availablePinNames: new[] { "in", "out1", "out2" });

        await vm.LoadFromFileCommand.ExecuteAsync(null);

        store.ShouldNotContainKey("comp_1");
        vm.StatusText.ShouldContain("cancelled", Case.Insensitive);
    }

    [Fact]
    public async Task LoadFromFile_PortCountMismatch_FailsLoud_NoDialog()
    {
        // Structurally impossible — the dialog can't help. Bail out with a
        // clear status message instead of opening a dialog the user can't
        // satisfy.
        var dialogService = new Mock<IPortMappingDialogService>(MockBehavior.Strict);

        var fileDialog = new Mock<IFileDialogService>();
        fileDialog
            .Setup(s => s.ShowOpenFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(_tempSparamPath);

        var vm = new ComponentSettingsDialogViewModel(
            fileDialog.Object,
            errorConsole: null,
            importers: null,
            portMappingDialog: dialogService.Object);

        var store = new Dictionary<string, ComponentSMatrixData>();
        // 2-port pin list against a 3-port file: structurally unmappable.
        vm.Configure(
            "comp_1", "comp_1", "MyComp", store,
            availablePinNames: new[] { "in", "out" });

        await vm.LoadFromFileCommand.ExecuteAsync(null);

        store.ShouldNotContainKey("comp_1");
        vm.StatusText.ShouldContain("3 port");
        vm.StatusText.ShouldContain("2 pin");
        dialogService.VerifyNoOtherCalls();
    }
}
