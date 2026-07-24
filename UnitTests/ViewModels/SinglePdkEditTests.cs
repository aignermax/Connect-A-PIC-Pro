using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.ViewModels;

public class SinglePdkEditTests
{
    [Fact]
    public void LoadForSinglePdkEdit_WithProcess_PopulatesEditorFromDraft()
    {
        var vm = new ProcessManagementViewModel(Mock.Of<IFileDialogService>());
        var draft = new PdkDraft
        {
            Name = "MyCustomPdk",
            Process = new ProcessDefinition
            {
                Name = "MyCustomPdk",
                Layers = { new ProcessLayer { Name = "WG", Layer = 12 } },
                Xsections = { new ProcessXsection { Name = "xs1", WidthUm = 0.5 } },
                Materials = { new ProcessMaterial { Name = "Silicon" } },
            },
        };

        vm.LoadForSinglePdkEdit(draft);

        vm.HasProcess.ShouldBeTrue();
        vm.ProcessName.ShouldBe("MyCustomPdk");
        vm.Layers.ShouldContain(l => l.Name == "WG");
        vm.Xsections.ShouldContain(x => x.Name == "xs1");
        vm.Materials.ShouldContain(m => m.Name == "Silicon");
    }

    [Fact]
    public void LoadForSinglePdkEdit_WithoutProcess_ShowsEmptyEditorSeededWithDraftName()
    {
        var vm = new ProcessManagementViewModel(Mock.Of<IFileDialogService>());
        var draft = new PdkDraft { Name = "BlankPdk", Process = null };

        Should.NotThrow(() => vm.LoadForSinglePdkEdit(draft));

        vm.ProcessName.ShouldBe("BlankPdk");
        vm.Layers.ShouldBeEmpty();
        vm.Xsections.ShouldBeEmpty();
        vm.Materials.ShouldBeEmpty();
    }

    [Fact]
    public async Task LoadForSinglePdkEdit_ThenSaveProcess_WritesToResolverPathAndFiresProcessSaved()
    {
        var dir = Path.Combine(Path.GetTempPath(), "single-pdk-edit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "custom.json");

        try
        {
            var draft = new PdkDraft
            {
                Name = "MyCustomPdk",
                Process = new ProcessDefinition { Name = "MyCustomPdk", CoreThicknessNm = 220 },
            };
            new PdkJsonSaver().SaveToFile(draft, path);

            var vm = new ProcessManagementViewModel(Mock.Of<IFileDialogService>())
            {
                PdkFilePathResolver = name => name == "MyCustomPdk" ? path : null,
                ConfirmSaveToPdk = _ => Task.FromResult(true),
            };
            vm.LoadForSinglePdkEdit(draft);

            var savedRaised = false;
            vm.ProcessSaved += (_, _) => savedRaised = true;

            vm.AddMetalXsectionCommand.Execute(null);
            await vm.SaveProcessCommand.ExecuteAsync(null);

            savedRaised.ShouldBeTrue();
            var reloaded = new PdkLoader().LoadFromFileForEditing(path);
            reloaded.Process.ShouldNotBeNull();
            reloaded.Process!.CoreThicknessNm.ShouldBe(220);
            reloaded.Process.Xsections.ShouldContain(x => x.Kind == XsectionKind.Metal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveProcess_WhenConfirmDeclines_DoesNotFireProcessSaved()
    {
        var dir = Path.Combine(Path.GetTempPath(), "single-pdk-edit-decline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "custom.json");

        try
        {
            var draft = new PdkDraft
            {
                Name = "MyCustomPdk",
                Process = new ProcessDefinition { Name = "MyCustomPdk" },
            };
            new PdkJsonSaver().SaveToFile(draft, path);

            var vm = new ProcessManagementViewModel(Mock.Of<IFileDialogService>())
            {
                PdkFilePathResolver = name => name == "MyCustomPdk" ? path : null,
                ConfirmSaveToPdk = _ => Task.FromResult(false),
            };
            vm.LoadForSinglePdkEdit(draft);

            var savedRaised = false;
            vm.ProcessSaved += (_, _) => savedRaised = true;

            await vm.SaveProcessCommand.ExecuteAsync(null);

            savedRaised.ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveProcess_AfterAddingAndRenamingRows_PersistsBothUnderTheirNewNames()
    {
        var dir = Path.Combine(Path.GetTempPath(), "single-pdk-edit-rename-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "custom.json");

        try
        {
            var draft = new PdkDraft { Name = "BlankPdk", Process = null };

            var vm = new ProcessManagementViewModel(Mock.Of<IFileDialogService>())
            {
                PdkFilePathResolver = name => name == "BlankPdk" ? path : null,
                ConfirmSaveToPdk = _ => Task.FromResult(true),
            };
            vm.LoadForSinglePdkEdit(draft);

            vm.AddLayerCommand.Execute(null);
            vm.Layers.Single().Name = "WG";

            vm.AddXsectionCommand.Execute(null);
            vm.Xsections.Single().Name = "strip";

            await vm.SaveProcessCommand.ExecuteAsync(null);

            var reloaded = new PdkLoader().LoadFromFileForEditing(path);
            reloaded.Process.ShouldNotBeNull();
            reloaded.Process!.Layers.ShouldContain(l => l.Name == "WG");
            reloaded.Process.Xsections.ShouldContain(x => x.Name == "strip");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LoadForSinglePdkEdit_EditingWithoutSave_DoesNotMutateTheLiveDraft()
    {
        var draft = new PdkDraft
        {
            Name = "MyCustomPdk",
            Process = new ProcessDefinition
            {
                Name = "MyCustomPdk",
                Xsections = new List<ProcessXsection> { new() { Name = "strip", WidthUm = 0.5 } },
            },
        };
        var vm = new ProcessManagementViewModel(Mock.Of<IFileDialogService>());

        vm.LoadForSinglePdkEdit(draft);
        vm.Xsections.Single(x => x.Name == "strip").WidthUm = 0.9;

        draft.Process.Xsections.Single(x => x.Name == "strip").WidthUm.ShouldBe(0.5,
            "editing the editor's copy must not mutate the live draft before Save");
    }

    [Fact]
    public async Task SaveProcess_AfterRenamingTheProcess_PersistsTheNewName()
    {
        var dir = Path.Combine(Path.GetTempPath(), "single-pdk-edit-procrename-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "custom.json");

        try
        {
            var draft = new PdkDraft
            {
                Name = "MyCustomPdk",
                Process = new ProcessDefinition { Name = "OldProcessName" },
            };
            new PdkJsonSaver().SaveToFile(draft, path);

            var vm = new ProcessManagementViewModel(Mock.Of<IFileDialogService>())
            {
                PdkFilePathResolver = name => name == "MyCustomPdk" ? path : null,
                ConfirmSaveToPdk = _ => Task.FromResult(true),
            };
            vm.LoadForSinglePdkEdit(draft);
            vm.ProcessName = "NewProcessName";

            await vm.SaveProcessCommand.ExecuteAsync(null);

            var reloaded = new PdkLoader().LoadFromFileForEditing(path);
            reloaded.Process!.Name.ShouldBe("NewProcessName");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
