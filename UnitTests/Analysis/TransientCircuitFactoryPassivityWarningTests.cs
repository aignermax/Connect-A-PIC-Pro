using CAP.Avalonia.ViewModels.Analysis;
using CAP_Core.LightCalculation;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis;

/// <summary>
/// The transient/eye pipeline sweeps hundreds of wavelength stops per run; a bundled
/// measured component inside the tolerated noise band would emit one warning per stop.
/// The factory forwards only the FIRST warning per component to the error console.
/// </summary>
public class TransientCircuitFactoryPassivityWarningTests
{
    [Fact]
    public void DedupePerComponent_forwardsOnlyTheFirstWarningPerComponent()
    {
        var received = new List<PassivityWarning>();
        var sink = TransientCircuitFactory.DedupePerComponent(received.Add)!;

        sink(new PassivityWarning("Broadband DC TE 1550", 1540, 0.40));
        sink(new PassivityWarning("Broadband DC TE 1550", 1550, 0.45));
        sink(new PassivityWarning("Y-Branch (measured)", 1550, 0.10));
        sink(new PassivityWarning("Broadband DC TE 1550", 1560, 0.44));

        received.Select(w => w.ComponentName).ShouldBe(
            new[] { "Broadband DC TE 1550", "Y-Branch (measured)" });
    }

    [Fact]
    public void DedupePerComponent_nullSink_staysNull()
    {
        TransientCircuitFactory.DedupePerComponent(null).ShouldBeNull();
    }
}
