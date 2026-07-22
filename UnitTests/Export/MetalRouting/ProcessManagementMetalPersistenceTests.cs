using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels;
using CAP_Core.Components.Process;
using CAP_Core.Export;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;
using Shouldly;

namespace UnitTests.Export.MetalRouting;

/// <summary>
/// The Fabrication Process dialog can define a metal cross-section and persist it back to the
/// PDK JSON so the export uses it (issue #682) — without dropping the process's fingerprint
/// fields (core thickness etc.).
/// </summary>
public class ProcessManagementMetalPersistenceTests
{
    /// <summary>StatusText is localized (issue #749); pin English so the substring
    /// assertion stays culture-independent regardless of the CI/dev OS language.</summary>
    public ProcessManagementMetalPersistenceTests()
    {
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
    }

    private static ProcessManagementViewModel VmSavingTo(string path, string pdkName)
    {
        var vm = new ProcessManagementViewModel(Mock.Of<IFileDialogService>())
        {
            PdkFilePathResolver = name => name == pdkName ? path : null,
        };
        return vm;
    }

    [Fact]
    public void AddMetalXsection_ThenSave_PersistsMetalAndPreservesThickness()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lunima_metal_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "fab.json");

        try
        {
            // A single-member process with a fingerprint-bearing thickness that must survive the save.
            var draft = new PdkDraft
            {
                Name = "MyFab",
                Process = new ProcessDefinition { Name = "MyFab", CoreThicknessNm = 220 },
            };
            new PdkJsonSaver().SaveToFile(draft, path);

            var active = new ActiveProcessSelection("MyFab", null, new[] { "MyFab" }, IsPlayground: false);
            var vm = VmSavingTo(path, "MyFab");
            vm.ShowActiveProcess(active, new[] { draft });

            vm.AddMetalXsectionCommand.Execute(null);
            vm.SaveProcessCommand.Execute(null);

            // Reload from disk: metal xsection persisted, thickness preserved.
            var reloaded = new PdkLoader().LoadFromFileForEditing(path);
            reloaded.Process.ShouldNotBeNull();
            reloaded.Process!.CoreThicknessNm.ShouldBe(220);
            reloaded.Process.Xsections.ShouldContain(x => x.Kind == XsectionKind.Metal);
            var metalLayer = reloaded.Process.Layers.FirstOrDefault(l => l.Name.Contains("METAL"));
            metalLayer.ShouldNotBeNull();
            metalLayer!.Layer.ShouldBe(MetalTraceStyle.DefaultGdsLayer);   // named constant, not a magic 11 (Finding 6)
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AddMetalXsection_LayerWithNullName_DoesNotThrow()
    {
        // A legacy/imported row can have a null Name despite the DTO's non-nullable declaration
        // (e.g. deserialized from JSON that omitted "name") — must not NRE (issue #686, Finding 3).
        var vm = new ProcessManagementViewModel(Mock.Of<IFileDialogService>());
        vm.Layers.Add(new ProcessLayer { Name = null! });

        Should.NotThrow(() => vm.AddMetalXsectionCommand.Execute(null));

        vm.Layers.ShouldContain(l => l.Name != null && l.Name.Contains("METAL"));
    }

    [Fact]
    public void SaveProcess_MultiMemberProcess_DoesNotWriteAndExplains()
    {
        var active = new ActiveProcessSelection(
            "Merged", null, new[] { "PdkA", "PdkB" }, IsPlayground: false);
        var drafts = new[]
        {
            new PdkDraft { Name = "PdkA", Process = new ProcessDefinition { Name = "A" } },
            new PdkDraft { Name = "PdkB", Process = new ProcessDefinition { Name = "B" } },
        };
        var vm = new ProcessManagementViewModel(Mock.Of<IFileDialogService>());
        vm.ShowActiveProcess(active, drafts);

        vm.SaveProcessCommand.Execute(null);

        vm.StatusText.ShouldContain("several PDKs");
    }

    [Fact]
    public void SaveProcess_AfterImportingAnUnrelatedReferencePdk_DoesNotWriteForeignRows()
    {
        // Reproduces issue #686 Finding 2: opening "Import from PDK" while a single-member
        // process is loaded pulls a foreign PDK's rows into the SAME editable collections via
        // Merge(); Save must persist only the rows that belong to the loaded member PDK.
        var dir = Path.Combine(Path.GetTempPath(), "lunima_metal_scope_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "fab.json");

        try
        {
            var draft = new PdkDraft
            {
                Name = "MyFab",
                Process = new ProcessDefinition
                {
                    Name = "MyFab",
                    CoreThicknessNm = 220,
                    Layers = { new ProcessLayer { Name = "OWN_LAYER", Layer = 5 } },
                    Xsections = { new ProcessXsection { Name = "own_xs", WidthUm = 0.5 } },
                },
            };
            new PdkJsonSaver().SaveToFile(draft, path);

            var active = new ActiveProcessSelection("MyFab", null, new[] { "MyFab" }, IsPlayground: false);
            var vm = VmSavingTo(path, "MyFab");
            vm.ShowActiveProcess(active, new[] { draft });

            // Simulate "Import from PDK" pulling in an unrelated reference PDK for comparison.
            vm.Merge(new ProcessDefinition
            {
                Name = "SomeOtherFab",
                Layers = { new ProcessLayer { Name = "FOREIGN_LAYER", Layer = 99 } },
                Xsections = { new ProcessXsection { Name = "foreign_xs", WidthUm = 9 } },
            });

            vm.SaveProcessCommand.Execute(null);

            var reloaded = new PdkLoader().LoadFromFileForEditing(path);
            reloaded.Process.ShouldNotBeNull();
            reloaded.Process!.Layers.ShouldContain(l => l.Name == "OWN_LAYER");
            reloaded.Process.Layers.ShouldNotContain(l => l.Name == "FOREIGN_LAYER");
            reloaded.Process.Xsections.ShouldContain(x => x.Name == "own_xs");
            reloaded.Process.Xsections.ShouldNotContain(x => x.Name == "foreign_xs");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
