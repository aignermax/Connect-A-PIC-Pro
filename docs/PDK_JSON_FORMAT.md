# PDK JSON Format Guide

This guide explains how to create a PDK (Process Design Kit) JSON file for Lunima, and how to convert Python-based (Nazca) PDKs using AI assistance.

---

## Quick Start

A PDK JSON file describes a set of photonic components — their physical dimensions, pin positions, and optical S-matrix responses. Lunima reads these files to populate the component library.

---

## Full PDK JSON Structure

```json
{
  "fileFormatVersion": 1,
  "name": "My Foundry PDK",
  "description": "Description of the PDK",
  "foundry": "My Foundry",
  "version": "1.0.0",
  "defaultWavelengthNm": 1550,
  "nazcaModuleName": "my_pdk",
  "components": [
    {
      "name": "1x2 MMI Splitter",
      "category": "Splitters",
      "nazcaFunction": "my_pdk.mmi1x2",
      "nazcaParameters": "length=100",
      "widthMicrometers": 80,
      "heightMicrometers": 55,
      "nazcaOriginOffsetX": 0,
      "nazcaOriginOffsetY": 27.5,
      "pins": [
        { "name": "in",   "offsetXMicrometers": 0,  "offsetYMicrometers": 27.5, "angleDegrees": 180 },
        { "name": "out1", "offsetXMicrometers": 80, "offsetYMicrometers": 25.5, "angleDegrees": 0   },
        { "name": "out2", "offsetXMicrometers": 80, "offsetYMicrometers": 29.5, "angleDegrees": 0   }
      ],
      "sMatrix": {
        "wavelengthNm": 1550,
        "connections": [
          { "fromPin": "in", "toPin": "out1", "magnitude": 0.707, "phaseDegrees": 0 },
          { "fromPin": "in", "toPin": "out2", "magnitude": 0.707, "phaseDegrees": 0 }
        ]
      }
    }
  ]
}
```

---

## Field Reference

### Top-Level Fields

| Field | Required | Description |
|-------|----------|-------------|
| `fileFormatVersion` | Yes | Always `1` |
| `name` | Yes | Display name of the PDK |
| `description` | No | Optional description |
| `foundry` | No | Foundry or company name |
| `version` | No | PDK version string |
| `defaultWavelengthNm` | No | Default simulation wavelength (e.g. `1550`) |
| `nazcaModuleName` | No | Python module name for Nazca export (e.g. `"nazca"`) |
| `process` | No | Fabrication-process block (see below) — enables single-process grouping |
| `processAgnostic` | No | `true` for tool PDKs (e.g. virtual analyzers) that are usable in **any** process and never exported to GDS |
| `components` | Yes | List of component definitions |

### Process Block (`process`)

A monolithic chip is fabricated in exactly **one** process. Lunima groups PDKs whose
process fingerprints are compatible (same core material and cladding, core thickness
within ±5 nm, design wavelength within ±40 nm) into one selectable process at
New Design. Declare the block so your PDK participates in that grouping:

```json
"process": {
  "name": "Generic SOI 220nm",
  "foundry": "My Foundry",
  "coreThicknessNm": 220,
  "materials": [
    { "name": "Si",   "nByWavelengthNm": {}, "role": "core" },
    { "name": "SiO2", "nByWavelengthNm": {}, "role": "cladding" }
  ],
  "layers": [],
  "xsections": [],
  "allowedAngles": []
}
```

| Field | Required | Description |
|-------|----------|-------------|
| `name` | Yes | Process display name (e.g. `"Generic SOI 220nm"`, `"HHI-MPW"`) |
| `foundry` | No | Foundry / source of the process |
| `version` | No | Process / PDK version string |
| `coreThicknessNm` | No | Waveguide-core thickness in nm — the key axis for process compatibility |
| `materials` | No | Optical materials; the entries with `role` `"core"` and `"cladding"` feed the compatibility fingerprint |
| `layers` | No | GDS layer stack (name, layer/datatype) |
| `xsections` | No | Waveguide/metal cross-sections (`widthUm`, bend radii) for routing |
| `allowedAngles` | No | Allowed placement/connection angles in degrees |

PDKs **without** a `process` block still load, but each forms its own unnamed
singleton process. Tool PDKs (analyzers, probes) should instead set top-level
`"processAgnostic": true` — they stay available regardless of the active process
and never appear in the process picker.

### Component Fields

| Field | Required | Description |
|-------|----------|-------------|
| `name` | Yes | Display name shown in the component library |
| `category` | Yes | Group name for the component library panel |
| `nazcaFunction` | Yes | Python function name for Nazca export (e.g. `"pdk.mmi1x2"`) |
| `nazcaParameters` | No | Optional default parameters (e.g. `"length=100"`) |
| `rawCode` | No | Custom Python cell source inlined into the export — written by the GDS import, not meant to be authored by hand (see the portability note below) |
| `rawCodeBackend` | No | Export backend the `rawCode` targets: `"nazca"` (GDS imports) or `"gdsfactory"` |
| `widthMicrometers` | Yes | Component bounding box width in µm |
| `heightMicrometers` | Yes | Component bounding box height in µm |
| `nazcaOriginOffsetX` | Yes* | Nazca cell origin measured from the bounding box **left** edge (µm) = `-XMin` of the Nazca bbox |
| `nazcaOriginOffsetY` | Yes* | Nazca cell origin measured from the bounding box **top** edge (µm) = `YMax` of the Nazca bbox |
| `pins` | Yes | List of optical port definitions |
| `outlinePolygons` | No | Imported GDS outline polygons (see below) — when present **and non-empty**, the canvas draws them instead of the plain rectangle body; an empty `[]` falls back to the rectangle |
| `sMatrix` | No | S-matrix for optical simulation (omit to skip simulation) |

> \* Required for GDS export on the normal load path. Analysis-tool components
> (`"nazcaFunction": "__analyzer__"`) are exempt — they are never exported.
> Don't compute the offsets by hand: open **Tools → PDK Offset Editor** and press
> **Auto-Calibrate** (or **Try-Fix-All**) — it renders the real Nazca/KLayout cell
> and writes bbox, offsets, and snapped pin positions back into the JSON.

> **Portability caveat (`rawCode`):** in GDS-imported PDKs the `rawCode` snippet
> embeds the **absolute, machine-local path** of the imported `.gds` file
> (`nd.load_gds(filename="…")`). Such a PDK JSON is therefore NOT portable across
> machines (or user accounts): on another machine the file is missing and the
> export falls back to a placeholder box with a warning instead of the real
> geometry. Re-import the GDS on the target machine to restore it.

### Pin Fields

| Field | Required | Description |
|-------|----------|-------------|
| `name` | Yes | Port name (must match S-matrix references) |
| `offsetXMicrometers` | Yes | X position relative to the bounding box **top-left** corner (µm), increasing right |
| `offsetYMicrometers` | Yes | Y position relative to the bounding box **top-left** corner (µm), increasing **down** |
| `angleDegrees` | Yes | Port direction: `0`=right, `90`=up, `180`=left, `270`=down |
| `pinKind` | No | Signal domain: `"Optical"` (default when absent) or `"Electrical"`. Electrical pins (heater/modulator contacts, detector anode/cathode, bond pads) can only be connected to other electrical pins — never to optical ports — and are excluded from the optical S-matrix and the optical (Nazca/gdsfactory/photontorch) export. |

### Outline Polygon Fields (`outlinePolygons`)

Optional list of closed outline polygons describing the component's physical
shape — written automatically by the GDS import, not meant to be authored by
hand. When present **and non-empty**, the canvas renders these polygons instead
of the plain rectangle body — the render condition is `Count > 0`, so an empty
`[]` falls back to the rectangle (pins, labels and rotation keep working either
way). Coordinates follow the
app convention: micrometers, **Y-down**, relative to the **top-left corner** of
the component's unrotated bounding box. Each polygon's `points` form a closed
ring — the first point is repeated at the end (GDS convention). `layer` and
`dataType` record the GDS origin; all layers currently share one style.

```json
"outlinePolygons": [
  {
    "layer": 1,
    "dataType": 0,
    "points": [
      { "x": 0,  "y": 10 },
      { "x": 20, "y": 10 },
      { "x": 20, "y": 12 },
      { "x": 0,  "y": 12 },
      { "x": 0,  "y": 10 }
    ]
  }
]
```

| Field | Required | Description |
|-------|----------|-------------|
| `layer` | Yes | GDS layer number the polygon came from |
| `dataType` | Yes | GDS datatype the polygon came from |
| `points` | Yes | Closed ring of vertices (`x`, `y` in µm); first point repeated at the end |

### S-Matrix Fields

| Field | Required | Description |
|-------|----------|-------------|
| `wavelengthNm` | Yes | Reference wavelength in nm |
| `connections` | Yes | List of port-to-port transmission entries |
| `fromPin` | Yes | Source pin name |
| `toPin` | Yes | Destination pin name |
| `magnitude` | Yes | Amplitude transmission (0.0–1.0); `1.0` = lossless |
| `phaseDegrees` | Yes | Phase shift in degrees (0–360) |

> **Note:** Only specify connections with non-zero transmission. Reciprocal paths (e.g. `out1 → in`) are automatically handled by the simulator if you omit them.

### Parametric S-Matrices (named physical parameters)

Instead of fixed `magnitude`/`phaseDegrees` values, a component can declare
**named physical parameters** (e.g. insertion loss, splitting ratio) and
compute its S-matrix from formulas. Each parameter appears as a labeled,
unit-aware row in the Properties panel and is editable **per placed instance**;
values are saved in the design file.

```json
"sMatrix": {
  "wavelengthNm": 1550,
  "connections": [
    {
      "fromPin": "in",
      "toPin": "out1",
      "magnitude": 0,
      "phaseDegrees": 0,
      "magnitudeFormula": "Sqrt((splitting_ratio / 100) * Pow(10, -insertion_loss / 10))",
      "phaseDegreesFormula": "0"
    },
    {
      "fromPin": "in",
      "toPin": "out2",
      "magnitude": 0,
      "phaseDegrees": 0,
      "magnitudeFormula": "Sqrt((1 - splitting_ratio / 100) * Pow(10, -insertion_loss / 10))",
      "phaseDegreesFormula": "0"
    }
  ],
  "parameters": [
    {
      "name": "insertion_loss",
      "defaultValue": 0.3,
      "minValue": 0,
      "maxValue": 3,
      "label": "Insertion Loss",
      "unit": "dB",
      "sliderNumber": 0
    },
    {
      "name": "splitting_ratio",
      "defaultValue": 50,
      "minValue": 0,
      "maxValue": 100,
      "label": "Splitting Ratio (out1)",
      "unit": "%",
      "sliderNumber": 1
    }
  ]
},
"sliders": [
  { "sliderNumber": 0, "minVal": 0, "maxVal": 3, "steps": 100, "type": 0 },
  { "sliderNumber": 1, "minVal": 0, "maxVal": 100, "steps": 100, "type": 0 }
]
```

**Parameter fields (`sMatrix.parameters[]`):**

| Field | Required | Description |
|-------|----------|-------------|
| `name` | Yes | Variable name used in formulas (letters, digits, `_`) |
| `defaultValue` | Yes | Value a freshly placed instance starts with |
| `minValue` / `maxValue` | Yes | Allowed range; edits are clamped to it |
| `label` | No | Display name in the Properties panel (defaults to `name`) |
| `unit` | No | Physical unit shown next to the value (e.g. `"dB"`, `"%"`, `"°"`); omit for dimensionless parameters |
| `sliderNumber` | No | 0-based index into `sliders` that stores this parameter's value; omit for a fixed constant evaluated at `defaultValue` |

**Formula fields (per connection):**

| Field | Description |
|-------|-------------|
| `magnitudeFormula` | Amplitude expression; may reference any parameter name |
| `phaseDegreesFormula` | Phase expression in degrees |

Formulas use NCalc syntax with invariant-culture decimals (`0.5`, never `0,5`).
Available functions include `Sqrt`, `Pow`, `Sin`, `Cos`, `Abs`, `Exp`, `Log`.

**Rules:**

- Each parameter bound to a slider needs a matching entry in the component's
  `sliders` list, and the slider's `minVal`/`maxVal` must equal the parameter's
  `minValue`/`maxValue` (the raw slider value *is* the parameter value).
- Invalid bindings (e.g. `sliderNumber` out of range) fail at PDK load time
  with a clear error, not silently at simulation time.
- Keep formulas physical: total output power should never exceed input power
  (e.g. always include the insertion-loss factor in every output branch).

Working examples in `demo-pdk.json`: **1x2 MMI Splitter** (insertion loss +
splitting ratio), **Directional Coupler** (coupling ratio, 90° cross phase),
**Phase Shifter** (phase).

### Bundled-PDK parametric coverage

Parameters drive the **simulation model only** — the GDS layout/geometry never
changes (the Properties panel says so explicitly). Geometry-parametric
components (PCells, e.g. splitting ratio → MMI length) are a separate future
feature; the schema stays forward-compatible with it.

**Parametric (editable in the Properties panel):**

| PDK | Components | Parameters |
|-----|------------|------------|
| Demo | 1x2 MMI Splitter, Y-Junction | insertion loss [dB], splitting ratio [%] |
| Demo | Directional Coupler | coupling ratio [%] |
| Demo | 2x2 MMI Coupler | insertion loss [dB], coupling ratio [%] |
| Demo | Phase Shifter | phase [°] |
| Demo | Straight Waveguide 100µm, 90° Bend | insertion loss [dB] |
| CornerStone SiN | Bend Euler, Bend S, Straight, Taper | insertion loss [dB] |
| SiEPIC | MMI 1x2 TE 1550 3dB, Y-Branch 895 / TE 1310 / Adiabatic / Adiabatic 500nm, SWG Splitter TE 1310 / 1550 | insertion loss [dB], splitting ratio [%] |
| SiEPIC | MMI 2x2 50/50 TE 1310, DC TE 895, DC 2-1 TE 895 | insertion loss [dB], coupling ratio [%] |

The parametrized SiEPIC components keep their fixed parasitic terms (back
reflection 0.02–0.03, port crosstalk 0.01–0.05). Because those terms add to
the split arms, the insertion-loss minimum is floored (0.25 dB for 1x2,
0.3 dB for 2x2) so the S-matrix stays passive (σ_max ≤ 1) at **every** slider
position, including ratio extremes.

**Deliberately NOT parametric (and why):**

- **Measured multi-wavelength S-matrices** (`wavelengthData`): CornerStone
  Coupler, Coupler Straight, Mmi1x2, Mmi2x2, MZI, grating couplers — sampled
  from cspdk's sax compact models per wavelength; formulas cannot reproduce
  the measured dispersion, and the schema does not mix `wavelengthData` with
  formulas.
- **Measured SiEPIC components** (Y-Branch 1550, Directional Coupler TE 1550
  variants, Broadband DC, DC Halfring, Contra-DC, tapers, terminators,
  crossings, polarizer): magnitudes/phases come from Lumerical simulations of
  the fixed layout; a scalar knob would misrepresent the measured response.
- **Light-source I/O** (grating/edge couplers): the Properties panel shows the
  laser editor (wavelength, power, line shape) instead of parameter rows.
- **Photodetector, probe/bond pads, terminators**: terminal elements without a
  meaningful transmission parameter.
- **DBR Filter (demo)**: its defining behaviour is wavelength-selective
  reflection, which the single-wavelength bundled S-matrix does not model; a
  loss slider would suggest a tunability the model doesn't have.

---

## Coordinate System

**Lunima uses a Y-down coordinate system** with the origin at the top-left corner of the canvas.

For component pin positions:
- `offsetXMicrometers` increases to the **right**
- `offsetYMicrometers` increases **downward**
- The reference point is the **top-left corner** of the component bounding box

**Nazca uses a Y-up coordinate system.** When converting a pin at Nazca-space
`(nazca_x, nazca_y)` (bbox `x ∈ [XMin, XMax]`, `y ∈ [YMin, YMax]`):

```
offsetXMicrometers = nazca_x - XMin
offsetYMicrometers = YMax - nazca_y
```

### nazcaOriginOffset

Nazca cells have their own internal origin (the cell org, usually at pin `a0`).
`nazcaOriginOffsetX/Y` record where that origin sits **measured from the bounding
box top-left corner**:

```
nazcaOriginOffsetX = -XMin    (origin's distance from the LEFT edge)
nazcaOriginOffsetY = YMax     (origin's distance from the TOP edge)
```

For Y-symmetric cells `YMax` equals `-YMin`, so older bottom-edge values happened
to be correct — for asymmetric cells (e.g. a 90° bend with bbox `y ∈ [-9.4, 200]`)
only the top-edge convention exports correctly (`nazcaOriginOffsetY: 200`, not `9.4`).

**Don't hand-compute these.** Open **Tools → PDK Offset Editor**, select the
component, and press **Auto-Calibrate**: it renders the actual cell, derives bbox,
origin offsets, and pin positions, and shows a visual overlay to verify alignment.
**Check-All / Try-Fix-All** does the same for the whole PDK in one pass.

---

## Converting Python PDKs to JSON

### Step 1: Identify the component structure

In a Nazca PDK, a component is typically defined like:

```python
def mmi1x2(length=20, width=6):
    with nd.Cell(name='mmi1x2') as C:
        nd.Pin('in',  xs='Deep_Ridge').put(0, 0, 180)
        nd.Pin('out1', xs='Deep_Ridge').put(length, -2, 0)
        nd.Pin('out2', xs='Deep_Ridge').put(length, +2, 0)
        ...
    return C
```

### Step 2: Extract the key values

From Nazca pin definitions:
- `put(x, y, angle)` gives the pin position in Nazca coordinates
- Convert Y: `lunima_y = component_height - nazca_y`

### Step 3: Write the JSON entry

```json
{
  "name": "1x2 MMI Splitter",
  "category": "Splitters",
  "nazcaFunction": "pdk.mmi1x2",
  "widthMicrometers": 20,
  "heightMicrometers": 10,
  "nazcaOriginOffsetX": 0,
  "nazcaOriginOffsetY": 5,
  "pins": [
    { "name": "in",   "offsetXMicrometers": 0,  "offsetYMicrometers": 5, "angleDegrees": 180 },
    { "name": "out1", "offsetXMicrometers": 20, "offsetYMicrometers": 7, "angleDegrees": 0   },
    { "name": "out2", "offsetXMicrometers": 20, "offsetYMicrometers": 3, "angleDegrees": 0   }
  ]
}
```

> Nazca y=−2 → Lunima y = 5 − (−2) = 7
> Nazca y=+2 → Lunima y = 5 − (+2) = 3

---

## Using AI Assistance (ChatGPT / Claude)

AI models are very effective at converting Nazca Python PDKs to Lunima JSON format. Use the prompt template below.

### Template Prompt

```
I need to convert a Nazca Python PDK to Lunima JSON format.

Here is the Lunima PDK JSON schema:
- fileFormatVersion: 1
- Each component has: name, category, nazcaFunction, widthMicrometers, heightMicrometers,
  nazcaOriginOffsetX (= -XMin), nazcaOriginOffsetY (= YMax), pins[], sMatrix{}
- Pins have: name, offsetXMicrometers, offsetYMicrometers, angleDegrees, and optionally
  pinKind ("Optical" default, or "Electrical" for heater/detector/pad contacts)
- Coordinate system: Y-down, pin offsets measured from the bounding box top-left corner
- Nazca conversion: offsetX = nazca_x - XMin, offsetY = YMax - nazca_y
- sMatrix connections have: fromPin, toPin, magnitude (0–1), phaseDegrees — reference
  OPTICAL pins only (electrical pins carry no light)

Here is my Nazca Python PDK code:
[PASTE YOUR PYTHON CODE HERE]

Please generate a complete Lunima PDK JSON file for all components found.
Use reasonable S-matrix values based on the component type if exact values are unknown
(e.g. 0.707 magnitude for 50/50 splitters, 0.99 for waveguides, 90° phase for crossing paths).
```

### Tips for Using AI

1. **Paste the entire PDK file** — AI works best with full context
2. **Review pin positions carefully** — the Y-axis flip is the most common source of errors
3. **Validate magnitude values** — check that `magnitude² ≤ 1` for each output port (energy conservation)
4. **Iterate component by component** — for large PDKs, ask the AI to convert one category at a time
5. **Check nazcaFunction names** — ensure they match the actual Python function names in your PDK

### Validating the Generated JSON

After loading the PDK in Lunima:
- Check the component library panel — all components should appear under their categories
- Place a component on the canvas and verify pins appear at correct positions
- Run a simulation and check that light flows through connections correctly
- Compare pin positions visually against known reference layouts

---

## Example: Complete Component Definitions

See the bundled example PDKs for reference:
- `PDKs/demo-pdk.json` — Demo components covering all major types
- `PDKs/siepic-ebeam-pdk.json` — Real-world SiEPIC EBeam foundry components

These files show correct usage of all fields including multi-pin components, optional parameters, and S-matrix definitions.
