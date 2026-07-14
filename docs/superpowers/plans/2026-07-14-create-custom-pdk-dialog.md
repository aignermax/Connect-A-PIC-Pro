# Getrennter „Create Custom PDK"-Dialog + Sichtbarkeits-Fix — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Anlegen (neuer schlanker „Create Custom PDK"-Dialog: Prozess übernehmen ODER neu definieren, modus-sauber) von Editieren (bestehender Prozess-Editor) trennen; den by-name-Sichtbarkeits-Bug bei per-Preset übernommenem Prozess by-value reparieren; nazca-Beispielcode korrigieren.

**Architecture:** Evolution von #723/#727/#729. Neuer `CreateCustomPdkViewModel`/`CreateCustomPdkWindow`; der `IsPdkCreationMode`-Aufsatz wird aus `ProcessManagementViewModel`/`ProcessManagementWindow` entfernt. Sichtbarkeit über by-value-Neuauflösung der aktiven Prozess-Mitgliedschaft nach PDK-Anlage. Wiederverwendet `UserPdkStore.CreateNamedPdkWithProcess`, `ProcessManagementViewModel`-Definitions-Editier-Collections (`Layers`/`Xsections`/`Materials`/`ToProcess()`), `ProcessCompatibility`/`ProcessCatalog`.

**Tech Stack:** C#/.NET 10/Avalonia 11/CommunityToolkit.Mvvm; xUnit+Shouldly+Moq.

## Global Constraints
- Keine erfundene Physik; S-Matrix bleibt DI-Service `IFdtdSMatrixService`.
- Foundry-JSONs nie geschrieben; custom PDKs nur unter `%LOCALAPPDATA%/Lunima/user-pdks/`.
- Cross-Platform: kein `Process.Start`; `x:DataType`/compiled bindings; `InvariantCulture`.
- Max. 250 Zeilen/neue Datei; bestehende ≤500. XML-Doku. Nur feature-bezogene Dateien.
- Out of scope: #726, #730, #700.

## File Structure
- `CAP.Avalonia/ViewModels/Components/AddCustomComponent/BackendCodeExamples.cs` (MODIFY) — nazca-Beispiel.
- `CAP.Avalonia/ViewModels/Panels/LeftPanelViewModel*.cs` (MODIFY) — Sichtbarkeits-Reapply by-value.
- `CAP.Avalonia/ViewModels/Components/AddCustomComponent/CreateCustomPdkViewModel.cs` (CREATE).
- `CAP.Avalonia/Views/CreateCustomPdkWindow.axaml(.cs)` (CREATE).
- `CAP.Avalonia/Views/MainWindow.axaml.cs` (MODIFY) — `CreateNewPdk`-Hook öffnet neuen Dialog.
- `CAP.Avalonia/ViewModels/ProcessManagementViewModel.PdkCreation.cs` (DELETE) + `ProcessManagementWindow.axaml` (MODIFY) — Creation-Mode-Rückbau.
- Tests unter `UnitTests/`.

---

### Task 1: nazca-Beispielcode-Fix

**Files:** Modify `.../BackendCodeExamples.cs`; Test `UnitTests/Components/AddCustomComponent/BackendCodeExamplesNazcaTests.cs`

- [ ] **Step 1: Read** `BackendCodeExamples.cs` + `scripts/render_component_preview.py` `_build_cell_from_code_file` (Contract: Funktion `component()` → Cell, oder Var `cell`).
- [ ] **Step 2: Write failing test** — `BackendCodeExamples.Nazca` enthält `def component()` UND `nd.Cell` UND `return`; enthält NICHT `component = nd.Cell` (die alte falsche Form). `BackendCodeExamples.For(GeometryBackend.Nazca)` == `Nazca`.
- [ ] **Step 3: Run → FAIL** (`py .cap-tools/smart_test.py BackendCodeExamplesNazca`).
- [ ] **Step 4: Implement** — `Nazca`-Konstante auf:
  `"import nazca as nd\n\ndef component():\n    with nd.Cell(name='my_component') as c:\n        nd.strt(length=20, width=0.5).put(0)\n        nd.Pin('a0').put(0, 0, 180)\n        nd.Pin('b0').put(20, 0, 0)\n    return c"`
- [ ] **Step 5: Run → PASS** + Regression `... BackendCodeAutoload`, `... NewComponentViewModel`.
- [ ] **Step 6: Commit** `(=) nazca example code: define a component() function per the render contract` && `git push`

---

### Task 2: Prozess-Sichtbarkeit by-value reparieren (Kern-Bug)

**Files:** Modify `CAP.Avalonia/ViewModels/Panels/LeftPanelViewModel*.cs` (+ ggf. `MainViewModel`/`PdkManagerViewModel`); Test `UnitTests/.../CustomPdkVisibilityTests.cs`

**Interfaces:** `ProcessCatalog.BuildGroups`, `ProcessCompatibility.AreCompatible`, `PdkManagerViewModel.ApplyProcessLock(IEnumerable<string>)`/`GetEnabledPdkNames`, `LeftPanelViewModel.GetLoadedPdkDrafts()`/`ReapplyActiveProcessAfterPdkChange()`/`ApplyActiveProcess`, `FileOperationsViewModel.ActiveProcess`/`ActiveProcessSelection`, `RegisterSavedCustomComponent`.

- [ ] **Step 1: Read** `LeftPanelViewModel.ApplyActiveProcess`/`ReapplyActiveProcessAfterPdkChange`/`RegisterSavedCustomComponent`, `PdkManagerViewModel.ApplyProcessLock`/`GetEnabledPdkNames`, `ProcessCatalog.BuildGroups`, `ProcessCompatibility.AreCompatible`, `ActiveProcessResolver`. Verstehe: `ApplyProcessLock` erlaubt nur Namen aus dem gespeicherten `MemberPdkNames`-Snapshot; ein neues wertkompatibles PDK fehlt dort.
- [ ] **Step 2: Write failing test** — VM-Level: aktiver Prozess = P (z.B. via geladenem PDK „Foundry" mit Prozess-Fingerprint f); ein neues custom PDK „MyLib" mit einem WERTKOMPATIBLEN Prozess (gleiche CoreMaterial/Cladding/Thickness/Wavelength) wird registriert (`RegisterSavedCustomComponent` bzw. der neue Anlege-Pfad). Nach dem Reapply: `PdkManager` hat „MyLib" enabled (nicht `IsLockedByProcess`), und eine „MyLib"-Komponente ist in `FilteredTemplates`. (Baue den Test gegen die realen VM-Konstruktion aus bestehenden `LeftPanel`-Tests.)
- [ ] **Step 3: Run → FAIL** (aktuell: MyLib locked/gefiltert).
- [ ] **Step 4: Implement** — In `ReapplyActiveProcessAfterPdkChange()` (bzw. dem nach PDK-Anlage aufgerufenen Pfad): statt `ApplyProcessLock(snapshot.MemberPdkNames)` die erlaubten Namen **by-value** neu bestimmen — Katalog aus `GetLoadedPdkDrafts()` bauen (`ProcessCatalog.BuildGroups`), die Gruppe finden, die mit dem aktiven Prozess kompatibel ist (`ProcessCompatibility.AreCompatible`), und deren `MemberPdkNames` (Live, inkl. neuem PDK) + prozess-agnostische an `ApplyProcessLock` geben. So werden wertkompatible neue PDKs automatisch erlaubt. Der gespeicherte Snapshot bleibt für Persistenz unangetastet; nur die Laufzeit-Lock-Menge wird live/by-value berechnet.
- [ ] **Step 5: Run → PASS** + Regression `... LeftPanel`, `... ProcessManagement`, `... AddCustomComponent`.
- [ ] **Step 6: Commit** `(=) Custom PDK visibility: allow value-compatible PDKs under the active process lock (live catalog, not stale name snapshot)` && `git push`

---

### Task 3: `CreateCustomPdkViewModel`

**Files:** Create `CAP.Avalonia/ViewModels/Components/AddCustomComponent/CreateCustomPdkViewModel.cs`; Test `.../CreateCustomPdkViewModelTests.cs`

**Interfaces:** `UserPdkStore.CreateNamedPdkWithProcess(pdkName, ProcessDefinition, backend, xs)`/`NamedPdkExists`; verfügbare Prozesse (aus `LeftPanelViewModel.GetLoadedPdkDrafts()` gefiltert `Process != null`); Prozess-Definition „define new" via komponiertem `ProcessManagementViewModel` (`Layers`/`Xsections`/`Materials`/`NewProcess()`/`ToProcess()`) ODER direkt.

**Produces:**
- `enum PdkProcessSource { UseExisting, DefineNew }`
- `[ObservableProperty] string PdkName`, `PdkProcessSource ProcessSource` (Default UseExisting), `ProcessDefinition? SelectedExistingProcess`, `IReadOnlyList<ProcessDefinition> AvailableProcesses`.
- Für DefineNew: eine eingebettete Prozess-Definitions-Quelle `public ProcessManagementViewModel ProcessDefinitionEditor { get; }` (im „Definitions-only"-Zustand: `NewProcess()` initial; die Edit-bestehend-Gadgets werden in der View ausgeblendet, nicht hier).
- `[RelayCommand(CanExecute=nameof(CanCreate))] CreatePdk` → baut `process = ProcessSource==UseExisting ? SelectedExistingProcess! : ProcessDefinitionEditor.ToProcess()`; Kollision via `NamedPdkExists` (Meldung + return); sonst `Created = store.CreateNamedPdkWithProcess(PdkName, process, "gdsfactory", null)` (Pfad) + `PdkCreated`-Event/Result. `CanCreate => !string.IsNullOrWhiteSpace(PdkName) && (ProcessSource==DefineNew || SelectedExistingProcess != null)`.
- `public event EventHandler<string>? PdkCreated;` (Pfad).
- ctor: `(UserPdkStore store, IReadOnlyList<ProcessDefinition> availableProcesses, ProcessManagementViewModel processDefinitionEditor)`.

- [ ] **Step 1: Read** `UserPdkStore.CreateNamedPdkWithProcess`/`NamedPdkExists`, `ProcessManagementViewModel` (`NewProcess`/`ToProcess`/`Layers`/`Xsections`/`Materials`), `ProcessDefinition`.
- [ ] **Step 2: Write failing tests** — (A) UseExisting + Name → `CreatePdk` ruft `CreateNamedPdkWithProcess` mit dem gewählten Prozess, feuert `PdkCreated(path)`; (B) DefineNew → nutzt `ProcessDefinitionEditor.ToProcess()`; (C) `CanCreate` false bei leerem Namen bzw. UseExisting ohne Auswahl; (D) Kollision (`NamedPdkExists`) → kein Create, Meldung. Mocke/temp-store `UserPdkStore`; `ProcessManagementViewModel` echt (Fake `IFileDialogService`).
- [ ] **Step 3–5:** FAIL → implementieren → PASS + Regression. ≤250 Zeilen.
- [ ] **Step 6: Commit** `(+) CreateCustomPdkViewModel: name + adopt-existing/define-new process -> create user PDK` && `git push`

---

### Task 4: `CreateCustomPdkWindow` + `CreateNewPdk`-Wiring

**Files:** Create `CAP.Avalonia/Views/CreateCustomPdkWindow.axaml(.cs)`; Modify `CAP.Avalonia/Views/MainWindow.axaml.cs`. Build/Regression.

**Interfaces:** Task 3 (`CreateCustomPdkViewModel`), `NewComponentViewModel.CreateNewPdk : Func<Task<UserPdkInfo?>>?`, `UserPdkStore.ListCustomPdks`.

- [ ] **Step 1: Read** `MainWindow.axaml.cs` (`ShowNewComponentWindowAsync`, aktueller `CreateNewPdk`-Hook, der bisher das ProcessManagementWindow modal öffnete — der wird ersetzt), `ProcessManagementWindow.axaml` (als Vorlage für die Layer/Xsection/Material-Grids, die im „Define new"-Bereich gezeigt werden), `HelpFlyoutButton`/`FileDialogService`-Muster.
- [ ] **Step 2: `CreateCustomPdkWindow.axaml`** — `x:DataType="vm:CreateCustomPdkViewModel"`. Layout: „PDK name"-TextBox; RadioButtons „Use existing"/„Define new" (an `ProcessSource`, via `EnumToBooleanConverter` — der wurde in #723 entfernt; falls nötig neu/klein wieder anlegen ODER zwei bool-Properties); „Use existing"-Dropdown (`AvailableProcesses`/`SelectedExistingProcess`, sichtbar bei UseExisting); „Define new"-Bereich (sichtbar bei DefineNew): die Prozess-Definitions-Grids aus `ProcessDefinitionEditor` (Prozessname + Layers/Xsections/Materials) — OHNE Import/Save-to-file/preset/New-Reset-Buttons; Cancel + „Create PDK" (`CreatePdkCommand`).
- [ ] **Step 3: `MainWindow.axaml.cs`** — im `ShowNewComponentWindowAsync`-Lambda `newComponentVm.CreateNewPdk` neu verdrahten: baut `CreateCustomPdkViewModel` (store + verfügbare Prozesse aus `vm.LeftPanel.GetLoadedPdkDrafts()` + ein `new ProcessManagementViewModel(new FileDialogService(this), <importers>, new PdkJsonSaver())` als DefinitionEditor), öffnet `CreateCustomPdkWindow` **modal** (`ShowDialog(newComponentWindow)`), schließt es bei `PdkCreated`, und gibt `ListCustomPdks().FirstOrDefault(FilePath==createdPath)` zurück (sonst null). Kein `Process.Start`.
- [ ] **Step 4: Build** `dotnet build -clp:ErrorsOnly` = 0 (XAML inkl.). Regression `... NewComponentViewModel`, `... CreateCustomPdkViewModel`.
- [ ] **Step 5: Commit** `(+) CreateCustomPdkWindow (adopt/define process, no edit-existing gadgets) + wire New PDK modal` && `git push`

---

### Task 5: Creation-Mode-Rückbau im Prozess-Editor

**Files:** Delete `CAP.Avalonia/ViewModels/ProcessManagementViewModel.PdkCreation.cs`; Modify `ProcessManagementWindow.axaml` (Top-„PDK name"+„Create PDK" entfernen); Delete/anpassen `UnitTests/ViewModels/ProcessManagementPdkCreationTests.cs`. Regression.

- [ ] **Step 1: Read** `ProcessManagementViewModel.PdkCreation.cs` (was es hinzufügt: `IsPdkCreationMode`/`PdkName`/`CreateUserPdk`/`PdkNameExists`/`PdkCreated`/`EnterPdkCreationMode`/`CreatePdk`), `ProcessManagementWindow.axaml:18-24` (Top-Leiste), `MainWindow.axaml.cs` (dass der alte Creation-Aufruf durch Task 4 ersetzt ist — keine verwaisten Referenzen).
- [ ] **Step 2:** Entferne die Partial-Datei + die Top-Leiste im AXAML + die zugehörigen Tests. Prüfe per Grep, dass keine Referenz auf `IsPdkCreationMode`/`EnterPdkCreationMode`/`CreateUserPdk`/`CreatePdkCommand` mehr existiert (außer im neuen CreateCustomPdk-Code, falls Namensgleichheit — dann eindeutig trennen).
- [ ] **Step 3: Build** = 0 Fehler. Regression `... ProcessManagement` (die verbleibenden Editor-Tests grün).
- [ ] **Step 4: Commit** `(-) Process editor: remove PDK-creation mode (creation now a dedicated dialog)` && `git push`

---

### Task 6: Integrationstest + Aufräumen

**Files:** Test `UnitTests/.../CreateCustomPdkFlowTests.cs`

- [ ] **Step 1: Write test** — (a) `CreateCustomPdkViewModel` UseExisting(wertkompatibler Prozess) → `CreatePdk` → `ListCustomPdks()` enthält das PDK; (b) danach ist das PDK unter dem aktiven kompatiblen Prozess enabled + Komponente sichtbar (Sichtbarkeits-Fix, ggf. via LeftPanel-Reapply); (c) nazca-Beispiel enthält `def component()`. Ein Assert pro Stufe.
- [ ] **Step 2: Run → PASS.**
- [ ] **Step 3: Grep-Check** — keine verwaisten `IsPdkCreationMode`/creation-mode-Reste; neue Dateien ≤250; `dotnet build` 0.
- [ ] **Step 4: Commit** `(+) End-to-end test: create custom PDK dialog + value-compatible visibility` && `git push`

---

## Self-Review
- **Spec-Coverage:** nazca → T1; Sichtbarkeit by-value → T2; Create-Dialog (adopt/define, modus-sauber) → T3/T4; Editor-Rückbau → T5; E2E → T6.
- **Placeholder:** T4 lässt Enum↔bool-Bindung (Converter vs. zwei bools) + genaue Grid-Einbettung bewusst offen mit klarer Anweisung; Verhalten/Asserts konkret.
- **Typkonsistenz:** `CreateNamedPdkWithProcess` (T3) ↔ Store; `CreateCustomPdkViewModel.PdkCreated`/`Created` (T3) ↔ MainWindow-Hook (T4); Sichtbarkeits-Reapply (T2) ↔ E2E (T6).
- **Verifikationspunkte:** `ProcessCatalog.BuildGroups`/`ProcessCompatibility.AreCompatible`-Signaturen (T2); `ProcessManagementViewModel`-Einbettung im Create-Dialog ohne Edit-Gadgets (T4); Enum↔bool-Bindung (T4).
