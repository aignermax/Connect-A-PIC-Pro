using CAP.Avalonia.ViewModels.Export.PythonEnvironmentManager;
using CAP_Core.Export;
using CAP_Core.Export.PythonEnvironmentManager;
using Shouldly;

namespace UnitTests.Export.PythonEnvironmentManager;

/// <summary>
/// Tests for <see cref="PythonEnvironmentManagerViewModel"/> covering the input validation
/// gates and the unified interpreter list (issue #645): activating managed vs system rows,
/// removing a managed environment, and the create/install-guard behaviour.
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
    public void SetActiveInterpreter_ManagedRow_ActivatesThatEnvironment()
    {
        var registry = CreateRegistry();
        registry.AddOrUpdate(MakeEnv("env-a"));
        var vm = CreateViewModel(registry);

        var managed = vm.Interpreters.Single(i => i.ManagedName == "env-a");
        vm.SetActiveInterpreterCommand.Execute(managed);

        registry.GetActive()?.Name.ShouldBe("env-a");
        // The rebuilt row reflects the active marker.
        vm.Interpreters.Single(i => i.ManagedName == "env-a").IsActive.ShouldBeTrue();
        vm.ProgressText.ShouldContain(managed.Path);
    }

    [Fact]
    public async Task RemoveInterpreter_VenvPathOutsideEnvsBaseDir_RefusesToDeleteTheDirectory()
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
            var entry = vm.Interpreters.Single(i => i.ManagedName == "tampered");

            await vm.RemoveInterpreterCommand.ExecuteAsync(entry);

            Directory.Exists(outsideDir).ShouldBeTrue();     // nothing outside envs/ was deleted
            registry.Exists("tampered").ShouldBeFalse();     // the registry entry is still cleaned up
        }
        finally
        {
            if (Directory.Exists(outsideDir))
                Directory.Delete(outsideDir, recursive: true);
        }
    }

    [Fact]
    public async Task StartDefaultNazcaInstall_EnvAlreadyExists_DoesNotCreateDuplicateAndExplains()
    {
        var registry = CreateRegistry();
        registry.AddOrUpdate(new PythonEnvironment
        {
            Name = PythonEnvironmentManagerViewModel.DefaultEnvironmentName,
            VenvPath = Path.Combine(UvBootstrapper.EnvironmentsBaseDir,
                PythonEnvironmentManagerViewModel.DefaultEnvironmentName),
        });
        var vm = CreateViewModel(registry);

        await vm.StartDefaultNazcaInstallAsync();

        registry.GetAll().Count.ShouldBe(1);                 // kein Duplikat
        vm.IsBusy.ShouldBeFalse();                           // kein Install gestartet
        vm.ProgressText.ShouldContain(
            PythonEnvironmentManagerViewModel.DefaultEnvironmentName);
    }

    [Fact]
    public async Task StartDefaultNazcaInstall_WhileBusy_IsIgnored()
    {
        var registry = CreateRegistry();
        var vm = CreateViewModel(registry);
        vm.IsBusy = true;

        await vm.StartDefaultNazcaInstallAsync();

        registry.GetAll().ShouldBeEmpty();                   // nichts registriert
    }

    [Fact]
    public void GdsFactoryVersion_RoundTripsThroughRegistryPersistence()
    {
        var registry = CreateRegistry();
        var env = MakeEnv("gf-env");
        env.GdsFactoryVersion = "9.34.2";
        registry.AddOrUpdate(env);

        var reloaded = new PythonEnvironmentRegistry(_tempRegistryFile);

        reloaded.GetAll().Single().GdsFactoryVersion.ShouldBe("9.34.2");
    }

    [Fact]
    public void SetActiveInterpreter_SystemRow_ClearsManagedActive_AndPushesPathThroughRegistryCallback()
    {
        // Activating a discovered system interpreter in the tab (issue #645) must route the
        // path through the registry callback — the same channel export/preview listen on —
        // and clear any managed active selection so exactly one interpreter is active.
        var registry = CreateRegistry();
        registry.AddOrUpdate(MakeEnv("managed-a"));
        registry.SetActive("managed-a");

        string? pushedPath = null;
        registry.OnActiveEnvironmentChanged = p => pushedPath = p;

        var vm = CreateViewModel(registry);
        var install = new PythonDiscoveryService.PythonInstallation
        {
            Path = @"/usr/bin/python3.12", Source = "System",
            PythonVersion = "3.12.0", NazcaVersion = "0.6.1",
        };
        var systemRow = new InterpreterEntryViewModel(install, isActive: false);

        vm.SetActiveInterpreterCommand.Execute(systemRow);

        pushedPath.ShouldBe(@"/usr/bin/python3.12");
        registry.GetActive().ShouldBeNull();
        vm.ProgressText.ShouldContain(@"/usr/bin/python3.12");
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
