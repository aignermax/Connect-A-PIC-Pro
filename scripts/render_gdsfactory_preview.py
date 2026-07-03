"""
render_gdsfactory_preview.py — Render a user-supplied gdsfactory snippet and
return bounding-box, polygon and port data as JSON (issue #637).

Companion of render_component_preview.py (Nazca): same output schema, same
--code-file contract, so the C# side (GdsFactoryComponentPreviewService /
NazcaPreviewResult parsing) is shared unchanged.

Usage:
    python3 render_gdsfactory_preview.py --code-file /path/to/snippet.py [--stub-length N]

The snippet must define a ``component()`` callable returning a gdsfactory
Component (``gf.Component``). As a fallback, a module-level ``cell`` variable
holding an already-built component is accepted.

Output (stdout): JSON
    { "success": true,
      "bbox": {"xmin": ..., "ymin": ..., "xmax": ..., "ymax": ...},
      "polygons": [{"layer": 1, "vertices": [[x, y], ...]}],
      "pins": [{"name": "o1", "x": 0.0, "y": 0.0, "angle": 180.0,
                "stubX1": -3.0, "stubY1": 0.0}] }

On failure:
    { "success": false, "error": "message" }
"""

import argparse
import contextlib
import json
import math
import os
import sys
import tempfile


def _parse_args():
    parser = argparse.ArgumentParser(description="Render gdsfactory component preview")
    parser.add_argument("--code-file", required=True,
                        help="Path to a .py file with raw gdsfactory code defining "
                             "component() (or a module-level 'cell' variable).")
    parser.add_argument("--stub-length", type=float, default=3.0,
                        help="Port stub length in µm (default: 3)")
    return parser.parse_args()


def _build_component_from_code_file(code_file):
    """Import the user's .py file and build its gdsfactory component.

    Mirrors the Nazca script's contract: ``component()`` callable preferred,
    module-level ``cell`` fallback. Errors propagate to the caller, which
    emits ``{"success": false, ...}``.
    """
    import importlib.util
    import gdsfactory  # noqa: F401 — fail early with a clear import error

    spec = importlib.util.spec_from_file_location("lunima_raw_gdsfactory_code", code_file)
    if spec is None or spec.loader is None:
        raise ValueError(f"Could not load code file: {code_file}")
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)

    component = getattr(mod, "component", None)
    if callable(component):
        return component()

    cell = getattr(mod, "cell", None)
    if cell is not None:
        return cell

    raise ValueError(
        "Raw gdsfactory code must define a 'component()' function returning a "
        "gf.Component, or a module-level 'cell' variable.")


def _extract_bbox(comp):
    """Return (xmin, ymin, xmax, ymax) in µm, across gdsfactory versions.

    gdsfactory ≥8 (kfactory-based) exposes ``dbbox()`` returning a DBox in
    µm; gdsfactory 9 additionally offers a µm-based ``bbox``. Older 7.x
    returned a numpy [[xmin, ymin], [xmax, ymax]] array from ``bbox``.
    Prefer dbbox() (unambiguously µm) and accept both shapes.
    """
    for name in ("dbbox", "bbox"):
        attr = getattr(comp, name, None)
        if attr is None:
            continue
        bb = attr() if callable(attr) else attr
        if hasattr(bb, "left"):
            return (float(bb.left), float(bb.bottom),
                    float(bb.right), float(bb.top))
        try:
            return (float(bb[0][0]), float(bb[0][1]),
                    float(bb[1][0]), float(bb[1][1]))
        except (TypeError, IndexError):
            continue
    raise ValueError("Could not determine the component's bounding box.")


def _port_center(port):
    """Return a port's (x, y) in µm across gdsfactory versions.

    gdsfactory 8 keeps µm coordinates on ``dcenter`` (``center`` is in
    database units there); gdsfactory 9 unified on a µm ``center``.
    Trying dcenter first therefore yields µm on both.
    """
    for name in ("dcenter", "center"):
        value = getattr(port, name, None)
        if value is None:
            continue
        try:
            return float(value[0]), float(value[1])
        except (TypeError, IndexError):
            pass
        if hasattr(value, "x"):
            return float(value.x), float(value.y)
    raise ValueError(f"Could not read the position of port '{port.name}'.")


def _extract_pins(comp, stub_length):
    """Return the component's optical/electrical ports as pin dicts with stubs.

    gdsfactory port orientation points OUTWARD (away from the component), the
    same convention the Nazca preview uses, so the stub endpoint is simply
    center + stub_length along the orientation.
    """
    pins = []
    for port in comp.ports:
        x, y = _port_center(port)
        angle = float(getattr(port, "orientation", 0.0) or 0.0)
        rad = math.radians(angle)
        pins.append({
            "name": str(port.name),
            "x": x,
            "y": y,
            "angle": angle,
            "stubX1": x + stub_length * math.cos(rad),
            "stubY1": y + stub_length * math.sin(rad),
        })
    return pins


def _extract_polygons(gds_path):
    """Extract polygons from a GDS file.

    Preference order: gdstk (fast, maintained) → gdspy → klayout.db. The
    klayout fallback matters because gdsfactory ≥8 always installs the
    klayout Python module, while gdstk/gdspy may be absent from the env.
    """
    try:
        import gdstk  # noqa: F401
        return _extract_polygons_gdstk(gds_path)
    except ImportError:
        pass

    try:
        import gdspy  # noqa: F401
        return _extract_polygons_gdspy(gds_path)
    except ImportError:
        pass

    try:
        import klayout.db  # noqa: F401
        return _extract_polygons_klayout(gds_path)
    except ImportError as exc:
        raise ImportError(
            "None of gdstk, gdspy or klayout is installed — cannot read GDS polygons.") from exc


def _extract_polygons_gdstk(gds_path):
    import gdstk
    lib = gdstk.read_gds(gds_path)
    polygons = []
    for cell in lib.cells:
        for poly in cell.polygons:
            polygons.append({
                "layer": int(poly.layer),
                "vertices": [[float(v[0]), float(v[1])] for v in poly.points],
            })
    return polygons


def _extract_polygons_gdspy(gds_path):
    import gdspy
    lib = gdspy.GdsLibrary(infile=gds_path)
    polygons = []
    for cell in lib.cells.values():
        for poly in cell.polygons:
            for i, verts in enumerate(poly.polygons):
                layer = poly.layers[i] if i < len(poly.layers) else 0
                polygons.append({
                    "layer": int(layer),
                    "vertices": [[float(v[0]), float(v[1])] for v in verts],
                })
    return polygons


def _extract_polygons_klayout(gds_path):
    """Read polygons via KLayout, flattening the hierarchy into µm vertices."""
    import klayout.db as kdb
    layout = kdb.Layout()
    layout.read(gds_path)
    tops = [c for c in layout.each_cell() if c.parent_cells == 0]
    cell = tops[0] if tops else next(layout.each_cell())
    dbu = layout.dbu
    polygons = []
    for li in layout.layer_indexes():
        info = layout.get_info(li)
        it = cell.begin_shapes_rec(li)
        while not it.at_end():
            shape = it.shape()
            if shape.is_polygon() or shape.is_box() or shape.is_path():
                poly = shape.polygon.transformed(it.trans())
                verts = [[float(p.x * dbu), float(p.y * dbu)]
                         for p in poly.each_point_hull()]
                if len(verts) >= 3:
                    polygons.append({"layer": int(info.layer), "vertices": verts})
            it.next()
    return polygons


def _render_to_gds(comp):
    """Export the component to a temp GDS file, return the path."""
    tmp = tempfile.mktemp(suffix=".gds")
    comp.write_gds(tmp)
    return tmp


def _do_render(args):
    comp = _build_component_from_code_file(args.code_file)
    xmin, ymin, xmax, ymax = _extract_bbox(comp)
    pins = _extract_pins(comp, args.stub_length)

    polygons = []
    polygon_warning = None
    gds_path = None
    try:
        gds_path = _render_to_gds(comp)
        polygons = _extract_polygons(gds_path)
    except ImportError:
        polygon_warning = (
            "Polygon overlay requires gdstk, gdspy or klayout — install one of "
            "them (e.g. `pip install gdstk`). Showing port stubs only for now.")
    except Exception as poly_err:  # noqa: BLE001 — polygons are best-effort
        polygon_warning = f"polygon extraction failed: {poly_err}"
    finally:
        if gds_path and os.path.exists(gds_path):
            os.remove(gds_path)

    result = {
        "success": True,
        "bbox": {
            "xmin": float(xmin),
            "ymin": float(ymin),
            "xmax": float(xmax),
            "ymax": float(ymax),
        },
        "polygons": polygons,
        "pins": pins,
    }
    if polygon_warning:
        result["polygon_warning"] = polygon_warning
    return result


def main():
    args = _parse_args()

    # gdsfactory logs chatter on stdout during import; redirect it to stderr
    # so it can't corrupt the JSON our caller expects on stdout.
    with contextlib.redirect_stdout(sys.stderr):
        try:
            result = _do_render(args)
        except Exception as exc:  # noqa: BLE001 — everything becomes a JSON error
            result = {"success": False, "error": str(exc)}

    print(json.dumps(result))
    sys.exit(0)  # non-exception exit so the C# parser reads our stdout


if __name__ == "__main__":
    main()
