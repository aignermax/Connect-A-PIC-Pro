using System.Text;
using CAP.Avalonia.ViewModels.Canvas;
using CAP_Core.Components.Core;
using CAP_Core.Export;

namespace CAP.Avalonia.Services;

/// <summary>
/// Emits the klayout post-pass that upgrades SiEPIC stub boxes to real foundry
/// geometry in the exported GDS. The nazca script places stub cells (polygon
/// boxes) so routing and positions work without any PDK install; after
/// <c>nd.export_gds()</c> this block reopens the GDS, resolves each SiEPIC
/// function in the EBeam* KLayout libraries — static library cell first, then
/// PCell with the component's parameters, the same resolution
/// <c>scripts/render_component_preview.py</c> uses for the editor preview — and
/// deep-copies the real geometry into the stub cell, keeping the cell name so
/// placed instances stay put. Any failure (klayout or PDK missing, cell unknown)
/// downgrades to a stderr warning and keeps the stub — the export never breaks.
/// Parameterized components get a parameters hash in their stub cell name
/// (<see cref="NazcaStubNaming"/>, issue #783), so the map keys each variant's
/// cell to ITS OWN function name + parameters; a residual stderr warning covers
/// only a hash collision (two parameter sets, same stub name).
/// </summary>
public static class SiepicCellUpgradeWriter
{
    /// <summary>
    /// Appends the upgrade block when the canvas holds SiEPIC PDK components
    /// (module name starting with "siepic", mirroring the preview's routing
    /// predicate); otherwise emits nothing.
    /// </summary>
    /// <param name="sb">The script under construction; the block runs after the
    /// footer's <c>nd.export_gds()</c>, which defines <c>gds_filename</c>.</param>
    /// <param name="canvas">The design canvas.</param>
    /// <param name="include">Optional group filter of a partial (mixed-backend) export.</param>
    public static void AppendUpgradeBlock(
        StringBuilder sb, DesignCanvasViewModel canvas, Func<Component, bool>? include = null)
    {
        var cells = CollectSiepicStubCells(canvas, include);
        if (cells.Count == 0)
            return;

        // stub cell name → (real PDK function name, parameters): the GDS cell to
        // patch is the hash-suffixed stub (#783), the library lookup needs the
        // original function name with THIS variant's parameters.
        var mapLiteral = string.Join(", ", cells.Select(
            kv => $"'{Escape(kv.Key)}': ('{Escape(kv.Value.FuncName)}', '{Escape(kv.Value.Params)}')"));

        sb.AppendLine();
        sb.AppendLine("# --- Lunima: upgrade SiEPIC stub boxes to real foundry geometry (klayout) ---");
        sb.AppendLine(PythonBlock);
        sb.AppendLine($"_lunima_upgrade_siepic_cells(gds_filename, {{{mapLiteral}}})");
        foreach (var collision in FindParamCollisions(canvas, include))
            sb.AppendLine(
                $"print(\"[Lunima] WARN: SiEPIC stub cell '{Escape(collision)}' maps to multiple parameter sets " +
                "(parameters-hash collision) — one shared cell is used for all instances; check geometry against the PDK.\", file=sys.stderr)");
        sb.AppendLine();
    }

    /// <summary>
    /// Stub cell names that more than one parameter set hashes to. Distinct parameter
    /// sets get distinct cells via the name hash (#783), so this fires only on an
    /// actual parameters-hash collision — flag it in the script output instead of
    /// silently rendering one variant for all.
    /// </summary>
    private static IEnumerable<string> FindParamCollisions(
        DesignCanvasViewModel canvas, Func<Component, bool>? include)
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var compVm in canvas.Components)
        {
            var comp = compVm.Component;
            if (comp.IsAnalysisTool) continue;
            if (comp is ComponentGroup group)
            {
                foreach (var child in group.GetAllComponentsRecursive())
                {
                    var hit = TrackParam(seen, child, include);
                    if (hit != null) yield return hit;
                }
            }
            else
            {
                var hit = TrackParam(seen, comp, include);
                if (hit != null) yield return hit;
            }
        }
    }

    private static string? TrackParam(
        Dictionary<string, string> seen, Component comp, Func<Component, bool>? include)
    {
        if (comp.IsAnalysisTool) return null;
        if (include != null && !include(comp)) return null;
        var funcName = comp.NazcaFunctionName;
        if (string.IsNullOrEmpty(funcName)) return null;
        if (!NazcaCoordinateMapper.IsPdkFunction(funcName)) return null;
        if (comp.NazcaModuleName?.StartsWith("siepic", StringComparison.OrdinalIgnoreCase) != true) return null;
        var parameters = comp.NazcaFunctionParameters ?? string.Empty;
        // Key on the stub cell name — the same key the stub generator dedupes by,
        // so only a genuine parameters-hash collision reports here.
        var stubName = NazcaStubNaming.StubName(funcName, parameters);
        if (seen.TryGetValue(stubName, out var existing))
            return existing != parameters ? stubName : null;
        seen[stubName] = parameters;
        return null;
    }

    /// <summary>
    /// Unique SiEPIC stub cells of the design: stub cell name (parameters-hash
    /// suffixed, <see cref="NazcaStubNaming"/>) → (real function name, parameter
    /// string). Same enumeration as the stub generator (groups flattened, analysis
    /// tools skipped, <paramref name="include"/> honoured). Parametric straights are
    /// excluded — their stub cell name embeds the instance length, so a
    /// per-function content swap cannot target them.
    /// </summary>
    private static IReadOnlyDictionary<string, (string FuncName, string Params)> CollectSiepicStubCells(
        DesignCanvasViewModel canvas, Func<Component, bool>? include)
    {
        var cells = new Dictionary<string, (string FuncName, string Params)>(StringComparer.Ordinal);
        foreach (var compVm in canvas.Components)
        {
            var comp = compVm.Component;
            if (comp.IsAnalysisTool) continue;
            if (comp is ComponentGroup group)
            {
                foreach (var child in group.GetAllComponentsRecursive())
                    AddIfSiepic(cells, child, include);
            }
            else
            {
                AddIfSiepic(cells, comp, include);
            }
        }
        return cells;
    }

    private static void AddIfSiepic(
        IDictionary<string, (string FuncName, string Params)> cells, Component comp, Func<Component, bool>? include)
    {
        if (comp.IsAnalysisTool) return;
        if (include != null && !include(comp)) return;
        var funcName = comp.NazcaFunctionName;
        if (string.IsNullOrEmpty(funcName)) return;
        if (!NazcaCoordinateMapper.IsPdkFunction(funcName)) return;
        if (NazcaCoordinateMapper.IsParametricStraight(funcName, comp.NazcaFunctionParameters)) return;
        // Cheap routing predicate — anything starting with 'siepic' resolves through
        // the EBeam* KLayout libraries (same split as the editor preview).
        if (comp.NazcaModuleName?.StartsWith("siepic", StringComparison.OrdinalIgnoreCase) != true) return;
        // The GDS cell to patch is the hash-suffixed stub (#783); the EBeam library
        // lookup still needs the original function name with this variant's parameters.
        var parameters = comp.NazcaFunctionParameters ?? string.Empty;
        cells.TryAdd(NazcaStubNaming.StubName(funcName, parameters), (funcName, parameters));
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("'", "\\'");

    private const string PythonBlock = """
def _lunima_upgrade_siepic_cells(gds_path, cells):
    import sys as _sys
    try:
        import klayout.db as _kdb
        import siepic_ebeam_pdk as _siepic  # noqa: F401 — registers the EBeam* KLayout libraries

        def _libs():
            for _lid in _kdb.Library.library_ids():
                _lib = _kdb.Library.library_by_id(_lid)
                if _lib is not None and _lib.name().startswith('EBeam'):
                    yield _lib

        def _parse_kwargs(_s):
            _kw = {}
            for _part in (_s or '').split(','):
                if '=' in _part:
                    _k, _v = _part.split('=', 1)
                    try:
                        _kw[_k.strip()] = float(_v)
                    except ValueError:
                        _v = _v.strip()
                        # PDK strings quote text values (pol='TE') — the PCell wants the bare text.
                        if len(_v) >= 2 and _v[0] == _v[-1] and _v[0] in "'\"":
                            _v = _v[1:-1]
                        _kw[_k.strip()] = _v
            return _kw

        def _resolve(_func_name, _params):
            # Returns (layout, cell) — the layout reference MUST travel with the cell:
            # a PCell variant's layout is owned by _resolve's local scope, and once it
            # is garbage-collected the cell dangles ("Object has been destroyed").
            _libraries = list(_libs())
            for _lib in _libraries:  # 1. static cell baked into a library layout
                for _c in _lib.layout().each_cell():
                    if _c.name == _func_name:
                        return _lib.layout(), _c
            for _lib in _libraries:  # 2. PCell with the exact name
                _lay = _lib.layout()
                if _func_name not in _lay.pcell_names():
                    continue
                _pid = _lay.pcell_id(_func_name)
                _decl = _lay.pcell_declaration(_pid)
                _uk = _parse_kwargs(_params)
                _vals = [_uk.get(_p.name, _p.default) for _p in _decl.get_parameters()]
                _ly = _kdb.Layout()
                _ly.dbu = 0.001  # 1 nm — matches what siepic_ebeam_pdk targets
                _vid = _ly.add_pcell_variant(_lib, _pid, _vals)
                return _ly, _ly.cell(_vid)
            raise FileNotFoundError(_func_name)

        _out = _kdb.Layout()
        _out.read(gds_path)
        _upgraded = 0
        for _stub_name, (_func_name, _params) in cells.items():
            _stub = _out.cell(_stub_name)
            if _stub is None:
                continue
            try:
                _src_layout, _real = _resolve(_func_name, _params)
            except Exception as _exc:
                print(f"[Lunima] WARN: real SiEPIC cell '{_func_name}' unavailable ({_exc}); keeping stub box.", file=_sys.stderr)
                continue
            # Swap content, keep the cell name so placed instances stay put.
            for _li in list(_out.layer_indexes()):
                _stub.shapes(_li).clear()
            _stub.copy_tree(_real)
            _upgraded += 1
        if _upgraded:
            import os as _os
            _tmp = _os.path.splitext(gds_path)[0] + '.tmp.gds'  # .gds suffix — klayout sniffs the format from the extension
            _out.write(_tmp)
            _os.replace(_tmp, gds_path)  # atomic — a failed write never truncates the export
            print(f"[Lunima] {_upgraded} SiEPIC cell(s) upgraded to real foundry geometry.")
    except Exception as _exc:
        print(f"[Lunima] WARN: SiEPIC real-geometry upgrade skipped ({_exc}); stub boxes kept.", file=_sys.stderr)

""";
}
