# PDK-/Editor-UX-Polish Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development.

**Goal:** Feld-Test-Polish nach #739: flachere Standard-Controls, Edit-Component-Fenster-Fixes,
Trash-Restore pro Lösch-Vorgang, Rechtsklick→unified Editor, kompakter PDK-Mgmt-Header.

**Architecture:** Nur UI/VM/Store-Detailarbeit auf bestehenden Slices; keine neuen Konzepte.
Spec: `docs/superpowers/specs/2026-07-16-pdk-ux-polish-design.md` (maßgeblich).

## Global Constraints
- Referenz-Buttonhöhe: „+ Material" (`FontSize="10" Padding="6,1"`); Ziel ~40 % flacher als heute.
- Keine erfundene Physik; der tote FDTD-Button verschwindet ersatzlos, kein neuer S-Matrix-Pfad.
- Compiled bindings (`x:DataType`); neue Dateien ≤250 Zeilen; bestehende ≤500 (MainWindow.* grandfathered).
- Tests: `$env:PYTHONUTF8='1'; py "$env:USERPROFILE\.cap-tools\smart_test.py" <Pattern>`; Build `dotnet build -clp:ErrorsOnly`.
- Commits klein, Präfix `(=)`/`(~)`/`(+)`; push auf `fix/pdk-ux-polish`.

---

### Task 1: App-weite Button-/ComboBox-Höhe
**Files:** Modify `CAP.Avalonia/App.axaml` (bzw. zentrales Styles-Include), ggf. `UnitTests/UI/UiScreenshotTests.cs`.
- [ ] Fluent-Defaults ermitteln (MinHeight/Padding von Button+ComboBox), Style-Override schreiben (~40 % flacher, Richtung `FontSize 10`-Referenz; ComboBox inkl. Item-Container nicht kaputt machen).
- [ ] Build 0; Screenshot-Harness laufen lassen und PNGs auf kaputte Layouts sichten (MainView, Panels).
- [ ] Commit `(~) App-wide compact control heights: buttons/combo boxes ~40% flatter` && push.

### Task 2: Edit-Component-Fenster: Scrollbar, Höhe, Titel, Dedup
**Files:** Modify `CAP.Avalonia/Views/NewComponentWindow.axaml(+.cs)`, `CAP.Avalonia/ViewModels/Components/AddCustomComponent/NewComponentViewModel.cs`, `CAP.Avalonia/Views/MainWindow.axaml.cs` (Show-Hook), Tests `UnitTests/Components/AddCustomComponent/`.
- [ ] Scrollbar aus dem Content raus (AllowAutoHide=False bzw. Padding); Fensterhöhe +~60px.
- [ ] `WindowTitle` = "Edit Component: <Name>" im Edit-Modus (Test: LoadForEdit → Titel enthält Namen).
- [ ] Fenster-Dedup pro Template (Dictionary-Muster `_openPdkEditWindows`; zweiter ✏-Klick aktiviert).
- [ ] Tests + Regression `NewComponent`; Commit `(=) Component editor: solid scrollbar, taller window, name in title, single window per component` && push.

### Task 3: Trash-Restore pro Lösch-Vorgang
**Files:** Modify `CAP-DataAccess/Components/AddCustomComponent/PdkTrashService.cs` (+Entry-Record), `CAP.Avalonia/ViewModels/Panels/PdkTrash/*`, Tests `UnitTests/Components/AddCustomComponent/PdkTrash*`.
- [ ] Failing test: A,B,C sequentiell gelöscht → Restore(Eintrag A) stellt NUR A wieder her.
- [ ] `ListEntries`: RemovedComponents → ein Eintrag pro fehlender Komponente (dedupe per Name, neuestes Backup); `Restore` re-added nur diese; DeletedPdk unverändert; UI-Titel = Komponentenname.
- [ ] Regression `PdkTrash`; Commit `(=) Trash restore: restore exactly the clicked component, not the whole backup diff` && push.

### Task 4: Canvas-Rechtsklick „Component Settings" → unified Editor; toter FDTD-Button raus
**Files:** Modify Canvas-Kontextmenü-Wiring (`MainWindow.axaml.cs`/`ShowComponentSettingsDialog`-Aufrufer, DesignCanvas-Kontextmenü), `CAP.Avalonia/Views/ComponentSettingsDialog.axaml` (+VM: `RecalculateSMatrixCommand`/`CanRecalculate` entfernen), Tests.
- [ ] Rechtsklick-Eintrag routet auf denselben Edit-Hook wie ✏ (Template via PdkSource+Name auflösen; bundled → fork-on-edit-Pfad greift automatisch). Settings-Dialog bleibt NUR als S-Matrix-Ansicht vom Editor aus.
- [ ] FDTD-Button + Command + tote Codepfade entfernen (keine verwaisten Referenzen).
- [ ] Regression `ComponentSettings`, `NewComponent`; Commit `(-) Retire dead FDTD-recalculate in Component Settings; canvas right-click opens the component editor` && push.

### Task 5: PDK-Mgmt-Header kompakt
**Files:** Modify `CAP.Avalonia/Views/MainWindow.axaml` (PDK-Bereich), ggf. `PdkManagerViewModel` (keine neuen Commands nötig — EnableAll/DisableAll existieren), Tests nur falls VM-Änderung.
- [ ] Header „PDK Mgmt"; Icon-Buttons Enable All („☑" o.ä.), Disable All („☐"), vor Papierkorb+„+", mit Tooltips; untere Buttonzeile raus; `ActiveProcessLabel`-Block raus.
- [ ] Build 0 (XAML), Screenshot-Harness sichten; Commit `(~) PDK Mgmt header: compact icon actions (enable/disable all), drop bottom buttons + process label` && push.

### Task 6: Abschluss
- [ ] Screenshot-Harness final, code-review-Workflow (high) über den PR, Findings fixen, CI grün, ready.
