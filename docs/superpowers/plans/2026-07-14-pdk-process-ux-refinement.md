# PDK-/Prozess-UX-Feinschliff — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** (1) Save im New-Component-Fenster ohne Preview-Pflicht (Save rendert/validiert selbst); (2) Create-Custom-PDK modus-sauber (UseExisting versteckt den Definitions-Editor wirklich) + „Start from template"-Dropdown im DefineNew; (3) Toolbar-🧱-Button raus, stattdessen „Edit…" pro custom PDK im PDK-Management, Prozess-Editor wird Single-PDK-Editor (ohne Load-preset/Import/New).

**Architecture:** Evolution von #732. `NewComponentViewModel` bekommt `EnsurePreviewAsync` (geteilte Extraktion). `CreateCustomPdkWindow.axaml` bekommt den Wrapper-Fix + Template-Dropdown (via `ProcessDefinitionEditor.Load`). `ProcessManagementViewModel` bekommt `LoadForSinglePdkEdit(PdkDraft)` + `ProcessSaved`-Event; das Fenster verliert Import/New/Preset. `MainWindow` verliert den Toolbar-Button/Command/Handler; PDK-Management-Zeilen (nicht-bundled) bekommen „Edit…".

**Tech Stack:** C#/.NET 10/Avalonia 11/CommunityToolkit.Mvvm; xUnit+Shouldly+Moq.

## Global Constraints
- Keine erfundene Physik; S-Matrix bleibt DI-Service; der Single-PDK-Editor ändert NIE `ActiveProcess` (#726 getrennt).
- Edit nur für custom (nicht-bundled) PDKs; Foundry read-only.
- Kein `Process.Start`; compiled bindings/`x:DataType`; `InvariantCulture`; ≤250 Zeilen neue Dateien, bestehende ≤500 (`LeftPanelViewModel.cs` 429, `MainWindow.axaml.cs` beachten).
- Nur feature-bezogene Dateien; XML-Doku.

## Andockpunkte (verifiziert)
- Toolbar-Button: `MainWindow.axaml:254` (`OpenProcessManagerCommand`); Command `MainViewModel.cs:735-741`; Handler `MainWindow.axaml.cs:179-202` (einziger Auslöser).
- PDK-Liste: `MainWindow.axaml:651-694`, ItemTemplate `:663-682` (`x:DataType="vmLib:PdkInfoViewModel"`, hat `FilePath`/`IsBundled`).
- ProcessWindow: Import `:168-170`, New `:171-173`, Load-preset `:174-183`, Save `:184-186`.
- `ProcessManagementViewModel`: `Load(ProcessDefinition)` `:113-121`; `SaveProcess` `:329-379` (braucht `_memberDrafts[0]` + `PdkFilePathResolver`); `_memberDrafts` privat.
- `NewComponentViewModel`: `CanSave` `Save.cs:23` (`HasPreview && !IsBusy && SelectedCustomPdk`); Save-Early-Return `Save.cs:118-122`; `RunPreview` `.cs:187-209`.
- CreateCustomPdk: Editor-Block `:86-170` trägt `IsVisible`+DataContext-Swap auf DEMSELBEN Element (Laufzeit-Bug); CoreThickness `:73-79`; UseExisting-Combo `:54-68`.

---

### Task 1: Save ohne Preview-Pflicht

**Files:** Modify `.../NewComponentViewModel.cs` + `.Save.cs`; Test `UnitTests/Components/AddCustomComponent/SaveWithoutPreviewTests.cs`

- [ ] **Step 1: Read** `RunPreview` (`.cs:187-209`), `Save` (`Save.cs:108-162`), `CanSave` (`Save.cs:23`).
- [ ] **Step 2: Write failing tests** — (A) OHNE vorherigen Preview-Klick: `SaveCommand.CanExecute` true (PDK gewählt, nicht busy) und `Save` mit gültigem Code (Extractor-Mock liefert Erfolgs-Result) speichert (SavedDraft != null, SavedFilePath gesetzt); (B) `Save` mit Render-FEHLER (Mock liefert `Success=false, Error="SyntaxError: …"`) → kein Save (SavedDraft null), `StatusText` enthält den Fehler; (C) Preview-Klick vorher funktioniert weiter (kein Doppel-Render nötig: wenn `_lastPreview` gültig, nutzt Save ihn direkt — verifiziere via Mock-`Times.Once` über beide Aufrufe).
- [ ] **Step 3: Run → FAIL** (`py .cap-tools/smart_test.py SaveWithoutPreview`).
- [ ] **Step 4: Implement** — private `async Task<bool> EnsurePreviewAsync()`: wenn `_lastPreview is { Success: true }` → true; sonst Extraktion wie `RunPreview` (inkl. `_lastPreview`/`HasPreview`/`PreviewBitmap`/StatusText-Fehler) und Erfolg zurückgeben. `RunPreview` nutzt dieselbe Methode (Invalidate vorher, damit explizites Preview neu rendert). In `Save`: statt Early-Return `if (!await EnsurePreviewAsync()) return;` (innerhalb des IsBusy-Blocks; Reentrancy beachten — `EnsurePreviewAsync` darf den IsBusy-Guard nicht doppelt setzen). `CanSave => !IsBusy && SelectedCustomPdk is not null;` (`OnHasPreviewChanged`-Notify kann bleiben/entfallen — konsistent halten).
- [ ] **Step 5: Run → PASS** + Regression `... NewComponentViewModel`, `... AddCustomComponent`, `... PreviewBitmapAndBlackBoxSave` (Blackbox-Save-Tests dürfen nicht brechen; passe Tests an, die das alte „Render a preview before saving" asserten).
- [ ] **Step 6: Commit** `(=) New Component: Save renders/validates itself; preview is no longer a prerequisite` && `git push`

---

### Task 2: Create-Custom-PDK — Sichtbarkeits-Fix + „Start from template"

**Files:** Modify `CAP.Avalonia/Views/CreateCustomPdkWindow.axaml`, `.../CreateCustomPdkViewModel.cs`; Test `.../CreateCustomPdkTemplateTests.cs`

- [ ] **Step 1: Read** `CreateCustomPdkWindow.axaml` (Editor-Block `:86-170`, CoreThickness `:73-79`), `CreateCustomPdkViewModel.cs`, `ProcessManagementViewModel.Load` (`:113-121`).
- [ ] **Step 2 (Sichtbarkeits-Fix, AXAML):** Den gesamten DefineNew-Bereich (CoreThickness-Feld + Editor-Block + Add-Buttons) in EINEN äußeren Container legen, der NUR `IsVisible="{Binding ProcessSource, Converter=…, ConverterParameter=DefineNew}"` trägt (DataContext = CreateCustomPdkViewModel). Der DataContext-Swap auf `ProcessDefinitionEditor` passiert erst auf einem INNEREN Kind-Element ohne eigenes IsVisible. Gleiches Muster prüfen/fixen für den UseExisting-Bereich. (Ursache dokumentieren: IsVisible auf dem geswappten Element band gegen den falschen DataContext → blieb true.)
- [ ] **Step 3 (Template, VM):** `[ObservableProperty] private ProcessDefinition? _selectedTemplate;` + `partial void OnSelectedTemplateChanged(ProcessDefinition? value)` → wenn value != null: `ProcessDefinitionEditor.Load(value); CoreThicknessNm = value.CoreThicknessNm;` (Editor-Prozessname wird von `Load` gesetzt). XML-Doku: Vorlage ist Startpunkt, frei editierbar.
- [ ] **Step 4 (Template, AXAML):** Im DefineNew-Bereich (äußerer Container, VOR dem Editor-Swap) ein Dropdown „Start from template (optional)": `ItemsSource={Binding AvailableProcesses}`, `SelectedItem={Binding SelectedTemplate}`, ItemTemplate `ProcessDefinition.Name`.
- [ ] **Step 5: Write failing test** — `OnSelectedTemplateChanged`: Auswahl eines Templates füllt `ProcessDefinitionEditor.Layers/Xsections/Materials` + `ProcessName` + `CoreThicknessNm` aus der Vorlage; danach `CreatePdk` (Name gesetzt) erzeugt ein PDK mit dem (ggf. modifizierten) Prozess. → FAIL → implementieren → PASS. `... CreateCustomPdkTemplate` + Regression `... CreateCustomPdk`.
- [ ] **Step 6: Build** `dotnet build -clp:ErrorsOnly` = 0 (XAML). **Commit** `(=) Create Custom PDK: mode visibility fixed (wrapper carries IsVisible) + start-from-template prefill` && `git push`

---

### Task 3: Toolbar-Button raus; „Edit…" pro custom PDK; Single-PDK-Editor

**Files:** Modify `CAP.Avalonia/Views/MainWindow.axaml` (Toolbar `:254` raus; PDK-Zeile `:663-682` Edit-Button), `CAP.Avalonia/Views/MainWindow.axaml.cs` (Handler `:179-202` raus; neuer Edit-Click-Handler), `CAP.Avalonia/ViewModels/MainViewModel.cs` (`OpenProcessManagerCommand`/`ShowProcessManagerRequested` raus), `CAP.Avalonia/ViewModels/ProcessManagementViewModel.cs` (`LoadForSinglePdkEdit` + `ProcessSaved`-Event), `CAP.Avalonia/Views/ProcessManagementWindow.axaml` (Import/New/Preset raus, Titel), Test `UnitTests/ViewModels/SinglePdkEditTests.cs`

- [ ] **Step 1: Read** die Andockpunkte oben + `TemplateEditComponent_Click`-Muster in `MainWindow.axaml.cs` + `SaveProcess` (`:329-379`).
- [ ] **Step 2 (VM):** `public void LoadForSinglePdkEdit(PdkDraft draft)` — analog `OnSelectedPresetChanged`: `Load(draft.Process ?? new ProcessDefinition { Name = draft.Name })`, `_memberDrafts = new List<PdkDraft> { draft }`, `StatusText`-Hinweis. `public event EventHandler? ProcessSaved;` — am Ende von `SaveProcess` nach erfolgreichem `SaveToFile` feuern. XML-Doku (ändert NIE ActiveProcess).
- [ ] **Step 3 (Fenster):** In `ProcessManagementWindow.axaml` die Buttons „Import from PDK…" (`:168-170`), „New" (`:171-173`) und das „Load preset"-Dropdown (`:174-183`) ENTFERNEN. „Save to PDK file…" bleibt. (Das Fenster wird nur noch als Single-PDK-Editor geöffnet.)
- [ ] **Step 4 (Toolbar raus):** `MainWindow.axaml:254`-Button entfernen; `MainViewModel.OpenProcessManagerCommand`+`ShowProcessManagerRequested` entfernen; Handler-Block `MainWindow.axaml.cs:179-202` entfernen. Grep: keine verbleibenden Referenzen.
- [ ] **Step 5 (Edit-Button):** In der PDK-Zeile (`MainWindow.axaml:663-682`) ein kleiner „Edit…"-Button, `IsVisible="{Binding !IsBundled}"`, Click-Handler `PdkEditProcess_Click` in `MainWindow.axaml.cs`: `if (sender is Button { DataContext: PdkInfoViewModel pdk } && !pdk.IsBundled)` → Draft per Name aus `vm.LeftPanel.GetLoadedPdkDrafts().FirstOrDefault(d => d.Name == pdk.Name)` (dieselbe Instanz!); wenn null → return. `var processVm = new ProcessManagementViewModel(new FileDialogService(this)); processVm.PdkFilePathResolver = _ => pdk.FilePath; processVm.ConfirmSaveToPdk = <wie bisheriges Muster>; processVm.LoadForSinglePdkEdit(draft); processVm.ProcessSaved += (_,_) => vm.LeftPanel.ReapplyActiveProcessAfterPdkChange(); new ProcessManagementWindow { DataContext = processVm, Title = $"Edit Process — {pdk.Name}" }.Show(this);` Kein `Process.Start`.
- [ ] **Step 6: Tests** — `LoadForSinglePdkEdit` befüllt Editor (Layers/Xsections/Materials/ProcessName aus Draft-Prozess) und `SaveProcess` schreibt anschließend in den Resolver-Pfad (Temp-Datei) + `ProcessSaved` feuert; Draft ohne Prozess → leerer Editor + kein Crash. → FAIL → implementieren → PASS. Regression `... ProcessManagement` (bestehende Tests: falls welche das entfernte Preset/Import-UI-Verhalten testen → an neue Realität anpassen, VM-Logik `SetAvailablePresets`/`ImportFromPdkCommand` bleibt im VM erhalten [nur UI entfernt] — NICHT VM-Members löschen, minimaler Eingriff).
- [ ] **Step 7: Build** = 0. **Commit** `(~) Process editor becomes a per-PDK editor: toolbar button removed, Edit… on custom PDKs, no preset/import UI` && `git push`

---

### Task 4: E2E + Cleanup

**Files:** Test `UnitTests/Components/AddCustomComponent/PdkUxRefinementFlowTests.cs`

- [ ] **Step 1: Write test** — (a) Save ohne Preview (Mock) speichert; (b) Template-Auswahl befüllt Editor + `CreatePdk` übernimmt Modifikationen; (c) `LoadForSinglePdkEdit`+`SaveProcess` aktualisiert die PDK-Datei (Reload via PdkLoader zeigt geänderte Xsection-Breite). Ein Assert pro Stufe.
- [ ] **Step 2: Run → PASS.** `dotnet build` = 0.
- [ ] **Step 3: Grep-Check** — keine Referenzen auf `OpenProcessManagerCommand`/`ShowProcessManagerRequested`; Dateigrößen (MainWindow.axaml.cs, LeftPanelViewModel ≤500, neue ≤250).
- [ ] **Step 4: Commit** `(+) End-to-end test: save-without-preview, template prefill, per-PDK process edit` && `git push`

---

## Self-Review
- **Spec-Coverage:** Save-ohne-Preview → T1; Sichtbarkeits-Fix + Template → T2; Toolbar raus/Edit pro PDK/Single-PDK-Editor (ohne Preset/Import/New) + Reapply nach Save → T3; E2E → T4.
- **Placeholder:** keine; T3 nennt exakte Zeilen/Callbacks; das Anpassen bestehender Tests ist auf „UI-entfernt, VM-Logik bleibt" eingegrenzt.
- **Typkonsistenz:** `EnsurePreviewAsync` (T1) intern; `SelectedTemplate` (T2) nutzt vorhandenes `AvailableProcesses`; `LoadForSinglePdkEdit`/`ProcessSaved` (T3) ↔ MainWindow-Wiring (T3).
- **Verifikationspunkte:** Reentrancy IsBusy in `EnsurePreviewAsync` (T1); Wrapper-Bindung (T2, Laufzeit-Smoke); `_memberDrafts`-Zugriff via neuer Methode statt privat (T3); `SaveProcess`-owned-Rows-Semantik nach `Load` (markiert alle owned → ganzer Prozess wird geschrieben, gewollt).
