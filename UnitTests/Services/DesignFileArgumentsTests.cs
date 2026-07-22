using CAP.Avalonia.Services;
using Shouldly;

namespace UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="DesignFileArguments.FindDesignFile"/> — the
/// command-line argument parser that picks the .lun design file to open at
/// startup (first existing .lun argument, case-insensitive extension,
/// normalized to a full path).
/// </summary>
public class DesignFileArgumentsTests : IDisposable
{
    private readonly string _existingDesignPath;

    public DesignFileArgumentsTests()
    {
        _existingDesignPath = Path.Combine(Path.GetTempPath(), $"args-test-{Guid.NewGuid():N}.lun");
        File.WriteAllText(_existingDesignPath, "{}");
    }

    public void Dispose()
    {
        if (File.Exists(_existingDesignPath))
        {
            File.Delete(_existingDesignPath);
        }
    }

    [Fact]
    public void NoArguments_ReturnsNull()
    {
        DesignFileArguments.FindDesignFile(Array.Empty<string>()).ShouldBeNull();
    }

    [Fact]
    public void NonDesignArguments_ReturnsNull()
    {
        DesignFileArguments.FindDesignFile(new[] { "--verbose", "readme.txt" }).ShouldBeNull();
    }

    [Fact]
    public void DesignArgumentForMissingFile_ReturnsNull()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.lun");

        DesignFileArguments.FindDesignFile(new[] { missing }).ShouldBeNull();
    }

    [Fact]
    public void ExistingDesignArgument_ReturnsFullPath()
    {
        DesignFileArguments.FindDesignFile(new[] { _existingDesignPath })
            .ShouldBe(Path.GetFullPath(_existingDesignPath));
    }

    [Fact]
    public void ExtensionMatch_IsCaseInsensitive()
    {
        var upperPath = Path.ChangeExtension(_existingDesignPath, ".LUN");
        File.Move(_existingDesignPath, upperPath);
        try
        {
            DesignFileArguments.FindDesignFile(new[] { upperPath })
                .ShouldBe(Path.GetFullPath(upperPath));
        }
        finally
        {
            File.Move(upperPath, _existingDesignPath);
        }
    }

    [Fact]
    public void FirstExistingDesignArgument_Wins()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.lun");

        DesignFileArguments.FindDesignFile(new[] { "--flag", missing, _existingDesignPath })
            .ShouldBe(Path.GetFullPath(_existingDesignPath));
    }

    [Fact]
    public void InvalidPathArgument_IsSkippedWithoutThrowing()
    {
        DesignFileArguments.FindDesignFile(new[] { "\0bad.lun", _existingDesignPath })
            .ShouldBe(Path.GetFullPath(_existingDesignPath));
    }
}
