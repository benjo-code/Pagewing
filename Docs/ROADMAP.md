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
| 1 | PDFium-Anbindung & Render-Kern | 1 – Fundament & Viewer | ✅ fertig | [Plan](superpowers/plans/2026-10-16-milestone-01-pdfium-render-kern.md) | 2026-10-16 |
| 2 | Designrichtungen | 1 – Fundament & Viewer | ✅ fertig | [Plan](superpowers/plans/2026-10-16-milestone-02-designrichtungen.md) | 2026-10-17 |
| 3 | App-Shell & schnelles Öffnen | 1 – Fundament & Viewer | ✅ fertig | [Plan](superpowers/plans/2026-10-17-milestone-03-app-shell.md) | 2026-10-17 |
| 4 | Navigation | 1 – Fundament & Viewer | 🔵 in Arbeit | [Plan](superpowers/plans/2026-10-17-milestone-04-navigation.md) | – |
| 4.1 | Tabs zwischen Fenstern ziehen | 1 – Fundament & Viewer | ⬜ offen | – | – |
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

- **2026-10-17 (M4: Tabs in eigenes Fenster verschieben; Ziehgeste als neuer Milestone 4.1):** Auf Wunsch des Projektinhabers lassen sich Tabs in ein eigenes Fenster verschieben. M4 liefert das Menü „In neuem Fenster öffnen“ (der Tab wechselt samt Sitzung das Fenster, ohne die PDF neu zu öffnen) und die dafür nötige Architektur (`TabViewModel`, `Workspace`); das Herausziehen und Zurückziehen per Maus ist der neue Milestone 4.1 (Voraussetzung M4). Ebenfalls mit M4 statt M3: Aktionsleiste und Panel (Seiten, Inhalt; „Suche“ folgt in M5), ein Layout-Knopf und das Zoom-Menü in der Statusleiste, Tastenkürzel Strg+0/1/2/G. Plan: `superpowers/plans/2026-10-17-milestone-04-navigation.md`.
- **2026-10-17 (M3: Scrollen mit der Tastatur kommt dazu):** Das Abschluss-Review von M3 fand, dass in der Dokumentansicht nur Bild auf/ab wirkte: Pfeiltasten, Leertaste, Pos1 und Ende taten nichts, und kein Milestone sah sie vor. Sie gehören jetzt zum fortlaufenden Scrollen von M3 (`DocumentView`, Begründung in `Docs/DECISIONS.md`, Eintrag „Dokumentansicht: Zeichenfläche unter dem ScrollViewer …“). Die Aufgaben von M4 und M6 bleiben unverändert.
- **2026-10-16 (M1: Kacheln über `FPDF_RenderPageBitmap_Start` mit Offset statt Matrix-API):** Kacheln werden nicht mit `FPDF_RenderPageBitmapWithMatrix` (Matrix + Clipping) gerendert, sondern mit `FPDF_RenderPageBitmap_Start`: Bitmap in Kachelgröße, Start bei minus Kachel-Offset, Größe der ganzen Seite in Pixeln. Grund: Die Matrix-API ist nicht progressiv, Pause und Abbruch wären damit nicht möglich. Folge: Spec 5.3 und die Roadmap-Aufgabe „Rendern ganzer Seiten und einzelner Kacheln“ sind angepasst. Kacheln sind in PDFium nicht allgemein bitgleich zum Ganzseiten-Rendering und werden deshalb mit 64 px Überlapp nach links gerendert; Scheduler und Kachelgröße (M3) müssen das berücksichtigen (Begründung in `Docs/DECISIONS.md`, Eintrag „Kacheln mit 64 px Überlapp nach links; Kacheln sind in PDFium nicht allgemein bitgleich“).
- **2026-10-16 (M1: Vergleichsmessung nur mit Chrome):** Die Vergleichsmessung wurde nur mit Chrome durchgeführt; Acrobat und SumatraPDF sind auf dem Rechner des Projektinhabers nicht installiert und wurden nicht gemessen. Die Aufgabe „Vergleichsmessung“ in M1 ist entsprechend umformuliert. Das genügt der Entscheidungsregel in `bench/compare/README.md` („Regel für den Vorschlag“), die sich nur auf den schnellsten Browser (Chrome) stützt; Acrobat und SumatraPDF dienten dort nur der Einordnung. Die vier Budgets bleiben unverändert und sind endgültig (Begründung in `Docs/DECISIONS.md`, Eintrag „Vergleichsmessung und endgültige Geschwindigkeitsbudgets“). Offen bleibt der Vergleich mit Acrobat und SumatraPDF.
- **2026-10-17 (M2: Design-Spec führt Dokumentinfos und zwei Einstellungen ein):** Die Design-Spec (`Docs/superpowers/specs/2026-10-17-designsystem.md`) sieht in der Statusleiste den Knopf „Dokumentinfos“ (Abschnitt 7.6) sowie die Einstellungen „Dichte“ (Normal/Kompakt, Abschnitt 5.4) und „Animationen reduzieren“ (Abschnitt 5.6; Avalonia 12.1 kennt keine Systemeinstellung dafür) vor. Folge: M4 bekommt die Aufgabe „Dokumentinfos in der Statusleiste“, die Einstellungen in M6 umfassen zusätzlich Dichte und Animationen reduzieren, und M6 bekommt die Aufgabe „Hochkontrast“ (Fluent wertet die Hochkontrast-Einstellung von Windows nicht aus, Design-Spec Abschnitte 2.7 und 9).
- **2026-10-17 (M3: Ein Dokument pro Fenster, keine Aktionsleiste und kein Panel):** M3 ist eine schlanke Leseansicht mit einem Dokument pro Fenster (mit dem Projektinhaber am 2026-10-17 abgestimmt). Eine zweite Datei (Argument, Pipe, Drop) ersetzt das offene Dokument; die Gesamt-Spec 5.4 spricht von einem neuen Tab, den es erst mit M4 gibt. Die Aktionsleiste und das Panel (Vorschaubilder, Inhalt, Suche) entfallen bis M4 bis M5, weil die Design-Spec keine ausgegrauten Platzhalter erlaubt. Das Seitenfeld der Statusleiste (Anzeige, Eingabe mit Enter, Pfeile) gehört schon zu M3; „Gehe zu Seite“ in M4 ergänzt weitere Wege dorthin. Details: Plan `superpowers/plans/2026-10-17-milestone-03-app-shell.md`, Abschnitt „Scope dieses Plans“.

## Geschwindigkeitsziele

Geschwindigkeit ist das wichtigste Merkmal von Pagewing. Die Budgets sind seit M1 endgültig (Vergleichsmessung mit Chrome, Begründung in `Docs/DECISIONS.md`, Eintrag „Vergleichsmessung und endgültige Geschwindigkeitsbudgets“) und werden ab M3 in der CI geprüft; Details in der [Spec](superpowers/specs/2026-10-16-pagewing-design.md), Abschnitt 5.4:

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
**Status:** ✅ fertig (2026-10-16) · **Plan:** [2026-10-16-milestone-01-pdfium-render-kern.md](superpowers/plans/2026-10-16-milestone-01-pdfium-render-kern.md) · **Voraussetzungen:** M0

Ziel: Der Kern öffnet PDFs, rendert Seiten und Kacheln und extrahiert Text, nachweisbar über die CLI, Render-Regressionstests und Benchmarks.

**Aufgaben**
- [x] `bblanchon.PDFium.Win32` und `bblanchon.PDFium.Linux` (ohne V8) einbinden, Drittlizenzen der Pakete erfassen
- [x] `Pagewing.Pdfium`: `[LibraryImport]`-Bindings nur für benötigte Funktionen, SafeHandles, `PdfiumThread` (alle Aufrufe auf einem Thread, Fremd-Thread-Aufruf löst Exception aus)
- [x] Öffnen aus Datei, auch passwortgeschützte PDFs; verständliche Fehler bei kaputten oder verschlüsselten Dateien
- [x] Rendern ganzer Seiten und einzelner Kacheln (über `FPDF_RenderPageBitmap_Start` mit Offset), progressiv mit Pause und Abbruch
- [x] Textextraktion pro Seite (Grundlage für Suche und Schwärzungstests)
- [x] CLI `pagewing info|render|text`
- [x] `testdata/pdfs` mit lizenzklaren Test-PDFs und `LICENSES.md`; `Pagewing.Render.Tests` vergleicht Renderings mit Referenzbildern (Toleranz), läuft in der CI auf beiden OS
- [x] `Pagewing.Benchmarks` (BenchmarkDotNet): Öffnen und Rendern
- [x] Vergleichsmessung mit Chrome auf dem Rechner des Projektinhabers (Acrobat und SumatraPDF nicht installiert, nicht gemessen); Budgets in Roadmap, `CLAUDE.md` und Spec endgültig festlegen

**Abnahmekriterien**
- [x] `pagewing render` erzeugt für alle Test-PDFs PNGs, die mit den Referenzbildern übereinstimmen (Render-Tests grün auf Linux und Windows) – `RenderRegressionTests.PagesMatchTheReferenceImages` (Theory über alle Test-PDFs, rendert mit `CliApp.RunAsync` im Prozess, also über dieselbe Logik wie `pagewing render`; Modus `Strict` bzw. `Tolerant` je Fall; ergänzt durch `EveryTestPdfIsInTheCorpusOrExplicitlySkipped`, `RenderingTwiceIsBitIdentical` und die Mutationstests `StrictComparisonDetectsAOnePixelShift`, `TolerantComparisonDetectsMissingText`, `TolerantComparisonDetectsAnEightPixelShift`, `TolerantComparisonDetectsAColourChange`): `dotnet test Pagewing.slnx` lokal (Windows) 236 bestanden, 0 fehlgeschlagen; CI-Lauf 38963543241 (PR #2, Commit c7aaa49) und CI-Lauf 38964588665 (PR #2, Commit 4e1cc4e, nach dem Commit mit diesem Nachweis): `build-linux` und `build-windows` grün
- [x] Test belegt, dass ein PDFium-Aufruf von einem fremden Thread eine Exception auslöst – `PdfiumDocumentTests.CallFromForeignThreadThrows` (und `PdfiumTextPageTests.CallFromForeignThreadThrows`), grün in `dotnet test Pagewing.slnx` lokal und im CI-Lauf 38963543241 (`build-linux`, `build-windows`)
- [x] Passwortgeschützte Test-PDF öffnet mit richtigem Passwort und scheitert mit falschem mit klarer Meldung (Test) – `OpenTests.EncryptedFileOpensWithCorrectPassword`, `OpenTests.EncryptedFileWithWrongPasswordThrowsPasswordIncorrect`, `OpenTests.EncryptedFileWithoutPasswordThrowsPasswordRequired` (je für `encrypted_hello_world_r3.pdf` und `_r6.pdf`) sowie CLI-seitig `CliTests.WrongPasswordExitsWithOne` und `CliTests.EncryptedWithoutPasswordExitsWithOneAndHint`; grün lokal und im CI-Lauf 38963543241
- [x] Vergleichsmessung dokumentiert (Werte und Methode in `Docs/DECISIONS.md`), Budgets festgelegt – Rohwerte `bench/compare/results/2026-10-16-WIN11-UNITY.csv` (40 Läufe) mit Begleitnotiz `2026-10-16-WIN11-UNITY.md`; Werte, Methode, Regel und Grenzen im Eintrag „Vergleichsmessung und endgültige Geschwindigkeitsbudgets“ in `Docs/DECISIONS.md`. Gemessen wurde nur Chrome 155.0.8059.80 (Acrobat und SumatraPDF nicht installiert); das genügt der Entscheidungsregel, die sich nur auf Chrome stützt. Ergebnis: Die vier vorläufigen Budgets (400 / 200 / 100 / 300 ms) bleiben unverändert und sind laut Projektinhaber (2026-10-16) endgültig. Chrome-Mediane (`t_visually_complete`): 1487,1 / 1537,3 / 613,3 / 1738,7 ms

## Milestone 2 – Designrichtungen
**Status:** ✅ fertig (2026-10-17) · **Plan:** [2026-10-16-milestone-02-designrichtungen.md](superpowers/plans/2026-10-16-milestone-02-designrichtungen.md) · **Spec:** [2026-10-17-designsystem.md](superpowers/specs/2026-10-17-designsystem.md) · **Voraussetzungen:** M0

Ziel: Der Projektinhaber hat aus mindestens drei Designrichtungen eine gewählt, und sie ist als Design-Spec mit Tokens festgehalten.

**Aufgaben**
- [x] Mindestens 3 unterschiedliche, zum Projekt passende Designrichtungen als HTML-Mockups (Artifacts): Hauptfenster mit geöffnetem PDF, Seitenleiste und Werkzeugleiste, jeweils hell und dunkel
- [x] Entscheidung des Projektinhabers einholen und in `Docs/DECISIONS.md` festhalten (inkl. verworfener Richtungen)
- [x] Design-Spec unter `Docs/superpowers/specs/` mit Tokens (Farben hell/dunkel, Typografie, Abstände, Radien, Icons), Fenster-Layout und Verhalten der wichtigsten Bereiche
- [x] App-Name-Schriftzug und Icon-Richtung festlegen (Grundlage für M8)

**Abnahmekriterien**
- [x] Mindestens 3 Richtungen wurden als Artifacts bereitgestellt (Links in der Design-Spec) – Design-Spec Abschnitt 3: https://claude.ai/artifact/He1E7CwqM3d8eQTxC2pnPs (A), https://claude.ai/artifact/Qz1iJbLUijWbUH21WXvq6y (B), https://claude.ai/artifact/GqTxJnQPUbhrRoX9bjQ1ae (C); WCAG-2.2-AA-Kontrast bestanden (28 Paare je Theme, 56 Prüfungen je Richtung), Sichtprüfung hell/dunkel bei 1440 px und 375 px
- [x] Projektinhaber hat eine Richtung ausdrücklich gewählt – 2026-10-17: Wahl von Richtung B „Werkbank“ in der überarbeiteten Fassung (Artifact-Version 2), Wortlaut „ja passt ich nehme richtung B“; Eintrag „Designrichtung: B „Werkbank“ (überarbeitete Fassung)“ in `Docs/DECISIONS.md`, Design-Spec Abschnitt 4
- [x] Design-Spec ist committet und in der Roadmap verlinkt – `Docs/superpowers/specs/2026-10-17-designsystem.md` (Status „abgestimmt“), committet auf `feature/m02-designrichtungen` ab Commit 43dd11d; im M2-Eintrag als `spec` verlinkt; `dotnet run tools/roadmap.cs -- check` grün

## Milestone 3 – App-Shell & schnelles Öffnen
**Status:** ✅ fertig (2026-10-17) · **Plan:** [2026-10-17-milestone-03-app-shell.md](superpowers/plans/2026-10-17-milestone-03-app-shell.md) · **Voraussetzungen:** M1, M2

Ziel: Pagewing öffnet eine PDF im gewählten Design und scrollt flüssig durch alle Seiten, innerhalb der Startzeit-Budgets.

**Aufgaben**
- [x] Hauptfenster nach Design-Spec, UI-Texte lokalisierbar (resx, Englisch und Deutsch)
- [x] Öffnen per Kommandozeilenargument, Dateidialog und Drag & Drop
- [x] Single-Instance: zweiter Aufruf übergibt den Pfad an die laufende Instanz (Named Pipe / Unix-Socket)
- [x] Fortlaufendes Scrollen mit Virtualisierung, Render-Scheduler (sichtbar zuerst, erst grob, dann scharf, Abbruch veralteter Aufträge) und LRU-Cache mit Speicherbudget
- [x] Zoom (Strg+Mausrad, Strg+Plus/Minus)
- [x] Schalter `--measure-startup`, Skript `bench/startup`, Startzeit-Check in der CI (Median aus 10 Läufen, Linux unter Xvfb)
- [x] `Pagewing.App.Tests` mit Avalonia.Headless

**Abnahmekriterien**
- [x] Startzeit-Check in der CI grün auf beiden Runnern, Werte als Artefakt – CI-Lauf 39033433468 (PR #4, Commit 82e3049): `build-linux` und `build-windows` grün, Schritt „Startup check“ besteht; Artefakte `startup-windows` und `startup-linux` mit den CSV-Werten. Mediane in ms (Budget) im Lauf 39033433468: Windows cold 148,1 (400), warm 146,9 (200), thousand 143,9 (300), second 46,3 (100); Linux unter Xvfb cold 148,0, warm 146,8, thousand 145,0, second 43,4. Mit dem Standard „Software-Rendering unter Windows“ (Eintrag „Software-Rendering als Windows-Standard (M3)“ in `Docs/DECISIONS.md`) erneut grün, CI-Lauf 39044339823 (Commit ed52c2e): Windows cold 160,7, warm 147,3, thousand 147,5, second 47,7; Linux cold 163,9, warm 166,4, thousand 149,3, second 47,0. Einschränkung: Der Windows-Runner ist eine VM ohne GPU; `cold` leert den Datei-Cache nicht. Zusätzlich auf dem Haupt-PC des Projektinhabers (Ryzen 7 7800X3D, RTX 4070 SUPER, 1080p/60 Hz) gemessen, Workflow „Hardware measurement“, Lauf 39045832687: cold 200,0, warm 199,7, thousand 197,8, second 15,5 (alle im Budget, der Warmstart knapp); der GPU-Pfad (`PAGEWING_RENDERING=default`) liegt dort bei 472,7 ms warm.
- [x] Weitere Datei bei laufender Instanz öffnet als neuer Inhalt im selben Prozess (Test + manuelle Prüfung) – Tests `OpenFlowTests.ExternalOpenReplacesTheDocument` und `SingleInstanceTests.PathsArriveAtThePrimary` grün (`dotnet test Pagewing.slnx`, M3 Task 13). Manuell am AOT-Binary (win-x64) am 2026-10-17: laufende Instanz mit `text-basic.pdf`, dann `Pagewing.exe hello_world.pdf` (relativer Pfad) und ein Pfad mit Leerzeichen; beide späteren Aufrufe enden mit Exit 0, die laufende Instanz zeigt danach die neue Datei im Fenstertitel, es bleibt ein Pagewing-Prozess. Nur unter Windows geprüft.
- [x] Schnelles Scrollen durch eine 1000-seitige Test-PDF ohne Ruckler des UI-Threads (manuelle Prüfung, Messwert notieren) – Messwert (`--measure-scroll testdata/pdfs/thousand-pages.pdf`, 600 Schritte, Abstände der Frame-Rückrufe, nicht angezeigte Bilder) auf dem Haupt-PC des Projektinhabers (Ryzen 7 7800X3D, RTX 4070 SUPER, 1920 × 1080 bei 60 Hz), Workflow „Hardware measurement“, Lauf 39045832687, Software-Rendering (Standard): Zoom 100 % p99 15,5 bis 16,9 ms, max höchstens 19,1 ms, kein Frame über 20 ms; Zoom 400 % p99 10,6 bis 10,8 ms. Manuelle Prüfung durch den Projektinhaber am 2026-10-17 mit dem gebauten AOT-Binary und `thousand-pages.pdf`: Scrollen ist flüssig (Aussage des Projektinhabers: „ist flüssig“). Grenzen: nur dieser PC, 1080p/60 Hz; die Mittel der Abstände (4 bis 8 ms) liegen unter der Bildschirmperiode und sind unerklärt; 4K, 144 Hz und schwache CPUs sind nicht gemessen.

## Milestone 4 – Navigation
**Status:** 🔵 in Arbeit · **Plan:** [2026-10-17-milestone-04-navigation.md](superpowers/plans/2026-10-17-milestone-04-navigation.md) · **Voraussetzungen:** M3

Ziel: Mehrere PDFs lassen sich in Tabs öffnen und über Vorschau, Inhaltsverzeichnis, Layouts und Zoom-Modi bequem navigieren.

**Aufgaben**
- [x] Tabs (öffnen, schließen, wechseln, Reihenfolge ändern)
- [ ] Seitenleiste mit Seitenvorschau (Thumbnails im Hintergrund, nach dem ersten Seitenbild)
- [ ] Inhaltsverzeichnis/Lesezeichen aus dem PDF anzeigen und anspringen
- [x] Layouts: einzeln, fortlaufend, doppelseitig (mit/ohne Deckblatt)
- [x] Zoom-Modi: Seitenbreite, ganze Seite, tatsächliche Größe, freier Zoom
- [x] Gehe zu Seite
- [ ] Dokumentinfos in der Statusleiste (Design-Spec 7.6: Datei, Seitenzahl, Seitenformat, PDF-Version, Dateigröße, Metadaten)

**Abnahmekriterien**
- [ ] Headless-Tests für Tab-Wechsel, Layoutwechsel und Lesezeichen-Sprung grün
- [ ] Startzeit-Budgets weiter eingehalten (CI)

## Milestone 4.1 – Tabs zwischen Fenstern ziehen
**Status:** ⬜ offen · **Plan:** – · **Voraussetzungen:** M4

Ziel: Ein Tab lässt sich per Maus aus der Tableiste ziehen und wird zu einem eigenen Fenster; ein Tab lässt sich in die Tableiste eines anderen Fensters zurückziehen.

**Aufgaben**
- [ ] Ziehgeste: Tab aus der Leiste herausziehen erzeugt ein neues Fenster am Zeiger (baut auf `Workspace.MoveToNewWindow` und dem Umsortieren aus M4 auf)
- [ ] Ablegen in der Tableiste eines anderen Fensters führt den Tab samt Sitzung dort ein (Position nach dem Zeiger)
- [ ] Verhalten unter Windows und Linux/X11 klären (Zeiger-Fang über Fenstergrenzen, Fensterposition; Wayland nur als Hinweis)

**Abnahmekriterien**
- [ ] Headless-Tests für Herauslösen und Zurückführen eines Tabs grün (Sitzung bleibt dieselbe, Seite und Zoom bleiben)
- [ ] Manuelle Prüfung auf Windows und Linux/X11: Tab herausziehen, zurückziehen, zwischen zwei Fenstern verschieben

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
- [ ] Einstellungen (Sprache, Theme, Dichte Normal/Kompakt, Animationen reduzieren, Standard-Layout und -Zoom), gespeichert per Source-generiertem JSON
- [ ] Hochkontrast (Windows `ContrastPreference`): entscheiden, ob Pagewing eine eigene Hochkontrast-Palette bekommt, und sie gegebenenfalls umsetzen (Design-Spec, Abschnitt 9)

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
- **Vergleichsmessung mit Acrobat und SumatraPDF nachholen:** Das Werkzeug `tools/measure-open.cs` und das Protokoll in `bench/compare/README.md` gibt es; die Befehle für Acrobat und SumatraPDF sind ungetestet (beide waren nicht installiert). Nur für die Einordnung im Vergleich nötig, nicht für die Budgetregel (die stützt sich nur auf Chrome).
- **Native-AOT-Laufzeitfehler (AppContext/UTF8Encoding-Zyklus) an dotnet/runtime melden:** Der sporadische Startabsturz `0xC0000602` ist ein Fehler der Native-AOT-Laufzeit von .NET 10: Wartet ein Thread im Klassenkonstruktor von `UTF8Encoding` zum ersten Mal im Prozess auf eine `System.Threading.Lock`, liest die Sperre `AppContext`, dessen Klassenkonstruktor `Encoding.UTF8` braucht, das dann noch `null` ist. Pagewing umgeht das (`Program.Main` von App und CLI sowie `PdfiumThread` initialisieren `AppContext` früh). Offen: Fehlerbericht an dotnet/runtime mit Stack und kleiner Reproduktion (Belege: `Docs/DECISIONS.md`, Eintrag „Startabsturz 0xC0000602 …“) und nach jedem .NET-Update prüfen, ob der Fehler behoben ist; erst dann kann der Workaround entfallen. Ein Issue gibt es noch nicht.
- **Erster Frame über ANGLE: Verhalten auf Rechnern mit GPU messen:** Die früher unerklärten ≈170 ms (Entwicklungs-VM) bzw. ≈200 ms (Windows-Runner) zwischen gezeichneter scharfer Kachel und dem Frame danach liegen laut Phasenmessung im ersten Frame über ANGLE/WARP (`Docs/DECISIONS.md`, Eintrag „Windows-Warmstart: Phasenmessung auf dem Runner (M3)“). Gemindert durch die Einträge „Seitenschatten als vorberechnetes Bild (M3)“ (rund 60 ms) und „Software-Rendering ohne Hardware-Grafikadapter (M3)“ (Abstand auf dem Runner 31 ms). Gemessen auf dem Haupt-PC (Lauf 39041482043, NVIDIA RTX 4070 SUPER): erster Frame über ANGLE 70 bis 90 ms, in Software rund 16 ms. Seit „Software-Rendering als Windows-Standard (M3)“ betrifft das nur noch den GPU-Pfad (`PAGEWING_RENDERING=default`); der Hardware-Workflow zeigt ihn zur Information. Offen nur, falls der GPU-Pfad wieder Standard oder eine Einstellung wird.
- **Arbeitsspeicher beim Scrollen: bis zum 2,4-Fachen des Cache-Budgets (Bildpuffer):** Für Nutzer sichtbar: Beim schnellen Scrollen durch 1000 Seiten belegt die App als Spitze (Working Set) 400 MiB bei 100 % und 556–616 MiB bei 200–800 %, also das 1,6- bis 2,4-Fache des Cache-Budgets von 256 MiB. Wer „Speicher unter dem Cache-Budget“ am belegten Speicher des Prozesses misst, sieht das Budget deshalb nicht eingehalten; der Cache selbst hält es etwa ein (verwalteter Heap nach voller Bereinigung 259 MiB). Ursache: verdrängte Bildpuffer bleiben im Large Object Heap belegt, dazu native Bitmaps. Gemessen in der Entwicklungs-VM mit Software-Darstellung. Ziel (Vorschlag aus dem Abschluss-Review von M3): Working Set beim Scrollen höchstens 1,5 × Budget (384 MiB). Erster, günstigster Versuch in M4, vor wiederverwendeten Puffern: `System.GC.ConserveMemory` in der runtimeconfig, mit Messung der Wirkung auf den Start (einmal als `DOTNET_GCConserveMemory=7` geprüft: 584 → 455 MiB bei 200 %, gleiche Frame-Zeiten, Start nicht gemessen). Reicht das nicht: wiederverwendete oder nicht verwaltete Bildpuffer. Messwerte und Methode: `Docs/DECISIONS.md`, Eintrag „Render-Scheduler und Kachelparameter (M3)“, Abschnitt „Bestätigung“.
- **Startzeit-Messung auf echter Hardware: Haupt-PC des Projektinhabers als Runner auf Abruf:** Der Haupt-PC des Projektinhabers wird als manuell gestarteter Runner (Label `dotnet-windows-gpu`, Name `hauptpc`, Runner-Gruppe `main-pc`, kein Dienst) eingerichtet, nur auf Abruf. Der Workflow dafür liegt vor: `.github/workflows/hardware-measure.yml` („Hardware measurement“; `workflow_dispatch` oder Label `hardware-measure` am PR; Einrichtung und Ablauf: `bench/startup/README.md`, Abschnitt „Messung auf echter Hardware (Haupt-PC)“; Entscheidung: `Docs/DECISIONS.md`, Eintrag „Messung auf echter Hardware: manueller Runner des Haupt-PCs (M3)“). Der Runner ist eingerichtet; Läufe 39035639908 und 39041482043 (Messmatrix, Ergebnis: `Docs/DECISIONS.md`, Eintrag „Software-Rendering als Windows-Standard (M3)“). Offen: ein Bestätigungslauf mit dem neuen Standard (Software). Gemessen wird vor Releases. Die Abnahme von v0.1.0 verlangt die Messwerte auf dem Rechner des Projektinhabers: die vier Fälle aus `bench/startup/README.md` ohne `Warnung:`-Zeile, mit `--phases`, `--measure-scroll` und die manuelle Scroll-Prüfung aus M3. Hintergrund: Der Windows-Runner `dotnet-windows` (`vm-git-windows-dotnet`) ist eine Proxmox-VM ohne GPU (Standard-VGA, „Microsoft Basic Display Adapter“) in einer angemeldeten Sitzung (Autologon und geplante Aufgabe); er misst wie der Haupt-PC den Software-Pfad, den Standard unter Windows (`Docs/DECISIONS.md`, Eintrag „Software-Rendering als Windows-Standard (M3)“); den GPU-Pfad misst nur ein informativer Schritt des Hardware-Workflows. In der Entwicklungs-VM (einzige Anzeige „Red Hat VirtIO GPU DOD controller“) sind Startzeiten wegen des DWM-Takts ohnehin nicht gültig (Eintrag „Frame-Takt der Desktop-Komposition …“).
- **Einstellung „Hardwarebeschleunigung“ (Idee, etwa M6):** Nur eine Idee, nicht geplant: Unter Windows rendert Pagewing in Software, der GPU-Pfad ist nur über `PAGEWING_RENDERING=default` erreichbar (`Docs/DECISIONS.md`, Eintrag „Software-Rendering als Windows-Standard (M3)“). Eine Einstellung könnte ihn für Nutzer schaltbar machen, wenn Messungen auf 4K, hohen Bildfrequenzen, schwachen CPUs oder im Akkubetrieb zeigen, dass Software-Rendering dort nicht reicht. Gemessen ist bisher nur 1920 × 1080 bei 60 Hz auf einer schnellen CPU.
