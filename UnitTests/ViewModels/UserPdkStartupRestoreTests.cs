using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Hierarchy;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Components.Creation;
using CAP_DataAccess.Components.ComponentDraftMapper;
using Shouldly;

namespace UnitTests.ViewModels;

/// <summary>
/// Regression tests for issue #700: user-imported PDK paths recorded in preferences
/// must be reloaded at startup (via <see cref="LeftPanelViewModel.Initialize"/>),
/// and paths whose file no longer exists must be skipped and pruned without crashing.
/// </summary>
public class UserPdkStartupRestoreTests : IDisposable
{
    private const string UserPdkJson = @"{
        ""fileFormatVersion"": 1,
        ""name"": ""Issue700 User PDK"",
        ""components"": [
            {
                ""name"": ""Issue700 Waveguide"",
                ""category"": ""Waveguides"",
                ""nazcaFunction"": ""user.wg"",
                ""widthMicrometers"": 100,
                ""heightMicrometers"": 5,
                ""nazcaOriginOffsetX"": 0,
                ""nazcaOriginOffsetY"": 0,
                ""pins"": [
                    { ""name"": ""a0"", ""offsetXMicrometers"": 0,   ""offsetYMicrometers"": 2.5 },
                    { ""name"": ""b0"", ""offsetXMicrometers"": 100, ""offsetYMicrometers"": 2.5 }
                ]
            }
        ]
    }";

    private readonly string _testPreferencesPath;
    private readonly string _userPdkPath;
    private readonly UserPreferencesService _preferencesService;

    public UserPdkStartupRestoreTests()
    {
        _testPreferencesPath = Path.Combine(Path.GetTempPath(), $"test-preferences-{Guid.NewGuid()}.json");
        _userPdkPath = Path.Combine(Path.GetTempPath(), $"user-pdk-{Guid.NewGuid()}.json");
        _preferencesService = new UserPreferencesService(_testPreferencesPath);
    }

    public void Dispose()
    {
        if (File.Exists(_testPreferencesPath)) File.Delete(_testPreferencesPath);
        if (File.Exists(_userPdkPath)) File.Delete(_userPdkPath);
    }

    /// <summary>Creates a LeftPanelViewModel wired against the isolated test preferences.</summary>
    private LeftPanelViewModel CreateLeftPanelViewModel()
    {
        var canvas = new DesignCanvasViewModel();
        var libraryManager = new GroupLibraryManager();
        return new LeftPanelViewModel(
            canvas, libraryManager, new PdkLoader(), _preferencesService,
            new HierarchyPanelViewModel(canvas),
            new PdkManagerViewModel(),
            new ComponentLibraryViewModel(libraryManager));
    }

    [Fact]
    public void Initialize_ReloadsUserPdkRecordedInPreferences()
    {
        File.WriteAllText(_userPdkPath, UserPdkJson);
        _preferencesService.AddUserPdkPath(_userPdkPath);

        var vm = CreateLeftPanelViewModel();
        vm.Initialize();

        vm.PdkManager.IsPdkLoaded(_userPdkPath).ShouldBeTrue();
        vm.AllTemplates.ShouldContain(t => t.Name == "Issue700 Waveguide");
        // The path must stay recorded so the PDK survives the NEXT restart too.
        _preferencesService.GetUserPdkPaths().ShouldContain(Path.GetFullPath(_userPdkPath));
    }

    [Fact]
    public void Initialize_MissingUserPdkFile_DoesNotCrashAndPrunesPath()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"gone-{Guid.NewGuid()}.json");
        _preferencesService.AddUserPdkPath(missingPath);

        var vm = CreateLeftPanelViewModel();
        Should.NotThrow(() => vm.Initialize());

        vm.PdkManager.IsPdkLoaded(missingPath).ShouldBeFalse();
        _preferencesService.GetUserPdkPaths().ShouldNotContain(Path.GetFullPath(missingPath));
    }

    [Fact]
    public void Initialize_UserPdkWithNullNazcaOffsets_StillRestores()
    {
        // User PDKs created via the "New Component" feature (#656) may lack Nazca
        // origin offsets — the restore path must use the edit-tolerant loader.
        var jsonWithoutOffsets = UserPdkJson
            .Replace(@"""nazcaOriginOffsetX"": 0,", "")
            .Replace(@"""nazcaOriginOffsetY"": 0,", "");
        File.WriteAllText(_userPdkPath, jsonWithoutOffsets);
        _preferencesService.AddUserPdkPath(_userPdkPath);

        var vm = CreateLeftPanelViewModel();
        Should.NotThrow(() => vm.Initialize());

        vm.PdkManager.IsPdkLoaded(_userPdkPath).ShouldBeTrue();
    }

    [Fact]
    public void Initialize_CorruptUserPdkFile_DoesNotCrashAndKeepsOtherPdks()
    {
        var corruptPath = Path.Combine(Path.GetTempPath(), $"corrupt-{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(corruptPath, "{ not valid json");
            File.WriteAllText(_userPdkPath, UserPdkJson);
            _preferencesService.AddUserPdkPath(corruptPath);
            _preferencesService.AddUserPdkPath(_userPdkPath);

            var vm = CreateLeftPanelViewModel();
            Should.NotThrow(() => vm.Initialize());

            vm.PdkManager.IsPdkLoaded(corruptPath).ShouldBeFalse();
            vm.PdkManager.IsPdkLoaded(_userPdkPath).ShouldBeTrue();
        }
        finally
        {
            if (File.Exists(corruptPath)) File.Delete(corruptPath);
        }
    }
}
