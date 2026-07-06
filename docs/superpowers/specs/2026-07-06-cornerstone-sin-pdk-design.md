# CornerStone SiN PDK integration — Design Spec

**Status:** proposed (investigation branch `feat/cornerstone-sin-pdk`)
**Date:** 2026-07-06
**Depends on:** #652 (single-process enforcement, issue #570) — the process-fingerprint model, `ProcessDefinition.coreThicknessNm`, `ProcessCatalog`, the New-Design picker and placement/paste enforcement. **This feature must be built on top of merged #652**, then branched off `main`.

## Goal
Ship a **real, open-source second fabrication process** (silicon nitride) so the single-process rule is demonstrable with genuine data — not the invented SiN demo PDK that was reverted (it broke `PdkJsonSaverRoundTripTests` and `GdsRoundtripTests`). CornerStone SiN is a real UK-MPW process, open via gdsfactory's `cspdk` package.

## Confirmed facts (this machine)
- gdsfactory **9.23.0** available; **cspdk 1.4.2** installed into a throwaway uv env; `cspdk.sin300.PDK` has 38 cells.
- SiN300 fingerprint (for #652's compatibility model): coreMaterial `Si3N4`, `coreThicknessNm` 300, cladding `SiO2`, designWavelengthNm 1550 → **distinct** from the Si-220 SOI PDKs (different material *and* thickness) → its own process group.

## Extraction results — DONE (real cspdk.sin300 data, via `scripts/generate_cspdk_sin300_pdk.py`)
Curated placeable set with real geometry + ports (µm; orientation in degrees). Lunima pin
mapping: `offsetX = port.x − bbox.left`, `offsetY = port.y − bbox.bottom`, `angleDegrees = orientation`.
(`coupler` skipped — its cspdk 1.4.2 default args raise `bend_s(allow_min_radius_violation=…)`.)

| component | W×H µm | ports (name, x, y, orient) |
|---|---|---|
| straight | 10.0 × 1.2 | o1(0,0,180) o2(10,0,0) |
| taper | 10.0 × 1.2 | o1(0,0,180) o2(10,0,0) |
| bend_euler | 25.6 × 25.6 | o1(0,0,180) o2(25,25,90) |
| mmi1x2 | 164.7 × 12.0 | o1(−50,0,180) o2(114.7,2.95,0) o3(114.7,−2.95,0) |
| mmi2x2 | 105.5 × 12.0 | o1(−50,−2.95,180) o2(−50,2.95,180) o3(55.5,2.95,0) o4(55.5,−2.95,0) |
| grating_coupler_rectangular | 219.8 × 11.0 | o1(0,0,180) o2(209.652,0,0) |

Note the SiN single-mode waveguide is **1.2 µm** wide (vs ~0.5 µm for Si-220), and mmi ports
sit at ±2.95 µm — genuine SiN-300 dimensions. `gdsFactoryFunction = "cspdk.sin300.<name>"`.

## Core problem: gdsfactory-native PDK in a nazca-centric schema
Lunima's bundled PDKs are **nazca-based**: each component carries a `nazcaFunction` (+ PDK `nazcaModuleName`), and several tests export **all** bundled components through nazca (`NazcaExportAllComponentsTests`, `GdsRoundtripTests`, `PdkJsonSaverRoundTripTests`). cspdk components are **gdsfactory** cells with no nazca functions. A naive add (invented nazca function strings, hand-written JSON) breaks those tests — exactly what happened with the reverted demo PDK.

So this needs a small schema/model addition, not just a JSON file.

## Recommended approach: a per-PDK `backend` flag
1. **Schema:** add `PdkDraft.Backend` (`"nazca"` default | `"gdsfactory"`). A gdsfactory-backend PDK's components:
   - are **excluded** from the nazca-export tests / all-component nazca export (they export via the existing gdsfactory export path — the GdsFactoryExporter/ubcpdk plumbing from the earlier gdsfactory work, using their gdsfactory cell names instead of `nazcaFunction`);
   - still load into the library, carry a `process` fingerprint, and participate in single-process selection/enforcement (all backend-agnostic).
   - store their gdsfactory factory name (e.g. `cspdk.sin300.straight`) in a `gdsFactoryFunction` field instead of `nazcaFunction`.
2. **S-matrices (light simulation):** cspdk ships layout, not necessarily circuit models. For v1, mark these components **black-box** (no S-matrix → no light sim), which the schema already supports (S-matrix optional). Real models can come later from gdsfactory/`sax` or measured data. Do NOT fabricate S-matrix physics.
3. **Round-trip test:** `PdkJsonSaverRoundTripTests` requires byte-exact save output. Generate the PDK JSON **via the saver** (or run the file through it once) so it is canonical, rather than hand-writing it.
4. **Tests:** exclude `backend == gdsfactory` PDKs from the nazca all-component export tests; add a focused gdsfactory-export smoke for the SiN PDK instead.

## Extraction plan (run when implementing, after #652)
1. Install cspdk into a throwaway env: `uv venv .cspdk && uv pip install --python .cspdk cspdk` (cspdk pins its own gdsfactory).
2. Introspect `cspdk.sin300`: enumerate the PDK's cells; for each chosen component (start with straight, bend, y-branch/1x2, directional coupler, grating coupler):
   - geometry: `c = pdk.get_component('...'); c.dbbox()` → width/height µm;
   - ports: `for p in c.ports: p.dcenter, p.orientation, p.name` → Lunima pins (offset from origin, angleDegrees);
   - factory name → `gdsFactoryFunction`.
3. Emit a `cornerstone-sin-pdk.json` with `backend: "gdsfactory"`, the `process` block (Si3N4/300/SiO2/1550), and the extracted geometry/pins (S-matrix omitted = black-box). Run it through `PdkJsonSaver` to canonicalise.
4. Verify: loads into the library; groups as a distinct "CornerStone SiN 300nm" process; New Design offers it; placing an SOI component on a SiN-locked design is blocked and vice-versa; gdsfactory export works; nazca-export tests stay green (SiN PDK excluded).

## Non-goals (v1)
- Real S-matrix/circuit models for the SiN components (black-box is fine for placement + single-process testing).
- Nazca export of SiN components (they are gdsfactory-native).
- The `cspdk.si220` process (we already have SiEPIC/Demo SOI-220).

## Sequencing
1. Merge #652.
2. Branch off `main`; implement the `backend` flag + test exclusion (small).
3. Run the extraction plan; commit `cornerstone-sin-pdk.json`.
4. Verify the two-process enforcement end-to-end.
