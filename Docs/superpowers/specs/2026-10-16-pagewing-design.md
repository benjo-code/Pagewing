# Pagewing – Gesamt-Spec

**Stand:** 2026-10-16 · **Status:** abgestimmt im Brainstorming (Teilprojekt 1 im Detail, Teilprojekte 2–4 als Rahmen) · **Roadmap:** [ROADMAP.md](../../ROADMAP.md)

## 1. Ziel

Pagewing ist eine schnelle Open-Source-Alternative zu Adobe Acrobat für Windows und Linux: PDFs ansehen, kommentieren, ausfüllen, unterschreiben, organisieren, schwärzen, per OCR durchsuchbar machen und in Grenzen bearbeiten. Dazu gibt es eine Kommandozeile mit demselben Kern.

**Motivation:** Acrobat startet und öffnet Dateien langsam, Chrome öffnet PDFs spürbar schneller. Pagewing soll mindestens so schnell wirken wie Chrome und dabei modern aussehen.

**Prioritäten**, in dieser Reihenfolge:
1. Geschwindigkeit: Start, Öffnen, Scrollen
2. Gutes, modernes Design
3. Funktionsumfang

**Zielgruppe:** zuerst der Projektinhaber als täglicher Nutzer, danach allgemein Nutzer, die einen schlanken, schnellen PDF-Editor wollen. Keine Enterprise-Anforderungen wie Rollout per Gruppenrichtlinien, Zertifikatssignaturen oder Server-Komponenten.

**Grundsätze:** komplett offline, keine Telemetrie, keine Konten, keine automatische Update-Prüfung.

## 2. Erfolgskriterien

- Ein Doppelklick auf eine PDF zeigt die erste Seite gefühlt sofort an. Messbar sind die Budgets aus Abschnitt 5.4, geprüft in der CI.
- Zu v0.1.0 kann Pagewing Acrobat beim Lesen von PDFs vollständig ersetzen: Navigation, Suche, Kopieren, Drucken, Standardprogramm.
- Bearbeitende Funktionen beschädigen nie das Original. Speichern ist atomar, und vorhandene digitale Signaturen bleiben gültig, wo es technisch geht.

## 3. Lizenz

**GPL-3.0-or-later.** Pagewing ist eine Endnutzer-App, so wie die nächsten Vorbilder (SumatraPDF unter GPL-3.0, Okular und Evince unter GPL-2.0+). Verbesserungen bleiben offen, Closed-Source-Klone sind ausgeschlossen.

- Alle Abhängigkeiten sind GPL-3.0-kompatibel: PDFium (BSD-3 + Apache-2.0), Avalonia (MIT), Tesseract (Apache-2.0), QPDF (Apache-2.0).
- Die freie OSS-Code-Signierung über die SignPath Foundation akzeptiert die GPL.

## 4. Tech-Stack

Alle Angaben wurden am 2026-10-16 geprüft.

| Baustein | Wahl | Stand | Lizenz |
|---|---|---|---|
| Laufzeit | .NET 10 (LTS, Support bis 2028-11-14), Native AOT | SDK 10.0.x | MIT |
| UI | Avalonia 12.1.x | 12.1.4 (2026-10-09) | MIT |
| PDF-Engine | PDFium über `bblanchon.PDFium.Win32` und `bblanchon.PDFium.Linux` (ohne V8, damit auch ohne XFA) | 157.0.8100 (2026-10-12), Release jede Woche | BSD-3 + Apache-2.0, dazu etwa 20 Drittlizenzen (FreeType, ICU, libjpeg-turbo, OpenJPEG …) |
| Strukturoperationen | QPDF über die C-API (Kandidat, Entscheidung in der Spec von Teilprojekt 3) | 12.4.2 (2026-09-27) | Apache-2.0 |
| OCR | Tesseract über die C-API (`capi.h`), Sprachdaten `tessdata_fast` (eng+deu etwa 5,6 MB) | 5.5.3 (2026-07-24) | Apache-2.0 |
| Abhängigkeits-Updates | Dependabot (NuGet mit Gruppe `bblanchon.PDFium*`, GitHub Actions) | – | – |

**Ausgeschlossen:**
- MuPDF und iText: AGPL oder kommerziell.
- PDFsharp ab 6.5: seit 2026-10 AGPL oder kommerziell. 6.2.4 ist die letzte MIT-Version und wird nicht mehr gepflegt.
- PDFiumCore: wird gepflegt (157.0.8100 vom 2026-10-15), ist aber mit CppSharp generiert, zielt höchstens auf net8 und hat eine riesige Oberfläche. Eigene Bindings geben Kontrolle über AOT, Trimming und Thread-Bindung.
- Die .NET-Wrapper für Tesseract (charlesw, TesseractOCR) brauchen Reflection.Emit und laufen deshalb nicht mit Native AOT.
- Uno Platform (unter Linux nur X11, keine Linux-Barrierefreiheit) und MAUI (keine Linux-Unterstützung).

**Bekannte Grenzen des Stacks:**
- **PDFium ist nicht thread-sicher**, auch nicht über verschiedene Dokumente hinweg. In `fpdfview.h` steht: „None of the PDFium APIs are thread-safe.“ Daraus folgt das Thread-Modell in 5.2.
- PDFium kann Metadaten (Info-Dictionary, XMP) und Lesezeichen nur lesen, nicht schreiben. Linien- und Polygon-Annotationen lassen sich nicht anlegen (`FPDFPage_CreateAnnot`). Die API für Annotationen ist insgesamt als „experimental“ markiert. Diese Lücken soll QPDF schließen.
- Avalonia hat keine Druck-API. Gedruckt wird plattformeigen (siehe 5.7).
- Wayland unterstützt Avalonia 12.1 nur experimentell und als Opt-in. Standard ist X11 bzw. XWayland.
- Native AOT kompiliert nicht über Betriebssystemgrenzen hinweg. Gebaut wird deshalb pro Betriebssystem auf einem eigenen Runner.
- Ohne V8 laufen keine JavaScript-Berechnungen in AcroForms (z. B. `AFNumber_Format` oder berechnete Summen).

## 5. Architektur

### 5.1 Projektstruktur

Eine Solution `Pagewing.slnx` mit zentraler Paketverwaltung (`Directory.Packages.props`), gemeinsamen Einstellungen (`Directory.Build.props`: Nullable, Warnungen als Fehler, `IsAotCompatible`) und `global.json`.

```
src/
  Pagewing.Pdfium/   LibraryImport-Bindings, SafeHandles, PdfiumThread – einziger Ort mit PDFium-Aufrufen
  Pagewing.Core/     Dokumentmodell, Render-Scheduler, Kachel-Cache, Suche, Speichern, später Seiten-Ops – ohne UI
  Pagewing.App/      Avalonia-App (MVVM), hängt nur von Core ab
  Pagewing.Cli/      CLI `pagewing`, hängt nur von Core ab
  Pagewing.Qpdf/     (erst in Teilprojekt 3) QPDF-Bindings
  Pagewing.Ocr/      (erst in Teilprojekt 4) Tesseract-Bindings, wird erst bei Nutzung geladen
tests/
  Pagewing.Core.Tests/     Unit- und Integrationstests (xUnit)
  Pagewing.Render.Tests/   Render-Regression gegen Referenzbilder
  Pagewing.App.Tests/      Avalonia.Headless (ab der App-Shell)
bench/
  Pagewing.Benchmarks/     BenchmarkDotNet: Öffnen, Rendern, Suche
  startup/                 Skript für die Startzeitmessung der AOT-Binaries
testdata/pdfs/             Test-PDFs mit LICENSES.md je Herkunft
```

**Abhängigkeitsrichtung:** App → Core → Pdfium bzw. Qpdf bzw. Ocr, und Cli → Core. Die App kennt keine PDFium-Typen.

### 5.2 Thread-Modell

- **PdfiumThread:** ein eigener Thread, auf dem alle PDFium-Aufrufe laufen. `FPDF_InitLibrary` wird einmal aufgerufen. Der Wrapper prüft die Thread-Bindung: Ein Aufruf von einem fremden Thread ist ein Programmierfehler und löst eine Exception aus.
- **UI-Thread:** wartet nie auf PDFium. Er stellt Aufträge in eine Warteschlange und bekommt fertige Bitmaps zurück.
- **Echte Parallelität**, also mehrere Seiten gleichzeitig, ginge nur mit Worker-Prozessen, so wie Chrome PDFium in eigene Prozesse auslagert. Das steht im Backlog und wird nur angegangen, wenn Messungen es nötig machen.

### 5.3 Render-Pipeline

1. Die Ansicht meldet den sichtbaren Bereich, also die sichtbaren Seiten plus einen Puffer davor und danach.
2. Der Scheduler auf dem PdfiumThread priorisiert: zuerst eine **grobe Vorschau** der ganzen Seite in niedriger Auflösung, dann **scharfe Kacheln** (Richtwert 512×512 Gerätepixel, `FPDF_RenderPageBitmapWithMatrix` mit Clipping).
3. Gerendert wird **progressiv** (`FPDF_RenderPageBitmap_Start` mit Pause-Callback). Aufträge für Seiten, die nicht mehr sichtbar sind, werden abgebrochen (`FPDF_RenderPage_Close`).
4. Ein **LRU-Cache** hält Vorschauen und Kacheln mit einem Speicherbudget. Beim schnellen Scrollen zeigt die Ansicht zuerst die Vorschau aus dem Cache und dann die scharfen Kacheln.

### 5.4 Start und Geschwindigkeitsziele

- Bis zum ersten Frame wird nur geladen, was das Fenster braucht. PDFium wird beim ersten Öffnen eines Dokuments initialisiert. Thumbnails, Textindex und OCR laufen erst nach dem ersten scharfen Seitenbild.
- **Single-Instance:** Läuft Pagewing bereits, übergibt ein neuer Aufruf den Dateipfad per Named Pipe (Windows) bzw. Unix-Socket (Linux) an die laufende Instanz und beendet sich. Die Datei öffnet als neuer Tab.
- **Budgets** (vorläufig; M1 misst Vergleichswerte von Acrobat, Chrome und SumatraPDF auf dem Rechner des Projektinhabers und legt die Ziele endgültig fest):

| Messgröße | Ziel |
|---|---|
| Kaltstart bis erste Seite scharf (1-seitige Test-PDF, AOT) | ≤ 400 ms |
| Warmstart bis erste Seite scharf | ≤ 200 ms |
| Weitere Datei in laufender Instanz bis erste Seite | ≤ 100 ms |
| 1000-seitige PDF öffnen bis erste Seite | ≤ 300 ms |

- **Messung in der CI:**
  - Die App hat einen versteckten Schalter `--measure-startup <pdf>`. Damit öffnet sie die Datei, wartet auf das erste scharfe Seitenbild, gibt die Zeit aus und beendet sich.
  - `bench/startup` wertet den Median über 10 Läufe auf beiden Runnern aus; unter Linux läuft die App dafür unter Xvfb.
  - Ein Median über Budget + 25 % lässt den Check fehlschlagen. Die Messwerte werden als CI-Artefakt abgelegt.

### 5.5 Speichern

- **Normalfall ist inkrementelles Speichern** (`FPDF_SaveAsCopy` mit `FPDF_INCREMENTAL`). Es ist schnell, und vorhandene digitale Signaturen bleiben gültig.
- **Vollständiger Neuschrieb** (`FPDF_NO_INCREMENTAL`) beim Schwärzen und bei Seitenoperationen. Ist das Dokument digital signiert, warnt die App vorher, dass die Signatur ungültig wird.
- **Immer atomar:** Pagewing schreibt in eine temporäre Datei im Zielordner, prüft sie, indem es sie mit PDFium wieder öffnet, und ersetzt erst dann das Ziel. Das Original wird nie halb überschrieben.
- Die Konstanten der Speicher-Flags kommen aus den Headern der ausgelieferten PDFium-Version. `FPDF_REMOVE_SECURITY` wechselte ab `chromium/8100` von 3 auf 4.

### 5.6 Sicherheit

- PDFium wird ohne V8 und ohne XFA genutzt, JavaScript in PDFs läuft also nie.
- Launch-Aktionen und eingebettete Dateien werden nie automatisch ausgeführt oder geöffnet.
- Externe Links öffnen erst nach Klick und einer Bestätigung, die die URL anzeigt.
- Keine Netzwerkzugriffe durch Pagewing selbst.

### 5.7 Drucken

- **Windows:** Druckdialog über Win32 (`PrintDlgEx`). Seiten gehen mit `FPDF_RenderPage` und dem Flag `FPDF_PRINTING` direkt auf den Drucker-HDC.
- **Linux:** Pagewing übergibt ein PDF der gewählten Seiten an CUPS, das PDF direkt annimmt. Der Dialog für Drucker, Seitenbereich und Kopien ist ein eigener Avalonia-Dialog.
- Die Abstraktion `IPrintService` liegt in Core, die Implementierungen pro Plattform in der App.

### 5.8 Lokalisierung und Sprachen

- UI-Texte sind von Anfang an lokalisierbar (resx), in Englisch und Deutsch. Die Sprache folgt dem System und lässt sich in den Einstellungen ändern.
- Code und Kommentare auf Englisch, Doku und Commits auf Deutsch. Die README hat zusätzlich einen kurzen englischen Abschnitt.

## 6. Funktionsumfang und Teilprojekte

Die Milestones mit Aufgaben und Abnahmekriterien stehen in der [Roadmap](../../ROADMAP.md). Hier stehen der Zuschnitt und die fachlichen Regeln.

### Teilprojekt 1 – Fundament & Viewer (diese Spec)

| M | Inhalt |
|---|---|
| 0 | Repo-Grundgerüst & CI: Solution-Struktur, Linux- und Windows-Runner, AOT-Builds pro OS, Dependabot, LICENSE |
| 1 | PDFium-Anbindung & Render-Kern: Bindings, PdfiumThread, Öffnen (auch mit Passwort), Seiten- und Kachel-Rendering, Textextraktion, CLI `info`/`render`/`text`, Render-Regression, Benchmarks, Vergleichsmessung |
| 2 | Designrichtungen: mindestens 3 Richtungen als HTML-Mockups (Artifacts) des Hauptfensters mit geöffnetem PDF, Seitenleiste und Werkzeugleiste, jeweils hell und dunkel. Der Projektinhaber entscheidet, das Ergebnis kommt als Design-Spec mit Tokens unter `Docs/superpowers/specs/` |
| 3 | App-Shell & schnelles Öffnen: Fenster im gewählten Design, Öffnen per Argument, Dialog und Drag & Drop, Single-Instance, fortlaufendes Scrollen mit Scheduler und Cache, Zoom, Startzeit-Check in der CI |
| 4 | Navigation: Tabs, Seitenvorschau, Inhaltsverzeichnis/Lesezeichen, Layouts (einzeln, fortlaufend, doppelseitig), Zoom-Modi, Gehe zu Seite |
| 5 | Text & Suche: Volltextsuche mit Treffermarkierung, Text markieren und kopieren, interne und externe Links |
| 6 | Komfort: dunkler Modus, Tastenkürzel, Befehlspalette (Strg+K), zuletzt geöffnete Dateien, Einstellungen |
| 7 | Drucken (Windows und Linux) |
| 8 | Verteilung: Windows-Installer und portable ZIP, Linux AppImage und .deb, Registrierung als PDF-Standardprogramm, Lizenzhinweise der Drittbibliotheken |

**Release v0.1.0 – schneller Viewer** nach M8.

Die UI-Arbeit (M3 ff.) beginnt erst, wenn die Designrichtung in M2 gewählt ist. M1 hat keine UI.

### Teilprojekt 2 – Annotieren, Ausfüllen, Unterschreiben

M9 Speichern & Textmarkierungen (Hervorheben, Unterstreichen, Durchstreichen, Kommentare mit Seitenleiste) · M10 Zeichnen & Formen (Freihand, Rechteck, Kreis, Linie, Pfeil, Textfelder) · M11 Formulare (AcroForm ausfüllen und speichern) · M12 Sichtbare Unterschrift. **Release v0.2.0.**

**Warum vor „Seiten organisieren“:**
- „Formular ausfüllen und unterschreiben“ ist der häufigste Grund, einen PDF-Editor statt des Browsers zu öffnen.
- Die Unterschrift baut auf Freihand- und Stempel-Annotationen auf.

**Regeln:**
- **Linie und Pfeil:** PDFium kann keine `/Line`-Annotationen anlegen. Die Spec von Teilprojekt 2 entscheidet zwischen Stempel-Annotation mit Pfad-Objekten und QPDF.
- **Formulare:** Ohne V8 laufen keine JavaScript-Berechnungen. Hat ein Formular berechnete Felder, weist die App darauf hin, dass sie sich nicht automatisch aktualisieren.
- **Sichtbare Unterschrift:** Sie wird nie als „rechtsgültig“ bezeichnet. Der Dialog zeigt den Hinweis: „Ersetzt keine handschriftliche Unterschrift, wo Schriftform verlangt ist.“ Gezeichnete oder importierte Unterschriften werden lokal gespeichert und lassen sich wiederverwenden.

### Teilprojekt 3 – Seiten & Schwärzen

M13 Seiten organisieren (umsortieren, drehen, löschen, einfügen, zusammenführen, teilen, als Bild exportieren; in App und CLI; Entscheidung über QPDF für Lesezeichen und Metadaten) · M14 Schwärzen. **Release v0.3.0.**

**Regeln fürs Schwärzen:** Der Inhalt wird **wirklich entfernt**. Ein schwarzes Rechteck darüber reicht nicht.
- Textobjekte im Bereich werden aufgeteilt; die betroffenen Zeichen werden entfernt, der Rest bleibt an seiner Position.
- Bildpixel im Bereich werden überschrieben und das Bild neu kodiert.
- Vektorpfade und Form-XObjects werden rekursiv behandelt.
- Annotationen im Bereich werden entfernt.
- Auf Wunsch werden die Metadaten entfernt, also das Info-Dictionary und XMP.
- Danach folgt immer ein **vollständiger Neuschrieb**, damit alte Revisionen und verwaiste Objekte verschwinden.
- **Tests:** Nach dem Schwärzen wird der Text extrahiert, und es darf nichts übrig sein. Zusätzlich werden die **dekomprimierten Rohbytes** der Datei nach dem geschwärzten Text durchsucht. Testfälle decken Text in Form-XObjects, Annotationen, Metadaten, älteren inkrementellen Revisionen und der OCR-Textebene ab.

### Teilprojekt 4 – OCR & Text bearbeiten

M15 OCR (gescannte PDFs durchsuchbar machen, unsichtbare Textebene) · M16 Text bearbeiten. **Release v1.0.0.**

**Regeln:**
- **OCR:** Tesseract läuft über eigene `[LibraryImport]`-Bindings auf `capi.h` und wird erst bei Nutzung geladen. Die Spec von Teilprojekt 4 entscheidet, wie die Sprachdaten und die nativen Binärdateien ausgeliefert werden.
- **Text bearbeiten ist bewusst begrenzt:** Vorhandene Zeilen und Tippfehler lassen sich korrigieren. Fehlen Zeichen in einer teilweise eingebetteten Schrift, springt eine Ersatzschrift ein. Absatz-Bearbeitung wie in Word wird nicht versprochen: PDFs speichern Text als positionierte Zeichen ohne Absatzstruktur, und Schriften sind oft nur teilweise eingebettet.

### Querschnitt

- **CLI `pagewing`:** wächst mit jedem Teilprojekt und nutzt denselben Kern, zum Beispiel `info`, `render`, `text`, später `merge`, `split`, `rotate`, `redact`, `ocr`. Sie wird ebenfalls mit Native AOT gebaut.
- Komplett offline, keine Telemetrie.

## 7. Tests und Qualität

- **Unit- und Integrationstests** (xUnit) für Core und Pdfium, Avalonia.Headless für die App.
- **Render-Regression:**
  - `testdata/pdfs` wird pro Seite zu PNG gerendert und mit Referenzbildern verglichen, mit Toleranz pro Pixel und Anteil abweichender Pixel.
  - Läuft in der CI auf beiden Betriebssystemen und schlägt bei jedem PDFium-Update von Dependabot an.
  - Referenzbilder werden nur bewusst aktualisiert, mit Sichtprüfung der Abweichungen im PR.
- **Test-PDFs:**
  - Nur Dateien mit klarer Lizenz, dokumentiert in `testdata/pdfs/LICENSES.md`.
  - Bevorzugt selbst erzeugte PDFs und Dateien aus PDFium `testing/resources` (BSD-3), ergänzt um veraPDF-Korpus (CC BY 4.0) und pdf20examples (CC BY-SA 4.0) mit Namensnennung.
  - Der pdf.js-Korpus hat keine klaren Einzellizenzen und wird nicht eingecheckt.
  - Keine privaten PDFs.
- **Benchmarks** (BenchmarkDotNet) für Öffnen, Rendern und Suche, dazu der Startzeit-Check aus 5.4.

## 8. CI und Infrastruktur

- Self-hosted Runner der Orga: `dotnet-linux` (vorhanden) und `dotnet-windows` (richtet der Projektinhaber in M0 ein).
- Pro PR nach `dev` und `main`: Build, Test, Format und Roadmap-Check auf beiden Betriebssystemen, AOT-Publish von App und CLI als Smoke-Test. Ab M1 kommt die Render-Regression dazu, ab M3 der Startzeit-Check.
- Dependabot für NuGet (Gruppe `bblanchon.PDFium*`, nicht auf minor/patch filtern, weil jede PDFium-Version formal ein Major-Update ist) und für GitHub Actions.
- **Sicherheit der Runner:** Das Repo ist derzeit privat. Wird es öffentlich, müssen PRs aus Forks vor dem Lauf auf selbst gehosteten Runnern freigegeben werden.
- Der Linux-Build ist an die glibc des Build-Rechners gebunden. Die älteste unterstützte Distribution wird in M8 festgelegt.

## 9. Bewusst nicht umsetzen (Out of Scope)

- Digitale Signatur mit Zertifikat (vorerst, kann später ergänzt werden)
- XFA-Formulare
- JavaScript in PDFs
- PDF nach Word umwandeln
- Cloud, Konten, KI-Assistent
- Druckvorstufe (Preflight), 3D- und Multimedia-Inhalte
- macOS und Mobilgeräte (Avalonia könnte es, es wird aber weder gebaut noch getestet)
- Automatische Online-Updates bzw. Update-Prüfung

## 10. Backlog (nicht eingeplant)

- Worker-Prozesse für paralleles Rendern
- Natives Wayland (Avalonia 12.1, experimentell)
- Flatpak/Flathub. Die Einreichung muss laut Flathub-Regeln ein Mensch vornehmen; AOT im Flatpak-Build muss erst erprobt werden.
- Code-Signierung über die SignPath Foundation. Voraussetzung ist ein öffentliches Repo mit bereits veröffentlichtem Release; als Herausgeber erscheint die SignPath Foundation.
- Microsoft Store (MSIX)
- Sitzung wiederherstellen
- Präsentationsmodus
- Barrierefreiheit über die Grundlagen hinaus (UIA unter Windows, AT-SPI2 unter Linux)
- arm64-Builds (die PDFium-Pakete enthalten sie bereits)

## 11. Offene Punkte

- **Windows-Runner:** muss mit dem Label `dotnet-windows` in der Orga eingerichtet werden. Das ist die Voraussetzung für die CI in M0.
- **Veröffentlichung des Repos:** Das Repo ist privat. Wann es öffentlich wird, ist noch offen, spätestens aber zu v0.1.0.
- **Budgets:** werden in M1 nach der Vergleichsmessung endgültig festgelegt.
- **Älteste unterstützte Linux-Distribution (glibc):** wird in M8 festgelegt.
- **Auslieferung von Tesseract und Sprachdaten:** im Paket enthalten oder als optionale Komponente. Entscheidet die Spec von Teilprojekt 4.
- **QPDF:** kommt es dazu oder nicht? Entscheidet die Spec von Teilprojekt 3, für Linie und Pfeil schon die Spec von Teilprojekt 2.

## 12. Quellen (geprüft 2026-10-16)

- PDFium-Header: https://pdfium.googlesource.com/pdfium/+/refs/heads/main/public/ (Thread-Sicherheit: `fpdfview.h`)
- pdfium-binaries: https://github.com/bblanchon/pdfium-binaries · https://www.nuget.org/packages/bblanchon.PDFium.Win32
- PDFiumCore: https://www.nuget.org/packages/PDFiumCore
- .NET-Support: https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core · AOT und Cross-OS: https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/cross-compile
- Avalonia 12.1: https://avaloniaui.net/blog/release-12-1 · AOT: https://docs.avaloniaui.net/docs/deployment/native-aot · Drucken: https://github.com/AvaloniaUI/Avalonia/issues/12567
- Tesseract: https://github.com/tesseract-ocr/tesseract/releases/tag/5.5.3 · C-API: https://github.com/tesseract-ocr/tesseract/blob/main/include/tesseract/capi.h
- QPDF: https://github.com/qpdf/qpdf/releases · PDFsharp-Lizenzwechsel: https://www.nuget.org/packages/PDFsharp
- Dependabot-Optionen: https://docs.github.com/en/code-security/dependabot/working-with-dependabot/dependabot-options-reference
- Flathub-Anforderungen: https://docs.flathub.org/docs/for-app-authors/requirements · SignPath Foundation: https://signpath.org/terms
