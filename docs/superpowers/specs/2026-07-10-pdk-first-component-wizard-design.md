# PDK-first „Neue Komponente"-Assistent — Design

**Datum:** 2026-07-10
**Status:** Freigegeben (Brainstorm)
**Baut auf:** #702 (v1), #721 (Rawcode-Authoring)

## Ziel

Das „Neue Komponente"-Fenster zu einem **PDK-first-Assistenten** umbauen: der Nutzer stellt eigene
Komponenten UND eigene (custom) PDKs über die UI zusammen. Eine Komponente wird immer in ein custom
PDK gespeichert (bestehendes oder neu angelegtes); der Strukturcode wird eingefügt oder aus `.py`
geladen; die S-Matrix wird via Meep berechnet.

## Motivation & Domänen-Einordnung

Nutzer (v.a. Studenten/Forscher) haben eigenen gdsfactory-/nazca-Komponenten-Code (Snippet oder
`.py`) und wollen den direkt einbringen — nicht eine bereits installierte `modul.funktion`
referenzieren. Eine gdsfactory-Komponente *ist* eine Python-Funktion; ein PDK *ist* ein Package aus
solchen Zellen + Prozess (Layerstack/Cross-Sections). Das PDK-first + own-code-Modell spiegelt diese
Realität. Der **Function-reference-Modus entfällt** (Nische: Zelle aus installiertem Package ziehen —
für die Zielgruppe irrelevant, für die gebündelten PDKs redundant).

## Flow & Layout (Einzelfenster, geordnete Abschnitte)

Ein Fenster ohne „New Component"-Header (steht im Fenstertitel), von oben nach unten:

1. **PDK** — Dropdown listet **nur custom PDKs** (`!IsBundled`) + Option **„+ Neues PDK"** (Name eingeben).
2. **Prozess** — an das PDK gebunden (ein PDK = ein Prozess, #570):
   - Bestehendes custom PDK gewählt → Prozess ist **geerbt**, nur angezeigt (read-only).
   - „Neues PDK" → **Prozess-Dropdown** (alle vorhandenen Prozesse zum Übernehmen) + Button
     **„Prozess-Editor öffnen…"** (startet den bestehenden Fabrication-Process-Editor #570; kein
     inline nachgebauter Editor).
3. **Name** — Komponentenname.
4. **Struktur (Code)** — Code-Editor mit Syntax-Highlighting; „Aus .py laden…"; „?"-Hilfe-Flyout mit
   herauskopierbarem Beispielcode. Backend-Auswahl gdsfactory/nazca.
5. **S-Matrix** — „Mit Meep berechnen" (oder überspringen → Blackbox).
6. **Speichern** — hängt die Komponente an das gewählte/neue custom PDK an; erscheint sofort in der Library.

## Persistenz-Modell: benannte custom PDKs

- Custom PDKs sind künftig **nutzerbenannt** (nicht mehr auto „My {Prozess} Components"), verwaltet
  unter `%LOCALAPPDATA%/Lunima/user-pdks/<name-slug>.json`. Foundry-JSONs bleiben unangetastet.
- `UserPdkStore` lernt: (a) **auflisten** vorhandener custom PDKs (Name + Pfad + Prozess),
  (b) **anlegen** eines neuen benannten PDK mit übernommenem Prozess, (c) **anhängen** einer
  Komponente an ein gewähltes custom PDK.
- Der PDK-Dropdown speist sich aus den geladenen Nicht-Bundled-PDKs (`PdkManagerViewModel.LoadedPdks`
  gefiltert auf `!IsBundled`) plus neu angelegten.

## Code-Editor & Hilfe

- **Syntax-Highlighting** via AvaloniaEdit + TextMate (Python-Grammar) — `Avalonia.AvaloniaEdit`,
  `AvaloniaEdit.TextMate`, `TextMateSharp.Grammars` sind bereits Paketreferenzen.
- **„Aus .py laden…"** über `FileDialogService` → Dateiinhalt in den Editor (kein `Process.Start`).
- **„?"-Hilfe-Flyout** (wiederverwendeter `HelpFlyoutButton`) mit backend-spezifischem, kopierbarem
  Beispielcode. Der Code muss eine Variable `component` definieren, z.B. gdsfactory
  `import gdsfactory as gf` / `component = gf.components.mmi1x2()`; nazca-Äquivalent analog.

## S-Matrix (DI-Service)

- Berechnung über den vorhandenen DI-Service `IFdtdSMatrixService` (Impl `DockerFdtdSMatrixService`,
  registriert in `FdtdFeatureExtensions`). Ein künftiger anderer FDTD-Solver = weitere
  Implementierung, kein Umbau.
- **Keine erfundene Physik:** S-Matrix nur aus echtem FDTD / Blackbox (null) / verlustfreiem
  2-Port-Ideal; FDTD-Fehler → nichts gespeichert.

## Architektur / Wiederverwendung

Evolution von #721, kein Neubau. Wiederverwendet: `ComponentGeometryExtractor` (Rawcode-Render),
`CustomComponentDraftFactory`, das Placement-Override-Seeding (`PlaceComponentCommand`),
`IFdtdSMatrixService`, `FdtdSMatrixToDraftConverter`, `HelpFlyoutButton`, `FileDialogService`.

Neu/geändert:
- `UserPdkStore` (CAP-DataAccess) → benannte custom PDKs anlegen/auflisten/anhängen.
- `NewComponentViewModel` → PDK-Auswahl (bestehend/neu+Name), Prozess (geerbt bei bestehend, wählbar
  bei neu), Name, Code, Load-.py, Compute, Save-an-PDK. Reference-Modus + `InputMode`-Umschalter raus.
- `NewComponentWindow.axaml` → PDK-first-Sektionen, AvaloniaEdit-Editor, Help-Flyout mit Beispielen,
  Header entfernt, „Prozess-Editor öffnen…"-Button, „+ Neues PDK"-Affordanz.

## Fehlerbehandlung

- Docker/FDTD nicht bereit → vorhandene `CheckAvailabilityAsync`-Meldung; Blackbox-Speichern möglich.
- FDTD-Fehler → roher Solver-Fehler, keine Fake-S-Matrix.
- Render-Fehler (ungültiger Code) → Speichern blockiert, klare Meldung; leerer Code → Hinweis.
- PDK-Namenskollision (neues PDK) → Überschreiben/umbenennen abfragen. Komponenten-Namenskollision im
  Ziel-PDK → wie #702 (Überschreiben/umbenennen).
- Alle maschinennahen Strings (Slug, JSON) mit `CultureInfo.InvariantCulture`.

## Testing

- `UserPdkStore`: benanntes PDK anlegen (Name+Prozess), auflisten (nur custom), Komponente anhängen,
  Round-trip via `PdkLoader`; Foundry-JSONs nie geschrieben.
- `NewComponentViewModel`: PDK-Auswahl bestehend vs. neu (+Name), Prozess geerbt vs. wählbar,
  Save-an-gewähltes-PDK, Blackbox-Pfad, FDTD-Fehler → kein Modell, kein Reference-Modus mehr.
- Help-Beispielcode je Backend vorhanden/kopierbar (VM-seitig testbar).
- Architektur-Test: Slice-Regeln; custom PDK nie im Bundled-Ordner.
- Editor-Highlighting/Datei-Dialog/Fenster-Interaktion nur zur Laufzeit (Smoke-Test).

## Cross-Platform-Parität

`Path.Combine` + `SpecialFolder`; kein direkter `Process.Start`; `x:DataType`/compiled bindings;
`InvariantCulture`; Tests plattformneutral (Linux-CI grün).

## Out of Scope (YAGNI)

- Voller Inline-Prozess-Editor (→ bestehender #570-Editor per „Prozess-Editor öffnen…").
- Function-reference-Modus (entfernt).
- Rawcode in gespeicherten Gruppen-Templates (#720).
- Startup-Reload der User-PDKs (#700, separat).
