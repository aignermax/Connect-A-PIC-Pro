using System.Collections.Generic;
using System.Linq;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP.Avalonia.Services.AddCustomComponent;

public static class ProcessDefinitionCloner
{
    public static ProcessDefinition Clone(ProcessDefinition source) => new()
    {
        Name = source.Name,
        Foundry = source.Foundry,
        Version = source.Version,
        CoreThicknessNm = source.CoreThicknessNm,
        Layers = source.Layers.Select(l => new ProcessLayer
        {
            Name = l.Name, Layer = l.Layer, Datatype = l.Datatype, Field = l.Field, Description = l.Description,
        }).ToList(),
        Xsections = source.Xsections.Select(x => new ProcessXsection
        {
            Name = x.Name, Kind = x.Kind, WidthUm = x.WidthUm, MinRadiusUm = x.MinRadiusUm,
            RecommendedRadiusUm = x.RecommendedRadiusUm, Layers = new List<string>(x.Layers), Description = x.Description,
        }).ToList(),
        Materials = source.Materials.Select(m => new ProcessMaterial
        {
            Name = m.Name, NByWavelengthNm = new Dictionary<int, double>(m.NByWavelengthNm), Role = m.Role,
        }).ToList(),
        AllowedAngles = new List<int>(source.AllowedAngles),
        ElectricalBridgeRequired = source.ElectricalBridgeRequired,
    };
}
