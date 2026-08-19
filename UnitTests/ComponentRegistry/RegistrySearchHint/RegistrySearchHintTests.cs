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
using Xunit;

namespace UnitTests.ComponentRegistry.RegistrySearchHint;

/// <summary>
/// Tests for the online-registry link row under the library search hits (issue
/// #772): the hint matches ONLY against the registry browser's already-loaded
/// in-memory index (no network roundtrip) — zero hits or an empty search hide
/// the row, while before the first successful load a neutral prompt keeps the
/// online registry discoverable.
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
        var registry = new RegistryBrowserViewModel(
            new CAP_Core.ComponentRegistry.RegistryClient.RegistryClient(new HttpClient()));
        if (entries.Length > 0)
            registry.HasIndexLoaded = true;
        foreach (var entry in entries)
            registry.Components.Add(new RegistryComponentItemViewModel(entry));
        return registry;
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
}
