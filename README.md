# Pagewing

Schnelle Open-Source-Alternative zu Adobe Acrobat für Windows und Linux. PDFs öffnen so schnell wie im Browser. Man kann sie lesen, durchsuchen, kommentieren, ausfüllen, unterschreiben, Seiten organisieren, schwärzen und per OCR durchsuchbar machen. Pagewing läuft als Desktop-App mit modernem Design oder als Kommandozeilen-Werkzeug `pagewing`. Offline, ohne Konto, ohne Telemetrie; ins Netz gehen nur die abschaltbare Update-Prüfung und der Download eines Updates, das man selbst installieren lässt.

**Status:** [v0.1.0](https://github.com/benjo-code/Pagewing/releases/tag/v0.1.0) (schneller Viewer) ist veröffentlicht, [v0.2.0](https://github.com/benjo-code/Pagewing/releases/tag/v0.2.0) (annotieren, ausfüllen, unterschreiben, automatische Updates) ist veröffentlicht, weitere Versionen sind in Entwicklung. Siehe [Docs/ROADMAP.md](Docs/ROADMAP.md) und [CHANGELOG.md](CHANGELOG.md).

> **English:** Pagewing is a fast, open-source alternative to Adobe Acrobat for Windows and Linux: view, search, annotate, fill, sign, organize, redact and OCR PDFs. Desktop app and CLI share one core. Offline apart from the update check (can be switched off) and the download of an update you choose to install, no telemetry. Built with .NET 10 (Native AOT), Avalonia and PDFium. Licensed under GPL-3.0-or-later. Project docs are in German; the app UI is English and German.

## Geplanter Funktionsumfang

| Version | Inhalt |
|---|---|
| v0.1.0 | Schneller Viewer: Tabs, Seitenvorschau, Inhaltsverzeichnis, Volltextsuche, Text kopieren, Layouts und Zoom, dunkler Modus, Befehlspalette (Strg+K), Drucken, Installer |
| v0.2.0 | Annotieren (Hervorheben, Zeichnen, Formen, Kommentare), Formulare ausfüllen, sichtbare Unterschrift, automatische Updates |
| v1.0.0 | Seiten organisieren (umsortieren, drehen, zusammenführen, teilen), Schwärzen, OCR für gescannte PDFs, Text in Grenzen bearbeiten |

Bewusst nicht geplant sind unter anderem Zertifikatssignaturen, XFA-Formulare, JavaScript in PDFs, PDF-nach-Word und Cloud-Funktionen (Details in der Roadmap unter „Out of Scope“).

## Installation

Die Pakete hängen am [GitHub-Release](https://github.com/benjo-code/Pagewing/releases) (Dateinamen und Bauanleitung: [packaging/README.md](packaging/README.md)); neben jedem Betriebssystem liegt eine `SHA256SUMS-<os>.txt` zum Prüfen der Downloads.

**Updates und Netz:** Ab `v0.2.0`, dem ersten Release mit Updater (`v0.1.0` enthält ihn nicht, von dort steigt man einmal von Hand um), sucht die Desktop-App nach dem Start im Hintergrund nach einer neuen Version, höchstens einmal in 24 Stunden. Sie fragt dafür nur die öffentliche Release-Seite von Pagewing auf GitHub und sendet keine Kennung, keine Version und keine Angaben über den Rechner; GitHub sieht wie jede Website die IP-Adresse. Abschalten lässt sich das in den Einstellungen („Automatisch nach Updates suchen“); danach geht Pagewing nur noch ins Netz, wenn man in der Befehlspalette „Nach Updates suchen“ wählt. Netzzugriff gibt es damit genau zweimal: für die Update-Prüfung und für den Download eines Updates, das man mit „Installieren“ gewählt hat. Installiert wird nur nach diesem Klick und nur, wenn Signatur und Prüfsumme passen; danach sollen sich die Tabs wieder öffnen. Selbst aktualisieren sollen sich die Windows-Installation (für „alle Benutzer“ mit UAC-Abfrage) und das AppImage; das `.deb` und die portable ZIP zeigen einen Link zur Download-Seite. Neustart, Tabs und Selbstaktualisierung sind bisher nur mit Testattrappen geprüft; ein echtes Update ist erst von `v0.2.0` auf die nächste Version möglich und damit dort der erste echte Lauf.

**Windows (10 und neuer, 64 Bit)**

- Installer `Pagewing-<Version>-win-x64-setup.exe`: grafisch mit Lizenz, Zielordner und der Frage „Pagewing jetzt starten?“. Standard ist die Installation nur für den eigenen Benutzer ohne Administratorrechte; für alle Benutzer wählt man es im Dialog oder startet mit `/ALLUSERS`. Der Installer ist nicht signiert, SmartScreen warnt deshalb („Weitere Informationen“, „Trotzdem ausführen“).
- Still, mit eigenem Zielordner: `Pagewing-<Version>-win-x64-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR="C:\Tools\Pagewing"` (für alle Benutzer zusätzlich `/ALLUSERS` aus einer Konsole mit Administratorrechten).
- Portabel: `Pagewing-<Version>-win-x64-portable.zip` entpacken und `Pagewing.exe` starten. Die CLI liegt im Unterordner `cli\`. Einstellungen und Verlauf liegen auch hier in `%APPDATA%\Pagewing`.

**Linux (x86-64)** – unterstützt sind Ubuntu 22.04 und neuer, Debian 12 und neuer, Linux Mint 21 und neuer sowie CachyOS (glibc ab 2.35; die Linux-Pakete entstehen im Container `ubuntu:22.04`).

- `.deb`: `sudo apt install ./pagewing_<Version>_amd64.deb`. Das Paket legt Starter, Symbole und die Zuordnung zu PDF an; `pagewing` im Pfad ist die CLI.
- AppImage: `chmod +x Pagewing-<Version>-x86_64.AppImage`, dann starten (Doppelklick oder Terminal). Es braucht kein `libfuse2`; fehlt FUSE ganz (etwa in einem Container), starten Sie es mit `APPIMAGE_EXTRACT_AND_RUN=1`. Die Runtime des AppImage enthält libfuse unter LGPL-2.1; dessen Quelltext (`fuse-3.15.0.tar.xz`) und die Bauskripte der Runtime (`type2-runtime-20251108.tar.gz`) hängen an jedem Release. Beim ersten Start bietet die Karte (unten) an, Pagewing als Standard für PDFs festzulegen; das AppImage trägt dabei Starter und Symbol in den Benutzerordner (`~/.local/share`) ein, sodass es im Anwendungsmenü erscheint.

**Standardprogramm für PDFs:** Beim ersten Start fragt eine Karte, ob Pagewing der Standard werden soll („Als Standard festlegen“, „Später“, „Nicht mehr fragen“). Unter Linux setzt Pagewing den Standard selbst; Windows erlaubt das nicht still, deshalb öffnet die Karte die Einstellungen von Windows, in denen man Pagewing wählt. Dieselbe Funktion steht jederzeit in der Befehlspalette (Strg+K): „Pagewing als Standard für PDFs festlegen“.

## Technik

- .NET 10 mit Native AOT für kurze Startzeit
- [Avalonia](https://avaloniaui.net/) 12 als UI-Framework
- [PDFium](https://pdfium.googlesource.com/pdfium/), die PDF-Engine von Chrome, ohne JavaScript-Unterstützung (Binaries von [bblanchon/pdfium-binaries](https://github.com/bblanchon/pdfium-binaries))
- Später [Tesseract](https://github.com/tesseract-ocr/tesseract) für OCR

Architektur und Entscheidungen stehen in der [Gesamt-Spec](Docs/superpowers/specs/2026-10-16-pagewing-design.md) und in [Docs/DECISIONS.md](Docs/DECISIONS.md).

## Setup

Das Code-Grundgerüst steht (Milestone 0): Solution mit `Pagewing.Pdfium`, `Pagewing.Core`, `Pagewing.App` (leeres Avalonia-Fenster) und `Pagewing.Cli`, jeweils als Native-AOT-Binary baubar, dazu CI auf Linux und Windows. Milestone 1 ist in Arbeit: Der Kern öffnet PDFs, rendert Seiten und Kacheln und extrahiert Text, nutzbar über die CLI (`pagewing info`, `render`, `text`, siehe unten). Die App selbst zeigt noch kein PDF; das folgt ab Milestone 3.

**Voraussetzungen:** .NET-10-SDK. Für den AOT-Publish braucht es zusätzlich die nativen Build-Werkzeuge:

- Windows: Visual Studio Build Tools mit „Desktopentwicklung mit C++“, und `vswhere.exe` im PATH (Ordner `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer`).
- Linux: `clang` und `zlib1g-dev` (`sudo apt-get install -y clang zlib1g-dev`).

Für selbst gehostete Windows-Runner kommt die PowerShell-Ausführungsrichtlinie `RemoteSigned` für LocalMachine hinzu (`Set-ExecutionPolicy -Scope LocalMachine -ExecutionPolicy RemoteSigned -Force` in einer Windows PowerShell mit Administratorrechten). Details stehen in `CLAUDE.md`, Abschnitt „CI“.

```bash
dotnet build Pagewing.slnx
dotnet test Pagewing.slnx
dotnet format Pagewing.slnx --verify-no-changes
dotnet publish src/Pagewing.App -c Release -r linux-x64   # bzw. win-x64, nur auf dem jeweiligen OS
```

Weitere Befehle:

```bash
dotnet test tests/Pagewing.Render.Tests                                              # Render-Regression gegen die Referenzbilder
PAGEWING_UPDATE_REFERENCES=1 dotnet test tests/Pagewing.Render.Tests                 # Referenzbilder bewusst neu erzeugen (der Lauf schlägt absichtlich fehl; Bilder prüfen, dann committen)
dotnet run -c Release --project bench/Pagewing.Benchmarks -- --filter "*"            # Benchmarks (Öffnen, Rendern, Text)
dotnet run tools/make-testpdfs.cs -- check                                           # Test-PDFs auf Reproduzierbarkeit prüfen (`generate` schreibt sie neu)
```

Die Vergleichsmessung der Viewer (Acrobat, Chrome, SumatraPDF; bisher nur Chrome gemessen) läuft mit `tools/measure-open.cs`, das Protokoll steht in `bench/compare/README.md`.

Die Roadmap wird aus `Docs/roadmap.json` erzeugt: `dotnet run tools/roadmap.cs -- generate` (prüfen mit `-- check`).

## CLI

`pagewing` zeigt Informationen an, rendert Seiten als PNG, gibt den Text aus, organisiert Seiten (drehen, zusammenführen, teilen, als Bilder exportieren) und schwärzt Text und Bereiche (`redact`). Die Ausgaben sind englisch und stabil, damit Skripte sie auswerten können. Exit-Codes: `0` Erfolg, `1` Fehler (Meldung `pagewing: …` auf stderr), `2` falscher Aufruf.

```bash
pagewing info dokument.pdf                       # Seitenzahl, PDF-Version, Metadaten, Seitengrößen
pagewing render dokument.pdf --pages 1-3 --dpi 200 --output bilder
pagewing render dokument.pdf --no-annotations    # ohne Annotationen, Dateien dokument-p001.png, …
pagewing text dokument.pdf --pages 2 > seite2.txt
pagewing rotate dokument.pdf --pages 2-4 --angle 90 --output gedreht.pdf
pagewing merge --output alles.pdf a.pdf b.pdf --pages 1-3 c.pdf
pagewing split dokument.pdf --every 10 --output teile
pagewing split dokument.pdf --bookmarks --output kapitel
pagewing export dokument.pdf --pages 1,3 --dpi 150 --output bilder
pagewing redact vertrag.pdf --output vertrag-redacted.pdf --text "Max Mustermann" --rect 2:72,100,200,24
pagewing redact vertrag.pdf --output x.pdf --text 4711 --dry-run
pagewing redact vertrag.pdf --output x.pdf --pattern iban --pattern email
pagewing redact vertrag.pdf --output x.pdf --text 4711 --rasterize-unredactable --dpi 150
pagewing --help
```

`--pages` nimmt `3`, `2-5`, `1,3,5-7` oder `all`; die Seiten kommen aufsteigend und ohne Doppelte (`--pages 3,1` ergibt die Seiten 1 und 3, ein Umsortieren geht damit nicht). `text` gibt UTF-8 ohne BOM aus und hängt nach jeder Seite ein Form-Feed an. Das Passwort einer verschlüsselten Datei kommt mit `--password` oder besser aus der Umgebungsvariable `PAGEWING_PASSWORD`, denn Kommandozeilenargumente sind für andere Prozesse sichtbar.

Die schreibenden Befehle (seit Milestone 13) schreiben in eine temporäre Datei im Zielordner, öffnen und prüfen sie und ersetzen erst dann. Sie ersetzen eine vorhandene Ausgabedatei nur mit `--force` und **nie eine Eingabedatei** (auch nicht über Verknüpfungen). Was sie verlieren oder verändern (etwa die Lesezeichen eingefügter Dateien, Formularfelder gleichen Namens), steht als `warning: …` auf stderr, bevor etwas geschrieben wird.

| Befehl | Aufruf | stdout bei Erfolg | Exit 1 (Auswahl) | Exit 2 (Auswahl) |
|---|---|---|---|---|
| `rotate` | `rotate <Datei> --pages <Bereich> --angle <90\|180\|270\|-90> --output <aus.pdf> [--force]` | Pfad der Ausgabe | Ausgabe vorhanden ohne `--force` oder gleich der Eingabe, Ordner fehlt, Passwort, Seite hinter dem Ende, Schreib- oder Prüffehler | `--pages`, `--angle` oder `--output` fehlt, ungültiger Winkel oder Bereich |
| `merge` | `merge --output <aus.pdf> <Datei> [--pages <Bereich>] [--password <pw>] <Datei> [--pages …] … [--force]` | Pfad der Ausgabe | wie `rotate`, Ausgabe gleich einer beliebigen Eingabe, mehr als 20 weitere Dateien, eine eingefügte verschlüsselte Datei in einer unverschlüsselten Hauptdatei (die Meldung nennt die Datei) | weniger als zwei Dateien, `--output` fehlt, `--pages`/`--password` vor der ersten Datei |
| `split` | `split <Datei> --output <Ordner> (--every <n> \| --ranges <Bereiche> \| --bookmarks) [--force]` | ein Pfad je Zeile, auch die fertigen bei Fehler oder Abbruch | Ordner fehlt (wird nie angelegt), Zieldateien vorhanden ohne `--force` (alle genannt, nichts geschrieben), `--bookmarks` ohne Lesezeichen der obersten Ebene, Teil scheitert (`N of M files were written`) | kein oder mehr als ein Modus, `--every` keine ganze Zahl ab 1, ungültige `--ranges` |
| `export` | `export <Datei> [--pages <Bereich>] [--dpi <n>] [--output <Ordner>] [--no-annotations] [--force]` | ein Pfad je Zeile (`<Name>-p<NNN>.png`) | Zieldatei vorhanden ohne `--force`, Seite über 64 Megapixel (nennt die Seite), Passwort | `--dpi` nicht positiv, ungültiger Bereich |

### `redact`: Schwärzen

```
pagewing redact <Datei> --output <aus.pdf> [--text <Begriff>]... [--pattern <email|iban|phone|card>]... [--include-unchecked] [--rect <Seite>:<x>,<y>,<b>,<h>]... [--pages <Bereich>] [--rasterize-pages <Bereich>] [--rasterize-unredactable] [--dpi <72-300>] [--match-case] [--dry-run] [--allow-unsearched] [--fill <black|white|#RRGGBB>] [--image-fill <solid|pixelate|blur>] [--image-strength <coarse|very-coarse>] [--overlay-text <Text>] [--overlay-repeat] [--force] [--password <pw>]
```

`--text` (beliebig oft) sucht jede Fundstelle im Seitentext, in den Texten der Annotationen und in den Werten der Formularfelder und entfernt sie; der Begriff ist reiner Text (kein Muster), Leerraum und Zeilenumbrüche zählen als ein Leerzeichen. Ohne `--match-case` werden alle gefundenen Schreibweisen entfernt. Ohne `--pages` muss der Begriff danach im **ganzen Dokument** weg sein; mit `--pages` werden nur die Treffer dieser Seiten entfernt (der Rest des Dokuments darf nicht mehr Vorkommen haben als vorher). Ein Begriff ohne Treffer ist ein Fehler (Exit 1, nichts geschrieben). `--rect` (beliebig oft) entfernt, was unter dem Rechteck liegt: Seite ab 1, Punkte, Ursprung oben links, Punkt als Dezimaltrenner; ein Teil hinter dem Seitenrand wird abgeschnitten und als `warning:` genannt, ein Rechteck ganz außerhalb ist ein Fehler. `--output` ist Pflicht, außer mit `--dry-run`.

- **Muster:** `--pattern <email|iban|phone|card>` (beliebig oft, kombinierbar mit `--text`, `--rect`, `--pages` und `--rasterize-*`) sucht E-Mail-Adressen, IBANs, Telefonnummern oder Kartennummern an denselben Stellen wie `--text` und entfernt jede Fundstelle; `--pages` begrenzt auch die Muster. Ehrlich gesagt findet ein Muster nur, was der Text zeigt: eine Nummer, die über einen Zeilenumbruch oder eine Seitengrenze geteilt ist, Ziffern, die durch andere Zeichen als die üblichen Trenner unterbrochen sind, und Nummern in Bildern werden **nicht** gefunden (*Not searched*, bei jedem Lauf mit `--text` oder `--pattern`); das Ergebnis ist nie „alle“. Eine IBAN oder Kartennummer, deren Prüfsumme oder Länge nicht stimmt, bleibt unberührt und wird auf stderr gezählt (Art und Seiten, nie der Wert, mit dem Hinweis auf `--include-unchecked`); `--include-unchecked` nimmt sie mit. Ohne jede Fundstelle der angegebenen Muster endet der Befehl wie `--text` ohne Treffer (Exit 1, nichts geschrieben); die Regel gilt für alle angegebenen Arten zusammen (hat keine Art eine markierbare Fundstelle, ist es Exit 1, auch wenn `--text` getroffen hat; eine einzelne Art ohne Fundstelle bleibt still); die Suchgrenze (10.000 Treffer) und nicht vollständig durchsuchte Seiten (`--allow-unsearched`) wirken wie bei `--text`. `--dry-run` zeigt je Art die Zahl der Treffer nach Sicherheit (Prüfsumme stimmt, nur Form, Prüfsumme falsch) und je Seite, nie die Werte. `--include-unchecked` ohne `--pattern` ist ein Aufruffehler (Exit 2), ebenso ein unbekannter Name für `--pattern`.
- **Entfernen heißt entfernen:** Text, Bildpixel, Pfade und Annotationen unter den Markierungen werden aus der Datei genommen, nicht überdeckt; die Datei wird vollständig neu aufgebaut (nie inkrementell, keine alte Revision). Dabei entfallen immer Info-Dictionary, XMP, Lesezeichen, die Anhänge des Dokuments (Namensbaum), Skripte und Seitenbeschriftungen; **Anmerkungen außerhalb der Markierungen bleiben unverändert (Kommentartexte, Autoren, Antworten), und die Dateien von Büroklammer-Annotationen (FileAttachment) bleiben in der Datei** (der Bericht nennt sie unter *Not cleaned*, „up to N“); interne Links verlieren ihr Ziel, Formularfelder sind danach nicht mehr zuverlässig ausfüllbar, überschriebene Bilder werden RGB.
- **Prüfung:** Pagewing liest die geschriebene Datei selbst zurück (Seitentext, Rohbytes mit entpackten Strömen und decodierten Zeichenketten, Zeichen in den Bereichen, Bildpixel, Seitenschlüssel, das Bereinigte, Vergleich der Seitenbilder außerhalb der Bereiche). **Findet sie etwas, endet der Befehl mit Exit 1 und es gibt keine Ausgabedatei.**
- **Ablehnung vorab (Exit 1, keine Ausgabe, Grund und Seitenzahlen ab 1):** verschlüsselte Datei, eine Seite mit `/Thumb`, `/PieceInfo`, `/Metadata` oder `/AA` (PDFium kann sie nicht entfernen; auch wenn sie nicht geprüft werden konnten), eine Seite, die sich nicht neu schreiben lässt, ohne sich außerhalb der Markierungen zu verändern (zum Beispiel mit Tiling-Muster). Ein Treffer, der sich nicht markieren lässt (keine Fläche, mehr als 500 Zeilen, nur Leerraum), und eine Suche, die an ihre Grenze von 10.000 Treffern im **ganzen Dokument** stößt (auch mit `--pages`; die Suche kennt keine Seitenauswahl), beenden den Befehl ebenfalls (Exit 1, nichts geschrieben): der Begriff ließe sich dort nicht entfernen.
- **Bericht auf stderr** (nie ein entfernter Text, auch nicht der Inhalt von Info-Feldern): *Removed* (Zahlen je Seite), *Cleaned (read back from the written file)*, *Lost*, *Not cleaned* (zum Beispiel `/Metadata` und Aktionen in Objekten, die mit den Seiten kommen), *Checked* und *Not checked*. Er verspricht nicht mehr, als die Prüfung leistet: Rohbytes werden erst ab 4 Zeichen langen Begriffen durchsucht, Antworten auf Annotationen, Autoren, Tooltips, Feldnamen, nur im Erscheinungsbild vorhandener Text und die Werte von Formularfeldern in einer Datei ohne Formular (Widgets ohne `/AcroForm`) werden **nicht durchsucht** (*Not searched*, bei jedem Lauf mit `--text`). Ließ sich eine Seite nicht vollständig durchsuchen (Seitentext nicht lesbar, eine Annotation oder ein Formularfeld wegen einer Grenze oder eines Schadens ausgelassen, Text über 2.000.000 Zeichen), steht der Begriff dort womöglich noch, und die Prüfung hat dieselben blinden Flecken: Der Befehl endet dann mit Exit 1, schreibt nichts (auch mit `--dry-run`) und nennt die Seiten. Nur `--allow-unsearched` nimmt das ausdrücklich hin („ich akzeptiere, dass diese Seiten nicht vollständig durchsucht wurden“); der Bericht führt sie dann unter *Not searched (accepted)*; die abschließende Prüfung der geschriebenen Datei läuft auch mit `--allow-unsearched` und kann den Befehl noch beenden (was eine nicht durchsuchte Stelle trägt, findet sie nur, wo ihr Weg hinreicht: Rohbytes erst ab 4 Zeichen). Ob ein anderes Programm die Datei genauso liest, ist damit nicht gesagt.
- **Seite in Bild umwandeln (Rettung für abgelehnte Seiten):** `--rasterize-pages <Bereich>` (Syntax wie `--pages`; dann sind `--text` und `--rect` nicht nötig) wandelt diese Seiten in je ein Bild um, `--rasterize-unredactable` genau die Seiten, die oben als „würde sich außerhalb der Markierungen ändern“ abgelehnt werden (die Seiten stehen vorab auf stderr; es braucht `--text`, `--pattern` oder `--rect`, sonst gäbe es nichts zu retten). `--dpi <72-300>` (ganze Zahl, Standard 200) gilt nur zusammen mit einem der beiden, sonst Exit 2; eine Seite, die dafür zu groß ist (über 32 Megapixel), bekommt eine niedrigere Auflösung, die der Bericht nennt, unter 72 dpi bleibt sie abgelehnt (Exit 1, auch mit `--rasterize-unredactable`: der Schalter fügt Seiten nur einmal hinzu und wiederholt nichts). Ohne einen der Schalter bleibt eine abgelehnte Seite eine Ablehnung (Exit 1, die Meldung nennt den Schalter). Verschlüsselte Dateien und Seitenschlüssel, die sich nicht entfernen lassen, werden dadurch **nicht** gerettet (Exit 1). Ehrlich gesagt: Der gesamte Text der Seite geht verloren, sie ist danach ein Bild; ihre Links, Kommentare und Formularfelder entfallen (der Bericht nennt je Seite, wie viele). Die markierten Flächen sind im Bild gefüllt. Ein sichtbares Vorkommen eines Begriffs **außerhalb** der Markierungen bleibt im Bild stehen und lässt sich ohne OCR nicht finden, die Prüfung kann das also für solche Seiten nicht feststellen (steht unter *Not checked*); geprüft werden die Struktur der Seite (genau ein Bild, sonst nichts) und die markierten Flächen. `--dry-run` sagt die Seiten, ihre Auflösung und den Verlust voraus.
- **Signierte Dateien:** vor dem Schreiben steht eine `warning:`-Zeile; die Signatur wird danach als ungültig angezeigt.
- **Füllung:** `--fill <black|white|#RRGGBB>` (Standard schwarz) wählt die Farbe der Flächen, die den entfernten Inhalt ersetzen; sie gilt für Bildpixel, Flächen auf der Seite und umgewandelte Seiten, und die Prüfung der geschriebenen Datei erwartet genau diese Farbe in den Bildpixeln unter den Markierungen. Ein anderer Wert (auch `#zz` oder `#FFF`) ist ein Aufruffehler (Exit 2).
- **Bildflächen:** `--image-fill <solid|pixelate|blur>` (Standard `solid`, die Füllung von `--fill`) bestimmt, was die Teile von Bildern unter den Markierungen werden: `pixelate` und `blur` teilen den Bildbereich in wenige grobe Blöcke (`--image-strength coarse`, Standard: höchstens 6 je Achse, oder `very-coarse`: höchstens 3; jeder Block mindestens 8 Bildpixel) und lassen nur deren Mittelwerte stehen (Verpixeln zeigt die Blöcke hart, Weichzeichnen blendet nur aus den Blockmitteln; Pixel außerhalb des Bereichs bleiben bitgleich, der Alphakanal im Bereich ist 255). Text- und Vektorflächen im selben Bereich bekommen immer die Volltonfläche. Wo die Regel nicht gilt (gedrehtes, geschertes oder zugeschnittenes Bild, ein Bereich unter 2 × 2 Blöcken zu je 8 Pixeln, mehrere Bereiche, die sich im selben Bild überlappen, nicht sicher lesbare oder schreibbare Pixel, zu viele behandelte Bilder unter einem Bereich), bekommt die Fläche die Volltonfläche; der Bericht (und `--dry-run`) nennt unter *Image areas* Zahl und Grund. Die Prüfung der geschriebenen Datei rechnet die Blockmittel aus dem **Original** nach und lässt die Datei durchfallen, wenn ein Bereich weder genau die Füllfarbe noch genau dieses Ergebnis ist (Weichzeichnen: höchstens 1 Stufe je Kanal Abweichung). **Ehrlich gesagt:** Text in Bildern kann auch verpixelt oder weichgezeichnet lesbar bleiben, und Pagewing erkennt Text in Bildern nicht; für Text im Bild die Volltonfläche wählen (der Bericht sagt es bei jedem Lauf mit `pixelate` oder `blur`). `--image-strength` ohne `--image-fill pixelate` oder `blur`, ein unbekannter Wert und eine doppelt angegebene Option sind Aufruffehler (Exit 2).
- **Text auf den Flächen:** `--overlay-text <Text>` schreibt einen Text auf jede markierte Fläche des Aufrufs (die Kommandozeile kennt keine einzelnen Markierungen): höchstens 200 Zeichen, nur Zeichen der Schrift des Textes (ein Text mit einem anderen Zeichen wird **als Ganzes** abgelehnt, nie geändert; die Meldung nennt die Stelle, nie den Text). Er wird mittig und so groß wie möglich eingepasst (nicht unter 6 pt); passt er auch dann nicht, bleibt die Fläche ohne Text gefüllt und der Bericht zählt sie („does not fit“, auch bei `--dry-run`); auf verpixelten oder weichgezeichneten Bildflächen steht kein Text. `--overlay-repeat` kachelt den Text über die Fläche (nur zusammen mit `--overlay-text`). **Enthält der Text einen geschwärzten Begriff** (ohne Beachtung der Schreibung, Leerräume zählen nicht), lehnt die Analyse ab (Exit 1, keine Ausgabedatei, auch bei `--dry-run`): der Text würde ihn zurück auf die Seite bringen. Ehrlich gesagt wird der Text als Formen gezeichnet, nicht als Text: er ist nicht auswählbar, steht in keiner Datei als Text, und die Prüfung der geschriebenen Datei liest ihn **nicht** zurück (*Not checked*). Exit 2: `--overlay-repeat` ohne `--overlay-text`, `--overlay-text` leer oder länger als 200 Zeichen oder mit nicht darstellbarem Zeichen, doppelt angegeben oder ohne `--text`/`--pattern`/`--rect`.
- **`--dry-run`** sucht und analysiert, schreibt nichts und zeigt je `--text` die Zahl der Treffer je Seite (nie den Text) und den Bericht. Exit 0 heißt: Es würde geschrieben; die Prüfung der geschriebenen Datei läuft erst beim Schreiben und kann den Befehl noch beenden. Ablehnungen gelten auch hier (Exit 1).
- Wie die anderen schreibenden Befehle: kein Überschreiben ohne `--force`, nie eine Eingabedatei, bei Strg+C bleibt keine temporäre Datei. stdout bei Erfolg: der Pfad der Ausgabe. Exit 2: Rechteck falsch geformt (`--rect`), leerer `--text`, unbekannte Art bei `--pattern`, `--include-unchecked` ohne `--pattern`, weder `--text` noch `--pattern` noch `--rect` noch `--rasterize-pages`, `--output` fehlt, `--pages` ohne `--text`/`--pattern`, ungültiger Bereich (auch bei `--rasterize-pages`), `--dpi` außerhalb 72 bis 300 oder ohne `--rasterize-*`, `--rasterize-unredactable` ohne `--text`/`--pattern`/`--rect`, ungültiges `--image-fill` oder `--image-strength`, `--image-strength` ohne `--image-fill pixelate`/`blur`.

`--pages` und `--password` hinter einer Datei gelten für diese Datei (wie bei `qpdf --pages`); die erste Datei von `merge` ist die primäre (ihre Lesezeichen, Formulare und Seitenbeschriftungen bleiben, die der anderen Dateien nicht). `split --bookmarks` teilt an den Lesezeichen der obersten Ebene. `export` legt einen fehlenden Ausgabeordner an (wie `render`), `split` nicht. Eine Datei, die wie eine Option heißt oder mit `-` beginnt, geht als `./-name.pdf`. Seiten umsortieren oder duplizieren geht bisher nur in der App (Modus „Seiten organisieren“, Strg+Umschalt+O).

## Lizenz

[GPL-3.0-or-later](LICENSE). Drittbibliotheken stehen unter eigenen, GPL-kompatiblen Lizenzen (unter anderem PDFium: BSD-3-Clause/Apache-2.0, Avalonia: MIT). Die Lizenzhinweise werden mit der App ausgeliefert.
