# Pagewing

Schnelle Open-Source-Alternative zu Adobe Acrobat für Windows und Linux. PDFs öffnen so schnell wie im Browser. Man kann sie lesen, durchsuchen, kommentieren, ausfüllen, unterschreiben, Seiten organisieren, schwärzen und per OCR durchsuchbar machen. Pagewing läuft als Desktop-App mit modernem Design oder als Kommandozeilen-Werkzeug `pagewing`. Komplett offline, ohne Konto, ohne Telemetrie.

**Status:** in Entwicklung, noch kein Release. Siehe [Docs/ROADMAP.md](Docs/ROADMAP.md).

> **English:** Pagewing is a fast, open-source alternative to Adobe Acrobat for Windows and Linux: view, search, annotate, fill, sign, organize, redact and OCR PDFs. Desktop app and CLI share one core. Fully offline, no telemetry. Built with .NET 10 (Native AOT), Avalonia and PDFium. Licensed under GPL-3.0-or-later. Project docs are in German; the app UI is English and German.

## Geplanter Funktionsumfang

| Version | Inhalt |
|---|---|
| v0.1.0 | Schneller Viewer: Tabs, Seitenvorschau, Inhaltsverzeichnis, Volltextsuche, Text kopieren, Layouts und Zoom, dunkler Modus, Befehlspalette (Strg+K), Drucken, Installer |
| v0.2.0 | Annotieren (Hervorheben, Zeichnen, Formen, Kommentare), Formulare ausfüllen, sichtbare Unterschrift |
| v0.3.0 | Seiten organisieren (umsortieren, drehen, zusammenführen, teilen), Schwärzen |
| v1.0.0 | OCR für gescannte PDFs, Text in Grenzen bearbeiten |

Bewusst nicht geplant sind unter anderem Zertifikatssignaturen, XFA-Formulare, JavaScript in PDFs, PDF-nach-Word und Cloud-Funktionen (Details in der Roadmap unter „Out of Scope“).

## Installation

Noch gibt es kein Release; die Pakete entstehen beim Veröffentlichen und hängen am GitHub-Release (Dateinamen und Bauanleitung: [packaging/README.md](packaging/README.md)). Pagewing greift nie ins Netz: keine Telemetrie, keine Update-Prüfung.

**Windows (10 und neuer, 64 Bit)**

- Installer `Pagewing-<Version>-win-x64-setup.exe`: grafisch mit Lizenz, Zielordner und der Frage „Pagewing jetzt starten?“. Standard ist die Installation nur für den eigenen Benutzer ohne Administratorrechte; für alle Benutzer wählt man es im Dialog oder startet mit `/ALLUSERS`. Der Installer ist nicht signiert, SmartScreen warnt deshalb („Weitere Informationen“, „Trotzdem ausführen“).
- Still, mit eigenem Zielordner: `Pagewing-<Version>-win-x64-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR="C:\Tools\Pagewing"` (für alle Benutzer zusätzlich `/ALLUSERS` aus einer Konsole mit Administratorrechten).
- Portabel: `Pagewing-<Version>-win-x64-portable.zip` entpacken und `Pagewing.exe` starten. Die CLI liegt im Unterordner `cli\`. Einstellungen und Verlauf liegen auch hier in `%APPDATA%\Pagewing`.

**Linux (x86-64)** – unterstützt sind Ubuntu 22.04 und neuer, Debian 12 und neuer, Linux Mint 21 und neuer sowie CachyOS (glibc ab 2.35; die Linux-Pakete entstehen im Container `ubuntu:22.04`).

- `.deb`: `sudo apt install ./pagewing_<Version>_amd64.deb`. Das Paket legt Starter, Symbole und die Zuordnung zu PDF an; `pagewing` im Pfad ist die CLI.
- AppImage: `chmod +x Pagewing-<Version>-x86_64.AppImage`, dann starten (Doppelklick oder Terminal). Es braucht kein `libfuse2`. Die Runtime des AppImage enthält libfuse unter LGPL-2.1; dessen Quelltext (`fuse-3.15.0.tar.xz`) und die Bauskripte der Runtime (`type2-runtime-20251108.tar.gz`) hängen an jedem Release. Beim ersten Start bietet die Karte (unten) an, Pagewing als Standard für PDFs festzulegen; das AppImage trägt dabei Starter und Symbol in den Benutzerordner (`~/.local/share`) ein, sodass es im Anwendungsmenü erscheint.

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

Für die self-hosted CI-Runner kommt unter Windows hinzu: die PowerShell-Ausführungsrichtlinie `RemoteSigned` für LocalMachine (`Set-ExecutionPolicy -Scope LocalMachine -ExecutionPolicy RemoteSigned -Force` in einer Windows PowerShell mit Administratorrechten). Details stehen in `CLAUDE.md`, Abschnitt „CI“.

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

`pagewing` zeigt Informationen an, rendert Seiten als PNG und gibt den Text aus. Die Ausgaben sind englisch und stabil, damit Skripte sie auswerten können. Exit-Codes: `0` Erfolg, `1` Fehler (Meldung `pagewing: …` auf stderr), `2` falscher Aufruf.

```bash
pagewing info dokument.pdf                       # Seitenzahl, PDF-Version, Metadaten, Seitengrößen
pagewing render dokument.pdf --pages 1-3 --dpi 200 --output bilder
pagewing render dokument.pdf --no-annotations    # ohne Annotationen, Dateien dokument-p001.png, …
pagewing text dokument.pdf --pages 2 > seite2.txt
pagewing --help
```

`--pages` nimmt `3`, `2-5`, `1,3,5-7` oder `all`. `text` gibt UTF-8 ohne BOM aus und hängt nach jeder Seite ein Form-Feed an. Das Passwort einer verschlüsselten Datei kommt mit `--password` oder besser aus der Umgebungsvariable `PAGEWING_PASSWORD`, denn Kommandozeilenargumente sind für andere Prozesse sichtbar.

## Lizenz

[GPL-3.0-or-later](LICENSE). Drittbibliotheken stehen unter eigenen, GPL-kompatiblen Lizenzen (unter anderem PDFium: BSD-3-Clause/Apache-2.0, Avalonia: MIT). Die Lizenzhinweise werden mit der App ausgeliefert.
