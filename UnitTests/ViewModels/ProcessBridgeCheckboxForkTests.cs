using CAP.Avalonia.Services;
using CAP.Avalonia.Services.Localization;
using CAP.Avalonia.ViewModels;
using CAP_DataAccess.Components.AddCustomComponent;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;
using Shouldly;
using Xunit;

namespace UnitTests.ViewModels;

/// <summary>
/// The metal/photonic crossing checkbox in the process editor and the implicit fork:
/// the checkbox maps to <see cref="ProcessDefinition.ElectricalBridgeRequired"/>
/// (checked = direct crossing = flag absent), and saving a bundled read-only PDK
/// forks it into the managed user root instead of touching the bundled file.
/// </summary>
public class ProcessBridgeCheckboxForkTests : IDisposable
{
    /// <summary>
    /// The fork status text reads the localized <c>LocalizationService.Instance</c>,
    /// so these tests pin English to stay culture-independent regardless of the CI/dev OS
    /// language. The only test that live-switches the shared instance is isolated in the
    /// "LocalizationSingleton" collection, so it never flips the language concurrently here.
    /// </summary>
    public ProcessBridgeCheckboxForkTests()
    {
        LocalizationService.Instance.SetLanguage(SupportedLanguage.English.Code);
    }
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "bridge-fork-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* temp cleanup best effort */ }
    }

    private ProcessManagementViewModel CreateVm(UserPdkStore store) =>
        new(Mock.Of<IFileDialogService>(), Array.Empty<IProcessImporter>(), userPdkStore: store);

    private UserPdkStore TempStore() =>
        new(Path.Combine(_tempDir, "user-pdks"), new PdkJsonSaver(), new PdkLoader());

    private static PdkDraft DraftWithProcess(bool? bridgeRequired) => new()
    {
        Name = "TestPdk",
        Process = new ProcessDefinition
        {
            Name = "TestPdk",
            ElectricalBridgeRequired = bridgeRequired,
            Layers = { new ProcessLayer { Name = "WG", Layer = 12 } },
            Xsections = { new ProcessXsection { Name = "metal", Kind = XsectionKind.Metal, WidthUm = 5 } },
        },
    };

    [Theory]
    [InlineData(null, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void LoadForSinglePdkEdit_CheckboxReflectsBridgeFlag(bool? bridgeRequired, bool expectedMayCross)
    {
        var vm = CreateVm(TempStore());

        vm.LoadForSinglePdkEdit(DraftWithProcess(bridgeRequired));

        vm.MetalMayCrossPhotonic.ShouldBe(expectedMayCross);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, true)]
    public async Task SaveProcess_CustomPdk_WritesCheckboxToFlag(bool mayCross, bool? expectedFlag)
    {
        var path = Path.Combine(_tempDir, "custom.json");
        Directory.CreateDirectory(_tempDir);
        var vm = CreateVm(TempStore());
        var draft = DraftWithProcess(null);
        vm.LoadForSinglePdkEdit(draft);
        vm.MetalMayCrossPhotonic = mayCross;
        vm.PdkFilePathResolver = _ => path;

        await vm.SaveProcessCommand.ExecuteAsync(null);

        draft.Process!.ElectricalBridgeRequired.ShouldBe(expectedFlag);
        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public async Task SaveProcess_BundledPdk_ForksImplicitly_BundledFileAndDraftUntouched()
    {
        var bundledPath = BundledFilePath("siepic-ebeam-pdk.json");
        BundledPdkPaths.IsBundledPdkFile(bundledPath).ShouldBeTrue("test premise: the path must classify as bundled");
        var store = TempStore();
        var vm = CreateVm(store);
        var bundledDraft = new PdkDraft
        {
            Name = "SiEPIC EBeam",
            FilePath = bundledPath,
            Components = { new PdkComponentDraft { Name = "SomeCell" } },
            Process = new ProcessDefinition
            {
                Name = "SiEPIC EBeam",
                ElectricalBridgeRequired = null,
                Layers = { new ProcessLayer { Name = "Si", Layer = 1 } },
            },
        };
        var originalProcess = bundledDraft.Process;
        vm.LoadForSinglePdkEdit(bundledDraft);
        vm.MetalMayCrossPhotonic = false;   // user toggles: bridges required
        vm.PdkFilePathResolver = _ => bundledPath;
        BundledPdkForkSavedEventArgs? forkEvent = null;
        vm.BundledPdkForkSaved += (_, args) => forkEvent = args;

        await vm.SaveProcessCommand.ExecuteAsync(null);

        // The bundled in-memory draft and its process instance are never mutated.
        bundledDraft.Process.ShouldBeSameAs(originalProcess);
        originalProcess.ElectricalBridgeRequired.ShouldBeNull();
        // The fork was written to the managed root, carrying the toggle and the components.
        forkEvent.ShouldNotBeNull();
        forkEvent!.PdkName.ShouldBe("SiEPIC EBeam");
        forkEvent.ForkPath.StartsWith(Path.Combine(_tempDir, "user-pdks"), StringComparison.OrdinalIgnoreCase)
            .ShouldBeTrue();
        var forkJson = await File.ReadAllTextAsync(forkEvent.ForkPath);
        forkJson.ShouldContain("electricalBridgeRequired");
        forkJson.ShouldContain("SomeCell");
        // And the editor is re-scoped to the fork.
        vm.StatusText.ShouldContain("custom copy");
    }

    [Fact]
    public async Task SaveProcess_AfterImplicitFork_SecondSaveWritesForkDirectly()
    {
        var bundledPath = BundledFilePath("siepic-ebeam-pdk.json");
        var store = TempStore();
        var vm = CreateVm(store);
        var bundledDraft = new PdkDraft
        {
            Name = "SiEPIC EBeam",
            FilePath = bundledPath,
            Process = new ProcessDefinition { Name = "SiEPIC EBeam" },
        };
        vm.LoadForSinglePdkEdit(bundledDraft);
        vm.MetalMayCrossPhotonic = false;
        vm.PdkFilePathResolver = _ => bundledPath;
        var forkCount = 0;
        string? forkPath = null;
        vm.BundledPdkForkSaved += (_, args) => { forkCount++; forkPath = args.ForkPath; };

        await vm.SaveProcessCommand.ExecuteAsync(null);   // first → implicit fork
        (await File.ReadAllTextAsync(forkPath!)).ShouldContain("electricalBridgeRequired");

        vm.MetalMayCrossPhotonic = true;                  // user toggles back
        await vm.SaveProcessCommand.ExecuteAsync(null);   // second → direct write to the fork

        forkCount.ShouldBe(1, "the fork happens once; later saves write the fork directly");
        // checked = direct crossing = the flag is omitted again
        (await File.ReadAllTextAsync(forkPath!)).ShouldNotContain("electricalBridgeRequired");
    }

    /// <summary>Locates a bundled PDK JSON inside the repo checkout (CAP-DataAccess/PDKs).</summary>
    private static string BundledFilePath(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CAP.Avalonia", "App.axaml.cs")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "CAP-DataAccess", "PDKs", fileName);
    }
}
