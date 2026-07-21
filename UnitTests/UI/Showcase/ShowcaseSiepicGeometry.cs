using CAP_Core.Export;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using Moq;

namespace UnitTests.UI.Showcase;

/// <summary>
/// SiEPIC-shaped fixtures for the GDS-export showcase: a preview-service stub whose render
/// results are derived from the bundled PDK JSON's own calibration (so every pin aligns at
/// Δ 0.00 µm — the state the shipped, calibration-locked JSONs are actually in, see
/// <c>SiepicBundledCalibrationE2ETests</c>), plus a drawn <c>ebeam_gc_te1550</c> polygon set
/// (focusing grating: fan slab + teeth arcs) so the offset editor's overlay shows
/// real-looking silicon geometry without a Python environment in the test host.
/// </summary>
internal static class ShowcaseSiepicGeometry
{
    private const double PortHalfWidthMicrometers = 0.25;
    private const double PinStubLengthMicrometers = 1.6;

    /// <summary>
    /// A successful render whose bbox and pin positions are computed from the draft's own
    /// NazcaOriginOffset calibration — exactly the geometry a correctly calibrated JSON
    /// expects (mapper convention: OffsetX = -XMin, OffsetY = YMax; Lunima y-down vs
    /// Nazca y-up), so <c>PdkOffsetCalibration.Evaluate</c> reports Aligned with Δ 0.00 µm.
    /// GDS-side pins carry SiEPIC-style <c>optN</c> names — the per-pin report then shows
    /// the real cross-PDK mapping ("port 1 → opt1") the greedy nearest matcher produces.
    /// </summary>
    public static NazcaPreviewResult PerfectResultFor(
        PdkComponentDraft draft, IReadOnlyList<NazcaPreviewPolygon>? polygons = null)
    {
        double offsetX = draft.NazcaOriginOffsetX ?? 0;
        double offsetY = draft.NazcaOriginOffsetY ?? 0;
        var pins = draft.Pins.Select((pin, index) =>
        {
            double x = pin.OffsetXMicrometers - offsetX;
            double y = offsetY - pin.OffsetYMicrometers;
            double angleRadians = pin.AngleDegrees * Math.PI / 180.0;
            return new NazcaPreviewPin
            {
                Name = $"opt{index + 1}",
                X = x,
                Y = y,
                Angle = pin.AngleDegrees,
                // The stub points INTO the component — opposite the pin's outward normal.
                StubX1 = x - Math.Cos(angleRadians) * PinStubLengthMicrometers,
                StubY1 = y - Math.Sin(angleRadians) * PinStubLengthMicrometers,
            };
        }).ToList();

        return new NazcaPreviewResult
        {
            Success = true,
            XMin = -offsetX,
            XMax = draft.WidthMicrometers - offsetX,
            YMax = offsetY,
            YMin = offsetY - draft.HeightMicrometers,
            Polygons = polygons ?? Array.Empty<NazcaPreviewPolygon>(),
            Pins = pins,
        };
    }

    /// <summary>
    /// Preview-service stub answering every render request with the perfect result for the
    /// matching draft (flat SiEPIC names like <c>ebeam_gc_te1550</c>); grating-coupler
    /// requests carry the drawn focusing-grating geometry so the overlay shows silicon,
    /// not just a dashed box.
    /// </summary>
    public static NazcaComponentPreviewService StubPreviewService(
        IReadOnlyList<PdkComponentDraft> drafts)
    {
        var mock = new Mock<NazcaComponentPreviewService>(
            "python3", "render_component_preview.py", null, null);
        mock.Setup(s => s.RenderAsync(
                It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string? _, string function, string? parameters, CancellationToken _) =>
            {
                var draft = drafts.FirstOrDefault(d =>
                    (d.NazcaFunction == function || d.NazcaFunction?.EndsWith("." + function) == true)
                    && (d.NazcaParameters ?? "") == (parameters ?? ""));
                if (draft == null)
                    return NazcaPreviewResult.Fail($"no showcase fixture for '{function}'");
                return PerfectResultFor(
                    draft, function == "ebeam_gc_te1550" ? GratingCouplerPolygons(draft) : null);
            });
        return mock.Object;
    }

    /// <summary>
    /// Layer-1 silicon polygons approximating SiEPIC's <c>ebeam_gc_te1550</c> focusing
    /// grating coupler in Nazca space: a fan-shaped slab opening westward from the optical
    /// port plus concentric grating-teeth arcs centered on the port's focal point.
    /// </summary>
    public static IReadOnlyList<NazcaPreviewPolygon> GratingCouplerPolygons(PdkComponentDraft draft)
    {
        var port = draft.Pins[0];
        double focusX = port.OffsetXMicrometers - (draft.NazcaOriginOffsetX ?? 0);
        double focusY = (draft.NazcaOriginOffsetY ?? 0) - port.OffsetYMicrometers;
        const double halfAngleDegrees = 16.5;

        var polygons = new List<NazcaPreviewPolygon>
        {
            FanPolygon(focusX, focusY, radius: 14.0, halfAngleDegrees),
        };
        for (double radius = 15.0; radius <= 37.0; radius += 2.0)
            polygons.Add(ToothPolygon(focusX, focusY, radius, radius + 1.15, halfAngleDegrees));
        return polygons;
    }

    /// <summary>The slab fan between the 0.5 µm port aperture and the first grating tooth.</summary>
    private static NazcaPreviewPolygon FanPolygon(
        double focusX, double focusY, double radius, double halfAngleDegrees)
    {
        var vertices = new List<(double X, double Y)> { (focusX, focusY + PortHalfWidthMicrometers) };
        vertices.AddRange(Arc(focusX, focusY, radius, +halfAngleDegrees, -halfAngleDegrees));
        vertices.Add((focusX, focusY - PortHalfWidthMicrometers));
        return new NazcaPreviewPolygon { Layer = 1, Vertices = vertices };
    }

    /// <summary>One annular grating tooth: inner arc out, outer arc back.</summary>
    private static NazcaPreviewPolygon ToothPolygon(
        double focusX, double focusY, double innerRadius, double outerRadius, double halfAngleDegrees)
    {
        var vertices = Arc(focusX, focusY, innerRadius, -halfAngleDegrees, +halfAngleDegrees)
            .Concat(Arc(focusX, focusY, outerRadius, +halfAngleDegrees, -halfAngleDegrees))
            .ToList();
        return new NazcaPreviewPolygon { Layer = 1, Vertices = vertices };
    }

    /// <summary>Arc samples around the westward (180°) direction from the focal point.</summary>
    private static IEnumerable<(double X, double Y)> Arc(
        double focusX, double focusY, double radius, double fromDegrees, double toDegrees)
    {
        const int segments = 20;
        for (int i = 0; i <= segments; i++)
        {
            double degrees = 180 + fromDegrees + (toDegrees - fromDegrees) * i / segments;
            double radians = degrees * Math.PI / 180.0;
            yield return (focusX + radius * Math.Cos(radians), focusY + radius * Math.Sin(radians));
        }
    }
}
