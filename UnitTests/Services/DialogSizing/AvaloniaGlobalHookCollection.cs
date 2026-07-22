using Xunit;

namespace UnitTests.Services.DialogSizing;

/// <summary>
/// Serializes tests that install process-wide Avalonia class handlers (e.g.
/// <c>DialogSizeGuard.Initialize</c>) or show real windows on the shared headless UI
/// thread. Both are global state: a concurrently running screenshot/walkthrough test can
/// observe the hook or lose its rendered frame (<c>CaptureRenderedFrame</c> returns null),
/// so these tests must not run in parallel with any other collection — same mechanism as
/// <c>LocalizationSingletonCollection</c>.
[CollectionDefinition("AvaloniaGlobalHook", DisableParallelization = true)]
public sealed class AvaloniaGlobalHookCollection
{
}
