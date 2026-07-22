# New-Component: Fixes + Preview + Edit/Recompute — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Reopen-Bug, fehlender Meep-Fortschritt und Save-schließt-Fenster fixen; Struktur-Preview als inline-Thumbnail + Popup-Viewer (Zoom/Pan); Speichern ohne S-Matrix explizit; Edit/Recompute bestehender custom-Komponenten.

**Architecture:** Evolution von #723/#727. Wiederverwendet `PreviewBitmapFactory`, das Zoom/Pan-Muster aus `DesignCanvas`, das FDTD-Live-Status-Muster aus `ComponentSettingsDialogViewModel.Fdtd.cs`, `NewComponentViewModel.Saved`, und `ComponentTemplate.RawCode`/`RawCodeBackend`.

**Tech Stack:** C#/.NET 10/Avalonia 11/CommunityToolkit.Mvvm; xUnit+Shouldly+Moq.

## Global Constraints
- Keine erfundene Physik; S-Matrix nur echtes FDTD / Blackbox / 2-Port-Ideal; bleibt DI-Service `IFdtdSMatrixService`.
- Edit nur für custom (nicht-bundled) Komponenten; Foundry-PDKs read-only.
- Cross-Platform: kein `Process.Start`; `x:DataType`/compiled bindings; `InvariantCulture`.
- Max. 250 Zeilen/neue Datei; bestehende ≤500 (VM-Partials nutzen). XML-Doku. Nur feature-bezogene Dateien.

## File Structure
- `CAP.Avalonia/ViewModels/Components/AddCustomComponent/NewComponentViewModel*.cs` (MODIFY) — Reopen-Fix, Progress, PreviewBitmap, Save-ohne-Modell, Edit-Modus.
- `CAP.Avalonia/Views/ComponentPreviewWindow.axaml(.cs)` (CREATE) — Popup-Viewer mit Zoom/Pan.
- `CAP.Avalonia/Views/NewComponentWindow.axaml` (MODIFY) — Thumbnail + „Edit"-Titel.
- `CAP.Avalonia/Views/MainWindow.axaml.cs` (MODIFY) — Save-Close, Thumbnail-Klick→Popup, Edit-Einstieg-Wiring.
- `CAP.Avalonia/ViewModels/Panels/LeftPanelViewModel.cs` + Library-View (MODIFY) — „Edit…"-Aktion an custom-Komponenten.
- Tests unter `UnitTests/Components/AddCustomComponent/`.

---

### Task 1: Reopen-Bug — Sentinel-Suppress-Flag

**Files:** Modify `.../NewComponentViewModel.PdkSelection.cs`; Test `UnitTests/Components/AddCustomComponent/NewPdkReopenGuardTests.cs`

**Interfaces:** Consumes `PdkChoices`/`SelectedPdkChoice`/`CreateNewPdk`/`RefreshPdkChoices`/`HandleNewPdkSentinelAsync`.

- [ ] **Step 1: Read** `NewComponentViewModel.PdkSelection.cs` (`OnSelectedPdkChoiceChanged`, `HandleNewPdkSentinelAsync`, `RefreshPdkChoices`).
- [ ] **Step 2: Write failing test** — `NewComponentViewModel` mit `UserPdkStore`(TempRoot, 1 vorhandenes custom PDK) + `CreateNewPdk`-Fake, der einen Zähler hochzählt und ein neues `UserPdkInfo` (echte via `CreateNamedPdkWithProcess` geschriebene Datei) zurückgibt. Setze `SelectedPdkChoice = <Sentinel>`, warte den Task ab; assert: `CreateNewPdk` wurde GENAU EINMAL aufgerufen, `SelectedCustomPdk` ist das neue PDK, `SelectedPdkChoice` ist NICHT der Sentinel.
- [ ] **Step 3: Run → FAIL** (`py .cap-tools/smart_test.py NewPdkReopenGuard`).
- [ ] **Step 4: Implement** — `private bool _suppressSentinelHandling;` In `RefreshPdkChoices` + der anschließenden Ziel-Selektion `_suppressSentinelHandling = true;` setzen (try/finally zurücksetzen). In `OnSelectedPdkChoiceChanged`: früh `if (_suppressSentinelHandling) return;` bevor der Sentinel-Zweig läuft. Zusätzlich: falls das neue PDK nicht gefunden wird (`FirstOrDefault == null`), NICHT auf dem Sentinel hängen bleiben (auf `_previousPdkChoice` oder erstes reales PDK zurückfallen).
- [ ] **Step 5: Run → PASS** + Regression `... NewComponentViewModel`, `... NewComponentNewPdkSentinel`.
- [ ] **Step 6: Commit** `(=) New Component: guard against sentinel re-trigger after PDK refresh (reopen bug)` && `git push`

---

### Task 2: Meep-Live-Fortschritt + Abbruch

**Files:** Modify `.../NewComponentViewModel.Save.cs` (+ ggf. `.cs`); Test `.../ComputeProgressTests.cs`

**Interfaces:** Consumes `IFdtdSMatrixService.SolveAsync(request, IProgress<string>?, CancellationToken)`; `StatusText`; `_computedModel`.

- [ ] **Step 1: Read** `NewComponentViewModel.Save.cs` `ComputeSMatrix`; `ComponentSettingsDialogViewModel.Fdtd.cs` `RunWithLiveStatusAsync`/`Shorten`/`BuildSolverStatus`.
- [ ] **Step 2: Write failing test** — Mock `IFdtdSMatrixService.SolveAsync`, der `progress.Report("Meep step 50%")` aufruft und dann ein Erfolgs-`FdtdSMatrixResult` liefert; nach `ComputeSMatrixCommand.Execute` enthält `StatusText` den gemeldeten Fortschrittstext (oder eine daraus gebaute Zusammenfassung). Zweiter Test: `SolveAsync` wird mit einem NICHT-null `IProgress<string>` aufgerufen (`It.IsAny<IProgress<string>>()` + `It.IsNotNull`).
- [ ] **Step 3: Run → FAIL.**
- [ ] **Step 4: Implement** — In `ComputeSMatrix` ein `Progress<string>` erstellen (→ `StatusText = Shorten(line)` bzw. mit Laufzeit-Präfix), einen `CancellationTokenSource _computeCts` halten und `await _fdtd.SolveAsync(request, progress, _computeCts.Token)` aufrufen. Optional 1-s-`DispatcherTimer`-Heartbeat wie im Vorbild (falls in Tests problematisch: Heartbeat hinter einer kleinen, in Tests umgehbaren Methode kapseln — der Kerntest prüft nur, dass `progress` durchgereicht + gemeldete Zeilen sichtbar werden). Bei Fehler: roher Fehler in `StatusText`, `_computedModel=null` (unverändert ehrlich). Ein `CancelCompute()`/Cleanup, das beim Fensterschließen aufgerufen werden kann (VM-Methode; Wiring optional in Task 4).
- [ ] **Step 5: Run → PASS** + Regression `... NewComponentViewModel`.
- [ ] **Step 6: Commit** `(+) New Component: live Meep progress + cancellation during S-matrix compute` && `git push`

---

### Task 3: PreviewBitmap + Speichern ohne Modell (explizit)

**Files:** Modify `.../NewComponentViewModel.cs` (+ `.Save.cs`); Test `.../PreviewBitmapAndBlackBoxSaveTests.cs`

**Interfaces:** Consumes `PreviewBitmapFactory.FromResult(NazcaPreviewResult, int pixels)`; `_lastPreview.Raw`; `CanSave`; `FdtdSMatrixToDraftConverter.BlackBox()`.

- [ ] **Step 1: Read** `PreviewBitmapFactory.cs` (`FromResult`-Signatur/Null-Verhalten); `NewComponentViewModel.RunPreview`; `CanSave`/`Save`.
- [ ] **Step 2: Write failing test** — (A) Mit gemocktem Renderer, der ein `NazcaPreviewResult` mit ≥1 Polygon liefert: nach `RunPreviewCommand.Execute` ist `PreviewBitmap != null` (headless: falls `FromResult` null liefert, Test tolerant formulieren — assert „RunPreview versucht das Bitmap zu setzen", z.B. über eine injizierbare Factory-Func ODER `PreviewBitmap`-Property existiert und wird nach Erfolg gesetzt/`HasPreview==true`). (B) Save OHNE vorheriges Compute → `SavedDraft.SMatrix == null` (Blackbox) UND `StatusText` enthält einen Blackbox-Hinweis. (C) `CanSave` ist true, sobald Preview+PDK da sind, auch ohne Compute.
- [ ] **Step 3: Run → FAIL.**
- [ ] **Step 4: Implement** — `[ObservableProperty] private Bitmap? _previewBitmap;` (using `Avalonia.Media.Imaging`). In `RunPreview` nach Erfolg `PreviewBitmap = PreviewBitmapFactory.FromResult(result.Raw, 512);` (bei Fehler/kein Preview: `PreviewBitmap = null`). Save-Status: nach erfolgreichem Save `StatusText` klar setzen: `_computedModel is null ? "Saved without simulation model (black box)." : "Saved with FDTD S-matrix."`. `CanSave` unverändert lassen (hängt NICHT an `_computedModel`).
- [ ] **Step 5: Run → PASS** + Regression `... NewComponentViewModel`.
- [ ] **Step 6: Commit** `(+) New Component: render preview thumbnail bitmap; explicit black-box save status` && `git push`

---

### Task 4: `ComponentPreviewWindow` (Zoom/Pan) + Thumbnail-Klick + Save-Close

**Files:** Create `CAP.Avalonia/Views/ComponentPreviewWindow.axaml(.cs)`; Modify `CAP.Avalonia/Views/NewComponentWindow.axaml`, `CAP.Avalonia/Views/MainWindow.axaml.cs`; Test: Build + Regression.

**Interfaces:** Consumes `NewComponentViewModel.PreviewBitmap`, `.Saved`; `DesignCanvas`-Zoom/Pan-Muster.

- [ ] **Step 1: Read** `NewComponentWindow.axaml` (wo das Thumbnail hin passt, nahe Preview/Code); `DesignCanvas.cs` `OnPointerWheelChanged` (Zoom-am-Cursor, delta 1.1/0.9, Clamp 0.1–10) + Pan; `ComponentSettingsDialog.axaml:198` (`<Image Source=…>`-Muster); `MainWindow.axaml.cs` `ShowNewComponentWindowAsync` (Fenster-Instanz `window`, `Saved`-Event).
- [ ] **Step 2: `ComponentPreviewWindow.axaml(.cs)`** — ein Fenster mit einem `Image` (Konstruktor-Param `Bitmap`), umgeben von einem `Border`/`Panel`. Code-behind: `RenderTransform` = `TransformGroup{ScaleTransform sc; TranslateTransform tr}`. `PointerWheelChanged` → Zoom am Cursor (Faktor 1.1/0.9, Clamp 0.1–10, Pan-Korrektur auf Cursor-Fokus, analog `DesignCanvas`). `PointerPressed`(Left)+`PointerMoved`+`PointerReleased` → Drag-Pan (Delta auf `tr`). ≤250 Zeilen. Ctor: `public ComponentPreviewWindow(Bitmap bitmap)`.
- [ ] **Step 3: `NewComponentWindow.axaml`** — ein kleines `Image` (`Source={Binding PreviewBitmap}`, feste Höhe ~120, `Stretch=Uniform`, `Cursor=Hand`, Tooltip „Click to enlarge"), sichtbar nur wenn `PreviewBitmap != null`. (Klick-Handler wird in Task via code-behind/`MainWindow` verdrahtet — da compiled bindings kein Command am Image haben, nutze ein `Button` mit transparentem Style um das Image ODER ein `PointerPressed` im NewComponentWindow-code-behind, das ein Event/Callback am VM auslöst.)
- [ ] **Step 4: Wiring in `MainWindow.axaml.cs`** (`ShowNewComponentWindowAsync`): `newComponentVm.Saved += (_, _) => window.Close();` (Save schließt Fenster). Für den Thumbnail-Klick: einen Callback `newComponentVm.ShowPreviewPopup` (`Action?`) ODER — einfacher — im `NewComponentWindow`-code-behind einen `PreviewImage_PointerPressed`-Handler, der `new ComponentPreviewWindow(vm.PreviewBitmap).Show(this)` öffnet (nur wenn `PreviewBitmap != null`). Wähle den saubersten Weg; kein `Process.Start`.
- [ ] **Step 5: Build** `dotnet build -clp:ErrorsOnly` = 0 (XAML inkl.). Regression `... NewComponentViewModel`.
- [ ] **Step 6: Commit** `(+) ComponentPreviewWindow (mousewheel-zoom + drag-pan); thumbnail click-to-open; Save closes window` && `git push`

---

### Task 5: Edit-Modus im ViewModel

**Files:** Modify `.../NewComponentViewModel*.cs`; Test `.../EditComponentModeTests.cs`

**Interfaces:** Consumes `ComponentTemplate` (`Name`, `RawCode`, `RawCodeBackend`, `PdkSource`), `store.ListCustomPdks()`, `AppendToExistingPdk`.

- [ ] **Step 1: Read** `NewComponentViewModel.cs`/`.Save.cs`/`.PdkSelection.cs` (ctor, `ComponentName`, `Code`, `SelectedBackend`, `SelectedPdkChoice`, Save-Routing); `ComponentTemplate` (RawCode/RawCodeBackend/PdkSource).
- [ ] **Step 2: Write failing test** — `LoadForEdit(ComponentTemplate)`: setzt `ComponentName`, `Code = template.RawCode`, `SelectedBackend` aus `template.RawCodeBackend`, wählt das PDK (per `PdkSource`-Name → passendes `PdkChoice`) und fixiert es; `IsEditMode == true`. Save → `AppendToExistingPdk(<PDK-Datei>, draft)` (überschreibt per Name). Test: nach `LoadForEdit` sind die Felder gesetzt + `IsEditMode`; nach `Save` (Preview gemockt) landet der Draft im richtigen PDK-File.
- [ ] **Step 3: Run → FAIL.**
- [ ] **Step 4: Implement** — `[ObservableProperty] bool _isEditMode;` + `public void LoadForEdit(ComponentTemplate template)` (prefill; PDK-Auswahl über vorhandenes `PdkChoices`/`SelectedPdkChoice`; falls `RawCode` leer → `Code=""` + Status-Hinweis). Save-Pfad unverändert (`AppendToExistingPdk`) — funktioniert für Edit (Overwrite by Name) wie für Append. `WindowTitle`/`SaveButtonLabel`-Properties (z.B. `IsEditMode ? "Edit Component" : "New Component"`), damit Task-6-View sie binden kann.
- [ ] **Step 5: Run → PASS** + Regression `... NewComponentViewModel`, `... AddCustomComponent`.
- [ ] **Step 6: Commit** `(+) New Component: edit mode (LoadForEdit prefill + overwrite existing custom component)` && `git push`

---

### Task 6: Library-„Edit…"-Aktion + Öffnen im Edit-Modus

**Files:** Modify `CAP.Avalonia/ViewModels/Panels/LeftPanelViewModel.cs`, die Library-View (MainWindow.axaml Komponentenliste), `CAP.Avalonia/Views/MainWindow.axaml.cs`, `CAP.Avalonia/Views/NewComponentWindow.axaml` (Titel/Label-Bindings). Test: LeftPanel-seitige Logik.

**Interfaces:** Consumes Task 5 (`LoadForEdit`), `NewComponentWindowLauncher`, `PdkManagerViewModel.IsBundled`, `ComponentTemplate.PdkSource`.

- [ ] **Step 1: Read** wie die PDK-Komponenten in der linken Library gelistet werden (`FilteredTemplates` in `LeftPanelViewModel`, zugehörige `MainWindow.axaml`-Liste) + ob es dort schon ein Kontextmenü gibt; wie `OpenNewComponent` das Fenster öffnet (Task 4/#727-Wiring).
- [ ] **Step 2: Write failing test** — `LeftPanelViewModel`: eine Methode `EditCustomComponent(ComponentTemplate)` / ein `[RelayCommand]`, das nur für custom (nicht-bundled) Templates greift und den Öffnungs-Hook mit einem im Edit-Modus vorbefüllten VM aufruft. Testbar analog zum bestehenden `OpenNewComponent`/`RegisterSavedCustomComponent`-Muster (nur die Bestimmung „ist custom" + Prefill delegieren, nicht das Fenster). Assert: für ein bundled Template passiert nichts / für custom wird der Prefill-Pfad (`LoadForEdit`) angestoßen.
- [ ] **Step 3: Run → FAIL.**
- [ ] **Step 4: Implement** — `[RelayCommand] EditCustomComponent(ComponentTemplate template)` in `LeftPanelViewModel`, das (a) prüft, ob `template` zu einem nicht-bundled PDK gehört (`PdkManager`/`PdkSource`), sonst no-op; (b) über `NewComponentWindowLauncher` ein VM baut, `vm.LoadForEdit(template)` ruft und `ShowNewComponentWindowAsync(vm)` öffnet. Im AXAML: an den Library-Komponenten ein Kontextmenü „Edit…" (nur sichtbar/enabled für custom — z.B. via ein `IsCustom`-Flag am Item-VM oder `PdkManager`-Lookup). `NewComponentWindow.axaml` Titel/Save-Label an `WindowTitle`/`SaveButtonLabel` binden.
- [ ] **Step 5: Build** = 0 Fehler; Regression `... LeftPanel`, `... NewComponentViewModel`.
- [ ] **Step 6: Commit** `(+) Library: 'Edit…' action on custom components opens the assistant in edit mode` && `git push`

---

### Task 7: Integrationstest + Aufräumen

**Files:** Test `.../PreviewEditFlowTests.cs`

- [ ] **Step 1: Write test** — (a) `RunPreview` (gemockt) → `PreviewBitmap` gesetzt/`HasPreview`; (b) `LoadForEdit(template)` prefillt + `Save` überschreibt im PDK (`AppendToExistingPdk`, geladene Datei hat die Komponente 1×); (c) Sentinel feuert nach Refresh nur 1×. Ein Assert pro Stufe.
- [ ] **Step 2: Run → PASS.**
- [ ] **Step 3: Grep-Check** — keine toten Members aus den Umbauten; `NewComponentViewModel`-Dateien ≤250; `dotnet build` 0.
- [ ] **Step 4: Commit** `(+) End-to-end test: preview bitmap + edit-mode overwrite + reopen guard` && `git push`

---

## Self-Review
- **Spec-Coverage:** Reopen-Fix → T1; Progress → T2; PreviewBitmap+Blackbox-Save → T3; Popup-Viewer+Thumbnail+Save-Close → T4; Edit-Modus → T5; Library-Edit-Einstieg → T6; E2E → T7.
- **Placeholder:** T4/T6 lassen den genauen Klick-/Kontextmenü-Andockpunkt bewusst offen mit klarer Anweisung „sauberste Variante, dokumentieren" (AXAML-Bindbarkeit erst am Control entscheidbar); Verhalten + Asserts konkret.
- **Typkonsistenz:** `PreviewBitmap` (T3) → Thumbnail/Popup (T4). `LoadForEdit`/`IsEditMode`/`WindowTitle` (T5) → Library-Einstieg + Titel-Binding (T6). Reopen-Guard (T1) → E2E (T7).
- **Verifikationspunkte:** `PreviewBitmapFactory.FromResult`-Null-Verhalten headless (T3-Test tolerant); `DesignCanvas`-Zoom/Pan-Konstanten (T4); Kontextmenü-Mechanik der Library (T6); `RunWithLiveStatusAsync`-Heartbeat testbar kapseln (T2).
