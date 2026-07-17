using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.ComponentSettings;
using CAP_DataAccess.Persistence.PIR;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.ComponentSettings;

/// <summary>
/// Tests that the Component Settings dialog VM behaves correctly when wired
/// against the user-global <see cref="UserSMatrixOverrideStore"/> instead of
/// the project's <c>StoredSMatrices</c>. The wiring lives in
/// <c>MainWindow.axaml.cs::ShowComponentSettingsDialog</c>; these tests pin
/// the contract the wiring depends on so that path can be refactored without
/// silently breaking PDK-template-scoped imports.
/// </summary>
public class ComponentSettingsDialogUserGlobalScopeTests : IDisposable
{
    private readonly string _tempStorePath;
    private readonly string _tempSparamPath;

    public ComponentSettingsDialogUserGlobalScopeTests()
    {
        // Status/title strings are now localized; pin English so assertions are locale-independent (runner is de_DE).
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
        _tempStorePath = Path.Combine(Path.GetTempPath(), $"sparam-overrides-{Guid.NewGuid()}.json");
        _tempSparamPath = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid()}.sparam");
        WriteMinimalSparamFile(_tempSparamPath);
    }

    public void Dispose()
    {
        if (File.Exists(_tempStorePath)) File.Delete(_tempStorePath);
        if (File.Exists(_tempSparamPath)) File.Delete(_tempSparamPath);
    }

    private static void WriteMinimalSparamFile(string path)
    {
        // 2-port single-wavelength Lumerical-format file just large enough
        // to drive the importer. Two transmission blocks (1→1 and 2→1).
        File.WriteAllText(path,
            "('port 1','TE',1,'port 1',1,'transmission')\n" +
            "(1,3)\n" +
            "1.93414e+14\t0.05\t0.0\n" +
            "('port 2','TE',1,'port 1',1,'transmission')\n" +
            "(1,3)\n" +
            "1.93414e+14\t0.95\t0.0\n");
    }

    [Fact]
    public async Task LoadFromFile_PdkTemplateScope_PersistsToUserStoreOnSave()
    {
        var userStore = new UserSMatrixOverrideStore(_tempStorePath);
        var fileDialog = new Mock<IFileDialogService>();
        fileDialog
            .Setup(s => s.ShowOpenFileDialogAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(_tempSparamPath);

        var vm = new ComponentSettingsDialogViewModel(fileDialog.Object);
        bool onChangedFired = false;
        vm.Configure(
            entityKey: "siepic-ebeam-pdk::2x2 MMI Coupler",
            smatrixKey: "siepic-ebeam-pdk::2x2 MMI Coupler",
            displayName: "2x2 MMI Coupler",
            storedSMatrices: userStore.Overrides,
            liveComponent: null,
            onChanged: () => { onChangedFired = true; userStore.Save(); },
            isUserGlobalScope: true);

        await vm.LoadFromFileCommand.ExecuteAsync(null);

        // Dialog ran the onChanged callback so the user-global store persisted.
        onChangedFired.ShouldBeTrue();
        userStore.Overrides.ShouldContainKey("siepic-ebeam-pdk::2x2 MMI Coupler");

        // Reload from disk: a fresh process picking up the same file would
        // see the same override. This is the cross-project guarantee.
        var fresh = new UserSMatrixOverrideStore(_tempStorePath);
        fresh.Overrides.ShouldContainKey("siepic-ebeam-pdk::2x2 MMI Coupler");
        fresh.Overrides["siepic-ebeam-pdk::2x2 MMI Coupler"].Wavelengths.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Configure_UserGlobalScope_AnnouncesScopeInTitle()
    {
        // The dialog title is the only piece of the dialog that distinguishes
        // user-global from project scope, so a future UI change that drops
        // the suffix would silently confuse the user. Pin the suffix.
        var userStore = new UserSMatrixOverrideStore(_tempStorePath);
        var vm = new ComponentSettingsDialogViewModel(Mock.Of<IFileDialogService>());

        vm.Configure(
            entityKey: "siepic-ebeam-pdk::2x2 MMI Coupler",
            smatrixKey: "siepic-ebeam-pdk::2x2 MMI Coupler",
            displayName: "2x2 MMI Coupler",
            storedSMatrices: userStore.Overrides,
            isUserGlobalScope: true);

        vm.Title.ShouldContain("applies to all projects");
    }

    [Fact]
    public void Configure_ProjectScope_DoesNotAnnounceUserScope()
    {
        var store = new Dictionary<string, ComponentSMatrixData>();
        var vm = new ComponentSettingsDialogViewModel(Mock.Of<IFileDialogService>());

        vm.Configure(
            entityKey: "comp_1",
            smatrixKey: "comp_1",
            displayName: "MyComponent_1",
            storedSMatrices: store,
            isUserGlobalScope: false);

        vm.Title.ShouldNotContain("applies to all projects");
    }

    [Fact]
    public void DeleteEntry_UserGlobalScope_RemovesFromUserStoreOnSave()
    {
        var userStore = new UserSMatrixOverrideStore(_tempStorePath);
        userStore.Apply(
            "siepic-ebeam-pdk::2x2 MMI Coupler",
            new ComponentSMatrixData
            {
                SourceNote = "test",
                Wavelengths =
                {
                    ["1550"] = new SMatrixWavelengthEntry
                    {
                        Rows = 2, Cols = 2,
                        Real = new List<double> { 1, 0, 0, 1 },
                        Imag = new List<double> { 0, 0, 0, 0 }
                    }
                }
            });
        userStore.Save();

        var vm = new ComponentSettingsDialogViewModel(Mock.Of<IFileDialogService>());
        vm.Configure(
            entityKey: "siepic-ebeam-pdk::2x2 MMI Coupler",
            smatrixKey: "siepic-ebeam-pdk::2x2 MMI Coupler",
            displayName: "2x2 MMI Coupler",
            storedSMatrices: userStore.Overrides,
            onChanged: userStore.Save,
            isUserGlobalScope: true);

        vm.SMatrixEntries.Count.ShouldBe(1);
        vm.DeleteEntryCommand.Execute(vm.SMatrixEntries[0]);

        userStore.Overrides.ShouldNotContainKey("siepic-ebeam-pdk::2x2 MMI Coupler");

        var fresh = new UserSMatrixOverrideStore(_tempStorePath);
        fresh.Overrides.ShouldNotContainKey("siepic-ebeam-pdk::2x2 MMI Coupler");
    }
}
