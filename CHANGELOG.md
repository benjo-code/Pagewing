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
- Beim ersten Start fragt eine Karte unten rechts, ob Pagewing der Standard für PDFs werden soll („Als Standard festlegen“, „Später“, „Nicht mehr fragen“); der Befehl „Pagewing als Standard für PDFs festlegen“ in der Befehlspalette tut dasselbe jederzeit. Unter Windows öffnet er die Standard-Apps-Einstellungen, in denen Sie Pagewing wählen.
- Die Quelltexte von libfuse und der AppImage-Runtime (LGPL-2.1) hängen an jedem Release (`fuse-3.15.0.tar.xz`, `type2-runtime-20251108.tar.gz`, Prüfsummen in `SHA256SUMS-linux.txt`).
- Karte „Über Pagewing“ (Befehlspalette) mit Version, Lizenz und den Hinweisen zu Drittsoftware.
- Installation: Windows-Installer (grafisch oder still mit eigenem Zielordner, für den Benutzer oder alle Benutzer, „Pagewing jetzt starten“, Eintrag als Programm für PDF) und portable ZIP; Linux `.deb` (Ubuntu, Debian, Linux Mint) und AppImage ohne `libfuse2`, auch für CachyOS. Alle Pakete enthalten die CLI und die Lizenzhinweise aller Drittbibliotheken (auch die der AppImage-Runtime). Der Windows-Installer ist nicht signiert, SmartScreen warnt.
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
- Dunkler Modus: Das Design folgt dem System oder steht fest auf Hell oder Dunkel und wechselt ohne Neustart. Auf Wunsch erscheinen im dunklen Design auch die Seiten dunkel (heller Text auf dunklem Grund, Bilder behalten ihre Farben); Strg+Umschalt+D schaltet die Seite eines Tabs zwischen hell und dunkel um. Einschränkung: Gefüllte Flächen wie Diagrammbalken oder Tabellenstreifen erscheinen auf der dunklen Seite nur als helle Umrisse, ihre Farbe geht verloren; gescannte Seiten bleiben hell.
- Einstellungen (Strg+Komma oder Knopf unten in der Aktionsleiste): Design, Dichte, dunkle Seiten, „Animationen reduzieren“, Standard-Layout und Standard-Zoom für neu geöffnete Dateien, Sprache der Oberfläche (gilt ab dem nächsten Start) und „Verlauf löschen“. Jede Änderung gilt sofort und wird nur lokal im Benutzerordner gespeichert.
- Befehlspalette (Strg+K oder „Suchen oder Befehl“ in der Titelleiste): Befehle mit ihren Kürzeln, zuletzt geöffnete Dateien, offene Tabs, „Gehe zu Seite“ per Zahl und Kapitel des Inhaltsverzeichnisses, ganz mit der Tastatur bedienbar.
- Tastenkürzel für Zoom, Navigation, Suche, Panels und Tabs und eine Übersicht mit Filter (F1). Neu: Strg+K (Befehlspalette), Strg+Komma (Einstellungen), F1 (Tastenkürzel), Strg+Umschalt+1 (Seiten), Strg+Umschalt+2 (Inhalt), Strg+Umschalt+D (Seite hell/dunkel).
- Zuletzt geöffnete Dateien auf der Startseite (die letzten fünf; Entf entfernt einen Eintrag) und in der Befehlspalette. Pagewing merkt sich je Datei Stelle, Zoom und Layout und öffnet sie dort wieder. Einschränkung: Die Datei, mit der Pagewing gestartet wird (etwa per Doppelklick, solange Pagewing noch nicht läuft), öffnet noch am Anfang.
- Dichte „Kompakt“: schmalere Leisten, kleinerer Seitenrand und die Seitenvorschau in zwei Spalten.
- „Animationen reduzieren“ schaltet die Einblendung des Panels ab.
- Hochkontrast unter Windows: Mit einem Kontrastdesign von Windows und dem Design „Wie im System“ zeigt Pagewing eine feste schwarze Palette mit weißem Text, weißen Trennlinien und gelbem Akzent; die Seiten bleiben hell.
- Screenreader: Die Einträge der Aktionsleiste melden, ob ihr Panel offen ist, der aktive Tab meldet sich als ausgewählt, die Befehlspalette meldet den gewählten Eintrag (mit Narrator, NVDA und Orca noch nicht geprüft).
- Drucken (Strg+P oder der Druckknopf unten in der Aktionsleiste) unter Windows und Linux: Seitenbereich (alle, aktuelle Seite, Bereich wie `1-3,5`), Kopien, Sortieren, Duplex, Hoch-/Querformat und „An Papier anpassen“ oder „Tatsächliche Größe“; jede Seite wird gedreht und zentriert, wenn sie so besser aufs Papier passt. Windows nutzt den Druckdialog des Systems, Linux einen eigenen Dialog und CUPS. Der Druck läuft im Hintergrund mit Fortschritt und „Drucken abbrechen“; gedruckt wird immer hell. Ein PDF, das das Drucken verbietet, wird nicht gedruckt. „Seitengröße beim Drucken“ steht in den Einstellungen.

### Changed

- Ein späterer Aufruf, das Ablegen einer Datei und Strg+O öffnen die Datei in einem neuen Tab, statt das angezeigte Dokument zu ersetzen; mehrere Dateien auf einmal öffnen höchstens 20 Tabs.
- Weniger Arbeitsspeicher beim schnellen Scrollen durch große Dokumente.
- Pagewing merkt sich, welches Panel zuletzt gewählt war (Seiten, Inhalt oder keines), und die Panelbreite; das nächste Fenster beginnt damit.
- Die Tooltips von „Seiten“ und „Inhalt“ nennen ihr Tastenkürzel.

### Fixed

- Ein Start mit mehreren Dateien (`pagewing a.pdf b.pdf`) öffnet jede als Tab, nicht nur die erste; der Tab der ersten Datei bleibt aktiv.
- Dieselbe Datei erneut öffnen, deren Tab eine Fehlerkarte zeigt, versucht es in diesem Tab noch einmal, statt nur die alte Fehlermeldung zu zeigen.
- Windows, Drucken: Im Druckdialog „Seiten“ mit einer einzelnen Seitenzahl (zum Beispiel `3`) druckte Pagewing die im Betrachter angezeigte Seite statt der gewählten. Jetzt wird die gewählte Seite gedruckt.
- Linux, AppImage: Wird das AppImage nach „Als Standardprogramm festlegen“ verschoben oder umbenannt, erscheint die Karte wieder (der Menüeintrag zeigte vorher auf den alten Pfad); „Als Standard festlegen“ schreibt ihn neu.
- Linux, „Als Standardprogramm festlegen“: Das AppImage trägt jetzt auch die Symbole in 22 und 96 Pixeln ein. Läuft Pagewing aus einem Ordner, ohne installiertes Paket und ohne AppImage, meldet die Karte, dass es von dort nicht geht, statt einen Standard zu setzen, der ins Leere zeigt.

### Removed
