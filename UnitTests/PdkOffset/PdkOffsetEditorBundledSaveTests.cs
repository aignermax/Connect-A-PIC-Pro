using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.PdkOffset;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using Shouldly;

namespace UnitTests.PdkOffset;

/// <summary>
/// Round-5 integrity fix: <c>SavePdk</c> used to write <c>_loadedFilePath</c>
/// unconditionally — for BUNDLED PDKs that overwrote the read-only shipped
/// JSON. These tests pin the fork-on-save semantics: a bundled save creates
/// the user's fork in the managed user-pdks root, leaves the bundled file
/// byte-identical, and raises <see cref="PdkOffsetEditorViewModel.BundledPdkForkSaved"/>
/// for the library shadow swap. Custom PDKs keep saving in place.
/// </summary>
public class PdkOffsetEditorBundledSaveTests : IDisposable
{
    private readonly string _userPdkRoot = Path.Combine(
        Path.GetTempPath(), "lunima-offsetfork-" + Guid.NewGuid().ToString("N"));
    private readonly string _bundledDir = Path.Combine(
        Path.GetTempPath(), "lunima-bundledsrc-" + Guid.NewGuid().ToString("N"));

    public PdkOffsetEditorBundledSaveTests() =>
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);

    public void Dispose()
    {
        if (Directory.Exists(_userPdkRoot)) Directory.Delete(_userPdkRoot, true);
        if (Directory.Exists(_bundledDir)) Directory.Delete(_bundledDir, true);
    }

    private const string PdkJson = @"{
        ""fileFormatVersion"": 1,
        ""name"": ""Shipped PDK"",
        ""components"": [
            {
                ""name"": ""Waveguide"",
                ""category"": ""Waveguides"",
                ""nazcaFunction"": ""pdk.wg"",
                ""widthMicrometers"": 100,
                ""heightMicrometers"": 5,
                ""pins"": [
                    { ""name"": ""a0"", ""offsetXMicrometers"": 0,   ""offsetYMicrometers"": 2.5 },
                    { ""name"": ""b0"", ""offsetXMicrometers"": 100, ""offsetYMicrometers"": 2.5 }
                ]
            }
        ]
    }";

    private string WriteBundledJson()
    {
        Directory.CreateDirectory(_bundledDir);
        var path = Path.Combine(_bundledDir, "shipped-pdk.json");
        File.WriteAllText(path, PdkJson);
        return path;
    }

    private (PdkOffsetEditorViewModel vm, UserPdkStore store, string bundledPath, List<(string Name, string Path)> forkEvents)
        BuildBundledVm(bool withStore = true)
    {
        var bundledPath = WriteBundledJson();
        var manager = new PdkManagerViewModel();
        manager.RegisterPdk("Shipped PDK", bundledPath, isBundled: true, componentCount: 1);

        var store = new UserPdkStore(_userPdkRoot, new PdkJsonSaver(), new PdkLoader());
        var vm = new PdkOffsetEditorViewModel(
            new PdkLoader(), new PdkJsonSaver(), manager,
            userPdkStore: withStore ? store : null);
        var forkEvents = new List<(string, string)>();
        vm.BundledPdkForkSaved = (name, path) => forkEvents.Add((name, path));
        return (vm, store, bundledPath, forkEvents);
    }

    private static void EditWaveguideOffset(PdkOffsetEditorViewModel vm)
    {
        vm.SelectedInstalledPdk = vm.AvailablePdks[0];
        vm.SelectedComponent = vm.Components[0];
        vm.OffsetX = 0.0;
        vm.OffsetY = 2.5;
        vm.ApplyOffsetCommand.Execute(null);
        vm.HasUnsavedChanges.ShouldBeTrue();
    }

    [Fact]
    public void BundledSave_CreatesFork_LeavesBundledByteIdentical_AndRaisesShadowHook()
    {
        var (vm, store, bundledPath, forkEvents) = BuildBundledVm();
        var bundledBytesBefore = File.ReadAllBytes(bundledPath);

        EditWaveguideOffset(vm);
        vm.SavePdkCommand.Execute(null);

        // Fork exists in the managed root and carries the edit.
        var forkPath = store.ResolveNamedPath("Shipped PDK");
        File.Exists(forkPath).ShouldBeTrue();
        var fork = new PdkLoader().LoadFromFileForEditing(forkPath);
        fork.Components[0].NazcaOriginOffsetY.ShouldBe(2.5);

        // The bundled JSON is untouched — byte-identical, not merely re-parsable.
        File.ReadAllBytes(bundledPath).ShouldBe(bundledBytesBefore);

        // Shadow registration hook fired exactly once with the fork location.
        forkEvents.ShouldBe(new[] { ("Shipped PDK", forkPath) });

        vm.HasUnsavedChanges.ShouldBeFalse();
        vm.StatusText.ShouldContain(Path.GetFileName(forkPath));
    }

    [Fact]
    public void BundledSave_RetargetsEditor_SecondSaveWritesForkDirectly()
    {
        var (vm, store, bundledPath, forkEvents) = BuildBundledVm();
        EditWaveguideOffset(vm);
        vm.SavePdkCommand.Execute(null);
        var bundledBytesAfterFirst = File.ReadAllBytes(bundledPath);

        // Second edit + save must go straight to the fork (no second fork event,
        // bundled file still untouched).
        vm.OffsetY = 3.5;
        vm.ApplyOffsetCommand.Execute(null);
        vm.SavePdkCommand.Execute(null);

        forkEvents.Count.ShouldBe(1);
        File.ReadAllBytes(bundledPath).ShouldBe(bundledBytesAfterFirst);
        var fork = new PdkLoader().LoadFromFileForEditing(store.ResolveNamedPath("Shipped PDK"));
        fork.Components[0].NazcaOriginOffsetY.ShouldBe(3.5);
    }

    [Fact]
    public void BundledSave_WithoutStore_RefusesAndLeavesEverythingUntouched()
    {
        // Defensive branch: no UserPdkStore wired → the bundled JSON must still
        // never be written; the save reports the read-only state instead.
        var (vm, _, bundledPath, forkEvents) = BuildBundledVm(withStore: false);
        var bundledBytesBefore = File.ReadAllBytes(bundledPath);

        EditWaveguideOffset(vm);
        vm.SavePdkCommand.Execute(null);

        File.ReadAllBytes(bundledPath).ShouldBe(bundledBytesBefore);
        forkEvents.ShouldBeEmpty();
        vm.HasUnsavedChanges.ShouldBeTrue();
        vm.StatusText.ShouldContain("read-only");
    }

    [Fact]
    public void CustomSave_StillWritesSourceFileDirectly_NoForkNoHook()
    {
        var bundledPath = WriteBundledJson();   // reused as a CUSTOM pdk file here
        var manager = new PdkManagerViewModel();
        manager.RegisterPdk("Shipped PDK", bundledPath, isBundled: false, componentCount: 1);

        var store = new UserPdkStore(_userPdkRoot, new PdkJsonSaver(), new PdkLoader());
        var vm = new PdkOffsetEditorViewModel(
            new PdkLoader(), new PdkJsonSaver(), manager, userPdkStore: store);
        var forkEvents = new List<(string, string)>();
        vm.BundledPdkForkSaved = (name, path) => forkEvents.Add((name, path));

        EditWaveguideOffset(vm);
        vm.SavePdkCommand.Execute(null);

        // Direct in-place save: source file carries the edit, no fork was created.
        var reloaded = new PdkLoader().LoadFromFileForEditing(bundledPath);
        reloaded.Components[0].NazcaOriginOffsetY.ShouldBe(2.5);
        File.Exists(store.ResolveNamedPath("Shipped PDK")).ShouldBeFalse();
        forkEvents.ShouldBeEmpty();
        vm.HasUnsavedChanges.ShouldBeFalse();
    }
}
