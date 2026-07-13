using System;
using System.IO;
using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

/// <summary>
/// End-to-end test for the PDK-first "New PDK…" modal flow (issue #726 follow-up, task 6): a
/// named custom PDK is created empty via <see cref="UserPdkStore"/>, a component is appended to
/// it afterwards, and <see cref="ProcessManagementViewModel"/>'s PDK-creation mode drives the
/// same store method end to end from the wizard's perspective. Mirrors
/// <c>UserPdkCreateEmptyTests</c>, <c>ProcessManagementPdkCreationTests</c> and
/// <c>UserPdkNamedStoreTests</c>.
/// </summary>
public class NewPdkModalFlowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lunima-newpdkmodal-" + Guid.NewGuid().ToString("N"));
    private UserPdkStore Store() => new(_root, new PdkJsonSaver(), new PdkLoader());
    private static ProcessDefinition Proc(string n) => new() { Name = n };
    private static PdkComponentDraft Comp(string n) => new()
    {
        Name = n, WidthMicrometers = 10, HeightMicrometers = 2,
        RawCode = "import gdsfactory as gf\ncomponent = gf.components.straight()", RawCodeBackend = "gdsfactory",
        Pins = new() { new PhysicalPinDraft { Name = "o1" }, new PhysicalPinDraft { Name = "o2" } },
    };

    [Fact]
    public void CreateNamedPdkWithProcess_produces_listed_pdk_with_no_components()
    {
        var store = Store();

        var path = store.CreateNamedPdkWithProcess("Lib", Proc("P"), "gdsfactory", null);

        store.ListCustomPdks().ShouldContain(i => i.Name == "Lib");
        new PdkLoader().LoadFromFileForEditing(path).Components.ShouldBeEmpty();
    }

    [Fact]
    public void AppendToExistingPdk_after_empty_creation_adds_one_component()
    {
        var store = Store();
        var path = store.CreateNamedPdkWithProcess("Lib", Proc("P"), "gdsfactory", null);

        store.AppendToExistingPdk(path, Comp("mmi"));

        new PdkLoader().LoadFromFileForEditing(path).Components.Count.ShouldBe(1);
    }

    [Fact]
    public void ProcessManagementViewModel_creation_mode_creates_pdk_file_and_raises_event()
    {
        var store = Store();
        var vm = new ProcessManagementViewModel(Mock.Of<IFileDialogService>())
        {
            CreateUserPdk = (name, process) => store.CreateNamedPdkWithProcess(name, process, "gdsfactory", null),
        };
        vm.EnterPdkCreationMode();
        vm.PdkName = "Lib2";
        string? raisedPath = null;
        vm.PdkCreated += (_, path) => raisedPath = path;

        vm.CreatePdkCommand.Execute(null);

        store.ListCustomPdks().ShouldContain(i => i.Name == "Lib2");
        raisedPath.ShouldNotBeNullOrEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }
}
