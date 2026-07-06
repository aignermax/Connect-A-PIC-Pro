using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Hierarchy;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Panels;
using CAP_Core.Components.Creation;
using CAP_Core.Components.Process;
using CAP_DataAccess.Components.ComponentDraftMapper;
using Shouldly;
using Xunit;

namespace UnitTests.ViewModels.Panels;

/// <summary>
/// Verifies that the component library filter follows the active fabrication process
/// (issue #570): a real process locks the enabled PDKs to its members and disables manual
/// toggling, while Playground/no-selection restores manual control.
/// </summary>
public class LibraryProcessFilterTests : IDisposable
{
    private readonly string _testPrefsPath;
    private readonly LeftPanelViewModel _leftPanel;

    public LibraryProcessFilterTests()
    {
        _testPrefsPath = Path.Combine(Path.GetTempPath(), $"LibraryProcessFilterPrefs_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testPrefsPath);

        var prefsFile = Path.Combine(_testPrefsPath, "user-preferences.json");
        var preferencesService = new UserPreferencesService();

        // Point the preferences service at an isolated temp file, same pattern as
        // LeftPanelWidthPersistenceTests, so this test never touches real user prefs.
        var pathField = typeof(UserPreferencesService).GetField("_preferencesFilePath",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var prefsField = typeof(UserPreferencesService).GetField("_preferences",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (pathField != null && prefsField != null)
        {
            pathField.SetValue(preferencesService, prefsFile);
            prefsField.SetValue(preferencesService, new UserPreferences());
        }

        var canvas = new DesignCanvasViewModel();
        var groupLibrary = new GroupLibraryManager();
        var pdkLoader = new PdkLoader();

        _leftPanel = new LeftPanelViewModel(canvas, groupLibrary, pdkLoader, preferencesService,
            new HierarchyPanelViewModel(canvas), new PdkManagerViewModel(),
            new ComponentLibraryViewModel(groupLibrary));
        _leftPanel.Initialize();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testPrefsPath))
        {
            try { Directory.Delete(_testPrefsPath, true); }
            catch { /* best effort cleanup */ }
        }
    }

    private string DemoPdkName() =>
        _leftPanel.PdkManager.LoadedPdks.First(p => p.Name.Contains("Demo", StringComparison.OrdinalIgnoreCase)).Name;

    [Fact]
    public void ApplyActiveProcess_RealProcess_EnablesOnlyItsMemberPdks_AndLocksToggles()
    {
        // Sanity check: bundled PDKs actually loaded (Demo PDK, SiEPIC EBeam PDK, Analysis Tools).
        _leftPanel.PdkManager.LoadedPdks.Count.ShouldBeGreaterThan(1);
        var demoPdkName = DemoPdkName();

        var active = new ActiveProcessSelection(
            DisplayName: "Demo Process",
            Fingerprint: null,
            MemberPdkNames: new List<string> { demoPdkName },
            IsPlayground: false);

        _leftPanel.ApplyActiveProcess(active);

        _leftPanel.PdkManager.ManualTogglesEnabled.ShouldBeFalse();

        var agnosticNames = _leftPanel.GetProcessAgnosticPdkNames();
        foreach (var pdk in _leftPanel.PdkManager.LoadedPdks)
        {
            var expectedEnabled = pdk.Name.Equals(demoPdkName, StringComparison.OrdinalIgnoreCase) ||
                agnosticNames.Contains(pdk.Name, StringComparer.OrdinalIgnoreCase);
            pdk.IsEnabled.ShouldBe(expectedEnabled);
        }

        _leftPanel.FilteredTemplates.Count.ShouldBeGreaterThan(0);
        _leftPanel.FilteredTemplates.ShouldAllBe(t =>
            t.PdkSource.Equals(demoPdkName, StringComparison.OrdinalIgnoreCase) ||
            agnosticNames.Contains(t.PdkSource, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void ApplyActiveProcess_RealProcess_KeepsProcessAgnosticToolsEnabledAndVisible()
    {
        var demoPdkName = DemoPdkName();
        var active = new ActiveProcessSelection(
            DisplayName: "Demo Process",
            Fingerprint: null,
            MemberPdkNames: new List<string> { demoPdkName },
            IsPlayground: false);

        _leftPanel.ApplyActiveProcess(active);

        _leftPanel.PdkManager.GetEnabledPdkNames().ShouldContain("Analysis Tools");
        _leftPanel.FilteredTemplates.ShouldContain(t => t.PdkSource.Equals("Analysis Tools", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ApplyActiveProcess_Playground_UnlocksManualToggles()
    {
        var demoPdkName = DemoPdkName();
        var active = new ActiveProcessSelection(
            DisplayName: "Demo Process",
            Fingerprint: null,
            MemberPdkNames: new List<string> { demoPdkName },
            IsPlayground: false);

        _leftPanel.ApplyActiveProcess(active);
        _leftPanel.PdkManager.ManualTogglesEnabled.ShouldBeFalse();

        _leftPanel.ApplyActiveProcess(ActiveProcessSelection.Playground());

        _leftPanel.PdkManager.ManualTogglesEnabled.ShouldBeTrue();
    }

    [Fact]
    public void ApplyActiveProcess_NullSelection_UnlocksManualToggles()
    {
        _leftPanel.PdkManager.ManualTogglesEnabled = false;

        _leftPanel.ApplyActiveProcess(null);

        _leftPanel.PdkManager.ManualTogglesEnabled.ShouldBeTrue();
    }
}
