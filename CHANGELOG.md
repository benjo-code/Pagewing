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
- Schnelleres Öffnen unter Windows: Die App zeichnet auf allen Rechnern ohne Direct3D (Software-Rendering), auch mit Grafikkarte; `PAGEWING_RENDERING=default` schaltet die Grafikbeschleunigung über Direct3D wieder ein.
- Neues App-Icon (Seite mit Welle) für die Exe unter Windows und das Fenster unter Linux.
- Tabs: mehrere PDFs in einem Fenster öffnen, schließen (×, Mittelklick, Strg+W), wechseln (Klick, Strg+Tab, Strg+Umschalt+Tab), per Ziehen umsortieren, bei Platzmangel über „Alle Tabs“; dieselbe Datei ein zweites Mal öffnen aktiviert ihren Tab.
- „In neuem Fenster öffnen“ im Kontextmenü eines Tabs: verschiebt den Tab samt Seite und Zoom in ein eigenes Fenster, ohne die Datei neu zu öffnen.
- Aktionsleiste mit Panel „Seiten“ (Seitenvorschau) und „Inhalt“ (Inhaltsverzeichnis aus den Lesezeichen des PDFs); bei breitem Fenster öffnet „Seiten“ nach dem ersten scharfen Seitenbild.
- Layouts: Einzelseite, fortlaufend, doppelseitig mit und ohne Deckblatt (Knopf in der Statusleiste).
- Zoom-Modi Seitenbreite (Strg+2), ganze Seite (Strg+0) und tatsächliche Größe (Strg+1), im Menü am Zoomwert der Statusleiste.
- Gehe zu Seite mit Strg+G.
- Dokumentinfos in der Statusleiste: Datei, Ordner, Seitenzahl, Seitenformat, PDF-Version, Dateigröße, Titel, Autor und Erstelldatum.

### Changed

- Ein späterer Aufruf, das Ablegen einer Datei und Strg+O öffnen die Datei in einem neuen Tab, statt das angezeigte Dokument zu ersetzen; mehrere Dateien auf einmal öffnen höchstens 20 Tabs. Auch der erste Aufruf mit mehreren Dateien öffnet jede als Tab; der Tab der ersten Datei bleibt aktiv.
- Weniger Arbeitsspeicher beim schnellen Scrollen durch große Dokumente.

### Fixed

### Removed
