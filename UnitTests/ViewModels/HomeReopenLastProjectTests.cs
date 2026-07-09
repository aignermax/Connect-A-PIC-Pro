using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Home;
using Shouldly;

namespace UnitTests.ViewModels;

/// <summary>
/// Unit tests for the "reopen last project on startup" preference:
/// persistence via <see cref="UserPreferencesService"/>, the checkbox-backed
/// <see cref="HomeViewModel.ReopenLastProjectOnStartup"/> property, and
/// <see cref="HomeViewModel.TryReopenLastProjectAsync"/> startup behavior.
/// </summary>
public class HomeReopenLastProjectTests : IDisposable
{
    private readonly string _testPreferencesPath;
    private readonly string _existingDesignPath;
    private readonly UserPreferencesService _preferences;
    private readonly RecentProjectsService _recentProjects;

    private readonly string _emptyExamplesBase;

    public HomeReopenLastProjectTests()
    {
        _testPreferencesPath = Path.Combine(Path.GetTempPath(), $"test-reopen-prefs-{Guid.NewGuid()}.json");
        _existingDesignPath = Path.Combine(Path.GetTempPath(), $"test-reopen-design-{Guid.NewGuid():N}.lun");
        _emptyExamplesBase = Path.Combine(Path.GetTempPath(), $"test-reopen-noexamples-{Guid.NewGuid():N}");
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

    // Hermetic: examples rooted in an empty temp dir (see HomeViewModelTests).
    private HomeViewModel CreateHomeViewModel() =>
        new(_recentProjects, _preferences, new ExampleDesignsService(_emptyExamplesBase));

    [Fact]
    public void ReopenLastProjectOnStartup_DefaultsToFalse()
    {
        CreateHomeViewModel().ReopenLastProjectOnStartup.ShouldBeFalse();
    }

    [Fact]
    public void SettingReopenLastProjectOnStartup_PersistsAcrossInstances()
    {
        CreateHomeViewModel().ReopenLastProjectOnStartup = true;

        var reloadedPreferences = new UserPreferencesService(_testPreferencesPath);
        var reloadedHome = new HomeViewModel(
            new RecentProjectsService(reloadedPreferences), reloadedPreferences);

        reloadedHome.ReopenLastProjectOnStartup.ShouldBeTrue();
    }

    [Fact]
    public async Task TryReopen_PreferenceDisabled_ReturnsFalseWithoutInvokingDelegate()
    {
        _recentProjects.RecordProject(_existingDesignPath);
        var home = CreateHomeViewModel();
        var invoked = false;
        home.OpenProjectFromPathRequested = _ => { invoked = true; return Task.FromResult(true); };

        (await home.TryReopenLastProjectAsync()).ShouldBeFalse();

        invoked.ShouldBeFalse();
    }

    [Fact]
    public async Task TryReopen_NoRecentProjects_ReturnsFalse()
    {
        var home = CreateHomeViewModel();
        home.ReopenLastProjectOnStartup = true;
        home.OpenProjectFromPathRequested = _ => Task.FromResult(true);

        (await home.TryReopenLastProjectAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task TryReopen_LastProjectFileMissing_ReturnsFalseWithoutInvokingDelegate()
    {
        _recentProjects.RecordProject(
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.lun"));
        var home = CreateHomeViewModel();
        home.ReopenLastProjectOnStartup = true;
        var invoked = false;
        home.OpenProjectFromPathRequested = _ => { invoked = true; return Task.FromResult(true); };

        (await home.TryReopenLastProjectAsync()).ShouldBeFalse();

        invoked.ShouldBeFalse();
    }

    [Fact]
    public async Task TryReopen_PreferenceEnabled_OpensMostRecentProject()
    {
        _recentProjects.RecordProject(
            Path.Combine(Path.GetTempPath(), $"older-{Guid.NewGuid():N}.lun"));
        _recentProjects.RecordProject(_existingDesignPath);
        var home = CreateHomeViewModel();
        home.ReopenLastProjectOnStartup = true;

        string? requestedPath = null;
        home.OpenProjectFromPathRequested = path =>
        {
            requestedPath = path;
            return Task.FromResult(true);
        };

        (await home.TryReopenLastProjectAsync()).ShouldBeTrue();

        requestedPath.ShouldBe(Path.GetFullPath(_existingDesignPath));
    }

    [Fact]
    public async Task TryReopen_DelegateNotWired_ReturnsFalseWithoutThrowing()
    {
        _recentProjects.RecordProject(_existingDesignPath);
        var home = CreateHomeViewModel();
        home.ReopenLastProjectOnStartup = true;

        (await home.TryReopenLastProjectAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task TryReopen_DelegateReportsFailure_ReturnsFalse()
    {
        _recentProjects.RecordProject(_existingDesignPath);
        var home = CreateHomeViewModel();
        home.ReopenLastProjectOnStartup = true;
        home.OpenProjectFromPathRequested = _ => Task.FromResult(false);

        (await home.TryReopenLastProjectAsync()).ShouldBeFalse();
    }
}
