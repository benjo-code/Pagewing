# Changelog

Alle nennenswerten Änderungen an diesem Projekt werden hier dokumentiert.

Format nach [Keep a Changelog](https://keepachangelog.com/de/1.1.0/), Versionierung nach [Semantic Versioning](https://semver.org/lang/de/). Jeder Release-Eintrag nennt die enthaltenen Milestones.

## [Unreleased]

### Added

- Automatische Updates (wirken ab dem ersten Release mit Updater, also für Updates **von** dieser Version aus; `v0.1.0` hat keinen Updater, von dort steigt man einmal von Hand um): Pagewing prüft nach dem Start im Hintergrund auf neue Versionen, höchstens einmal in 24 Stunden (abschaltbar in den Einstellungen unter „Aktualisierungen“; „Nach Updates suchen“ in der Befehlspalette prüft jederzeit und meldet auch „aktuell“ oder „konnte nicht prüfen“). Gibt es eine, erscheint in der Titelleiste die Schaltfläche „Aktualisieren“ (englisch „Update“); ein Klick zeigt die Karte mit „Installieren“ und „Später“. Installiert wird nur auf Klick, mit einem Fortschrittsfenster, und nur, wenn Signatur und Prüfsumme passen; danach soll Pagewing neu starten und die Tabs an ihrer Stelle zurückbringen. Selbst aktualisieren sollen sich die Windows-Installation (bei „alle Benutzer“ mit UAC-Abfrage) und das AppImage; das `.deb` und die portable ZIP zeigen einen Link zur Download-Seite. Neustart, Tabs und Selbstaktualisierung sind bisher nur mit Testattrappen geprüft; ein echtes Update wird erst zwischen zwei Releases mit Updater möglich. Die Prüfung fragt nur die öffentliche Release-Seite von Pagewing auf GitHub und sendet keine Kennung und keine Angaben zum Rechner; Startseite und „Über Pagewing“ sagen das. Ins Netz gehen nur die Prüfung und der Download eines gewählten Updates.
- Speichern (Strg+S) und „Speichern unter“ (Strg+Umschalt+S): Pagewing hängt Änderungen an die Datei an (inkrementell), schreibt zuerst in eine temporäre Datei im selben Ordner, öffnet und prüft sie und ersetzt das Original erst danach; die Bytes des Originals bleiben unverändert am Anfang der Datei. Scheitert etwas (schreibgeschützt, Platte voll, Datei von einem anderen Programm geöffnet), bleibt das Original unberührt und eine Karte nennt den Grund. Wurde die Datei seit dem Öffnen ersetzt, fragt Pagewing vor dem Überschreiben; verschlüsselte Dateien bleiben verschlüsselt. Pagewing speichert nie von selbst.
- Ungespeicherte Änderungen zeigen ein Punkt im Tab und „Nicht gespeichert“ in der Statusleiste. Beim Schließen eines Tabs oder Fensters mit Änderungen fragt eine Karte „Speichern“, „Nicht speichern“, „Abbrechen“ (mehrere Tabs nacheinander); „Nicht speichern“ ist nach dem Erscheinen kurz gesperrt. Auch „Installieren“ eines Updates fragt vorher nach ungespeicherten Änderungen (und kurz vor dem Installer noch einmal nach dem, was sich während des Downloads geändert hat); ab dieser Antwort ist das Bearbeiten bis zum Neustart gesperrt.
- Textmarkierungen: Hervorheben (Strg+Umschalt+H), Unterstreichen (Strg+Umschalt+U) und Durchstreichen (Strg+Umschalt+X) von markiertem Text, in fünf Farben (Gelb, Grün, Blau, Rosa, Orange; die Farbe merkt sich Pagewing je Art). Jede Markierung bekommt ein eigenes Erscheinungsbild (die Hervorhebung liegt hinter dem Text), damit Chrome und Acrobat Reader sie wie Pagewing zeigen; in beiden geprüft ist das an einer Beispieldatei, gedrehte Seiten, versetzte Seitenausschnitte und echt signierte Dateien stehen noch aus. Markierungen werden mitgedruckt.
- Modus „Kommentieren“ (Strg+Umschalt+K oder Knopf „Kommentieren“ im Panel „Kommentare“) mit eigener Werkzeugleiste: Werkzeug wählen, Text markieren, beim Loslassen der Maus entsteht die Markierung; „Fertig“ oder Esc beendet den Modus. Die Leseansicht zeigt weiter keine Werkzeuge. Erlaubt ein Dokument keine Kommentare, sagt das Panel warum.
- Panel „Kommentare“ (Strg+Umschalt+3): alle Annotationen des Dokuments, auch aus anderen Programmen, mit Art, Autor, Datum, markiertem Text und Kommentar, Filter nach Art und Sprung zur Stelle. Im Modus „Kommentieren“ lassen sich Kommentar, Farbe und Löschen für Hervorhebungen, Unterstreichungen und Durchstreichungen bearbeiten (auch fremde ohne Antworten). Links und Formularfelder stehen nicht in der Liste. Den Autor der eigenen Kommentare legen Sie in den Einstellungen fest (Standard leer).
- Rückgängig (Strg+Z) und Wiederholen (Strg+Umschalt+Z oder Strg+Y) für Markierungen und Kommentare; in einem Textfeld gelten die Tasten weiter für den Text. Nach dem Speichern beginnt der Verlauf neu.
- Signierte Dokumente: Vor dem Speichern liest Pagewing die Rechte der Signatur. Erlaubt sie keine Kommentare, warnt eine Karte, dass Programme wie Acrobat sie danach als ungültig anzeigen (Standard „Abbrechen“); sonst sagt ein Hinweis, dass die signierten Bytes unverändert bleiben. Pagewing prüft keine Signatur und nennt keine „gültig“.
- Markierungen auf der dunklen Seite: Hervorhebungen erscheinen als getönte Fläche hinter dem Text, Unterstreichungen und Durchstreichungen in ihrer Farbe (bisher wurden Hervorhebungen dort unsichtbar).
- Drucken unter Linux mit ungespeicherten Markierungen: Pagewing übergibt eine Kopie aus dem Speicher, sodass gedruckt wird, was der Tab zeigt (unter Windows war das schon so).

### Changed

- Das `.deb` verlangt zusätzlich `libssl3` (bzw. `libssl3t64`) für die Update-Prüfung.

### Fixed

- Seltener Absturz beim Rendern („attempted to call a UnmanagedCallersOnly method from managed code“, unter Windows Exit-Code `0xC0000602`), wenn während des Zeichnens einer Seite eine Speicherbereinigung lief. Betrifft auch `v0.1.0` (der Fehler steckt seit Milestone 1 im Rückruf, mit dem PDFium beim Zeichnen fragt, ob es pausieren soll: Er las die Uhr so, dass die Laufzeit nach dem Rücksprung in PDFium in einem falschen Zustand war). Mit Markierungen auf der Seite trat er häufiger auf; unter Linux ist die Behebung noch nicht eigens bestätigt.

## [0.1.0] – 2026-10-23

Enthaltene Milestones: 0 (Repo-Grundgerüst und CI), 1 (PDFium-Anbindung und Render-Kern), 2 (Designrichtungen), 3 (App-Shell und schnelles Öffnen), 4 (Navigation), 4.1 (Tabs zwischen Fenstern ziehen), 5 (Text und Suche), 6 (Komfort), 7 (Drucken) und 8 (Verteilung). Pagewing ist in diesem Release ein schneller Viewer; Bearbeiten kommt in späteren Versionen.

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
