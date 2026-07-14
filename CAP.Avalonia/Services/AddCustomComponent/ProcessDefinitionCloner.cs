using System.Collections.Generic;
using System.Linq;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP.Avalonia.Services.AddCustomComponent;

/// <summary>
/// Deep-copies a <see cref="ProcessDefinition"/> so it can be handed to a
/// <see cref="CAP.Avalonia.ViewModels.ProcessManagementViewModel"/> editor as an independent
/// starting point without ever aliasing the source's rows (issue #733 review, Finding 3).
/// A <see cref="ProcessDefinition"/> handed to the editor can be a live, in-memory object owned
/// elsewhere — an already-loaded PDK's own process (<see cref="ProcessManagementViewModel.LoadForSinglePdkEdit"/>)
/// or a template offered by <c>CreateCustomPdkViewModel</c> — and
/// <see cref="ProcessManagementViewModel.Load"/> only copies collection references. Without a
/// deep copy, editing the loaded grid rows would mutate that other owner's process object in
/// place before the user ever saves. Shared by both call sites so the copy logic exists exactly
/// once.
/// </summary>
public static class ProcessDefinitionCloner
{
    /// <summary>Returns an independent deep copy of <paramref name="source"/>.</summary>
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
