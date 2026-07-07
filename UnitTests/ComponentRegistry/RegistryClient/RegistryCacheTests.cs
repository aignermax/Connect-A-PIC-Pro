using CAP_Core.ComponentRegistry;
using Shouldly;
using Xunit;

namespace UnitTests.ComponentRegistry;

/// <summary>Tests for <see cref="RegistryCache"/> path handling and round-trips.</summary>
public sealed class RegistryCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "lunima-registry-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void WriteThenRead_RoundTripsNestedPaths()
    {
        var cache = new RegistryCache(_root);

        cache.Write("processes/generic-si220/components/x/component.json", "{\"id\":\"x\"}");

        cache.Read("processes/generic-si220/components/x/component.json").ShouldBe("{\"id\":\"x\"}");
    }

    [Fact]
    public void Read_MissingEntry_ReturnsNull()
    {
        new RegistryCache(_root).Read("index.json").ShouldBeNull();
    }

    [Theory]
    [InlineData("../outside.json")]
    [InlineData("a/../../outside.json")]
    [InlineData("")]
    public void Write_PathEscapingCacheRoot_IsRejected(string relativePath)
    {
        var cache = new RegistryCache(_root);

        cache.Write(relativePath, "data");

        cache.Read(relativePath).ShouldBeNull();
        Directory.Exists(_root).ShouldBeFalse();
    }

    [Fact]
    public void Write_RootedPath_IsRejected()
    {
        var cache = new RegistryCache(_root);
        var rooted = Path.Combine(Path.GetTempPath(), "lunima-registry-tests", "escape.json");

        cache.Write(rooted, "data");

        File.Exists(rooted).ShouldBeFalse();
    }
}
