# PDK-first-Assistent v-next: modales „New PDK", Backend-Autoload — Design

**Datum:** 2026-07-13
**Status:** Freigegeben (Brainstorm)
**Baut auf:** #723 (PDK-first-Assistent)

## Ziel

Verfeinerung des „Neue Komponente"-Assistenten: (1) „New PDK…" als Eintrag im PDK-Dropdown statt
separatem Button; (2) Auswahl von „New PDK…" öffnet ein **modales PDK-Erstellungsfenster** (Name +
Fabrikationsprozess inkl. Breiten), das das New-Component-Fenster sperrt, bis das PDK erstellt (oder
abgebrochen) ist; (3) beim Umschalten des Backends wird der Beispielcode automatisch in den Editor
geladen — nur solange der Editor leer oder noch unangetastetes Beispiel ist.

## Nicht-Ziel (bewusst getrennt)

Den Prozess eines **bestehenden** PDK umstellen — das ist ein separater, vorbestehender Bug/Design-Gap
(**#726**, analysiert) und bleibt außerhalb dieses Features. Der Erstellungs-Modus legt nur NEUE PDKs
an und fasst den `FileOperations.ActiveProcess`-Pfad nicht an.

## Flow

New-Component-Fenster → PDK-Dropdown = custom PDKs **+ Sentinel „New PDK…"**. Wählt man den Sentinel:
1. Modales PDK-Erstellungsfenster öffnet sich (`ShowDialog`, Owner = New-Component-Fenster → blockiert).
2. Dort: **PDK-Name** + Prozess (leeren „New process" starten ODER bestehenden als Preset übernehmen,
   Breiten/Layer/Cross-Sections editierbar — alles vorhandene Editor-Funktion). „Create PDK" legt via
   `UserPdkStore` eine neue benannte User-PDK an (Name + `ToProcess()`, leere Komponentenliste).
3. Zurück im New-Component-Fenster: `AvailableCustomPdks` wird neu geladen, das neue PDK vorausgewählt
   (Prozess geerbt/read-only). Bei Abbruch: Dropdown-Auswahl springt zurück auf den vorherigen Wert.

Danach wie gehabt: Name → own-code (Editor) → S-Matrix (Meep) → Speichern (hängt Komponente an das PDK).

## Architektur / Komponenten

- **`UserPdkStore`** (CAP-DataAccess): neue Methode
  `CreateNamedPdkWithProcess(string pdkName, ProcessDefinition process, string backend, string? routingCrossSection) : string`
  — schreibt `<slug(pdkName)>.json` mit `Name=pdkName`, `Process=process`, **leerer** `Components`-Liste.
  Foundry-JSONs unangetastet.
- **`ProcessManagementViewModel`** (CAP.Avalonia): **PDK-Erstellungs-Modus** — neue Properties
  `[ObservableProperty] string PdkName`, `[ObservableProperty] bool IsPdkCreationMode`; neuer
  `[RelayCommand] CreatePdk` (nur aktiv in Creation-Mode + Name nicht leer), der über einen injizierten
  Callback `Func<string, ProcessDefinition, string?>? CreateUserPdk` (Name, Prozess → Pfad) das PDK
  anlegt und ein `PdkCreated`-Event/Result feuert. Startzustand im Creation-Mode: frischer Prozess
  (`NewProcess()` bzw. Preset wählbar). Berührt `ActiveProcess`/`SetActiveProcess` NICHT. Bestehendes
  Verhalten (Toolbar-Öffnung, non-modal, `SaveProcess`) bleibt unverändert.
- **`NewComponentViewModel`** (CAP.Avalonia): PDK-Dropdown-Quelle = custom PDKs + Sentinel `NewPdkOption`
  (z.B. `UserPdkInfo?`-Sentinel bzw. eine Wrapper-Liste). `OnSelectedCustomPdkChanged`: ist der Sentinel
  gewählt → `Func<Task<UserPdkInfo?>>? CreateNewPdk` aufrufen; bei Erfolg `AvailableCustomPdks` neu aus
  `store.ListCustomPdks()` + neues PDK auswählen; bei null (Abbruch) → auf vorherige Auswahl zurück.
  Entfernt: `IsNewPdk`/`NewPdkName` + inline Prozess-Picker (wandern ins Modal). Prozess weiterhin aus
  `SelectedCustomPdk.Process` geerbt (read-only).
- **Backend-Beispielcode:** gemeinsame Konstante `BackendCodeExamples` (gdsfactory/nazca) als einzige
  Quelle; `OnSelectedBackendChanged` lädt das Beispiel in `Code`, wenn `Code` leer ODER == dem
  anderen Backend-Beispiel (unangetastet). XAML-Hilfe-Flyout nutzt dieselbe Quelle.
- **`NewComponentWindow.axaml`:** Dropdown inkl. Sentinel; inline New-PDK/Prozess-UI entfernt; Prozess
  read-only angezeigt.
- **`MainWindow.axaml.cs`:** `newComponentVm.CreateNewPdk = async () => {…}` öffnet ein
  `ProcessManagementWindow` im Creation-Mode **modal** (`ShowDialog(newComponentWindow)`); `CreateUserPdk`
  des Prozess-VM ruft `UserPdkStore.CreateNamedPdkWithProcess`; nach Schließen wird der erzeugte
  `UserPdkInfo` (oder null) zurückgegeben.

## Fehlerbehandlung

- Leerer PDK-Name → „Create PDK" deaktiviert. Namenskollision (`NamedPdkExists`) → Überschreiben/umbenennen
  abfragen (`MessageBoxService`).
- Prozess ohne Cross-Sections/leer → Hinweis; Anlegen erlaubt (Prozess kann später ergänzt werden), aber
  klar kommuniziert.
- Abbruch des Modals → keine PDK-Anlage, Dropdown zurückgesetzt.
- Keine erfundene Physik; S-Matrix bleibt `IFdtdSMatrixService` (DI). `InvariantCulture` für Slug/JSON.

## Testing

- `UserPdkStore.CreateNamedPdkWithProcess`: legt Datei mit Name+Prozess+leerer Komponentenliste an;
  `ListCustomPdks` findet sie; Foundry-JSONs nie geschrieben.
- `ProcessManagementViewModel` Creation-Mode: `CreatePdk` ruft `CreateUserPdk` mit Name+`ToProcess()`,
  feuert `PdkCreated`; `CanCreatePdk` false bei leerem Namen; berührt `ActiveProcess` nicht (Regression:
  bestehende ProcessManagement-Tests bleiben grün).
- `NewComponentViewModel`: Sentinel-Auswahl ruft `CreateNewPdk`; Erfolg → Liste refreshed + neues PDK
  gewählt; Abbruch → vorherige Auswahl; Backend-Autoload (leer/unangetastet vs. eigener Code).
- Modal-Öffnung/Interaktion nur zur Laufzeit (Smoke-Test).

## Cross-Platform / Constraints

`Path.Combine`+`SpecialFolder`; kein `Process.Start`; `x:DataType`/compiled bindings; `InvariantCulture`;
max. 250 Zeilen/neue Datei, bestehende ≤500 (`ProcessManagementViewModel` sorgfältig unter Limit halten,
ggf. Creation-Mode in ein Partial `.PdkCreation.cs`).

## Out of Scope (YAGNI)

Prozess bestehender PDKs umstellen (#726); Edit/Delete von custom PDKs; Startup-Reload (#700); Rückkanal
des non-modalen Toolbar-Prozess-Editors.
