using System.IO;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.PdkOffset;
using CAP_DataAccess.Components.ComponentDraftMapper;
using Shouldly;

namespace UnitTests.PdkOffset;

/// <summary>
/// Tests for the installed-PDK dropdown selection path in
/// <see cref="PdkOffsetEditorViewModel"/>. Verifies that selecting a
/// <see cref="PdkInfoViewModel"/> from <see cref="PdkManagerViewModel.LoadedPdks"/>
/// loads the PDK into the editor identically to the file-dialog path.
/// </summary>
public class PdkOffsetEditorSelectInstalledPdkTests
{
    /// <summary>StatusText is localized (issue #749); pin English so substring assertions
    /// stay culture-independent regardless of the CI/dev OS language.</summary>
    public PdkOffsetEditorSelectInstalledPdkTests() =>
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);

    private const string TwoPdkJson = @"{
        ""fileFormatVersion"": 1,
        ""name"": ""Registry PDK"",
        ""components"": [
            {
                ""name"": ""MMI"",
                ""category"": ""Splitters"",
                ""nazcaFunction"": ""pdk.mmi"",
                ""widthMicrometers"": 50,
                ""heightMicrometers"": 30,
                ""nazcaOriginOffsetX"": 5.0,
                ""nazcaOriginOffsetY"": 15.0,
                ""pins"": [
                    { ""name"": ""a0"", ""offsetXMicrometers"": 0,  ""offsetYMicrometers"": 15 },
                    { ""name"": ""b0"", ""offsetXMicrometers"": 50, ""offsetYMicrometers"": 10 },
                    { ""name"": ""b1"", ""offsetXMicrometers"": 50, ""offsetYMicrometers"": 20 }
                ]
            },
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

    private static (PdkOffsetEditorViewModel vm, PdkManagerViewModel manager, string tempFile)
        BuildVmWithRegisteredPdk()
    {
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, TwoPdkJson);

        var manager = new PdkManagerViewModel();
        manager.RegisterPdk("Registry PDK", tempFile, isBundled: false, componentCount: 2);

        var vm = new PdkOffsetEditorViewModel(new PdkLoader(), new PdkJsonSaver(), manager);
        return (vm, manager, tempFile);
    }

    [Fact]
    public void AvailablePdks_ReflectsManagerLoadedPdks()
    {
        var (vm, manager, tempFile) = BuildVmWithRegisteredPdk();
        try
        {
            vm.AvailablePdks.Count.ShouldBe(1);
            vm.AvailablePdks[0].Name.ShouldBe("Registry PDK");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void SelectInstalledPdk_LoadsPdkComponents()
    {
        var (vm, manager, tempFile) = BuildVmWithRegisteredPdk();
        try
        {
            vm.SelectedInstalledPdk = vm.AvailablePdks[0];

            vm.Components.Count.ShouldBe(2);
            vm.Components[0].ComponentName.ShouldBe("MMI");
            vm.Components[1].ComponentName.ShouldBe("Waveguide");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void SelectInstalledPdk_SetsStatusText_WithCounts()
    {
        var (vm, manager, tempFile) = BuildVmWithRegisteredPdk();
        try
        {
            vm.SelectedInstalledPdk = vm.AvailablePdks[0];

            vm.StatusText.ShouldContain("Registry PDK");
            vm.StatusText.ShouldContain("2 components");
            vm.StatusText.ShouldContain("1 missing offset");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void SelectInstalledPdk_ClearsSelectedComponent()
    {
        var (vm, manager, tempFile) = BuildVmWithRegisteredPdk();
        try
        {
            vm.SelectedInstalledPdk = vm.AvailablePdks[0];

            vm.SelectedComponent.ShouldBeNull();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void SelectInstalledPdk_HasNoUnsavedChanges_AfterLoad()
    {
        var (vm, manager, tempFile) = BuildVmWithRegisteredPdk();
        try
        {
            vm.SelectedInstalledPdk = vm.AvailablePdks[0];

            vm.HasUnsavedChanges.ShouldBeFalse();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void SelectInstalledPdk_WithNullFilePath_ShowsErrorStatus()
    {
        var manager = new PdkManagerViewModel();
        // Register a PDK with null file path (e.g. a hypothetical in-memory PDK)
        manager.RegisterPdk("No-Path PDK", filePath: null, isBundled: true, componentCount: 0);

        var vm = new PdkOffsetEditorViewModel(new PdkLoader(), new PdkJsonSaver(), manager);

        vm.SelectedInstalledPdk = vm.AvailablePdks[0];

        vm.Components.ShouldBeEmpty();
        vm.StatusText.ShouldContain("no file path");
    }

    [Fact]
    public void SelectInstalledPdk_NullSelection_IsNoOp()
    {
        var (vm, manager, tempFile) = BuildVmWithRegisteredPdk();
        try
        {
            // First load something
            vm.SelectedInstalledPdk = vm.AvailablePdks[0];
            vm.Components.Count.ShouldBe(2);

            // Then clear selection — should not crash or clear components
            vm.SelectedInstalledPdk = null;

            vm.Components.Count.ShouldBe(2); // unchanged
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void SelectInstalledPdk_CanEditAndSaveBack()
    {
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, TwoPdkJson);
        try
        {
            var manager = new PdkManagerViewModel();
            manager.RegisterPdk("Registry PDK", tempFile, isBundled: false, componentCount: 2);

            var vm = new PdkOffsetEditorViewModel(new PdkLoader(), new PdkJsonSaver(), manager);
            vm.SelectedInstalledPdk = vm.AvailablePdks[0];

            // Select the Waveguide (missing offset) and apply an offset
            vm.SelectedComponent = vm.Components[1]; // Waveguide — Missing
            vm.OffsetX = 0.0;
            vm.OffsetY = 2.5;
            vm.ApplyOffsetCommand.Execute(null);

            vm.HasUnsavedChanges.ShouldBeTrue();
            vm.SavePdkCommand.Execute(null);
            vm.HasUnsavedChanges.ShouldBeFalse();

            // Reload and verify the offset was persisted
            var reloaded = new PdkLoader().LoadFromFileForEditing(tempFile);
            reloaded.Components[1].NazcaOriginOffsetX.ShouldBe(0.0);
            reloaded.Components[1].NazcaOriginOffsetY.ShouldBe(2.5);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void AvailablePdks_UpdatesWhenManagerGetsNewPdk()
    {
        var manager = new PdkManagerViewModel();
        var vm = new PdkOffsetEditorViewModel(new PdkLoader(), new PdkJsonSaver(), manager);

        vm.AvailablePdks.Count.ShouldBe(0);

        manager.RegisterPdk("Late PDK", filePath: null, isBundled: true, componentCount: 3);

        // AvailablePdks is the same ObservableCollection as manager.LoadedPdks
        vm.AvailablePdks.Count.ShouldBe(1);
        vm.AvailablePdks[0].Name.ShouldBe("Late PDK");
    }
}
