#!/usr/bin/env python3
"""Generate (and re-generate/update) the Lunima **CornerStone SiN-300** PDK JSON from
gdsfactory's open-source `cspdk` library — issue #570 follow-up / gdsfactory-backend PDKs.

This is the PDK's *updater*: the CornerStone process is a Python library (cspdk); when it
changes, re-run this to regenerate the Lunima PDK JSON. No hand-editing, no fabricated data.

Environment (cspdk >= 1.4.3 — 1.4.2's `coupler` cell raises on a `bend_s` kwarg; sax ~0.17
matches cspdk's model kwargs; installed --no-deps to avoid the unused gplugins/gdstk chain):

    uv venv .cspdk --python 3.12
    uv pip install --python .cspdk "gdsfactory==9.43.0" "sax~=0.17.0"
    uv pip install --python .cspdk --no-deps "cspdk==1.4.3"
    .cspdk/Scripts/python scripts/generate_cspdk_sin300_pdk.py CAP-DataAccess/PDKs/cornerstone-sin-pdk.json

Emits a `backend: "gdsfactory"` PDK: each component carries its gdsfactory factory name
(`gdsFactoryFunction = "cspdk.sin300.<cell>"`) instead of a `nazcaFunction`, so it exports via
the gdsfactory path and is skipped by the nazca-export tests. Geometry + pins are real
(`c.dbbox()` / `c.ports`); S-matrices come from cspdk's own `sax` compact models (#665). The
process fingerprint (Si3N4 / 300 nm / SiO2 / 1550 nm) makes it a distinct process from the
220 nm SOI PDKs for the single-process rule (#570).
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
# With sax's "optical" port-naming strategy the model port names (o1, o2, …) are *identical*
# to the layout port names, so the mapping is verified name identity (checked in
# _eval_model_smatrix; any mismatch → black-box). The grating-coupler fibre-port convention:
# the sax model's o1 is the in-plane waveguide port, o2 the out-of-plane fibre port — the
# layout exposes exactly those two ports under the same names (#665).
SAX_MODEL_COMPONENTS = {"mmi1x2", "mmi2x2", "coupler",
                        "grating_coupler_rectangular", "grating_coupler_elliptical"}

# Multi-component cells with no direct sax model: composed with sax.circuit from the cell's
# own recursive netlist + cspdk's models, so arm lengths (mzi delta_length) enter via the
# real layout netlist. (cspdk.sin300's mzi has no heater — passive arms only.)
CIRCUIT_MODEL_COMPONENTS = {"mzi"}

# Passive routing components that faithfully pass light (or current) straight through — a
# lossless pass-through S-matrix is the honest ideal. Their cspdk sax model wrappers raise
# on the `loss` kwarg (see _mzi_circuit), so we synthesize the transfer rather than evaluate
# it. NOT gratings: a grating is a fibre coupler (out-of-plane, lossy, wavelength-dependent),
# so a pass-through would be a wrong model — it gets its real sax model above (#665).
PASS_THROUGH_COMPONENTS = {"straight", "taper", "bend_euler", "bend_s", "wire_corner"}

# Curated subset of cspdk.sin300's LayerMapCornerstone (issue #570 follow-up): the physical
# fabrication layers relevant to a placed/routed design. Excludes label/error-marker layers
# (LABEL_SETTINGS/LABEL_INSTANCE duplicate LBL; routing_error_marker is a diagnostic, not a
# fabrication layer). Numbers are read from LAYER at generation time — never hand-typed here.
PROCESS_LAYER_NAMES = ["NITRIDE", "NITRIDE_ETCH", "HEATER", "PAD", "CLAD_OPEN", "FLOORPLAN", "LBL"]
LAYER_DESCRIPTIONS = {
    "NITRIDE": "Waveguide core (Si3N4)",
    "NITRIDE_ETCH": "Nitride etch region",
    "HEATER": "Heater metal (TiN)",
    "PAD": "Bond-pad / routing metal (Aluminum)",
    "CLAD_OPEN": "Cladding opening",
    "FLOORPLAN": "Chip floorplan / die outline",
    "LBL": "Label",
}

# Routing cross-sections cspdk.sin300 actually defines (xs_nc/xs_no optical, metal_routing/
# heater_metal electrical — see cspdk.sin300.tech). kind 0 = Optical, 1 = Metal (ProcessDefinition.
# XsectionKind's default int serialization — no JsonStringEnumConverter is registered).
XSECTION_SPECS = [
    ("xs_nc", 0, "NITRIDE", "SiN C-band strip waveguide"),
    ("xs_no", 0, "NITRIDE", "SiN O-band strip waveguide"),
    ("metal_routing", 1, "PAD", "Electrical routing metal"),
    ("heater_metal", 1, "HEATER", "Heater trace metal"),
]


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
        # Optical waveguide ports and vertical fibre ports become Lunima pins; electrical
        # ports (e.g. wire_corner's metal ends) do not — this is an optical PDK. A grating
        # coupler's vertical_te/vertical_tm port sits in the MIDDLE of the grating on
        # purpose: that is where the fibre couples from above (same convention as
        # SiEPIC/KLayout fibre pins), and the #665 coupling-band S-matrix (o1 -> o2)
        # needs it as its second terminal.
        if getattr(p, "port_type", "optical") not in ("optical", "vertical_te", "vertical_tm"):
            continue
        dc = p.dcenter
        x = _num(getattr(dc, "x", dc[0] if hasattr(dc, "__getitem__") else 0))
        y = _num(getattr(dc, "y", dc[1] if hasattr(dc, "__getitem__") else 0))
        ori = getattr(p, "orientation", 0.0)
        pins.append({
            "name": p.name,
            "offsetXMicrometers": round(x - left, 3),
            # Lunima pin offsets are measured from the bounding box TOP-left, y-down
            # (see docs/PDK_JSON_FORMAT.md). bottom-up ("y - bottom") only coincides for
            # y-symmetric cells and mirrors e.g. the euler bend's pins.
            "offsetYMicrometers": round(top - y, 3),
            # gdsfactory orientation is y-up (math convention); Lunima's editor is y-down, so
            # a vertical port flips: gf 90° (up) -> 270°, gf 270° (down) -> 90° (0/180 unchanged).
            # Matches the bundled demofab 90° bend (b0 = 270°); an unflipped 90° would make the
            # euler bend's exit port face into the cell and break connection snapping/routing.
            "angleDegrees": round((360.0 - _num(ori)) % 360.0, 3),
        })
    comp = {
        "name": _prettify(name),
        "category": _category(name),
        "gdsFactoryFunction": f"cspdk.sin300.{name}",
        "widthMicrometers": round(right - left, 3),
        "heightMicrometers": round(top - bottom, 3),
        # Where the gdsfactory cell origin (0,0) sits relative to the bbox, in the same
        # convention as Nazca PDKs (#640): ox = -XMin, oy = YMax. cspdk cells are
        # port-anchored (origin at o1, geometry y-centered), so the origin is NOT the
        # bbox corner — the exporter needs this to place the real cell where Lunima drew
        # it (without it the mapper falls back to a bottom-left anchor and every cell
        # lands ~height/2 off its routed waveguides).
        "nazcaOriginOffsetX": round(-left, 3),
        "nazcaOriginOffsetY": round(top, 3),
        "pins": pins,
    }
    smatrix = build_smatrix(pdk, name, pins)
    if smatrix is not None:
        comp["sMatrix"] = smatrix
    return comp


def _west_east(pins):
    """Split layout pin names into (west-facing, east/other) sets by orientation.

    Forward transfers are west→east: Lunima adds the reverse transfer per connection
    (PdkTemplateConverter.CreateSMatrixFromPdk), so we must only emit one direction.
    """
    west = {p["name"] for p in pins if 90 < p["angleDegrees"] % 360 < 270}
    east = {p["name"] for p in pins} - west
    return west, east


def _sax_models():
    """cspdk's sax models with the "optical" port-naming strategy, so model port names are
    the layout port names (o1, o2, …) — the strategy applies when a model is *called*."""
    import sax
    sax.set_port_naming_strategy("optical")
    import cspdk.sin300.models as models
    return sax, models


def _mzi_circuit(pdk, sax, models):
    """Compose the mzi S-model from its own recursive netlist + cspdk's sub-cell models.

    cspdk's straight/bend model wrappers pass `loss=` to sax models that spell the kwarg
    `loss_dB_cm` (upstream naming bug), so we call sax.models.straight directly with cspdk's
    own SiN parameters (straight_nc partial keywords: neff/ng/wl0/loss) — a kwarg rename,
    not new physics. Netlist instance settings supply the real arm lengths.
    """
    import inspect
    import sax.models as sm
    nc = dict(models.straight_nc.keywords)
    bend_loss = inspect.signature(models.bend_euler).parameters["loss"].default

    def straight(wl=1.55, length=10.0, loss=nc["loss"], **_):
        return sm.straight(wl=wl, length=length, wl0=nc["wl0"],
                           neff=nc["neff"], ng=nc["ng"], loss_dB_cm=loss)

    def bend_euler(wl=1.55, length=10.0, loss=bend_loss, **_):
        return straight(wl=wl, length=length, loss=loss)

    netlist = pdk.get_component("mzi").get_netlist(recursive=True)
    circuit, _info = sax.circuit(netlist, models={
        "straight": straight, "bend_euler": bend_euler,
        "mmi1x2": models.mmi1x2_nc, "mmi2x2": models.mmi2x2_nc,
    })
    return circuit


def _eval_model_smatrix(model, pins):
    """Sample `model` at WAVELENGTHS_NM; keep forward (west→east) transfers by name identity.

    Guardrail (#665): every model port must exist as a layout pin, else the mapping is not
    verified and the component stays black-box (a wrong model is worse than none).
    """
    import numpy as np
    west, east = _west_east(pins)
    pin_names = west | east

    # Evaluate ONCE over the whole wavelength array: sax's mmi models normalise their
    # spectral response by its max over the passed wl array, so scalar-per-wavelength calls
    # would flatten the band to the peak value. The grid contains wl0 = 1550 nm, so the
    # normalisation anchor is the true peak.
    s = model(wl=np.array([w / 1000.0 for w in WAVELENGTHS_NM]))   # sax wl in µm
    model_ports = {p for k in s for p in k}
    if not model_ports <= pin_names:
        print(f"model ports {sorted(model_ports - pin_names)} missing from layout — "
              "black-box", file=sys.stderr)
        return None

    wl_data = []
    for i, wl_nm in enumerate(WAVELENGTHS_NM):
        conns = []
        for (a, b), value in s.items():
            if a not in west or b not in east:
                continue   # forward transfers only; skip reverse + reflections
            arr = np.asarray(value).reshape(-1)
            v = complex(arr[i] if arr.size > 1 else arr[0])
            if abs(v) < 1e-6:
                continue
            conns.append({
                "fromPin": a,
                "toPin": b,
                "magnitude": round(abs(v), 6),
                "phaseDegrees": round(cmath.phase(v) * 180.0 / cmath.pi, 3),
            })
        if conns:
            wl_data.append({"wavelengthNm": wl_nm, "connections": conns})
    if not wl_data:
        return None
    return {"wavelengthNm": 1550, "wavelengthData": wl_data}


def build_smatrix(pdk, name, pins):
    """Real S-matrix for the component, or None (black-box).

    - SAX_MODEL_COMPONENTS: the cspdk sax model, sampled at WAVELENGTHS_NM.
    - CIRCUIT_MODEL_COMPONENTS: a sax.circuit composition of the cell's netlist.
    - 2-port passives (1 in, 1 out): a lossless pass-through (their cspdk models raise).
    - otherwise None (unverified port mapping / erroring components stay black-box).
    """
    if name in SAX_MODEL_COMPONENTS or name in CIRCUIT_MODEL_COMPONENTS:
        try:
            sax, models = _sax_models()
            model = (_mzi_circuit(pdk, sax, models) if name in CIRCUIT_MODEL_COMPONENTS
                     else getattr(models, name))
            return _eval_model_smatrix(model, pins)
        except Exception as e:  # noqa: BLE001 — black-box on any model failure
            print(f"{name}: sax model failed ({e}) — black-box", file=sys.stderr)
            return None

    # Lossless pass-through for a known passive routing component (straight/taper/bend/wire).
    west, east = _west_east(pins)
    if name in PASS_THROUGH_COMPONENTS and len(west) == 1 and len(east) == 1:
        return {
            "wavelengthNm": 1550,
            "connections": [{
                "fromPin": next(iter(west)), "toPin": next(iter(east)),
                "magnitude": 1.0, "phaseDegrees": 0.0,
            }],
        }
    return None


def build_layers(sin):
    """Real GDS layer/datatype numbers read from cspdk.sin300's LayerMapCornerstone — never
    hand-typed, so a cspdk layer renumbering is caught by re-running this generator."""
    layer = sin.LAYER
    return [
        {
            "name": name,
            "layer": int(getattr(layer, name)[0]),
            "datatype": int(getattr(layer, name)[1]),
            "description": LAYER_DESCRIPTIONS[name],
        }
        for name in PROCESS_LAYER_NAMES
    ]


def build_xsections():
    """Real cross-section width/bend-radius numbers from cspdk.sin300's activated PDK
    (gf.get_cross_section), not invented — see XSECTION_SPECS."""
    import gdsfactory as gf
    xsections = []
    for name, kind, layer_name, description in XSECTION_SPECS:
        xs = gf.get_cross_section(name)
        xsections.append({
            "name": name,
            "kind": kind,
            "widthUm": round(float(xs.width), 3),
            "minRadiusUm": round(float(xs.radius_min or 0), 3),
            "recommendedRadiusUm": round(float(xs.radius or 0), 3),
            "layers": [layer_name],
            "description": description,
        })
    return xsections


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
                        "backend — components export via gdsfactory, not Nazca; S-matrices "
                        "are sampled from cspdk's own sax compact models (#665)."),
        "foundry": "CornerStone",
        "version": f"cspdk-{getattr(__import__('cspdk'), '__version__', '?')}",
        "defaultWavelengthNm": 1550,
        "backend": "gdsfactory",
        # Routing cross-section for waveguides between components. cspdk's route_single_nc uses
        # xs_nc (C-band, 1.2 µm); the O-band variant would be xs_no. The generic gdsfactory
        # "strip" default does not exist under this nitride PDK, so the export must name it (#570).
        "gdsFactoryRoutingCrossSection": "xs_nc",
        "process": {
            "name": "CornerStone SiN 300nm",
            "foundry": "CornerStone",
            "coreThicknessNm": 300,
            "layers": build_layers(sin),
            "xsections": build_xsections(),
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
