using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.ComponentRegistry.RegistryBrowser;
using CAP.Avalonia.ViewModels.Hierarchy;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.ComponentRegistry.RegistryClient;
using CAP_Core.Components.Creation;
using CAP_DataAccess.Components.ComponentDraftMapper;
using Shouldly;
using UnitTests.ComponentRegistry.RegistryClient;
using Xunit;

namespace UnitTests.ComponentRegistry.RegistrySearchHint;

/// <summary>
/// Tests for the online-registry link row under the library search hits (issue
/// #772): the hint matches ONLY against the registry browser's locally known
/// index — the already-loaded in-memory copy, otherwise the on-disk cache (no
/// network roundtrip either way). Zero hits or an empty search hide the row,
/// while without any locally known index a neutral prompt keeps the online
/// registry discoverable.
/// </summary>
public class RegistrySearchHintTests
{
    private readonly DesignCanvasViewModel _canvas = new();
    private readonly GroupLibraryManager _libraryManager = new();
    private readonly PdkLoader _pdkLoader = new();
    private readonly UserPreferencesService _preferencesService;
    private readonly string _testPreferencesPath;

    /// <summary>
    /// The hint text is localized via LocalizationService.Instance, so pin
    /// English to keep the exact-string assertions culture-independent.
    /// </summary>
    public RegistrySearchHintTests()
    {
        // Isolated temp-file prefs — the real user preferences must never be touched.
        _testPreferencesPath = Path.Combine(Path.GetTempPath(), $"registry-hint-prefs-{Guid.NewGuid()}.json");
        _preferencesService = new UserPreferencesService(_testPreferencesPath);
        CAP.Avalonia.Services.Localization.LocalizationService.Instance.SetLanguage(
            CAP.Avalonia.Services.Localization.SupportedLanguage.English.Code);
    }

    private LeftPanelViewModel CreateLeftPanel(RegistryBrowserViewModel? registry) =>
        new(_canvas, _libraryManager, _pdkLoader, _preferencesService,
            new HierarchyPanelViewModel(_canvas),
            new PdkManagerViewModel(),
            new ComponentLibraryViewModel(_libraryManager),
            registryBrowser: registry);

    private static RegistryBrowserViewModel CreateRegistry(params RegistryIndexEntry[] entries)
    {
        // Throwaway cache dir: the disk-cache fallback must never observe the
        // developer machine's real registry cache — that would make the
        // not-loaded assertions environment-dependent.
        var registry = new RegistryBrowserViewModel(
            new CAP_Core.ComponentRegistry.RegistryClient.RegistryClient(
                new HttpClient(), ThrowawayCache()));
        if (entries.Length > 0)
            registry.HasIndexLoaded = true;
        foreach (var entry in entries)
            registry.Components.Add(new RegistryComponentItemViewModel(entry));
        return registry;
    }

    private static RegistryCache ThrowawayCache() =>
        new(Path.Combine(Path.GetTempPath(), $"registry-hint-cache-{Guid.NewGuid():N}"));

    /// <summary>
    /// Registry browser whose client shares the harness's warmed cache directory
    /// but has never loaded the index this session — the pre-load disk-cache path.
    /// </summary>
    private static async Task<RegistryBrowserViewModel> CreateDiskCachedRegistryAsync(
        RegistryTestHarness harness)
    {
        await harness.CreateClient().GetIndexAsync(); // Populates the shared cache directory.
        return new RegistryBrowserViewModel(harness.CreateClient());
    }

    private static RegistryIndexEntry Entry(string name, string description) =>
        new() { Name = name, Description = description };

    [Fact]
    public void EmptySearch_HidesHint()
    {
        var registry = CreateRegistry(Entry("Ring resonator", "All-pass microring."));
        var leftPanel = CreateLeftPanel(registry);

        leftPanel.SearchText = "";

        leftPanel.RegistrySearchHintText.ShouldBeEmpty();
    }

    [Fact]
    public void IndexNotLoaded_ShowsNeutralPrompt()
    {
        var leftPanel = CreateLeftPanel(CreateRegistry());

        leftPanel.SearchText = "ring resonator";

        leftPanel.RegistrySearchHintText.ShouldBe("Search the Component Registry…");
    }

    [Fact]
    public void NoRegistryWired_ShowsNeutralPrompt()
    {
        var leftPanel = CreateLeftPanel(registry: null);

        leftPanel.SearchText = "ring resonator";

        leftPanel.RegistrySearchHintText.ShouldBe("Search the Component Registry…");
    }

    [Fact]
    public void LoadedIndex_ShowsHitCount()
    {
        var registry = CreateRegistry(
            Entry("Ring resonator", "All-pass microring."),
            Entry("Ring filter", "Bus-coupled add-drop filter."),
            Entry("Y-branch splitter", "Power splitter."));
        var leftPanel = CreateLeftPanel(registry);

        leftPanel.SearchText = "ring";

        leftPanel.RegistrySearchHintText.ShouldBe("2 hits in the Component Registry…");
    }

    [Fact]
    public void LoadedIndex_NoHits_HidesHint()
    {
        var registry = CreateRegistry(Entry("Y-branch splitter", "Power splitter."));
        var leftPanel = CreateLeftPanel(registry);

        leftPanel.SearchText = "ring";

        leftPanel.RegistrySearchHintText.ShouldBeEmpty();
    }

    [Fact]
    public void LoadedIndex_MatchesDescriptionToo()
    {
        var registry = CreateRegistry(Entry("MZI", "Two ideal 50/50 couplers."));
        var leftPanel = CreateLeftPanel(registry);

        leftPanel.SearchText = "couplers";

        leftPanel.RegistrySearchHintText.ShouldBe("1 hits in the Component Registry…");
    }

    [Fact]
    public void IndexLoadedAfterSearch_UpgradesPromptToHitCount()
    {
        var registry = CreateRegistry(); // Never loaded yet.
        var leftPanel = CreateLeftPanel(registry);
        leftPanel.SearchText = "ring";
        leftPanel.RegistrySearchHintText.ShouldBe("Search the Component Registry…");

        registry.HasIndexLoaded = true;
        registry.Components.Add(new RegistryComponentItemViewModel(
            Entry("Ring resonator", "All-pass microring.")));

        leftPanel.RegistrySearchHintText.ShouldBe("1 hits in the Component Registry…");
    }

    [Fact]
    public void ClearingSearch_HidesHintAgain()
    {
        var registry = CreateRegistry(Entry("Ring resonator", "All-pass microring."));
        var leftPanel = CreateLeftPanel(registry);
        leftPanel.SearchText = "ring";
        leftPanel.RegistrySearchHintText.ShouldNotBeEmpty();

        leftPanel.SearchText = "";

        leftPanel.RegistrySearchHintText.ShouldBeEmpty();
    }

    [Fact]
    public async Task DiskCachedIndex_BeforeFirstLoad_ShowsHitCount()
    {
        using var harness = new RegistryTestHarness();
        var registry = await CreateDiskCachedRegistryAsync(harness); // Fixture index, never loaded in-session.
        var leftPanel = CreateLeftPanel(registry);

        // "coupler" matches the fixture's directional-coupler name and the MZI description.
        leftPanel.SearchText = "coupler";

        leftPanel.RegistrySearchHintText.ShouldBe("2 hits in the Component Registry…");
    }

    [Fact]
    public async Task DiskCachedIndex_MatchingNeverTouchesNetwork()
    {
        using var harness = new RegistryTestHarness();
        var registry = await CreateDiskCachedRegistryAsync(harness);
        var requestsAfterCaching = harness.Handler.RequestCount;
        var leftPanel = CreateLeftPanel(registry);

        leftPanel.SearchText = "coupler";
        leftPanel.SearchText = "ring";
        leftPanel.SearchText = "waveguide";

        harness.Handler.RequestCount.ShouldBe(requestsAfterCaching);
        leftPanel.RegistrySearchHintText.ShouldBe("1 hits in the Component Registry…");
    }

    [Fact]
    public async Task DiskCachedIndex_NoHits_ShowsNeutralPrompt()
    {
        using var harness = new RegistryTestHarness();
        var registry = await CreateDiskCachedRegistryAsync(harness);
        var leftPanel = CreateLeftPanel(registry);

        leftPanel.SearchText = "no-such-component";

        leftPanel.RegistrySearchHintText.ShouldBe("Search the Component Registry…");
    }

    [Fact]
    public async Task LoadedIndex_ShadowsDiskCache()
    {
        using var harness = new RegistryTestHarness();
        var registry = await CreateDiskCachedRegistryAsync(harness); // Disk: 2 "coupler" hits.
        var leftPanel = CreateLeftPanel(registry);
        leftPanel.SearchText = "coupler";
        leftPanel.RegistrySearchHintText.ShouldBe("2 hits in the Component Registry…");

        registry.HasIndexLoaded = true;
        registry.Components.Add(new RegistryComponentItemViewModel(
            Entry("Dual-ring coupler", "Add-drop through-port coupling.")));

        leftPanel.RegistrySearchHintText.ShouldBe("1 hits in the Component Registry…");
    }
}
