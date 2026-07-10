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
    /// attaching <paramref name="sMatrix"/> (null = black box) verbatim. When
    /// <paramref name="rawCode"/> is set, it (plus <paramref name="rawCodeBackend"/>) becomes the
    /// draft's authoritative source and <c>GdsFactoryFunction</c>/<c>NazcaFunction</c> are left
    /// unset; otherwise the qualified function is routed to the gdsfactory or nazca field per the
    /// reference's backend, as in the module/function reference path.
    /// </summary>
    public static PdkComponentDraft Build(
        string name, GeometryReference reference, GeometryExtractResult preview, PdkSMatrixDraft? sMatrix,
        string? rawCode = null, string? rawCodeBackend = null)
    {
        var draft = new PdkComponentDraft
        {
            Name = name,
            WidthMicrometers = preview.WidthUm,
            HeightMicrometers = preview.HeightUm,
            Pins = MapPins(preview.Pins),
            SMatrix = sMatrix,
        };
        if (rawCode is not null)
        {
            draft.RawCode = rawCode;
            draft.RawCodeBackend = rawCodeBackend;
        }
        else if (reference.Backend == GeometryBackend.GdsFactory)
            draft.GdsFactoryFunction = reference.QualifiedFunction;
        else
            draft.NazcaFunction = reference.QualifiedFunction;
        return draft;
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
