using System;
using System.IO;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Shouldly;
using Xunit;

namespace UnitTests.Architecture;

/// <summary>
/// Enforces the "user PDK, never the bundled foundry PDK" invariant from issue #570/#655:
/// <see cref="UserPdkStore"/> must always resolve into the caller-supplied writable root,
/// never into the bundled <c>CAP-DataAccess/PDKs</c> folder that ships read-only foundry data.
/// </summary>
public class AddCustomComponentSliceTests
{
    [Fact]
    public void UserPdk_path_is_never_inside_the_bundled_pdk_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "lunima-arch-" + Guid.NewGuid().ToString("N"));
        var store = new UserPdkStore(root, new PdkJsonSaver(), new PdkLoader());
        var path = store.ResolvePath(new ProcessDefinition { Name = "CornerStone SiN" });

        path.Replace('\\', '/').ShouldNotContain("/PDKs/");
        path.ShouldStartWith(root);
    }
}
