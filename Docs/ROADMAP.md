<!-- Generiert aus Docs/roadmap.json mit `dotnet run tools/roadmap.cs -- generate` – nicht von Hand ändern. -->
# Pagewing – Roadmap

Gesamtfahrplan für das Projekt. Jeder Milestone ist so geschnitten, dass er in **einem** Plan und einer Umsetzungsrunde abgearbeitet werden kann.

> **Diese Datei ist generiert.** Quelle ist [roadmap.json](roadmap.json) (Schema: [roadmap.schema.json](roadmap.schema.json)). Änderungen nur dort, danach `dotnet run tools/roadmap.cs -- generate`.

## Arbeitsweise

1. **„Plane Milestone N“:** Claude Code liest diese Roadmap, die verlinkte Spec und den aktuellen Code-Stand und schreibt einen Plan nach `Docs/superpowers/plans/YYYY-MM-DD-milestone-NN-<thema>.md`. Der Plan wird beim Milestone verlinkt, Status → `🟡 geplant`.
2. **Umsetzung:** Plan abarbeiten. Jede erledigte Aufgabe wird sofort abgehakt (`done: true` in `Docs/roadmap.json`).
3. **Abschluss:** Alle Abnahmekriterien geprüft und abgehakt → Status `✅ fertig`, Datum eintragen.
4. Scope-Änderungen während der Umsetzung werden unter „Scope-Änderungen“ festgehalten, nicht stillschweigend im Code.

**Status-Legende:** `⬜ offen` · `🟡 geplant` · `🔵 in Arbeit` · `✅ fertig`

**Regeln:**
- Milestones grundsätzlich in Reihenfolge abarbeiten. Maßgeblich sind die Voraussetzungen beim jeweiligen Milestone.
- Ein Milestone beginnt mit `Spec erstellen`, wenn für sein Teilprojekt noch keine Spec existiert. Die Spec entsteht per Brainstorming und wird unter `Docs/superpowers/specs/` abgelegt.
- Aufgaben späterer Teilprojekte sind absichtlich grob gehalten und werden beim Schreiben der jeweiligen Spec präzisiert.

## Übersicht

| # | Milestone | Teilprojekt | Status | Plan | Fertig am |
|---|---|---|---|---|---|
| 0 | Repo-Grundgerüst & CI | 1 – Fundament & Viewer | ✅ fertig | [Plan](superpowers/plans/2026-10-16-milestone-00-grundgeruest.md) | 2026-10-16 |
| 1 | PDFium-Anbindung & Render-Kern | 1 – Fundament & Viewer | ⬜ offen | – | – |
| 2 | Designrichtungen | 1 – Fundament & Viewer | ⬜ offen | – | – |
| 3 | App-Shell & schnelles Öffnen | 1 – Fundament & Viewer | ⬜ offen | – | – |
| 4 | Navigation | 1 – Fundament & Viewer | ⬜ offen | – | – |
| 5 | Text & Suche | 1 – Fundament & Viewer | ⬜ offen | – | – |
| 6 | Komfort | 1 – Fundament & Viewer | ⬜ offen | – | – |
| 7 | Drucken | 1 – Fundament & Viewer | ⬜ offen | – | – |
| 8 | Verteilung | 1 – Fundament & Viewer | ⬜ offen | – | – |
| – | **Release `v0.1.0`** – schneller Viewer (M0 bis M8) | – | ⬜ offen | – | – |
| 9 | Speichern & Textmarkierungen | 2 – Annotieren, Ausfüllen, Unterschreiben | ⬜ offen | – | – |
| 10 | Zeichnen & Formen | 2 – Annotieren, Ausfüllen, Unterschreiben | ⬜ offen | – | – |
| 11 | Formulare | 2 – Annotieren, Ausfüllen, Unterschreiben | ⬜ offen | – | – |
| 12 | Sichtbare Unterschrift | 2 – Annotieren, Ausfüllen, Unterschreiben | ⬜ offen | – | – |
| – | **Release `v0.2.0`** – annotieren, ausfüllen, unterschreiben (M9 bis M12) | – | ⬜ offen | – | – |
| 13 | Seiten organisieren | 3 – Seiten & Schwärzen | ⬜ offen | – | – |
| 14 | Schwärzen | 3 – Seiten & Schwärzen | ⬜ offen | – | – |
| – | **Release `v0.3.0`** – Seiten organisieren und schwärzen (M13 bis M14) | – | ⬜ offen | – | – |
| 15 | OCR | 4 – OCR & Text bearbeiten | ⬜ offen | – | – |
| 16 | Text bearbeiten | 4 – OCR & Text bearbeiten | ⬜ offen | – | – |
| – | **Release `v1.0.0`** – OCR und Textbearbeitung (M15 bis M16) | – | ⬜ offen | – | – |

## Scope-Änderungen

_Noch keine._

## Geschwindigkeitsziele

Geschwindigkeit ist das wichtigste Merkmal von Pagewing. Vorläufige Budgets (werden in M1 nach einer Vergleichsmessung mit Acrobat, Chrome und SumatraPDF endgültig festgelegt, ab M3 in der CI geprüft; Details in der [Spec](superpowers/specs/2026-10-16-pagewing-design.md), Abschnitt 5.4):

| Messgröße | Ziel |
|---|---|
| Kaltstart bis erste Seite scharf (1-seitige Test-PDF, AOT) | ≤ 400 ms |
| Warmstart bis erste Seite scharf | ≤ 200 ms |
| Weitere Datei in laufender Instanz bis erste Seite | ≤ 100 ms |
| 1000-seitige PDF öffnen bis erste Seite | ≤ 300 ms |

## Out of Scope

Bewusst nicht umgesetzt:

- Digitale Signatur mit Zertifikat (vorerst; kann später ergänzt werden)
- XFA-Formulare
- JavaScript in PDFs
- PDF nach Word umwandeln
- Cloud, Konten, KI-Assistent
- Druckvorstufe (Preflight), 3D- und Multimedia-Inhalte
- macOS und Mobilgeräte (Avalonia könnte es, wird aber weder gebaut noch getestet)
- Automatische Online-Updates bzw. Update-Prüfung (komplett offline, keine Telemetrie)

---

# Teilprojekt 1 – Fundament & Viewer

**Spec:** [2026-10-16-pagewing-design.md](superpowers/specs/2026-10-16-pagewing-design.md)

## Milestone 0 – Repo-Grundgerüst & CI
**Status:** ✅ fertig (2026-10-16) · **Plan:** [2026-10-16-milestone-00-grundgeruest.md](superpowers/plans/2026-10-16-milestone-00-grundgeruest.md) · **Voraussetzungen:** keine

Ziel: Leeres, aber vollständig aufgesetztes Repo mit Solution-Struktur und CI auf Linux und Windows, auf dem alle weiteren Milestones aufbauen.

**Aufgaben**
- [x] Spec erstellen (Brainstorming): Ziel, Umfang, Stack, Teilprojekte; hier verlinken und die ersten Milestones eintragen
- [x] Repo-Einrichtung laut README erledigt: `dev` als Default-Branch, Merge-Optionen, Branch Protection für `main` und `dev`
- [x] Projekt-Kopf in `CLAUDE.md` und `README.md` ausfüllen, Template-Abschnitt aus der README entfernen
- [x] `LICENSE` mit GPL-3.0 (Lizenz: GPL-3.0-or-later)
- [x] Self-hosted Windows-Runner mit Label `dotnet-windows` in der Orga `benjo-code` einrichten (Projektinhaber)
- [x] `.gitignore` für .NET (inkl. `.env`, `bin/`, `obj/`, Render-Diff-Ausgaben), `NuGet.config` (nur nuget.org, Source-Mapping) und C#-Stilregeln in `.editorconfig`
- [x] Solution-Gerüst laut Spec, Abschnitt 5.1: `Pagewing.slnx`, `global.json` (inkl. Test-Runner Microsoft.Testing.Platform), `Directory.Build.props` (Nullable, Warnungen als Fehler, Analyse-Stufe, deterministischer Build, Version; `IsAotCompatible` je `src/`-Projekt), `Directory.Packages.props`, Isolation der Einzeldatei-Werkzeuge in `tools/`, Projekte `Pagewing.Pdfium`, `Pagewing.Core`, `Pagewing.App` (leeres Avalonia-12-Fenster), `Pagewing.Cli` (`--version`) mit Smoke-Skript `tools/smoke-cli.cs`, `Pagewing.Core.Tests` (xunit.v3)
- [x] CI (GitHub Actions) auf PRs nach `dev` und `main`: Build, Test und `dotnet format --verify-no-changes` auf `dotnet-linux` und `dotnet-windows`, AOT-Publish von App und CLI pro OS als Smoke-Test; der Job `roadmap` bleibt
- [x] CI-Jobs (`roadmap` und die Build/Test/Lint-Jobs beider OS) als Pflicht-Status-Checks in der Branch Protection von `main` und `dev` eintragen
- [x] Dependabot für NuGet (Gruppe `bblanchon.PDFium*`, ohne Filter auf minor/patch) und GitHub Actions
- [x] Stolperfallen in `CLAUDE.md`, Entscheidungen in `Docs/DECISIONS.md` und Setup in `README.md` nachtragen

**Abnahmekriterien**
- [x] Build, Test und Format/Lint laufen lokal grün (Befehle in `CLAUDE.md`) – `dotnet build Pagewing.slnx` (0 Warnungen, 0 Fehler), `dotnet test Pagewing.slnx` (1/1), `dotnet format Pagewing.slnx --verify-no-changes` (Exit 0) — Ausgabe der Abschlussprüfung
- [x] AOT-Publish von `Pagewing.App` und `Pagewing.Cli` läuft ohne AOT-/Trim-Warnungen auf Linux und Windows; `pagewing --version` des AOT-Binaries gibt die Version aus – CI-Lauf 38933844896 (PR #1): `build-linux` und `build-windows` veröffentlichen CLI und App per Native AOT ohne Warnungen; `dotnet run tools/smoke-cli.cs` meldet auf beiden „Smoke-Test bestanden (0.0.0)“
- [x] CI ist auf einem PR nach `dev` auf beiden Runnern grün – PR #1: `roadmap`, `build-linux`, `build-windows` grün (Lauf 38933844896, Commit f038a45)
- [x] Branch Protection für `main` und `dev` ist aktiv: PR Pflicht, CI-Checks Pflicht, kein Force-Push, kein Löschen – `gh api repos/benjo-code/Pagewing/branches/{main,dev}/protection` (2026-10-16): Pflicht-Checks `roadmap`, `build-linux`, `build-windows`, strict, PR Pflicht, Force-Push und Löschen verboten

## Milestone 1 – PDFium-Anbindung & Render-Kern
**Status:** ⬜ offen · **Plan:** – · **Voraussetzungen:** M0

Ziel: Der Kern öffnet PDFs, rendert Seiten und Kacheln und extrahiert Text, nachweisbar über die CLI, Render-Regressionstests und Benchmarks.

**Aufgaben**
- [ ] `bblanchon.PDFium.Win32` und `bblanchon.PDFium.Linux` (ohne V8) einbinden, Drittlizenzen der Pakete erfassen
- [ ] `Pagewing.Pdfium`: `[LibraryImport]`-Bindings nur für benötigte Funktionen, SafeHandles, `PdfiumThread` (alle Aufrufe auf einem Thread, Fremd-Thread-Aufruf löst Exception aus)
- [ ] Öffnen aus Datei, auch passwortgeschützte PDFs; verständliche Fehler bei kaputten oder verschlüsselten Dateien
- [ ] Rendern ganzer Seiten und einzelner Kacheln (Matrix + Clipping), progressiv mit Pause und Abbruch
- [ ] Textextraktion pro Seite (Grundlage für Suche und Schwärzungstests)
- [ ] CLI `pagewing info|render|text`
- [ ] `testdata/pdfs` mit lizenzklaren Test-PDFs und `LICENSES.md`; `Pagewing.Render.Tests` vergleicht Renderings mit Referenzbildern (Toleranz), läuft in der CI auf beiden OS
- [ ] `Pagewing.Benchmarks` (BenchmarkDotNet): Öffnen und Rendern
- [ ] Vergleichsmessung Acrobat, Chrome, SumatraPDF auf dem Rechner des Projektinhabers; Budgets in Roadmap, `CLAUDE.md` und Spec endgültig festlegen

**Abnahmekriterien**
- [ ] `pagewing render` erzeugt für alle Test-PDFs PNGs, die mit den Referenzbildern übereinstimmen (Render-Tests grün auf Linux und Windows)
- [ ] Test belegt, dass ein PDFium-Aufruf von einem fremden Thread eine Exception auslöst
- [ ] Passwortgeschützte Test-PDF öffnet mit richtigem Passwort und scheitert mit falschem mit klarer Meldung (Test)
- [ ] Vergleichsmessung dokumentiert (Werte und Methode in `Docs/DECISIONS.md`), Budgets festgelegt

## Milestone 2 – Designrichtungen
**Status:** ⬜ offen · **Plan:** – · **Voraussetzungen:** M0

Ziel: Der Projektinhaber hat aus mindestens drei Designrichtungen eine gewählt, und sie ist als Design-Spec mit Tokens festgehalten.

**Aufgaben**
- [ ] Mindestens 3 unterschiedliche, zum Projekt passende Designrichtungen als HTML-Mockups (Artifacts): Hauptfenster mit geöffnetem PDF, Seitenleiste und Werkzeugleiste, jeweils hell und dunkel
- [ ] Entscheidung des Projektinhabers einholen und in `Docs/DECISIONS.md` festhalten (inkl. verworfener Richtungen)
- [ ] Design-Spec unter `Docs/superpowers/specs/` mit Tokens (Farben hell/dunkel, Typografie, Abstände, Radien, Icons), Fenster-Layout und Verhalten der wichtigsten Bereiche
- [ ] App-Name-Schriftzug und Icon-Richtung festlegen (Grundlage für M8)

**Abnahmekriterien**
- [ ] Mindestens 3 Richtungen wurden als Artifacts bereitgestellt (Links in der Design-Spec)
- [ ] Projektinhaber hat eine Richtung ausdrücklich gewählt
- [ ] Design-Spec ist committet und in der Roadmap verlinkt

## Milestone 3 – App-Shell & schnelles Öffnen
**Status:** ⬜ offen · **Plan:** – · **Voraussetzungen:** M1, M2

Ziel: Pagewing öffnet eine PDF im gewählten Design und scrollt flüssig durch alle Seiten, innerhalb der Startzeit-Budgets.

**Aufgaben**
- [ ] Hauptfenster nach Design-Spec, UI-Texte lokalisierbar (resx, Englisch und Deutsch)
- [ ] Öffnen per Kommandozeilenargument, Dateidialog und Drag & Drop
- [ ] Single-Instance: zweiter Aufruf übergibt den Pfad an die laufende Instanz (Named Pipe / Unix-Socket)
- [ ] Fortlaufendes Scrollen mit Virtualisierung, Render-Scheduler (sichtbar zuerst, erst grob, dann scharf, Abbruch veralteter Aufträge) und LRU-Cache mit Speicherbudget
- [ ] Zoom (Strg+Mausrad, Strg+Plus/Minus)
- [ ] Schalter `--measure-startup`, Skript `bench/startup`, Startzeit-Check in der CI (Median aus 10 Läufen, Linux unter Xvfb)
- [ ] `Pagewing.App.Tests` mit Avalonia.Headless

**Abnahmekriterien**
- [ ] Startzeit-Check in der CI grün auf beiden Runnern, Werte als Artefakt
- [ ] Weitere Datei bei laufender Instanz öffnet als neuer Inhalt im selben Prozess (Test + manuelle Prüfung)
- [ ] Schnelles Scrollen durch eine 1000-seitige Test-PDF ohne Ruckler des UI-Threads (manuelle Prüfung, Messwert notieren)

## Milestone 4 – Navigation
**Status:** ⬜ offen · **Plan:** – · **Voraussetzungen:** M3

Ziel: Mehrere PDFs lassen sich in Tabs öffnen und über Vorschau, Inhaltsverzeichnis, Layouts und Zoom-Modi bequem navigieren.

**Aufgaben**
- [ ] Tabs (öffnen, schließen, wechseln, Reihenfolge ändern)
- [ ] Seitenleiste mit Seitenvorschau (Thumbnails im Hintergrund, nach dem ersten Seitenbild)
- [ ] Inhaltsverzeichnis/Lesezeichen aus dem PDF anzeigen und anspringen
- [ ] Layouts: einzeln, fortlaufend, doppelseitig (mit/ohne Deckblatt)
- [ ] Zoom-Modi: Seitenbreite, ganze Seite, tatsächliche Größe, freier Zoom
- [ ] Gehe zu Seite

**Abnahmekriterien**
- [ ] Headless-Tests für Tab-Wechsel, Layoutwechsel und Lesezeichen-Sprung grün
- [ ] Startzeit-Budgets weiter eingehalten (CI)

## Milestone 5 – Text & Suche
**Status:** ⬜ offen · **Plan:** – · **Voraussetzungen:** M4

Ziel: Text lässt sich durchsuchen, markieren und kopieren, Links funktionieren.

**Aufgaben**
- [ ] Volltextsuche im Dokument mit Treffermarkierung und Weiter/Zurück, Suche im Hintergrund ohne UI-Blockade
- [ ] Text markieren und in die Zwischenablage kopieren
- [ ] Interne Links (Sprung zu Seite/Ziel) und externe Links (Öffnen nach Bestätigung mit angezeigter URL)
- [ ] CLI `pagewing search`

**Abnahmekriterien**
- [ ] Suchtests auf Test-PDFs (Treffer und Positionen) grün
- [ ] Externer Link öffnet erst nach Bestätigung (Test)
- [ ] Benchmark: Suche in 1000-seitiger Test-PDF, Wert notiert

## Milestone 6 – Komfort
**Status:** ⬜ offen · **Plan:** – · **Voraussetzungen:** M5

Ziel: Pagewing lässt sich weitgehend per Tastatur bedienen und merkt sich Einstellungen und zuletzt geöffnete Dateien.

**Aufgaben**
- [ ] Dunkler Modus (folgt dem System, umschaltbar), optional dunkle Seitendarstellung
- [ ] Tastenkürzel für alle Viewer-Aktionen, Übersicht im Hilfe-Dialog
- [ ] Befehlspalette (Strg+K) mit allen Aktionen und zuletzt geöffneten Dateien
- [ ] Zuletzt geöffnete Dateien (lokal gespeichert)
- [ ] Einstellungen (Sprache, Theme, Standard-Layout und -Zoom), gespeichert per Source-generiertem JSON

**Abnahmekriterien**
- [ ] Headless-Tests für Befehlspalette und Theme-Wechsel grün
- [ ] Einstellungen und zuletzt geöffnete Dateien überleben einen Neustart (Test)

## Milestone 7 – Drucken
**Status:** ⬜ offen · **Plan:** – · **Voraussetzungen:** M6

Ziel: PDFs lassen sich unter Windows und Linux drucken.

**Aufgaben**
- [ ] Abstraktion `IPrintService` in Core
- [ ] Windows: Druckdialog per Win32, Ausgabe mit `FPDF_RenderPage` auf den Drucker-HDC
- [ ] Linux: Übergabe an CUPS, eigener Avalonia-Dialog (Drucker, Seitenbereich, Kopien)
- [ ] Seitenbereich, Kopien, Anpassen an Papiergröße

**Abnahmekriterien**
- [ ] Druck einer Test-PDF auf einen echten Drucker unter Windows und Linux (manuelle Prüfung durch den Projektinhaber)
- [ ] Druck in PDF-Drucker (Microsoft Print to PDF bzw. CUPS-PDF) ergibt korrekte Seiten (manuelle Prüfung)

## Milestone 8 – Verteilung
**Status:** ⬜ offen · **Plan:** – · **Voraussetzungen:** M7

Ziel: Pagewing lässt sich unter Windows und Linux installieren und als Standardprogramm für PDFs festlegen.

**Aufgaben**
- [ ] Windows: Installer und portable ZIP, Registrierung als PDF-Standardprogramm
- [ ] Linux: AppImage und .deb mit `.desktop`-Datei und MIME-Zuordnung; älteste unterstützte Distribution (glibc) festlegen
- [ ] Lizenzhinweise aller Drittbibliotheken (PDFium-Drittlizenzen, Avalonia usw.) im Paket und im Über-Dialog
- [ ] Release-Artefakte per CI bauen und an das GitHub-Release hängen

**Abnahmekriterien**
- [ ] Installation, Doppelklick auf PDF und Deinstallation auf frischem Windows und frischer Linux-Distribution (manuelle Prüfung)
- [ ] Lizenzhinweise vollständig (Abgleich mit den Lizenzdateien der Pakete)

## Release `v0.1.0` – Abnahme

Pagewing ersetzt Acrobat beim Lesen von PDFs. Manuelle Abnahme durch den Projektinhaber.

- [ ] Eine Woche Alltagsnutzung als Standard-PDF-Programm unter Windows und Linux ohne Rückgriff auf Acrobat zum Lesen
- [ ] Startzeit-Budgets auf dem Rechner des Projektinhabers eingehalten (Messwerte notieren)

---

# Teilprojekt 2 – Annotieren, Ausfüllen, Unterschreiben

**Spec:** noch nicht erstellt (wird in M9 erstellt); Rahmen in der [Gesamt-Spec](superpowers/specs/2026-10-16-pagewing-design.md), Abschnitt 6

## Milestone 9 – Speichern & Textmarkierungen
**Status:** ⬜ offen · **Plan:** – · **Voraussetzungen:** M8

Ziel: Text lässt sich hervorheben, unter- und durchstreichen und kommentieren, und Änderungen werden sicher gespeichert.

**Aufgaben**
- [ ] Spec erstellen (Brainstorming) für Teilprojekt 2
- [ ] Speicher-Pipeline: inkrementell, atomar (temporäre Datei, Prüfung, Ersetzen), Speichern unter, Ungespeichert-Hinweis
- [ ] Hervorheben, Unterstreichen, Durchstreichen
- [ ] Kommentare mit Seitenleiste

**Abnahmekriterien**
- [ ] Gespeicherte Annotationen erscheinen in einem anderen Viewer (z. B. Chrome) korrekt (manuelle Prüfung)
- [ ] Abbruch während des Speicherns lässt das Original unverändert (Test)
- [ ] Digital signierte Test-PDF: Signatur bleibt nach Annotieren gültig (Test oder Prüfung in Acrobat Reader)

## Milestone 10 – Zeichnen & Formen
**Status:** ⬜ offen · **Plan:** – · **Voraussetzungen:** M9

Ziel: Freihandzeichnungen, Formen und Textfelder lassen sich hinzufügen, bearbeiten und speichern.

**Aufgaben**
- [ ] Freihand zeichnen
- [ ] Formen: Rechteck, Kreis, Linie, Pfeil (Umsetzung für Linie/Pfeil laut Spec von Teilprojekt 2)
- [ ] Textfelder hinzufügen
- [ ] Annotationen auswählen, verschieben, löschen, Rückgängig/Wiederholen

**Abnahmekriterien**
- [ ] Round-Trip-Tests: angelegte Annotationen sind nach Speichern und Neuladen unverändert

## Milestone 11 – Formulare
**Status:** ⬜ offen · **Plan:** – · **Voraussetzungen:** M9

Ziel: AcroForm-Formulare lassen sich ausfüllen und speichern.

**Aufgaben**
- [ ] AcroForm-Felder ausfüllen (Text, Checkbox, Radio, Liste, Combo), Tab-Reihenfolge
- [ ] Speichern der Werte
- [ ] Hinweis bei berechneten Feldern (ohne V8 keine JavaScript-Berechnung)

**Abnahmekriterien**
- [ ] Ausgefüllte Test-Formulare zeigen die Werte in einem anderen Viewer (Test + manuelle Prüfung)

## Milestone 12 – Sichtbare Unterschrift
**Status:** ⬜ offen · **Plan:** – · **Voraussetzungen:** M10

Ziel: Eine gezeichnete oder als Bild eingefügte Unterschrift lässt sich platzieren, speichern und wiederverwenden.

**Aufgaben**
- [ ] Unterschrift zeichnen oder als Bild importieren
- [ ] Unterschriften lokal speichern und wiederverwenden
- [ ] Platzieren, skalieren, speichern
- [ ] Hinweis im Dialog: „Ersetzt keine handschriftliche Unterschrift, wo Schriftform verlangt ist.“ (keine Bezeichnung als „rechtsgültig“)

**Abnahmekriterien**
- [ ] Test prüft, dass der Hinweis im Dialog steht und „rechtsgültig“ in keinem UI-Text vorkommt

---

# Teilprojekt 3 – Seiten & Schwärzen

**Spec:** noch nicht erstellt (wird in M13 erstellt); Rahmen in der [Gesamt-Spec](superpowers/specs/2026-10-16-pagewing-design.md), Abschnitt 6

## Milestone 13 – Seiten organisieren
**Status:** ⬜ offen · **Plan:** – · **Voraussetzungen:** M11, M12

Ziel: Seiten lassen sich in App und CLI umsortieren, drehen, löschen, einfügen, zusammenführen, teilen und als Bild exportieren.

**Aufgaben**
- [ ] Spec erstellen (Brainstorming) für Teilprojekt 3, inkl. Entscheidung über QPDF (Lesezeichen beim Zusammenführen, Metadaten)
- [ ] Umsortieren, drehen, löschen, einfügen in der Seitenleiste
- [ ] PDFs zusammenführen und teilen
- [ ] Seiten als Bild exportieren
- [ ] CLI `pagewing merge|split|rotate|export`

**Abnahmekriterien**
- [ ] Tests für alle Seitenoperationen (Seitenzahl, Reihenfolge, Drehung, Inhalt) grün

## Milestone 14 – Schwärzen
**Status:** ⬜ offen · **Plan:** – · **Voraussetzungen:** M13

Ziel: Bereiche und Textstellen lassen sich so schwärzen, dass der Inhalt nachweislich aus der Datei verschwindet.

**Aufgaben**
- [ ] Bereich oder Textstelle schwärzen (Markieren, Vorschau, Anwenden)
- [ ] Textobjekte aufteilen bzw. entfernen, Bildpixel überschreiben, Vektorpfade und Form-XObjects rekursiv behandeln, Annotationen im Bereich löschen
- [ ] Metadaten entfernen (Info-Dictionary, XMP)
- [ ] Immer vollständiger Neuschrieb, Warnung bei digital signierten Dokumenten
- [ ] CLI `pagewing redact`

**Abnahmekriterien**
- [ ] Tests: nach dem Schwärzen enthält weder die Textextraktion noch der dekomprimierte Rohinhalt der Datei den geschwärzten Text (Fälle: Form-XObject, Annotation, Metadaten, ältere inkrementelle Revision)
- [ ] Tests: Bildpixel im geschwärzten Bereich sind im gespeicherten Bild überschrieben

---

# Teilprojekt 4 – OCR & Text bearbeiten

**Spec:** noch nicht erstellt (wird in M15 erstellt); Rahmen in der [Gesamt-Spec](superpowers/specs/2026-10-16-pagewing-design.md), Abschnitt 6

## Milestone 15 – OCR
**Status:** ⬜ offen · **Plan:** – · **Voraussetzungen:** M14

Ziel: Gescannte PDFs lassen sich offline durchsuchbar machen.

**Aufgaben**
- [ ] Spec erstellen (Brainstorming) für Teilprojekt 4, inkl. Auslieferung von Tesseract und Sprachdaten
- [ ] `Pagewing.Ocr`: eigene `[LibraryImport]`-Bindings auf die Tesseract-C-API, erst bei Nutzung geladen
- [ ] Unsichtbare Textebene erzeugen, im Hintergrund mit Fortschritt und Abbruch
- [ ] CLI `pagewing ocr`

**Abnahmekriterien**
- [ ] Gescannte Test-PDF ist nach OCR durchsuchbar, Treffer liegen an der richtigen Stelle (Test)
- [ ] Startzeit-Budgets unverändert eingehalten (Tesseract wird beim Start nicht geladen)

## Milestone 16 – Text bearbeiten
**Status:** ⬜ offen · **Plan:** – · **Voraussetzungen:** M15

Ziel: Vorhandene Textzeilen und Tippfehler lassen sich korrigieren, mit Ersatzschrift für fehlende Zeichen.

**Aufgaben**
- [ ] Vorhandene Zeile auswählen und bearbeiten (bewusst keine Absatz-Bearbeitung)
- [ ] Ersatzschrift für Zeichen, die in der eingebetteten Teilschrift fehlen
- [ ] Hinweis in der UI auf die Grenzen der Textbearbeitung

**Abnahmekriterien**
- [ ] Tests: korrigierte Zeile wird extrahiert wie erwartet, übrige Seite unverändert (Render-Vergleich)

## Backlog

Bewusst nicht eingeplant. Ideen, die `/ideen` hier ablegt, stehen als Liste; bei Bedarf wird daraus ein Milestone.

- **Worker-Prozesse für paralleles Rendern:** PDFium ist nicht thread-sicher; echte Parallelität nur über mehrere Prozesse. Nur angehen, wenn Messungen es nötig machen.
- **Natives Wayland:** Avalonia 12.1 bietet Wayland experimentell als Opt-in (`UseWaylandWithFallback`); Standard bleibt X11/XWayland.
- **Flatpak/Flathub:** Einreichung muss laut Flathub-Regeln ein Mensch vornehmen; Native AOT im Flatpak-Build erst erproben, Dateizugriff über Portale.
- **Code-Signierung über SignPath Foundation:** Kostenlos für OSS; setzt öffentliches Repo und ein veröffentlichtes Release voraus, Herausgeber ist dann die SignPath Foundation.
- **Microsoft Store (MSIX):** Registrierung für Einzelentwickler kostenlos, der Store signiert MSIX selbst.
- **Sitzung wiederherstellen:** Beim Start die zuletzt offenen Tabs mit Position wieder öffnen.
- **Präsentationsmodus:** Vollbild, eine Seite pro Bildschirm, Weiter mit Leertaste/Pfeiltasten.
- **Erweiterte Barrierefreiheit:** Über die Grundlagen hinaus: Screenreader-Unterstützung für Seiteninhalt (UIA unter Windows, AT-SPI2 unter Linux).
- **arm64-Builds:** Die PDFium-Pakete enthalten bereits win-arm64 und linux-arm64; braucht passende Runner.
