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
- Tabs lassen sich aus der Leiste ziehen: Das neue Fenster hängt am Zeiger, über der Tableiste eines Fensters zeigt ein Strich die Stelle, beim Loslassen landet der Tab dort (mit Seite und Zoom); Esc bricht ab. Unter Windows geprüft, Linux/X11 offen.
- „In neuem Fenster öffnen“ im Kontextmenü eines Tabs: verschiebt den Tab samt Seite und Zoom in ein eigenes Fenster, ohne die Datei neu zu öffnen.
- Aktionsleiste mit Panel „Seiten“ (Seitenvorschau) und „Inhalt“ (Inhaltsverzeichnis aus den Lesezeichen des PDFs); bei breitem Fenster öffnet „Seiten“ nach dem ersten scharfen Seitenbild.
- Layouts: Einzelseite, fortlaufend, doppelseitig mit und ohne Deckblatt (Knopf in der Statusleiste).
- Zoom-Modi Seitenbreite (Strg+2), ganze Seite (Strg+0) und tatsächliche Größe (Strg+1), im Menü am Zoomwert der Statusleiste.
- Gehe zu Seite mit Strg+G.
- Dokumentinfos in der Statusleiste: Datei, Ordner, Seitenzahl, Seitenformat, PDF-Version, Dateigröße, Titel, Autor und Erstelldatum.
- Suche im Dokument (Strg+F): Panel „Suche“ mit Trefferliste, Treffer auf der Seite hinterlegt, Weiter/Zurück mit Enter, Umschalt+Enter, F3 und Umschalt+F3; die Suche läuft im Hintergrund, löst Ligaturen in der Suche auf und findet Wörter über die Silbentrennung am Zeilenende hinweg. Optionen „Groß-/Kleinschreibung beachten“ und „Ganze Wörter“, höchstens 10.000 Treffer.
- Text markieren (Ziehen, Doppelklick = Wort, Dreifachklick = Zeile) und kopieren: Strg+C kopiert formatierten Fließtext (Absätze, ohne Trennstriche am Zeilenende, mit Fett, Kursiv und Überschriften), Strg+Umschalt+C den Rohtext mit den Zeilenumbrüchen des PDFs. Eine Seite ohne Text (Scan) zeigt einen Hinweis.
- Links im PDF: interne Sprünge zu Seite und Stelle (Alt+Links und Alt+Rechts gehen zurück und vor), externe Adressen (`http`, `https`, `mailto`) öffnen erst nach einer Bestätigung mit der vollständigen Adresse und Warnung bei täuschenden Adressen („Öffnen“ ist nach dem Erscheinen der Karte kurz gesperrt, damit ein Doppelklick nichts öffnet); andere Ziele (Dateien, Programme, JavaScript) sind nicht klickbar.
- CLI `pagewing search <datei.pdf> <abfrage>` gibt je Treffer Seite, Zeichenindex und Textausschnitt aus (`--pages`, `--match-case`, `--whole-word`, `--password`); `--` beendet die Optionen bei allen Befehlen.

### Changed

- Ein späterer Aufruf, das Ablegen einer Datei und Strg+O öffnen die Datei in einem neuen Tab, statt das angezeigte Dokument zu ersetzen; mehrere Dateien auf einmal öffnen höchstens 20 Tabs.
- Weniger Arbeitsspeicher beim schnellen Scrollen durch große Dokumente.

### Fixed

- Ein Start mit mehreren Dateien (`pagewing a.pdf b.pdf`) öffnet jede als Tab, nicht nur die erste; der Tab der ersten Datei bleibt aktiv.
- Dieselbe Datei erneut öffnen, deren Tab eine Fehlerkarte zeigt, versucht es in diesem Tab noch einmal, statt nur die alte Fehlermeldung zu zeigen.

### Removed
