using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Hierarchy;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Components.Creation;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

/// <summary>
/// Covers <see cref="LeftPanelViewModel.BeginBatchRegistration"/>: bulk registrations
/// (a GDS import registering hundreds of drafts) must run the filtered-list rebuild and
/// the preferences write ONCE per batch instead of once per draft. Each
/// <c>FilterComponents</c> run clears <see cref="LeftPanelViewModel.FilteredTemplates"/>,
/// so counting its Reset events counts the refreshes.
/// </summary>
public class LeftPanelBatchRegistrationTests : IDisposable
{
    private const string PdkName = "GDS Import - chip";

    private readonly string _prefsPath =
        Path.Combine(Path.GetTempPath(), $"lp-batch-prefs-{Guid.NewGuid():N}.json");
    private readonly string _pdkFilePath =
        Path.Combine(Path.GetTempPath(), $"lp-batch-pdk-{Guid.NewGuid():N}.json");

    public void Dispose() { if (File.Exists(_prefsPath)) File.Delete(_prefsPath); }

    private (LeftPanelViewModel Vm, UserPreferencesService Preferences) CreateLeftPanel()
    {
        var canvas = new DesignCanvasViewModel();
        var libraryManager = new GroupLibraryManager();
        var preferences = new UserPreferencesService(_prefsPath);
        var vm = new LeftPanelViewModel(
            canvas, libraryManager, new PdkLoader(), preferences,
            new HierarchyPanelViewModel(canvas),
            new PdkManagerViewModel(),
            new ComponentLibraryViewModel(libraryManager));
        return (vm, preferences);
    }

    private static PdkComponentDraft Draft(string name) => new()
    {
        Name = name,
        Category = "Custom",
        NazcaFunction = "test.straight",
        WidthMicrometers = 10,
        HeightMicrometers = 2,
    };

    [Fact]
    public void ThreeDraftsInsideBatchScope_RefreshAndSaveRunOnceAtScopeEnd()
    {
        var (vm, preferences) = CreateLeftPanel();
        var filterRuns = 0;
        vm.FilteredTemplates.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset) filterRuns++;
        };

        using (vm.BeginBatchRegistration())
        {
            vm.RegisterSavedCustomComponent(Draft("wg1"), PdkName, _pdkFilePath);
            vm.RegisterSavedCustomComponent(Draft("wg2"), PdkName, _pdkFilePath);
            vm.RegisterSavedCustomComponent(Draft("wg3"), PdkName, _pdkFilePath);

            filterRuns.ShouldBe(0, "the list refresh must be deferred while the scope is open");
            preferences.GetEnabledPdks().ShouldBeEmpty("the preferences write must be deferred too");
        }

        filterRuns.ShouldBe(1, "one deferred refresh at scope end, not one per draft");
        vm.FilteredTemplates.Select(t => t.Name)
            .ShouldBe(new[] { "wg1", "wg2", "wg3" }, ignoreOrder: true);
        preferences.GetEnabledPdks().ShouldContain(PdkName);
    }

    [Fact]
    public void NestedScopes_RefreshOnlyWhenTheOutermostCloses()
    {
        var (vm, _) = CreateLeftPanel();
        var filterRuns = 0;
        vm.FilteredTemplates.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset) filterRuns++;
        };

        var outer = vm.BeginBatchRegistration();
        var inner = vm.BeginBatchRegistration();
        vm.RegisterSavedCustomComponent(Draft("wg1"), PdkName, _pdkFilePath);
        inner.Dispose();
        filterRuns.ShouldBe(0, "an inner scope closing must not flush the outer batch");

        vm.RegisterSavedCustomComponent(Draft("wg2"), PdkName, _pdkFilePath);
        outer.Dispose();

        filterRuns.ShouldBe(1);
        vm.FilteredTemplates.Count.ShouldBe(2);
    }

    [Fact]
    public void ScopeDisposedTwice_LaterRegistrationsRefreshImmediatelyAgain()
    {
        var (vm, _) = CreateLeftPanel();
        var filterRuns = 0;
        vm.FilteredTemplates.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset) filterRuns++;
        };

        var scope = vm.BeginBatchRegistration();
        vm.RegisterSavedCustomComponent(Draft("wg1"), PdkName, _pdkFilePath);
        scope.Dispose();
        scope.Dispose();
        filterRuns.ShouldBe(1, "a double dispose must not rerun the deferred refresh");

        vm.RegisterSavedCustomComponent(Draft("wg2"), PdkName, _pdkFilePath);
        filterRuns.ShouldBe(2, "outside any scope each registration refreshes immediately");
    }

    [Fact]
    public void EmptyScope_TriggersNoRefreshOnDispose()
    {
        var (vm, _) = CreateLeftPanel();
        var filterRuns = 0;
        vm.FilteredTemplates.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset) filterRuns++;
        };

        using (vm.BeginBatchRegistration()) { }

        filterRuns.ShouldBe(0, "nothing was registered, so there is nothing to refresh");
    }
}
