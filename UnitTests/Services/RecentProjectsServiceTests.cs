using CAP.Avalonia.Services;
using Shouldly;

namespace UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="RecentProjectsService"/> — the most-recently-used
/// project list backing the Home screen. Verifies MRU ordering, deduplication,
/// the entry cap, path normalization, and persistence via UserPreferencesService.
/// Uses an isolated temp preferences file to avoid polluting user settings.
/// </summary>
public class RecentProjectsServiceTests : IDisposable
{
    private static readonly DateTime FixedUtcNow = new(2026, 7, 8, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _testPreferencesPath;
    private readonly UserPreferencesService _preferences;
    private readonly RecentProjectsService _service;

    public RecentProjectsServiceTests()
    {
        _testPreferencesPath = Path.Combine(Path.GetTempPath(), $"test-recent-projects-{Guid.NewGuid()}.json");
        _preferences = new UserPreferencesService(_testPreferencesPath);
        _service = new RecentProjectsService(_preferences, () => FixedUtcNow);
    }

    public void Dispose()
    {
        if (File.Exists(_testPreferencesPath))
        {
            File.Delete(_testPreferencesPath);
        }
    }

    private static string TestProjectPath(string fileName) =>
        Path.Combine(Path.GetTempPath(), "lunima-recents-tests", fileName);

    [Fact]
    public void GetRecentProjects_WithNoHistory_ReturnsEmptyList()
    {
        _service.GetRecentProjects().ShouldBeEmpty();
    }

    [Fact]
    public void RecordProject_AddsMostRecentEntryFirst()
    {
        var first = TestProjectPath("first.lun");
        var second = TestProjectPath("second.lun");

        _service.RecordProject(first);
        _service.RecordProject(second);

        var recents = _service.GetRecentProjects();
        recents.Count.ShouldBe(2);
        recents[0].FilePath.ShouldBe(Path.GetFullPath(second));
        recents[1].FilePath.ShouldBe(Path.GetFullPath(first));
    }

    [Fact]
    public void RecordProject_ExistingPath_MovesToFrontWithoutDuplicate()
    {
        var first = TestProjectPath("first.lun");
        var second = TestProjectPath("second.lun");

        _service.RecordProject(first);
        _service.RecordProject(second);
        _service.RecordProject(first);

        var recents = _service.GetRecentProjects();
        recents.Count.ShouldBe(2);
        recents[0].FilePath.ShouldBe(Path.GetFullPath(first));
        recents[1].FilePath.ShouldBe(Path.GetFullPath(second));
    }

    [Fact]
    public void RecordProject_PathDifferingOnlyByCase_MatchesPlatformFilesystemSemantics()
    {
        var lower = TestProjectPath("design.lun");
        var upper = TestProjectPath("DESIGN.LUN");

        _service.RecordProject(lower);
        _service.RecordProject(upper);

        // Linux filesystems are case-sensitive: these are two distinct files.
        // Windows and (default) macOS are case-insensitive: same file, one entry.
        var expectedCount = OperatingSystem.IsLinux() ? 2 : 1;
        _service.GetRecentProjects().Count.ShouldBe(expectedCount);
    }

    [Fact]
    public void RecordProject_BeyondMaximum_DropsOldestEntries()
    {
        var overflow = RecentProjectsService.MaxRecentProjects + 2;
        for (int i = 0; i < overflow; i++)
        {
            _service.RecordProject(TestProjectPath($"design-{i}.lun"));
        }

        var recents = _service.GetRecentProjects();
        recents.Count.ShouldBe(RecentProjectsService.MaxRecentProjects);
        recents[0].FilePath.ShouldBe(Path.GetFullPath(TestProjectPath($"design-{overflow - 1}.lun")));
        recents.ShouldAllBe(e => e.FilePath != Path.GetFullPath(TestProjectPath("design-0.lun")));
        recents.ShouldAllBe(e => e.FilePath != Path.GetFullPath(TestProjectPath("design-1.lun")));
    }

    [Fact]
    public void RecordProject_RelativePath_IsNormalizedToFullPath()
    {
        _service.RecordProject("relative-design.lun");

        _service.GetRecentProjects()[0].FilePath
            .ShouldBe(Path.GetFullPath("relative-design.lun"));
    }

    [Fact]
    public void RecordProject_StampsLastOpenedFromInjectedClock()
    {
        _service.RecordProject(TestProjectPath("stamped.lun"));

        _service.GetRecentProjects()[0].LastOpenedUtc.ShouldBe(FixedUtcNow);
    }

    [Fact]
    public void RecordProject_PersistsAcrossServiceInstances()
    {
        var path = TestProjectPath("persisted.lun");
        _service.RecordProject(path);

        var reloadedPreferences = new UserPreferencesService(_testPreferencesPath);
        var reloadedService = new RecentProjectsService(reloadedPreferences, () => FixedUtcNow);

        var recents = reloadedService.GetRecentProjects();
        recents.Count.ShouldBe(1);
        recents[0].FilePath.ShouldBe(Path.GetFullPath(path));
        recents[0].LastOpenedUtc.ShouldBe(FixedUtcNow);
    }

    [Fact]
    public void RemoveProject_RemovesEntryAndPersists()
    {
        var keep = TestProjectPath("keep.lun");
        var remove = TestProjectPath("remove.lun");
        _service.RecordProject(keep);
        _service.RecordProject(remove);

        _service.RemoveProject(remove);

        _service.GetRecentProjects().ShouldHaveSingleItem()
            .FilePath.ShouldBe(Path.GetFullPath(keep));

        var reloaded = new RecentProjectsService(
            new UserPreferencesService(_testPreferencesPath), () => FixedUtcNow);
        reloaded.GetRecentProjects().ShouldHaveSingleItem()
            .FilePath.ShouldBe(Path.GetFullPath(keep));
    }

    [Fact]
    public void ClearAll_EmptiesListAndPersists()
    {
        _service.RecordProject(TestProjectPath("a.lun"));
        _service.RecordProject(TestProjectPath("b.lun"));

        _service.ClearAll();

        _service.GetRecentProjects().ShouldBeEmpty();

        var reloaded = new RecentProjectsService(
            new UserPreferencesService(_testPreferencesPath), () => FixedUtcNow);
        reloaded.GetRecentProjects().ShouldBeEmpty();
    }
}
