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
| 1 | **GDS import with pin detection + optional auto-connect** | 🟡 mostly shipped | Port detection from port layers/labels (#878) and the edge-heuristic fallback (#879) are merged; imported cells arrive as black-box components with real pins. Still open: the opt-in "auto-connect all pins" pass (#880). Manual geometry stays the default (#807). Serves external users (Peter) and the vision (cells as reusable building blocks). |
| 2 | **DRC-lite** | 🟡 mostly shipped | Design Validation covers bend radius, blocked paths, overlaps, bounds, PDK compatibility — plus min spacing (#899/#915), PDK-driven min width (#926/#931), foundry-cited Cornerstone limits (#920/#924), and per-connection rule sets from each chiplet's own process (#936). Still open: unconnected pins / pin mismatch + menu-item promotion (#897, in review). Only the rules that kill real tapeouts — not a full foundry DRC. |
| 3 | Hierarchy done right | ✅ core proven | Group → reusable component with exposed pins, prefab library, instances all work; the chiplet-composition journey (#927) proved two group prefabs compose pin-to-pin, simulate and round-trip with correct physics. Gates, chips, systems = same operation nested. |
| 4 | Behavioral / gate layer | 🟡 chain complete | The full chain ships: truth-table extraction over the real S-matrix sim (#934), Truth Table panel (#947), persisted pin roles + threshold in the .lun (#984), `LogicNetworkBuilder` — canvas wiring as source of truth (#983), `LogicNetworkAssembler` — loaded .lun → evaluable network with per-gate re-simulation (#988), the Logic panel with input toggles + live gate outputs (#991), and shipped half-adder (#987) / full-adder (#992) examples. Next: logic-state visualization on the canvas, delays/transfer functions, honest fan-out treatment (optically, fan-out needs splitters + level restoration — the logic layer currently idealizes it). |
| 5 | ISA + assembler + execution visualizer | future | Tiny 4-bit ISA (Hack-computer-style); the assembler is the easy part, the visualizer is the work. |
| 6 | System / multi-chip layer | 🟡 started (chiplets) | Chiplets carry their own process identity: per-chiplet placement scope + per-connection bend floors (#935/#937), persisted process bindings in .lun (#938), per-chiplet DRC rule sets (#936). Still open: per-chiplet GDS export stacks (#939). Boards, fibers between chips, photonic I/O devices, control loops remain future. |
| 7 | Manufacturing path | 🟡 validation shipped | MPW tapeout. First target: Cornerstone SiN — the open KLayout pre-DRC deck is vendored with a headless runner and gated proof fixtures (#932), and the bundled SiN PDK carries the foundry-cited gap/width/bend limits (#920/#924). Next: run real exports through it routinely, then reach out to the foundry with a DRC-clean artifact. SiEPIC is a consortium, not a fab — fabrication runs via partner fabs (openEBL/AMF et al.). **Optical transistor additionally needs saturable absorbers (SA), i.e. an InP platform** (Fraunhofer HHI / Smart Photonics JePPIX, NDA-based): fab provides data, we build the Lunima PDK JSON and ship it with the software / registry. Connects to #620 (AI-assisted PDK import). |

## How open issues map to the rungs

- Rung 1 remainder: #880 (opt-in auto-connect after import); groundwork still open: #704 (routing core defects), #725 (routing rework: direct-first, anytime-A*)
- Rung 2 remainder: #897 (unconnected-pin check + menu item, in review)
- Rung 4/7 enabler: #753 (waveguide length matching / phase matching)
- Rung 6 remainder: #939 (per-chiplet GDS export stacks — last red station of the multi-process journey #933)
- PDK strategy (rung 7): #620 (AI-assisted PDK import), #773, #772, #740 (registry/library)
- Onboarding (principle 1): #769 (first-steps tutorial), #768 (examples in release bundles)
