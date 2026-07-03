using CAP_Core.Export.PythonEnvironmentManager;
using Shouldly;

namespace UnitTests.Export.PythonEnvironmentManager;

/// <summary>
/// Unit tests for <see cref="PythonEnvironmentRegistry"/>. Each test uses its own
/// temp registry file so tests are isolated from each other and never touch the
/// user's real app-data registry.
/// </summary>
public class PythonEnvironmentRegistryTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(
        Path.GetTempPath(), $"lunima-registry-test-{Guid.NewGuid():N}.json");

    private PythonEnvironmentRegistry CreateRegistry() => new(_tempFile);

    [Fact]
    public void GetAll_WhenEmpty_ReturnsEmptyList()
    {
        CreateRegistry().GetAll().ShouldBeEmpty();
    }

    [Fact]
    public void AddOrUpdate_NewEnv_AppearsInGetAll()
    {
        var registry = CreateRegistry();

        registry.AddOrUpdate(MakeEnv("test-add"));

        registry.GetAll().ShouldContain(e => e.Name == "test-add");
    }

    [Fact]
    public void AddOrUpdate_ExistingEnv_ReplacesIt()
    {
        var registry = CreateRegistry();
        registry.AddOrUpdate(MakeEnv("test-replace"));

        var updated = MakeEnv("test-replace");
        updated.PythonVersion = "3.12.0";
        registry.AddOrUpdate(updated);

        var found = registry.GetAll().ShouldHaveSingleItem();
        found.PythonVersion.ShouldBe("3.12.0");
    }

    [Fact]
    public void Remove_ExistingEnv_DisappearsFromList()
    {
        var registry = CreateRegistry();
        registry.AddOrUpdate(MakeEnv("test-remove"));

        registry.Remove("test-remove");

        registry.GetAll().ShouldBeEmpty();
    }

    [Fact]
    public void Remove_ActiveEnv_ClearsActiveSelection()
    {
        var registry = CreateRegistry();
        registry.AddOrUpdate(MakeEnv("test-active-remove"));
        registry.SetActive("test-active-remove");

        registry.Remove("test-active-remove");

        registry.GetActive().ShouldBeNull();
    }

    [Fact]
    public void SetActive_ExistingEnv_ReturnsItAsActive()
    {
        var registry = CreateRegistry();
        registry.AddOrUpdate(MakeEnv("test-active"));

        registry.SetActive("test-active");

        registry.GetActive()?.Name.ShouldBe("test-active");
    }

    [Fact]
    public void SetActive_FiresCallbackWithPythonExecutable()
    {
        var registry = CreateRegistry();
        var env = MakeEnv("test-callback");
        registry.AddOrUpdate(env);

        string? notifiedPath = null;
        registry.OnActiveEnvironmentChanged = p => notifiedPath = p;

        registry.SetActive("test-callback");

        notifiedPath.ShouldBe(env.PythonExecutable);
    }

    [Fact]
    public void Exists_AfterAdd_ReturnsTrue()
    {
        var registry = CreateRegistry();
        registry.AddOrUpdate(MakeEnv("test-exists"));

        registry.Exists("test-exists").ShouldBeTrue();
    }

    [Fact]
    public void Exists_ForMissingEnv_ReturnsFalse()
    {
        CreateRegistry().Exists("definitely-not-there").ShouldBeFalse();
    }

    [Fact]
    public void Persistence_SecondInstanceOnSamePath_SeesSavedState()
    {
        var first = CreateRegistry();
        first.AddOrUpdate(MakeEnv("persisted"));
        first.SetActive("persisted");

        var second = CreateRegistry();

        second.Exists("persisted").ShouldBeTrue();
        second.GetActive()?.Name.ShouldBe("persisted");
    }

    private static PythonEnvironment MakeEnv(string name) => new()
    {
        Name = name,
        VenvPath = Path.Combine(Path.GetTempPath(), name),
        Status = PythonEnvironmentStatus.Unknown,
    };

    public void Dispose()
    {
        if (File.Exists(_tempFile))
            File.Delete(_tempFile);
    }
}
