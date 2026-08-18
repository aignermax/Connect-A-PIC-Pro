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
| 1 | **GDS import with pin detection + optional auto-connect** | 🟡 mostly shipped | Port detection from port layers/labels (#878) and the edge-heuristic fallback (#879) are merged; imported cells arrive as black-box components with real pins, and the full journey (import → pins → route → simulate → save/load → re-export) is pinned as an E2E test (#1001/#1006). That journey exposed a real defect — imported cells simulated as black holes (empty S-matrix) — fixed: 2-optical-pin imports now default to a lossless pass-through (#1005/#1012). Still open: the opt-in "auto-connect all pins" pass (#880). Manual geometry stays the default (#807). Serves external users (Peter) and the vision (cells as reusable building blocks). |
| 2 | **DRC-lite** | ✅ v1 shipped | Design Validation covers bend radius, blocked paths, overlaps, bounds, PDK compatibility — plus min spacing (#899/#915), PDK-driven min width (#926/#931), foundry-cited Cornerstone limits (#920/#924), and per-connection rule sets from each chiplet's own process (#936). Unconnected pins / pin mismatch shipped with #894, and Design Validation is reachable as a menu item (#897/#908) — the planned DRC-lite scope is complete. Only the rules that kill real tapeouts — not a full foundry DRC. |
| 3 | Hierarchy done right | ✅ core proven | Group → reusable component with exposed pins, prefab library, instances all work; the chiplet-composition journey (#927) proved two group prefabs compose pin-to-pin, simulate and round-trip with correct physics. Gates, chips, systems = same operation nested. |
| 4 | Behavioral / gate layer | 🟡 chain complete | The full chain ships: truth-table extraction over the real S-matrix sim (#934), Truth Table panel (#947), persisted pin roles + threshold in the .lun (#984), `LogicNetworkBuilder` — canvas wiring as source of truth (#983), `LogicNetworkAssembler` — loaded .lun → evaluable network with per-gate re-simulation (#988), the Logic panel with input toggles + live gate outputs (#991), and shipped half-adder (#987) / full-adder (#992) examples. On top: live 0/1 badges on every gate group on the canvas (#994/#997), honest fan-out treatment — qualitative warnings (#996/#999) plus the quantitative per-branch level report vs. gate thresholds (#1011/#1018) — and behavioral timing slice 1: per-gate propagation delay + critical path in the Logic panel (#1002/#1004), correct for nested groups and mixed dispersion (#1009/#1015), and inter-gate wire delays in the critical path (#1020/#1027). The whole chain is pinned by one E2E journey over the shipped full adder — load → assemble → truth table → timing → fan-out levels → save/load identical (#1022/#1030) — and scale-proven by a 344-gate 4-bit ripple-carry adder example (#1023/#1031, assembly ~1.1 s). The #1018 kill-review defect is fixed: network inputs carry explicit signal identity (#1025/#1032) — the full adder exposes A/B/Cin instead of 30 pin toggles, and fan-out sites report true member counts. Next: signal-naming UI (#1033), 4-bit adder signal names (#1034), event timeline slice 1 (#1035), nonzero-wire-delay honesty proof (#1037), transfer functions. |
| 5 | ISA + assembler + execution visualizer | future | Tiny 4-bit ISA (Hack-computer-style); the assembler is the easy part, the visualizer is the work. The 4-bit ripple-carry adder (#1023/#1031, shipped) is the first datapath stone; the event timeline (#1035) is the visualizer's data structure. |
| 6 | System / multi-chip layer | 🟡 chiplet foundation complete | Chiplets carry their own process identity: per-chiplet placement scope + per-connection bend floors (#935/#937), persisted process bindings in .lun (#938), per-chiplet DRC rule sets (#936), and per-process GDS interconnects — every routed waveguide exports on its own chiplet's cross-section (#939/#960), closing the multi-process journey #933. The whole chain is pinned by one E2E proof: two chiplets, two processes → per-chiplet DRC → per-process export → Cornerstone pre-DRC (#1010/#1016). Boards, fibers between chips, photonic I/O devices, control loops remain future. |
| 7 | Manufacturing path | 🟡 validation shipped | MPW tapeout. First target: Cornerstone SiN — the open KLayout pre-DRC deck is vendored with a headless runner and gated proof fixtures (#932), and the bundled SiN PDK carries the foundry-cited gap/width/bend limits (#920/#924). The CI gap is closed: the runner installs the KLayout CLI (#1017/#1026), so all foundry-deck proofs — single gate (#978), full adder (#995/#998), multi-process chiplets (#1010/#1016) — now run fleet-wide; next scale proof: the 4-bit adder export (#1036), then reach out to the foundry with a DRC-clean artifact. SiEPIC is a consortium, not a fab — fabrication runs via partner fabs (openEBL/AMF et al.). **Optical transistor additionally needs saturable absorbers (SA), i.e. an InP platform** (Fraunhofer HHI / Smart Photonics JePPIX, NDA-based): fab provides data, we build the Lunima PDK JSON and ship it with the software / registry. Connects to #620 (AI-assisted PDK import). |

## How open issues map to the rungs

- Rung 1 remainder: #880 (opt-in auto-connect after import); groundwork still open: #704 (routing core defects), #725 (routing rework: direct-first, anytime-A*)
- Rung 4 next: #1033 (signal-naming UI), #1034 (4-bit adder signal names), #1035 (event timeline slice 1), #1037 (nonzero wire-delay honesty proof)
- Rung 4/7: length matching (#753) is complete — kernel (#1003/#1007), canvas actuator (#1008/#1013), UI (#1021/#1029)
- Rung 7 next: #1036 (4-bit adder GDS export through Cornerstone pre-DRC)
- PDK strategy (rung 7): #620 (AI-assisted PDK import), #773, #772, #740 (registry/library)
- Onboarding (principle 1): #769 (first-steps tutorial), #768 (examples in release bundles)
