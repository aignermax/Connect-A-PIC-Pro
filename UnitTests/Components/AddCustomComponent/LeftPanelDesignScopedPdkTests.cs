using System;
using System.Collections.Generic;
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
/// Covers <see cref="LeftPanelViewModel.RegisterDesignScopedPdk"/> /
/// <see cref="LeftPanelViewModel.RemoveDesignScopedPdk"/> (issue #830):
/// GDS-imported sets register as in-memory PDKs (null file path) visible while
/// their design is open, and tear down cleanly — without ever touching a
/// same-named file-backed PDK — when it closes.
/// </summary>
public class LeftPanelDesignScopedPdkTests : IDisposable
{
    private const string PdkName = "GDS Import - chip";

    private readonly string _prefsPath =
        Path.Combine(Path.GetTempPath(), $"lp-designscope-prefs-{Guid.NewGuid():N}.json");

    public void Dispose() { if (File.Exists(_prefsPath)) File.Delete(_prefsPath); }

    private LeftPanelViewModel CreateLeftPanel()
    {
        var canvas = new DesignCanvasViewModel();
        var libraryManager = new GroupLibraryManager();
        return new LeftPanelViewModel(
            canvas, libraryManager, new PdkLoader(), new UserPreferencesService(_prefsPath),
            new HierarchyPanelViewModel(canvas),
            new PdkManagerViewModel(),
            new ComponentLibraryViewModel(libraryManager));
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
    public void Register_AddsTemplatesAndNullFilePathManagerEntry()
    {
        var vm = CreateLeftPanel();

        vm.RegisterDesignScopedPdk(PdkName, new[] { Draft("wg1"), Draft("wg2") });

        vm.AllTemplates.Select(t => t.Name).ShouldBe(new[] { "wg1", "wg2" }, ignoreOrder: true);
        vm.AllTemplates.ShouldAllBe(t => t.PdkSource == PdkName && t.IsCustom);
        var entry = vm.PdkManager.LoadedPdks.ShouldHaveSingleItem();
        entry.Name.ShouldBe(PdkName);
        entry.FilePath.ShouldBeNull("design-scoped PDKs have no file on disk");
        entry.IsBundled.ShouldBeFalse();
        vm.FilteredTemplates.Count.ShouldBe(2, "the set is visible in the library panel");
    }

    [Fact]
    public void Register_NameAlreadyLoaded_IsSkippedWithoutDuplicates()
    {
        var vm = CreateLeftPanel();
        vm.RegisterDesignScopedPdk(PdkName, new[] { Draft("wg1") });

        vm.RegisterDesignScopedPdk(PdkName, new[] { Draft("wg1") });

        vm.AllTemplates.ShouldHaveSingleItem();
        vm.PdkManager.LoadedPdks.ShouldHaveSingleItem();
    }

    [Fact]
    public void Remove_TearsDownTemplatesOrphanedCategoryAndManagerEntry()
    {
        var vm = CreateLeftPanel();
        vm.RegisterDesignScopedPdk(PdkName, new[] { Draft("wg1"), Draft("wg2") });

        vm.RemoveDesignScopedPdk(PdkName);

        vm.AllTemplates.ShouldBeEmpty();
        vm.Categories.ShouldNotContain("Custom");
        vm.PdkManager.LoadedPdks.ShouldBeEmpty();
        vm.FilteredTemplates.ShouldBeEmpty();
    }

    [Fact]
    public void Remove_NeverTouchesASameNamedFileBackedPdk()
    {
        var vm = CreateLeftPanel();
        // A file-backed (non-design-scoped) PDK under the same name.
        vm.PdkManager.RegisterPdk(PdkName, Path.Combine(Path.GetTempPath(), "chip.json"), false, 1);

        vm.RemoveDesignScopedPdk(PdkName);

        vm.PdkManager.LoadedPdks.ShouldHaveSingleItem().FilePath.ShouldNotBeNull(
            "a design closing must never tear down a file-backed PDK");
    }

    [Fact]
    public void DesignScopedTemplates_AreNotEditableAndNotDeletable()
    {
        var vm = CreateLeftPanel();
        vm.RegisterDesignScopedPdk(PdkName, new[] { Draft("wg1") });
        var template = vm.AllTemplates.Single();

        vm.CanEditTemplate(template).ShouldBeFalse(
            "no file to save edits to — design-scoped components are read-only");
        vm.CanDeleteTemplate(template).ShouldBeFalse();
    }

    [Fact]
    public void Register_KeepsCategoriesSharedWithOtherPdksAliveOnRemove()
    {
        var vm = CreateLeftPanel();
        vm.RegisterDesignScopedPdk(PdkName, new[] { Draft("wg1") });
        vm.RegisterDesignScopedPdk("GDS Import - other", new[] { Draft("wg9") });

        vm.RemoveDesignScopedPdk(PdkName);

        vm.Categories.ShouldContain("Custom",
            "the category is still used by the other design-scoped set");
        vm.AllTemplates.ShouldHaveSingleItem().Name.ShouldBe("wg9");
    }
}
