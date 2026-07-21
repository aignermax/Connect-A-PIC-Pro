using CAP_Core;
using CAP_Contracts.Logger;
using Shouldly;
using Xunit;

namespace UnitTests.Core;

/// <summary>
/// The error console's <see cref="ErrorConsoleService.Entries"/> is UI-bound; worker threads
/// (simulation runs, python renders) log through the same service. Every append must therefore
/// route through <see cref="ErrorConsoleService.PostToUiThread"/> when installed — logging from
/// a background thread without it crashed the transient simulation ("Call from invalid thread").
/// </summary>
public class ErrorConsoleThreadMarshalTests
{
    [Fact]
    public void Log_withDispatcherInstalled_appendsThroughTheDispatcher()
    {
        var console = new ErrorConsoleService();
        var dispatched = new List<Action>();
        console.PostToUiThread = dispatched.Add;

        console.LogWarning("from a worker thread");

        console.Entries.ShouldBeEmpty("the append must wait for the dispatcher");
        dispatched.Count.ShouldBe(1);
        dispatched[0]();
        console.Entries.Count.ShouldBe(1);
        console.Entries[0].Level.ShouldBe(LogLevel.Warn);
    }

    [Fact]
    public void Log_withoutDispatcher_appendsSynchronously()
    {
        var console = new ErrorConsoleService();

        console.LogError("headless/test path");

        console.Entries.Count.ShouldBe(1);
    }

    [Fact]
    public void Log_capsEntries_atMaxAlsoWhenDispatched()
    {
        var console = new ErrorConsoleService();
        var dispatched = new List<Action>();
        console.PostToUiThread = dispatched.Add;

        for (int i = 0; i < ErrorConsoleService.MaxEntries + 5; i++)
            console.LogInfo($"entry {i}");
        foreach (var action in dispatched)
            action();

        console.Entries.Count.ShouldBe(ErrorConsoleService.MaxEntries);
        console.Entries[^1].Message.ShouldContain($"entry {ErrorConsoleService.MaxEntries + 4}");
    }
}
