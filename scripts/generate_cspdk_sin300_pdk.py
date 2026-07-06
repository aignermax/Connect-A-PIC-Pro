#!/usr/bin/env python3
"""Generate (and re-generate/update) the Lunima **CornerStone SiN-300** PDK JSON from
gdsfactory's open-source `cspdk` library — issue #570 follow-up / gdsfactory-backend PDKs.

This is the PDK's *updater*: the CornerStone process is a Python library (cspdk); when it
changes, re-run this to regenerate the Lunima PDK JSON. No hand-editing, no fabricated data.

    uv venv .cspdk --python 3.11
    uv pip install --python .cspdk cspdk
    .cspdk/Scripts/python scripts/generate_cspdk_sin300_pdk.py CAP-DataAccess/PDKs/cornerstone-sin-pdk.json

Emits a `backend: "gdsfactory"` PDK: each component carries its gdsfactory factory name
(`gdsFactoryFunction = "cspdk.sin300.<cell>"`) instead of a `nazcaFunction`, so it exports via
the gdsfactory path and is skipped by the nazca-export tests. Geometry + pins are real
(`c.dbbox()` / `c.ports`); no S-matrix is emitted (black-box light sim for v1). The process
fingerprint (Si3N4 / 300 nm / SiO2 / 1550 nm) makes it a distinct process from the 220 nm SOI
PDKs for the single-process rule (#570).
"""
import sys
import json

# gdsfactory generic utilities / frames / arrays — not placeable SiN circuit components.
SKIP_UTILITIES = {"array", "compass", "die", "die_nc", "die_no", "pad", "rectangle",
                  "grating_coupler_array"}


def _num(v):
    """kdb.DBox left/right/top/bottom are float properties; width()/height() are methods;
    a Port's dcenter is a DPoint. Coerce either form to a rounded float."""
    try:
        return round(float(v), 3)
    except TypeError:
        return round(float(v()), 3)


def _is_cross_section_variant(name):
    """cspdk emits _nc (nitride C-band) / _no (nitride O-band) cross-section variants of the
    same device. Keep only the base cell (default cross-section) so the PDK is one clean
    1550 nm SiN process rather than duplicated/O-band-split entries."""
    return name.endswith("_nc") or name.endswith("_no")


def _prettify(name):
    acronyms = {"mmi": "MMI", "mzi": "MZI"}
    words = [acronyms.get(w, w.capitalize()) for w in name.split("_")]
    return " ".join(words)


def _category(name):
    if name.startswith("grating_coupler"):
        return "Grating Couplers"
    if name.startswith("mmi1x2"):
        return "Splitters"
    if name.startswith(("mmi2x2", "coupler")):
        return "Couplers"
    if name.startswith("mzi"):
        return "Interferometers"
    if name.startswith(("straight", "bend", "taper", "wire")):
        return "Waveguides"
    return "General"


def build_component(pdk, name):
    c = pdk.get_component(name)
    bb = c.dbbox()
    left, bottom, right, top = _num(bb.left), _num(bb.bottom), _num(bb.right), _num(bb.top)
    pins = []
    for p in c.ports:
        dc = p.dcenter
        x = _num(getattr(dc, "x", dc[0] if hasattr(dc, "__getitem__") else 0))
        y = _num(getattr(dc, "y", dc[1] if hasattr(dc, "__getitem__") else 0))
        ori = getattr(p, "orientation", 0.0)
        pins.append({
            "name": p.name,
            "offsetXMicrometers": round(x - left, 3),
            "offsetYMicrometers": round(y - bottom, 3),
            "angleDegrees": _num(ori),
        })
    return {
        "name": _prettify(name),
        "category": _category(name),
        "gdsFactoryFunction": f"cspdk.sin300.{name}",
        "widthMicrometers": round(right - left, 3),
        "heightMicrometers": round(top - bottom, 3),
        "pins": pins,
    }


def main():
    import cspdk.sin300 as sin
    pdk = sin.PDK
    pdk.activate()

    cells = sorted(pdk.cells.keys())
    components, skipped, errored = [], [], []
    for name in cells:
        if name in SKIP_UTILITIES:
            skipped.append(name); continue
        if _is_cross_section_variant(name):
            skipped.append(name); continue
        try:
            comp = build_component(pdk, name)
            if len(comp["pins"]) == 0:
                skipped.append(name + " (no ports)"); continue
            components.append(comp)
        except Exception as e:  # noqa: BLE001
            errored.append(f"{name}: {e}")

    pdk_json = {
        "fileFormatVersion": 1,
        "name": "CornerStone SiN 300nm",
        "description": ("CornerStone open-source silicon-nitride (Si3N4, 300 nm) process, "
                        "generated from gdsfactory's cspdk.sin300 by "
                        "scripts/generate_cspdk_sin300_pdk.py. A distinct fabrication process "
                        "from the 220 nm SOI PDKs (single-process rule, #570). gdsfactory "
                        "backend — components export via gdsfactory, not Nazca; light sim is "
                        "black-box until circuit models are added."),
        "foundry": "CornerStone",
        "version": f"cspdk-{getattr(__import__('cspdk'), '__version__', '?')}",
        "defaultWavelengthNm": 1550,
        "backend": "gdsfactory",
        "process": {
            "name": "CornerStone SiN 300nm",
            "foundry": "CornerStone",
            "coreThicknessNm": 300,
            "materials": [
                {"name": "Si3N4", "role": "core"},
                {"name": "SiO2", "role": "cladding"},
            ],
        },
        "components": sorted(components, key=lambda c: (c["category"], c["name"])),
    }

    text = json.dumps(pdk_json, indent=2)
    if len(sys.argv) > 1:
        with open(sys.argv[1], "w", encoding="utf-8") as f:
            f.write(text + "\n")
        print(f"wrote {sys.argv[1]}: {len(components)} components")
    else:
        print(text)
    print("skipped:", ", ".join(skipped), file=sys.stderr)
    if errored:
        print("errored (skipped):", "; ".join(errored), file=sys.stderr)


if __name__ == "__main__":
    main()
