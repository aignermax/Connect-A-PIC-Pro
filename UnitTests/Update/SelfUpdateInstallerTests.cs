using CAP.Avalonia.Services.Update;
using Shouldly;

namespace UnitTests.Update;

public class SelfUpdateInstallerTests
{
    [Fact]
    public void IsDirectoryWritable_TempDirectory_ReturnsTrue()
    {
        SelfUpdateInstaller.IsDirectoryWritable(Path.GetTempPath()).ShouldBeTrue();
    }

    [Fact]
    public void IsDirectoryWritable_NonexistentDirectory_ReturnsFalse()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"lunima-missing-{Guid.NewGuid():N}", "deeper");

        SelfUpdateInstaller.IsDirectoryWritable(missing).ShouldBeFalse();
    }
}
