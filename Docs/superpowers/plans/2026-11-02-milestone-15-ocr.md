# Milestone 15: OCR – Implementierungsplan

> **Für ausführende Agenten:** PFLICHT-SKILL: `superpowers:subagent-driven-development` (empfohlen) oder `superpowers:executing-plans`, Aufgabe für Aufgabe. Schritte sind Checkboxen (`- [ ]`).

**Ziel:** Gescannte PDFs werden offline durchsuchbar: Pagewing erkennt Text mit Tesseract und legt ihn als unsichtbare Textebene hinter das unveränderte Seitenbild; in der App (Hinweis, Karte, Fortschritt, Abbruch, Bericht) und als `pagewing ocr`.

**Architektur:** `Pagewing.Ocr` (neu) kapselt nur die Tesseract-C-API (erst bei Nutzung geladen). `Pagewing.Core.Ocr` (neu, UI- und PDFium-frei) entscheidet je Seite, rechnet Auflösung und Koordinaten und führt den Lauf (Rendern auf dem `PdfiumThread` in kurzen Aufträgen, Erkennen auf Worker-Threads). Die Textebene schreibt PDFium beim Übernehmen ins geöffnete Dokument; der Lauf ist ein Schritt im gemeinsamen Verlauf (`AnnotationCommand`), gespeichert wird inkrementell wie in M9. Die Bibliothek baut ein eigener Workflow, die Ergebnisse liegen als Vorabversion-Release und werden per Prüfsumme geholt.

**Tech Stack:** .NET 10 Native AOT, Avalonia 12.1, PDFium über eigene Bindings, Tesseract 5.5.x + Leptonica (selbst gebaut), `tessdata_fast` (`eng`, `deu`), xunit.v3, Avalonia.Headless.

**Spec:** [2026-11-02-teilprojekt4-ocr-design.md](../specs/2026-11-02-teilprojekt4-ocr-design.md) (maßgeblich; „Spec 3.2“ unten sind ihre Abschnitte). Rahmen: `CLAUDE.md` (Blöcke „Speichern und Annotationen“, „Schwärzen“, „Seiten organisieren“).

## Globale Randbedingungen

- PDFium nur über den `PdfiumThread` in kurzen Aufträgen (`Background`, Abbruch zwischen Schritten); der UI-Thread und der `PdfiumThread` warten nie auf Tesseract. **Kein neuer `[UnmanagedCallersOnly]`-Rückruf.** `CULong` für `unsigned long`. `AppContext` ist initialisiert, bevor ein Worker-Thread startet.
- Nichts im Startpfad: Tesseract wird nicht geladen, Karte, Hinweis, Runner und Bibliothek entstehen erst beim Gebrauch; Messläufe öffnen die Funktion nie (`OcrStartPathTests`). Startzeit-Budgets nie lockern.
- Kein Netz: Tesseract wird ohne `libcurl`/`libarchive`/Bilddecoder gebaut; kein Umgebungs- oder Konfigurationseinfluss auf `datapath`, `debug_file`, Dumps; Seitenbilder liegen nur im Arbeitsspeicher. Bibliothek nur aus dem Ordner neben der Anwendung (nie PATH oder Arbeitsverzeichnis).
- Das Seitenbild wird nie verändert oder neu kodiert. Kein UI-Text und kein Bericht sagt „fehlerfrei“, „korrekt erkannt“, „vollständig“, „rechtsgültig“ oder eine Signatur „gültig“. Jede übersprungene Seite steht mit Grund im Bericht, nie still.
- Werte aus der Spec: Seite „hat Text“ ab **50 Zeichen ohne Leerraum**; Auflösung automatisch aus dem Bild (**150 bis 300 dpi**, ohne Bild 300), Wahl in der Karte „Automatisch“, **150, 200, 300, 400**; Pixelobergrenze **50 Megapixel** (senkt die Auflösung, nie still), Mindestauflösung **150 dpi** (darunter übersprungen); Zeitgrenze je Seite **300 s**; Worker **min(Kerne − 1, 4)**, mindestens 1; Sprachdatei höchstens **200 MB**, Name aus `[a-z_]+`; Standardsprachen Oberflächensprache + Englisch, höchstens zwei, mehr als drei warnen; Verlauf-Obergrenze **32 MiB** (`AnnotationHistoryFullException` vor der Änderung).
- UI-Texte Englisch **und** Deutsch (`Strings.en.resx`, `Strings.de.resx`, `Resources/Strings.cs`); Kürzel nur im `CommandCatalog`; Zustände nie nur über Farbe; WCAG 2.2 AA; keine Farb- und Maßliterale im XAML.
- Code und Kommentare Englisch; Doku und Commits Deutsch (Conventional Commits, gezielt stagen, nie `git add .`, danach `git show --stat HEAD`), Trailer `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`, nur auf `feature/m15-ocr`. Testnamen ohne Unterstriche. Tests schreiben nie in den Benutzerordner und nie neben Eingaben in `testdata` (Kopien im Temp-Ordner); Tests nutzen `UserSettings.Detached()`.
- Befehle: `dotnet build Pagewing.slnx`, `dotnet test Pagewing.slnx`, `dotnet format Pagewing.slnx --verify-no-changes`, `dotnet run tools/roadmap.cs -- check`, `dotnet run tools/licenses.cs -- check`, `dotnet run tools/make-testpdfs.cs -- check`; einzeln `dotnet test tests/Pagewing.Core.Tests --filter-class "*Name"`.
- OCR-Tests, die die native Bibliothek brauchen, melden sich lokal ohne sie als übersprungen; mit `PAGEWING_REQUIRE_OCR=1` (CI) ist Fehlen ein Fehler. Tesseract-Version fest; geprüft werden Wörter und Lage mit Toleranz, nie Konfidenz auf Dezimalstellen.

## Festgelegte Namen

| Ort | Name und Gestalt |
|---|---|
| `Pagewing.Ocr` | `TesseractLibrary.IsAvailable`; `OcrEngine : IDisposable` mit `static OcrEngine Create(string tessdataDirectory, string languages)` und `OcrRecognition Recognize(ReadOnlySpan<byte> gray, int width, int height, int dpi, CancellationToken ct)`; `readonly record struct OcrWord(string Text, int Left, int Top, int Right, int Bottom, float Confidence)` (Pixel, Ursprung oben links); `OcrRecognition(IReadOnlyList<OcrWord> Words)`; `static IReadOnlyList<string> OcrEngine.InstalledLanguages(string tessdataDirectory)`; `OcrEngineException` |
| `Pagewing.Core.Ocr` | `enum OcrMode { Skip, Redo, Force }`; `sealed record OcrOptions(string Languages, OcrMode Mode = Skip, int Dpi = 0 /*0 = automatisch*/, IReadOnlySet<int>? Pages = null, int Jobs = 0 /*0 = Standard*/)`; `enum OcrSkipReason { HasText, AlreadyRecognized, TooLarge, ResolutionTooLow, Timeout, Failed, NoSupportedCharacters }`; `OcrResolution.Resolve(double widthPt, double heightPt, double? imageDpi, int requestedDpi)` → `OcrRaster(int Dpi, int WidthPx, int HeightPx, bool Lowered, bool TooLow)`; `OcrPlanner.Decide(PageOcrInfo info, OcrMode mode)` → `OcrDecision(bool Run, OcrSkipReason? Reason, bool LittleText)`; `sealed record OcrPageWord(string Text, double Left, double Top, double Right, double Bottom, float Confidence)` (Seitenpunkte, oben links); `OcrWordMapper.ToPagePoints(OcrWord, OcrRaster, double widthPt, double heightPt)`; `OcrPageResult(int Page, IReadOnlyList<OcrPageWord> Words, int Dpi, bool Lowered, double MeanConfidence)`; `OcrReport` (Seiten erkannt, Wörter, mittlere Konfidenz, `IReadOnlyList<OcrSkippedPage>`, `LowConfidencePages`, `LittleTextPages`); `IOcrEngineFactory.Create(string languages)` → `IOcrEngine` (`Recognize(...)`, Dispose); `OcrRunner.RunAsync(PdfDocument, OcrOptions, IOcrEngineFactory, IProgress<OcrProgress>?, CancellationToken)` → `OcrRunResult(IReadOnlyList<OcrPageResult> Pages, OcrReport Report)`; `TessdataStore` (Import, Liste) |
| `PdfDocument` | `GetOcrPageInfoAsync(int page, CancellationToken)` → `PageOcrInfo(int TextCharacters /*ohne eigene Ebene, ohne Leerraum*/, bool HasOwnLayer, double? ImageDpi, double ImageCoverage)`; `RenderPageForOcrAsync(int page, int dpi, CancellationToken)` → `RenderedImage` (Annotationen aus, hell, Priorität `Background`); `AddOcrLayerAsync(int page, IReadOnlyList<OcrPageWord> words, CancellationToken)`; `RemoveOcrLayerAsync(int page, CancellationToken)`; `ReadOcrLayerAsync(int page, CancellationToken)` → `IReadOnlyList<OcrPageWord>` |
| Verlauf | `AnnotationCommand.OcrLayer(IReadOnlyDictionary<int, IReadOnlyList<OcrPageWord>> Before, IReadOnlyDictionary<int, IReadOnlyList<OcrPageWord>> After)`; `AnnotationEditor.ApplyOcrAsync(OcrRunResult result, bool replace)` |
| Einstellung/Pfade | `AppSettings.OcrLanguages` (`string?`, normalisiert); `UserDataPaths.TessdataDirectory()` (`tessdata` im Benutzerordner) |
| CLI | `Commands/OcrCommand.RunAsync(document, options, password, output, error, ct, progress)`; Optionen `--language`, `--tessdata`, `--mode skip|redo|force`, `--dpi`, `--jobs`, `--dry-run` |

## Review-Fokus

Eingaben, die die Spec nicht ausspricht, wahrscheinlichste zuerst; jede Zeile hat ihren Test in der genannten Aufgabe:

1. **Gedrehte Seite (`/Rotate 90/270`) mit CropBox-Versatz:** Treffer und Auswahl liegen auf dem Wort im Bild, die Seite rendert danach pixelgleich (Aufgabe 6, 11).
2. **Seite ohne erkennbaren Text (leer/weiß):** Lauf endet ohne Fehler, keine leeren Textobjekte, Bericht „keine Wörter“; Seite mit 49 bzw. 50 Zeichen Text an der Schwelle (Aufgabe 5, 7).
3. **Bibliothek oder Sprachdatei fehlt, beschädigt oder zu groß:** klare Meldung (App-Karte, CLI-Exit 1), nie Absturz, nie Netzzugriff; unbekannte Sprache nennt die gefundenen (Aufgabe 3, 9, 10).
4. **Abbruch, Tab-/Fensterschluss, Update oder geänderte Datei mitten im Lauf:** nichts Halbes im Dokument, Original byteweise gleich, kein hängender Worker, kein geladener Speicher (Aufgabe 7, 8, 10).
5. **Zeichen außerhalb des Schriftvorrats (z. B. kyrillisch bei `rus`) und 100.000 winzige Wörter:** Wort mit Grund gezählt statt ersetzt, Grenzen greifen mit Meldung (Aufgabe 6, 7, 11).

---

## Dateistruktur

| Datei | Verantwortung |
|---|---|
| `src/Pagewing.Ocr/` (neu: `Pagewing.Ocr.csproj`, `TesseractLibrary.cs`, `TesseractNative.cs`, `OcrEngine.cs`, `OcrWord.cs`) | nur Tesseract-C-API, kennt weder PDFium noch Core |
| `src/Pagewing.Core/Ocr/` (neu) | Entscheidung, Auflösung, Koordinaten, Lauf, Bericht, Sprachdateien; UI- und PDFium-frei |
| `src/Pagewing.Core/Pdf/PdfDocument.Ocr.cs`, `src/Pagewing.Pdfium/PdfiumOcr*.cs` (Spiegeltypen) | Zugang zu PDFium: Infos, Rendern, Textebene |
| `src/Pagewing.Core/Annotations/AnnotationHistory.cs`, `AnnotationEditor.cs` (ändern) | Verlaufsschritt `OcrLayer` |
| `src/Pagewing.Cli/Commands/OcrCommand.cs`, `CliOptions.cs`, `CliApp.cs`, `README.md` | CLI |
| `src/Pagewing.App/` (`ViewModels/TabViewModel.Ocr.cs`, `MainViewModel.Ocr.cs`, `OcrViewModel.cs`, `Views/OcrHintBar*`, `OcrCard*`, `OcrReportCard*`, `Commands/CommandCatalog.cs`, `Resources/*`) | Oberfläche |
| `tools/package.cs`, `.github/workflows/native-tesseract.yml`, `third-party/`, `packaging/` | Bau, Holen, Paket, Lizenzen |
| `tests/Pagewing.Core.Tests/Ocr/`, `…/Cli/OcrCommandTests.cs`, `tests/Pagewing.App.Tests/Ocr*Tests.cs`, `tools/make-testpdfs.cs` | Tests und Fixtures |

---

## Aufgabe 1: Spike Textebene (Spec 3.4)

Klärt Schrift, Unicode-Zuordnung, Darstellungsmodus, Marke und Reihenfolge, **bevor** Aufgabe 6 darauf baut. Läuft ohne Tesseract mit festen Wortlisten.

**Dateien:** Neu `tests/Pagewing.Core.Tests/Ocr/OcrLayerSpikeTests.cs` (nur mit `PAGEWING_SPIKE_OUT=<Ordner>`, sonst übersprungen; wie `PageEditSpikeTests`); Eintrag in `Docs/DECISIONS.md`.

**Schnittstellen:** Produziert die Entscheidung zu: (a) PDFium-Aufrufe für unsichtbaren Text (Textobjekt, Darstellungsmodus 3, Schrift laden, Matrix), (b) Schrift: Geist als Unicode-Schrift mit Zuordnung vs. glyphlose Schrift, (c) Marke der eigenen Ebene, (d) Ebene vor oder hinter dem Seitenbild, (e) ob Aufrufe experimentell sind (Header prüfen, Wächter in `NativeBindingsTests`).

- [ ] **Schritt 1:** Tests schreiben, die je Variante eine Datei erzeugen und prüfen: `TextLayerIsExtractedWithUmlautsAndEszett` (Wörter „Müller Straße Größe“ werden von PDFium extrahiert, gleiche Zeichen), `TextLayerDoesNotChangeTheRenderedPage` (Render vorher/nachher pixelgleich, Strict), `SelectionFollowsTheWordBox` (Zeichenrahmen der Wörter liegen in der Wortbox, Toleranz ½ Zeichenhöhe), `RotatedPageAndCropBoxKeepTheTextUpright`, `LayerSurvivesIncrementalSaveAndReopen` (Marke da), `LayerSurvivesPageImport` (M13, `FPDF_ImportPagesByIndex`), `RedactingOneWordKeepsTheMarkOfTheOthers` (M14-Neuschrieb), `FileGrowsByFewKilobytesPerPage` (Schrift nicht je Seite eingebettet), `CharactersOutsideTheFontAreCounted`.
- [ ] **Schritt 2:** Ausführen, Varianten bauen, bis die Tests entscheiden. `PAGEWING_SPIKE_OUT=<Ordner> dotnet test tests/Pagewing.Core.Tests --filter-class "*OcrLayerSpikeTests"`.
- [ ] **Schritt 3:** Die zwei Beispieldateien (`ocr-layer-plain.pdf`, `ocr-layer-rotated.pdf`) dem Projektinhaber zur Prüfung in Chrome, Acrobat Reader und Pagewing geben (Auswahl, Suche, Kopieren); Ablauf als Punkt in `packaging/README.md`. Aufgaben 2 bis 5 hängen nicht davon ab und laufen währenddessen weiter; Aufgabe 6 beginnt erst mit der Rückmeldung.
- [ ] **Schritt 4:** Entscheidung (Schrift, Darstellungsmodus, Marke, Reihenfolge, Ausfälle der Marke nach M13/M14) in `Docs/DECISIONS.md`; Commit `docs: Spike Textebene (M15)`.

## Aufgabe 2: Tesseract bauen, ausliefern, Lizenzen (Spec 5.1)

**Dateien:** Neu `.github/workflows/native-tesseract.yml` (nur `workflow_dispatch`), `packaging/native/` (Bauskripte Windows/Linux), Befehl `native-fetch` in `tools/package.cs`, `third-party/tesseract/`, `third-party/leptonica/`, `third-party/tessdata-fast/`; ändern `third-party/licenses.json`, `src/Pagewing.App/Resources/ThirdPartyNotices.txt` (neu erzeugen), `tools/package.cs` (`layout`, `check-binary`), `packaging/README.md`; Test in `package.cs selftest`.

**Schnittstellen:** Produziert `tools/package.cs native-fetch --out <Ordner>` (holt per `gh` mit `GH_TOKEN` das Release `native-tesseract-<Version>-<n>`, prüft die im Quelltext festgehaltene SHA-256 je Datei, legt `tesseract.dll`/`libtesseract.so`, `tessdata/eng.traineddata`, `tessdata/deu.traineddata` ab); die Pfadkonvention für Tests: Umgebungsvariable `PAGEWING_TESSERACT_DIR` oder `artifacts/native/` im Repo.

- [ ] **Schritt 1:** Selbsttest-Fälle in `package.cs selftest` zuerst: das Release ist `prerelease`, der Tag passt **nicht** auf `UpdateVersion.TryParse` und nicht auf die Regex von `release.yml`; `native-fetch` lehnt eine falsche Prüfsumme ab und legt nichts ab; das Paketlayout enthält die Bibliothek neben App **und** `cli/` sowie `tessdata/`.
- [ ] **Schritt 2:** Bauskripte: Tesseract + Leptonica statisch zu **einer** Bibliothek je System, **ohne OpenMP, ohne curl, ohne libarchive, ohne Bilddecoder**; Windows MSVC (`win-x64`), Linux im Container `ubuntu:22.04`. Tor: scheitert der Bau, folgt nichts; Ausweg nur eine nicht ausgelieferte Entwicklungsbibliothek.
- [ ] **Schritt 3:** Workflow von Hand starten (auf Anweisung des Projektinhabers), Ergebnis als Vorabversion hochladen; Version und Prüfsummen in `package.cs` festhalten (Muster `InnoSetup`/`AppImageRuntime`).
- [ ] **Schritt 4:** `check-binary --max-glibc 2.35 --max-glibcxx 3.4.30` auf die Linux-Bibliothek; Test, dass die gebaute Bibliothek keine Symbole `curl_`/`archive_` enthält; Größe der Bibliothek messen und in `DECISIONS.md` festhalten.
- [ ] **Schritt 5:** Lizenzen: Einträge samt Lizenzdateien (Tesseract Apache-2.0, Leptonica BSD-2-Clause, tessdata_fast Apache-2.0; Texte aus den Quellen prüfen, nicht aus dem Gedächtnis), `dotnet run tools/licenses.cs -- generate`, `… -- check`, `dotnet run tools/package.cs -- selftest`.
- [ ] **Schritt 6:** Commit `feat(ocr): Tesseract bauen, holen und ausliefern (M15)`; Eintrag in `DECISIONS.md` (Version, Bauoptionen, Release-Schema, Größe).

## Aufgabe 3: Bindings und `OcrEngine`

**Dateien:** Neu `src/Pagewing.Ocr/*` (siehe Dateistruktur), Projekt in `Pagewing.slnx`, `IsAotCompatible`; Test `tests/Pagewing.Core.Tests/Ocr/OcrEngineTests.cs`, `TesseractBindingsTests.cs`; `UnmanagedCallbackTests` bleibt unverändert (kein Rückruf).

**Schnittstellen:** Produziert `TesseractLibrary`, `OcrEngine`, `OcrWord`, `OcrRecognition` laut Tabelle. Bindings nur für gebrauchte Funktionen (Create/Init/SetImage mit Rohpuffer/SetSourceResolution/Recognize/ResultIterator: Wortgrenzen, UTF-8-Text, Konfidenz/Delete). Bibliothek per `NativeLibrary` aus dem Ordner neben der Anwendung (bzw. `PAGEWING_TESSERACT_DIR` in Tests); `datapath` ausdrücklich gesetzt, nur Optionen aus fester Liste, Umgebungsvariablen von Tesseract werden ignoriert.

- [ ] **Schritt 1: Tests schreiben:** `LibraryIsNotLoadedBeforeFirstUse` (`TesseractLibrary.IsAvailable` ist die einzige Probe, Laden erst bei `OcrEngine.Create`), `RecognizesAGeneratedWordImage` (Graustufen-Bitmap mit „Hello World“ aus Geist gerendert → Wörter „Hello“, „World“, Boxen innerhalb ±3 px der bekannten), `GermanUmlautsAreRecognized` (`deu`), `UnknownLanguageThrowsAndNamesTheInstalledOnes`, `CorruptTraineddataThrowsOcrEngineExceptionNotACrash` (zufällige Bytes als `xyz.traineddata`), `CancellationBetweenCallsIsHonoured`, `NoNetworkAndNoFilesAreWritten` (Arbeitsverzeichnis und Temp unverändert nach einem Lauf), `NativeConstantsMatchTheHeaders` (nach `NativeBindingsTests`-Muster). Alle mit `PAGEWING_REQUIRE_OCR`-Regel.
- [ ] **Schritt 2:** `dotnet test tests/Pagewing.Core.Tests --filter-class "*OcrEngineTests"` – schlägt fehl.
- [ ] **Schritt 3:** Umsetzen (`SafeHandle`, String-Eigentum `TessDeleteText`, Iteratoren löschen, Wortbox in Pixeln, Konfidenz 0–100 als `float`); ob Leptonica-Typen (`GetWords`) nötig sind, entscheidet sich hier, vermeiden (Iterator).
- [ ] **Schritt 4:** Test grün, `dotnet build Pagewing.slnx` ohne Warnungen (AOT/Trim), Commit `feat(ocr): Tesseract-Bindings und OcrEngine (M15)`.

## Aufgabe 4: Fixtures

**Dateien:** Ändern `tools/make-testpdfs.cs`, `testdata/pdfs/LICENSES.md`, `tests/Pagewing.Render.Tests` (`RenderCorpus.Skipped`); neue PDFs `ocr-scan.pdf`, `ocr-scan-rotated.pdf`, `ocr-mixed.pdf`, `ocr-hostile.pdf`.

**Schnittstellen:** Produziert die Fixtures und eine Tabelle der bekannten Wörter und Seitenpositionen (`OcrFixtures.KnownWords`, in `tests/Shared`, Wort → Seite + Rechteck in Seitenpunkten oben links). Spec Abschnitt 7: `ocr-scan` 3 Seiten nur Bild (Seite 2 mit „Müller Straße Größe“, Seite 3 zweispaltig), `-rotated` (`/Rotate 90`, CropBox-Versatz), `-mixed` (eine Seite mit echtem Text ≥ 50 Zeichen, eine Seite Scan mit nur einer Seitenzahl, eine leere weiße Seite), `-hostile` (Seite 14.400 × 14.400 pt, Seite mit 100.000 winzigen Wörtern als Bild).

- [ ] **Schritt 1:** `dotnet run tools/make-testpdfs.cs -- check` schlägt fehl (Dateien fehlen); `EveryTestPdfIsInTheCorpusOrExplicitlySkipped` rot.
- [ ] **Schritt 2:** Erzeugen (Text mit Geist in ein Bild rendern, deterministisch, Bild unkomprimiert oder Flate), `generate`, Einträge in `LICENSES.md` (selbst erzeugt) und `RenderCorpus.Skipped`.
- [ ] **Schritt 3:** `dotnet run tools/make-testpdfs.cs -- check` und `dotnet test tests/Pagewing.Render.Tests` grün; Commit `test(ocr): Scan-Fixtures (M15)`.

## Aufgabe 5: Kernentscheidungen (UI- und PDFium-frei)

**Dateien:** Neu `src/Pagewing.Core/Ocr/{OcrMode,OcrOptions,OcrPlanner,OcrResolution,OcrWordMapper,OcrReport,TessdataStore}.cs`; ändern `UserDataPaths.cs` (`TessdataDirectory()`), `AppSettings.cs` (`OcrLanguages`); Tests `OcrPlannerTests`, `OcrResolutionTests`, `OcrWordMapperTests`, `TessdataStoreTests`, `AppSettingsTests`.

**Schnittstellen:** Produziert die Typen der Tabelle (ohne Runner).

- [ ] **Schritt 1: Tests:** `OcrPlannerTests`: `49CharactersRunWithLittleTextNote` (49 → Run, `LittleText`), `50CharactersSkipInSkipModeWithHasText`, `ForceRunsPagesWithText`, `OwnLayerCountsAsAlreadyRecognizedUnlessRedo` (Skip → `AlreadyRecognized`; Redo → Run). `OcrResolutionTests`: `AutoUsesImageDpiClampedTo150Through300` (Bild 600 dpi → 300, 220 → 220, 100 → 150, kein Bild → 300; `Lowered` ist nur wahr, wenn die Pixelobergrenze die Auflösung senkt), `OverTheLimitLowersAndReports` (14.400 pt bei 300 dpi → `Lowered`), `BelowTheMinimumIsTooLow` (nach Senkung < 150 → `TooLow`), `RequestedDpiIsRespected`. `OcrWordMapperTests`: `RotatedAndCropBoxPagesMapToUprightPoints` (Rotation 0/90/180/270, CropBox-Versatz; Pixel → Punkte oben links, Abbildung kommt aus der Anzeigegröße der Seite, keine eigene Rotationsrechnung außer der Skalierung `72 / dpi`). `TessdataStoreTests`: `ImportCopiesIntoTheUserFolderWithASafeName`, `NamesOutsideTheAllowedSetAreRefused`, `FilesOver200MbAreRefused`, `ImportNeverOverwritesAShippedFile`. `AppSettingsTests`: `OcrLanguagesAreNormalizedAndDefaultIsNull`.
- [ ] **Schritt 2–4:** Tests rot, umsetzen, `dotnet test tests/Pagewing.Core.Tests --filter-namespace "*Ocr"` grün, Commit `feat(ocr): Entscheidung, Auflösung, Koordinaten, Sprachdateien (M15)`.

## Aufgabe 6: PDFium-Zugang und Textebene

Beginnt erst mit der Rückmeldung aus Aufgabe 1.

**Dateien:** Neu `src/Pagewing.Core/Pdf/PdfDocument.Ocr.cs`, `src/Pagewing.Pdfium/PdfiumOcr.cs`, `PdfiumOcrLayer.cs`; ändern `src/Pagewing.Pdfium/Native/*` (nur gebrauchte Funktionen), `NativeBindingsTests`; Tests `PdfDocumentOcrTests`, `OcrLayerTests`.

**Schnittstellen:** Produziert die vier `PdfDocument`-Methoden der Tabelle und `PageOcrInfo`. Alles in kurzen `Background`-Aufträgen; `AddOcrLayerAsync` setzt die Marke aus Aufgabe 1, schreibt die Schrift einmal je Dokument, rechnet die Seitenpunkte über `DisplayTransform` zurück in den Seitenraum; Wörter mit Zeichen außerhalb des Schriftvorrats werden ausgelassen und über eine Rückgabe gezählt (`AddOcrLayerResult(int Written, int Skipped)`).

- [ ] **Schritt 1: Tests:** `InfoCountsTextWithoutWhitespaceAndWithoutTheOwnLayer`, `InfoFindsTheImageResolutionAndCoverage` (`ocr-scan`: ein Bild, Abdeckung ≈ 1), `RenderForOcrHasTheDisplayedSizeAtTheRequestedDpi` (auch `-rotated`), `RenderForOcrDrawsNoAnnotations`, `AddedLayerIsFoundBySearchAtTheKnownPosition` (`OcrFixtures.KnownWords`, Trefferrechtecke ±½ Zeichenhöhe, auch rotiert und mit CropBox), `AddingTheLayerLeavesThePageRenderBitIdentical`, `RemoveTakesOnlyTheOwnLayer` (fremder unsichtbarer Text bleibt), `ReadBackReturnsWhatWasWritten`, `CharactersOutsideTheFontAreSkippedAndCounted`, `EmptyWordListWritesNothing`.
- [ ] **Schritt 2–4:** rot, umsetzen, `dotnet test … --filter-class "*OcrLayerTests"` grün, Commit `feat(ocr): Textebene lesen, schreiben, entfernen (M15)`.

## Aufgabe 7: Lauf (`OcrRunner`)

**Dateien:** Neu `src/Pagewing.Core/Ocr/{OcrRunner,IOcrEngineFactory,OcrProgress}.cs`; Test `OcrRunnerTests` (mit `FakeOcrEngine`, ohne native Bibliothek) und `OcrRunnerNativeTests` (mit echter Bibliothek).

**Schnittstellen:** Konsumiert `PdfDocument.*Ocr*`, `OcrPlanner`, `OcrResolution`, `OcrWordMapper`; produziert `OcrRunner.RunAsync`. Rendern nacheinander auf dem `PdfiumThread`, Erkennen auf `min(Kerne − 1, 4)` Workern mit je eigener Engine, Rückstau: höchstens Worker + 1 gerenderte Seiten im Speicher; Zeitgrenze 300 s je Seite (Seite wird `Timeout`, Worker wird ersetzt); Grenzen für Wörter und Zeichen je Seite; Fortschritt zwischen den Seiten.

- [ ] **Schritt 1: Tests:** `SkipsPagesWithTextAndReportsWhy`, `PagesAreReturnedInPageOrderWhateverTheWorkersFinishFirst`, `MemoryStaysBoundedBySlowWorkers` (Fake blockiert; Zahl gerenderter Seiten ≤ Worker + 1), `CancelStopsWorkersAndKeepsNothingHalf` (kein Worker läuft nach dem Abbruch weiter, `OperationCanceledException`), `TimeoutSkipsThePageAndKeepsTheRun` (Fake mit manueller `TimeProvider`-Uhr), `BlankPageGivesNoWordsAndNoError`, `ATooLargePageIsSkippedWithTheReason`, `AWorkerFailureSkipsThePageWithFailed`, `TooManyWordsOnAPageSkipsIt`, `ReportCountsWordsConfidenceAndLowConfidencePages`, `PdfiumThreadIsNeverBlockedByRecognition` (Fake dauert 2 s, gleichzeitig ein `GetPageSizeAsync` unter 200 ms). Nativ: `ScanFixtureIsRecognisedAtTheKnownPositions`.
- [ ] **Schritt 2–4:** rot, umsetzen, grün, Commit `feat(ocr): OcrRunner mit Workern, Rückstau und Zeitgrenze (M15)`.

## Aufgabe 8: Verlauf, Speichern, Signaturhinweis

**Dateien:** Ändern `AnnotationHistory.cs` (`AnnotationCommand.OcrLayer`, Größenrechnung), `AnnotationEditor.cs` (`ApplyOcrAsync`, `StepAsync`), `SignaturePolicy`/`SignatureAdvice` (Text nennt hinzugefügten Text); Tests `OcrEditorTests`, `OcrSaveTests`.

**Schnittstellen:** `ApplyOcrAsync(OcrRunResult result, bool replace)` liest bei `replace` die alte Ebene vorher (`ReadOcrLayerAsync`) als `Before`, schreibt die neue, ruft `PdfDocument.InvalidatePages`-Pfad der Sitzung; wirft `AnnotationHistoryFullException` **vor** der Änderung, wenn die Wortlisten die 32 MiB sprengen.

- [ ] **Schritt 1: Tests:** `UndoRemovesTheLayerAndRedoWritesItAgain`, `ReplaceUndoRestoresTheOldLayer`, `OversizedRunIsRefusedBeforeTheDocumentChanges`, `RunCountsAsAnUnsavedChange`, `SaveIsIncrementalAndTheOriginalBytesStayThePrefix` (Muster `FormSaveTests`), `SavedFileIsSearchableAfterReopen`, `HistoryIsEmptyAfterSaving`, `SignedDocumentWarnsAndNamesAddedText`, `EncryptedFileStaysEncryptedOrIsRefused`.
- [ ] **Schritt 2–4:** rot, umsetzen, grün, Commit `feat(ocr): Lauf als Verlaufsschritt, inkrementelles Speichern (M15)`.

## Aufgabe 9: CLI `pagewing ocr`

**Dateien:** Neu `src/Pagewing.Cli/Commands/OcrCommand.cs`; ändern `CliOptions.cs` (Optionen je Befehl, Zeile 96-Muster), `CliApp.cs` (Dispatch, Hilfetext), `README.md`; Test `tests/Pagewing.Core.Tests/Cli/OcrCommandTests.cs` (über `CliApp.RunAsync`, `CliTestBase`).

**Schnittstellen:** Syntax laut Spec 6.2. Schreibt nur inkrementell (`DocumentSaver.SaveAsync` mit Präfixprüfung), `OutputGuard`, `--force` ersetzt nie eine Eingabe, Bericht auf stderr ohne erkannten Text, Exit 1 bei Fehlern und wenn keine Seite erkannt wurde; `--dry-run` lädt Tesseract nicht.

- [ ] **Schritt 1: Tests:** `RecognisesAScanAndTheOutputIsSearchable` (Suche im Ergebnis), `OutputKeepsTheInputBytesAsPrefix`, `PagesWithTextAreSkippedAndReportedOnStderr`, `UnknownLanguageExits1ListingTheInstalled`, `MissingLibraryExits1WithAMessage`, `OutputEqualToInputIsRefusedAndForceNeverReplacesAnInput`, `DryRunLoadsNoLibraryAndListsPagesAndDpi`, `NoRecognisedPageExits1`, `ReportContainsNoRecognisedText`, `BadModeOrDpiExits2`.
- [ ] **Schritt 2–4:** rot, umsetzen, grün, `dotnet run tools/smoke-cli.cs -- <pagewing.exe>` am AOT-Binary um einen `ocr`-Aufruf erweitern und ausführen, Commit `feat(ocr): CLI pagewing ocr (M15)`.

## Aufgabe 10: Oberfläche

**Dateien:** Neu `ViewModels/TabViewModel.Ocr.cs`, `MainViewModel.Ocr.cs`, `OcrViewModel.cs`, `Views/OcrHintBar.axaml(.cs)`, `OcrCard.axaml(.cs)`, `OcrReportCard.axaml(.cs)`; ändern `CommandCatalog.cs` (`ocr.run`, Kürzel nach Kollisionsprüfung), `MainViewModel.Close.cs`/`Save.cs`/`Print.cs`/Update-Weg (Sperren), `SettingsViewModel` nur falls Sprachen dort stehen, `Strings*.resx`, `Strings.cs`; Tests `OcrHintTests`, `OcrCardTests`, `OcrRunTests`, `OcrStartPathTests`, `CommandCatalogTests`.

**Schnittstellen:** Konsumiert `OcrRunner`, `AnnotationEditor.ApplyOcrAsync`, `TessdataStore`, `AppSettings.OcrLanguages`; Nähte für Tests: `MainViewModel.OcrEngineFactory` (Fake), `MainViewModel.TessdataPicker`.

- [ ] **Schritt 1: Tests:** `HintAppearsOnceForAScanAfterTheFirstSharpPage` (nie in Messläufen, nie ohne Bild über der Seite, „Nicht jetzt“ und „Für diese Datei nicht mehr fragen“ im Speicher des Tabs), `HintNeverStartsARun`, `CardDefaultsAreSkipModeAutoResolutionAndRememberedLanguages`, `CardStatesWhatHappensAndNeverPromisesAccuracy` (kein „fehlerfrei“/„korrekt“; Texte in beiden Sprachen), `ImportingALanguageFileAsksForTrust`, `RunLocksEditSavePrintAndOtherModesWithAVisibleReason`, `ReadingScrollingAndSearchStayPossibleDuringARun`, `EscCancelsAndOffersFinishedPagesDefaultingToDiscard`, `ClosingTheTabOrWindowDuringARunCancelsIt`, `ReportListsSkippedPagesLowConfidenceAndNeverSaysComplete`, `AfterApplyingSearchFindsTheWordsWithoutReopening`, `OcrStartPathTests` (Bibliothek nicht geladen, Karte/Runner nicht erzeugt, `MeasurementRun.Start` öffnet nie), `CommandCatalogTests` (Eintrag, Kürzel ohne Kollision, Hilfekarte listet ihn), Hochkontrast/„Kompakt“/150 %/200 % und Automation-Namen wie bei `RedactApplyCard`.
- [ ] **Schritt 2–4:** rot, umsetzen (Hinweis nach `HasShownSharpPage`, Erkennung „sieht aus wie ein Scan“: erste bis zu 5 Seiten ohne Text mit Bildabdeckung > 0,8, in kurzen Hintergrundaufträgen), grün, Commit `feat(ocr): App-Oberfläche für OCR (M15)`.

## Aufgabe 11: Zusammenspiel, feindliche Eingaben, Messung

**Dateien:** Neu `tests/Pagewing.Core.Tests/Ocr/OcrInteractionTests.cs`, `OcrHostileTests.cs`, `OcrMeasurementTests.cs` (nur mit `PAGEWING_MEASURE_OUT`), `InteropSampleTests`-Erweiterung (`WritesOcrSamplesForOtherViewers`).

- [ ] **Schritt 1: Tests:** `OcrThenRedactingAWordRemovesPixelsAndLayerAndTheCheckPasses` (Abnahme 6, M14-Pipeline), `OrganizingPagesKeepsTheLayerOrTheReportSaysSo` (Aufgabe 1 entschied), `RotatedFixtureSearchHitsAreOnTheWord`, `HostileFixtureEndsWithReportedSkipsAndBoundedMemory`, `SecondRunInRedoModeLeavesExactlyOneLayer`, `ForceModeKeepsExistingTextUntouched`. Messung: Seiten je Sekunde, Spitzenspeicher je Instanz, Ladezeit der Bibliothek, Dateigrößenzuwachs, 2.000-Seiten-Fall (prüfen nie eine Zeit).
- [ ] **Schritt 2–4:** rot, ggf. Korrekturen in den vorigen Aufgaben, grün, Commit `test(ocr): Zusammenspiel und feindliche Eingaben (M15)`.

## Aufgabe 12: Dokumentation, Abnahme, Roadmap

- [ ] **Schritt 1:** `Docs/DECISIONS.md` (Auslieferung, Textebene, Sprachdaten, Grenzen, Messwerte), `CLAUDE.md` Block „OCR (seit M15)“ (Wo-was-liegt, Stolperfallen; `Pagewing.Ocr` aus „später“ nehmen), `README.md` (`pagewing ocr`), `CHANGELOG.md` (`[Unreleased]`), `packaging/README.md` (Bau der Bibliothek, Handprüfung: Chrome/Acrobat, Linux-Läufe `*Ocr*` und `*Cli`, echter eigener Scan, Karte hell/dunkel/Hochkontrast/150 %/200 %/Bildschirmleser, Startzeit-Check auf dem Haupt-PC).
- [ ] **Schritt 2:** `Docs/roadmap.json`: Aufgaben und Kriterien abhaken **nur mit Nachweis** (Testname, Befehl + Ergebnis), Scope-Änderungen, Backlog (Sprachdatei-Download, OSD/Drehung, Prüfmodus für unsichere Wörter), `dotnet run tools/roadmap.cs -- generate` und `-- check`.
- [ ] **Schritt 3: Fertig heißt:** `dotnet build Pagewing.slnx` (0 Warnungen), `dotnet test Pagewing.slnx` Debug **und** `-c Release` (Zahlen nennen), `dotnet format Pagewing.slnx --verify-no-changes`, `licenses check`, `make-testpdfs check`, `package.cs selftest`, AOT-Publish von App und CLI (win-x64) und `smoke-cli`. Offen markieren und mit Befehl nennen: Linux-Läufe, Startzeit-Check auf echter Hardware, Handprüfungen.
- [ ] **Schritt 4:** Commit `docs: OCR (Milestone 15) abgenommen`, PR nach `dev`.
