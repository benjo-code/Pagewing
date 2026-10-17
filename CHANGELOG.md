# Changelog

Alle nennenswerten Änderungen an diesem Projekt werden hier dokumentiert.

Format nach [Keep a Changelog](https://keepachangelog.com/de/1.1.0/), Versionierung nach [Semantic Versioning](https://semver.org/lang/de/). Jeder Release-Eintrag nennt die enthaltenen Milestones.

## [Unreleased]

### Added

- CLI `pagewing info`, `render`, `text`; PDFs öffnen (auch mit Passwort), Seiten und Kacheln rendern, Text extrahieren; Render-Regression und Benchmarks.
- Desktop-App: Hauptfenster im Design „Werkbank“ (helles und dunkles Theme, unter Windows mit Tab in der Titelleiste), Startbildschirm, Passwortkarte und Fehlerkarte.
- PDFs öffnen per Kommandozeilenargument, Dateidialog (Strg+O) und Drag & Drop.
- Single-Instance: Ein weiterer Aufruf übergibt die Datei an das laufende Fenster.
- Fortlaufendes Scrollen durch alle Seiten: erst Platzhalter und grobe Vorschau, dann scharfe Kacheln, gerendert wird nur der sichtbare Bereich.
- Zoom mit Strg+Mausrad, Strg+Plus/Minus und in der Statusleiste (10 % bis 800 %); Seitenwechsel mit Bild auf/ab und über das Seitenfeld.
- Scrollen mit der Tastatur: Pfeiltasten, Leertaste (Umschalt+Leertaste zurück), Pos1 und Ende.
- Oberfläche auf Deutsch und Englisch, nach Systemsprache.
- Schnelleres Öffnen unter Windows auf Rechnern ohne Grafikbeschleunigung (etwa in virtuellen Maschinen): Die App zeichnet dort ohne Direct3D; `PAGEWING_RENDERING=software` oder `default` erzwingt einen Modus.

### Changed

### Fixed

### Removed
