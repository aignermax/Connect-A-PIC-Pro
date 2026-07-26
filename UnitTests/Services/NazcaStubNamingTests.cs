using CAP.Avalonia.Services;
using Shouldly;
using Xunit;

namespace UnitTests.Services;

/// <summary>
/// Pins the stub-naming contract of issue #783: parameterized components get a short
/// deterministic parameters hash so each parameter set gets its own stub cell;
/// unparameterized components (and names that bypass stubs) keep the bare name, so
/// existing exports are unchanged.
/// </summary>
public class NazcaStubNamingTests
{
    [Fact]
    public void StubName_NoParameters_KeepsBareFunctionName()
    {
        NazcaStubNaming.StubName("ebeam_y_1550", null).ShouldBe("ebeam_y_1550");
        NazcaStubNaming.StubName("ebeam_y_1550", "").ShouldBe("ebeam_y_1550");
    }

    [Fact]
    public void StubName_SameParameters_IsDeterministic()
    {
        NazcaStubNaming.StubName("ebeam_dc_te1550", "gap=200E-9")
            .ShouldBe(NazcaStubNaming.StubName("ebeam_dc_te1550", "gap=200E-9"));
    }

    [Fact]
    public void StubName_DifferentParameters_DifferentNames()
    {
        NazcaStubNaming.StubName("ebeam_dc_te1550", "gap=200E-9")
            .ShouldNotBe(NazcaStubNaming.StubName("ebeam_dc_te1550", "gap=200E-9,Lc=5E-6"));
    }

    [Fact]
    public void StubName_Parameters_AppendsShortHexHash()
    {
        // 6 hex chars: short GDS cell names, pinned scheme (SHA-256, first 3 bytes).
        NazcaStubNaming.StubName("ebeam_dc_te1550", "gap=200E-9")
            .ShouldBe("ebeam_dc_te1550_7f986c");
    }

    [Fact]
    public void StubName_DottedName_KeepsBareName()
    {
        // Dotted names call the real module function directly (demo.shallow.bend) —
        // their stub is never invoked, so hashing would break the call.
        NazcaStubNaming.StubName("demo.shallow.bend", "angle=90")
            .ShouldBe("demo.shallow.bend");
    }

    [Fact]
    public void StubName_ParametricStraight_KeepsBareName()
    {
        // The straight stub embeds the length in its runtime cell name already.
        NazcaStubNaming.StubName("demo_pdk_straight", "length=100")
            .ShouldBe("demo_pdk_straight");
    }
}
