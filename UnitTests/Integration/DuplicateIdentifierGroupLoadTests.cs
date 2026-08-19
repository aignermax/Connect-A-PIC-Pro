using CAP.Avalonia.Services;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.Integration;

/// <summary>
/// Regression for the load path defect found while building the wire-delay honesty
/// tests (#1037): two groups sharing one identifier saved on one canvas came back from
/// <c>LoadDesignFromPathAsync</c> as two copies of the first — the topological sort in
/// the loader keyed its bookkeeping by the identifier string. Group identifiers are not
/// unique (library instances keep them until a copy/paste regenerates), so a file with
/// a duplicate merges silently.
/// </summary>
public class DuplicateIdentifierGroupLoadTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var path in _tempFiles.Where(File.Exists))
            File.Delete(path);
    }

    [Fact]
    public async Task SaveLoadReload_TwoGroupsSharingOneIdentifier_KeepTheirDistinctNames()
    {
        var first = SingleGroup(await LogicGateHalfAdderExampleTests.LoadCanvas(NotNandPath()));
        first.GroupName = "DUP1";
        var second = SingleGroup(await LogicGateHalfAdderExampleTests.LoadCanvas(NotNandPath()));
        second.GroupName = "DUP2";
        second.Identifier.ShouldBe(first.Identifier,
            "this fixture needs two genuinely identifier-sharing groups");

        var canvas = new DesignCanvasViewModel();
        canvas.AddComponent(first);
        canvas.AddComponent(second);
        var savedPath = await Save(canvas);
        var reloaded = await LogicGateHalfAdderExampleTests.LoadCanvas(savedPath);

        LogicGateHalfAdderExampleTests.GroupsOf(reloaded)
            .Select(g => g.GroupName).ShouldBe(new[] { "DUP1", "DUP2" }, ignoreOrder: true,
                "duplicate identifiers must not merge two groups into copies of the first on load");
    }

    private static string NotNandPath() =>
        Path.Combine(ExampleDesignFilesTests.ExamplesDirectory(), "Logic Gate NOT-NAND.lun");

    private static ComponentGroup SingleGroup(DesignCanvasViewModel canvas) =>
        (ComponentGroup)canvas.Components.Single(c => c.Component is ComponentGroup).Component;

    private async Task<string> Save(DesignCanvasViewModel canvas)
    {
        var path = Path.Combine(Path.GetTempPath(), $"duplicate-identifier-{Guid.NewGuid():N}.lun");
        _tempFiles.Add(path);
        var saveVm = LogicGateHalfAdderExampleTests.CreateFileOperations(canvas);
        var dialog = new Mock<IFileDialogService>();
        dialog.Setup(f => f.ShowSaveFileDialogAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(path);
        saveVm.FileDialogService = dialog.Object;
        await saveVm.SaveDesignAsCommand.ExecuteAsync(null);
        File.Exists(path).ShouldBeTrue("the real save path must write the temp .lun");
        return path;
    }
}
