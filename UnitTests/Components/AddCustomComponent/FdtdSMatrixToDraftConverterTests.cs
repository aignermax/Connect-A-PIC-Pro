using System.Collections.Generic;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP_DataAccess.Persistence.PIR;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

/// <summary>
/// Verifies <see cref="FdtdSMatrixToDraftConverter"/> maps real FDTD results honestly,
/// never fabricates values, and produces the exact lossless two-port identity.
/// </summary>
public class FdtdSMatrixToDraftConverterTests
{
    [Fact]
    public void FromFdtd_maps_each_wavelength_entry()
    {
        var data = new ComponentSMatrixData
        {
            SourceNote = "FDTD Meep 2D",
            Wavelengths = new()
            {
                ["1550"] = new SMatrixWavelengthEntry
                {
                    Rows = 2, Cols = 2,
                    Real = new() { 0, 1, 1, 0 },
                    Imag = new() { 0, 0, 0, 0 },
                    PortNames = new() { "o1", "o2" }
                }
            }
        };

        var draft = FdtdSMatrixToDraftConverter.FromFdtd(data);

        draft.ShouldNotBeNull();
        draft!.WavelengthData!.Count.ShouldBe(1);
        draft.WavelengthData[0].WavelengthNm.ShouldBe(1550);
    }

    [Fact]
    public void FromFdtd_converts_rectangular_values_to_magnitude_and_phase()
    {
        var data = new ComponentSMatrixData
        {
            Wavelengths = new()
            {
                ["1550"] = new SMatrixWavelengthEntry
                {
                    Rows = 2, Cols = 2,
                    Real = new() { 0, 1, 1, 0 },
                    Imag = new() { 0, 0, 0, 0 },
                    PortNames = new() { "o1", "o2" }
                }
            }
        };

        var draft = FdtdSMatrixToDraftConverter.FromFdtd(data);

        var connections = draft!.WavelengthData![0].Connections;
        connections.ShouldContain(c => c.FromPin == "o2" && c.ToPin == "o1" && c.Magnitude == 1.0 && c.PhaseDegrees == 0.0);
        connections.ShouldContain(c => c.FromPin == "o1" && c.ToPin == "o2" && c.Magnitude == 1.0 && c.PhaseDegrees == 0.0);
    }

    [Fact]
    public void FromFdtd_throws_ArgumentException_on_length_mismatch()
    {
        var data = new ComponentSMatrixData
        {
            Wavelengths = new()
            {
                ["1550"] = new SMatrixWavelengthEntry
                {
                    Rows = 2, Cols = 2,
                    Real = new() { 0, 1, 1 }, // 3 entries, but 2x2 needs 4
                    Imag = new() { 0, 0, 0, 0 },
                    PortNames = new() { "o1", "o2" }
                }
            }
        };

        Should.Throw<System.ArgumentException>(() => FdtdSMatrixToDraftConverter.FromFdtd(data));
    }

    [Fact]
    public void FromFdtd_returns_null_when_no_wavelengths()
    {
        var data = new ComponentSMatrixData { Wavelengths = new() };
        FdtdSMatrixToDraftConverter.FromFdtd(data).ShouldBeNull();
    }

    [Fact]
    public void BlackBox_is_null_so_the_component_has_no_model()
    {
        FdtdSMatrixToDraftConverter.BlackBox().ShouldBeNull();
    }

    [Fact]
    public void LosslessTwoPort_is_unit_magnitude_both_directions()
    {
        var draft = FdtdSMatrixToDraftConverter.LosslessTwoPort("o1", "o2", 1550);
        draft.Connections.Count.ShouldBe(2);
        draft.Connections.ShouldContain(c => c.FromPin == "o1" && c.ToPin == "o2" && c.Magnitude == 1.0);
        draft.Connections.ShouldContain(c => c.FromPin == "o2" && c.ToPin == "o1" && c.Magnitude == 1.0);
    }
}
