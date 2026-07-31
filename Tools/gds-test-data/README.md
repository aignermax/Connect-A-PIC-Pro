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
