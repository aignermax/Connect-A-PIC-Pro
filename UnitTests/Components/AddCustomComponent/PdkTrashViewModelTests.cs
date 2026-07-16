using CAP.Avalonia.ViewModels.Panels.PdkTrash;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;

namespace UnitTests.Components.AddCustomComponent;

public sealed class PdkTrashViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lunima-trashvm-" + Guid.NewGuid().ToString("N"));
    private readonly UserPdkStore _store;
    private readonly PdkTrashViewModel _vm;

    public PdkTrashViewModelTests()
    {
        _store = new UserPdkStore(_root, new PdkJsonSaver(), new PdkLoader());
        _vm = new PdkTrashViewModel(_store.CreateTrashService());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            try { Directory.Delete(_root, true); } catch { }
    }

    private static PdkComponentDraft Component(string name) => new()
    {
        Name = name, WidthMicrometers = 5, HeightMicrometers = 1,
        RawCode = "import gdsfactory as gf\ncomponent = gf.components.straight()", RawCodeBackend = "gdsfactory",
        Pins = new() { new PhysicalPinDraft { Name = "o1" }, new PhysicalPinDraft { Name = "o2" } },
    };

    [Fact]
    public void OpenCommand_ListsRecoverableEntries()
    {
        var path = _store.SaveToNamedPdk("My Lib", new ProcessDefinition { Name = "P" }, Component("A"), "gdsfactory", null);
        _store.MoveToTrash(path);

        _vm.OpenCommand.Execute(null);

        _vm.IsOpen.ShouldBeTrue();
        _vm.HasEntries.ShouldBeTrue();
        _vm.Entries.Count.ShouldBe(1);
        _vm.Entries[0].Title.ShouldBe("My Lib");
        _vm.Entries[0].IsDeletedPdk.ShouldBeTrue();
    }

    [Fact]
    public void RestoreRemovedComponent_ReRegistersComponentAndClearsEntry()
    {
        var proc = new ProcessDefinition { Name = "P" };
        _store.SaveToNamedPdk("My Lib", proc, Component("Keep"), "gdsfactory", null);
        var path = _store.SaveToNamedPdk("My Lib", proc, Component("Gone"), "gdsfactory", null);
        _store.RemoveComponent(path, "Gone");
        _vm.Refresh();

        PdkTrashRestoreResult? restored = null;
        _vm.OnRestored = r => restored = r;

        _vm.Entries[0].RestoreCommand.Execute(null);

        restored.ShouldNotBeNull();
        restored!.Kind.ShouldBe(PdkTrashKind.RemovedComponents);
        restored.RestoredComponents.Select(c => c.Name).ShouldBe(new[] { "Gone" });
        new PdkLoader().LoadFromFileForEditing(path).Components.Select(c => c.Name)
            .ShouldBe(new[] { "Keep", "Gone" }, ignoreOrder: true);
        _vm.Entries.ShouldBeEmpty();
    }

    [Fact]
    public void Restore_InvokesReRegisterCallback_AndClearsTheEntry()
    {
        var path = _store.SaveToNamedPdk("My Lib", new ProcessDefinition { Name = "P" }, Component("A"), "gdsfactory", null);
        _store.MoveToTrash(path);
        _vm.Refresh();

        PdkTrashRestoreResult? restored = null;
        _vm.OnRestored = r => restored = r;

        _vm.Entries[0].RestoreCommand.Execute(null);

        restored.ShouldNotBeNull();
        restored!.Kind.ShouldBe(PdkTrashKind.DeletedPdk);
        restored.PdkName.ShouldBe("My Lib");
        File.Exists(path).ShouldBeTrue();
        _vm.Entries.ShouldBeEmpty();
    }

}
