using System.Collections.Generic;
using System.Linq;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using CAP_DataAccess.Persistence.PIR;

namespace CAP.Avalonia.Services.AddCustomComponent;

public static class CustomComponentDraftFactory
{
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

    private static List<PhysicalPinDraft> MapPins(IReadOnlyList<OverridePinData> pins) =>
        pins.Select(p => new PhysicalPinDraft
        {
            Name = p.Name,
            OffsetXMicrometers = p.OffsetXMicrometers,
            OffsetYMicrometers = p.OffsetYMicrometers,
            AngleDegrees = p.AngleDegrees,
        }).ToList();
}
