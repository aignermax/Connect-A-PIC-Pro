# PDK-/Editor-UX-Polish + Trash-Restore-Fix — Design

**Datum:** 2026-07-16
**Status:** Freigegeben (Feld-Test-Feedback nach Merge von #739, end-to-end autonom)
**Baut auf:** #739 (PDK-Lifecycle, unified Editor, Trash-Restore)

## Befunde (Feld-Test auf main)

1. **Buttons/Dropdowns app-weit zu hoch:** Standard-Buttons (z.B. im Edit-Component-Fenster) sollen
   app-übergreifend ~40 % flacher werden; Referenz ist der „+ Material"-Button im
   Fabrication-Process-Editor (`FontSize="10" Padding="6,1"`). Dasselbe für ComboBoxen.
2. **Edit-Component-Fenster:** Die Overlay-Scrollbar schwebt ÜBER den Buttons/Controls und
   verdeckt sie beim Scrollen. Fenster darf minimal höher sein.
3. **Bug Trash-Restore:** „Restore" an einem gelöschten Component stellt ALLES wieder her, was im
   Papierkorb fehlt — nicht nur dieses eine Component. Ursache: `PdkTrashService.
   RestoreRemovedComponents` re-added die komplette Differenz Backup−Live; da jedes Backup eine
   Vollkopie der Datei vor dem jeweiligen Delete ist, enthält das älteste Backup alle später
   gelöschten Komponenten mit.
4. **Component Settings (Grid-Rechtsklick):** Der Button „Recalculate S-matrix (FDTD)…" ist
   outdated (kann die Geometrie nicht sehen, tut nichts Sinnvolles mehr). Logisch soll der
   Rechtsklick-Eintrag zum unified „Edit Component"-Editor führen (Spec
   2026-07-15-unified-editable-pdk-model: „Route Component Settings… and Edit… to the single
   editor" — für den Grid-Rechtsklick nicht fertig umgesetzt).
5. **Doppelte Edit-Fenster:** Zweimal auf ✏ klicken öffnet zwei „Edit Component"-Fenster —
   der zweite Klick soll das bestehende nur in den Vordergrund holen.
6. **Fenstertitel:** „Edit Component: <Name>" (Template-Name aus der Library, z.B. „test3").
7. **PDK-Management-Header:** Titel „PDK Mgmt"; Enable-All- und Disable-All-ICON-Buttons in den
   Header links neben das Papierkorb-Icon; die unteren „Enable All"/„Disable All"-Buttons
   entfallen. Das Prozess-Label („Playground — not manufacturable" bzw. Prozessname) unten im
   PDK-Bereich entfällt komplett — steht schon im HUD des Grids.

## Lösung

### 1. Globale Control-Höhen (T1)
App-weite Styles (in `App.axaml` bzw. dem zentralen Styles-Include): `Button` und `ComboBox`
bekommen reduzierte `MinHeight`/`Padding` in Richtung der „+ Material"-Referenz (~40 % flacher als
Avalonia-Fluent-Default). Buttons/Comboboxen mit expliziter `Height`/`Padding` im XAML bleiben
unangetastet (lokale Setter gewinnen). Sichtprüfung über den UI-Screenshot-Harness.

### 2. Edit-Component-Fenster (T2)
- ScrollViewer: Scrollbar nicht mehr über dem Inhalt (`ScrollViewer.AllowAutoHide="False"` bzw.
  rechtes Content-Padding in Scrollbar-Breite), damit nichts verdeckt wird.
- Fensterhöhe moderat erhöhen (z.B. 760 → 820, MaxHeight beachten).
- `NewComponentViewModel.WindowTitle`: im Edit-Modus „Edit Component: <TemplateName>".
- Fenster-Dedup: pro Template nur ein Editor-Fenster (Dictionary-Muster wie
  `_openPdkEditWindows` in `MainWindow.axaml.cs`; Key PdkSource+Name; zweiter Klick → Activate).

### 3. Trash-Restore pro Lösch-Vorgang (T3)
`PdkTrashService.ListEntries` expandiert `RemovedComponents`-Backups zu EINEM Eintrag pro
fehlendem Component (dedupe per Name über alle Backups, neuestes Backup gewinnt);
`Restore(entry)` re-added NUR die Komponente(n) dieses Eintrags. „DeletedPdk"-Einträge bleiben
wie sie sind. Statusmeldung nennt die konkrete Komponente.

### 4. Rechtsklick → unified Editor (T4)
Der Canvas-Kontextmenü-Eintrag („Component Settings" → „Edit Component…") öffnet den unified
„Edit Component"-Editor für die Template-Definition der angeklickten Komponente (gleicher Pfad
wie ✏ in der Library, inkl. fork-on-edit für bundled). Der `ComponentSettingsDialog` bleibt als
S-Matrix-Ansicht (vom Editor über „stored S-matrices" geöffnet).

**Revision nach Merge-Kollision:** Der ursprünglich als „tot" eingestufte
„Recalculate S-matrix (FDTD)…"-Button wurde parallel durch PR #743 (Issue #582) repariert und
ausgebaut (Wellenlängen-Sweep über die komponenten-eigenen Wellenlängen, Stale-Warnung,
Provenance-Tags). Er bleibt deshalb ERHALTEN — die Feld-Beobachtung „geht nicht" bezog sich auf
den Stand vor #743. Entfernt wird nichts; nur das Rechtsklick-Routing ändert sich.

### 5. PDK-Mgmt-Header (T5)
Header-Text „PDK Mgmt"; drei kompakte Icon-Buttons im Header (Reihenfolge: Enable All,
Disable All, Papierkorb, „+"), mit Tooltips; untere Button-Zeile entfällt; das
`ActiveProcessLabel`-TextBlock im PDK-Bereich entfällt.

## Constraints
Wie gehabt: keine erfundene Physik; compiled bindings; ≤250 Zeilen neue Dateien, bestehende ≤500;
platzierte Komponenten unangetastet; bundled JSONs read-only; kein `Process.Start`.

## Testing
- T3: Store-Tests — drei sequentielle Component-Deletes → drei Restore-Einträge (je 1 Komponente,
  dedupe), Restore(A) stellt nur A wieder her, B/C bleiben im Papierkorb restaurierbar.
- T2/T4: VM-Tests für WindowTitle und das Routing (Rechtsklick-Handler ruft Editor-Hook);
  Dedup per Handler-Logik-Test soweit ohne UI möglich.
- T1/T5: Screenshot-Harness aktualisieren; Build 0; bestehende Suiten grün.

## Out of Scope
Keine weiteren Editor-Features; keine Änderung der S-Matrix-Semantik; #740 (git-Versionierung)
separat.
