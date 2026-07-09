using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;

namespace CAP.Avalonia.ViewModels.Process;

/// <summary>
/// Computes and applies design-specific overrides between a preset fabrication process
/// and the edited state shown in the Fabrication Process window (issue #696). Only the
/// differences are stored with the design; the preset definition itself is never mutated.
/// </summary>
public static class ProcessOverrideDiff
{
    /// <summary>Describes one comparable/settable property of a process row.</summary>
    private sealed record RowProperty<T>(string Name, Func<T, string?> Get, Action<T, string?> Set);

    private static readonly IReadOnlyList<RowProperty<ProcessLayer>> LayerProperties = new RowProperty<ProcessLayer>[]
    {
        new(nameof(ProcessLayer.Layer), l => Fmt(l.Layer), (l, v) => l.Layer = ParseInt(v)),
        new(nameof(ProcessLayer.Datatype), l => Fmt(l.Datatype), (l, v) => l.Datatype = ParseInt(v)),
        new(nameof(ProcessLayer.Field), l => l.Field, (l, v) => l.Field = v),
        new(nameof(ProcessLayer.Description), l => l.Description, (l, v) => l.Description = v),
    };

    private static readonly IReadOnlyList<RowProperty<ProcessXsection>> XsectionProperties = new RowProperty<ProcessXsection>[]
    {
        new(nameof(ProcessXsection.Kind), x => x.Kind.ToString(), (x, v) => x.Kind = ParseKind(v)),
        new(nameof(ProcessXsection.WidthUm), x => Fmt(x.WidthUm), (x, v) => x.WidthUm = ParseDouble(v)),
        new(nameof(ProcessXsection.MinRadiusUm), x => Fmt(x.MinRadiusUm), (x, v) => x.MinRadiusUm = ParseDouble(v)),
        new(nameof(ProcessXsection.RecommendedRadiusUm), x => Fmt(x.RecommendedRadiusUm), (x, v) => x.RecommendedRadiusUm = ParseDouble(v)),
        new(nameof(ProcessXsection.Layers), x => string.Join(",", x.Layers),
            (x, v) => x.Layers = (v ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries).ToList()),
        new(nameof(ProcessXsection.Description), x => x.Description, (x, v) => x.Description = v),
    };

    private static readonly IReadOnlyList<RowProperty<ProcessMaterial>> MaterialProperties = new RowProperty<ProcessMaterial>[]
    {
        new(nameof(ProcessMaterial.Role), m => m.Role, (m, v) => m.Role = v),
    };

    /// <summary>Lists every property the edited process changes relative to the preset.</summary>
    public static List<ProcessPropertyOverrideData> Diff(ProcessDefinition preset, ProcessDefinition edited)
    {
        var result = new List<ProcessPropertyOverrideData>();
        DiffRows(ProcessPropertyOverrideData.LayersSection, preset.Layers, edited.Layers, l => l.Name, LayerProperties, result);
        DiffRows(ProcessPropertyOverrideData.XsectionsSection, preset.Xsections, edited.Xsections, x => x.Name, XsectionProperties, result);
        DiffRows(ProcessPropertyOverrideData.MaterialsSection, preset.Materials, edited.Materials, m => m.Name, MaterialProperties, result);
        return result;
    }

    /// <summary>Deep-clones a process definition (JSON round-trip).</summary>
    public static ProcessDefinition Clone(ProcessDefinition process) =>
        JsonSerializer.Deserialize<ProcessDefinition>(JsonSerializer.Serialize(process))!;

    /// <summary>Returns a copy of the preset with the overrides applied; the preset itself is untouched.</summary>
    public static ProcessDefinition Apply(ProcessDefinition preset, IEnumerable<ProcessPropertyOverrideData> overrides)
    {
        var clone = Clone(preset);
        foreach (var o in overrides)
        {
            switch (o.Section)
            {
                case ProcessPropertyOverrideData.LayersSection: ApplyRow(clone.Layers, o, l => l.Name, LayerProperties); break;
                case ProcessPropertyOverrideData.XsectionsSection: ApplyRow(clone.Xsections, o, x => x.Name, XsectionProperties); break;
                case ProcessPropertyOverrideData.MaterialsSection: ApplyRow(clone.Materials, o, m => m.Name, MaterialProperties); break;
            }
        }
        return clone;
    }

    private static void DiffRows<T>(string section, List<T> presetRows, List<T> editedRows,
        Func<T, string?> name, IReadOnlyList<RowProperty<T>> properties, List<ProcessPropertyOverrideData> result)
    {
        foreach (var edited in editedRows)
        {
            var rowName = name(edited);
            if (string.IsNullOrEmpty(rowName))
                continue;

            var preset = presetRows.FirstOrDefault(r => NameEquals(name(r), rowName));
            if (preset == null)
            {
                result.Add(Override(section, rowName, ProcessPropertyOverrideData.RowAdded, JsonSerializer.Serialize(edited)));
                continue;
            }

            foreach (var prop in properties)
                if (!string.Equals(prop.Get(preset) ?? string.Empty, prop.Get(edited) ?? string.Empty, StringComparison.Ordinal))
                    result.Add(Override(section, rowName, prop.Name, prop.Get(edited)));
        }

        foreach (var preset in presetRows)
        {
            var rowName = name(preset);
            if (!string.IsNullOrEmpty(rowName) && editedRows.All(r => !NameEquals(name(r), rowName)))
                result.Add(Override(section, rowName, ProcessPropertyOverrideData.RowRemoved, value: null));
        }
    }

    private static void ApplyRow<T>(List<T> rows, ProcessPropertyOverrideData o,
        Func<T, string?> name, IReadOnlyList<RowProperty<T>> properties) where T : class
    {
        if (o.Property == ProcessPropertyOverrideData.RowAdded)
        {
            var row = o.Value == null ? null : JsonSerializer.Deserialize<T>(o.Value);
            if (row != null)
                rows.Add(row);
            return;
        }
        if (o.Property == ProcessPropertyOverrideData.RowRemoved)
        {
            rows.RemoveAll(r => NameEquals(name(r), o.RowName));
            return;
        }

        var target = rows.FirstOrDefault(r => NameEquals(name(r), o.RowName));
        var prop = properties.FirstOrDefault(p => p.Name == o.Property);
        if (target != null && prop != null)
            prop.Set(target, o.Value);
    }

    private static ProcessPropertyOverrideData Override(string section, string rowName, string property, string? value) =>
        new() { Section = section, RowName = rowName, Property = property, Value = value };

    private static bool NameEquals(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Fmt(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Fmt(double value) => value.ToString(CultureInfo.InvariantCulture);

    private static int ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;

    private static double ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0;

    private static XsectionKind ParseKind(string? value) =>
        Enum.TryParse<XsectionKind>(value, ignoreCase: true, out var kind) ? kind : XsectionKind.Optical;
}
