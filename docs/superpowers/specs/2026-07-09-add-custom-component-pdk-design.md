# Bring-your-own-Component → eigenes PDK — Design

**Datum:** 2026-07-09
**Status:** Freigegeben (Brainstorm), bereit für Implementierungsplan

## Ziel

Ein UI-Pfad, mit dem Nutzer eigene photonische Komponenten (Geometrie via gdsfactory
oder nazca) anlegen, ihre S-Matrix mit dem vorhandenen Meep/Docker-FDTD-Solver berechnen
und in ein eigenes, wiederverwendbares PDK speichern — gebunden an einen gewählten
Fabrikationsprozess. So definiert der Nutzer schrittweise sein eigenes PDK.

## Motivation

Nutzer haben gefragt, wie sie eigene Komponenten via gdsfactory einfügen können. Heute gibt
es nur zwei Pfade: (1) Import einer **ganzen** PDK-Datei (.json / .py über den Import-Wizard),
oder (2) einen per-Instanz-Override (#637), der nur eine bereits platzierte Instanz umschreibt
und nichts zur Library hinzufügt. Es fehlt der glatte „eigene Komponente → wiederverwendbare
Library"-Pfad. Personas: **Priya** (import own PDK first-class) und **Mirko** (gdsfactory /
offene Formate).

## Harte Randbedingung: keine erfundene Physik

Die S-Matrix einer eigenen Komponente stammt **ausschließlich** aus einer dieser Quellen:

- **(a) FDTD-Meep** auf der echten Geometrie + echtem Prozess-Materialstack → echte Physik.
- **(b) Blackbox** — leere S-Matrix, klar als „kein Simulationsmodell" gelabelt (wie die
  bestehende `HasNoSimulationModel`-Warnung), wenn der Nutzer das Rechnen überspringt.
- **(c) Verlustfreies Ideal** (honest pass-through) nur für reine 2-Port-Routing-Bauteile.

**Nie erfundene Werte.** Schlägt FDTD fehl, wird der rohe Solver-Fehler gezeigt und **nichts**
gespeichert — keine Fake-S-Matrix als Fallback.

## Architektur

Vertical Slice `AddCustomComponent`, wiederverwendet die bestehende FDTD-Pipeline und den
Geometrie-Editor des Override-Pfads.

```
CAP-DataAccess/Components/AddCustomComponent/
  UserPdkStore.cs               — legt/aktualisiert eine beschreibbare User-PDK-Datei pro Prozess
CAP.Avalonia/Services/AddCustomComponent/
  ComponentGeometryExtractor.cs — Render(module,function)→bbox+pins (reuse OverridePinMapper)
  FdtdSMatrixToDraftConverter.cs— ComponentSMatrixData → PdkSMatrixDraft (+ Blackbox/2-Port-Ideal)
CAP.Avalonia/ViewModels/Components/AddCustomComponent/
  NewComponentViewModel.cs      — orchestriert Name/Prozess/Geometrie/FDTD/Speichern
CAP.Avalonia/Views/
  NewComponentWindow.axaml(.cs)
CAP.Avalonia/DI/
  AddCustomComponentFeature.cs  — DI-Extension, aufgerufen aus App.axaml.cs
UnitTests/Components/AddCustomComponent/
```

`UserPdkStore` liegt in CAP-DataAccess (reine Persistenz); die Services, die UI-Preview- und
FDTD-Bausteine koordinieren, liegen in CAP.Avalonia (dort leben die Preview-Services und
`OverridePinMapper`).

Wiederverwendete bestehende Bausteine (nicht neu bauen):
- `IFdtdSMatrixService` / `DockerFdtdSMatrixService` — `CheckAvailabilityAsync`, `SolveAsync`.
- `ComponentFdtdRequestFactory` (`Func<Component, CancellationToken, Task<FdtdSMatrixRequest?>>`).
- `FdtdSMatrixConverter.ToComponentSMatrixData(...)`.
- Der Geometrie-Code-Editor + Live-Render aus dem #637-Override
  (`InstanceNazcaCodeEditorViewModel` inkl. Backend-Toggle nazca|gdsfactory).
- `PdkManager.RegisterPdk(...)`, der PDK-JSON-Writer (`PdkJsonSaver`/`_pdkSaver.SaveToFile`),
  `UserPreferencesService.AddUserPdkPath(...)`.
- DTOs `PdkDraft` / `PdkComponentDraft` / `PdkSMatrixDraft`
  (`CAP-DataAccess/Components/ComponentDraftMapper/DTOs/PdkDraft.cs`).

## Zuschnitt: Geometrie-Quelle (v1 vs. v2)

**v1 = Funktions-Referenz.** Die Geometrie wird als gdsfactory-/nazca-**Funktionsreferenz**
angegeben (`module.function` + optionale Parameter, z.B. `cspdk.sin300.mmi2x2`, `ubcpdk.*`
oder eine Funktion aus einem eigenen installierten Modul). Dieser Pfad ist end-to-end erprobt
(Render, Platzieren, Export) — genau wie die gebündelten cspdk-Komponenten — und braucht **kein**
neues Export-/Render-Plumbing. Das löst direkt „CornerStone hat nur 12 Komponenten": jede
Funktion aus cspdk/ubcpdk/eigenem Modul wird hinzufügbar.

**v2 (Folge-Projekt, hier nur skizziert): Rawcode-Authoring.** Beliebigen gdsfactory-/nazca-Code
einfügen (wie im #637-Override). Erfordert (a) ein Rawcode-Feld auf `PdkComponentDraft`,
(b) Seeding eines per-Instanz-`NazcaCodeOverride` aus dem gespeicherten Code beim Platzieren
(wiederverwendet die bewährte Rawcode-Render-/Export-Pipeline, #559), (c) Export-Verifikation.
Wegen des Export-Risikos bewusst aus v1 herausgehalten.

## Nutzer-Flow (v1)

Ein `+`-Button an der PDK-Komponenten-Library (links, an `LeftPanelViewModel`, wo `AllTemplates`
lebt — **nicht** `ComponentLibraryViewModel`, das verwaltet Gruppen) öffnet das
**„Neue Komponente"-Fenster**:

1. **Name** eingeben.
2. **Prozess** aus Dropdown wählen (bundled + vorhandene User-Prozesse). Bestimmt das Ziel-User-PDK
   und (soweit die FDTD-Request-Contract es zulässt) Port-Breite/Wellenlängenbereich.
3. **Geometrie** als Funktionsreferenz angeben (Backend-Toggle gdsfactory/nazca, Modul.Funktion,
   optionale Parameter) + „Run Preview"; Bounding-Box + physische Pins werden aus dem Render
   extrahiert.
4. **S-Matrix berechnen** — „Mit FDTD (Meep) rechnen" nutzt die vorhandene
   `IFdtdSMatrixService`-Pipeline. Oder überspringen → Blackbox.
5. **Speichern** → schreibt einen `PdkComponentDraft` (mit `gdsFactoryFunction`/`nazcaFunction`,
   extrahierten Maßen/Pins, `sMatrix`|leer) ins User-PDK des Prozesses, registriert es,
   Komponente erscheint sofort in der Library.

Wiederöffnen des Fensters für eine User-PDK-Komponente erlaubt Editieren (Referenz/Parameter/Name
ändern → neu rendern → neu rechnen → speichern). Foundry-Komponenten bleiben read-only.

## Datenfluss

```
Name + Funktionsreferenz (module.function[+params]) + Backend + Prozess
  → ComponentGeometryExtractor: preview.RenderAsync(module, function, params)
       → NazcaPreviewResult → bbox (XMax-XMin, YMax-YMin) + Pins (OverridePinMapper.BuildOverridePins)
  → [optional] transiente Component (NazcaModuleName/NazcaFunctionName + PhysicalPins gesetzt)
       → ComponentFdtdRequestFactory.BuildAsync → IFdtdSMatrixService.SolveAsync
       → FdtdSMatrixResult → FdtdSMatrixConverter.ToComponentSMatrixData
       → FdtdSMatrixToDraftConverter → PdkSMatrixDraft (WavelengthData)
  → PdkComponentDraft (name, gdsFactoryFunction|nazcaFunction, params, dims, pins, sMatrix|leer)
  → UserPdkStore.AddOrUpdate(process, draft) → schreibt user-pdks/<slug>.json (PdkJsonSaver)
  → PdkManagerViewModel.RegisterPdk + AddUserPdkPath
  → LeftPanelViewModel: PdkTemplateConverter.ConvertToTemplate → AllTemplates → Library aktualisiert
```

**FDTD-Backend-Hinweis:** Die FDTD-Request-Factory rendert über den DI-`NazcaComponentPreviewService`.
Ob gdsfactory-Geometrie damit gerendert wird, hängt von der DI-Registrierung ab; falls der
FDTD-Pfad für gdsfactory-Backends nicht rendert, ist die contained Fix, der Factory den
gdsfactory-Preview-Service zu geben (kleiner, lokaler Eingriff, in der Umsetzung zu verifizieren).

## User-PDK-Persistenz & Prozess-Bindung (#570)

- Erstes „+" pro Prozess legt `%LOCALAPPDATA%/Lunima/user-pdks/<prozess-slug>.json` an
  (plattformneutral via `Environment.GetFolderPath(SpecialFolder.LocalApplicationData)` +
  `Path.Combine`; slug locale-invariant erzeugt). Auf macOS/Linux das jeweilige
  App-Data-Verzeichnis.
- Als beschreibbares User-PDK registriert (`AddUserPdkPath`). Die **gebündelten Foundry-JSONs
  werden nie geschrieben** — das entschärft die „Save-to-PDK-gefährlich"-Sorge.
- Ein PDK = ein Prozess (S-Matrix ist prozess-spezifisch, konsistent mit #570). Weitere
  Komponenten desselben Prozesses landen in derselben Datei.

## Fehlerbehandlung

- **Docker fehlt/nicht bereit** → bestehende `CheckAvailabilityAsync`-Meldung; Komponente lässt
  sich trotzdem als Blackbox speichern, Rechnen später.
- **FDTD-Fehler** → roher Solver-Fehler (bestehendes Verhalten), keine Fake-S-Matrix.
- **Geometrie-Render schlägt fehl** → Speichern blockiert mit Meldung.
- **Namenskollision** im Ziel-User-PDK → Überschreiben/Umbenennen abfragen.
- **Prozess unvollständig** (kein Materialstack/Layer) → FDTD nicht möglich; warnen und
  Blackbox-Speichern anbieten.
- Alle maschinennahen Strings (Slug, JSON) mit `CultureInfo.InvariantCulture`.

## Testing

- `UserPdkStore`: Datei-Anlage/-Naming pro Prozess; `PdkComponentDraft`-Round-trip (save→load
  via `PdkLoader`); Foundry-JSONs werden **nie** geschrieben (Pfad-Assertion).
- `ComponentGeometryExtractor`: Funktionsreferenz → bbox + Pins (Preview-Service gemockt).
  Deckt denselben Extract wie der Override-`Apply` (bbox-Größe + `OverridePinMapper`).
- `FdtdSMatrixToDraftConverter`: `ComponentSMatrixData` → `PdkSMatrixDraft` (WavelengthData korrekt,
  Portnamen erhalten); Blackbox → leere `sMatrix`; 2-Port-Ideal → verlustfreies Pass-through.
- `NewComponentViewModel`: Namensvalidierung; „Prozess nötig für Compute"; Blackbox-Speicherpfad;
  Kollisionsbehandlung; keine Speicherung bei FDTD-Fehler (`IFdtdSMatrixService` gemockt).
- Architektur-Test: der Slice hält die Vertical-Slice-Import-Regeln ein.
- Plattform-abhängige Pfad-Assertions mit `OperatingSystem.IsX()` guarden (Linux-CI grün).

## Cross-Platform-Parität

- Pfade via `Path.Combine` + `Environment.SpecialFolder`, keine Drive-Letter-Annahmen.
- Kein direkter `Process.Start` — der Render/Solver läuft über die bestehenden Abstraktionen
  (`ProcessLaunchFactory` / Subprocess-Runner der FDTD-Pipeline).
- Datei-Öffnen/Reveal (falls „im Ordner zeigen" angeboten wird) über `IUrlLauncher`.

## Out of Scope (YAGNI)

- User-PDKs teilen/publizieren → Photonic-Registry (#655/#656).
- Netlist-View/Export → separates Issue #687.
- Round-trip-Import einzelner Komponenten aus fremden Formaten.
- KI-gestützte PDK-Transformation → #620.
