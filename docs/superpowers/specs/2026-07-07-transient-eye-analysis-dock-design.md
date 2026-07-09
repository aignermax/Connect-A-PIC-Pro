# Transient & Eye/BER Analysis Dock — Design Spec

**Date:** 2026-07-07
**Issue(s):** relocation of the Transient (#600/#601) panel + integration of Eye/BER (#535/#627)
**Status:** design — awaiting approval

## Goal

Turn the transient (time-domain) simulation from a permanent, out-of-place section in the
**properties panel** into a first-class **simulation mode** with a proper **analysis home**, and
fold the Eye-diagram/BER work (agent PR #627) into that same home instead of adding a second
right-panel section. One coherent change that supersedes both current placements.

## Personas served

- **Mirko** (full-stack system simulation toward a photonic CPU): time-domain → eye/BER is a real
  pipeline stage; a dedicated, growable analysis area with a clear mode concept fits his
  "simulate on all levels" goal.
- **Peter** (precision, Figma-like flow): non-modal, inline results (canvas stays visible), exact
  numeric controls. He dislikes modal dialogs — the bottom dock is non-modal.
- **Priya/Ingrid** (secondary): a legible power-vs-time / eye plot is demo-friendly and a hook for
  later measurement comparison. No change makes the tool incomprehensible to Ingrid.

Chosen primary purpose (confirmed with the user): **signal integrity / eye-diagram precursor.**

## Scope

**In scope**
1. A **`SimulationMode`** concept (CW / Transient) surfaced as a compact selector next to the
   toolbar Run button; the Run action (L) dispatches by the active mode.
2. A new **bottom Analysis dock** — a collapsible bottom region (mirroring the existing Error
   Console dock), separate from and leaving the Error Console untouched — hosting a `TabControl`
   with a **Transient** tab and an **Eye/BER** tab.
3. **Re-home** the existing transient UI (params, run, waveform plot, per-pin legend, peak table,
   CSV export) from `TimeDomainPanel` in the right panel into the dock's **Transient** tab.
4. **Adopt** #627's self-contained Eye/BER work into the dock's **Eye/BER** tab.
5. **Remove** the transient section from the right/properties panel; **do not** adopt #627's
   right-panel placement of the Eye panel.

**Out of scope (YAGNI / non-goals)**
- No change to the transient or eye/BER **algorithms** (Core is reused verbatim).
- No rewrite of `TimeDomainViewModel` or `EyeDiagramViewModel` logic — this is re-hosting + a
  mode-state promotion + placement.
- No new eye/BER features beyond what #627 already implemented.
- Error Console behavior is unchanged (stays its own dock/toggle).
- No FDTD/Tidy3D modes yet (the `SimulationMode` enum is designed to allow them later, but only
  CW and Transient are implemented now).

## Architecture

### Components

- **`SimulationMode` (enum: `Cw`, `Transient`)** + a small owner of the current mode. Today the
  flag lives as `RightPanel.TimeDomain.IsTimeDomainMode`; it is promoted to a shared, top-level
  state exposed to the toolbar. The transient VM reads the mode rather than owning it.
- **Toolbar mode selector** (in `MainWindow.axaml`, next to `RunSimulationCommand`): a compact
  two-way selector (CW | Transient). Switching to Transient opens the Analysis dock on the
  Transient tab. The existing Run button/`L` binding runs the active mode.
- **Bottom Analysis dock** — new view `AnalysisDockPanel.axaml` + a `BottomPanel.Analysis`
  sub-ViewModel that holds the tab state (`SelectedTab`, `IsVisible`, toggle command), collapsible
  like the Error Console. Contains a `TabControl`:
  - **Transient tab** — hosts the existing transient controls + plot (moved from `TimeDomainPanel`).
  - **Eye/BER tab** — hosts #627's `EyeDiagramPanel` content.
- **Reused core (unchanged):**
  - `Connect-A-Pic-Core/LightCalculation/TimeDomainSimulation/*` (transient).
  - `Connect-A-Pic-Core/Analysis/EyeDiagram/*` (from #627: `PrbsGenerator`, `NoiseModel`,
    `EyeDiagramBuilder`, `BerEstimator`, `EyeMetrics`, `EyeHistogram`, `EyeSimulationPlan`).
- **Reused/adopted ViewModels:**
  - `TimeDomainViewModel` (transient) — re-parented under the Analysis dock; loses the
    `IsTimeDomainMode` flag (moves to the shared `SimulationMode`).
  - `EyeDiagramViewModel` + `EyeDiagramPlotBuilder` + `TransientCircuitFactory` (from #627) —
    adopted as-is; `EyeDiagramPanel.axaml` view re-parented into the Eye/BER tab.

### What we adopt from #627 vs discard

**Adopt (new, self-contained, no conflict with main):**
- `Connect-A-Pic-Core/Analysis/EyeDiagram/*` + their unit tests.
- `CAP.Avalonia/ViewModels/Analysis/EyeDiagram/EyeDiagramViewModel.cs`,
  `EyeDiagramPlotBuilder.cs`, `CAP.Avalonia/ViewModels/Analysis/TransientCircuitFactory.cs`.
- `CAP.Avalonia/Views/Panels/EyeDiagramPanel.axaml(.cs)` — content reused, but hosted in the
  Analysis dock's Eye/BER tab, not the right panel.

**Discard from #627 (superseded by this design):**
- Its right-panel wiring: additions to `RightPanelViewModel` and the right-panel section in
  `MainWindow.axaml`. We wire the Eye panel into the Analysis dock instead.
- Its stale (77-commits-behind) versions of shared files (`MainViewModel`, `TimeDomainViewModel`,
  `CanvasAndPanelExtensions`) — we integrate the adopted pieces onto current main rather than
  taking #627's copies. `TransientCircuitFactory` (the one genuinely shared extraction) is taken
  as a new file.

### Data flow

1. User picks **CW** or **Transient** in the toolbar selector → updates `SimulationMode`.
2. **Run (L):**
   - CW → existing steady-state simulation (unchanged).
   - Transient → `TimeDomainViewModel.RunTransient` (existing) → result renders in the Transient
     tab; the Analysis dock opens to that tab.
3. **Eye/BER tab:** its own `Run Eye Analysis` (from #627) drives the circuit via
   `TransientCircuitFactory` + PRBS/noise → histogram → eye plot + BER metric. Independent of the
   transient tab's single-pulse run, but shares the same circuit/simulator setup.

## Files

**New**
- `CAP.Avalonia/Views/Panels/AnalysisDockPanel.axaml(.cs)` — the bottom dock with the TabControl.
- `CAP.Avalonia/ViewModels/Analysis/SimulationMode.cs` — the enum.
- Analysis-dock sub-ViewModel (tab/visibility state) under `CAP.Avalonia/ViewModels/Panels/`.
- Adopted from #627: `Connect-A-Pic-Core/Analysis/EyeDiagram/*` (+ tests),
  `.../Analysis/EyeDiagram/EyeDiagramViewModel.cs`, `EyeDiagramPlotBuilder.cs`,
  `.../Analysis/TransientCircuitFactory.cs`, `Views/Panels/EyeDiagramPanel.axaml(.cs)`.

**Modified**
- `CAP.Avalonia/Views/MainWindow.axaml` — add toolbar mode selector; remove
  `<panels:TimeDomainPanel/>` from the right panel (~line 790); add the Analysis dock at the bottom.
- `CAP.Avalonia/ViewModels/MainViewModel.cs` — own/expose `SimulationMode`; wire Run dispatch and
  dock-open-on-transient.
- `CAP.Avalonia/ViewModels/Panels/RightPanelViewModel.cs` — drop the transient wiring (mode flag
  moves out).
- `CAP.Avalonia/ViewModels/Analysis/TimeDomainViewModel.cs` — read shared `SimulationMode` instead
  of owning `IsTimeDomainMode`; otherwise unchanged.
- `CAP.Avalonia/Views/Panels/TimeDomainPanel.axaml` — becomes the Transient-tab content (mode
  radio buttons removed; params/plot kept).
- `CAP.Avalonia/DI/CanvasAndPanelExtensions.cs` (or the relevant DI extension) — register the
  Analysis dock VM + `EyeDiagramViewModel`.

## Testing

- **Mode dispatch:** Run in Transient mode invokes the transient run (not CW) and opens the
  Analysis dock on the Transient tab; Run in CW mode invokes the CW path.
- **Mode state:** toggling the toolbar selector updates `SimulationMode`; the transient VM reflects
  it (no stale `IsTimeDomainMode`).
- **Dock:** the Analysis dock toggles/collapses independently of the Error Console; tab selection
  works.
- **Eye/BER core:** #627's existing unit tests (`PrbsGeneratorTests`, `NoiseModelTests`,
  `EyeDiagramBuilderTests`, `BerEstimatorTests`, `EyeSimulationPlanTests`) are adopted and must pass.
- No physics/algorithm tests change — Core is reused.

## Conflict-avoidance rationale

Both the current transient placement (#601) and #627's eye placement put a full analysis section in
the right/properties panel. Doing the relocation and the eye integration **separately** would
force a double conflict on the same shared files (`TimeDomainViewModel`, `MainViewModel`,
`MainWindow.axaml`, `RightPanelViewModel`). This design does both **once**: it builds the bottom
Analysis dock, re-homes transient, and adopts #627's self-contained core + eye VM into the dock —
so #627's stale right-panel wiring is simply not used, and there is a single integration window.
On merge, #627 is closed as superseded (its valuable code lives on in the dock).
