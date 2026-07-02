using System.Collections.Specialized;
using CAP.Avalonia.ViewModels.Export.PythonEnvironmentManager;
using CAP_Core.Export;
using CAP_Core.Export.PythonEnvironmentManager;
using Shouldly;

namespace UnitTests.Export.PythonEnvironmentManager;

/// <summary>
/// Tests for <see cref="PythonEnvironmentManagerViewModel"/> covering the input
/// validation gates and the SetActive/RefreshList interaction with a ListBox-style
/// two-way binding (which nulls the selection whenever the collection is cleared).
/// </summary>
public class PythonEnvironmentManagerViewModelTests : IDisposable
{
    private readonly string _tempRegistryFile = Path.Combine(
        Path.GetTempPath(), $"lunima-vm-registry-test-{Guid.NewGuid():N}.json");

    private PythonEnvironmentRegistry CreateRegistry() => new(_tempRegistryFile);

    private static PythonEnvironmentManagerViewModel CreateViewModel(PythonEnvironmentRegistry registry) =>
        new(registry,
            new UvBootstrapper(),
            new NazcaPackageInstaller(),
            new EnvironmentHealthChecker(new PythonDiscoveryService()));

    [Theory]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData(@"..\escape")]
    [InlineData(@"C:\Windows")]
    public async Task CreateAndInstall_PathLikeName_IsRejectedWithoutSideEffects(string name)
    {
        var registry = CreateRegistry();
        var vm = CreateViewModel(registry);
        vm.NewEnvironmentName = name;

        await vm.CreateAndInstallCommand.ExecuteAsync(null);

        registry.GetAll().ShouldBeEmpty();          // nothing was registered
        vm.IsBusy.ShouldBeFalse();                  // no long operation started
        vm.ProgressText.ShouldContain("name");      // the user is told why
    }

    [Fact]
    public async Task CreateAndInstall_InvalidPythonVersion_IsRejectedWithoutSideEffects()
    {
        var registry = CreateRegistry();
        var vm = CreateViewModel(registry);
        vm.NewEnvironmentName = "valid-name";
        vm.PythonVersion = "3.11 --seed";

        await vm.CreateAndInstallCommand.ExecuteAsync(null);

        registry.GetAll().ShouldBeEmpty();
        vm.ProgressText.ShouldContain("version");
    }

    [Fact]
    public void SetActive_BindingClearsSelectionDuringRefresh_ActivatesWithoutThrowing()
    {
        // Avalonia's ListBox pushes SelectedItem = null into the two-way binding as soon
        // as RefreshList() clears the collection. Simulate that here: any reset/removal
        // nulls the selection, exactly like the real panel does.
        var registry = CreateRegistry();
        registry.AddOrUpdate(MakeEnv("env-a"));
        var vm = CreateViewModel(registry);
        vm.Environments.CollectionChanged += (_, e) =>
        {
            if (e.Action is NotifyCollectionChangedAction.Reset or NotifyCollectionChangedAction.Remove)
                vm.SelectedEnvironment = null;
        };
        vm.SelectedEnvironment = vm.Environments.Single();

        Should.NotThrow(() => vm.SetActiveCommand.Execute(null));

        registry.GetActive()?.Name.ShouldBe("env-a");
        vm.ProgressText.ShouldContain("env-a");
        // The selection survives the refresh, so the action buttons stay visible
        // and the active badge is reflected on the selected item.
        vm.SelectedEnvironment.ShouldNotBeNull();
        vm.SelectedEnvironment!.IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task Remove_VenvPathOutsideEnvsBaseDir_RefusesToDeleteTheDirectory()
    {
        // A tampered registry entry (or a legacy entry created before name validation)
        // must never lead to a recursive delete outside the managed envs directory.
        var outsideDir = Path.Combine(Path.GetTempPath(), $"lunima-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDir);
        try
        {
            var registry = CreateRegistry();
            registry.AddOrUpdate(new PythonEnvironment { Name = "tampered", VenvPath = outsideDir });
            var vm = CreateViewModel(registry);
            vm.SelectedEnvironment = vm.Environments.Single();

            await vm.RemoveCommand.ExecuteAsync(null);

            Directory.Exists(outsideDir).ShouldBeTrue();     // nothing outside envs/ was deleted
            registry.Exists("tampered").ShouldBeFalse();     // the registry entry is still cleaned up
        }
        finally
        {
            if (Directory.Exists(outsideDir))
                Directory.Delete(outsideDir, recursive: true);
        }
    }

    private static PythonEnvironment MakeEnv(string name) => new()
    {
        Name = name,
        VenvPath = Path.Combine(UvBootstrapper.EnvironmentsBaseDir, name),
        Status = PythonEnvironmentStatus.Unknown,
    };

    public void Dispose()
    {
        if (File.Exists(_tempRegistryFile))
            File.Delete(_tempRegistryFile);
    }
}
