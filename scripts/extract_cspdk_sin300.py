#!/usr/bin/env python3
"""Extract real CornerStone SiN-300 component geometry + ports from gdsfactory's cspdk,
for building a Lunima gdsfactory-backend PDK JSON (issue #570 follow-up / gdsfactory PDKs).

Run in an env with cspdk installed:
    uv venv .cspdk --python 3.11
    uv pip install --python .cspdk cspdk
    .cspdk/Scripts/python extract_cspdk_sin300.py out.json

Emits, per curated component: bbox (um), width/height (um), and ports (name, x, y in um,
orientation in degrees). Lunima pin mapping (component box origin at bbox bottom-left,
y up): offsetXMicrometers = port.x - bbox.left ; offsetYMicrometers = port.y - bbox.bottom ;
angleDegrees = port.orientation.  gdsFactoryFunction = "cspdk.sin300.<name>".

Process fingerprint for these components (Lunima single-process, #570):
  coreMaterial Si3N4, coreThicknessNm 300, cladding SiO2, defaultWavelengthNm 1550.
"""
import sys
import json

# Curated placeable set (skip die/array/pad/rectangle/compass and the _nc/_no
# cross-section variants; 'coupler' is skipped — its cspdk default args raise
# bend_s(allow_min_radius_violation=...) in cspdk 1.4.2).
CURATED = ["straight", "taper", "bend_euler", "mmi1x2", "mmi2x2",
           "grating_coupler_rectangular"]


def _num(v):
    """kdb.DBox exposes left/right/top/bottom as float properties but width()/height()
    as methods; ports' dcenter is a DPoint. Coerce either form to a rounded float."""
    try:
        return round(float(v), 3)
    except TypeError:
        return round(float(v()), 3)


def extract(pdk):
    out = {}
    for name in CURATED:
        try:
            c = pdk.get_component(name)
            bb = c.dbbox()
            left, bottom, right, top = _num(bb.left), _num(bb.bottom), _num(bb.right), _num(bb.top)
            entry = {
                "bbox": [left, bottom, right, top],
                "width_um": round(right - left, 3),
                "height_um": round(top - bottom, 3),
                "ports": [],
            }
            for p in c.ports:
                dc = p.dcenter
                x = _num(getattr(dc, "x", dc[0] if hasattr(dc, "__getitem__") else 0))
                y = _num(getattr(dc, "y", dc[1] if hasattr(dc, "__getitem__") else 0))
                ori = getattr(p, "orientation", None)
                entry["ports"].append({
                    "name": p.name,
                    "x": x, "y": y,
                    "orientation": _num(ori) if ori is not None else None,
                })
            out[name] = entry
        except Exception as e:  # noqa: BLE001 - report per-component, keep going
            out[name] = {"error": str(e)}
    return out


def main():
    import cspdk.sin300 as sin
    pdk = sin.PDK
    pdk.activate()
    result = extract(pdk)
    text = json.dumps(result, indent=1)
    print(text)
    if len(sys.argv) > 1:
        with open(sys.argv[1], "w", encoding="utf-8") as f:
            f.write(text)


if __name__ == "__main__":
    main()
