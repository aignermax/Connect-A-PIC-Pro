using System.Collections.Generic;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP_DataAccess.Persistence.PIR;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

/// <summary>
/// Covers <see cref="SMatrixSourceResolver"/> (#701): the "no invented physics" gating —
/// FDTD needs a computed result, the lossless ideal needs exactly two ports, and the
/// black box is always resolvable.
/// </summary>
public class SMatrixSourceResolverTests
{
    private static List<OverridePinData> Pins(int count)
    {
        var pins = new List<OverridePinData>();
        for (int i = 0; i < count; i++)
            pins.Add(new OverridePinData { Name = $"o{i + 1}" });
        return pins;
    }

    [Fact]
    public void BlackBox_always_resolves_to_no_draft()
    {
        var result = SMatrixSourceResolver.Resolve(SMatrixSource.BlackBox, null, Pins(3));

        result.Success.ShouldBeTrue();
        result.Draft.ShouldBeNull();
    }

    [Fact]
    public void Fdtd_without_a_computed_model_fails_instead_of_degrading_silently()
    {
        var result = SMatrixSourceResolver.Resolve(SMatrixSource.Fdtd, null, Pins(2));

        result.Success.ShouldBeFalse();
        result.Error.ShouldContain("Compute S-Matrix");
    }

    [Fact]
    public void LosslessTwoPort_requires_exactly_two_ports()
    {
        var result = SMatrixSourceResolver.Resolve(SMatrixSource.LosslessTwoPort, null, Pins(3));

        result.Success.ShouldBeFalse();
        result.Error.ShouldContain("2 ports");
    }

    [Fact]
    public void LosslessTwoPort_on_two_ports_yields_the_exact_unit_passthrough()
    {
        var result = SMatrixSourceResolver.Resolve(SMatrixSource.LosslessTwoPort, null, Pins(2));

        result.Success.ShouldBeTrue();
        var draft = result.Draft.ShouldNotBeNull();
        draft.WavelengthNm.ShouldBe(SMatrixSourceResolver.LosslessIdealWavelengthNm);
        draft.Connections!.Count.ShouldBe(2);
        foreach (var conn in draft.Connections)
        {
            conn.Magnitude.ShouldBe(1.0);       // exact ideal, not an estimate
            conn.PhaseDegrees.ShouldBe(0.0);
        }
        draft.Connections[0].FromPin.ShouldBe("o1");
        draft.Connections[0].ToPin.ShouldBe("o2");
        draft.Connections[1].FromPin.ShouldBe("o2");
        draft.Connections[1].ToPin.ShouldBe("o1");
    }

    [Fact]
    public void Fdtd_with_a_computed_model_converts_it()
    {
        var model = new ComponentSMatrixData
        {
            Wavelengths = new Dictionary<string, SMatrixWavelengthEntry>
            {
                ["1550"] = new SMatrixWavelengthEntry
                {
                    Rows = 1, Cols = 1,
                    Real = new List<double> { 0.5 },
                    Imag = new List<double> { 0.0 },
                    PortNames = new List<string> { "o1" },
                }
            }
        };

        var result = SMatrixSourceResolver.Resolve(SMatrixSource.Fdtd, model, Pins(1));

        result.Success.ShouldBeTrue();
        result.Draft.ShouldNotBeNull();
        result.Draft!.WavelengthData!.Count.ShouldBe(1);
    }
}
