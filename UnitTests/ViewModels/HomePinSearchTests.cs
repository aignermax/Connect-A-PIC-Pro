using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Home;
using Shouldly;

namespace UnitTests.ViewModels;

/// <summary>
/// Unit tests for pinned-first display ordering and the recents search filter
/// in <see cref="HomeViewModel"/>.
/// </summary>
public class HomePinSearchTests : IDisposable
{
    private readonly string _testPreferencesPath;
    private readonly UserPreferencesService _preferences;
    private readonly RecentProjectsService _recentProjects;

    private readonly string _emptyExamplesBase;

    public HomePinSearchTests()
    {
        _testPreferencesPath = Path.Combine(Path.GetTempPath(), $"test-pinsearch-prefs-{Guid.NewGuid()}.json");
        _emptyExamplesBase = Path.Combine(Path.GetTempPath(), $"test-pinsearch-noexamples-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_emptyExamplesBase);
        _preferences = new UserPreferencesService(_testPreferencesPath);
        _recentProjects = new RecentProjectsService(_preferences);
    }

    public void Dispose()
    {
        if (File.Exists(_testPreferencesPath))
        {
            File.Delete(_testPreferencesPath);
        }
        if (Directory.Exists(_emptyExamplesBase))
        {
            Directory.Delete(_emptyExamplesBase, recursive: true);
        }
    }

    private static string TestProjectPath(string fileName) =>
        Path.Combine(Path.GetTempPath(), "lunima-pinsearch-tests", fileName);

    // Hermetic: examples rooted in an empty temp dir (see HomeViewModelTests).
    private HomeViewModel CreateHomeViewModel() =>
        new(_recentProjects, _preferences, new ExampleDesignsService(_emptyExamplesBase));

    [Fact]
    public void RecentList_ShowsPinnedEntriesFirst()
    {
        var pinnedOlder = TestProjectPath("pinned-older.lun");
        _recentProjects.RecordProject(pinnedOlder);
        _recentProjects.RecordProject(TestProjectPath("newer.lun"));
        _recentProjects.TogglePin(pinnedOlder);

        var home = CreateHomeViewModel();

        home.RecentProjects[0].FullPath.ShouldBe(Path.GetFullPath(pinnedOlder));
        home.RecentProjects[0].IsPinned.ShouldBeTrue();
        home.RecentProjects[1].IsPinned.ShouldBeFalse();
    }

    [Fact]
    public void TogglePinCommand_PinsEntryAndReordersList()
    {
        var older = TestProjectPath("to-pin.lun");
        _recentProjects.RecordProject(older);
        _recentProjects.RecordProject(TestProjectPath("newest.lun"));
        var home = CreateHomeViewModel();
        home.RecentProjects[1].FullPath.ShouldBe(Path.GetFullPath(older));

        home.TogglePinCommand.Execute(home.RecentProjects[1]);

        home.RecentProjects[0].FullPath.ShouldBe(Path.GetFullPath(older));
        home.RecentProjects[0].IsPinned.ShouldBeTrue();
    }

    [Fact]
    public void Search_FiltersByFileNameCaseInsensitive()
    {
        _recentProjects.RecordProject(TestProjectPath("ring-resonator.lun"));
        _recentProjects.RecordProject(TestProjectPath("mzi-splitter.lun"));
        var home = CreateHomeViewModel();

        home.SearchText = "RING";

        home.RecentProjects.ShouldHaveSingleItem()
            .FileName.ShouldBe("ring-resonator");
    }

    [Fact]
    public void Search_MatchesAgainstFullPath()
    {
        _recentProjects.RecordProject(TestProjectPath("design.lun"));
        var home = CreateHomeViewModel();

        home.SearchText = "lunima-pinsearch-tests";

        home.RecentProjects.Count.ShouldBe(1);
    }

    [Fact]
    public void ClearingSearch_RestoresFullList()
    {
        _recentProjects.RecordProject(TestProjectPath("alpha.lun"));
        _recentProjects.RecordProject(TestProjectPath("beta.lun"));
        var home = CreateHomeViewModel();

        home.SearchText = "alpha";
        home.RecentProjects.Count.ShouldBe(1);

        home.SearchText = "";

        home.RecentProjects.Count.ShouldBe(2);
    }

    [Fact]
    public void HasAnyRecentProjects_StaysTrueWhenFilterMatchesNothing()
    {
        _recentProjects.RecordProject(TestProjectPath("only.lun"));
        var home = CreateHomeViewModel();

        home.SearchText = "no-such-match";

        home.RecentProjects.ShouldBeEmpty();
        home.HasAnyRecentProjects.ShouldBeTrue();
    }

    [Fact]
    public void HasAnyRecentProjects_FalseWithNoHistory()
    {
        CreateHomeViewModel().HasAnyRecentProjects.ShouldBeFalse();
    }
}
