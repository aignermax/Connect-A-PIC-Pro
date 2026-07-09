using System;
using System.Collections.Generic;
using CAP.Avalonia.Services.AddCustomComponent;
using CAP_Core.Export;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using CAP_DataAccess.Persistence.PIR;
using Shouldly;
using Xunit;

namespace UnitTests.Components.AddCustomComponent;

/// <summary>
/// Covers <see cref="CustomComponentDraftFactory"/>: the pure draft/pin assembly extracted out of
/// <see cref="CAP.Avalonia.ViewModels.Components.AddCustomComponent.NewComponentViewModel"/> to
/// keep that view model under the 250-line new-file cap.
/// </summary>
public class CustomComponentDraftFactoryTests
{
    private static GeometryExtractResult Preview(double xMin = 0, double yMax = 2) => new(
        Success: true, Error: null, WidthUm: 10, HeightUm: 2,
        Pins: new List<OverridePinData>
        {
            new() { Name = "o1", OffsetXMicrometers = 0, OffsetYMicrometers = 1, AngleDegrees = 180 },
            new() { Name = "o2", OffsetXMicrometers = 10, OffsetYMicrometers = 1, AngleDegrees = 0 },
        },
        Raw: new NazcaPreviewResult { Success = true, XMin = xMin, YMin = yMax - 2, XMax = xMin + 10, YMax = yMax });

    [Fact]
    public void Build_gdsfactory_maps_size_pins_and_routes_function_to_gdsfactory_field()
    {
        var reference = new GeometryReference(GeometryBackend.GdsFactory, "cspdk.sin300", "coupler", null);

        var draft = CustomComponentDraftFactory.Build("My Coupler", reference, Preview(), sMatrix: null);

        draft.Name.ShouldBe("My Coupler");
        draft.WidthMicrometers.ShouldBe(10);
        draft.HeightMicrometers.ShouldBe(2);
        draft.GdsFactoryFunction.ShouldBe("cspdk.sin300.coupler");
        draft.NazcaFunction.ShouldBeNull();
        draft.SMatrix.ShouldBeNull();                     // null in => black box, never fabricated
        draft.Pins.Count.ShouldBe(2);
        draft.Pins[0].Name.ShouldBe("o1");
        draft.Pins[0].AngleDegrees.ShouldBe(180);
    }

    [Fact]
    public void Build_nazca_routes_function_to_the_nazca_field_and_keeps_the_sMatrix()
    {
        var reference = new GeometryReference(GeometryBackend.Nazca, "demo", "mmi", null);
        var sMatrix = new PdkSMatrixDraft { WavelengthNm = 1550 };

        var draft = CustomComponentDraftFactory.Build("MMI", reference, Preview(), sMatrix);

        draft.NazcaFunction.ShouldBe("demo.mmi");
        draft.GdsFactoryFunction.ShouldBeNull();
        draft.SMatrix.ShouldBe(sMatrix);
    }

    [Fact]
    public void Build_nazca_derives_the_origin_offset_from_the_rendered_bbox()
    {
        // #701: ox = -XMin, oy = YMax (cell org measured from the bbox top-left corner,
        // same formula as the PDK Offset Editor's Auto-Calibrate).
        var reference = new GeometryReference(GeometryBackend.Nazca, "demo", "mmi", null);

        var draft = CustomComponentDraftFactory.Build("MMI", reference, Preview(xMin: -3, yMax: 5), sMatrix: null);

        draft.NazcaOriginOffsetX.ShouldBe(3);
        draft.NazcaOriginOffsetY.ShouldBe(5);
    }

    [Fact]
    public void Build_gdsfactory_does_not_set_nazca_offsets()
    {
        var reference = new GeometryReference(GeometryBackend.GdsFactory, "cspdk.sin300", "coupler", null);

        var draft = CustomComponentDraftFactory.Build("C", reference, Preview(xMin: -3, yMax: 5), sMatrix: null);

        draft.NazcaOriginOffsetX.ShouldBeNull();
        draft.NazcaOriginOffsetY.ShouldBeNull();
    }

    [Theory]
    [InlineData(GeometryBackend.GdsFactory, "gdsfactory")]
    [InlineData(GeometryBackend.Nazca, "nazca")]
    public void BuildFromRawCode_carries_code_backend_and_offsets(GeometryBackend backend, string expectedBackend)
    {
        var draft = CustomComponentDraftFactory.BuildFromRawCode(
            "Raw", backend, "component = something()", Preview(xMin: 1, yMax: 4), sMatrix: null);

        draft.RawCode.ShouldBe("component = something()");
        draft.RawCodeBackend.ShouldBe(expectedBackend);
        draft.GdsFactoryFunction.ShouldBeNull();
        draft.NazcaFunction.ShouldBeNull();
        draft.NazcaOriginOffsetX.ShouldBe(-1);   // always derived: placement seeds the override anchor from it
        draft.NazcaOriginOffsetY.ShouldBe(4);
        draft.Pins.Count.ShouldBe(2);
    }
}
