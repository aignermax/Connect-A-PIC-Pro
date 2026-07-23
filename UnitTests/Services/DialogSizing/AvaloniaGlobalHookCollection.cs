using Xunit;

namespace UnitTests.Services.DialogSizing;

// Serializes tests that install global Avalonia hooks or show real windows on the shared
// headless UI thread, so they can't disturb concurrently running tests.
[CollectionDefinition("AvaloniaGlobalHook", DisableParallelization = true)]
public sealed class AvaloniaGlobalHookCollection
{
}
