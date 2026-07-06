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
import cmath

# gdsfactory generic utilities / frames / arrays — not placeable SiN circuit components.
SKIP_UTILITIES = {"array", "compass", "die", "die_nc", "die_no", "pad", "rectangle",
                  "grating_coupler_array"}

# Wavelengths (nm) at which to sample the sax circuit models. C-band-centred on 1550.
WAVELENGTHS_NM = [1500, 1520, 1540, 1550, 1560, 1580, 1600]

# Components whose sax model + port mapping are unambiguous enough to emit a real S-matrix.
# mmi1x2/mmi2x2 build cleanly and their in*/out* ports map to the layout ports by orientation.
# Grating couplers (#665): their 2 layout ports map cleanly too — o1 (180°, waveguide) -> in0,
# o2 (0°, fibre) -> out0 — and the sax model returns a real wavelength-dependent coupling band
# (≈0.03 mag at 1500/1600 nm, ≈0.50 at the 1550 peak). `mzi` has no cspdk compact model (it is a
# composite that would need circuit simulation) and `coupler` fails to build in cspdk 1.4.2
# (bend_s allow_min_radius_violation) — both stay black-box/excluded until those are resolved.
SAX_MODEL_COMPONENTS = {"mmi1x2", "mmi2x2",
                        "grating_coupler_rectangular", "grating_coupler_elliptical"}

# Passive routing components that faithfully pass light (or current) straight through — a
# lossless pass-through S-matrix is the honest ideal. Their cspdk 1.4.2 sax models raise on
# the `loss` kwarg, so we synthesize the transfer rather than evaluate it. NOT gratings:
# a grating is a fibre coupler (out-of-plane, lossy, wavelength-dependent), so a pass-through
# would be a wrong model — it stays black-box until its real fibre model is wired.
PASS_THROUGH_COMPONENTS = {"straight", "taper", "bend_euler", "bend_s", "wire_corner"}


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
    comp = {
        "name": _prettify(name),
        "category": _category(name),
        "gdsFactoryFunction": f"cspdk.sin300.{name}",
        "widthMicrometers": round(right - left, 3),
        "heightMicrometers": round(top - bottom, 3),
        "pins": pins,
    }
    smatrix = build_smatrix(name, pins)
    if smatrix is not None:
        comp["sMatrix"] = smatrix
    return comp


def _classify_ports(pins):
    """Split layout pins into (inputs, outputs) by orientation and return the model-port map.

    gdsfactory/sax name west-facing ports in0,in1,… and east-facing ports out0,out1,…, in
    ascending transverse (y) order. A layout pin faces "in" when its angle points westish
    (90°<angle<270°). Returns (map, n_in, n_out) where map is {"in0": pinName, "out0": …}.
    """
    def norm(a):
        return a % 360
    inputs = sorted((p for p in pins if 90 < norm(p["angleDegrees"]) < 270),
                    key=lambda p: p["offsetYMicrometers"])
    outputs = sorted((p for p in pins if not (90 < norm(p["angleDegrees"]) < 270)),
                     key=lambda p: p["offsetYMicrometers"])
    port_map = {}
    for i, p in enumerate(inputs):
        port_map[f"in{i}"] = p["name"]
    for i, p in enumerate(outputs):
        port_map[f"out{i}"] = p["name"]
    return port_map, len(inputs), len(outputs)


def build_smatrix(name, pins):
    """Real S-matrix for the component, or None (black-box).

    - SAX_MODEL_COMPONENTS: evaluate the cspdk sax model at each WAVELENGTHS_NM and emit only
      forward (in→out) transfers mapped to layout pins — Lunima adds the reverse transfer per
      connection (PdkTemplateConverter.CreateSMatrixFromPdk), so we must not emit both.
    - 2-port passives (1 in, 1 out): a lossless pass-through (their cspdk models raise).
    - otherwise None (grating fibre ports / erroring components stay black-box).
    """
    import numpy as np
    port_map, n_in, n_out = _classify_ports(pins)

    if name in SAX_MODEL_COMPONENTS:
        try:
            import cspdk.sin300.models as models
            model = getattr(models, name)
            wl_data = []
            for wl_nm in WAVELENGTHS_NM:
                s = model(wl=wl_nm / 1000.0)   # sax wavelengths are in µm
                conns = []
                for (a, b), value in s.items():
                    if a not in port_map or b not in port_map:
                        continue
                    if not (a.startswith("in") and b.startswith("out")):
                        continue   # forward transfers only; skip reverse + reflections
                    v = complex(np.asarray(value).reshape(-1)[0])
                    if abs(v) < 1e-6:
                        continue
                    conns.append({
                        "fromPin": port_map[a],
                        "toPin": port_map[b],
                        "magnitude": round(abs(v), 6),
                        "phaseDegrees": round(cmath.phase(v) * 180.0 / cmath.pi, 3),
                    })
                if conns:
                    wl_data.append({"wavelengthNm": wl_nm, "connections": conns})
            if wl_data:
                return {"wavelengthNm": 1550, "wavelengthData": wl_data}
        except Exception:  # noqa: BLE001 — fall through to black-box on any model failure
            return None
        return None

    # Lossless pass-through for a known passive routing component (straight/taper/bend/wire).
    if name in PASS_THROUGH_COMPONENTS and n_in == 1 and n_out == 1:
        return {
            "wavelengthNm": 1550,
            "connections": [{
                "fromPin": port_map["in0"], "toPin": port_map["out0"],
                "magnitude": 1.0, "phaseDegrees": 0.0,
            }],
        }
    return None


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
