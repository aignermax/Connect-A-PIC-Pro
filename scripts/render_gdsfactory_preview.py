"""Render a gdsfactory component to a geometry-preview JSON (issue #637).

Parallel to render_component_preview.py (Nazca), this executes user-supplied
gdsfactory code and emits the SAME JSON contract the C# preview parser expects:

    { "success": true,
      "bbox": {"xmin": -5.0, "ymin": -10.0, "xmax": 75.0, "ymax": 45.0},
      "polygons": [{"layer": 1, "vertices": [[x, y], ...]}],
      "pins": [{"name": "o1", "x": 0.0, "y": 27.5, "angle": 180.0}] }

or, on any failure:

    { "success": false, "error": "message" }

Usage: python render_gdsfactory_preview.py --code-file <file>
       python render_gdsfactory_preview.py <file>          (positional shorthand)
The user code must produce a gdsfactory Component: either assign it to a
variable named ``component`` (preferred) or ``c``, or leave a single
gf.Component in module scope. A ``gf.gpdk.PDK.activate()`` is done first so
plain layer tuples resolve without an explicit PDK.
"""
import json
import sys


def _emit(result):
    print(json.dumps(result))
    sys.exit(0)


def _find_component(namespace):
    """Locates the gf.Component the user's code produced."""
    import gdsfactory as gf
    for key in ("component", "c"):
        obj = namespace.get(key)
        if isinstance(obj, gf.Component):
            return obj
    candidates = [v for v in namespace.values() if isinstance(v, gf.Component)]
    if len(candidates) == 1:
        return candidates[0]
    raise ValueError(
        "No gdsfactory Component found. Assign your component to a variable "
        "named 'component' (e.g. `component = gf.components.straight()`).")


def _layer_index(layer):
    """Reduces a gdstk/gdsfactory layer key to the layer number for the preview."""
    if isinstance(layer, tuple):
        return int(layer[0])
    try:
        return int(layer)
    except (TypeError, ValueError):
        return 0


def _extract(component):
    import gdsfactory as gf  # noqa: F401  (ensures gdsfactory is importable)

    bb = component.dbbox()
    polygons = []
    # get_polygons_points(by='tuple') -> {(layer,dtype): [ndarray(N,2), ...]} in µm.
    by_layer = component.get_polygons_points(by="tuple")
    for layer, polys in by_layer.items():
        lidx = _layer_index(layer)
        for poly in polys:
            verts = [[float(x), float(y)] for x, y in poly]
            if verts:
                polygons.append({"layer": lidx, "vertices": verts})

    pins = []
    for p in component.ports:
        cx, cy = p.dcenter
        pins.append({
            "name": str(p.name),
            "x": float(cx),
            "y": float(cy),
            "angle": float(p.orientation),
        })

    return {
        "success": True,
        "bbox": {
            "xmin": float(bb.left),
            "ymin": float(bb.bottom),
            "xmax": float(bb.right),
            "ymax": float(bb.top),
        },
        "polygons": polygons,
        "pins": pins,
    }


def _code_file_arg():
    """Accepts '--code-file <path>' (matching the Nazca raw-code CLI) or a bare path."""
    args = sys.argv[1:]
    if "--code-file" in args:
        i = args.index("--code-file")
        if i + 1 < len(args):
            return args[i + 1]
        return None
    return args[0] if args else None


def main():
    path = _code_file_arg()
    if not path:
        _emit({"success": False, "error": "usage: render_gdsfactory_preview.py --code-file <file>"})
    try:
        with open(path, "r", encoding="utf-8") as f:
            code = f.read()

        import gdsfactory as gf
        try:
            gf.gpdk.PDK.activate()
        except Exception:
            pass  # a PDK the user's code activates itself takes precedence

        namespace = {"gf": gf}
        exec(compile(code, "<override>", "exec"), namespace)
        component = _find_component(namespace)
        _emit(_extract(component))
    except Exception as exc:  # noqa: BLE001 — any failure becomes a structured error
        _emit({"success": False, "error": str(exc)})


if __name__ == "__main__":
    main()
