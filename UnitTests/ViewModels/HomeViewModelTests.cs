using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Home;
using Shouldly;

namespace UnitTests.ViewModels;

/// <summary>
/// Unit tests for <see cref="HomeViewModel"/> — the startup Home screen shown
/// inside the main window. Verifies visibility lifecycle, the recent-projects
/// list (ordering, missing-file marking, remove/clear), and that the
/// New/Open/Open-recent actions delegate to the wired callbacks.
/// </summary>
public class HomeViewModelTests : IDisposable
{
    private readonly string _testPreferencesPath;
    private readonly string _existingDesignPath;
    private readonly UserPreferencesService _preferences;
    private readonly RecentProjectsService _recentProjects;

    private readonly string _emptyExamplesBase;

    public HomeViewModelTests()
    {
        _testPreferencesPath = Path.Combine(Path.GetTempPath(), $"test-home-prefs-{Guid.NewGuid()}.json");
        _existingDesignPath = Path.Combine(Path.GetTempPath(), $"test-home-design-{Guid.NewGuid():N}.lun");
        _emptyExamplesBase = Path.Combine(Path.GetTempPath(), $"test-home-noexamples-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_emptyExamplesBase);
        File.WriteAllText(_existingDesignPath, "{}");
        _preferences = new UserPreferencesService(_testPreferencesPath);
        _recentProjects = new RecentProjectsService(_preferences);
    }

    public void Dispose()
    {
        foreach (var path in new[] { _testPreferencesPath, _existingDesignPath })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        if (Directory.Exists(_emptyExamplesBase))
        {
            Directory.Delete(_emptyExamplesBase, recursive: true);
        }
    }

    private static string MissingDesignPath() =>
        Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.lun");

    // Hermetic: examples rooted in an empty temp dir so the repo's real
    // examples/ folder can't leak into these tests via the walk-up discovery.
    private HomeViewModel CreateHomeViewModel() =>
        new(_recentProjects, _preferences, new ExampleDesignsService(_emptyExamplesBase));

    [Fact]
    public void Constructor_StartsVisible()
    {
        CreateHomeViewModel().IsHomeVisible.ShouldBeTrue();
    }

    [Fact]
    public void Constructor_PopulatesRecentProjectsMostRecentFirst()
    {
        var older = MissingDesignPath();
        _recentProjects.RecordProject(older);
        _recentProjects.RecordProject(_existingDesignPath);

        var home = CreateHomeViewModel();

        home.RecentProjects.Count.ShouldBe(2);
        home.RecentProjects[0].FullPath.ShouldBe(Path.GetFullPath(_existingDesignPath));
        home.RecentProjects[1].FullPath.ShouldBe(Path.GetFullPath(older));
    }

    [Fact]
    public void RecentItem_ForMissingFile_IsMarkedMissing()
    {
        _recentProjects.RecordProject(MissingDesignPath());
        _recentProjects.RecordProject(_existingDesignPath);

        var home = CreateHomeViewModel();

        home.RecentProjects[0].IsMissing.ShouldBeFalse();
        home.RecentProjects[1].IsMissing.ShouldBeTrue();
    }

    [Fact]
    public async Task OpenRecentProject_ExistingFile_InvokesDelegateWithPath()
    {
        _recentProjects.RecordProject(_existingDesignPath);
        var home = CreateHomeViewModel();

        string? requestedPath = null;
        home.OpenProjectFromPathRequested = path =>
        {
            requestedPath = path;
            return Task.FromResult(true);
        };

        await home.OpenRecentProjectCommand.ExecuteAsync(home.RecentProjects[0]);

        requestedPath.ShouldBe(Path.GetFullPath(_existingDesignPath));
    }

    [Fact]
    public async Task OpenRecentProject_FileDeletedAfterListWasBuilt_MarksMissingWithoutInvokingDelegate()
    {
        var disappearing = Path.Combine(Path.GetTempPath(), $"disappearing-{Guid.NewGuid():N}.lun");
        File.WriteAllText(disappearing, "{}");
        _recentProjects.RecordProject(disappearing);
        var home = CreateHomeViewModel();
        var item = home.RecentProjects[0];
        item.IsMissing.ShouldBeFalse("file exists when the list is built");

        // The file disappears after the list was built
        File.Delete(disappearing);

        var invoked = false;
        home.OpenProjectFromPathRequested = _ =>
        {
            invoked = true;
            return Task.FromResult(true);
        };

        await home.OpenRecentProjectCommand.ExecuteAsync(item);

        invoked.ShouldBeFalse();
        item.IsMissing.ShouldBeTrue("the click-time re-check must flag the vanished file");
    }

    [Fact]
    public async Task HomeActionThatLeavesHomeVisible_RefreshesRecentsList()
    {
        var home = CreateHomeViewModel();
        home.RecentProjects.ShouldBeEmpty();

        // e.g. Open → unsaved-changes prompt → user saves → picker cancelled:
        // a recent got recorded but Home stays visible.
        home.OpenProjectRequested = () =>
        {
            _recentProjects.RecordProject(_existingDesignPath);
            return Task.CompletedTask;
        };

        await home.OpenProjectCommand.ExecuteAsync(null);

        home.RecentProjects.ShouldContain(
            i => i.FullPath == Path.GetFullPath(_existingDesignPath),
            "the visible list must not go stale after a Home-initiated action");
    }

    [Fact]
    public void RemoveRecentProject_RemovesFromListAndService()
    {
        _recentProjects.RecordProject(_existingDesignPath);
        var home = CreateHomeViewModel();

        home.RemoveRecentProjectCommand.Execute(home.RecentProjects[0]);

        home.RecentProjects.ShouldBeEmpty();
        _recentProjects.GetRecentProjects().ShouldBeEmpty();
    }

    [Fact]
    public void ClearRecentProjects_EmptiesListAndService()
    {
        _recentProjects.RecordProject(MissingDesignPath());
        _recentProjects.RecordProject(_existingDesignPath);
        var home = CreateHomeViewModel();

        home.ClearRecentProjectsCommand.Execute(null);

        home.RecentProjects.ShouldBeEmpty();
        _recentProjects.GetRecentProjects().ShouldBeEmpty();
    }

    [Fact]
    public void ContinueWithoutProject_HidesHome()
    {
        var home = CreateHomeViewModel();

        home.ContinueWithoutProjectCommand.Execute(null);

        home.IsHomeVisible.ShouldBeFalse();
    }

    [Fact]
    public void OnProjectOpened_HidesHome()
    {
        var home = CreateHomeViewModel();

        home.OnProjectOpened();

        home.IsHomeVisible.ShouldBeFalse();
    }

    [Fact]
    public void Show_RefreshesRecentListAndBecomesVisible()
    {
        var home = CreateHomeViewModel();
        home.OnProjectOpened();
        _recentProjects.RecordProject(_existingDesignPath);

        home.Show();

        home.IsHomeVisible.ShouldBeTrue();
        home.RecentProjects.ShouldHaveSingleItem()
            .FullPath.ShouldBe(Path.GetFullPath(_existingDesignPath));
    }

    [Fact]
    public async Task NewProjectCommand_InvokesDelegate()
    {
        var home = CreateHomeViewModel();
        var invoked = false;
        home.NewProjectRequested = () =>
        {
            invoked = true;
            return Task.CompletedTask;
        };

        await home.NewProjectCommand.ExecuteAsync(null);

        invoked.ShouldBeTrue();
    }

    [Fact]
    public async Task OpenProjectCommand_InvokesDelegate()
    {
        var home = CreateHomeViewModel();
        var invoked = false;
        home.OpenProjectRequested = () =>
        {
            invoked = true;
            return Task.CompletedTask;
        };

        await home.OpenProjectCommand.ExecuteAsync(null);

        invoked.ShouldBeTrue();
    }
}
