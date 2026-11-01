# Milestone 13: Seiten organisieren – Implementierungsplan

> **Für ausführende Agenten:** PFLICHT-SKILL: `superpowers:subagent-driven-development` (empfohlen) oder `superpowers:executing-plans`, Aufgabe für Aufgabe. Schritte sind Checkboxen (`- [ ]`).

**Ziel:** Seiten lassen sich in der Seitenleiste vormerken (drehen, löschen, umsortieren, duplizieren, leere Seite, Seiten aus einer anderen PDF), mit „Anwenden“ vollständig neu schreiben, zusammenführen, teilen, extrahieren und als PNG exportieren; die CLI kann `merge`, `split`, `rotate`, `export`.

**Architektur:** Rechnerisches (`PageEditPlan`, Verlauf, Verlustliste, Dateinamen, Teilen) liegt UI- und PDFium-frei in `Pagewing.Core.Pages`. Ein Plan wird auf einer **Arbeitskopie** des geöffneten Dokuments ausgeführt (Löschen, Verschieben, Drehen am Dokument selbst; fremde Seiten per `FPDF_ImportPagesByIndex`), nie durch Neuaufbau in ein leeres Dokument; die erste Datei ist die primäre. Geschrieben wird mit einem neuen Modus „vollständiger Neuschrieb“ der Prüfkette von `DocumentSaver`. Die Ansicht bleibt bis „Anwenden“ unverändert. Aufgabe 1 (Spike) entscheidet vor jedem Produktivcode, ob PDFium reicht oder QPDF nötig wird (Halt).

**Tech Stack:** .NET 10 Native AOT, Avalonia 12.1, PDFium (`bblanchon.PDFium.*`, 157.0.8100) über eigene `[LibraryImport]`-Bindings, xunit.v3 (Microsoft.Testing.Platform), Avalonia.Headless.

**Spec:** [2026-10-31-teilprojekt3-seiten-design.md](../specs/2026-10-31-teilprojekt3-seiten-design.md) (maßgeblich, Abschnittsnummern unten beziehen sich darauf); Rahmen: [Gesamt-Spec](../specs/2026-10-16-pagewing-design.md) Abschnitt 6, [Designsystem](../specs/2026-10-17-designsystem.md); Roadmap „Milestone 13“.

## Globale Randbedingungen

Gelten für jede Aufgabe (Wortlaut aus Spec und `CLAUDE.md`):

- PDFium nur über den `PdfiumThread`, Aufträge **kurz** (Importe in Stapeln zu höchstens `PageEditLimits.ImportBatch` = 50 Seiten, `PdfiumPriority.Background`, Abbruch zwischen den Stapeln); der UI-Thread wartet nie auf PDFium. Ein Neuschrieb ist ein Auftrag (bekannte Grenze aus M9). `unsigned long` immer `CULong`; Konstanten und Funktionen gegen die Header 157.0.8100 prüfen (`NativeBindingsTests`). Kein neuer `[UnmanagedCallersOnly]`-Rückruf geplant.
- Seitenoperationen erzwingen einen **vollständigen Neuschrieb** (`FPDF_NO_INCREMENTAL`), nie inkrementell; signierte Dokumente: Warnkarte vor dem Anwenden (jede DocMDP-Stufe, „wird als ungültig angezeigt“, nie „gültig“, nie „rechtsgültig“). Atomar: temporäre Datei im Zielordner, Neuöffnen, Prüfen, erst dann ersetzen; das Original wird nie vor der Prüfung angefasst. Kein stilles Speichern.
- **Nie still verlieren:** Lesezeichen, Formularfelder, Seitenbeschriftungen und Signaturen, die ein Vorgang verliert, verkoppelt oder ungültig macht, stehen **vorher** in der Verlustliste (Karte bzw. CLI-Ausgabe). Verschlüsselt bleibt verschlüsselt, sonst wird abgelehnt (`SaveFailureReason.EncryptedNotSupported`), nie still entschlüsselt, auch kein Weg „entschlüsselt speichern“. Mindestens eine Seite bleibt.
- Ein offener Plan sperrt Annotations- und Formularmodus (Grund sichtbar), zählt als ungespeicherte Änderung (`AsksBeforeClosing`, Update-Weg `ResolveUnsavedChangesAsync`) und lebt im `TabViewModel`; ungespeicherte Annotations-/Formularänderungen beim Anwenden: Karte „erst speichern“ (inkrementell, bestehender Weg), kein Einfließen. Extrahieren, Teilen und Bilderexport lesen das geöffnete Dokument **ohne** Plan und sind gesperrt, solange ein Plan offen ist oder Änderungen ungespeichert sind (Grund sichtbar).
- Anwenden-Karte: Standardknopf „Speichern unter …“, daneben „In der Originaldatei speichern“ und „Zurück“; ein Satz sagt, dass der Neuschrieb nicht rückgängig zu machen ist; keine Sicherungskopie.
- Ausgabedateien (App, CLI) nie ohne ausdrückliche Wahl überschreiben (CLI: `--force` an **jedem** schreibenden Befehl); Eingabe ≠ Ausgabe. Dateinamen aus Lesezeichen werden bereinigt (kein Pfadtrenner, keine reservierten Windows-Namen, Länge begrenzt, eindeutig, nie aus dem Zielordner heraus).
- Zustände nie nur über Farbe; Kontrast WCAG 2.2 AA; Tab fängt den Fokus nie; Automation-Namen, `AccessibilityView="Content"` an `Grid`/`Border` mit Namen; keine Farb- oder Maßliterale im XAML; Kürzel nur im `CommandCatalog` (Kollisionstest); Leseansicht ohne Bearbeitungswerkzeuge (Werkzeuge nur im Modus „Organisieren“); nichts ausgegraut für spätere Versionen.
- Startpfad: nichts Neues vor dem ersten scharfen Seitenbild; Modus, Plan, Ausführer, Karten, Arbeitskopie entstehen erst beim Gebrauch; Messläufe unverändert; Startzeit-Budgets nie lockern.
- Keine neuen Abhängigkeiten außer der QPDF-Entscheidung aus Aufgabe 1 (dann Lizenzmanifest, `ThirdPartyNotices.txt`, `DECISIONS`). Native AOT: keine Reflection, JSON nur mit Source-Generator. PNG ist das einzige Bildformat (JPEG Backlog).
- UI-Texte Englisch **und** Deutsch (`Strings.en.resx`, `Strings.de.resx`, `Strings.cs`). Code und Kommentare Englisch; Doku und Commits Deutsch (Conventional Commits, gezielt stagen, nie `git add .`, danach `git show --stat HEAD`). Testnamen ohne Unterstriche. Tests schreiben nie in den Benutzerordner (`UserSettings.Detached()`, Temp-Ordner) und nie neben Eingaben in `testdata` (Arbeit auf Kopien, `TempFolder`).
- Neue Test-PDFs: Herkunft in `testdata/pdfs/LICENSES.md`, Eintrag in `RenderCorpus.Skipped`, Erzeugung in `tools/make-testpdfs.cs` (`check` bleibt grün).
- Befehle: Build `dotnet build Pagewing.slnx`, Test `dotnet test Pagewing.slnx`, Format `dotnet format Pagewing.slnx --verify-no-changes`, Roadmap `dotnet run tools/roadmap.cs -- check`; einzelne Tests `dotnet test tests/Pagewing.Core.Tests --filter-class "*Name"` (App: `tests/Pagewing.App.Tests`).
- Commit-Trailer jeder Aufgabe: `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`. Commits nur auf `feature/m13-seiten-organisieren`.

## Festgelegte Werte (Startwerte des Plans sind markiert)

| Größe | Wert |
|---|---|
| Obergrenzen (Startwerte, Test fixiert sie) | `PageEditLimits.MaxSlots` 20.000 Seiten je Plan; `MaxInsertedFiles` 20; `ImportBatch` 50; Datei einfügen höchstens `PdfiumDocument.MaxFileLength` |
| Drehung | Vielfache von 90, absolut 0/90/180/270 nach dem Anwenden; Plan trägt die **zusätzliche** Drehung |
| Leere Seite | Größe der Nachbarseite (Seite davor, sonst danach), Startwert A4 595 × 842 pt bei leerem Umfeld |
| Seitenfingerabdruck der Prüfung | `PageFingerprint(Width, Height, Rotation, TextHash)`: Größe nach Drehung auf 0,5 pt, Hash der ersten 256 Zeichen Text |
| Kürzel (Startwerte, Kollisions- und Erreichbarkeitstest auf deutscher Tastatur entscheidet) | `pages.organize` Strg+Umschalt+O; die Befehle im Modus (Drehen links/rechts, Löschen Entf, Duplizieren, Leere Seite, Datei einfügen, Anwenden, Verwerfen) nur im Katalog |
| CLI | Syntax wie Spec 5.6; `export` = nur Bilder; `split --bookmarks` nur oberste Ebene |
| Bilderexport | PNG, Standard 144 dpi (wie `render`), Obergrenze 64 Megapixel je Seite (Startwert; sonst klarer Fehler), Namen `<Name>-p<NNN>.png` |

## Review-Fokus

Eingaben und Zustände, die die Spec nicht ausspricht, wahrscheinlichste zuerst; jeder Punkt hat seinen Test in der genannten Aufgabe:

1. **Alles gelöscht oder leerer Plan:** Löschen der letzten Seite wird abgelehnt (Grund sichtbar), ein Plan ohne Änderung (`IsIdentity`) schreibt nichts (Aufgabe 2, 6, 9).
2. **Feindliche Lesezeichentitel als Dateinamen** (`..\..\x`, `a/b`, `CON`, `NUL.pdf`, leer, 5.000 Zeichen, gleiche Titel, Unicode mit Steuerzeichen): jede Datei landet im Zielordner, eindeutig, nichts wird überschrieben (Aufgabe 11).
3. **Vervielfachen:** Duplizieren in Schleife, dieselbe Datei 50× einfügen, Bereich `1-2000000000`: Obergrenzen greifen mit klarer Meldung, kein Speicherstau, der `PdfiumThread` bleibt für das Rendern frei (Aufgabe 2, 3, 10).
4. **Quelle ändert sich zwischen Vormerken und Anwenden** (Original oder eingefügte Datei überschrieben, gelöscht, gesperrt, falsches Passwort): Anwenden scheitert mit Grund, Ziel byteweise unverändert, Plan bleibt erhalten (Aufgabe 4, 8, 10).
5. **Zustandswechsel mit offenem Plan:** Tab schließen, Tab in neues Fenster ziehen, Update-Weg, Drucken, Tab-Wechsel, Speichern während der Arbeitskopie entsteht: nichts geht still verloren, der Plan wandert mit dem Tab (Aufgabe 6, 8).

---

## Dateistruktur

| Datei | Verantwortung |
|---|---|
| `src/Pagewing.Core/Pages/` (neu): `PageEditPlan.cs`, `PageSlot.cs`, `PageEditHistory.cs`, `PageEditLimits.cs`, `PageEditException.cs`, `PageEditExecutor.cs`, `PageEditReport.cs` (Verlustliste), `OutputNames.cs`, `DocumentSplitter.cs`, `PageImageExporter.cs` | rein rechnerisch bzw. Ablauf über `PdfDocument`; kein UI, kein direktes PDFium |
| `src/Pagewing.Core/Pdf/PdfDocument.Pages.cs` (neu) | Operationen auf einer Arbeitskopie |
| `src/Pagewing.Pdfium/Native/Fpdf.cs`, `PdfiumPageEdit.cs` (neu) | Bindings und kurze PDFium-Aufträge |
| `src/Pagewing.Core/Saving/DocumentSaver.cs`, `SaveRequest`-Typen | neuer Modus `RewriteAsync` mit `RewriteRequest` |
| `src/Pagewing.Core/Viewing/ViewerSession.cs` | `AdoptRewrittenDocumentAsync` (neue Cache-Instanz) |
| `src/Pagewing.App/ViewModels/TabViewModel.Pages.cs`, `MainViewModel.Pages.cs`, `PendingSave.cs` (erweitert) | Modus, Plan, Sperren, Anwenden, Karten |
| `src/Pagewing.App/Views/PagesPanel*`, `PagesOrganizeBar`, `ApplyPagesCard`, `InsertFileCard`, `SplitCard`, `ExportImagesCard` | Oberfläche |
| `src/Pagewing.Cli/Commands/` (`RotateCommand`, `MergeCommand`, `SplitCommand`, `ExportCommand`, `PagesCommand`), `CliApp.cs`, `CliOptions.cs` | CLI |
| `tests/Pagewing.Core.Tests/Pages/`, `tests/Pagewing.Core.Tests/Cli/`, `tests/Pagewing.App.Tests/Pages*Tests.cs` | Tests |
| `testdata/pdfs/pages-marked.pdf`, `tools/make-testpdfs.cs` | Fixture |

---

## Stufe 1

### Aufgabe 1: Fixture, Bindings und Spike (Halt-Aufgabe)

**Dateien:** Ändern `tools/make-testpdfs.cs`, `testdata/pdfs/LICENSES.md`, `tests/Pagewing.Render.Tests/RenderCorpus.cs` (`Skipped`), `src/Pagewing.Pdfium/Native/Fpdf.cs`; neu `src/Pagewing.Pdfium/PdfiumPageEdit.cs`, `testdata/pdfs/pages-marked.pdf`, `tests/Pagewing.Core.Tests/Pdfium/PageEditSpikeTests.cs`; Ergebnis in `Docs/DECISIONS.md`.

**Schnittstellen – erzeugt:** `internal static class PdfiumPageEdit` (nur auf dem `PdfiumThread`): `Import(PdfiumDocument dest, PdfiumDocument source, ReadOnlySpan<int> indices, int destination)`, `Delete(PdfiumDocument, int index)`, `Move(PdfiumDocument, ReadOnlySpan<int> indices, int destination)`, `SetRotation(PdfiumDocument, int index, int degrees)`, `GetRotation(PdfiumDocument, int index)`, `InsertBlank(PdfiumDocument, int index, double width, double height)`, `SaveRewrite(PdfiumDocument, Stream)` (`FPDF_NO_INCREMENTAL`, ohne `FPDF_REMOVE_SECURITY`); alle werfen `PdfiumException`-Familie bei `FALSE` und lassen sich nur mit Prüfung der Thread-Zugehörigkeit aufrufen.

- [ ] **Schritt 1: Fixture.** `pages-marked.pdf` (deterministisch, unkomprimiert): 6 Seiten, Seite *n* trägt den Text `PAGE n` groß in der Mitte; Seite 3 Querformat, Seite 5 mit `/Rotate 90`; Lesezeichen *Kapitel 1* (→ Seite 1) mit Kind *Abschnitt 1.1* (→ Seite 2), *Kapitel 2* (→ 3), *Kapitel 3* (→ 5); `/PageLabels` (römisch für 1–2, dezimal ab 3); Info-Titel. Eintrag in `LICENSES.md` („selbst erzeugt“) und `RenderCorpus.Skipped`; `dotnet run tools/make-testpdfs.cs -- generate`, danach `-- check` grün. Fixtures für Formular (`forms-basic.pdf` zweimal geöffnet liefert die Namenskollision), verschlüsselt (`encrypted_hello_world_r3/r6.pdf`) und signiert (`signed-docmdp-*.pdf`) gibt es schon.
- [ ] **Schritt 2: Bindings.** `Fpdf.FPDF_ImportPagesByIndex(nint dest, nint src, int* indices, CULong length, int index)` → `int`; `FPDF_MovePages(nint doc, int* indices, CULong length, int dest)` → `int`; `FPDFPage_Delete(nint doc, int index)`; `FPDFPage_New(nint doc, int index, double width, double height)` → `nint`; `FPDFPage_SetRotation(nint page, int rotate)`; `FPDFPage_GetRotation(nint page)` → `int`. `PdfiumPageEdit` nach dem Muster der vorhandenen Klassen (`PdfiumDocument.Access()`, Seite laden/schließen).
- [ ] **Schritt 3: Spike-Tests schreiben und laufen lassen.** Jeder Test hält das **gefundene** Verhalten als Wächter gegen PDFium-Updates fest (Erwartung nach dem ersten Lauf eintragen, nie lockern). Namen und Fragen (Spec 6): `DeletingPagesKeepsOutlineLabelsAndFormOfTheOthers` (a: Lesezeichen, `/PageLabels`, AcroForm, Info der primären Datei nach Löschen, Verschieben, Drehen; Lesezeichen auf gelöschte Seiten), `ImportKeepsAnnotationsLinksAndWidgets` (b: Annotationen, interne Links, Felder der importierten Seiten), `ImportFromTheSameDocumentDuplicatesAPage`, `ImportOfTwoFormsWithTheSameFieldNamesYieldsOneOrTwoFields` (b: `forms-basic.pdf` in sich selbst und in eine Kopie), `RewriteOfEncryptedKeepsEncryption` (c: r3 und r6), `RewriteOfSignedDocumentSaysWhatItDoes` (Attrappe, nur Struktur). Externe Prüfung (d): `qpdf --check` auf jedes Ergebnis (lokal, sonst Container `ubuntu:24.04` mit `apt-get install qpdf` wie im Plan v0.2.0) und Chrome (Handsicht); Fehler und Warnungen getrennt festhalten.
- [ ] **Schritt 4: Auswerten und HALT.** Eintrag in `Docs/DECISIONS.md` („Seiten organisieren: Spike und QPDF-Entscheidung (M13)“) mit Ergebnis je Frage (a)–(d) und Anwendung der Entscheidungsregel aus Spec 6. Reicht PDFium: weiter, die Verlustliste (Aufgabe 3) richtet sich nach den Befunden. Trifft ein Auslöser zu (Fehler von `qpdf --check` durch PDFiums Neuschrieb, beschädigte primäre Daten, nicht erhaltene Verschlüsselung): **nicht weiterbauen**, dem Projektinhaber den Befund vorlegen und die QPDF-Variante gemeinsam planen.
- [ ] **Schritt 5: Gesamtlauf und Commit** (`dotnet build`, `dotnet test tests/Pagewing.Core.Tests`, `tools/make-testpdfs.cs -- check`, `dotnet format --verify-no-changes`); `feat(pdfium): Seitenbearbeitung binden, Spike M13`.

### Aufgabe 2: Plan, Verlauf, Grenzen (rein rechnerisch)

**Dateien:** neu `src/Pagewing.Core/Pages/PageEditPlan.cs`, `PageSlot.cs`, `PageEditHistory.cs`, `PageEditLimits.cs`, `PageEditException.cs`; Test `tests/Pagewing.Core.Tests/Pages/PageEditPlanTests.cs`.

**Schnittstellen – erzeugt:** `readonly record struct PageSourceId(int Value)` (0 = primär); `sealed record BlankPage(double Width, double Height)`; `sealed record PageSlot(PageSourceId Source, int SourceIndex, int ExtraRotation, BlankPage? Blank)`; `sealed class PageEditPlan` mit `static Identity(int pageCount)`, `IReadOnlyList<PageSlot> Slots`, `bool IsIdentity`, unveränderlichen Operationen auf **Positionen** der Liste: `Rotate(IReadOnlyCollection<int> positions, int degrees)`, `Delete(positions)`, `Move(positions, int destination)`, `Duplicate(positions)`, `InsertBlank(int at, BlankPage size)`, `InsertFrom(PageSourceId source, IReadOnlyList<int> sourceIndices, int at)`, `int FollowPage(int oldPrimaryIndex)` (neue Position der Seite, sonst die nächste vorhandene); `sealed class PageEditHistory(PageEditPlan start)` mit `Current`, `Version`, `CanUndo`, `CanRedo`, `Do(PageEditPlan)`, `Undo()`, `Redo()`; `static class PageEditLimits` (`MaxSlots`, `MaxInsertedFiles`, `ImportBatch`); `PageEditException(PageEditError Error)` mit `PageEditError { LastPage, LimitExceeded, OutOfRange, InvalidRotation }`.

- [ ] **Schritt 1: Tests.** `DeleteRejectsTheLastPage` (`Error == LastPage`, Plan unverändert), `RotateAddsAndWrapsAt360` (90+270 = 0; 45 → `InvalidRotation`), `MoveKeepsTheOrderOfTheMovedPages` (Positionen {1,3} nach 5 in 6 Seiten), `DuplicateInsertsRightAfterEachSource`, `InsertBlankTakesTheSizeGiven`, `InsertFromAppendsForeignSlots`, `DuplicateBeyondMaxSlotsFails` (`LimitExceeded`, auch `InsertFrom` mit 21 Dateien), `OutOfRangePositionsFail`, `FollowPageMapsAfterDeleteAndMove` (gelöschte Seite → nächste vorhandene), `HistoryUndoRedoAndVersion` (`Version` steigt bei jedem `Do`, `Undo`, `Redo`; `Do` nach `Undo` leert Redo), `IdentityPlanIsIdentityUntilChanged`.
- [ ] **Schritt 2: Laufen lassen (rot), Plan und Verlauf implementieren** (unveränderlich, Listen kopieren; Obergrenzen vor jeder Vergrößerung prüfen), Tests grün: `dotnet test tests/Pagewing.Core.Tests --filter-class "*PageEditPlanTests"`.
- [ ] **Schritt 3: Commit** `feat(core): Seitenplan mit Verlauf und Grenzen`.

### Aufgabe 3: Arbeitskopie, Ausführer und Verlustliste

**Dateien:** neu `src/Pagewing.Core/Pdf/PdfDocument.Pages.cs`, `src/Pagewing.Core/Pages/PageEditExecutor.cs`, `PageEditReport.cs`; Tests `tests/Pagewing.Core.Tests/Pages/PageEditExecutorTests.cs`, `PageEditReportTests.cs`.

**Schnittstellen – verbraucht:** `PdfiumPageEdit` (Aufgabe 1), `PageEditPlan` (Aufgabe 2), `PdfEngine.OpenAsync(string path, string? password, CancellationToken)`, `PdfDocument.GetOutlineAsync`, `GetFormFieldsAsync`, `GetSignatureInfoAsync`. **Erzeugt:** auf `PdfDocument` (nur für Arbeitskopien): `Task ImportPagesAsync(PdfDocument source, IReadOnlyList<int> sourceIndices, int destination, CancellationToken ct = default)` (Stapel zu `ImportBatch`), `Task DeletePagesAsync(IReadOnlyList<int> indices, ct)`, `Task MovePagesAsync(IReadOnlyList<int> indices, int destination, ct)`, `Task SetPageRotationAsync(int index, int degrees, ct)`, `Task<int> GetPageRotationAsync(int index, ct)`, `Task InsertBlankPageAsync(int index, double width, double height, ct)`, `Task SaveRewriteAsync(Stream destination, ct)`; `sealed record PageSourceFile(string Path, string? Password)`; `sealed class PageEditExecutor(PdfEngine engine)` mit `Task<PdfDocument> BuildAsync(PageEditPlan plan, PageSourceFile primary, IReadOnlyDictionary<PageSourceId, PageSourceFile> inserted, IProgress<double>? progress, CancellationToken ct)` (öffnet die Arbeitskopie aus der **Datei** der primären Quelle, nie aus dem angezeigten Dokument; Ergebnis hat genau `plan.Slots.Count` Seiten in Planreihenfolge); `sealed record PageEditReport(IReadOnlyList<PageLoss> Losses)` und `PageLoss(PageLossKind Kind, string? Detail, IReadOnlyList<int>? Pages)` mit `PageLossKind { ForeignOutlineDropped, BookmarkToDeletedPage, DuplicatedFormFields, FormFieldNameCollision, SignatureInvalidated, EncryptionNotPreserved }`; `static Task<PageEditReport> PageEditExecutor.AnalyzeAsync(PageEditPlan plan, PdfDocument primary, IReadOnlyDictionary<PageSourceId, PdfDocument> inserted, CancellationToken ct)` (Befunde des Spikes bestimmen, welche Arten wirklich auftreten; Mindestanforderung der Spec 5.5: gemeldet, nie still).

- [ ] **Schritt 1: Tests (Executor, `pages-marked.pdf` auf einer Kopie).** `DeleteMoveRotateGivesTheOrderAndRotationOfThePlan` (Reihenfolge über `GetTextAsync` je Seite: `PAGE 3`, `PAGE 1`, … ; Rotation 0/90/180/270 über `GetPageRotationAsync`), `OutlineLabelsAndInfoOfThePrimaryFileSurvive` (Spike-Ergebnis als Erwartung), `DuplicateGivesTwoPagesWithTheSameText`, `InsertFromAnotherFileAppendsItsPages` (Text der Fremdseiten an den Planpositionen), `InsertBlankHasTheGivenSizeAndNoText`, `BuildDoesNotTouchTheOriginalFile` (Bytes vorher = nachher), `BuildIsCancelableBetweenBatches` (Abbruch nach dem ersten Stapel, Arbeitskopie freigegeben), `BuildOfFiftyCopiesStaysUnderTheLimit` (51. Kopie → `LimitExceeded`), `WrongPasswordOfAnInsertedFileFails` (klare Ausnahme, kein Absturz).
- [ ] **Schritt 2: Tests Verlustliste.** `DeletingATargetPageReportsBookmarkToDeletedPage` (Kapitel 2 → Seite 3 gelöscht), `InsertedFileWithOutlineReportsForeignOutlineDropped`, `SameFieldNamesFromTwoFilesReportCollision` (`forms-basic.pdf` zweimal), `DuplicatingAFormPageReportsDuplicatedFormFields`, `SignedDocumentReportsSignatureInvalidated` (`signed-docmdp-3.pdf`, alle Stufen), `PlainReorderReportsNothing` (`pages-marked.pdf`, nur Drehen und Umsortieren: leere Liste).
- [ ] **Schritt 3: Implementieren** (rot → grün): `PdfDocument.Pages.cs` als dünne Aufträge auf dem `PdfiumThread` (`document.Thread.InvokeAsync(..., PdfiumPriority.Background, ct)`), Reihenfolge der Schritte im Ausführer nach dem Spike (Import, Löschen, `MovePages`, Drehen, leere Seiten), Obergrenzen vor dem Öffnen prüfen. `dotnet test tests/Pagewing.Core.Tests --filter-namespace "*Pages"`.
- [ ] **Schritt 4: Commit** `feat(core): Arbeitskopie, Ausführer und Verlustliste für Seitenpläne`.

### Aufgabe 4: Vollständiger Neuschrieb in der Prüfkette

**Dateien:** Ändern `src/Pagewing.Core/Saving/DocumentSaver.cs`; neu `src/Pagewing.Core/Saving/RewriteRequest.cs`; Test `tests/Pagewing.Core.Tests/Saving/DocumentSaverRewriteTests.cs`.

**Schnittstellen – erzeugt:** `sealed record PageFingerprint(double Width, double Height, int Rotation, int TextHash)` (`ToString` ohne Passwort-Bezug nötig nicht); `sealed record RewriteRequest(string OriginalPath, string TargetPath, FileStamp? OriginalStamp, string? Password, IReadOnlyList<PageFingerprint> ExpectedPages, IReadOnlyList<PdfDocument> UnchangedSources, bool OverwriteChangedOriginal)` (Passwort wie bei `SaveRequest` nie in `ToString`); `static Task<SaveResult> DocumentSaver.RewriteAsync(PdfDocument workingCopy, PdfEngine engine, RewriteRequest request, IProgress<double>? progress = null, CancellationToken ct = default)`; Hilfsfunktion `static Task<IReadOnlyList<PageFingerprint>> PageEditExecutor.FingerprintsAsync(PdfDocument, CancellationToken)` (Größe nach Drehung, Hash der ersten 256 Zeichen Text).

- [ ] **Schritt 1: Tests.** `RewriteWritesAFullFileWithoutThePrefixCheck` (Datei ist kein Anhang des Originals, Seitenzahl und Fingerabdrücke wie erwartet, `Reopened` offen), `RewriteKeepsTheTargetUntouchedOnEveryFailure` (Faults aus `SaveFaults`: Schreibfehler, Abbruch, Verifikation; Zieldatei byteweise gleich, kein `.pw-*.tmp`), `RewriteRefusesWhenAnUnchangedSourceChangedMeanwhile` (eingefügte Quelle zwischen Bau und Schreiben überschrieben → `SourceModified`, auch für „Speichern unter“), `RewriteRefusesWhenTheOriginalWasReplaced` (`OriginalChanged`, übersteuerbar), `RewriteRefusesWhenEncryptionIsLost` (`EncryptedNotSupported`, Quelle r3/r6), `RewriteDetectsAWrongOrderOrRotation` (absichtlich falscher Fingerabdruck → `VerificationFailed`), `RewriteToANewNameLeavesTheOriginalAlone`.
- [ ] **Schritt 2: Implementieren** (rot → grün): Pipeline wie `SaveAsync` ohne `CheckPrefixAsync`, mit `SaveRewriteAsync`, Quellprüfung auch für `UnchangedSources`, Prüfrendern aller Seiten bis höchstens 50, danach stichprobenweise; Ersetzen unverändert. `dotnet test tests/Pagewing.Core.Tests --filter-class "*DocumentSaver*"` (die vorhandenen Tests bleiben grün, Linux-only-Tests laufen hier nicht).
- [ ] **Schritt 3: Commit** `feat(core): vollständiger Neuschrieb in der Prüfkette des Speicherns`.

### Aufgabe 5: Sitzung übernimmt ein umgeschriebenes Dokument

**Dateien:** Ändern `src/Pagewing.Core/Viewing/ViewerSession.cs`; Test `tests/Pagewing.Core.Tests/Viewing/ViewerSessionRewriteTests.cs`.

**Schnittstellen – verbraucht:** `ViewerSession.AdoptSavedDocumentAsync` (gleiche Seitenzahl, gleiche `Cache`-Instanz) als Muster. **Erzeugt:** `public async Task<bool> AdoptRewrittenDocumentAsync(PdfDocument reopened, string path, PdfDocument? replacing = null)` (neue `RenderCache`-Instanz, neuer Planer und Seitenvorschau, Seitengrößen neu in Stapeln, `OpenedStamp` neu gelesen, Ereignis `Changed`; gilt dieselben Regeln gegen Rennen wie `AdoptSavedDocumentAsync`; die alte Instanz wird freigegeben).

- [ ] **Schritt 1: Tests.** `AdoptRewrittenReplacesDocumentCacheAndSizes` (anderes `PageCount`, `Cache` ist eine neue Instanz, alte freigegeben, `PageSizes` des neuen Dokuments), `AdoptRewrittenIgnoresASupersededDocument` (`replacing` nicht mehr offen → `false`, `reopened` geschlossen), `AdoptRewrittenAfterDisposeDoesNothing`, `AdoptRewrittenRaisesChangedOnceAndNeverLeavesReady`, `ThumbnailsRestartForTheNewDocument`.
- [ ] **Schritt 2: Implementieren** (rot → grün) mit demselben Sperrschema wie `AdoptSavedDocumentAsync`; `dotnet test tests/Pagewing.Core.Tests --filter-namespace "*Viewing"`.
- [ ] **Schritt 3: Commit** `feat(core): Sitzung übernimmt umgeschriebene Dokumente`.

### Aufgabe 6: Tab-Zustand, Modus „Organisieren“, Sperren

**Dateien:** neu `src/Pagewing.App/ViewModels/TabViewModel.Pages.cs`; ändern `TabViewModel.cs`/`TabViewModel.Annotations.cs` (`AsksBeforeClosing`, `CanRunMarkup`, `CanRunEdit`, `SaveBlockedReason`), `Commands/CommandCatalog.cs`, `Resources/Strings.*.resx`, `Strings.cs`; Test `tests/Pagewing.App.Tests/PagesOrganizeModelTests.cs`.

**Schnittstellen – verbraucht:** `PageEditPlan`, `PageEditHistory`. **Erzeugt:** auf `TabViewModel`: `bool IsOrganizing`, `PageEditHistory? PageHistory` (entsteht beim ersten Gebrauch), `bool HasPendingPageEdits`, `IReadOnlySet<int> SelectedPagePositions`, Befehle `OrganizeCommand`, `RotateSelectionCommand(int degrees)`, `DeleteSelectionCommand`, `DuplicateSelectionCommand`, `InsertBlankPageCommand`, `UndoPagesCommand`, `RedoPagesCommand`, `DiscardPageEditsCommand`, `string? PageEditsBlockedReason` (Grund für gesperrte Aktionen, lokalisiert); Katalogeinträge `pages.organize` und die Modusbefehle (Startkürzel siehe Tabelle).

- [ ] **Schritt 1: Tests.** `OrganizingNeedsASharpPageFirst` (erst nach `HasShownSharpPage`), `EditsAreStagedAndTheDocumentStaysUnchanged` (`PageCount` des Tabs bleibt), `PendingPlanLocksAnnotateAndFillModes` (Grund sichtbar), `PendingPlanCountsAsUnsavedWhenClosing` (`AsksBeforeClosing`, Frage „Seitenänderungen verwerfen?“), `PendingPlanSurvivesMovingTheTabToANewWindow` (Plan wandert mit), `ExtractSplitAndExportAreDisabledWhilePlanOrChangesArePending`, `DeletingTheLastPageIsRefusedWithReason`, `UndoRedoFollowTheHistory`, `ShortcutsDoNotCollide` (`CommandCatalogTests` um die neuen Gesten erweitern; Erreichbarkeit auf deutscher Tastatur ohne AltGr), `NothingIsCreatedAtStartup` (Plan, Verlauf, Befehle entstehen erst beim Modus; `MeasurementRun` unverändert).
- [ ] **Schritt 2: Implementieren** (rot → grün); `dotnet test tests/Pagewing.App.Tests --filter-class "*PagesOrganizeModelTests"` und `*CommandCatalogTests`.
- [ ] **Schritt 3: Commit** `feat(app): Modus Organisieren mit Seitenplan am Tab`.

### Aufgabe 7: Seitenleiste im Organisiermodus

**Dateien:** ändern `src/Pagewing.App/Views/PagesPanel.axaml(.cs)`, `Controls/ThumbnailList.cs`; neu `Views/PagesOrganizeBar.axaml(.cs)`; Test `tests/Pagewing.App.Tests/PagesOrganizePanelTests.cs`.

**Schnittstellen – verbraucht:** `TabViewModel.*` (Aufgabe 6), `ThumbnailSource` des Tabs. **Erzeugt:** Mehrfachauswahl (Strg/Umschalt) in `ThumbnailList` im Modus; Ziehen mit Einfügemarke (`Move`), Tastatur (Verschieben um eine Position, Entf, Drehen, Einfügen), Kontextmenü; Zeichnen der Planliste: Miniatur aus `ThumbnailSource` nach Quellindex, Drehung beim Zeichnen angewandt, leere Seite gezeichnet statt gerendert, Fremdseiten über eine eigene Quelle je eingefügter Datei; Kennzeichnung mit Symbol **und** Text (gedreht, neu, leer, aus anderer Datei), Zähler „12 → 10 Seiten“.

- [ ] **Schritt 1: Tests (Headless).** `ModeShowsTheToolbarOnlyWhileOrganizing` (außerhalb des Modus unveränderte Liste, keine Werkzeuge), `MultiSelectWithCtrlAndShift`, `DragMovesPagesAndShowsTheInsertionMarker`, `KeyboardOnlyUseWorks` (Auswahl, Verschieben, Löschen, Drehen, Einfügen, Anwenden, Verwerfen, Tab verlässt die Liste ohne Falle), `PageUpDownAndEscKeepTheirRules`, `RotatedPageIsDrawnRotatedAndLabelledWithTextNotColour`, `CounterShowsOldAndNewPageCount`, `ThousandPagesStillScroll` (`thousand-pages.pdf`, nur realisierte Einträge planen), `AutomationNamesExistForEveryButton`, `ColoursMeetAA` (neue Farbpaare nach `TokenTests`-Muster), `TextsExistInBothLanguages`.
- [ ] **Schritt 2: Implementieren** (rot → grün), Layout nach Designsystem 7.x, keine Literale; `dotnet test tests/Pagewing.App.Tests --filter-class "*PagesOrganizePanelTests"` und `*PagesPanelTests`.
- [ ] **Schritt 3: Commit** `feat(app): Seitenleiste mit Organisiermodus`.

### Aufgabe 8: Anwenden, Karten und Strg+S

**Dateien:** neu `src/Pagewing.App/ViewModels/MainViewModel.Pages.cs`, `Views/ApplyPagesCard.axaml(.cs)`, `Views/MainWindow.PagesCards.cs`; ändern `PendingSave.cs` (neue Arten `ApplyPages`, `SaveAnnotationsFirst`), `MainViewModel.Save.cs` (Strg+S mit offenem Plan öffnet die Karte), `Strings.*`; Test `tests/Pagewing.App.Tests/ApplyPagesTests.cs`.

**Schnittstellen – verbraucht:** `PageEditExecutor.BuildAsync`/`AnalyzeAsync`/`FingerprintsAsync`, `DocumentSaver.RewriteAsync`, `ViewerSession.AdoptRewrittenDocumentAsync`, `MainViewModel.PickTargetAsync`, `AskAboutSignaturesAsync`-Muster. **Erzeugt:** `internal Task<bool> MainViewModel.ApplyPageEditsAsync(TabViewModel tab)`; Testnaht `MainViewModel.PageRewriter` (ersetzt den Schreibweg wie `Saver`); Karte `ApplyPagesCard` (Zusammenfassung „12 → 10 Seiten, 3 gedreht, 1 Datei eingefügt“, Verlustliste, Signaturwarnung, Standardknopf „Speichern unter …“, „In der Originaldatei speichern“, „Zurück“, Satz „Der Neuschrieb lässt sich nicht rückgängig machen.“).

- [ ] **Schritt 1: Tests.** `ApplyAsksToSaveAnnotationsFirstWhenDirty` (Ablehnen: Plan bleibt), `CardShowsSummaryAndEveryLossOfTheReport`, `DefaultButtonIsSaveAs` (Fokus und Standard), `SignedDocumentShowsTheWarningAndNeverSaysValid`, `ApplyWritesAndTheTabAdoptsTheNewDocument` (Seitenzahl, Position folgt `FollowPage`, Plan leer, Annotationsverlauf leer, Panel bleibt), `ApplyFailureKeepsThePlanAndTheTarget` (Quelle geändert, Platte voll, Verschlüsselung nicht erhalten: Karte mit Grund, Plan bleibt), `CtrlSOpensTheApplyCardWhileAPlanIsPending`, `ApplyIsBlockedWhilePrintingAndViceVersa` (`SaveBlockedReason`), `UpdatePathAsksAboutAPendingPlan`, `NoTextCallsAnythingLegallyBindingOrValid` (nach `SaveCardsTests`-Muster), `EscCancelsTheCardNotThePlan`.
- [ ] **Schritt 2: Implementieren** (rot → grün); `dotnet test tests/Pagewing.App.Tests --filter-class "*ApplyPagesTests"` und `*SaveCardsTests`, `*SaveCommandTests`, `*CloseWithChangesTests` bleiben grün.
- [ ] **Schritt 3: Commit** `feat(app): Seitenänderungen anwenden mit Prüfkarte`.

### Aufgabe 9: CLI `rotate` und der Rahmen für schreibende Befehle

**Dateien:** ändern `src/Pagewing.Cli/CliApp.cs`, `CliOptions.cs`; neu `Commands/RotateCommand.cs`, `Commands/OutputGuard.cs`; Test `tests/Pagewing.Core.Tests/Cli/RotateCommandTests.cs`.

**Schnittstellen – verbraucht:** `PageEditPlan.Rotate`, `PageEditExecutor`, `DocumentSaver.RewriteAsync`, `PageRange.Parse`. **Erzeugt:** `internal static class OutputGuard` (`static void Check(string input, string output, bool force)` → `CliException` bei gleichem Pfad oder vorhandener Datei ohne `--force`); Befehl `pagewing rotate <file> --pages <range> --angle <90|180|270|-90> --output <out.pdf> [--force] [--password <pw>]`; `Usage`/`Help` ergänzt; `CliApp` leitet Befehle, die mehrere Dateien öffnen (`merge`), vor dem Öffnen einer einzigen Datei weiter.

- [ ] **Schritt 1: Tests.** `RotateTurnsOnlyTheGivenPages` (`pages-marked.pdf`, effektive Drehung, andere Seiten unverändert, Seitentext gleich), `RotateRefusesExistingOutputWithoutForce` (Exit 1, Ausgabe unverändert), `RotateRefusesSameInputAndOutput`, `RotateKeepsOutlineAndLabels`, `RotateOfEncryptedFileNeedsThePasswordAndKeepsEncryption` (`PAGEWING_PASSWORD`), `RotateRejectsBadAngleAndRange` (Exit 2/1 mit Meldung), `RotateOfSignedFilePrintsTheWarningOnStderr` (Verlustliste der Aufgabe 3 als Text), `CancelLeavesNoTemporaryFile`.
- [ ] **Schritt 2: Implementieren** (rot → grün); `dotnet test tests/Pagewing.Core.Tests --filter-namespace "*Cli"`; `dotnet run tools/smoke-cli.cs -- <pagewing.exe>` nach dem Publish in Aufgabe 15.
- [ ] **Schritt 3: Commit** `feat(cli): rotate und gemeinsamer Ausgabeschutz`.
- [ ] **Schritt 4 (Zwischenstand Stufe 1):** Build, Test, Format grün; Roadmap-Aufgaben „Umsortieren, drehen, löschen, einfügen in der Seitenleiste“ (ohne „einfügen aus Datei“) noch **nicht** abhaken, solange Aufgabe 10 fehlt.

---

## Stufe 2

### Aufgabe 10: Datei einfügen und Zusammenführen

**Dateien:** neu `Views/InsertFileCard.axaml(.cs)`, `ViewModels/InsertFileViewModel.cs`; ändern `TabViewModel.Pages.cs`, `MainViewModel.Pages.cs`, `Strings.*`; Test `tests/Pagewing.App.Tests/InsertFileTests.cs`.

**Schnittstellen – verbraucht:** vorhandener Öffnen-Dialog (`_picker`), Passwortabfrage, `PageRange.Parse`, `PageEditPlan.InsertFrom`, `PageSourceFile`. **Erzeugt:** `TabViewModel.InsertFileCommand`; `InsertFileViewModel` (Datei, Passwort, Bereich als Text, Position); `TabViewModel.InsertedSources` (`PageSourceId` → geöffnetes `PdfDocument` + `PageSourceFile`), vom Tab freigegeben bei Verwerfen, Anwenden und Schließen.

- [ ] **Schritt 1: Tests.** `InsertAppendsTheSelectedRangeAfterTheSelection`, `RangeSyntaxIsThePrintSyntax` (`1-3,5`, `all`, Fehlermeldung bei `5-3`, `0`, `1-2000000000` ohne Speicherstau), `EncryptedFileAsksForThePasswordAndWrongPasswordIsReported`, `NotAPdfIsReported` (`not-a-pdf.pdf`), `TwentyFirstFileIsRefused`, `InsertedSourceChangedBeforeApplyFailsWithReasonAndKeepsThePlan` (Quelle überschrieben/gelöscht), `ForeignThumbnailsComeFromTheirOwnSource`, `InsertedFilesAreClosedOnDiscardAndClose`, `MergeResultHasPagesOfBothFilesInOrder` (Anwenden, Text je Seite).
- [ ] **Schritt 2: Implementieren** (rot → grün); `dotnet test tests/Pagewing.App.Tests --filter-class "*InsertFileTests"`.
- [ ] **Schritt 3: Commit** `feat(app): Seiten aus einer Datei einfügen`.

### Aufgabe 11: Teilen und Extrahieren (Core)

**Dateien:** neu `src/Pagewing.Core/Pages/OutputNames.cs`, `DocumentSplitter.cs`; Tests `tests/Pagewing.Core.Tests/Pages/OutputNamesTests.cs`, `DocumentSplitterTests.cs`.

**Schnittstellen – verbraucht:** `PageEditExecutor`, `DocumentSaver.RewriteAsync`, `GetOutlineAsync`, `PageRange`. **Erzeugt:** `static class OutputNames` (`static string Sanitize(string title, int maxLength = 80)`, `static IReadOnlyList<string> Unique(IEnumerable<string> stems, string extension, Func<string, bool> exists)`); `static class DocumentSplitter` (`IReadOnlyList<SplitPart> Plan(int pageCount, SplitMode mode, ...)` mit `SplitPart(string Stem, IReadOnlyList<int> Pages)`; Modi `Every(int n)`, `Ranges(string text)`, `Bookmarks(IReadOnlyList<PdfOutlineItem> outline)` nach Spec 5.6 (oberste Ebene, Teile ab Zielseite sortiert, Vorspann als erster Teil, gleiche Zielseite ein Teil, ohne Lesezeichen → `PageEditException`/`CliException`-taugliche Ausnahme); `Task<IReadOnlyList<string>> WriteAsync(PdfDocument source, PageSourceFile file, IReadOnlyList<SplitPart> parts, string directory, bool overwrite, IProgress<double>?, CancellationToken)`); Extrahieren = ein `SplitPart` mit der Auswahl.

- [ ] **Schritt 1: Tests.** `SanitizeRemovesSeparatorsReservedNamesAndControlCharacters` (`..\..\x`, `a/b`, `CON`, `NUL.pdf`, `aux`, Punkte/Leerzeichen am Ende, leer → `part`), `SanitizeLimitsTheLengthByTextElements`, `UniqueNumbersEqualAndExistingNamesAndNeverOverwrites`, `EveryNSplitsWithARemainder`, `RangesSplitAsGiven` (Überlappungen erlaubt), `BookmarksSplitAtTopLevelTargetsSorted` (`pages-marked.pdf`: Teile 1–2, 3–4, 5–6), `BookmarksWithoutTopLevelFail`, `BookmarksOnTheSamePageFormOnePart`, `PagesBeforeTheFirstBookmarkFormTheFirstPart`, `WriteCreatesFilesInsideTheFolderOnly` (feindliche Titel), `WriteRefusesExistingFilesWithoutOverwrite`, `WrittenPartsHaveThePagesOfTheirRange` (Text je Seite), `SplitKeepsOutlineLabelsOfThePrimaryWhereTheSpikeSaysSo`.
- [ ] **Schritt 2: Implementieren** (rot → grün; jedes Teil über Arbeitskopie + `RewriteAsync`, Fortschritt, Abbruch); `dotnet test tests/Pagewing.Core.Tests --filter-namespace "*Pages"`.
- [ ] **Schritt 3: Commit** `feat(core): Teilen und Extrahieren mit sicheren Dateinamen`.

### Aufgabe 12: Bilderexport (Core, gemeinsam mit `render`)

**Dateien:** neu `src/Pagewing.Core/Pages/PageImageExporter.cs`; ändern `src/Pagewing.Cli/Commands/RenderCommand.cs`; Test `tests/Pagewing.Core.Tests/Pages/PageImageExporterTests.cs`.

**Schnittstellen – verbraucht:** `PdfDocument.RenderPageAsync`, `PngEncoder.Encode`. **Erzeugt:** `static Task<IReadOnlyList<string>> PageImageExporter.ExportAsync(PdfDocument document, string stem, IReadOnlyList<int> pages, double scale, bool annotations, string directory, bool overwrite, IProgress<double>?, CancellationToken)` (Namen `<stem>-p<NNN>.png` mit Stellenzahl wie `RenderCommand`, Obergrenze 64 Megapixel je Seite → neue `PageExportException(int page, string message)`).

- [ ] **Schritt 1: Tests.** `ExportWritesOneSizedPngPerPageWithTheRenderNames` (gleiche Dateinamen und Bytes wie `pagewing render`), `ExportRefusesPagesAboveTheMegapixelLimit` (Seite mit riesigem MediaBox bei hoher dpi), `ExportDoesNotOverwriteWithoutPermission`, `ExportCanBeCanceledBetweenPages`, `RenderCommandStillPrintsThePaths` (vorhandene Tests bleiben grün).
- [ ] **Schritt 2: Implementieren** (rot → grün; `RenderCommand` ruft den Exporter, Verhalten unverändert); `dotnet test tests/Pagewing.Core.Tests --filter-namespace "*Cli"` und `*Pages*`.
- [ ] **Schritt 3: Commit** `refactor(core): Bilderexport gemeinsam für render und export`.

### Aufgabe 13: Karten für Extrahieren, Teilen und Bilderexport

**Dateien:** neu `Views/SplitCard.axaml(.cs)`, `Views/ExportImagesCard.axaml(.cs)`, `ViewModels/SplitViewModel.cs`, `ExportImagesViewModel.cs`; ändern `MainViewModel.Pages.cs`, `CommandCatalog.cs` (Befehle im Panelmenü, nicht im Lesen), `Strings.*`; Test `tests/Pagewing.App.Tests/SplitAndExportCardTests.cs`.

**Schnittstellen – verbraucht:** `DocumentSplitter`, `OutputNames`, `PageImageExporter`, `TabViewModel.PageEditsBlockedReason`. **Erzeugt:** Befehle `ExtractSelectionCommand`, `SplitCommand`, `ExportImagesCommand` am Tab; Karten mit Zielordner (Ordnerwahl über den vorhandenen Picker), Fortschritt und Abbruch.

- [ ] **Schritt 1: Tests.** `ActionsAreDisabledWithReasonWhilePlanOrChangesArePending`, `ExtractWritesTheSelectedPagesAsANewFileAndLeavesTheOriginal`, `SplitCardOffersEveryNRangesAndBookmarks` (Lesezeichen nur wählbar mit Lesezeichen), `ExportCardUsesDpiAndSelection` (PNG, Standard 144 dpi), `ProgressAndCancelWork`, `NothingIsOverwrittenWithoutAnExplicitChoice`, `KeyboardOnlyUseWorks`, `TextsExistInBothLanguages`.
- [ ] **Schritt 2: Implementieren** (rot → grün); `dotnet test tests/Pagewing.App.Tests --filter-class "*SplitAndExportCardTests"`.
- [ ] **Schritt 3: Commit** `feat(app): Extrahieren, Teilen und Bilder exportieren`.

### Aufgabe 14: CLI `merge`, `split`, `export` (und `pages`, wenn billig)

**Dateien:** neu `Commands/MergeCommand.cs`, `SplitCommand.cs`, `ExportCommand.cs`, `PagesCommand.cs` (nur falls der Ausführer es ohne Umbau erlaubt, sonst Backlog); ändern `CliApp.cs`, `CliOptions.cs`; Tests `tests/Pagewing.Core.Tests/Cli/MergeCommandTests.cs`, `SplitCommandTests.cs`, `ExportCommandTests.cs`.

**Schnittstellen – verbraucht:** `OutputGuard`, `PageEditPlan.InsertFrom`, `PageEditExecutor`, `DocumentSplitter`, `PageImageExporter`. **Erzeugt:** `pagewing merge --output <out.pdf> <file> [--pages <r>] [--password <pw>] <file> ... [--force]` (Optionen nach einer Datei gelten für diese; Umgebungsvariable `PAGEWING_PASSWORD` als Rückfall für alle); `pagewing split <file> --output <dir> (--every <n> | --ranges "…" | --bookmarks) [--force]`; `pagewing export <file> [--pages <r>] [--dpi <n>] [--output <dir>] [--no-annotations] [--force]` (nur Bilder); Ausgaben englisch und stabil, Verlustliste auf stderr, Exit-Codes 0/1/2.

- [ ] **Schritt 1: Tests.** `MergeConcatenatesFilesWithRangesPerFile` (Text je Seite), `MergeKeepsOutlineOfTheFirstFileAndPrintsTheLossOfTheOthers`, `MergeWarnsAboutFormFieldNameCollisions`, `MergeNeedsAtLeastTwoFilesAndAnOutput` (Exit 2), `MergeRefusesExistingOutputWithoutForce`, `MergeWithWrongPasswordNamesTheFile`, `SplitEveryTwoWritesThreeFiles`, `SplitBookmarksWritesNamedFilesAndRefusesNone`, `SplitNeverWritesOutsideTheFolder` (feindliche Titel), `ExportWritesPngWithRenderNamesAndForceRules`, `HelpTextListsTheNewCommands`, `CtrlCCancelsWithoutTemporaryFiles`.
- [ ] **Schritt 2: Implementieren** (rot → grün); `dotnet test tests/Pagewing.Core.Tests --filter-namespace "*Cli"`.
- [ ] **Schritt 3: Commit** `feat(cli): merge, split und export`.

### Aufgabe 15: Gesamtprüfung, Dokumentation, Roadmap

**Dateien:** `Docs/DECISIONS.md`, `CLAUDE.md`, `CHANGELOG.md` (`[Unreleased]`), `README.md` (CLI), `Docs/roadmap.json` + `Docs/ROADMAP.md`, `packaging/README.md` (Handprüfpunkte), `src/Pagewing.Cli` Hilfetext.

- [ ] **Schritt 1: Messungen.** `PagesOrganizeMeasurementTests` (Info, nur mit `PAGEWING_MEASURE_OUT`): Organisiermodus mit `thousand-pages.pdf`, Anwenden bei 380-MiB-Datei nur als Hinweis; Startzeit-Check lokal (`--measure-startup`) unverändert; nichts im Startpfad (Test `PagesStartPathTests`: ohne Gebrauch entsteht kein Plan, keine Arbeitskopie, kein Dialog).
- [ ] **Schritt 2: Dokumentation.** `DECISIONS`-Einträge: Spike und QPDF-Entscheidung (aus Aufgabe 1), „Plan statt Sofort-Änderung (M13)“, „Arbeitskopie statt Neuaufbau, primäre Datei (M13)“, „Neuschrieb-Modus der Prüfkette (M13)“, „Verlustliste und Obergrenzen (M13)“, „Messwerte (M13)“; `CLAUDE.md` Abschnitt „Seiten organisieren (seit M13)“ (Wo-was-liegt, Stolperfallen); `CHANGELOG.md`; `README.md`; Backlog-Einträge in `roadmap.json` für alle Nicht-Ziele (Spec 3: Zuschneiden, Wasserzeichen, Kopf-/Fußzeilen, Seitenzahlen, Beschriftungen bearbeiten, Bilder als Seiten, JPEG-Export, Dialog „Dateien zusammenführen“, Ziehen zwischen Tabs/Fenstern, Metadaten bearbeiten, Einfließen ungespeicherter Änderungen, „entschlüsselt speichern“, Duplikat-Felder umbenennen); Handprüfpunkte (Ergebnisdateien in Acrobat Reader, Linux-Läufe) nach `packaging/README.md` und in den Block von `v1.0.0`.
- [ ] **Schritt 3: Vollständiger Nachweis** (Befehl und Ergebnis in den Nachweis): `dotnet build Pagewing.slnx` (0 Warnungen), `dotnet test Pagewing.slnx` (Debug und `-c Release`), `dotnet format Pagewing.slnx --verify-no-changes`, `dotnet run tools/make-testpdfs.cs -- check`, `dotnet run tools/licenses.cs -- check`, `dotnet run tools/package.cs -- selftest`, `dotnet run tools/roadmap.cs -- generate` dann `-- check`, Native-AOT-Publish von App und CLI (`dotnet publish src/Pagewing.App -c Release -r win-x64`, analog CLI) ohne Warnungen und `dotnet run tools/smoke-cli.cs -- <pagewing.exe>`.
- [ ] **Schritt 4: Roadmap.** Aufgaben und Abnahmekriterium von Milestone 13 mit Nachweis abhaken, Status `✅ fertig` mit Datum, `generate`, `check`; Commit `docs: Milestone 13 abgeschlossen`; PR nach `dev` (Squash), CI grün abwarten, selbst mergen.

---

## Selbstprüfung gegen die Spec

| Spec | Aufgabe |
|---|---|
| 5.1 Plan, Verlauf | 2 |
| 5.2 Arbeitskopie, Ausführen, Neuschrieb, Übernahme | 1, 3, 4, 5, 8 |
| 5.3 Oberfläche (Modus, Sperren, Anwenden-Karte, Schließen, Datei einfügen, Extrahieren/Teilen/Export) | 6, 7, 8, 10, 13 |
| 5.4 Zusammenführen, Teilen, Export, Obergrenzen | 3, 10, 11, 12 |
| 5.5 Verlustliste, Signatur, Verschlüsselung | 3, 4, 8 |
| 5.6 CLI | 9, 14 |
| 6 Spike und QPDF-Entscheidung | 1 |
| 8 Tests, Startzeit, Messung | alle, 15 |
| 9 Doku | 15 |
