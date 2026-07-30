# Lunima Roadmap — The Photonic Mini-Computer

> Tracks the overarching goal of the project and how the open issues fit into it.
> Meta-issue: [#537](https://github.com/aignermax/Lunima/issues/537)

## Vision

Enable anyone — including people without a photonics background — to **design,
simulate, program, and eventually manufacture a small photonic computer**:

- Place components, route waveguides, watch the optical impulse propagate through
  the whole circuit (already works at PIC level).
- Build logic gates from components, chips from gates, systems from chips —
  the same nesting operation at every level (Figma-style components).
- Write assembly code, run it on the self-designed chip, and watch it execute.
- Tape out the design on an MPW run and hold a real (low-performance but working)
  photonic chip in your hands.

Positioning: education first — "NAND2TETRIS for photonics" / a Tiny-Tapeout-style
learning path. Performance of the fabricated device is irrelevant; the learning
is the product.

## Guiding principles

1. **Figma-simple.** Every rung must stay usable for non-photonics people.
2. **Every rung ships standalone.** Each step below is a useful product on its own.
3. **Never build more than the next rung.** Users pull us to the rung after.
4. **Layered simulation.** S-matrix/FDTD does not scale to a full computer.
   Abstraction layers: device level (S-matrix) → behavioral level (Verilog-A-style
   transfer functions + delay) → logic level (event-driven, thresholds).

## Rungs

| # | Rung | Status | Notes |
|---|------|--------|-------|
| 0 | PIC canvas, S-matrix sim, light propagation, GDS export (Nazca), S-param import, metal routing, component groups | ✅ exists | |
| 1 | **GDS import with pin detection + optional auto-connect** | 🔜 next | Load a GDS cell as a black-box component; detect ports from port layers/labels, fall back to edge heuristics. Import option "auto-connect all pins": after placement, route all pin pairs with the existing A* router + crossing insertion (runs a while on large imports). No separate "bake" step needed — manual geometry stays the default (#807), auto-connect is opt-in per import. Depends on routing rework #704/#725. Serves external users (Peter) and the vision (cells as reusable building blocks). |
| 2 | **DRC-lite** | planned | Entry point exists: Design Validation ("check conflicts", Diagnostics panel) already covers bend radius, blocked paths, waveguide overlaps, component bounds, PDK compatibility (`DesignValidator`). Extend with: min spacing, unconnected pins / pin mismatch, width + layer rules with PDK-driven values. Promote from panel button to a menu item. Only the rules that kill real tapeouts — not a full foundry DRC. |
| 3 | Hierarchy done right | partially there (`ComponentGroup`) | Group any circuit → reusable component with exposed pins, library, instances. Gates, chips, systems = same operation nested. |
| 4 | Behavioral / gate layer | seed exists (Verilog-A export) | Gates with transfer-function models, thresholds, delays; logic-level animation instead of field propagation. |
| 5 | ISA + assembler + execution visualizer | future | Tiny 4-bit ISA (Hack-computer-style); the assembler is the easy part, the visualizer is the work. |
| 6 | System / multi-chip layer | future | Boards, fibers between chips, photonic I/O devices, heater/phase control loops (metal routing already exists). |
| 7 | Manufacturing path | future | MPW tapeout. First target: Cornerstone SiN (open PDK, open KLayout DRC deck — validate our exported GDS against it, then reach out with a concrete artifact). SiEPIC is a consortium, not a fab — fabrication runs via partner fabs (openEBL/AMF et al.). **Optical transistor additionally needs saturable absorbers (SA), i.e. an InP platform** (Fraunhofer HHI / Smart Photonics JePPIX, NDA-based): fab provides data, we build the Lunima PDK JSON and ship it with the software / registry. Connects to #620 (AI-assisted PDK import). |

## How open issues map to the rungs

- Rung 1 groundwork: #704 (routing core defects), #725 (routing rework: direct-first, anytime-A*), #801 (grid ownership bug)
- Rung 4/7 enabler: #753 (waveguide length matching / phase matching)
- PDK strategy (rung 7): #620 (AI-assisted PDK import), #773, #772, #740 (registry/library)
- Onboarding (principle 1): #769 (first-steps tutorial), #768 (examples in release bundles)

## Next issues to create

1. **GDS import with pin detection (+ optional auto-connect)** — load a GDS cell as
   a black-box component; ports from port layers/labels with edge-heuristic
   fallback; import option to route all pins via the existing router.
2. **DRC-lite: extend Design Validation + menu item** — add min spacing, pin
   mismatch, width/layer rules (PDK-driven) to `DesignValidator`; promote the
   check to a menu item.
3. **Foundry validation: Cornerstone DRC deck** — run our exported GDS through
   Cornerstone's open KLayout DRC deck; fix what fails; then contact foundry with
   the DRC-clean demo.
