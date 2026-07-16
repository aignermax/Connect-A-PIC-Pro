# Getrennter „Create Custom PDK"-Dialog + Prozess-Sichtbarkeits-Fix — Design

**Datum:** 2026-07-14
**Status:** Freigegeben (Brainstorm)
**Baut auf:** #723/#727/#729 (New-Component-Assistent)

## Ziel

Den verwirrenden Zwitter beheben, der entstand, weil der Fabrication-Process-Editor als „New PDK"-Modal
wiederverwendet wurde. **Anlegen und Editieren werden getrennt:** ein eigener schlanker
„Create Custom PDK"-Dialog (Prozess übernehmen ODER neu definieren, modus-sauber) vs. der bestehende
Editor rein zum Editieren. Zusätzlich: der „nicht anschaltbar/nicht sichtbar"-Bug bei per-Preset
übernommenem Prozess und der falsche nazca-Beispielcode.

## Probleme (Feld-Test)

1. **nazca-Beispielcode wirft.** Das Render-Skript (`scripts/render_component_preview.py`,
   `_build_cell_from_code_file`) erwartet eine `component()`-**Funktion**, die eine Nazca-Cell zurückgibt
   (oder eine Modul-Variable `cell`). Die Konstante `BackendCodeExamples.Nazca` setzt fälschlich eine
   Variable `component = nd.Cell(...)`.
2. **Per-Preset übernommener Prozess → PDK nicht sichtbar/anschaltbar.** Fingerprint ist by-value
   (`ProcessCompatibility.AreCompatible`: CoreMaterial/Cladding/Thickness±5nm/Wellenlänge±40nm; Name
   zählt nicht) → ein übernommener CornerStone-Prozess ist wertkompatibel. ABER der Lock/Filter ist
   by-name gegen die im Design gespeicherte `ActiveProcessSelection.MemberPdkNames` (Snapshot); das neue
   PDK steht da nicht → `IsLockedByProcess=true`, aus `FilteredTemplates` gefiltert.
3. **Zwitter-Dialog.** Im Creation-Mode des ProcessManagementWindow sind Editier-Altlasten immer
   sichtbar (Import from PDK, „New"=Reset, Load-preset, „Process:"/Name-Textbox, „Save to PDK file…" +
   Layer/Xsection-Editor), widersprüchlich zu „Create PDK".

## Lösung

### 1. Eigener „Create Custom PDK"-Dialog (`CreateCustomPdkWindow` + `CreateCustomPdkViewModel`)
Zweckgebaut, **nicht** der Prozess-Editor. Felder:
- **PDK name** (Pflicht).
- **Process source** (Radio): **Use existing** → Dropdown der vorhandenen Prozesse (übernehmen);
  **Define new** → volle Prozess-Definition (Prozessname, Materialien, Layer, Cross-Sections,
  Breiten/Radien).
- **Modus-sauber:** Im „Define new"-Bereich sind KEINE Gadgets zum Ändern/Speichern *bestehender*
  Prozesse (kein „Import from PDK", „Save to PDK file…", Preset-Loader, „New"-Reset).
- **Create PDK** → baut `ProcessDefinition` (aus gewähltem existierendem ODER aus den definierten
  Feldern), ruft `UserPdkStore.CreateNamedPdkWithProcess(name, process, backend, xs)`, triggert den
  Sichtbarkeits-Refresh (§3) und liefert das neue `UserPdkInfo` zurück (an
  `NewComponentViewModel.CreateNewPdk`).
- Namenskollision (`NamedPdkExists`) → Meldung/kein Anlegen.

### 2. Fabrication-Process-Editor wieder rein zum Editieren
Der `IsPdkCreationMode`-Aufsatz (PdkName-Feld, „Create PDK", `CreatePdk`, `CreateUserPdk`/`PdkNameExists`/
`PdkCreated`) wird aus `ProcessManagementViewModel`/`ProcessManagementWindow.axaml` **entfernt**. Der
Editor tut wieder nur „Prozess ansehen/editieren" (bestehendes Verhalten vor #727).

### 3. Prozess-Sichtbarkeit by-value reparieren
Nach dem Anlegen eines custom PDK: Katalog neu bauen und die aktive Prozess-Mitgliedschaft **by-value**
neu auflösen (statt der Snapshot-Namensliste), sodass ein wertkompatibles neues PDK in die erlaubten
Namen aufgenommen wird → anschaltbar, Komponenten in `FilteredTemplates`. Konkret: nach
`RegisterSavedCustomComponent`/PDK-Anlage `BuildProcessCatalog()` + eine Neu-Auflösung der aktiven
Auswahl gegen den Live-Katalog (nicht nur `ApplyProcessLock(snapshotNames)`), sodass die
by-value-Gruppenmitglieder (inkl. neuem PDK) erlaubt werden.

### 4. nazca-Beispielcode-Fix
`BackendCodeExamples.Nazca` auf eine contract-konforme `component()`-Funktion setzen (siehe oben).

## Architektur / Wiederverwendung

- Neu: `CAP.Avalonia/ViewModels/.../CreateCustomPdkViewModel.cs` + `CAP.Avalonia/Views/CreateCustomPdkWindow.axaml(.cs)`.
- „Define new" **komponiert** die Prozess-Definitions-Editier-Logik: entweder ein getrimmtes
  `ProcessManagementViewModel` (nur Layers/Xsections/Materials-Grids + `ToProcess()`, ohne
  Edit-bestehend-Gadgets) eingebettet, ODER die editierbaren Collections direkt genutzt. Kein
  Duplizieren der Grid-Logik.
- `UserPdkStore.CreateNamedPdkWithProcess` (vorhanden) für die Anlage.
- `NewComponentViewModel.CreateNewPdk`-Hook öffnet künftig `CreateCustomPdkWindow` modal
  (`ShowDialog`, Owner = New-Component-Fenster) statt des Prozess-Editors.
- Sichtbarkeits-Refresh über `MainViewModel.BuildProcessCatalog()`/`LeftPanelViewModel`-Reapply.

## Fehlerbehandlung
- Leerer PDK-Name → „Create PDK" deaktiviert. Kollision → Meldung.
- „Define new" ohne gültige Cross-Sections → Hinweis, Anlegen erlaubt (Prozess minimal), klar
  kommuniziert.
- Abbruch (Cancel) → keine Anlage, Dropdown zurück.
- Keine erfundene Physik; S-Matrix bleibt DI-Service; `InvariantCulture`.

## Testing
- nazca-Beispiel: `BackendCodeExamples.Nazca` enthält `def component()` + `nd.Cell` (String-Assert;
  echter Render-Lauf ist Python/Subprozess, nicht Unit).
- `CreateCustomPdkViewModel`: Use-existing → `CreateNamedPdkWithProcess` mit übernommenem Prozess;
  Define-new → `ToProcess()`-Prozess; Kollision blockt; `CanCreate` (Name nicht leer).
- **Sichtbarkeits-Fix (Kern-Test):** ein via übernommenem (wertkompatiblem) Prozess angelegtes custom PDK
  ist nach der Anlage bei aktivem kompatiblem Prozess enabled UND seine Komponente in `FilteredTemplates`
  (VM-Level-Test gegen `PdkManager`/`FilterComponents`).
- Editor-Rückbau: bestehende `ProcessManagement`-Tests grün; `ProcessManagementPdkCreationTests` werden
  entfernt/ersetzt (Creation-Mode entfällt).
- Dialog-Interaktion nur Laufzeit (Smoke-Test).

## Cross-Platform / Constraints
`x:DataType`/compiled bindings; kein `Process.Start`; `InvariantCulture`; max. 250 Zeilen/neue Datei,
bestehende ≤500; keine erfundene Physik; S-Matrix DI-Service.

## Out of Scope (YAGNI)
#726 (Prozess eines bestehenden PDK umstellen); #730 (Edit+Rename verwaist Original); #700 (Startup-Reload).
