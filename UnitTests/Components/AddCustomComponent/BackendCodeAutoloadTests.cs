using System;
using System.Collections.Generic;
using System.IO;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP.Avalonia.ViewModels.Components.AddCustomComponent;
using CAP_Core.Solvers.Fdtd;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

public class BackendCodeAutoloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lunima-nc-autoload-" + Guid.NewGuid().ToString("N"));

    private NewComponentViewModel Build()
    {
        var nazca = new Mock<IComponentPreviewRenderer>();
        var gds = new Mock<IComponentPreviewRenderer>();
        var extractor = new ComponentGeometryExtractor(nazca.Object, gds.Object);
        var fdtd = new Mock<IFdtdSMatrixService>();
        var store = new UserPdkStore(_root, new PdkJsonSaver(), new PdkLoader());
        return new NewComponentViewModel(extractor, fdtd.Object, store,
            new List<ProcessDefinition> { new() { Name = "P" } });
    }

    [Fact]
    public void Ctor_loads_the_default_backends_example_into_an_empty_editor()
    {
        var vm = Build();
        vm.Code.ShouldBe(BackendCodeExamples.For(vm.SelectedBackend));
    }

    [Fact]
    public void Test_A_empty_code_then_switch_to_gdsfactory_loads_its_example()
    {
        var vm = Build();
        vm.SelectedBackend = GeometryBackend.Nazca;
        vm.Code = string.Empty;

        vm.SelectedBackend = GeometryBackend.GdsFactory;

        vm.Code.ShouldBe(BackendCodeExamples.GdsFactory);
    }

    [Fact]
    public void Test_B_untouched_gdsfactory_example_then_switch_to_nazca_replaces_it()
    {
        var vm = Build();
        vm.SelectedBackend = GeometryBackend.GdsFactory;
        vm.Code = BackendCodeExamples.GdsFactory;

        vm.SelectedBackend = GeometryBackend.Nazca;

        vm.Code.ShouldBe(BackendCodeExamples.Nazca);
    }

    [Fact]
    public void Test_C_user_authored_code_survives_a_backend_switch()
    {
        var vm = Build();
        vm.SelectedBackend = GeometryBackend.GdsFactory;
        vm.Code = "mein eigener code";

        vm.SelectedBackend = GeometryBackend.Nazca;

        vm.Code.ShouldBe("mein eigener code");
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
