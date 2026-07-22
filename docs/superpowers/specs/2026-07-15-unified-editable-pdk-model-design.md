# Unified, editable PDK / component model

Status: **approved (brainstorming)** — implement autonomously on `feat/pdk-lifecycle-management` (#739).
Owner: PDK/component lifecycle.

## Problem

Today the app treats PDKs as two conceptual kinds, but they are the **same JSON format**
(`PdkLoader`/`PdkDraft`): "bundled" (`CAP-DataAccess/PDKs/*.json`, `IsBundled=true`, read-only)
vs "user" (`user-pdks/*.json`, editable). The only difference is a boolean flag. This forces two
overlapping editing paths that confuse users:

- **Component Settings** = per-instance Nazca *override* + S-matrix per-wavelength view/import.
- **Edit Component** = edit the component *definition* (custom PDKs only).

For a custom component both exist and do "the same" thing at different scopes; for bundled
components override was the only way to change anything. Photonic users don't want per-placement
geometry overrides — they want named component variants. And measured-S-matrix-from-file (today
only in Component Settings) is valuable for *every* component.

## Target model

**One PDK concept. Everything is editable.** The shipped PDKs are just pre-installed templates.

1. **Copy-to-user on first edit.** Bundled PDKs stay pristine, resettable templates in the app
   dir (update-safe). Editing a bundled component/PDK forks the PDK into `user-pdks/` (via
   `UserPdkStore`), and edits thereafter live there. The library entry switches from the bundled
   copy to the user copy. Bundled original remains as reference / "reset to original".
2. **One component editor.** Merge the two dialogs into a single per-component editor with:
   structure / Nazca code (from the New Component editor) + **S-matrix (visual per-wavelength view
   + import-from-file)** (from Component Settings) + parameters — available for **all** components.
   The per-instance Nazca *override* is retired.
3. **Enable All / Disable All** operate on the currently *allowed* set: Enable All enables every
   process-compatible PDK (the ones not locked out by the active process); Disable All disables
   all. (Not a playground gray-out bug — a semantics fix.)

## Backward compatibility

- Existing `.lun` files that stored per-instance Nazca overrides (`NazcaCodeOverride`) still
  **load and apply** at simulation time — we only remove the UI to *create* new ones. The
  `InstanceNazcaOverrideViewModel`/persistence stay (they already note this).
- Bundled JSONs are never written in place; forking copies to `user-pdks/`.

## Decomposition (independent, sequenced; each its own commit)

**Piece 1 — Enable All / Disable All semantics.** `PdkManagerViewModel.EnableAll/DisableAll`
enable/disable only the toggleable (allowed) PDKs, not process-locked ones. Small, isolated. Tests.

**Piece 2 — Bundled editability via copy-to-user-on-edit.**
- `CanEditTemplate` / the edit + delete affordances apply to **all** components (drop the
  `IsBundled` gate on *editing*; keep bundled files themselves read-only).
- New `UserPdkStore.ForkBundledPdk(bundledFilePath)` (and/or per-component fork): copies the
  bundled PDK JSON into `user-pdks/` under a non-colliding name, returns the new path.
- Edit flow: when the target component's PDK is bundled, fork first, then edit the user copy;
  re-register the user PDK and (optionally) hide/replace the bundled entry for that PDK.
- Tests: fork copies + is editable; bundled original untouched; re-registration.

**Piece 3 — One component editor + S-matrix everywhere + retire override.**
- Fold the S-matrix per-wavelength visual + `Import S-parameters from file` into the component
  editor (the structure/Nazca editor), for all components.
- Route "Component Settings…" and "Edit…" / ✏ to the single editor.
- Remove the per-instance Nazca-override editor UI from Component Settings (keep load/apply of
  stored overrides for old designs).
- Keep the scope banners only where still meaningful.

## Testing / verification

Each piece: `dotnet build` 0/0, targeted unit tests, and the headless UI-screenshot harness
(`Category=UiScreenshots`) updated so the unified editor renders. CI green before moving on.

## Non-goals (YAGNI)

- No new PDK *import* pipeline changes (import already produces the same JSON).
- Groups stay as-is (composition of components is already a separate, working concept).
- No migration of the on-disk JSON schema.
