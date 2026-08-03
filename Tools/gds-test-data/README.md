# GDS test data

Real, externally-produced GDSII files used by the integration tests in
`UnitTests/Import/Gds/RealGdsFileIntegrationTests.cs`. The tests parse these
files with Lunima's own `GdsReader`/`GdsHierarchyImporter` only — no Python or
network access is needed at test time.

## gdsfactory_mzi_like.gds

- **Produced by:** [gdsfactory](https://github.com/gdsfactory/gdsfactory) 9.47.0
  (generic PDK `gf.gpdk`, Python 3.14, installed in a throwaway venv — no
  hand-editing of the output file).
- **Circuit:** `mmi1x2` -> `bend_euler(radius=10)` -> `straight(length=10)`
  (arm 1) and `mmi1x2` -> `straight(length=10)` (arm 2), abutted with
  gdsfactory's `connect()`, so the three instance-to-instance joints are exact
  (nm grid, well within the importer's 0.05 µm abutment tolerance).
- **Hierarchy:** top cell `gdsfactory_mzi_like` with 4 direct references
  (mmi1x2, bend_euler, 2x the same straight cell — one rotated 90°).
  All cell names carry gdsfactory's parameter/hash suffixes (plus a `$1`
  dedup suffix from the `dup()` calls). kfactory's `$$$CONTEXT_INFO$$$`
  metadata cell is omitted (`SaveLayoutOptions.write_context_info = False`),
  so the file holds exactly the circuit's own cells.
- **Port labels:** every cell's ports are written as TEXT elements on layer
  (1,10) — gdsfactory's port-label convention (`o1`/`o2`/`o3` on the
  sub-cells, circuit ports `in0`/`out0`/`out1` on the top cell). Waveguide
  cores are polygons on (1,0); the file uses a 1 nm database unit.
- **Size:** ~3.4 KB.

### Regenerating

```python
import gdsfactory as gf

gf.gpdk.PDK.activate()

c = gf.Component("gdsfactory_mzi_like")

# dup() unlocks the cached @cell components so port labels can be added.
mmi = gf.components.mmi1x2().dup()
bend = gf.components.bend_euler(radius=10).dup()
wg = gf.components.straight(length=10).dup()

# Port labels on (1,10) BEFORE referencing (cells lock once instantiated).
for comp in (mmi, bend, wg):
    for port in comp.ports:
        comp.add_label(text=port.name, position=port.center, layer="PORT")

mmi_ref = c << mmi
bend_ref = c << bend
wg1_ref = c << wg
wg2_ref = c << wg

bend_ref.connect("o1", mmi_ref.ports["o2"])
wg1_ref.connect("o1", bend_ref.ports["o2"])
wg2_ref.connect("o1", mmi_ref.ports["o3"])

c.add_port("in0", port=mmi_ref.ports["o1"])
c.add_port("out0", port=wg1_ref.ports["o2"])
c.add_port("out1", port=wg2_ref.ports["o2"])

for port in c.ports:
    c.add_label(text=port.name, position=port.center, layer="PORT")

# Skip kfactory's $$$CONTEXT_INFO$$$ metadata cell.
from kfactory import kdb

save_options = kdb.SaveLayoutOptions()
save_options.write_context_info = False
c.write_gds("gdsfactory_mzi_like.gds", save_options=save_options)
```

## test.gds

- **Produced by:** a **Lunima mixed-backend export, part 2 of 2** (the
  gdsfactory merge script `test.py`): `gf.Component('ConnectAPIC_Design')`
  with 30 `add_ref` route segments of ubcpdk-based
  `gf.components.straight` / `bend_circular` (one `straight(length=0.00)`),
  merging `test_nazca_partial.gds` (part 1, nazca-rendered devices) via
  `gf.import_gds`. Committed **as-is** (the exact bytes the user reported);
  `test_nazca_partial.gds` itself is NOT committed — its content is fully
  contained in `test.gds`.
- **Structure (34 cells, 1 nm database unit):** top-cell candidates
  `$$$CONTEXT_INFO$$$` (kfactory metadata, references all 23 route cells at
  the origin) and `ConnectAPIC_Design` (31 references: 30 route cells + the
  merged partial). The 22 distinct route cells (20 `straight_*`, 2
  `bend_circular_*`) carry waveguide cores on (1,0) plus a (68,0) devrec halo
  and **no port labels**; the zero-length straight
  (`straight_..._L0_N_a362bd09`) is an empty cell. The merged partial sits
  behind nazca's default **`nazca`** pass-through wrapper (gf.import_gds names
  the component after the source file's top cell) →
  `ConnectAPIC_NazcaPartial` → 7 flattened device references (2× mmi2x2_dp,
  ebeam_bdc_te1550, 2× ebeam_crossing4, ebeam_adiabatic_te/tm1550) whose
  (1,10)/(501,1) port labels are nested inside the device cells.
- **Expected import behavior (asserted by
  `UnitTests/Services/GdsImport/MixedBackendReimportIntegrationTests.cs`):**
  explode registers the 22 route cells, places 29 instances (31 refs −
  zero-length straight − artifact wrapper), skips `ConnectAPIC_NazcaPartial`
  behind its `nazca` wrapper with one info note (flattened partial geometry
  is not reconstructed, v1), drops the zero-geometry straight with one info
  note, and reconstructs 20 route↔route abutments (the chains run through
  the skipped devices, so the joints at device ports dangle). Black-box
  finds no pins (no top-level labels; no (1,0) polygon touches the top bbox
  — route ends sit 0.225 µm inside the devrec halo) and fails with the
  honest "no pins → nothing registered" warnings.

### Regenerating

Part 2 of the two-script export (`test.py`, run after the nazca part 1 wrote
`test_nazca_partial.gds` next to it) is reproduced in the issue/PR
discussion; the committed file is the ground truth and is not regenerated in
CI.

