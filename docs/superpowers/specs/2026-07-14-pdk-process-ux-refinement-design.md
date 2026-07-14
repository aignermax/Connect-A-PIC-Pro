# PDK-/Prozess-UX-Feinschliff — Design

**Datum:** 2026-07-14
**Status:** Freigegeben (Feld-Test-Feedback, end-to-end autonom)
**Baut auf:** #732 (Create-Custom-PDK-Dialog)

## Probleme (Feld-Test)

1. **New Component:** „Save" verlangt einen vorherigen „Preview"-Klick (`CanSave` hängt an `HasPreview`;
   `Save` early-returned ohne `_lastPreview`). Preview ist aber nur eine Sichtprüfung für den Nutzer —
   Save soll selbst rendern/validieren und Python-Fehler direkt anzeigen.
2. **Create Custom PDK:** Bei „Use existing process" sind Prozessname, +Layer/+Cross-section/+Material
   und die Materials-Grids (Si/SiO2-Seed) trotzdem sichtbar/editierbar. Ursache: der „Define new"-Block
   trägt `IsVisible={Binding ProcessSource,…}` UND `DataContext={Binding ProcessDefinitionEditor}` auf
   demselben Element — zur Laufzeit wertet `IsVisible` gegen den geswappten DataContext aus (Property
   fehlt dort) und bleibt `true`.
3. **Define new ohne Vorlage ist mühsam:** man muss alles von Hand ausfüllen. Es soll ein
   „Start from template"-Dropdown geben (vorhandene Prozesse), das den Editor vorbefüllt; danach nur
   abweichende Werte ändern.
4. **Toolbar „Fabrication Process"-Button (🧱) ist fehl am Platz:** sein Dialog mischt „aktiven Prozess
   ansehen", „Preset laden", „Import from PDK" und „Save to PDK file". Prozessinformationen editiert man
   pro PDK. → Button raus; stattdessen ein **„Edit"-Button an den custom PDKs** im PDK-Management, der
   den Prozess **genau dieses PDKs** editiert. Der Editor-Dialog verliert dabei „Load preset",
   „Import from PDK" und „New" (Prozess-Import gehört zum Load-PDK-Flow, der `PdkDraft.Process` ohnehin
   mitlädt).

## Lösung

### 1. Save ohne Preview-Pflicht (`NewComponentViewModel`)
- Extraktion aus `RunPreview` in eine gemeinsame private Methode ziehen (`EnsurePreviewAsync`):
  `ExtractAsync(BuildReference())` → `_lastPreview`/`HasPreview`/`PreviewBitmap`/`StatusText`.
- `Save`: wenn `_lastPreview` fehlt/ungültig → selbst `EnsurePreviewAsync()` ausführen; schlägt der
  Render fehl (Python-Fehler) → Fehler in `StatusText`, kein Save. Kein „Render a preview before
  saving"-Early-Return mehr.
- `CanSave` verliert `HasPreview` (bleibt: `!IsBusy && SelectedCustomPdk != null`; Namens-/PDK-Guards
  bleiben im Save). Der Preview-Button bleibt als reine Sichtprüfung.

### 2. Create-Custom-PDK: Modus-Sichtbarkeit reparieren + Template-Dropdown
- **Sichtbarkeits-Fix:** Ein ÄUSSERER Container (DataContext = `CreateCustomPdkViewModel`) trägt
  `IsVisible={ProcessSource→DefineNew}`; erst ein INNERES Element macht den DataContext-Swap auf
  `ProcessDefinitionEditor`. Bei „Use existing" ist damit der komplette Definitions-Bereich
  (Prozessname, CoreThickness, Grids, Add-Buttons) unsichtbar.
- **„Start from template"**: im DefineNew-Bereich ein Dropdown der vorhandenen Prozesse
  (`AvailableProcesses`); Auswahl ruft `ProcessDefinitionEditor.Load(template)` und übernimmt
  `CoreThicknessNm` aus der Vorlage. Danach frei editierbar. (Kein Zwang — leer starten geht weiter.)

### 3. Prozess-Editor wird per-PDK-Editor; Toolbar-Button entfällt
- **Toolbar:** 🧱-Button (`MainWindow.axaml:254`), `OpenProcessManagerCommand`/`ShowProcessManagerRequested`
  (MainViewModel) und der Handler in `MainWindow.axaml.cs` werden entfernt (einziger Auslöser).
- **PDK-Management:** pro NICHT-bundled PDK-Zeile ein „Edit…"-Button (Bundled = read-only, kein Button).
  Klick öffnet `ProcessManagementWindow` im **Single-PDK-Edit-Modus**.
- **`ProcessManagementViewModel`:** neue public Methode `LoadForSinglePdkEdit(PdkDraft draft)` —
  `ResetState()`-Äquivalent, `Load(draft.Process)` (falls null → leerer Prozess mit Hinweis),
  `_memberDrafts = { draft }`, `HasProcess = true`; plus ein `ProcessSaved`-Event (nach erfolgreichem
  `SaveProcess`), damit der Aufrufer `ReapplyActiveProcessAfterPdkChange()` ausführen kann
  (Fingerprint/Sichtbarkeit live halten).
- **`ProcessManagementWindow.axaml`:** „Import from PDK…", „New" und das „Load preset"-Dropdown werden
  ENTFERNT (Fenster ist jetzt ausschließlich „diesen Prozess dieses PDKs ansehen/editieren");
  „Save to PDK file…" bleibt (schreibt in genau dieses PDK; Confirm bleibt). Titel zeigt das PDK.
- Aufruf-Wiring in `MainWindow.axaml.cs` (Click-Handler am Edit-Button, Muster
  `TemplateEditComponent_Click`): Draft per Name aus `LeftPanel.GetLoadedPdkDrafts()` (dieselbe Instanz →
  in-memory-Sync nach Save), `PdkFilePathResolver = _ => pdkInfo.FilePath`, `ConfirmSaveToPdk` wie
  bisher, `ProcessSaved → vm.LeftPanel.ReapplyActiveProcessAfterPdkChange()`.

## Nicht-Ziele
- #726 (aktiven Design-Prozess umstellen) bleibt getrennt; der Single-PDK-Editor ändert nur die
  PDK-Datei/Prozessdefinition, nie `ActiveProcess`.
- Kein Prozess-Merge-Feature: identische Prozesse werden seit #732 by-value als kompatibel gruppiert —
  das beantwortet „warum passen exakt gleiche PDKs nicht zusammen?" bereits.
- #700/#730 unverändert.
- **Bundled PDKs bleiben read-only** (kein Edit-Button, Toolbar-🧱-Button entfällt komplett) — damit
  entfällt der frühere #682-Workflow „Metall-Cross-Section in ein BUNDLED PDK speichern" ersatzlos.
  Das ist eine bewusste Entscheidung, kein Versehen: ein Bundled/Foundry-PDK ist die vom Hersteller
  gelieferte Wahrheit und soll nicht vom Nutzer verändert werden können. Wer Metall-Routing auf Basis
  eines Bundled-Prozesses braucht, legt ein custom PDK an, das diesen Prozess übernimmt (Create-Custom-PDK-
  Dialog, „Use existing process") und ergänzt dort die Metall-Cross-Section — der Bundled-Prozess selbst
  bleibt unangetastet. Ein Follow-up-Issue für einen komfortableren „Duplicate as custom PDK"-Weg wird
  separat angelegt (#733 review, Finding 6).

## Testing
- Save ohne Preview: Save mit gültigem Code (Extractor-Mock) rendert selbst und speichert; mit
  Render-Fehler → StatusText-Fehler, kein Save; `CanSave` ohne `HasPreview`.
- Template-Dropdown: Auswahl befüllt `ProcessDefinitionEditor` (Layers/Xsections/Materials/Name) +
  `CoreThicknessNm`.
- `LoadForSinglePdkEdit`: befüllt Editor aus Draft, `SaveProcess` schreibt in dessen Datei (Resolver),
  `ProcessSaved` feuert.
- Sichtbarkeits-Fix: UI-Bindings nur Laufzeit (Smoke); die Struktur (äußerer Wrapper) wird im Review
  geprüft.
- Toolbar-Entfernung: keine verwaisten Referenzen (`OpenProcessManagerCommand`/`ShowProcessManagerRequested`).

## Constraints
Wie gehabt: keine erfundene Physik; S-Matrix DI-Service; kein `Process.Start`; compiled bindings;
`InvariantCulture`; ≤250 Zeilen neue Dateien, bestehende ≤500; Edit nur custom PDKs.
