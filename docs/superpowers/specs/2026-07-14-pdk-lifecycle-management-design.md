# PDK-Lifecycle-Management — Design

**Datum:** 2026-07-14
**Status:** Freigegeben (Feld-Test-Feedback; Entscheidungen: Divergenz+Warnung+Konflikt, Papierkorb)
**Baut auf:** #732/#733/#736

## Probleme (Feld-Test)

1. **Alte custom PDKs fehlen in der PDK-Management-Liste** (und beim Start), erscheinen aber im
   „+"-New-Component-Dropdown. Ursache = **#700**: User-PDKs werden beim App-Start nie geladen
   (`UserPreferencesService.GetUserPdkPaths()` hat keinen Aufrufer); die Management-Liste kennt nur
   session-registrierte PDKs, während der Dialog das `user-pdks`-Verzeichnis direkt scannt
   (`UserPdkStore.ListCustomPdks()`).
2. **Prozess-Edit ohne Konsequenz:** NITRIDE-Layer 203→2030 editiert + gespeichert → Komponenten des
   PDKs bleiben platzierbar, obwohl der Prozess real nicht mehr zum Design passt. Ursache: der
   Prozess-Fingerprint (Materialien/Dicke/Wellenlänge) enthält **keine Layer-Zuordnung** — real
   bestimmt die Layer-Nummer aber die Maske; gemischte Nummerierung = nicht fertigbarer Chip.
3. **Fehlende Verwaltung:** kein „+" (PDK ohne Komponente anlegen), kein Löschen von PDKs/Komponenten,
   kein Schutz vor Versehen.

## Entscheidungen (User)

- **Prozess-Edit:** Divergenz + Warnung + Konflikt-Anzeige (Option A). Keine Propagation auf andere
  PDK-Dateien.
- **Löschen:** Papierkorb-Modell — Delete mit Confirm, Datei wandert nach `user-pdks/.trash/`
  (wiederherstellbar); echte Versionierung (git) als Follow-up-Issue.

## Lösung

### 1. #700: User-PDKs beim Start laden
Beim App-Start (nach dem Laden der bundled PDKs) werden geladen und regulär registriert
(Templates + `PdkManager.RegisterPdk` + Prozess-Lock-Reapply, derselbe Pfad wie
`LoadPdkFromJsonFileAsync`):
- alle `*.json` im `user-pdks`-Verzeichnis (`UserPdkStore.ListCustomPdks()`-Quelle, auch PDKs OHNE
  Komponenten — sie sollen in der Management-Liste erscheinen),
- plus die in den Preferences gemerkten Import-Pfade (`GetUserPdkPaths()`, für außerhalb liegende
  importierte PDKs), dedupliziert per Pfad; fehlende Dateien tolerant überspringen (und aus den
  Prefs bereinigen).

### 2. „+"-Button im PDK-Management
Ein „+"-Button im PDK-Management-Panel öffnet direkt den bestehenden `CreateCustomPdkWindow`
(ohne New-Component-Umweg). Nach erfolgreichem Anlegen wird das (ggf. komponentenlose) PDK sofort
registriert (Liste + Lock-Reapply) — kein Neustart nötig.

### 3. Layer-Stack wird Teil der (Live-)Prozess-Kompatibilität
- Neue Prüfung `ProcessCompatibility.LayersConsistent(a, b)`: für jeden Layer-**Namen**, der in
  BEIDEN Prozessen existiert (case-insensitiv), müssen `(Layer, Datatype)` übereinstimmen.
  **Zusätzliche Layer sind erlaubt** (z.B. custom PDK ergänzt einen Metall-Layer → bleibt
  kompatibel, #734-Workflow intakt); nur **widersprüchliche Nummern desselben Layers** divergieren.
- Integration in die **Live**-Auflösung (`ResolveLiveMemberPdkNames`): ein geladenes PDK ist Mitglied,
  wenn Fingerprint-kompatibel UND layers-konsistent mit der Referenz-Prozessdefinition des aktiven
  Prozesses (Referenz = Prozessdefinition eines geladenen Snapshot-Mitglieds; wenn keines geladen →
  nur Fingerprint wie bisher). **Kein Persistenzformat-Change** (der gespeicherte Fingerprint bleibt
  unverändert); die Verschärfung wirkt nur auf die Laufzeit-Menge — dieselbe Quelle, die seit #736
  Platzierung/Paste/Gruppen/AiGrid/Metall-Spec speist. Damit blockt der 203→2030-Edit automatisch
  neue Platzierungen und deaktiviert das PDK.
- **Divergenz-Warnung beim Speichern:** Nach `ProcessSaved` (per-PDK-Editor) wird der Lock neu
  aufgelöst; ist das editierte PDK dadurch NICHT mehr Mitglied UND liegen bereits Komponenten dieses
  PDKs auf dem Canvas, erscheint eine Warnung („Der gespeicherte Prozess weicht jetzt vom
  Design-Prozess ab — N platzierte Komponenten sind konfliktbehaftet; neue Platzierungen sind
  blockiert."). Platzierte Komponenten werden NICHT gelöscht.
- **Konflikt-Anzeige:** Ein Design-Check (DesignChecksPanel) listet platzierte Komponenten, deren
  `PdkSource` nicht (mehr) in der effektiven Mitglieder-/Enabled-Menge ist, als Fehler-Einträge.

### 4. Löschen mit Papierkorb
- **PDK löschen:** „Delete…"-Button an custom (nicht-bundled) PDK-Zeilen. Confirm-Dialog (nennt
  Komponentenzahl). Die JSON wird nach `user-pdks/.trash/<slug>-<timestamp>.json` VERSCHOBEN
  (`UserPdkStore.MoveToTrash`), dann deregistriert (Templates raus, `LoadedPdks`-Eintrag raus,
  Drafts raus, Lock-Reapply, Prefs-Pfad raus).
- **Komponente löschen:** „Delete…"-Kontextmenüpunkt an custom Komponenten in der Library (neben
  „Edit…"). Confirm-Dialog. Vor dem Umschreiben der PDK-Datei wird eine Kopie nach `.trash`
  gelegt (`<slug>-<timestamp>.json`), dann die Komponente entfernt
  (`UserPdkStore.RemoveComponent`), Template deregistriert.
- Bundled PDKs: weder Delete noch Component-Delete (read-only, unverändert).
- Follow-up-Issue: git-basierte Versionierung der User-PDK-Bibliothek.

## Constraints
Wie gehabt: keine erfundene Physik; #570-Integrität (Layer-Konsistenz macht sie strenger, nie
lockerer); Foundry-JSONs nie geschrieben/gelöscht; kein `Process.Start`; compiled bindings;
`InvariantCulture`; ≤250 Zeilen neue Dateien, bestehende ≤500. Platzierte Komponenten werden nie
still entfernt.

## Testing
- Startup-Reload: PDKs im user-pdks-Ordner (mit+ohne Komponenten) + Prefs-Pfade → nach Init in
  `LoadedPdks` + Templates; fehlender Pfad → kein Crash + Prefs bereinigt.
- LayersConsistent: gleicher Layer andere Nummer → inkonsistent; zusätzlicher Layer → konsistent;
  Live-Auflösung schließt renumbered PDK aus (Platzierung geblockt), Metall-Ergänzung bleibt drin.
- Divergenz-Warnung: Save mit Renumbering + platzierte Komponenten → Warn-Hook gefeuert mit N.
- Design-Check: platzierte Komponente eines nicht-mehr-Mitglieds → Fehler-Eintrag.
- Trash: Delete verschiebt (Datei existiert in .trash, nicht mehr im Root), deregistriert;
  Component-Delete backupt + entfernt; Confirm=false → no-op.
- Bundled: kein Delete/Component-Delete möglich.

## Out of Scope
#726 (aktiven Design-Prozess umstellen), #737 (Placement-Kontext), Prozess-Propagation, git-Versionierung (Follow-up-Issue), Wiederherstellen-UI für .trash (Datei-Manager reicht v1).
