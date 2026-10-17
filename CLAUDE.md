# Pagewing

Pagewing ist eine schnelle Open-Source-Alternative zu Adobe Acrobat für Windows und Linux: PDFs ansehen, kommentieren, ausfüllen, unterschreiben, organisieren, schwärzen, per OCR durchsuchbar machen, als Desktop-App und als CLI mit gemeinsamem Kern. Geschwindigkeit (Start, Öffnen, Scrollen) ist das wichtigste Merkmal, gefolgt von gutem, modernem Design. Komplett offline, keine Telemetrie.

- **Stack:** .NET 10 (LTS) mit Native AOT · Avalonia 12.1 · PDFium (`bblanchon.PDFium.*`, ohne V8) über eigene `[LibraryImport]`-Bindings · später QPDF (Kandidat) und Tesseract über deren C-APIs
- **Lizenz:** GPL-3.0-or-later ([LICENSE](LICENSE))
- **Sprache:** Doku und Commits Deutsch · Code und Kommentare Englisch · UI-Texte Englisch und Deutsch (resx, Auswahl nach Systemsprache)
- **Build:** `dotnet build Pagewing.slnx` · **Test:** `dotnet test Pagewing.slnx` · **Format/Lint:** `dotnet format Pagewing.slnx --verify-no-changes`
- **AOT-Publish:** `dotnet publish src/Pagewing.App -c Release -r <win-x64|linux-x64>` (analog `src/Pagewing.Cli`); nur auf dem jeweiligen OS möglich
- **Gesamt-Spec:** [Docs/superpowers/specs/2026-10-16-pagewing-design.md](Docs/superpowers/specs/2026-10-16-pagewing-design.md)
- **Fahrplan:** [Docs/ROADMAP.md](Docs/ROADMAP.md) (generiert, Quelle [Docs/roadmap.json](Docs/roadmap.json), siehe „Roadmap pflegen“)
- **Entscheidungen:** [Docs/DECISIONS.md](Docs/DECISIONS.md)
- **Specs:** `Docs/superpowers/specs/` · **Pläne:** `Docs/superpowers/plans/`

## Projektspezifisches

### Architektur

- Projekte und Abhängigkeitsrichtung laut Spec, Abschnitt 5.1: `Pagewing.App` und `Pagewing.Cli` → `Pagewing.Core` → `Pagewing.Pdfium` (später `Pagewing.Qpdf`, `Pagewing.Ocr`). App und CLI kennen keine PDFium-Typen.
- **PDFium ist nicht thread-sicher**, auch nicht über verschiedene Dokumente hinweg (`fpdfview.h`: „None of the PDFium APIs are thread-safe“). Nur `Pagewing.Pdfium` ruft PDFium auf, und zwar ausschließlich über den `PdfiumThread`. Ein Aufruf von einem anderen Thread muss eine Exception auslösen. Kein paralleles Rendern über mehrere Threads.
- Native AOT (.NET 10): `AppContext` muss initialisiert sein, bevor der erste zusätzliche Thread startet, sonst kann der Start sporadisch mit `0xC0000602` abstürzen. Das erledigen die erste Anweisung von `Program.Main` in App und CLI und zusätzlich der Konstruktor von `PdfiumThread`; wer früher einen Thread startet (etwa in einem statischen Konstruktor), muss es vorher tun. Siehe `Docs/DECISIONS.md`, Eintrag „Startabsturz 0xC0000602 …“.
- Der UI-Thread wartet nie auf PDFium. Er stellt Render-Aufträge ein und bekommt Bitmaps zurück.
- Render-Pipeline: Nur sichtbare Seiten plus Puffer werden gerendert. Zuerst kommt eine grobe Vorschau, dann folgen scharfe Kacheln, progressiv mit Pause und Abbruch veralteter Aufträge. Ein LRU-Cache mit Speicherbudget hält die Ergebnisse.
- App-Shell (seit M3):
  - `Pagewing.Core.Viewing` ist UI-frei: `DocumentLayout` (Seitenrechtecke, Zoom), `ViewportPlanner` und `TilePlanner` (welche Vorschauen und Kacheln der Viewport braucht, Konstanten dort), `RenderCache` (LRU, 256 MiB), `RenderScheduler` (eine Wunschliste, höchstens ein nicht abgebrochener Auftrag gleichzeitig auf dem `PdfiumThread`, Ereignisse auf Pool-Threads, Handler dürfen nie werfen), `ViewerSession` (Zustände `Empty`/`Loading`/`Ready`/`PasswordRequired`/`PasswordIncorrect`/`Failed`, Seitengrößen in Stapeln).
  - `Pagewing.App`: `Program.Main` liest `StartupOptions`, wählt die Single-Instance und startet das Öffnen einer übergebenen Datei sofort, noch vor Avalonia. `MainViewModel` spiegelt die Session (eigene `ViewModelBase`/`RelayCommand`, keine MVVM-Bibliothek). `Views/DocumentView` besitzt Layout und Scroll-Position; `Controls/DocumentCanvas` liegt in Viewport-Größe unter dem `ScrollViewer` und zeichnet in Viewport-Koordinaten (Eintrag „Dokumentansicht …“ in `Docs/DECISIONS.md`).
  - Single-Instance (`Pagewing.Core.Hosting.SingleInstance`): Sperrdatei wählt die primäre Instanz, Named Pipe bzw. Unix-Socket übergibt die Pfade; die Pipe öffnet erst nach dem ersten Frame.
  - UI-Texte: `Resources/Strings.en.resx` und `Strings.de.resx` sind beide in die Haupt-Assembly eingebettet (keine Satellitenassemblies), `Strings` wählt nach `CurrentUICulture`, `--lang <code>` überschreibt das.
- Bindings nur für Funktionen, die gebraucht werden. Konstanten (z. B. Speicher-Flags) immer gegen die Header der ausgelieferten PDFium-Version prüfen, weil sie sich ändern: `FPDF_REMOVE_SECURITY` ist seit `chromium/8100` 4 statt 3.
- Plattformabhängige C-Typen: `unsigned long` ist unter Windows 32 Bit, unter Linux 64 Bit breit und wird deshalb immer als `CULong` gebunden (nie `uint`/`ulong`). Folge und Grenze: `PdfiumDocument.MaxFileLength`, siehe `Docs/DECISIONS.md`, Eintrag „PDFium-Anbindung“.
- Rendern: `FPDF_RenderPageBitmap_Start` verlangt eine Pause-Struktur ungleich `null` (sonst `FPDF_RENDER_FAILED`), siehe `PdfiumRenderWork`. Kacheln rendert `PdfiumRenderWork` mit 64 px Überlapp nach links (`TileOverlap`) und sind nicht allgemein bitgleich zum Ganzseiten-Rendering, Begründung im `DECISIONS`-Eintrag „Kacheln mit 64 px Überlapp nach links …“.
- **Native AOT ist Pflicht** für App und CLI:
  - kompilierte Bindings, keine XAML-Ladung zur Laufzeit, keine Reflection-basierte Serialisierung (JSON nur mit Source-Generator),
  - `IsAotCompatible` in jedem Projekt unter `src/` (je Projekt gesetzt, nicht im Root), AOT- und Trim-Warnungen sind Fehler.
  - Native AOT kompiliert nicht über Betriebssystemgrenzen hinweg: Windows-Binaries entstehen auf `dotnet-windows`, Linux-Binaries auf `dotnet-linux`.

### Geschwindigkeit

- Budgets (seit M1 endgültig, Vergleichsmessung und Begründung in `Docs/DECISIONS.md`, Eintrag „Vergleichsmessung und endgültige Geschwindigkeitsbudgets“) stehen in der Roadmap unter „Geschwindigkeitsziele“. Ab M3 prüft der Startzeit-Check in der CI sie: Schalter `--measure-startup`, Skript `bench/startup`, Median aus 10 Läufen, Fehlschlag ab Budget + 25 %.
- Versteckte Messschalter der App (nicht in der Hilfe): `--measure-startup <pdf>` schreibt `first-sharp` auf stdout, sobald das erste scharfe Bild von Seite 1 gezeichnet ist; `--measure-second <a> <b>` öffnet danach `b` wie ein späterer Aufruf (`MainViewModel.OnExternalOpen`) und schreibt `second-sharp <ms>`; `--measure-scroll <pdf> [--zoom <Prozent>]` schreibt `scroll frames=<n> p99-ms=<x> max-ms=<y>` und `scroll-detail …` (ohne Budget; bei Scrollweg 0 eine Warnung auf stderr, die Abstände sind dann Avalonias Takt ohne Änderung, siehe `bench/startup/README.md`). Exit-Codes 0 / 1 (`error: …` auf stderr) / 3 (30 s Zeitlimit). Messläufe umgehen die Single-Instance-Wahl. Code: `src/Pagewing.App/Measurement/`.
- Nach `first-sharp` schreiben die Start- und Zweitdatei-Messläufe (`--measure-startup`, `--measure-second`) Phasenzeiten seit Prozessstart (`phase <Name> <ms>`); `bench/startup/startup.cs --phases` gibt ihre Mediane aus, nur zur Information (CI: Fall `warm`). Befund auf dem Windows-Runner: `Docs/DECISIONS.md`, Eintrag „Windows-Warmstart: Phasenmessung auf dem Runner (M3)“.
- `bench/startup/startup.cs` (`--case cold|warm|thousand|second`, `selftest`) misst damit die Budgets, Protokoll in `bench/startup/README.md`, Definition in `Docs/DECISIONS.md`, Eintrag „Startzeitmessung: Definition und Grenzen (M3)“.
- Startzeiten nur auf einem Rechner mit GPU und aktiver Anzeige messen. Avalonia richtet seine Frames unter Windows nach DWM; zeigt DWM nur wenige Bilder pro Sekunde (etwa in einer VM ohne GPU), sind alle Startzeiten ungültig. `startup.cs` schreibt dann eine `Warnung: …`; fehlt sie, sind die Zahlen damit noch nicht belegt gültig. Befund: `Docs/DECISIONS.md`, Eintrag „Frame-Takt der Desktop-Komposition: Messumgebung statt Kompositionsmodus (M3)“.
- Unter Windows rendert die App immer in Software (`Win32RenderingMode.Software` als einziger Modus, kein Direct3D, kein ANGLE, keine Komposition; `RenderingModeSelector`), auf Rechnern mit und ohne GPU. `PAGEWING_RENDERING=default` wählt Avalonias Standard (GPU-Pfad über ANGLE), jeder andere Wert behält Software. Andere Systeme nutzen Avalonias Standard. CI-Runner und Haupt-PC messen damit beide den Software-Pfad. Begründung: `Docs/DECISIONS.md`, Eintrag „Software-Rendering als Windows-Standard (M3)“.
- Nichts Neues im Startpfad ohne Messung. Bis zum ersten Frame wird nur geladen, was das Fenster braucht. PDFium startet beim ersten Öffnen eines Dokuments, Thumbnails, Textindex und OCR laufen erst nach dem ersten scharfen Seitenbild.
- Single-Instance: Ein zweiter Aufruf übergibt die Datei an die laufende Instanz (Named Pipe bzw. Unix-Socket).
- Budgets oder den Startzeit-Check nie lockern, nur damit etwas grün wird. Eine Budgetänderung ist eine Entscheidung in `Docs/DECISIONS.md`.

### Design

- Verbindlich für alle UI-Arbeit ist die Design-Spec [Docs/superpowers/specs/2026-10-17-designsystem.md](Docs/superpowers/specs/2026-10-17-designsystem.md) (Richtung B „Werkbank“, überarbeitet): Tokens, Maße, Layout und Verhalten. Weichen Mockup und Spec ab, gilt die Spec. Tokens und Maße nur dort ändern; jede Änderung ist ein Eintrag in `Docs/DECISIONS.md`.
- Gelb ist nie Akzent, sondern nur für Suchtreffer (Markierungsfarben sind PDF-Inhalt). Zustände nie nur über Farbe kennzeichnen. Ein neues Farbpaar vor der Verwendung auf WCAG 2.2 AA prüfen (Text 4,5:1, Icons, Ränder und Fokus 3:1).
- Icons: Tabler outline mit 2 px Strich, nur die genutzten Pfade kopieren. `PathIcon` füllt nur, deshalb braucht es ein eigenes Icon-Control mit Strich.
- Den Seitenschatten nie als `BoxShadow` zeichnen: `PageShadowPainter` berechnet ihn als Bild aus dem Token `Pw.Shadow.Page` (über ANGLE ohne GPU kostete der `BoxShadow` rund 60 bis 80 ms im ersten Frame; `Docs/DECISIONS.md`, Eintrag „Seitenschatten als vorberechnetes Bild (M3)“).
- Die Leseansicht zeigt keine Bearbeitungswerkzeuge. Funktionen späterer Versionen erscheinen nicht ausgegraut in der UI.
- Mockup-Quellen liegen unter `Docs/design/mockups/` (Artifact-Quellen ohne Dokument-Rahmen; lokal nur eingeschränkt darstellbar, maßgeblich ist die Artifact-Ansicht). Das App-Icon (Master, hicolor-Größen, `.desktop`-Vorlage) liegt unter `Docs/design/icons/`, die `.ico` bei der App unter `Assets/`, der Schriftzug unter `Docs/design/`. Windows nimmt das Icon über `ApplicationIcon`, Linux setzt `Window.Icon` nach dem ersten Frame.

### Abhängigkeiten

- Vor jeder neuen Abhängigkeit prüfen:
  - Lizenz GPL-3.0-kompatibel (MIT, BSD, Apache-2.0, zlib o. ä.). Kein AGPL, nichts Kommerzielles, kein Dual-Licensing mit kommerzieller Pflicht.
  - Native-AOT-fähig (ohne Reflection.Emit).
  - Eintrag in `Docs/DECISIONS.md`.
- Ausdrücklich ausgeschlossen: MuPDF, iText, PDFsharp ab 6.5 (AGPL/kommerziell), PDFiumCore (wir nutzen eigene Bindings), die .NET-Wrapper für Tesseract (nicht AOT-fähig).
- PDFium-Pakete ohne V8 verwenden: `bblanchon.PDFium.Win32` und `bblanchon.PDFium.Linux`, nicht `bblanchon.PDFiumV8.*`. Es gibt jede Woche ein Release, die Versionsnummer ist die Chromium-Buildnummer, damit ist jedes Update formal ein Major-Update. Dependabot-PRs der Gruppe `bblanchon.PDFium*` nur mergen, wenn die Render-Regression grün ist.
  - Die Schrittzahl-Prüfungen in `tests/Pagewing.Core.Tests/Pdfium/PdfiumRenderWorkTests.cs` (`StepCount > 2` in `InterleavedTileRendersMatchSequentialRenders`, das Pausieren bei Zeitscheibe 0 in `ZeroTimeSliceYieldsAndUnlimitedSliceRendersInOneStep`) hängen davon ab, wie oft PDFium den Pause-Callback abfragt. Schlagen sie nach einem Update fehl, die Ursache prüfen und die Testseite oder Erwartung bewusst anpassen, nicht die Prüfung entfernen.
  - Beim nächsten PDFium-Update einen Test ergänzen, der prüft, dass `bblanchon.PDFium.Linux` und `bblanchon.PDFium.Win32` in `Directory.Packages.props` dieselbe Version haben. `NativeBindingsTests.ConstantsMatchThePackageHeaders` liest nur die Header des Win32-Pakets; eine halb gemergte Dependabot-Gruppe fiele sonst nicht auf.
- Avalonia-Updates: `Pw.WindowDecorations` (`src/Pagewing.App/Views/MainWindow.axaml`) blendet die Fluent-Template-Teile `PART_TitleTextPanel > TextBlock` und `PART_FullScreenButton` von `WindowDrawnDecorations` über ihre Namen aus; ein Update kann sie umbenennen. `MainWindowTests.WindowDecorationsHideTitleTextAndFullScreenButtonAndFitTheReservedSpace` schlägt dann fehl (er schaltet die Dekorationen über die interne Methode `TopLevelHost.UpdateDrawnDecorations` per Reflection ein, die ein Update ebenfalls ändern kann).
- Drittlizenzen gehören ins Paket und in den Über-Dialog. PDFium bringt allein rund 20 mit (FreeType, ICU, libjpeg-turbo, OpenJPEG …).

### Offline und Sicherheit

- Keine Netzwerkzugriffe durch Pagewing selbst: keine Telemetrie, keine Update-Prüfung, keine Online-Dienste.
- JavaScript in PDFs läuft nie (PDFium ohne V8 und ohne XFA). Launch-Aktionen und eingebettete Dateien werden nie automatisch ausgeführt oder geöffnet.
- Externe Links öffnen erst nach Klick und einer Bestätigung, die die URL anzeigt.

### Speichern

- Normalfall ist inkrementelles Speichern: schnell, und vorhandene digitale Signaturen bleiben gültig.
- Immer atomar: in eine temporäre Datei im Zielordner schreiben, sie mit PDFium wieder öffnen und prüfen, erst dann ersetzen. Das Original darf nie halb überschrieben werden.
- Schwärzen und Seitenoperationen erzwingen einen vollständigen Neuschrieb. Ist das Dokument digital signiert, warnt die App vorher, dass die Signatur ungültig wird.

### Fachliche Regeln

- **Schwärzen entfernt den Inhalt wirklich.** Ein schwarzes Rechteck darüber reicht nicht.
  - Textobjekte im Bereich werden aufgeteilt bzw. entfernt.
  - Bildpixel werden überschrieben und das Bild neu kodiert.
  - Vektorpfade und Form-XObjects werden rekursiv behandelt.
  - Annotationen im Bereich werden gelöscht, Metadaten (Info-Dictionary, XMP) auf Wunsch entfernt.
  - Danach folgt immer ein vollständiger Neuschrieb, nie inkrementell. Sonst bleibt die alte Revision samt Text in der Datei.
  - Pflicht-Tests nach dem Schwärzen: Die Textextraktion findet nichts mehr, **und** auch die dekomprimierten Rohbytes der Datei enthalten den Text nicht. Fälle: Form-XObject, Annotation, Metadaten, ältere inkrementelle Revision, OCR-Textebene.
- **Text bearbeiten ist bewusst begrenzt.** Vorhandene Zeilen und Tippfehler lassen sich korrigieren, für fehlende Zeichen springt eine Ersatzschrift ein. Nie Absatz-Bearbeitung wie in Word versprechen, weder in UI noch Doku. Der Grund: PDFs speichern Text als positionierte Zeichen ohne Absatzstruktur, Schriften sind oft nur teilweise eingebettet.
- **Sichtbare Unterschrift** nie als „rechtsgültig“ bezeichnen. Der Dialog zeigt: „Ersetzt keine handschriftliche Unterschrift, wo Schriftform verlangt ist.“ (in der englischen UI sinngemäß übersetzt).
- **Formulare:** Ohne V8 laufen keine JavaScript-Berechnungen. Berechnete Felder aktualisieren sich nicht, die App weist darauf hin.
- **Digitale Signaturen mit Zertifikat, XFA, JavaScript, PDF→Word, Cloud/Konten/KI, Preflight, 3D/Multimedia, macOS/Mobil** sind Out of Scope (Roadmap). Nicht nebenbei anfangen.

### Tests

- **Render-Regression** (`tests/Pagewing.Render.Tests`): Die Test-PDFs aus `testdata/pdfs` werden pro Seite zu PNG gerendert und mit Referenzbildern verglichen, mit Toleranz. Läuft in der CI auf beiden OS und muss jedes PDFium-Update bestehen.
- Referenzbilder werden nur bewusst aktualisiert, mit Sichtprüfung der Abweichungen und Begründung im PR. Nie pauschal, nur damit ein Update grün wird.
  - Aktualisieren nur mit `PAGEWING_UPDATE_REFERENCES=1 dotnet test tests/Pagewing.Render.Tests`; der Lauf schlägt absichtlich immer fehl, damit eine Aktualisierung nie unbemerkt bleibt. Referenzen entstehen unter Windows, es gibt einen gemeinsamen Satz für beide OS.
  - Der Vergleich hat zwei Modi je Fall in `RenderCorpus`: `Strict` (pixelweise, Seiten ohne Text) und `Tolerant` (8×8-Blockmittel, Seiten mit Text), weil PDFium die Ersatzschriften je OS unterschiedlich wählt. Grenzwerte und Begründung: `Docs/DECISIONS.md`, Eintrag „Render-Regression: zwei Vergleichsmodi …“.
- Die Test-PDFs erzeugt `tools/make-testpdfs.cs` (`generate` schreibt, `check` prüft in der CI die Reproduzierbarkeit). Sie sind deterministisch und unkomprimiert; PDFs aus PDFium `testing/resources` bleiben unverändert.
- Vergleichsmessung der Viewer: Protokoll `bench/compare/README.md`, Werkzeug `tools/measure-open.cs` (nur Windows, angemeldete Desktop-Sitzung; Ergebnisse als CSV nach `bench/compare/results/`).
- Test-PDFs nur mit klarer Lizenz, Herkunft und Lizenz pro Datei in `testdata/pdfs/LICENSES.md`.
  - Bevorzugt: selbst erzeugte PDFs, PDFium `testing/resources` (BSD-3), veraPDF-Korpus (CC BY 4.0), pdf20examples (CC BY-SA 4.0).
  - Den pdf.js-Korpus nicht einchecken (Einzellizenzen unklar).
  - Nie private oder vertrauliche PDFs.
- UI-Tests mit Avalonia.Headless, Benchmarks mit BenchmarkDotNet in `bench/`.

### CI

- Self-hosted Runner der Orga: `dotnet-linux` und `dotnet-windows`. Build, Test, Format und AOT-Publish laufen auf beiden.
- Messung auf echter Hardware: Workflow `hardware-measure.yml` („Hardware measurement“), Runner mit dem einzigen eigenen Label `dotnet-windows-gpu` (Name `hauptpc`, Runner-Gruppe `main-pc`) auf dem Haupt-PC des Projektinhabers. Er startet nur manuell (`workflow_dispatch` oder Label `hardware-measure` am PR), nie bei `push` oder `pull_request` ohne Label. Der Projektinhaber startet den Runner von Hand (`run.cmd` in seiner angemeldeten Sitzung), wenn Claude darum bittet, etwa vor einem Release; er ist kein Dienst, nie darauf verlassen, dass er online ist. Einrichtung und Ablauf: `bench/startup/README.md`, Abschnitt „Messung auf echter Hardware (Haupt-PC)“; Begründung: `Docs/DECISIONS.md`, Eintrag „Messung auf echter Hardware: manueller Runner des Haupt-PCs (M3)“.
- Das Repo ist derzeit privat. Wird es öffentlich, vorher in den Actions-Einstellungen einstellen, dass PRs aus Forks vor dem Lauf freigegeben werden müssen. Sonst führen fremde PRs Code auf den eigenen Runnern aus.
- Der Linux-Build ist an die glibc des Build-Rechners gebunden. Die älteste unterstützte Distribution wird in M8 festgelegt.
- Voraussetzungen der Linux-Runner: `clang` und `zlib1g-dev` (`sudo apt-get install -y clang zlib1g-dev`). Der CI-Schritt „Check the Native AOT prerequisites (Linux)“ meldet, was fehlt.
  - Skia (`libSkiaSharp.so`) braucht `libfontconfig1`, also schon `dotnet test` mit den Headless-UI-Tests in `tests/Pagewing.App.Tests`, nicht erst der Startzeit-Check. Für den Startzeit-Check zusätzlich Xvfb und die X11-Bibliotheken, die Avalonia laut seiner Docker-Anleitung braucht: `sudo apt-get install -y xvfb libx11-6 libice6 libsm6 libfontconfig1`. Der Schritt prüft `xvfb-run` und `libfontconfig.so.1` (über `ldconfig -p`); fehlende X11-Bibliotheken zeigen sich erst als Fehler des Startzeit-Checks.
- Voraussetzungen des Windows-Runners (`vm-git-windows-dotnet`, Label `dotnet-windows`):
  - Ausführungsrichtlinie `RemoteSigned` für LocalMachine (`Set-ExecutionPolicy -Scope LocalMachine -ExecutionPolicy RemoteSigned -Force` in einer Windows PowerShell mit Administratorrechten). Sonst scheitert jeder `run:`-Schritt mit „Ausführung von Skripts ... deaktiviert“.
  - Visual Studio Build Tools mit Workload „Desktopentwicklung mit C++“. Prüfen: `vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath` gibt einen Pfad aus.
- Auf dem Windows-Runner ist git nicht im PATH; `actions/checkout` lädt dort ein Zip, dessen Dateizeiten vor dem Build eines Einzeldatei-Skripts liegen können, den ein früherer Job in `%TEMP%` zwischengespeichert hat. Der inkrementelle Build hält diese Kopie dann für aktuell. Ohne den Schritt „Refresh the file times of the checkout (Windows)“ liefen die Einzeldatei-Skripte (`dotnet run <datei>.cs`) deshalb veraltet; `--no-cache` hilft dagegen nicht.
- Der Windows-Runner läuft nicht als Dienst, sondern in einer angemeldeten Desktop-Sitzung: automatische Anmeldung des Benutzers `git-windows-dotnet`, ein geplanter Task mit Auslöser „Bei Anmeldung“ startet `run.cmd` im Runner-Ordner (Neustart bei Fehler, kein Zeitlimit). Bildschirm, Standby und Sperrbildschirm sind abgeschaltet. Grund: Der Startzeit-Check startet die echte App mit Fenster, und ein Dienst hat keinen Desktop. Ob der Startzeit-Check auf diesem Runner gültige Werte liefert, hängt außerdem von der Bildrate der virtuellen Grafikkarte der VM ab; das Skript `bench/startup/startup.cs` warnt, wenn die Desktop-Komposition unter 25 Hz liegt. Bis ein CI-Lauf mit gültigen Werten vorliegt, bleibt die Abnahme „Startzeit-Check grün auf beiden Runnern“ offen.

### Build-Stolperfallen

- Die Root-Dateien `Directory.Packages.props` (zentrale Paketverwaltung) und `Directory.Build.props` gelten auch für Einzeldatei-Werkzeuge. `tools/Directory.Build.props` und `tools/Directory.Packages.props` isolieren sie; ohne sie bricht `tools/roadmap.cs` mit NU1008.
- xunit.v3 läuft auf Microsoft.Testing.Platform: `global.json` enthält den Runner. `Microsoft.NET.Test.Sdk` und `xunit.runner.visualstudio` nicht referenzieren.
- Testnamen ohne Unterstriche (CA1707 unter `latest-recommended`).
- Die native PDFium-Bibliothek liegt neben dem AOT-Binary (`pdfium.dll` bzw. `libpdfium.so`). `tools/smoke-cli.cs` prüft das, indem es `info`, `render` und `text` auf `testdata/pdfs/text-basic.pdf` ausführt; es läuft aus dem Repo-Root, z. B. `dotnet run tools/smoke-cli.cs -- <pagewing.exe>`.
- Die App ist eine `WinExe`: Unter Windows kommt ihre Standardausgabe (z. B. `first-sharp` der Messschalter) nur an, wenn der Elternprozess sie umleitet (`RedirectStandardOutput`), wie in `bench/startup/startup.cs`. `bench/startup/Directory.Build.props` und `Directory.Packages.props` isolieren das Skript wie `tools/`.
- CLI-Exit-Codes: `0` Erfolg, `1` Fehler (Meldung `pagewing: …` auf stderr), `2` falscher Aufruf. Das Passwort verschlüsselter PDFs kommt aus `PAGEWING_PASSWORD` oder `--password`; Ausgaben sind englisch und stabil (Skriptnutzung).
- Native AOT unter Windows braucht `vswhere.exe` im PATH (Ordner `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer`) und die VS-Build-Tools mit C++-Workload; unter Linux `clang` und `zlib1g-dev`.

---

<!-- Ab hier: allgemeine Regeln aus benjo-code/project-template. Projektübergreifend gleich halten; Verbesserungen im Template pflegen und in die Projekte nachziehen. -->

## Roadmap pflegen

Die Quelle der Wahrheit ist `Docs/roadmap.json` (Schema: `Docs/roadmap.schema.json`). `Docs/ROADMAP.md` wird daraus erzeugt und nie von Hand geändert. Voraussetzung ist das .NET-10-SDK, auch wenn das Projekt sonst einen anderen Stack nutzt.

1. Nur `Docs/roadmap.json` bearbeiten: Status, Plan-Link, Aufgaben und Kriterien abhaken (`"done": true`, Nachweis in `"evidence"`), Scope-Änderungen, Backlog. Optionale Felder weglassen, nie auf `null` setzen.
2. Danach `dotnet run tools/roadmap.cs -- generate` ausführen und beide Dateien committen. Fehler nennen den Pfad im JSON (zum Beispiel `subprojects[0].milestones[2].completedOn`).
3. `dotnet run tools/roadmap.cs -- check` prüft Schema, Verweise und ob `ROADMAP.md` aktuell ist; die CI (Job `roadmap`) tut dasselbe.
4. Regeln: `status` ist `open`, `planned`, `in-progress` oder `done`; `done` verlangt `completedOn` (JJJJ-MM-TT), ab `planned` ist `plan` Pflicht (Pfad relativ zu `Docs/`); Milestone-Ids sind eindeutige Strings (`"5.1"` ist erlaubt); Voraussetzungen und `afterMilestone` müssen existieren; Titel, Ziel und Name stehen in einer Zeile; Texte sind Markdown; was nirgends passt, kommt als Eintrag in `blocks` (`title`, `text`).
5. Ein neuer Milestone ist ein Eintrag in `subprojects[].milestones`:

```json
{
  "id": "1", "title": "<Titel>", "status": "open",
  "prerequisites": ["0"],
  "goal": "<Ein Satz: Was ist nach diesem Milestone möglich?>",
  "tasks": [ { "text": "…", "done": false } ],
  "acceptance": [ { "text": "… (prüfbar: Test, Befehl oder konkrete manuelle Prüfung)", "done": false } ]
}
```

   Ein neues Teilprojekt ist ein Eintrag in `subprojects` (`number`, `name`, `spec` mit `text` und/oder `link`, `milestones`), ein Release ein Eintrag in `releases` (`version`, `title`, `status`, `afterMilestone`, optional `covers`, `date`, `intro`, `acceptance`, `blocks`).
6. Ideen gehören in `backlog` (`title`, `text`, optional `idea`). Der Skill `/ideen` (aus `benjo-code/Labjo`) trägt übernommene Ideen dort ein und erzeugt `Docs/ROADMAP.md` neu.

## Milestone-Workflow

Jede Änderung „in der Roadmap“ (Plan verlinken, Status setzen, Aufgaben abhaken, Scope-Änderungen) ist eine Änderung an `Docs/roadmap.json` samt neu erzeugter `Docs/ROADMAP.md`, siehe „Roadmap pflegen“.

**„Plane Milestone N“:**
1. Branch `feature/mNN-<thema>` vom aktuellen `dev` anlegen. Plan, Roadmap-Pflege und Umsetzung des Milestones laufen alle über diesen Branch.
2. `Docs/ROADMAP.md`, die Spec des Teilprojekts und den aktuellen Code-Stand lesen.
3. Offene Ideen des Projekts mit `/ideen` sichten (Skill aus `benjo-code/Labjo`, Installation: `bash tools/install-ideen-skill.sh` dort): übernehmen, umsetzen oder verwerfen. Was in den Milestone gehört, fließt in den Plan ein. Die Roadmap-Änderung liegt auf dem Milestone-Branch (beim Nachfragen „aktueller Branch“ wählen).
4. Beginnt der Milestone mit „Spec erstellen“ oder existiert für das Teilprojekt noch keine Spec: zuerst die Spec per Brainstorming mit dem Nutzer erarbeiten und unter `Docs/superpowers/specs/` ablegen.
5. Plan nach `Docs/superpowers/plans/YYYY-MM-DD-milestone-NN-<thema>.md` schreiben. Der Plan deckt genau die Aufgaben und Abnahmekriterien des Milestones ab, nicht mehr.
6. In der Roadmap den Plan verlinken und den Status auf `🟡 geplant` setzen (Übersichtstabelle und Milestone).

**Umsetzung:**
- Status `🔵 in Arbeit`. Jede erledigte Aufgabe sofort in der Roadmap abhaken.
- Abnahmekriterien erst abhaken, wenn sie tatsächlich geprüft wurden, und den Nachweis dahinter notieren (Test-Name, Befehl + Ergebnis oder manuelle Prüfung).
- Am Ende Status `✅ fertig` mit Datum in Tabelle und Milestone, dann PR nach `dev` (siehe „Git & Branches“).
- Scope-Änderungen in der Roadmap unter „Scope-Änderungen“ festhalten, nicht stillschweigend im Code.
- Nur im Umfang des aktiven Milestones arbeiten. Keine Funktionen, Pakete oder Architekturentscheidungen späterer Milestones vorwegnehmen.

## Git & Branches

- `main` enthält nur veröffentlichte Stände (Releases). `dev` ist der Integrationszweig.
- Arbeit immer auf `feature/<thema>` oder `fix/<thema>`, abgezweigt vom aktuellen `dev` (`git switch dev && git pull && git switch -c feature/<thema>`).
- Vor jeder Arbeit `git status` und den aktuellen Branch prüfen. Steht man auf `main` oder `dev`, zuerst einen Feature-Branch anlegen. Nie direkt auf `main` oder `dev` committen.
- Feature- und Fix-Branches per PR nach `dev`. Claude merged diese PRs selbst, sobald die Arbeit fertig ist und alle CI-Checks grün sind (Squash-Merge erlaubt).
- PRs nach `main` gibt es nur für Releases und Hotfixes und nur auf ausdrückliche Anweisung des Nutzers.
- PRs zwischen `main` und `dev` (Release, Hotfix, Rückführung `main` → `dev`) immer mit **„Create a merge commit“** mergen, nie per Squash oder Rebase. Sonst verlieren `main` und `dev` ihre gemeinsame History, und jedes Release erzeugt Konflikte.
- Hotfix: `hotfix/<thema>` von `main` abzweigen → PR nach `main` → Release (siehe unten) → danach `main` per PR nach `dev` mergen.
- Gezielt stagen (`git add <datei>`). Kein `git add .` oder `git add -A`, wenn fremde, nicht zur Aufgabe gehörende Änderungen im Working Tree liegen. Bei Dateien, die schon vorher geändert waren, erst `git diff <datei>` lesen und ggf. `git add -p` nutzen.
- Nach jedem Commit `git show --stat HEAD` gegen die erwartete Dateiliste prüfen.
- Commit-Messages nach Conventional Commits: `feat:`, `fix:`, `docs:`, `refactor:`, `test:`, `chore:`, `ci:`, optional mit Scope (`feat(api): …`).
- Ohne ausdrückliche Anweisung verboten: Force-Push, `git reset --hard`, Branches löschen, History umschreiben, Hooks mit `--no-verify` umgehen.
- Neue Repos (auch begleitende wie `-Releases` oder `-Privacy`) immer in der Orga `benjo-code` anlegen, immer mit ausgeschriebenem Owner (`gh repo create benjo-code/<name> …`), nie unter dem persönlichen Account. Danach mit `gh repo view benjo-code/<name>` prüfen.

## Releases

- Versionierung nach SemVer (`vMAJOR.MINOR.PATCH`).
- `CHANGELOG.md` → `[Unreleased]` wird in jedem PR mit nennenswerter Änderung für Nutzer ergänzt (nicht bei reinen Planungs-, Roadmap- oder internen Aufräum-PRs).
- Ablauf (nur auf Anweisung):
  1. `release/vX.Y.Z` von `dev` abzweigen, in `CHANGELOG.md` `[Unreleased]` zu `[X.Y.Z] – YYYY-MM-DD` machen (enthaltene Milestones nennen) und ein neues, leeres `[Unreleased]` darüber anlegen.
  2. PR `release/vX.Y.Z` → `main` (Merge-Commit).
  3. Tag und GitHub-Release in einem Schritt: `gh release create vX.Y.Z --target main --title "vX.Y.Z" --notes "<CHANGELOG-Abschnitt>"`.
  4. `main` per PR nach `dev` zurückführen (Merge-Commit), damit beide nicht auseinanderlaufen.

## Projektgedächtnis

- Zukunftswichtiges ohne Aufforderung im Repo dokumentieren:
  - Architektur- und Technikentscheidungen, festgelegte Versionen, verworfene Ansätze → `Docs/DECISIONS.md`
  - Scope-Änderungen → `Docs/ROADMAP.md`, Abschnitt „Scope-Änderungen“
  - Dauerhaftes Projektwissen (Architektur, Konventionen, Workarounds, Stolperfallen) → diese Datei, Abschnitt „Projektspezifisches“
- Keine Vermutungen, Zwischenstände oder temporären Diagnosen als Fakten festhalten.
- Verweise nennen Datei und Typ-, Member- oder Abschnittsnamen, nie Zeilennummern (die veralten).

## Qualität & Nachweis

- „Fertig“ heißt: Build, Test und Format/Lint (Befehle siehe oben) sind nachweislich grün. Befehl und Ergebnis nennen.
- `dotnet run tools/roadmap.cs -- check` gehört zu „fertig“: die Roadmap ist gültig und `Docs/ROADMAP.md` ist aktuell.
- Was nicht geprüft werden konnte (z. B. weil echte Hardware oder Zugangsdaten fehlen), explizit als offen markieren, zusammen mit dem Befehl, der es prüft.
- Keine Warnungen unterdrücken, Tests überspringen oder Checks abschwächen, nur damit etwas grün wird.

## Sicherheit

- Keine Secrets (Tokens, Passwörter, Keys, echte Zugangsdaten) in Dateien, Commits, Logs, PRs oder Antworten.
- Werte aus `.env` o. ä. nie zitieren oder ausgeben, nur Variablennamen oder Platzhalter nennen.
- Secrets kommen aus Env-Vars oder einem Secret-Store. Für jede benötigte Variable einen Platzhalter in `.env.example` pflegen. `.env` steht in `.gitignore`.

## Zusammenarbeit

- Bei unklaren Anforderungen nachfragen statt raten.
- Destruktive oder nach außen wirkende Aktionen (Merge nach `main`, Release, Löschen von Branches, Tags, Releases oder Repos, Änderungen an GitHub-Einstellungen) nur auf ausdrückliche Anweisung. Automatisch gelöschte Feature-Branches nach dem Merge sind davon ausgenommen.
