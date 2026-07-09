using System.Collections.Generic;
using System.Linq;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using CAP_DataAccess.Persistence.PIR;

namespace CAP.Avalonia.Services.AddCustomComponent;

/// <summary>
/// Assembles the <see cref="PdkComponentDraft"/> saved by <c>NewComponentViewModel</c> from a
/// rendered geometry reference plus its extracted size/pins and an (already-decided) S-matrix.
/// Pure mapping — it invents no physics; the caller passes the S-matrix it resolved (black box
/// or FDTD), so this factory never fabricates one.
/// </summary>
public static class CustomComponentDraftFactory
{
    /// <summary>
    /// Builds the draft for <paramref name="name"/> from the rendered <paramref name="preview"/>,
    /// routing the qualified function to the gdsfactory or nazca field per the reference's backend
    /// and attaching <paramref name="sMatrix"/> (null = black box) verbatim. Nazca components
    /// additionally get their <c>NazcaOriginOffset</c> derived from the render, so the strict
    /// PDK load/export path accepts them (issue #701).
    /// </summary>
    public static PdkComponentDraft Build(
        string name, GeometryReference reference, GeometryExtractResult preview, PdkSMatrixDraft? sMatrix)
    {
        var draft = BuildBase(name, preview, sMatrix);
        if (reference.Backend == GeometryBackend.GdsFactory)
        {
            draft.GdsFactoryFunction = reference.QualifiedFunction;
        }
        else
        {
            draft.NazcaFunction = reference.QualifiedFunction;
            SetNazcaOriginOffset(draft, preview);
        }
        return draft;
    }

    /// <summary>
    /// Builds the draft for a raw-code authored component (issue #701, v2): instead of a
    /// function reference it carries the user's complete cell code plus the backend it is
    /// written for, and always the render-derived <c>NazcaOriginOffset</c> — placement uses
    /// it to seed the per-instance override's export anchor (bboxXMin = −offsetX,
    /// bboxYMax = offsetY).
    /// </summary>
    public static PdkComponentDraft BuildFromRawCode(
        string name, GeometryBackend backend, string rawCode,
        GeometryExtractResult preview, PdkSMatrixDraft? sMatrix)
    {
        var draft = BuildBase(name, preview, sMatrix);
        draft.RawCode = rawCode;
        draft.RawCodeBackend = backend == GeometryBackend.GdsFactory ? "gdsfactory" : "nazca";
        SetNazcaOriginOffset(draft, preview);
        return draft;
    }

    private static PdkComponentDraft BuildBase(
        string name, GeometryExtractResult preview, PdkSMatrixDraft? sMatrix) => new()
    {
        Name = name,
        WidthMicrometers = preview.WidthUm,
        HeightMicrometers = preview.HeightUm,
        Pins = MapPins(preview.Pins),
        SMatrix = sMatrix,
    };

    /// <summary>
    /// Derives the Nazca origin offset from the rendered bounding box, per the
    /// <c>NazcaCoordinateMapper</c> contract (same formula as the PDK Offset Editor's
    /// Auto-Calibrate): the cell org measured from the bbox TOP-LEFT corner —
    /// <c>ox = −XMin</c>, <c>oy = YMax</c>.
    /// </summary>
    private static void SetNazcaOriginOffset(PdkComponentDraft draft, GeometryExtractResult preview)
    {
        draft.NazcaOriginOffsetX = -preview.Raw.XMin;
        draft.NazcaOriginOffsetY = preview.Raw.YMax;
    }

    /// <summary>
    /// Maps extracted preview pins to PDK physical-pin drafts. Only name/offset/angle are
    /// derivable from geometry extraction — logical-pin linkage and pin kind have no source
    /// here and are left at their defaults.
    /// </summary>
    private static List<PhysicalPinDraft> MapPins(IReadOnlyList<OverridePinData> pins) =>
        pins.Select(p => new PhysicalPinDraft
        {
            Name = p.Name,
            OffsetXMicrometers = p.OffsetXMicrometers,
            OffsetYMicrometers = p.OffsetYMicrometers,
            AngleDegrees = p.AngleDegrees,
        }).ToList();
}
