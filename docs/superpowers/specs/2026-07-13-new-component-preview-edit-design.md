# New-Component-Assistent: Fixes, Preview, Edit/Recompute — Design

**Datum:** 2026-07-13
**Status:** Freigegeben (Brainstorm)
**Baut auf:** #723/#727 (PDK-first-Assistent)

## Ziel

Feld-Test-Feedback am „Neue Komponente"-Assistenten umsetzen: drei Bugfixes, eine visuelle
Struktur-Preview (Thumbnail + Popup-Viewer), klare S-Matrix-Optionalität, und ein
Edit/Recompute-Flow für bestehende custom-Komponenten.

## Umfang

### 1. Bugfixes
- **Reopen-Bug:** Nach „Create PDK" im modalen Prozess-Editor öffnete sich dieser sofort wieder,
  weil nach `RefreshPdkChoices()` die ComboBox den neu angehängten Sentinel „New PDK…" erneut
  selektierte und `HandleNewPdkSentinelAsync` ein zweites Mal feuerte (Guard `IsBusy` war bereits
  false). Fix: dediziertes Suppress-Flag `_suppressSentinelHandling`, das den Sentinel-Zweig
  während Refresh + Ziel-Selektion abschirmt.
- **Compute-Progress:** `ComputeSMatrix` ruft `IFdtdSMatrixService.SolveAsync` bisher ohne
  `IProgress`/`ct`. Übernimm das `RunWithLiveStatusAsync`-Muster aus
  `ComponentSettingsDialogViewModel.Fdtd.cs`: `Progress<string>` (live Meep-Zeilen) + 1-s-Heartbeat
  (Laufzeit) → `StatusText`; `CancellationTokenSource`, das beim Fensterschließen abbricht.
- **Save schließt Fenster:** `NewComponentViewModel.Saved` (existiert) in `MainWindow.axaml.cs` an
  `window.Close()` binden.

### 2. Struktur-Preview: Thumbnail + Popup-Viewer
- VM: `[ObservableProperty] Bitmap? PreviewBitmap`, in `RunPreview` via
  `PreviewBitmapFactory.FromResult(_lastPreview.Raw)` befüllt (Rohdaten liegen in
  `GeometryExtractResult.Raw : NazcaPreviewResult`).
- Fenster: kleines **statisches** `Image` (`PreviewBitmap`). Klick → **Popup-Fenster**
  `ComponentPreviewWindow` mit hochauflösendem Bild und **Mausrad-Zoom am Cursor + Links-Drag-Pan +
  Scrollen** (Zoom/Pan-Logik analog `DesignCanvas.OnPointerWheelChanged`, angewandt auf ein `Image`
  mit `ScaleTransform`+`TranslateTransform`; kein voller `DesignCanvas`).

### 3. Ohne S-Matrix speichern (explizit)
Compute bleibt optional. „Save" ist aktiv, sobald Preview + PDK vorhanden sind (kein Compute-Zwang);
ohne berechnetes Modell wird als Blackbox gespeichert und der Status sagt klar „gespeichert ohne
Simulationsmodell (Blackbox)". Keine erfundene Physik.

### 4. Edit/Recompute bestehender custom-Komponente
- **Einstieg:** custom (nicht-bundled) Komponenten in der linken Library bekommen eine
  **„Edit…"-Aktion** (Kontextmenü). Foundry-Komponenten bleiben read-only (keine Edit-Aktion).
- **Prefill:** öffnet das Fenster im **Edit-Modus** — PDK fix auf das besitzende, `ComponentName`
  gesetzt, `Code` aus `ComponentTemplate.RawCode`, `SelectedBackend` aus `RawCodeBackend`.
- **Speichern** überschreibt die Komponente im selben PDK (`UserPdkStore.AppendToExistingPdk` ersetzt
  per Name). Meep-Recompute wie im Create-Flow (langer Lauf später nachholbar).
- Fenstertitel + Save-Label spiegeln „Edit" vs. „New".

## Architektur / Wiederverwendung

- `PreviewBitmapFactory.FromResult(NazcaPreviewResult, pixels)` (vorhanden) für Thumbnail + Popup.
- Zoom/Pan-Muster aus `DesignCanvas` (`OnPointerWheelChanged`, delta 1.1/0.9, Clamp 0.1–10, Pan über
  Cursor-Fokus) — auf ein `Image` im neuen `ComponentPreviewWindow` übertragen.
- `RunWithLiveStatusAsync`/`Shorten` aus `ComponentSettingsDialogViewModel.Fdtd.cs` (Muster) für den
  Meep-Live-Status.
- `NewComponentViewModel.Saved`-Event → Fenster-Close-Wiring.
- `ComponentTemplate.RawCode`/`RawCodeBackend` (seit #721/#723) für den Edit-Prefill.
- `UserPdkStore.AppendToExistingPdk` (Overwrite-by-Name) für Edit-Save.

## Neue Dateien (jeweils ≤250 Zeilen)
- `CAP.Avalonia/Views/ComponentPreviewWindow.axaml(.cs)` — Popup-Viewer mit Zoom/Pan.
- Ggf. `CAP.Avalonia/Controls/ZoomablePreview.cs` (kleines Control) falls sauberer als Code-behind.

## Fehlerbehandlung
- Compute-Fehler/Abbruch → roher Solver-Fehler bzw. „abgebrochen"; keine Fake-S-Matrix.
- Preview-Render-Fehler → kein Thumbnail, klare Meldung; Save bleibt gesperrt (kein Preview).
- Edit einer Komponente ohne gespeicherten RawCode (z.B. reine Funktionsreferenz-Altbestände) →
  Code-Feld leer + Hinweis; Nutzer kann Code ergänzen. Foundry-Komponenten: keine Edit-Aktion.
- `PreviewBitmapFactory` gibt headless evtl. `null` → Thumbnail-Bereich bleibt leer, kein Crash.

## Testing
- Reopen-Fix: nach simuliertem `CreateNewPdk`-Erfolg + Refresh feuert der Sentinel-Handler NICHT
  erneut (VM-Test mit Zähler auf dem `CreateNewPdk`-Fake).
- Compute-Progress: `SolveAsync` wird mit einem `IProgress<string>` aufgerufen; gemeldete Zeilen
  landen in `StatusText` (Mock-Service, der `progress.Report(...)` aufruft).
- PreviewBitmap: nach `RunPreview` ist `PreviewBitmap != null` (mit gemocktem Renderer, der ein
  `NazcaPreviewResult` mit Polygonen liefert) — bzw. dokumentiert headless-null-tolerant.
- Save ohne Compute → Blackbox-Draft (`SMatrix == null`), StatusText-Hinweis.
- Edit-Modus: `LoadForEdit(template)` prefillt Name/Code/Backend + fixes PDK; Save → `AppendToExistingPdk`.
- Zoom/Pan-Popup: nur Laufzeit (Smoke-Test).

## Cross-Platform / Constraints
`x:DataType`/compiled bindings; kein `Process.Start`; `InvariantCulture`; keine erfundene Physik;
S-Matrix bleibt DI-Service; max. 250 Zeilen/neue Datei, bestehende ≤500 (VM-Partials nutzen).

## Out of Scope (YAGNI)
Prozess bestehender PDKs umstellen (#726); Edit/Delete ganzer PDKs; Pop-out des eingebetteten
Bereichs über das Popup hinaus; Startup-Reload (#700).
