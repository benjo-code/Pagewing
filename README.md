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

## Technik

- .NET 10 mit Native AOT für kurze Startzeit
- [Avalonia](https://avaloniaui.net/) 12 als UI-Framework
- [PDFium](https://pdfium.googlesource.com/pdfium/), die PDF-Engine von Chrome, ohne JavaScript-Unterstützung (Binaries von [bblanchon/pdfium-binaries](https://github.com/bblanchon/pdfium-binaries))
- Später [Tesseract](https://github.com/tesseract-ocr/tesseract) für OCR

Architektur und Entscheidungen stehen in der [Gesamt-Spec](Docs/superpowers/specs/2026-10-16-pagewing-design.md) und in [Docs/DECISIONS.md](Docs/DECISIONS.md).

## Setup

Das Code-Grundgerüst entsteht in Milestone 0. Bis dahin gibt es nur Planungsdokumente.

**Voraussetzungen:** .NET-10-SDK. Für den AOT-Publish braucht es zusätzlich die nativen Build-Werkzeuge: unter Windows Visual Studio Build Tools mit „Desktopentwicklung mit C++“, unter Linux `clang` und `zlib1g-dev`.

```bash
dotnet build Pagewing.slnx
dotnet test Pagewing.slnx
dotnet format Pagewing.slnx --verify-no-changes
dotnet publish src/Pagewing.App -c Release -r linux-x64   # bzw. win-x64, nur auf dem jeweiligen OS
```

Die Roadmap wird aus `Docs/roadmap.json` erzeugt: `dotnet run tools/roadmap.cs -- generate` (prüfen mit `-- check`).

## Lizenz

[GPL-3.0-or-later](LICENSE). Drittbibliotheken stehen unter eigenen, GPL-kompatiblen Lizenzen (unter anderem PDFium: BSD-3-Clause/Apache-2.0, Avalonia: MIT). Die Lizenzhinweise werden mit der App ausgeliefert.
