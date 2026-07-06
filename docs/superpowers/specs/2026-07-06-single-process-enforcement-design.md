# Single-Process Enforcement — Design Spec (issue #570)

**Status:** proposed
**Date:** 2026-07-06
**Scope:** (A) PDK process metadata + (B) compatibility derivation + (C) design-start process selection, indicator, library filter + (D) enforcement & migration. **Out of scope:** (E) FDTD solver wiring (consumes the same metadata later).

---

## 1. Goal

A monolithic photonic chip is fabricated in exactly one process. All components on the canvas must belong to processes that are physically compatible. The **primary goal is comprehension**: the user must clearly understand, at all times, which fabrication process the design is locked to — and that they can only use one at a time.

This supersedes agent PR #602, whose `SinglePdkPolicy` keys enforcement on the raw PDK **name** string. That is too strict: several PDKs (a foundry library, a research library, the user's own components) can target the **same process** and must be usable together. #602 is **not merged**; its policy logic is recycled into the process-keyed policy here.

## 2. Domain rationale

- A **process** defines the physical stack: waveguide core material + thickness (e.g. Si 220 nm, SiN 340 nm, InP), cladding, design wavelength band. Components are dispersion/geometry-engineered for one process; fabricated in another they simply do not work.
- **"PDK" ≠ "process".** Multiple PDKs can target one process → mixable. What matters is the physical fingerprint, not the PDK label.
- **Built-in / tool components** (lasers, detectors, fiber couplers) are process-agnostic packaged/heterogeneous parts → always allowed.
- Cross-process integration exists only as heterogeneous/packaged assembly, never monolithically — out of scope here.
- The same process fingerprint (material, thickness, wavelength) is exactly what the **FDTD solver** needs (E), so declaring it in the PDK serves double duty.

## 3. Data model

### 3.1 Existing (reused)

- `CAP_DataAccess…DTOs.PdkDraft.Process` (`ProcessDefinition?`) — **already present** (added for #570). The `process` block in a PDK JSON.
- `ProcessDefinition` — `Name`, `Foundry`, `Layers`, `Xsections`, `Materials` (each with `Role` = "core"/"cladding"/… and `NByWavelengthNm`), `AllowedAngles`.
- `PdkDraft.DefaultWavelengthNm` (int, default 1550) — the design wavelength.
- `ComponentTemplate.PdkSource` (string, default `"Built-in"`).
- `PdkManagerViewModel` — `LoadedPdks`, `GetEnabledPdkNames()`, `OnFilterChanged`; drives `LeftPanelViewModel.FilterComponents`.

### 3.2 New

- **`ProcessDefinition.CoreThicknessNm`** (`double?`, `"coreThicknessNm"`): the defining waveguide-core thickness. The only physical fingerprint axis not yet modelled (layers carry no thickness). Optional → old PDKs still parse.
- **`ProcessFingerprint`** (new value type, `CAP_Core.Components.Process`): the compatibility identity derived from a PDK, computed by a pure `ProcessFingerprintFactory.From(PdkDraft)`:
  - `CoreMaterial` — `Process.Materials.FirstOrDefault(role=="core").Name` (case-insensitive).
  - `Cladding` — `Process.Materials.FirstOrDefault(role=="cladding").Name`.
  - `CoreThicknessNm` — `Process.CoreThicknessNm`.
  - `DesignWavelengthNm` — `PdkDraft.DefaultWavelengthNm`.
  - `ProcessName` — `Process.Name` (display only).
  - `IsSpecified` — true when the PDK has a `process` block with at least a core material.

### 3.3 Compatibility rule (with tolerance)

`ProcessCompatibility.AreCompatible(a, b)` (pure):

- Categorical, exact (case-insensitive): `CoreMaterial`, `Cladding` must be equal.
- Numeric, within tolerance:
  - `|CoreThicknessNm_a − CoreThicknessNm_b| ≤ CoreThicknessToleranceNm` (**default 5 nm**).
  - `|DesignWavelengthNm_a − DesignWavelengthNm_b| ≤ WavelengthToleranceNm` (**default 40 nm**, ≈ C-band width).
- Tolerances are named constants (single place, tunable; no UI in v1).
- **Unspecified fallback:** a PDK with no `process` block, or an incomplete fingerprint (no core material), is its **own singleton process** keyed by the PDK name; it never groups with any other PDK.

`Si 220 nm SOI @1550` and `Si 222 nm SOI @1560` → compatible. `Si 220 nm` vs `SiN 340 nm` or `@1310` → incompatible.

### 3.4 Bundled PDKs

Populate `process` blocks (incl. `coreThicknessNm`, core+cladding materials, `defaultWavelengthNm`) for the shipped PDKs (`demo-pdk.json`, `siepic-ebeam-pdk.json`) so grouping works out of the box. Values are public/generic (no proprietary foundry data).

## 4. Processes = derived groups (B)

There is **no separate persistent process registry**. `ProcessCatalog` (pure, in `CAP_Core.Components.Process`) groups the currently loaded PDKs by compatibility into **processes**:

- Input: the loaded PDKs' `(PdkName, ProcessFingerprint)`.
- Output: a list of `ProcessGroup { DisplayName, Fingerprint, MemberPdkNames }`. `DisplayName` = `ProcessName` when all members agree, else a derived label like `"Si 220 nm · SiO₂ · 1550 nm"`.
- Compatible fingerprints collapse into one group; unspecified PDKs each form a singleton group.
- "Drifting apart" is simply two PDKs landing in different groups.

## 5. Design-start selection, active-process state, indicator (C)

### 5.1 Active-process state

`FileOperationsViewModel.ActiveProcess` (replaces #602's `ActivePdkName`), a small record persisted in the `.lun` `DesignFileData`:

- `DesignFileData.ActiveProcess` (`ActiveProcessData?`): `{ Fingerprint fields…, DisplayName, IsPlayground }`.
- Three states: **unset** (brand-new, before choice), a **real process** (fingerprint + member PDKs), or **Playground** (`IsPlayground = true`).

### 5.2 New Design flow

`NewProject` opens a **"Choose fabrication process"** dialog before the empty canvas is usable:

- Lists the derived `ProcessGroup`s (DisplayName + member-PDK count).
- Plus **"Playground — mix any components (not manufacturable)"**.
- Choice sets `ActiveProcess`. Cancelling the dialog cancels New Design (keeps the current design).

### 5.3 Indicator

A **persistent active-process chip** in the top toolbar/title area, always visible:

- Real process: `Process: <DisplayName>` (neutral styling).
- Playground: `⚠ Playground — not manufacturable` (warning styling).
- Unset: `No process selected`.
- Tooltip lists the process's member PDKs / the fingerprint. (Exact control placement is an implementation detail; requirement: always visible, not a transient status-bar message.)

## 6. Library filter reconciliation (C)

`LeftPanelViewModel` already filters templates by `PdkManager.GetEnabledPdkNames()`.

- **Real process active:** the enabled set is **driven** to the active process's `MemberPdkNames` (+ Built-in/tool always shown). The manual per-PDK enable toggles in the PDK manager are **hidden/disabled** — the process, not hand-picking, defines availability.
- **Playground:** the current manual multi-toggle behaviour is retained.
- Reconciliation runs on active-process change and on PDK (re)load.

## 7. Enforcement (D)

`SingleProcessPolicy` (pure, `CAP_Core.Components.Process`) — the process-keyed successor to #602's `SinglePdkPolicy`:

- `CheckPlacement(activeProcess, componentPdkName, catalog) → (IsAllowed, BlockReason?)`:
  - Built-in/tool component (`PdkSource` null/empty/`"Built-in"`) → allowed.
  - Playground or unset active process → allowed.
  - Component's PDK compatible with the active process (same group) → allowed.
  - Otherwise blocked with a clear message naming both processes ("belongs to <X>; chip is locked to <Y>").
- Wired into **both** placement paths in `CanvasInteractionViewModel`:
  - `PlaceComponentAt` (before issuing the undo command — blocked placements never touch the undo stack; a clear status message is shown).
  - **`OnComponentsPasted`** — foreign-process components are rejected/filtered with a clear message (closes #602's paste gap).
- Built-in exemption keys on `PdkSource ∈ {null, "", "Built-in"}`.

## 8. Persistence & migration

- Save: `DesignFileData.ActiveProcess` is written from the current `ActiveProcess`.
- Load (has `ActiveProcess`): restore it verbatim.
- Load (legacy `.lun`, no `ActiveProcess`): infer from placed components' PDK fingerprints via `ProcessCatalog`:
  - all components in one group → adopt that process;
  - components spanning multiple groups → open in **Playground** and log a one-time warning listing the conflicting processes (never silently drop components; migration option: "start a new design or remove conflicting parts").
- `NewProject` sets `ActiveProcess` from the New-Design dialog (or Playground); never leaves it silently unset once a design is in use.

## 9. Relationship to PR #602

- `SinglePdkPolicy` + its 13 tests are **recycled** into `SingleProcessPolicy` (process-keyed). #602's `DesignFileData.ActivePdkName` becomes `ActiveProcess`. The `CanvasInteractionViewModel`/`MainViewModel`/`FileOperationsViewModel` wiring from #602 is the starting scaffold for the placement path.
- **#602 is not merged**; it is closed as superseded once this ships (its branch already carries a stale, too-strict model).

## 10. Non-goals

- FDTD solver wiring (E) — separate spec; will read `ProcessFingerprint`/`ProcessDefinition`.
- A UI to edit tolerances (constants only in v1).
- **Switching the active process of a non-empty design.** The process is chosen at New Design. To change it, start a new design (or use Playground). The indicator is informational, not a mid-design switcher in v1 — this avoids silently invalidating already-placed components.
- Heterogeneous/multi-chip cross-process assembly.
- Auto-detecting a process from arbitrary imported GDS.

## 11. Testing strategy

- `ProcessFingerprintFactory` — extraction from `PdkDraft` (core/cladding material by role, wavelength, thickness; unspecified fallback).
- `ProcessCompatibility.AreCompatible` — categorical equality; thickness/wavelength tolerance boundaries (in/out); unspecified singletons never match.
- `ProcessCatalog` — grouping of multiple PDKs (same group vs distinct); singleton fallback; display-name derivation.
- `SingleProcessPolicy.CheckPlacement` — built-in exemption; Playground/unset bypass; same-group allow; cross-group block with message.
- Migration — legacy inference: single-group adopt; multi-group → Playground + warning.
- Library filter — enabled set follows the active process; Playground retains manual toggles.
- Bundled PDKs parse with valid `process` blocks and group as expected.

## 12. Assumptions

- Bundled PDKs can be given public/generic process fingerprints (no proprietary data required for material names + thickness + wavelength).
- `ProcessDefinition.Materials[role=="core"/"cladding"]` is the canonical source for the material fingerprint; PDKs we populate will follow this convention.
- Default tolerances (5 nm thickness, 40 nm wavelength) are acceptable starting values; tunable via constants.
