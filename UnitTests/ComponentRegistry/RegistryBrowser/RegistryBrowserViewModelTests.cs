using CAP.Avalonia.ViewModels.ComponentRegistry.RegistryBrowser;
using Shouldly;
using UnitTests.ComponentRegistry.RegistryClient;
using Xunit;

namespace UnitTests.ComponentRegistry.RegistryBrowser;

/// <summary>
/// Tests for <see cref="RegistryBrowserViewModel"/> (issue #656) against a
/// stubbed registry client fed by the committed fixture files — no network.
/// </summary>
public class RegistryBrowserViewModelTests : IDisposable
{
    private readonly RegistryTestHarness _harness = new();

    private RegistryBrowserViewModel CreateViewModel() => new(_harness.CreateClient());

    [Fact]
    public async Task Load_ListsAllDemoComponents_WithTierBadgesAndStatus()
    {
        var vm = CreateViewModel();

        await vm.LoadCommand.ExecuteAsync(null);

        vm.Components.Count.ShouldBe(5);
        vm.ErrorMessage.ShouldBeEmpty();
        vm.IsLoading.ShouldBeFalse();
        // Fixture demo components: simulated ✓, geometry ✗, measured ✗, status demo.
        foreach (var item in vm.Components)
        {
            item.HasSimulated.ShouldBeTrue(item.Id);
            item.HasGeometry.ShouldBeFalse(item.Id);
            item.HasMeasured.ShouldBeFalse(item.Id);
            item.Status.ShouldBe("demo");
            item.ProcessId.ShouldBe("generic-si220");
            item.TiersText.ShouldBe("geometry \u2717 \u00b7 simulated \u2713 \u00b7 measured \u2717");
        }
        // Ordered by name for stable browsing.
        vm.Components.Select(c => c.Name).ShouldBe(vm.Components.Select(c => c.Name).OrderBy(n => n));
    }

    [Fact]
    public async Task Load_NetworkFailureWithoutCache_ShowsErrorState_DoesNotThrow()
    {
        _harness.Handler.SimulateNetworkFailure = true;
        var vm = CreateViewModel();

        await vm.LoadCommand.ExecuteAsync(null);

        vm.Components.ShouldBeEmpty();
        vm.ErrorMessage.ShouldStartWith("Could not load the registry:");
        vm.IsLoading.ShouldBeFalse();
    }

    [Fact]
    public async Task Refresh_NetworkFailureWithCache_ServesCachedData_WithOfflineNote()
    {
        var vm = CreateViewModel();
        await vm.LoadCommand.ExecuteAsync(null); // Populates the cache.

        _harness.Handler.SimulateNetworkFailure = true;
        await vm.RefreshCommand.ExecuteAsync(null);

        vm.Components.Count.ShouldBe(5);
        vm.ErrorMessage.ShouldBeEmpty();
        vm.SourceNote.ShouldBe("Offline \u2014 showing cached registry data.");
    }

    [Fact]
    public async Task Refresh_FailureWithoutCache_KeepsExistingList_AndShowsError()
    {
        // Own cache directory so it can be wiped to force a hard failure
        // (network down AND no cached copy) after a successful first load.
        var cacheDir = Path.Combine(
            Path.GetTempPath(), "lunima-registry-browser-tests", Guid.NewGuid().ToString("N"));
        var client = new CAP_Core.ComponentRegistry.RegistryClient.RegistryClient(
            new HttpClient(_harness.Handler),
            new CAP_Core.ComponentRegistry.RegistryClient.RegistryCache(cacheDir),
            logger: null, baseUrl: RegistryTestHarness.BaseUrl);
        var vm = new RegistryBrowserViewModel(client);
        await vm.LoadCommand.ExecuteAsync(null);
        vm.Components.Count.ShouldBe(5);

        Directory.Delete(cacheDir, recursive: true);
        _harness.Handler.SimulateNetworkFailure = true;
        await vm.RefreshCommand.ExecuteAsync(null);

        // Non-blocking error: the previously listed components stay visible.
        vm.Components.Count.ShouldBe(5);
        vm.ErrorMessage.ShouldStartWith("Could not load the registry:");
    }

    [Fact]
    public async Task Refresh_BypassesCache_AndHitsNetworkAgain()
    {
        var vm = CreateViewModel();
        await vm.LoadCommand.ExecuteAsync(null);
        var requestsAfterLoad = _harness.Handler.RequestCount;

        await vm.RefreshCommand.ExecuteAsync(null);

        _harness.Handler.RequestCount.ShouldBe(requestsAfterLoad + 1);
        vm.SourceNote.ShouldBeEmpty(); // Fresh network data — no cache note.
    }

    [Fact]
    public async Task ActiveProcessId_FlagsMismatchedComponents()
    {
        var vm = CreateViewModel();
        await vm.LoadCommand.ExecuteAsync(null);

        vm.Components.ShouldAllBe(c => !c.IsProcessMismatch); // No process loaded.

        vm.ActiveProcessId = "my-inhouse-fab";
        vm.Components.ShouldAllBe(c => c.IsProcessMismatch);

        vm.ActiveProcessId = "GENERIC-SI220"; // Case-insensitive match.
        vm.Components.ShouldAllBe(c => !c.IsProcessMismatch);

        vm.ActiveProcessId = null;
        vm.Components.ShouldAllBe(c => !c.IsProcessMismatch);
    }

    [Fact]
    public async Task SelectingComponent_LoadsDetails_WithParametersAndProvenance()
    {
        var vm = CreateViewModel();
        await vm.LoadCommand.ExecuteAsync(null);
        var yBranch = vm.Components.Single(c => c.Id == "y-branch-1x2");

        vm.SelectedComponent = yBranch;
        await vm.DetailsLoadTask;

        vm.Details.ErrorMessage.ShouldBeEmpty();
        vm.Details.Description.ShouldNotBeEmpty();
        vm.Details.PortsText.ShouldContain("optical ports");
        vm.Details.HasArtifacts.ShouldBeTrue();
        var artifact = vm.Details.Artifacts.First();
        artifact.Tier.ShouldBe("simulated");
        artifact.Status.ShouldBe("demo");
        artifact.Provenance.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task DeselectingComponent_ClearsDetails()
    {
        var vm = CreateViewModel();
        await vm.LoadCommand.ExecuteAsync(null);
        vm.SelectedComponent = vm.Components.Single(c => c.Id == "y-branch-1x2");
        await vm.DetailsLoadTask;

        vm.SelectedComponent = null;

        vm.Details.Description.ShouldBeEmpty();
        vm.Details.Artifacts.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExpandingPanel_TriggersInitialLoad()
    {
        var vm = CreateViewModel();

        vm.IsExpanded = true;
        await vm.IndexLoadTask;

        vm.Components.Count.ShouldBe(5);
    }

    [Fact]
    public void StatusColors_MapAllKnownStatuses()
    {
        RegistryStatusPresentation.ToColor("demo").ShouldBe("#8a6d3b");
        RegistryStatusPresentation.ToColor("verified").ShouldBe("#3d6d3d");
        RegistryStatusPresentation.ToColor("unverified").ShouldBe("#555555");
        RegistryStatusPresentation.ToColor("disputed").ShouldBe("#8a3d3d");
        RegistryStatusPresentation.ToColor("withdrawn").ShouldBe("#5d3d5d");
        RegistryStatusPresentation.ToColor("something-new").ShouldBe("#555555");
    }

    public void Dispose() => _harness.Dispose();
}
