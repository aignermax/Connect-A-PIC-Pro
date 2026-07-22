using CAP.Avalonia.Services;
using Shouldly;

namespace UnitTests.Services;

/// <summary>
/// Unit tests for pinning in <see cref="RecentProjectsService"/>: toggling and
/// persistence of the pinned flag, pin survival across re-records and cap
/// eviction, and that storage order stays MRU (display ordering is a
/// ViewModel concern).
/// </summary>
public class RecentProjectsPinTests : IDisposable
{
    private readonly string _testPreferencesPath;
    private readonly RecentProjectsService _service;

    public RecentProjectsPinTests()
    {
        _testPreferencesPath = Path.Combine(Path.GetTempPath(), $"test-pin-prefs-{Guid.NewGuid()}.json");
        _service = new RecentProjectsService(new UserPreferencesService(_testPreferencesPath));
    }

    public void Dispose()
    {
        if (File.Exists(_testPreferencesPath))
        {
            File.Delete(_testPreferencesPath);
        }
    }

    private static string TestProjectPath(string fileName) =>
        Path.Combine(Path.GetTempPath(), "lunima-pin-tests", fileName);

    [Fact]
    public void TogglePin_SetsPinnedAndPersists()
    {
        var path = TestProjectPath("pinned.lun");
        _service.RecordProject(path);

        _service.TogglePin(path);

        _service.GetRecentProjects().ShouldHaveSingleItem().Pinned.ShouldBeTrue();

        var reloaded = new RecentProjectsService(new UserPreferencesService(_testPreferencesPath));
        reloaded.GetRecentProjects().ShouldHaveSingleItem().Pinned.ShouldBeTrue();
    }

    [Fact]
    public void TogglePin_Twice_Unpins()
    {
        var path = TestProjectPath("toggled.lun");
        _service.RecordProject(path);

        _service.TogglePin(path);
        _service.TogglePin(path);

        _service.GetRecentProjects().ShouldHaveSingleItem().Pinned.ShouldBeFalse();
    }

    [Fact]
    public void RecordProject_PreservesPinnedFlagWhenMovingToFront()
    {
        var pinned = TestProjectPath("keep-pin.lun");
        _service.RecordProject(pinned);
        _service.TogglePin(pinned);
        _service.RecordProject(TestProjectPath("other.lun"));

        _service.RecordProject(pinned);

        var recents = _service.GetRecentProjects();
        recents[0].FilePath.ShouldBe(Path.GetFullPath(pinned));
        recents[0].Pinned.ShouldBeTrue();
    }

    [Fact]
    public void Eviction_DropsOldestUnpinnedAndKeepsPinned()
    {
        var pinned = TestProjectPath("evict-survivor.lun");
        _service.RecordProject(pinned);
        _service.TogglePin(pinned);

        var overflow = RecentProjectsService.MaxRecentProjects + 2;
        for (int i = 0; i < overflow; i++)
        {
            _service.RecordProject(TestProjectPath($"filler-{i}.lun"));
        }

        var recents = _service.GetRecentProjects();
        recents.Count.ShouldBe(RecentProjectsService.MaxRecentProjects);
        recents.ShouldContain(e => e.FilePath == Path.GetFullPath(pinned) && e.Pinned);
        // The oldest unpinned fillers were evicted instead of the pinned entry
        recents.ShouldAllBe(e => e.FilePath != Path.GetFullPath(TestProjectPath("filler-0.lun")));
    }

    [Fact]
    public void RecordProject_AllOtherEntriesPinned_NeverEvictsTheJustOpenedProject()
    {
        for (int i = 0; i < RecentProjectsService.MaxRecentProjects; i++)
        {
            var pinnedPath = TestProjectPath($"pinned-{i}.lun");
            _service.RecordProject(pinnedPath);
            _service.TogglePin(pinnedPath);
        }

        var justOpened = TestProjectPath("just-opened.lun");
        _service.RecordProject(justOpened);

        var recents = _service.GetRecentProjects();
        recents[0].FilePath.ShouldBe(Path.GetFullPath(justOpened));
        recents.Count.ShouldBe(RecentProjectsService.MaxRecentProjects + 1);
    }

    [Fact]
    public void GetRecentProjects_KeepsMruOrderRegardlessOfPin()
    {
        var older = TestProjectPath("older.lun");
        var newer = TestProjectPath("newer.lun");
        _service.RecordProject(older);
        _service.RecordProject(newer);

        _service.TogglePin(older);

        var recents = _service.GetRecentProjects();
        recents[0].FilePath.ShouldBe(Path.GetFullPath(newer));
        recents[1].FilePath.ShouldBe(Path.GetFullPath(older));
    }
}
