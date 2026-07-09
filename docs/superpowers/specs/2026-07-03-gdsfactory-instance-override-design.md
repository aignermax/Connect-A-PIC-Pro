# Per-instance structure override for gdsfactory (Issue #637)

**Datum:** 2026-07-03 · Baut auf #581 (gdsfactory-Export, gemergt).
**Ground Truth:** gdsfactory 9.34.2 + ubcpdk 3.3.4.

## Ziel
Der per-Instanz-Code-Override in den Component Settings (heute Nazca-only, #556/#559)
soll auch **gdsfactory**-Code unterstützen. UX (vom Nutzer): ein **Segment-Toggle
„Nazca | gdsfactory"** oben im Editor; er schaltet Hilfetext/Beispiel/Docs und das
Backend für Preview + Apply um. Darunter unverändert: Code-Editor, Run Preview,
Apply, Size/Pin-Recompute.

## Verifizierter Kern (dieser PR)
`scripts/render_gdsfactory_preview.py`: führt Nutzer-gdsfactory-Code aus und emittiert
**denselben JSON-Kontrakt** wie `render_component_preview.py`
(`{success, bbox, polygons:[{layer,vertices}], pins:[{name,x,y,angle}]}`) — verifiziert
gegen gf 9.34.2 (`gf.components.mmi1x2`, echte `ubcpdk`-Zelle, Fehlerfall). Damit ist
die **Preview-Plumbing wiederverwendbar** (gleiche `--code-file`-CLI wie der
Nazca-Raw-Code-Modus): der Editor bekommt einen zweiten Preview-Service, der auf dieses
Skript zeigt, und routet nach Backend.

- Component-Deklaration im Nutzercode: Variable `component` (oder `c`, oder die einzige
  `gf.Component` im Scope). `gf.gpdk.PDK.activate()` läuft vorab, damit Layer-Tupel ohne
  eigenes PDK auflösen; aktiviert der Nutzercode ein PDK (z. B. ubcpdk), gewinnt das.

## Verbleibende Schritte (Folge-PRs, klar geschnitten)
1. **Datenmodell**: `NazcaCodeOverride.Backend` (enum `Nazca|GdsFactory`, Default `Nazca`
   für Rückwärtskompatibilität) + Clone/Persistenz.
2. **Preview-Service-Wiring**: DI erzeugt einen Preview-Service auf das gf-Skript;
   Editor-VM erhält beide Services und wählt nach `SelectedBackend`.
3. **Editor-VM + XAML**: `SelectedBackend`-Toggle, Hilfetext/Beispiel je Backend,
   Preview/Apply route zum gewählten Backend. (Der VM ist bereits 495 Zeilen — die
   backend-neutralen Teile beim Erweitern faktorisieren.)
4. **Export-Anbindung**: `GdsFactoryExporter` respektiert gf-Backend-Overrides (emittiert
   den Nutzer-Code als Component-Factory statt Stub); `SimpleNazcaExporter` ignoriert
   gf-Overrides. Nazca-Overrides bleiben unverändert der Default.

## Nicht-Ziele
- Kein Zwang, denselben Code für beide Backends zu schreiben (Backend ist pro Override
  getaggt).
- Kein Auto-Konvertieren bestehender Nazca-Overrides.

## Tests / Verifikation
- Renderer-E2E gegen den gf-Env (skippt ohne Installation).
- VM-Tests: Backend-Toggle schaltet Hilfetext + Preview-Route; Apply mit gf-Backend
  übernimmt Größe/Pins aus dem gf-Render.
- Export-Test: gf-Backend-Override erscheint als Factory im gf-Skript.
