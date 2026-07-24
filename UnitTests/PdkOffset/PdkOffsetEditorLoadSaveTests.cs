using System.IO;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.PdkOffset;
using CAP_DataAccess.Components.ComponentDraftMapper;
using Shouldly;

namespace UnitTests.PdkOffset;

/// <summary>
/// Integration tests for the load-file and save-file paths of
/// <see cref="PdkOffsetEditorViewModel"/>. These exercise the ViewModel
/// through its public API (no reflection), including the failure
/// branches that the per-VM unit tests cannot reach.
/// </summary>
public class PdkOffsetEditorLoadSaveTests
{
    /// <summary>StatusText is localized (issue #749); pin English so substring assertions
    /// stay culture-independent regardless of the CI/dev OS language.</summary>
    public PdkOffsetEditorLoadSaveTests() =>
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);

    private const string UncalibratedPdkJson = @"{
        ""fileFormatVersion"": 1,
        ""name"": ""Uncalibrated Demo"",
        ""components"": [
            {
                ""name"": ""Raw Waveguide"",
                ""category"": ""Waveguides"",
                ""nazcaFunction"": ""demo.raw_wg"",
                ""widthMicrometers"": 100,
                ""heightMicrometers"": 5,
                ""pins"": [
                    { ""name"": ""a0"", ""offsetXMicrometers"": 0,   ""offsetYMicrometers"": 2.5 },
                    { ""name"": ""b0"", ""offsetXMicrometers"": 100, ""offsetYMicrometers"": 2.5 }
                ]
            },
            {
                ""name"": ""Calibrated Splitter"",
                ""category"": ""Splitters"",
                ""nazcaFunction"": ""demo.splitter"",
                ""widthMicrometers"": 40,
                ""heightMicrometers"": 20,
                ""nazcaOriginOffsetX"": 5.0,
                ""nazcaOriginOffsetY"": 10.0,
                ""pins"": [
                    { ""name"": ""a0"", ""offsetXMicrometers"": 0,  ""offsetYMicrometers"": 10 }
                ]
            }
        ]
    }";

    private static PdkOffsetEditorViewModel BuildViewModel() =>
        new(new PdkLoader(), new PdkJsonSaver(), new PdkManagerViewModel());

    [Fact]
    public async Task LoadPdkFile_WithMissingOffsets_LoadsAndReportsCounts()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, UncalibratedPdkJson);
            var vm = BuildViewModel();
            vm.FileDialogService = new StubFileDialogService(tempFile);

            await vm.LoadPdkFileCommand.ExecuteAsync(null);

            vm.Components.Count.ShouldBe(2);
            vm.SelectedComponent.ShouldBeNull();
            vm.HasUnsavedChanges.ShouldBeFalse();
            vm.StatusText.ShouldContain("Uncalibrated Demo");
            vm.StatusText.ShouldContain("1 missing offset");
            vm.Components[0].Status.ShouldBe(OffsetStatus.Missing);
            vm.Components[1].Status.ShouldBe(OffsetStatus.Set);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task LoadPdkFile_WhenCancelled_LeavesStateUntouched()
    {
        var vm = BuildViewModel();
        vm.FileDialogService = new StubFileDialogService(null);

        await vm.LoadPdkFileCommand.ExecuteAsync(null);

        vm.Components.ShouldBeEmpty();
        vm.SelectedComponent.ShouldBeNull();
    }

    [Fact]
    public async Task LoadPdkFile_Reload_ReplacesPreviousComponents()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, UncalibratedPdkJson);
            var vm = BuildViewModel();
            vm.FileDialogService = new StubFileDialogService(tempFile);

            await vm.LoadPdkFileCommand.ExecuteAsync(null);
            vm.SelectedComponent = vm.Components[0];
            vm.Components.Count.ShouldBe(2);

            // Re-load the same file — previous list must be cleared first,
            // otherwise a reload would double each entry.
            await vm.LoadPdkFileCommand.ExecuteAsync(null);

            vm.Components.Count.ShouldBe(2);
            vm.SelectedComponent.ShouldBeNull();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task SavePdk_FullLoadEditSaveRoundTrip_PreservesAllFieldsAndEditedOffset()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, UncalibratedPdkJson);
            var vm = BuildViewModel();
            vm.FileDialogService = new StubFileDialogService(tempFile);

            await vm.LoadPdkFileCommand.ExecuteAsync(null);

            vm.SelectedComponent = vm.Components[0]; // Raw Waveguide — Missing
            vm.OffsetX = 12.5;
            vm.OffsetY = 2.5;
            vm.ApplyOffsetCommand.Execute(null);
            vm.SavePdkCommand.Execute(null);

            vm.HasUnsavedChanges.ShouldBeFalse();

            // Reload via the strict loader — the file must now validate as a
            // fully-calibrated PDK for the waveguide row.
            var reloaded = new PdkLoader().LoadFromFileForEditing(tempFile);
            reloaded.Name.ShouldBe("Uncalibrated Demo");
            reloaded.Components.Count.ShouldBe(2);
            reloaded.Components[0].NazcaOriginOffsetX.ShouldBe(12.5);
            reloaded.Components[0].NazcaOriginOffsetY.ShouldBe(2.5);
            reloaded.Components[0].Pins.Count.ShouldBe(2);
            reloaded.Components[0].Pins[0].Name.ShouldBe("a0");
            reloaded.Components[1].NazcaOriginOffsetX.ShouldBe(5.0);
            reloaded.Components[1].Category.ShouldBe("Splitters");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task SavePdk_WhenSaveThrows_KeepsUnsavedChangesAndSurfacesError()
    {
        // If saving fails for any reason, the in-memory edits must remain
        // pending — otherwise the user sees "no unsaved changes" and closes
        // the window losing work. Trigger the failure by deleting the parent
        // directory between load and save (PdkJsonSaver rejects a missing
        // directory with InvalidOperationException).
        var tempDir = Path.Combine(Path.GetTempPath(), "pdk-save-fail-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        var tempFile = Path.Combine(tempDir, "source.json");
        File.WriteAllText(tempFile, UncalibratedPdkJson);

        try
        {
            var vm = BuildViewModel();
            vm.FileDialogService = new StubFileDialogService(tempFile);

            await vm.LoadPdkFileCommand.ExecuteAsync(null);
            vm.SelectedComponent = vm.Components[0];
            vm.OffsetX = 1.0;
            vm.OffsetY = 1.0;
            vm.ApplyOffsetCommand.Execute(null);
            vm.HasUnsavedChanges.ShouldBeTrue();

            Directory.Delete(tempDir, recursive: true);

            vm.SavePdkCommand.Execute(null);

            vm.HasUnsavedChanges.ShouldBeTrue();
            vm.StatusText.ShouldContain("Save failed");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task DeselectComponent_ClearsPinPositionsAndMarkers()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, UncalibratedPdkJson);
            var vm = BuildViewModel();
            vm.FileDialogService = new StubFileDialogService(tempFile);
            await vm.LoadPdkFileCommand.ExecuteAsync(null);

            vm.SelectedComponent = vm.Components[0];
            vm.PinPositions.Count.ShouldBe(2);
            vm.PinMarkers.Count.ShouldBe(2);

            vm.SelectedComponent = null;

            vm.PinPositions.ShouldBeEmpty();
            vm.PinMarkers.ShouldBeEmpty();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void SaveToFile_LeavesNoTempFileOnSuccess()
    {
        // Atomic write: after a successful save there must be no leftover
        // `<path>.tmp` on disk. Guards against a regression where the temp
        // file rename step is skipped or the temp path naming drifts.
        var tempFile = Path.GetTempFileName();
        try
        {
            new PdkJsonSaver().SaveToFile(BuildMinimalPdk(), tempFile);

            File.Exists(tempFile).ShouldBeTrue();
            File.Exists(tempFile + ".tmp").ShouldBeFalse();
        }
        finally
        {
            if (File.Exists(tempFile))           File.Delete(tempFile);
            if (File.Exists(tempFile + ".tmp"))  File.Delete(tempFile + ".tmp");
        }
    }

    [Fact]
    public void SaveToFile_WhenMoveFails_DoesNotLeaveOrphanTempFile()
    {
        // Atomic write should clean up its <path>.tmp sibling if the final
        // rename step throws (target is a directory). Without the cleanup
        // block we'd accumulate .tmp files on every failed save.
        var tempDir = Path.Combine(Path.GetTempPath(), "pdk-saver-orphan-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        var targetPath = Path.Combine(tempDir, "target");
        Directory.CreateDirectory(targetPath); // target is a directory → File.Move throws

        try
        {
            Should.Throw<Exception>(() =>
                new PdkJsonSaver().SaveToFile(BuildMinimalPdk(), targetPath));

            File.Exists(targetPath + ".tmp").ShouldBeFalse();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    private static CAP_DataAccess.Components.ComponentDraftMapper.DTOs.PdkDraft BuildMinimalPdk() =>
        new()
        {
            Name = "Minimal",
            Components = new()
            {
                new()
                {
                    Name = "One",
                    NazcaFunction = "x",
                    WidthMicrometers = 10,
                    HeightMicrometers = 5,
                    NazcaOriginOffsetX = 0,
                    NazcaOriginOffsetY = 0,
                    Pins = new() { new() { Name = "a0", OffsetXMicrometers = 0, OffsetYMicrometers = 2.5 } }
                }
            }
        };

    /// <summary>
    /// Minimal <see cref="IFileDialogService"/> that returns a pre-configured
    /// path for open-file and throws for any other operation. Lets tests
    /// drive the ViewModel's LoadPdkFile command without an Avalonia window.
    /// </summary>
    private sealed class StubFileDialogService : IFileDialogService
    {
        private readonly string? _pathToReturn;

        public StubFileDialogService(string? pathToReturn)
        {
            _pathToReturn = pathToReturn;
        }

        public Task<string?> ShowOpenFileDialogAsync(string title, string filters)
            => Task.FromResult(_pathToReturn);

        public Task<string?> ShowSaveFileDialogAsync(string title, string defaultExtension, string filters)
            => throw new NotSupportedException("Not used in these tests.");
    }
}
