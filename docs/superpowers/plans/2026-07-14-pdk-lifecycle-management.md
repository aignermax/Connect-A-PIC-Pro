# PDK-Lifecycle-Management — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** (1) #700 — User-PDKs beim Start laden (fixt die fehlenden PDKs in der Management-Liste); (2) „+"-Button im PDK-Management (PDK ohne Komponente anlegen); (3) Layer-Stack wird Teil der Live-Prozess-Kompatibilität + Divergenz-Warnung nach Prozess-Edit + Design-Check-Konflikt-Anzeige; (4) Löschen von custom PDKs/Komponenten mit Confirm + `.trash`-Papierkorb.

**Architecture:** Evolution von #732/#736. Startup-Reload nutzt den bestehenden `LoadPdkFromJsonFileAsync`-Registrierpfad. Layer-Konsistenz als NEUE Prüfung `ProcessCompatibility.LayersConsistent` NUR in der Live-Auflösung (`ResolveLiveMemberPdkNames`) — kein Persistenzformat-Change; alle #736-Consumer (Platzierung/Paste/Gruppen/AiGrid/Metall) erben automatisch. Divergenz-Warnung im `ProcessSaved`-Wiring (MainWindow). Papierkorb im `UserPdkStore` (`MoveToTrash`/`RemoveComponent`).

**Tech Stack:** C#/.NET 10/Avalonia 11/CommunityToolkit.Mvvm; xUnit+Shouldly+Moq.

## Global Constraints
- #570 wird STRENGER, nie lockerer: Layer-Konsistenz darf nur ausschließen, nie zusätzlich erlauben. Zusätzliche Layer (Metall-Ergänzung, #734-Workflow) bleiben kompatibel.
- Platzierte Komponenten werden NIE still entfernt (nur Warnung + Design-Check-Konflikt).
- Foundry-JSONs nie geschrieben/gelöscht; Delete nur custom; `.trash` unter `user-pdks/.trash/`.
- Kein `Process.Start`; compiled bindings; `InvariantCulture` (Timestamps im Trash-Namen invariant, z.B. `yyyyMMdd-HHmmss`); ≤250 Zeilen neue Dateien, bestehende ≤500 (`LeftPanelViewModel*`, `MainWindow.axaml.cs` grandfathered). XML-Doku.

## Verifizierte Andockpunkte
- `UserPreferencesService.GetUserPdkPaths()` (`CAP.Avalonia/Services/UserPreferencesService.cs:144`) — kein Aufrufer. `AddUserPdkPath` `:152`.
- `LeftPanelViewModel.LoadPdkFromJsonFileAsync` (~Z361-405, Registrier-Muster) + `RegisterSavedCustomComponent`/`CustomComponentLibraryRegistrar` + `ReapplyActiveProcessAfterPdkChange` (ProcessLock.cs).
- `UserPdkStore` (`CAP-DataAccess/Components/AddCustomComponent/UserPdkStore.cs`): `ListCustomPdks` (nur `Process != null`!), `_root`, `Slug`, `ResolveNamedPath`.
- `ProcessCompatibility` (`Connect-A-Pic-Core/Components/Process/ProcessCompatibility.cs`), `ResolveLiveMemberPdkNames` (`LeftPanelViewModel.ProcessLock.cs:~94-104`, per-PDK `AreCompatible`), `ProcessDefinition.Layers` (`ProcessLayer{Name,Layer,Datatype,…}`).
- PDK-Zeile: `MainWindow.axaml:~651-694` (ItemTemplate `PdkInfoViewModel` mit `IsBundled`/`FilePath`, „Edit…"-Button aus #733); `PdkEditProcess_Click`-Muster + `_openPdkEditWindows` (`MainWindow.axaml.cs`).
- `ProcessSaved`-Wiring (`MainWindow.axaml.cs`, `PdkEditProcess_Click`): `ProcessSaved += (_,_) => vm.LeftPanel.ReapplyActiveProcessAfterPdkChange();`.
- Library-Kontextmenü „Edit…" (`MainWindow.axaml`, `TemplateEditComponent_Click`-Muster; `ComponentTemplate.IsCustom`).
- Design-Checks: `CAP.Avalonia/Views/Panels/DesignChecksPanel.axaml` + zugehöriges VM — Implementer liest die bestehende Check-Struktur und ergänzt einen Check.
- Create-Dialog-Wiring: `CreateNewPdk`-Hook in `MainWindow.axaml.cs` (öffnet `CreateCustomPdkWindow` modal).

---

### Task 1: #700 — Startup-Reload der User-PDKs

**Files:** Modify `CAP.Avalonia/ViewModels/Panels/LeftPanelViewModel.AddCustomComponent.cs` (oder neues kleines Partial) + Aufruf beim App-Start (dort, wo bundled PDKs geladen werden — Implementer findet die Stelle via `LoadPdk`-Init-Pfad/`MainViewModel`-Init); ggf. `UserPreferencesService` (Pfad-Bereinigung). Test `UnitTests/Components/AddCustomComponent/UserPdkStartupReloadTests.cs`.

- [ ] **Step 1: Read** `LeftPanelViewModel.LoadPdkFromJsonFileAsync` (Registrier-Kette), `CustomComponentLibraryRegistrar`, `UserPdkStore.ListCustomPdks` (nur Process!=null — für die Management-Liste sollen auch process-lose User-PDKs erscheinen: nutze zusätzlich einen Directory-Scan oder erweitere tolerant), `GetUserPdkPaths`/`AddUserPdkPath`, App-Start-Ladepfad der bundled PDKs.
- [ ] **Step 2: Write failing test** — `LeftPanelViewModel` (Test-Konstruktion wie bestehende Tests) + Temp-user-pdks-Root mit 2 PDK-Dateien (eine mit Komponenten, eine leer/ohne Komponenten) + 1 Prefs-Pfad außerhalb + 1 Prefs-Pfad auf nicht-existente Datei: nach `ReloadUserPdksAtStartup()` (neuer Name) sind beide Dir-PDKs + das Prefs-PDK in `PdkManager.LoadedPdks` (nicht-bundled) und die Komponenten in `AllTemplates`; der tote Pfad crasht nicht und ist aus den Prefs entfernt; doppelte Pfade (Dir+Prefs) nur einmal registriert; Lock-Reapply gelaufen.
- [ ] **Step 3: FAIL → implementieren → PASS.** Methode `internal void ReloadUserPdksAtStartup(string? userPdkRootOverride = null)` (Override für Tests; default `UserPdkStore`-Root): Pfade sammeln (Dir-Scan `*.json` + `GetUserPdkPaths()`), dedupe (OrdinalIgnoreCase, Vollpfad), je Pfad tolerant `LoadFromFileForEditing` + Registrier-Kette wie `LoadPdkFromJsonFileAsync` (OHNE erneutes `AddUserPdkPath` für Dir-Funde nötig — schadet aber nicht; Implementer entscheidet konsistent), fehlende/kaputte Dateien überspringen + Prefs bereinigen; am Ende EIN `ReapplyActiveProcessAfterPdkChange()` + `FilterComponents()`. App-Start-Aufruf an der gefundenen Init-Stelle NACH den bundled PDKs.
- [ ] **Step 4:** Regression `... LeftPanel`, `... AddCustomComponent`, `... CustomPdkVisibility`. Build 0.
- [ ] **Step 5: Commit** `(=) #700: reload user PDKs at startup (dir scan + remembered import paths)` && `git push`

---

### Task 2: „+"-Button im PDK-Management

**Files:** Modify `MainWindow.axaml` (PDK-Management-Header, „+"-Button), `MainWindow.axaml.cs` (Click-Handler öffnet `CreateCustomPdkWindow` modal — denselben Aufbau wie der `CreateNewPdk`-Hook, aber Owner=MainWindow; nach Erfolg: leeres PDK registrieren). Ggf. kleine Registrier-Hilfe in `LeftPanelViewModel` (`RegisterCreatedPdk(string filePath)`: Draft laden, `LoadedPdks`-Registrierung + Drafts + Reapply — OHNE Komponenten-Template). Test für die Registrier-Hilfe.

- [ ] **Step 1: Read** den bestehenden `CreateNewPdk`-Hook in `MainWindow.axaml.cs` (CreateCustomPdkViewModel/Window-Aufbau) + `CustomComponentLibraryRegistrar`.
- [ ] **Step 2:** `LeftPanelViewModel.RegisterCreatedPdk(string filePath)` (testbar): lädt Draft, registriert PDK (Name, filePath, isBundled=false, componentCount=Components.Count), fügt Draft zu `_loadedPdkDrafts`, Reapply+Filter. Test: leeres PDK-File → erscheint in `LoadedPdks` mit 0 Komponenten.
- [ ] **Step 3:** „+"-Button im PDK-Management-Header (`MainWindow.axaml`), Handler `PdkCreate_Click` in `MainWindow.axaml.cs`: baut CreateCustomPdkViewModel/Window wie der Hook (verfügbare Prozesse aus `GetLoadedPdkDrafts()`), `ShowDialog(this)`; bei `PdkCreated(path)` → `vm.LeftPanel.RegisterCreatedPdk(path)`.
- [ ] **Step 4:** Build 0; Regression `... CreateCustomPdk`, `... LeftPanel`. **Commit** `(+) PDK management: '+' creates a custom PDK directly (no component required)` && `git push`

---

### Task 3: Layer-Konsistenz in der Live-Kompatibilität

**Files:** Modify `Connect-A-Pic-Core/Components/Process/ProcessCompatibility.cs` (+`LayersConsistent`), `CAP.Avalonia/ViewModels/Panels/LeftPanelViewModel.ProcessLock.cs` (`ResolveLiveMemberPdkNames`). Test `UnitTests/.../LayerConsistencyTests.cs`.

- [ ] **Step 1: Read** `ProcessCompatibility.cs`, `ProcessLayer` (Name/Layer/Datatype), `ResolveLiveMemberPdkNames` + `GetLoadedPdkProcessEntries` (liefert Fingerprints — für die Layer-Prüfung braucht die Auflösung Zugriff auf die `ProcessDefinition`en: nutze `GetLoadedPdkDrafts()` direkt).
- [ ] **Step 2: Write failing tests** — `LayersConsistent(a,b)`: (A) gleicher Layer-Name, andere Nummer (NITRIDE 203 vs 2030) → false; (B) b hat ZUSÄTZLICHEN Layer (Metall) → true; (C) disjunkte/leere Layer-Listen → true (nichts widerspricht); (D) case-insensitive Namen; (E) gleicher Name gleiche Nummer anderes Datatype → false. Und `ResolveLiveMemberPdkNames`-Integration: Referenz = Prozessdefinition eines GELADENEN Snapshot-Mitglieds (erstes gefundene; wenn keines geladen → nur Fingerprint wie bisher): renumbered PDK fällt aus der Live-Menge (und damit Platzierung, via bestehendem #736-Pfad), Metall-ergänztes bleibt drin.
- [ ] **Step 3: FAIL → implementieren → PASS.** `public static bool LayersConsistent(ProcessDefinition? a, ProcessDefinition? b)` (null → true). In `ResolveLiveMemberPdkNames`: Referenzdefinition bestimmen (erstes geladenes PDK, dessen Name im Snapshot `active.MemberPdkNames` steht und dessen `Process != null`); Kandidaten müssen `AreCompatible` UND `LayersConsistent(reference, candidate.Process)` erfüllen (Referenz null → nur Fingerprint). XML-Doku (strenger, nie lockerer; Additions erlaubt).
- [ ] **Step 4:** Regression `... CustomPdkVisibility`, `... SingleProcessPolicy`, `... MetalRouting`, `... LeftPanel` — ALLE grün (die #734-Metall-Ergänzung darf nicht brechen!). Build 0.
- [ ] **Step 5: Commit** `(=) #570: layer stack joins live process compatibility (conflicting numbers diverge; additions stay compatible)` && `git push`

---

### Task 4: Divergenz-Warnung + Design-Check

**Files:** Modify `MainWindow.axaml.cs` (`PdkEditProcess_Click`-`ProcessSaved`-Wiring: nach Reapply Divergenz prüfen + Warn-Dialog), Design-Check-VM/Panel (Implementer liest `DesignChecksPanel` + zugehöriges VM und ergänzt einen Check „component's PDK not in active process"). Tests für die Check-Logik (VM-Level).

- [ ] **Step 1: Read** `PdkEditProcess_Click`/`ProcessSaved`-Wiring, `PdkManager.GetEnabledPdkNames()`/`IsLockedByProcess`, Canvas-Komponenten-Zugriff (`vm.Canvas.Components` mit `Component.PdkSource`? — Implementer verifiziert das echte Member), DesignChecks-Infrastruktur (wie werden Checks definiert/gelistet).
- [ ] **Step 2 (Warnung):** Im `ProcessSaved`-Handler nach `ReapplyActiveProcessAfterPdkChange()`: wenn die editierte PDK jetzt `IsLockedByProcess` (per `LoadedPdks`-Lookup) UND es platzierte Komponenten mit diesem `PdkSource` gibt → `MessageBoxService`-Warnung: „The saved process no longer matches the design's process ('…'). N placed component(s) from '<pdk>' are now in conflict; new placements are blocked. Existing components are kept — review them in Design Checks." Kein Auto-Delete.
- [ ] **Step 3 (Design-Check):** Neuer Check (im bestehenden Checks-Muster): listet platzierte Komponenten, deren `PdkSource` weder process-agnostisch noch in `GetEnabledPdkNames()` ist → Fehler-Eintrag pro Komponente („'comp' from 'pdk' does not match the active process"). VM-Test: Komponente mit gesperrtem PdkSource → Eintrag; mit erlaubtem → keiner.
- [ ] **Step 4:** Build 0; Regression Checks/LeftPanel. **Commit** `(+) Process divergence: warning after per-PDK save + design check flags conflicted placed components` && `git push`

---

### Task 5: Löschen mit Papierkorb

**Files:** Modify `UserPdkStore` (`MoveToTrash(filePath) : string`, `RemoveComponent(filePath, componentName, backupFirst=true) : string?`), `MainWindow.axaml` (PDK-Zeile „Delete…"-Button `IsVisible={!IsBundled}`; Library-Kontextmenü „Delete…" bei custom), `MainWindow.axaml.cs` (Handler mit Confirm via `MessageBoxService`), `LeftPanelViewModel` (Deregistrierung: `UnregisterPdk(filePath)` — Templates dieses PdkSource raus, `LoadedPdks`-Eintrag raus [`PdkManagerViewModel` braucht ggf. eine Remove-Methode — prüfen, `:131` gibt es schon Remove-nahe Logik], Draft raus, Prefs-Pfad raus, Reapply+Filter; `RemoveCustomComponent(template)` analog). Tests `UnitTests/.../PdkTrashDeleteTests.cs`.

- [ ] **Step 1: Read** `UserPdkStore` (Root/Slug), `PdkManagerViewModel` (existierende Remove/UnregisterLogik `:131`?), Library-Kontextmenü („Edit…"-Muster), `RegisterSavedCustomComponent` (als Spiegel für Deregistrierung).
- [ ] **Step 2: Write failing tests** — Store: `MoveToTrash` verschiebt nach `.trash/<name>-<yyyyMMdd-HHmmss>.json` (Datei weg im Root, da im Trash; Timestamp invariant; Namenskollision im Trash → Suffix); `RemoveComponent` legt Backup in `.trash` und schreibt die Datei ohne die Komponente (per Name, OrdinalIgnoreCase); nicht-existente Datei → sauberer Fehler/No-op. VM: `UnregisterPdk` entfernt Templates+Registry+Draft und reappliziert; `RemoveCustomComponent` entfernt Template + Komponente aus Datei. Bundled: Guards (kein Delete).
- [ ] **Step 3: FAIL → implementieren → PASS.** UI: „Delete…"-Button/Menüpunkt + Confirm-Dialoge („Move '<pdk>' (N components) to trash? The file is moved to user-pdks/.trash and can be restored manually." / analog Komponente). Handler rufen Store+VM. Kein `Process.Start`.
- [ ] **Step 4:** Regression `... UserPdk`, `... LeftPanel`, `... AddCustomComponent`. Build 0 (XAML). **Commit** `(+) PDK management: delete custom PDKs/components to a .trash folder (confirmed, recoverable)` && `git push`

---

### Task 6: E2E + Cleanup

- [ ] **Step 1: Test** `UnitTests/.../PdkLifecycleFlowTests.cs`: (a) Startup-Reload registriert Dir-PDKs; (b) renumbered Layer → PDK fällt aus Live-Menge (Platzierung geblockt via `SingleProcessPolicy` mit Live-Menge), Metall-Add bleibt; (c) `MoveToTrash`+`UnregisterPdk` → weg aus Liste, Datei im Trash. Ein Assert pro Stufe. Keine Duplikate bestehender Tests (nur die Kette).
- [ ] **Step 2:** Grep/Zeilencheck; Build 0; betroffene Slices grün.
- [ ] **Step 3: Commit** `(+) End-to-end test: PDK lifecycle (startup reload, layer divergence, trash delete)` && `git push`

---

## Self-Review
- Spec-Coverage: #700→T1; „+"→T2; Layer-Identität+Warnung+Check→T3/T4; Papierkorb→T5; E2E→T6. Git-Versionierung = Follow-up-Issue (Koordinator).
- Konsistenz: Layer-Prüfung NUR in der Live-Auflösung → alle #736-Consumer erben; Persistenz unverändert.
- Verifikationspunkte: App-Start-Init-Stelle (T1); DesignChecks-Infra (T4); `PdkManagerViewModel`-Remove (T5); Canvas-Komponenten-PdkSource-Zugriff (T4); `ListCustomPdks` filtert Process!=null — T1 braucht auch process-lose (Dir-Scan direkt).
