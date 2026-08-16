using System.Globalization;
using System.Text;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Connections;
using CAP_Core.Components.Core;
using CAP_Core.Components.PinKinds;
using CAP_Core.Routing.InterconnectRouting;

namespace CAP.Avalonia.Services;

/// <summary>
/// The per-process interconnect table of one Nazca export. The global
/// <c>ic = Interconnect(width=WG_WIDTH, …)</c> from the user preferences stays the
/// fallback for connections whose pins carry no process stamps; every optical
/// connection or frozen path whose endpoint pins DO carry PDK width/layer stamps gets
/// an interconnect of its own cross-section (<c>ic_p1</c>, <c>ic_p2</c>, …), so each
/// chiplet's routed waveguides land on their own process' width, bend radius and GDS
/// layer. Deterministic: names follow the sorted cross-section keys, never canvas order.
/// </summary>
internal sealed class NazcaProcessInterconnectPlan
{
    private const string LegacyName = "ic";

    private readonly record struct CrossSectionKey(double Width, double Radius, int? Layer);

    private readonly InterconnectSettings _settings;
    private readonly Dictionary<CrossSectionKey, string> _names = new();
    private readonly List<(CrossSectionKey Key, string Name)> _ordered = new();

    private NazcaProcessInterconnectPlan(InterconnectSettings settings) => _settings = settings;

    /// <summary>
    /// Collects the distinct cross-sections of all optical connections and pinned frozen
    /// paths on the canvas. Connections without process stamps and metal traces are not
    /// registered — they keep the legacy interconnect (or the metal emission).
    /// </summary>
    public static NazcaProcessInterconnectPlan Build(DesignCanvasViewModel canvas, InterconnectSettings settings)
    {
        var plan = new NazcaProcessInterconnectPlan(settings);
        foreach (var connVm in canvas.Connections)
        {
            var conn = connVm.Connection;
            if (conn.StartPin?.ParentComponent?.IsAnalysisTool == true) continue;
            if (conn.EndPin?.ParentComponent?.IsAnalysisTool == true) continue;
            if (IsMetal(conn.StartPin, conn.EndPin)) continue;
            plan.Register(
                ConnectionCrossSectionResolver.Resolve(conn.StartPin, conn.EndPin),
                conn.BendRadiusMicrometers);
        }

        foreach (var compVm in canvas.Components)
        {
            if (compVm.Component is ComponentGroup group)
                plan.RegisterGroup(group);
        }
        foreach (var pathVm in canvas.CanvasFrozenPaths)
            plan.RegisterFrozen(pathVm.Path);

        plan.AssignNames();
        return plan;
    }

    /// <summary>The interconnect variable a connection's pin-to-pin fallback routes through.</summary>
    public string InterconnectFor(WaveguideConnection connection) =>
        NameFor(connection.StartPin, connection.EndPin, connection.BendRadiusMicrometers);

    /// <summary>The interconnect variable a frozen path's pin-to-pin fallback routes through.</summary>
    public string InterconnectFor(FrozenWaveguidePath path) =>
        NameFor(path.StartPin, path.EndPin, path.BendRadiusMicrometers);

    /// <summary>Appends the per-process interconnect definitions after the legacy one.</summary>
    public void AppendTo(StringBuilder sb)
    {
        if (_ordered.Count == 0)
            return;

        sb.AppendLine("# Per-process interconnects: connections whose pins carry PDK stamps route");
        sb.AppendLine("# on their own process' cross-section (width/radius/layer), not the global one.");
        var ci = CultureInfo.InvariantCulture;
        foreach (var (key, name) in _ordered)
        {
            var width = key.Width.ToString("0.0###", ci);
            var radius = key.Radius.ToString("0.###", ci);
            var layer = key.Layer.HasValue
                ? $", layer={key.Layer.Value.ToString(ci)}"
                : string.Empty;
            sb.AppendLine($"{name} = Interconnect(width={width}, radius={radius}{layer})");
        }
        sb.AppendLine();
    }

    private void RegisterGroup(ComponentGroup group)
    {
        foreach (var frozenPath in group.InternalPaths)
            RegisterFrozen(frozenPath);
        foreach (var child in group.ChildComponents)
        {
            if (child is ComponentGroup nested)
                RegisterGroup(nested);
        }
    }

    private void RegisterFrozen(FrozenWaveguidePath? frozenPath)
    {
        if (frozenPath?.StartPin == null || frozenPath.EndPin == null)
            return;
        if (IsMetal(frozenPath.StartPin, frozenPath.EndPin))
            return;
        Register(
            ConnectionCrossSectionResolver.Resolve(frozenPath.StartPin, frozenPath.EndPin),
            frozenPath.BendRadiusMicrometers);
    }

    private void Register(ProcessCrossSection crossSection, double radiusMicrometers)
    {
        if (!crossSection.HasOpticalStamps)
            return;
        var key = KeyOf(crossSection, radiusMicrometers);
        if (!key.Equals(DefaultKey()))
            _names.TryAdd(key, string.Empty); // placeholder; names are assigned in AssignNames
    }

    private void AssignNames()
    {
        var index = 0;
        foreach (var key in _names.Keys
                     .OrderBy(k => k.Layer ?? -1)
                     .ThenBy(k => k.Width)
                     .ThenBy(k => k.Radius)
                     .ToList())
        {
            var name = $"ic_p{++index}";
            _names[key] = name;
            _ordered.Add((key, name));
        }
    }

    private string NameFor(PhysicalPin? startPin, PhysicalPin? endPin, double radiusMicrometers)
    {
        var crossSection = ConnectionCrossSectionResolver.Resolve(startPin, endPin);
        if (!crossSection.HasOpticalStamps)
            return LegacyName;
        var key = KeyOf(crossSection, radiusMicrometers);
        return _names.TryGetValue(key, out var name) && name.Length > 0 ? name : LegacyName;
    }

    private CrossSectionKey KeyOf(ProcessCrossSection crossSection, double radiusMicrometers) =>
        new(
            crossSection.WidthMicrometers ?? _settings.WidthMicrometers,
            radiusMicrometers,
            crossSection.GdsLayer ?? _settings.GdsLayer);

    private CrossSectionKey DefaultKey() =>
        new(_settings.WidthMicrometers, _settings.BendRadiusMicrometers, _settings.GdsLayer);

    private static bool IsMetal(PhysicalPin? first, PhysicalPin? second) =>
        PinKindHelper.IsElectrical(first) && PinKindHelper.IsElectrical(second);
}
