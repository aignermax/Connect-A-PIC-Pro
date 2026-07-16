using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CAP.Avalonia.Services;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.ViewModels.Library;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

/// <summary>
/// The registrar's stale-template replacement must match the PDK name case-insensitively,
/// like every other PDK-name comparison in the save flow — otherwise a casing difference
/// between the on-disk PDK name and the registered template's PdkSource resurrects the
/// duplicate/stale-template bug the replacement exists to fix.
/// </summary>
public class RegistrarPdkNameCasingTests : IDisposable
{
    private readonly string _prefsPath =
        Path.Combine(Path.GetTempPath(), $"registrar-casing-prefs-{Guid.NewGuid():N}.json");

    public void Dispose() { if (File.Exists(_prefsPath)) File.Delete(_prefsPath); }

    private static PdkComponentDraft Draft(string name) => new()
    {
        Name = name,
        Category = "Test",
        NazcaFunction = "test.straight",
        WidthMicrometers = 10,
        HeightMicrometers = 2,
        Pins = new List<PhysicalPinDraft>
        {
            new() { Name = "a0", OffsetXMicrometers = 0, OffsetYMicrometers = 1, AngleDegrees = 180 },
            new() { Name = "b0", OffsetXMicrometers = 10, OffsetYMicrometers = 1, AngleDegrees = 0 },
        },
    };

    [Fact]
    public void Register_replacesTheStaleTemplate_evenWhenThePdkNameCasingDiffers()
    {
        var allTemplates = new ObservableCollection<ComponentTemplate>();
        var categories = new ObservableCollection<string>();
        var pdkManager = new PdkManagerViewModel();
        var preferences = new UserPreferencesService(_prefsPath);
        var loader = new PdkLoader();
        var drafts = new List<PdkDraft>();
        var filePath = Path.Combine(Path.GetTempPath(), $"registrar-casing-{Guid.NewGuid():N}.json");

        CustomComponentLibraryRegistrar.Register(
            Draft("Straight A"), "My Lib", filePath,
            allTemplates, categories, pdkManager, preferences, loader, drafts, () => { }, () => { });

        // Re-registration of the same component with a differently-cased PDK name (the name is
        // re-read from the PDK file on every save) must REPLACE, not duplicate.
        CustomComponentLibraryRegistrar.Register(
            Draft("Straight A"), "MY LIB", filePath,
            allTemplates, categories, pdkManager, preferences, loader, drafts, () => { }, () => { });

        allTemplates.Count(t => string.Equals(t.Name, "Straight A", StringComparison.OrdinalIgnoreCase))
            .ShouldBe(1, "the stale template must be replaced, not listed twice");
    }
}
