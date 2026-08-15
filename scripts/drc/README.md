# Vendored CORNERSTONE KLayout pre-DRC deck

`cornerstone_sin300_drc.lydrc` is the **SiN 300nm pre-DRC deck v2.1** from the open
CORNERSTONE PDK, vendored (pinned, no runtime download) so exported GDS can be checked
against foundry-true rules headless (issue #932, roadmap rung 7).

## Provenance

- **Source:** https://github.com/cornerstone-uos/cornerstone-pdk/blob/main/SiN_300nm/drc_rules.lydrc
- **Pinned commit:** `b57ee3a9f0809535b90525805f11eae089430ce2` (2025-08-20)
- **License:** TAPR Open Hardware License v1.0 (repo `LICENSE.txt`) — copy/modify/distribute
  with attribution permitted.
- **Modifications:** none to the rule logic. The only change vs. upstream is the
  `<description>` element, which carries this vendoring note. The `cornerstone-pdk`
  variant was chosen over `cornerstone-community`'s copy because it already supports
  batch mode (`if $input / source($input)` / `if $report / report(...)` plumbing, commit
  `fdd6b311f38c`); the rule checks themselves are byte-identical between the two repos.

## Headless usage

```bash
klayout -b -r scripts/drc/cornerstone_sin300_drc.lydrc -rd input=chip.gds -rd report=report.lyrdb
```

Usually via the wrapper, which also parses the report and sets a meaningful exit code:

```bash
python3 scripts/run_cornerstone_drc.py chip.gds
```

## Updating the pin

1. Check https://github.com/cornerstone-uos/cornerstone-pdk/commits/main/SiN_300nm/drc_rules.lydrc
   for a newer deck version.
2. Replace the file, update the commit hash in the `<description>` element and in this README.
3. Re-run `python3 scripts/run_cornerstone_drc.py` on a known-good and a known-broken GDS
   (the xUnit tests under `UnitTests/Export/CornerstoneDrc/` do exactly this when `klayout`
   is on PATH).

## What the deck checks (v2.1)

| GDS layer | Rule | Limit |
|-----------|------|-------|
| 203 (SiN light field / waveguide core) | min feature size | 250 nm (350 nm for features shorter than ~20 µm) |
| 203 | min gap | 250 nm |
| 204 (SiN dark field / etch) | min feature size / gap | 250 nm (350 nm gap for short features) |
| 39 (heater) | min width / gap | 600 nm / 10 µm |
| 41 (contact pad) | min width / gap | 2 µm / 10 µm |
| 100 (label) | min width / gap | 250 nm |
| 22 (cladding opening) | min width / gap | 20 µm |
| 99 (cell outline) | die area | must equal 15450 × 11470 µm² (full CORNERSTONE die) |
| all | grid | 1 nm |

Note: the deck has **no bend-radius rule** — Lunima's DRC-lite 30 µm minimum bend radius
comes from cspdk's tech constants, not from this deck. This is a pre-DRC; the foundry runs
the full DRC on submission.
