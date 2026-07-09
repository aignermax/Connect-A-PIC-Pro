using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels;
using CAP_Core.Components.Process;
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
            reloaded.Process.Layers.ShouldContain(l => l.Name.Contains("METAL"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
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
}
