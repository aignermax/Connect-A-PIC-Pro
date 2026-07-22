using System;
using System.IO;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

public class UserPdkCreateEmptyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lunima-createempty-" + Guid.NewGuid().ToString("N"));
    private UserPdkStore Store() => new(_root, new PdkJsonSaver(), new PdkLoader());

    [Fact]
    public void CreateNamedPdkWithProcess_writes_named_pdk_with_process_and_no_components()
    {
        var s = Store();
        var path = s.CreateNamedPdkWithProcess("My SiN Lib", new ProcessDefinition { Name = "CornerStone SiN 300" }, "gdsfactory", null);
        Path.GetFileName(path).ShouldBe("my-sin-lib.json");
        var pdk = new PdkLoader().LoadFromFileForEditing(path);
        pdk.Name.ShouldBe("My SiN Lib");
        pdk.Process!.Name.ShouldBe("CornerStone SiN 300");
        pdk.Components.ShouldBeEmpty();
        s.ListCustomPdks().ShouldContain(i => i.Name == "My SiN Lib" && i.Process.Name == "CornerStone SiN 300");
    }

    [Fact]
    public void CreateNamedPdkWithProcess_throws_when_name_already_exists()
    {
        var s = Store();
        s.CreateNamedPdkWithProcess("Lib", new ProcessDefinition { Name = "P" }, "gdsfactory", null);
        Should.Throw<InvalidOperationException>(() =>
            s.CreateNamedPdkWithProcess("Lib", new ProcessDefinition { Name = "P" }, "gdsfactory", null));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
