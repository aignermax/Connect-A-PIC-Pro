using CAP.Avalonia.Commands;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;
using CAP_Core.Components.Creation;
using CAP_Core.LightCalculation;
using CAP_DataAccess.Persistence.PIR;
using Shouldly;
using Xunit;

namespace UnitTests.Commands;

/// <summary>
/// Regression tests for issue #720: raw-code/override components inside a saved group
/// template must keep their geometry when the template is re-placed in another design.
/// The overrides travel with the template and are seeded into the target design's
/// override store under the new instance identifiers.
/// </summary>
public class PlaceGroupTemplateOverrideSeedingTests : IDisposable
{
    private const string RawCodeMarker = "nd.strt(length=123.5, width=0.45)";

    private readonly string _testLibraryPath;

    public PlaceGroupTemplateOverrideSeedingTests()
    {
        _testLibraryPath = Path.Combine(Path.GetTempPath(), $"GroupOvrTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testLibraryPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testLibraryPath))
            Directory.Delete(_testLibraryPath, true);
    }

    [Fact]
    public void SaveTemplate_WithOverrideProvider_PersistsOverrideJson()
    {
        var (group, overriddenId, sourceStore) = CreateGroupWithRawCodeOverride();
        var manager = new GroupLibraryManager(_testLibraryPath);

        var template = manager.SaveTemplate(group, "Ovr Template",
            nazcaOverrideJsonProvider: GroupTemplateNazcaOverrides.CreateJsonProvider(sourceStore));

        template.NazcaOverridesJson.Count.ShouldBe(1);
        template.NazcaOverridesJson.ShouldContainKey(overriddenId);

        // Reload from disk in a fresh manager (simulates another design/session)
        var loadManager = new GroupLibraryManager(_testLibraryPath);
        loadManager.LoadTemplates();
        var loaded = loadManager.Templates.Single(t => t.Name == "Ovr Template");
        loaded.NazcaOverridesJson.ShouldContainKey(overriddenId);
        GroupTemplateNazcaOverrides.Deserialize(loaded.NazcaOverridesJson[overriddenId])!
            .RawCode.ShouldContain(RawCodeMarker);
    }

    [Fact]
    public void PlaceLoadedTemplate_InEmptyDesign_SeedsOverrideUnderNewIdentifier()
    {
        var (loaded, loadManager, overriddenId) = SaveAndReloadTemplate();
        var canvas = new DesignCanvasViewModel();
        var targetStore = new Dictionary<string, NazcaCodeOverride>();

        var cmd = PlaceGroupTemplateCommand.TryCreate(
            canvas, loadManager, loaded, 100, 100, targetStore);
        cmd.ShouldNotBeNull();
        cmd.Execute();

        targetStore.Count.ShouldBe(1,
            "the template member's override must be seeded into the target design's store");
        var (seededId, seeded) = targetStore.Single();
        seededId.ShouldNotBe(overriddenId,
            "the override must be keyed by the NEW instance identifier, not the template's");
        seeded.RawCode.ShouldContain(RawCodeMarker);
        seeded.Backend.ShouldBe(OverrideBackend.Nazca);

        // The seeded identifier must belong to a child of the placed instance
        var placedGroup = (ComponentGroup)canvas.Components[0].Component;
        placedGroup.ChildComponents.Select(c => c.Identifier).ShouldContain(seededId);
    }

    [Fact]
    public void PlaceLoadedTemplate_ExportContainsRawCodeGeometry()
    {
        var (loaded, loadManager, _) = SaveAndReloadTemplate();
        var canvas = new DesignCanvasViewModel();
        var targetStore = new Dictionary<string, NazcaCodeOverride>();

        var cmd = PlaceGroupTemplateCommand.TryCreate(
            canvas, loadManager, loaded, 100, 100, targetStore);
        cmd.ShouldNotBeNull();
        cmd.Execute();

        var script = new SimpleNazcaExporter().Export(canvas, overrides: targetStore);

        // The export must emit the raw-code geometry of the re-placed override component
        script.ShouldContain(RawCodeMarker);
    }

    [Fact]
    public void Undo_RemovesOnlySeededOverrides()
    {
        var (loaded, loadManager, _) = SaveAndReloadTemplate();
        var canvas = new DesignCanvasViewModel();
        var preExisting = new NazcaCodeOverride { RawCode = "def component():\n    pass\n" };
        var targetStore = new Dictionary<string, NazcaCodeOverride> { ["existing_comp"] = preExisting };

        var cmd = PlaceGroupTemplateCommand.TryCreate(
            canvas, loadManager, loaded, 100, 100, targetStore);
        cmd.ShouldNotBeNull();
        cmd.Execute();
        targetStore.Count.ShouldBe(2);

        cmd.Undo();

        targetStore.Count.ShouldBe(1, "undo must remove only the overrides it seeded");
        targetStore["existing_comp"].ShouldBeSameAs(preExisting);
    }

    [Fact]
    public void ExecuteUndoExecute_SeedsOverrideAgain()
    {
        var (loaded, loadManager, _) = SaveAndReloadTemplate();
        var canvas = new DesignCanvasViewModel();
        var targetStore = new Dictionary<string, NazcaCodeOverride>();

        var cmd = PlaceGroupTemplateCommand.TryCreate(
            canvas, loadManager, loaded, 100, 100, targetStore);
        cmd.ShouldNotBeNull();

        cmd.Execute();
        cmd.Undo();
        targetStore.ShouldBeEmpty();
        cmd.Execute();

        targetStore.Count.ShouldBe(1, "re-executing after undo must seed the override again");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Saves a group with one raw-code-overridden member and reloads it from disk in a
    /// fresh manager, simulating the "other design" side of the issue-#720 scenario.
    /// </summary>
    private (GroupTemplate loaded, GroupLibraryManager loadManager, string overriddenId)
        SaveAndReloadTemplate()
    {
        var (group, overriddenId, sourceStore) = CreateGroupWithRawCodeOverride();
        new GroupLibraryManager(_testLibraryPath).SaveTemplate(group, "Ovr Template",
            nazcaOverrideJsonProvider: GroupTemplateNazcaOverrides.CreateJsonProvider(sourceStore));

        var loadManager = new GroupLibraryManager(_testLibraryPath);
        loadManager.LoadTemplates();
        var loaded = loadManager.Templates.Single(t => t.Name == "Ovr Template");
        loaded.TemplateGroup.ShouldNotBeNull();
        return (loaded, loadManager, overriddenId);
    }

    /// <summary>
    /// Creates a two-member group whose first member carries a raw-code override in a
    /// simulated source-design override store.
    /// </summary>
    private static (ComponentGroup group, string overriddenId,
        Dictionary<string, NazcaCodeOverride> sourceStore) CreateGroupWithRawCodeOverride()
    {
        var group = new ComponentGroup("RawCodeGroup") { PhysicalX = 0, PhysicalY = 0 };
        for (var i = 0; i < 2; i++)
            group.AddChild(CreateChild(i));

        var overriddenId = group.ChildComponents[0].Identifier;
        var sourceStore = new Dictionary<string, NazcaCodeOverride>
        {
            [overriddenId] = new NazcaCodeOverride
            {
                RawCode = $"with nd.Cell('custom') as component:\n    {RawCodeMarker}\n",
                Backend = OverrideBackend.Nazca,
                OverrideWidthMicrometers = 123.5,
                OverrideHeightMicrometers = 0.45
            }
        };

        return (group, overriddenId, sourceStore);
    }

    private static Component CreateChild(int index)
    {
        return new Component(
            new Dictionary<int, SMatrix>(),
            new List<Slider>(),
            "test_component",
            "",
            new Part[1, 1] { { new Part() } },
            -1,
            $"comp_{index}_{Guid.NewGuid():N}",
            DiscreteRotation.R0,
            new List<PhysicalPin>
            {
                new() { Name = "a0", OffsetXMicrometers = 0, OffsetYMicrometers = 0, AngleDegrees = 180 }
            })
        {
            PhysicalX = index * 100,
            PhysicalY = 0,
            WidthMicrometers = 50,
            HeightMicrometers = 30
        };
    }
}
