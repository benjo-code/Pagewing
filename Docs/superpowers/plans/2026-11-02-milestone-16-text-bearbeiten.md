# Milestone 16: Text bearbeiten – Implementierungsplan

> **Für ausführende Agenten:** PFLICHT-SKILL: `superpowers:subagent-driven-development` (empfohlen) oder `superpowers:executing-plans`, Aufgabe für Aufgabe. Schritte sind Checkboxen (`- [ ]`).

**Ziel:** Eine vorhandene Textzeile (ein „Lauf“) lässt sich im PDF ändern: Pagewing ersetzt das Textobjekt, behält die Schrift, wo sie die neuen Zeichen trägt, setzt fehlende Zeichen in Geist, sagt das vorher und nimmt nie still ein Zeichen weg. Rückgängig, Speichern, Hinweise auf die Grenzen.

**Architektur:** `Pagewing.Core.TextEdit` (neu, UI- und PDFium-frei) entscheidet (Zeichenabdeckung, Ersatz, Überlappung, Sperrgründe). `PdfDocument.TextEdit.cs` und `Pagewing.Pdfium/PdfiumTextEdit` lesen Läufe und ersetzen Textobjekte in einem kurzen Auftrag (Bausteine von `PdfiumRedactionNative`, `PdfiumGlyphs`, `PdfiumOcrLayer`). Ein Bearbeiten ist ein Schritt im gemeinsamen Verlauf (`AnnotationCommand.TextEdit`) und wird inkrementell gespeichert. Die App bekommt einen eigenen Modus mit Umriss, Eingabefeld über dem Lauf und Hinweisleiste.

**Tech Stack:** .NET 10 Native AOT, Avalonia 12.1, PDFium über eigene Bindings (experimentelle Textfunktionen schon gebunden), Geist Regular (im Core eingebettet), xunit.v3, Avalonia.Headless.

**Spec:** [2026-11-02-m16-text-bearbeiten-design.md](../specs/2026-11-02-m16-text-bearbeiten-design.md) (maßgeblich; „Spec 3.3“ unten sind ihre Abschnitte). Rahmen: `CLAUDE.md` (Blöcke „Speichern und Annotationen“, „Zeichnen“ mit Textfeld, „Formulare“, „Schwärzen“, „OCR“).

## Globale Randbedingungen

- PDFium nur über den `PdfiumThread` in kurzen Aufträgen (`Background` zum Lesen, `Interactive` für das Ersetzen nach Nutzeraktion, Abbruch zwischen Läufen); der UI-Thread wartet nie. **Kein neuer `[UnmanagedCallersOnly]`-Rückruf.** `CULong` für `unsigned long`. Koordinaten nur über `FPDF_PageToDevice`/`DisplayTransform`, keine eigene Rotations- oder CropBox-Rechnung.
- Nichts im Startpfad: Modus, Läufe, Overlay, Eingabefeld, Hinweisleiste und die Geist-Glyphentabelle entstehen erst beim Gebrauch; Messläufe öffnen den Modus nie (`TextEditStartPathTests`, Muster `RedactionStartPathTests`). Startzeit-Budgets nie lockern.
- Nie ein Zeichen still ersetzen, weglassen oder als `.notdef` zeichnen: Fehlt es in der vorhandenen Schrift **und** in Geist, wird der neue Text als Ganzes abgelehnt (Spec 3.3). Der alte Text wird ersetzt, **nie** überdeckt (kein weißes Rechteck, kein zweites Textobjekt). Geprüft werden nur neu getippte Zeichen; unveränderte bleiben in ihrer Schrift.
- Ersatzschrift ist **Geist Regular, nichts anderes** (`TextBoxFont`, `TextBoxCharacters`), nur eingebettet, wenn ein Lauf sie braucht (rund 25 KB nach Flate); keine Systemschrift. „Ganze Zeile in Ersatzschrift“ nie vorgewählt und nur, wenn alle Zeichen des Laufs in Geist stehen.
- Einheit ist der Lauf (gleiches Schriftobjekt, Größe, Farbe, Matrix, eine Zeile), als Ganzes ersetzt; der Rest hinter der Änderung rückt um die Breitendifferenz mit; Startpunkt bleibt; kein Umbruch. Zeichen je Lauf höchstens **2.000**.
- Gesperrt, mit sichtbarem Grund (nie still): unsichtbarer Text (Darstellungsmodus 3, auch die OCR-Ebene), Type-3-Schrift, senkrechte Schreibrichtung, nicht sicher neu schreibbarer Darstellungsmodus, geteiltes oder (je nach Spike) verschachteltes Form-XObject, `/ActualText`/`/Alt`/`/E`, nicht speicherbare Dokumente (`EncryptedNotSupported`, `SourceModified`).
- Speichern inkrementell über `DocumentSaver.SaveAsync`; der alte Text bleibt in früheren Fassungen, die Speicherkarte nennt das **einmal je Tab und Dokument als Hinweis ohne Bestätigung**, die Hinweisleiste kurz dauerhaft. Kein Text sagt „entfernt“, „gelöscht“, „sicher“, „rechtsgültig“ über den alten Text oder eine Signatur „gültig“. Keine Absatz-Versprechen in UI oder Doku.
- Bedienung: Enter übernimmt, Esc verwirft (`DocumentView.TryEscape`, nicht im Katalog), Tab fängt den Fokus nie (WCAG 2.1.2); Kürzel nur im `CommandCatalog` (Strg+Umschalt+E ist `annotate.ellipse`); Text im offenen Feld ist ungespeichert und wird vor Speichern, Drucken, Modusende, Schließen und Update übernommen; was nicht geht, bleibt mit Grund im Feld.
- UI-Texte Englisch **und** Deutsch (`Strings.en.resx`, `Strings.de.resx`, `Resources/Strings.cs`); Zustände nie nur über Farbe; WCAG 2.2 AA; keine Farb- und Maßliterale im XAML.
- Code und Kommentare Englisch; Doku und Commits Deutsch (Conventional Commits, gezielt stagen, nie `git add .`, danach `git show --stat HEAD`), Trailer `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`, nur auf `feature/m16-text-bearbeiten`. Testnamen ohne Unterstriche. Tests arbeiten auf Kopien im Temp-Ordner, nie im echten Benutzerordner (`UserSettings.Detached()`).
- Befehle: `dotnet build Pagewing.slnx`, `dotnet test Pagewing.slnx`, `dotnet format Pagewing.slnx --verify-no-changes`, `dotnet run tools/roadmap.cs -- check`, `dotnet run tools/make-testpdfs.cs -- check`; einzeln `dotnet test tests/Pagewing.Core.Tests --filter-class "*Name"`.

## Festgelegte Namen

| Ort | Name und Gestalt |
|---|---|
| `Pagewing.Core.TextEdit` | `enum TextRunFontKind { Standard14, EmbeddedFull, EmbeddedSubset, NotEmbedded, Type3 }`; `enum TextEditLockReason { InvisibleText, Type3Font, VerticalWriting, UnsupportedRenderMode, SharedFormXObject, NestedFormXObject, ActualText, NotSavable }`; `sealed record TextRun(int Page, int ObjectIndex, string Text, PageRect Bounds, string FontName, TextRunFontKind FontKind, double FontSize, AnnotationColor Color, string Fingerprint, TextEditLockReason? Lock)` (`PageRect` wie bei `TextBoxSpec`); `enum TextEditMode { KeepFont, WholeRunInGeist }` |
| Entscheidung | `interface IFontCoverage { bool Covers(int codePoint); }`; `static TextEditCheck.Evaluate(string oldText, string newText, IFontCoverage existing, TextEditMode mode)` → `TextEditDecision(TextEditOutcome Outcome, IReadOnlyList<TextEditSegment> Segments, IReadOnlyList<string> Missing /*fehlt in der vorhandenen Schrift*/, IReadOnlyList<string> Unsupported /*fehlt auch in Geist*/, bool WholeRunInGeistPossible)`; `enum TextEditOutcome { Unchanged, KeepFont, SubstituteMissing, WholeRunInGeist, Rejected }`; `sealed record TextEditSegment(string Text, bool InGeist)` |
| Platz | `[Flags] enum TextEditOverflow { None, OverlapsText, OutsidePage }`; `static TextEditOverlap.Find(PageRect newBounds, IReadOnlyList<PageRect> otherCharBoxes, PageRect pageBounds)` → `TextEditOverflow`; `static class TextEditLimits { const int MaxCharsPerRun = 2000; … }` |
| `PdfDocument` | `GetTextRunsAsync(int page, CancellationToken)` → `IReadOnlyList<TextRun>`; `ReplaceTextRunAsync(TextRun run, string newText, TextEditMode mode, CancellationToken)` → `TextReplaceResult(TextRun Before, TextRun? After, PageRect BoundsBefore, PageRect BoundsAfter)` (leerer Text entfernt den Lauf, `After == null`); wirft `TextRunStaleException` (Fingerabdruck stimmt nicht), `TextEditRejectedException(TextEditDecision)`, `TextRunLockedException(TextEditLockReason)` |
| Verlauf | `AnnotationCommand.TextEdit(int Page, TextRun Before, TextRun? After, string NewText, TextEditMode Mode)`; `AnnotationEditor.ApplyTextEditAsync(TextRun run, string newText, TextEditMode mode)` → `TextEditApplyResult?`; `AnnotationEditor.HasTextEdits` |
| App | `TabViewModel.TextEdit.cs` (`IsTextEditing`, `ToggleTextEditCommand`, `HasTextEditDraft`, `FlushTextEditDraft`), `Controls/TextEditOverlay`, `Views/TextRunEditor`, `Views/TextEditHintBar`, `Views/DocumentView.TextEdit.cs`; Katalog-Id `text.edit` |

## Review-Fokus

Eingaben, die die Spec nicht ausspricht, wahrscheinlichste zuerst; jede Zeile hat ihren Test in der genannten Aufgabe:

1. **Leerer Text:** Der Nutzer löscht alles im Feld. Entscheidung dieses Plans (Spec schweigt): leerer Text **entfernt den Lauf** als ein Verlaufsschritt (Rückgängig stellt ihn her), nie ein leeres Textobjekt, nie ein Fehler (Aufgabe 4, 5, 6).
2. **Unveränderter Text bestätigt (Enter ohne Änderung):** kein Verlaufsschritt, keine Änderung der Datei, kein „ungespeichert“ (Aufgabe 3, 6).
3. **Gedrehte Seite und gedrehter Text mit CropBox-Versatz:** Der neue Text steht aufrecht in Leserichtung, Rahmen und Überlappungshinweis stimmen (Aufgabe 4, 7).
4. **Veraltet:** Der Lauf wurde inzwischen durch Rückgängig, Seiten organisieren, Schwärzen oder OCR verändert, oder zwei Bearbeitungen kurz hintereinander: `TextRunStaleException`, nichts wird getroffen, das Feld bleibt mit Grund (Aufgabe 4, 5, 6).
5. **Lauf mit Zeichen, die Geist nicht hat (ł, →, kyrillisch), und 2.001 Zeichen / 10.000 Läufe auf einer Seite:** Tippfehler an anderer Stelle bleibt korrigierbar; Grenzen sperren mit Grund statt abzuschneiden (Aufgabe 3, 4, 7).

---

## Dateistruktur

| Datei | Verantwortung |
|---|---|
| `src/Pagewing.Core/TextEdit/` (neu) | Typen, Abdeckungsentscheidung, Überlappung, Grenzen; UI- und PDFium-frei |
| `src/Pagewing.Core/Pdf/PdfDocument.TextEdit.cs`, `src/Pagewing.Pdfium/PdfiumTextEdit.cs` (+ Spiegeltypen, ggf. `Native/Fpdf.cs` nur gebrauchte Funktionen) | Zugang zu PDFium: Läufe lesen, Textobjekt ersetzen |
| `src/Pagewing.Core/Annotations/AnnotationHistory.cs`, `AnnotationEditor.cs` (ändern) | Verlaufsschritt `TextEdit`, `HasTextEdits`, Größe |
| `src/Pagewing.App/` (`ViewModels/TabViewModel.TextEdit.cs`, `Controls/TextEditOverlay`, `Views/TextRunEditor*`, `TextEditHintBar*`, `DocumentView.TextEdit.cs`, `Commands/CommandCatalog.cs`, `Resources/*`) | Oberfläche |
| `tests/Pagewing.Core.Tests/TextEdit/`, `tests/Pagewing.App.Tests/TextEdit*Tests.cs`, `tools/make-testpdfs.cs` | Tests und Fixtures |

---

## Aufgabe 1: Fixtures

**Dateien:** Ändern `tools/make-testpdfs.cs`, `testdata/pdfs/LICENSES.md`, `tests/Pagewing.Render.Tests` (`RenderCorpus.Skipped`); neue PDFs `text-edit-subset.pdf` (TrueType-Teilmenge als Type0/Identity-H mit `/ToUnicode`, wie aus Word; dazu eine Type1-/einfache TrueType-Teilmenge mit `/Encoding`), `text-edit-standard.pdf` (Standard-14 ohne Einbettung), `text-edit-full.pdf` (vollständig eingebettet), `text-edit-styles.pdf` (mehrere Stile und `TJ`-Verschiebungen in einer Zeile), `text-edit-rotated.pdf` (`/Rotate 90`, CropBox-Versatz, gedrehter Text), `text-edit-locked.pdf` (unsichtbarer Text, Type 3, `/ActualText`, geteiltes und verschachteltes Form-XObject, senkrechte Schrift), `text-edit-hostile.pdf` (10.000 Läufe auf einer Seite, ein Lauf mit 2.001 Zeichen).

**Schnittstellen:** Produziert die Fixtures und `TextEditFixtures.KnownRuns` in `tests/Shared` (Datei → Seite, Text, Rechteck in Seitenpunkten oben links, Schrifttyp). Deterministisch, unkomprimiert, selbst erzeugt (Geist, OFL-Hinweis in `LICENSES.md`).

- [ ] **Schritt 1:** `dotnet run tools/make-testpdfs.cs -- check` schlägt fehl (Dateien fehlen); `EveryTestPdfIsInTheCorpusOrExplicitlySkipped` rot.
- [ ] **Schritt 2:** Erzeugen, `generate`, Einträge in `LICENSES.md` und `RenderCorpus.Skipped`.
- [ ] **Schritt 3:** `make-testpdfs -- check` und `dotnet test tests/Pagewing.Render.Tests` grün; Commit `test(text): Fixtures für die Textbearbeitung (M16)`.

## Aufgabe 2: Spike (Spec 4)

Klärt die sieben Fragen der Spec **vor** Aufgabe 4. Läuft ohne Oberfläche gegen die Fixtures aus Aufgabe 1.

**Dateien:** Neu `tests/Pagewing.Core.Tests/TextEdit/TextEditSpikeTests.cs` (Wächter laufen immer; Ablegen der Muster nur mit `PAGEWING_SPIKE_OUT=<Ordner>`, Muster `OcrLayerSpikeTests`), Prototyp im Testprojekt; Eintrag in `Docs/DECISIONS.md`.

**Schnittstellen:** Produziert die Entscheidung zu: (a) Lesen und Zusammenführen zu Läufen, Erkennen von Teilmenge/eingebettet/Standard, Erreichbarkeit von Text in Form-XObjects, Erkennen geteilter Objekte und `/ActualText`; (b) **Zeichenabdeckung** (wie `IFontCoverage` für Teilmengen entsteht; was `FPDFText_SetText` mit einem fehlenden Zeichen tut; `FPDFText_SetCharcodes` bei Identity-H); (c) Ersetzen (Reihenfolge, Abstände, Farbe, Marken); (d) zwei Schriften in einer Zeile; (e) was Rückgängig braucht und die Größe für die Obergrenze; (f) Dateigröße je Bearbeiten; (g) Chrome und Acrobat. Hängt (b) so, dass sich die Abdeckung nicht sicher bestimmen lässt, gilt der Rückfall der Spec 4 (nur Zeichen, die diese Schrift im Dokument schon trägt).

- [ ] **Schritt 1:** Wächtertests je Frage, Namen u. a.: `RunsAreMergedAcrossTJShifts`, `FontKindIsRecognisedForSubsetStandardAndFull`, `ASetTextWithAMissingGlyphIsDetectedByReadBack`, `UnchangedCharactersKeepTheirPositions`, `ReplacedTextIsInTheNewRevisionOnlyOnceInExtractionAndContentBytes`, `RotatedRunStaysUprightInReadingDirection`, `TwoFontsInOneLineAreSetAtTheRightPlace`, `SavedFileKeepsTheOriginalBytesAsPrefix`, `ExtractionOfTheSavedFileGivesTheNewText`.
- [ ] **Schritt 2:** `PAGEWING_SPIKE_OUT=<Ordner> dotnet test tests/Pagewing.Core.Tests --filter-class "*TextEditSpikeTests"` ausführen, Varianten bauen, bis die Tests entscheiden.
- [ ] **Schritt 3:** Die Muster `text-edit-*-edited.pdf` dem Projektinhaber zur Prüfung in Chrome, Acrobat Reader und Pagewing geben (Anzeige, Auswahl, Suche; kann Acrobat die Zeile weiter bearbeiten? ehrliche Antwort); Ablauf als Punkt in `packaging/README.md`. Aufgabe 3 hängt nicht davon ab und läuft währenddessen; Aufgabe 4 beginnt erst mit der Rückmeldung.
- [ ] **Schritt 4:** Entscheidung (a bis g, Rückfall ja/nein, endgültige Sperrgründe) in `Docs/DECISIONS.md`; Commit `docs: Spike Textbearbeitung (M16)`.

## Aufgabe 3: Kernentscheidungen (UI- und PDFium-frei)

**Dateien:** Neu `src/Pagewing.Core/TextEdit/{TextRun,TextRunFontKind,TextEditLockReason,TextEditMode,IFontCoverage,TextEditCheck,TextEditDecision,TextEditOverlap,TextEditLimits}.cs`; Tests `TextEditCheckTests`, `TextEditOverlapTests`.

**Schnittstellen:** Produziert die Typen der Tabelle (ohne PDFium-Zugang). `TextEditCheck.Evaluate` setzt Spec 3.3 um: nur neue Zeichen werden geprüft (Vergleich von `oldText` und `newText` als Zeichenfolge, Unicode-Textelemente, nicht UTF-16-Einheiten).

- [ ] **Schritt 1: Tests:** `TextEditCheckTests`: `SameTextIsUnchanged`, `AllNewCharactersInTheFontKeepTheFont`, `MissingCharactersAreSetInGeistAndNamed` (Segmente: nur das fehlende Zeichen `InGeist`), `WholeRunInGeistIsOfferedOnlyWhenEveryCharacterIsInGeist`, `AnExistingCharacterGeistLacksStaysInItsFont` (Lauf mit „ł“, Tippfehler anderswo → `KeepFont`), `ACharacterInNeitherFontRejectsTheWholeText` (`Unsupported` nennt es, keine Segmente), `NonLatinRunIsEditableWhileItsFontCoversTheNewCharacters`, `EmptyNewTextIsARemoval` (eigener Ausgang, kein `Rejected`), `SurrogatePairsAndCombiningMarksAreOneElement`, `MoreThan2000CharactersIsRejected`. `TextEditOverlapTests`: `LongerTextOverlappingANeighbourIsFlagged`, `TextOverTheRightPageEdgeIsOutsidePage`, `ShorterTextIsNone`, `TouchingBoxesAreNotOverlapping`.
- [ ] **Schritt 2–4:** rot, umsetzen, `dotnet test tests/Pagewing.Core.Tests --filter-namespace "*TextEdit"` grün, `dotnet build Pagewing.slnx` ohne Warnungen, Commit `feat(text): Entscheidung über Schrift, Ersatz und Platz (M16)`.

## Aufgabe 4: PDFium-Zugang

Beginnt erst mit der Rückmeldung aus Aufgabe 2.

**Dateien:** Neu `src/Pagewing.Core/Pdf/PdfDocument.TextEdit.cs`, `src/Pagewing.Pdfium/PdfiumTextEdit.cs` (Spiegeltypen wie bei Zeichnungen); ändern `src/Pagewing.Pdfium/Native/*` (nur gebrauchte Funktionen), `NativeBindingsTests`; Tests `PdfDocumentTextEditTests`.

**Schnittstellen:** Produziert `GetTextRunsAsync` und `ReplaceTextRunAsync` laut Tabelle. Ersetzen in **einem** kurzen `Interactive`-Auftrag: Fingerabdruck prüfen, Textobjekt neu aufbauen (Bausteine `PdfiumRedactionNative.ReadTextFacts`/`CreateRuns`, Geist über `PdfiumOcrLayer`-Schriftweg, einmal je Dokument, nur bei Bedarf), altes entfernen, **Rücklesen** (Text, Rahmen) und bei Abweichung den alten Zustand wiederherstellen und `TextEditRejectedException` werfen; Seite invalidieren (`InvalidatePages`).

- [ ] **Schritt 1: Tests:** `ReadsTheKnownRunsOfEveryFixture` (`TextEditFixtures.KnownRuns`), `LockedRunsCarryTheirReason` (je Grund der Fixture `locked`), `ReplacingWithPresentCharactersKeepsTheFont` (Schriftname gleich, Extraktion neu), `MissingCharactersUseGeistOnlyForThem`, `WholeRunInGeist`, `OldTextIsGoneFromTheNewRevisionNotJustCovered` (Extraktion und Seiteninhalt), `TheRestOfThePageRendersIdentical` (Render-Vergleich außerhalb des Vereinigungsrechtecks, Strict), `TheTailMovesByTheWidthDifference`, `EmptyTextRemovesTheRun`, `RotatedPageAndTextStayUpright`, `AStaleFingerprintIsRefusedAndNothingChanges`, `ARejectedTextLeavesTheDocumentByteIdentical`, `GeistIsEmbeddedOnlyWhenNeeded` (Dateigröße), `RunsAboveTheLimitAreLockedNotTruncated` (`hostile`), `InvisibleTextAndOcrLayerAreLocked`.
- [ ] **Schritt 2–4:** rot, umsetzen, `dotnet test … --filter-class "*PdfDocumentTextEditTests"` grün, Commit `feat(text): Läufe lesen und Textobjekte ersetzen (M16)`.

## Aufgabe 5: Verlauf, Speichern, Hinweis

**Dateien:** Ändern `AnnotationHistory.cs` (`AnnotationCommand.TextEdit`, Größenrechnung), `AnnotationEditor.cs` (`ApplyTextEditAsync`, `HasTextEdits`, `StepAsync`), `SignaturePolicy`/`SignatureAdvice` (Warnkarte nennt geänderten Text), Speicherkarte (Hinweis zum alten Text, Muster des Signaturhinweises); Tests `TextEditEditorTests`, `TextEditSaveTests`.

**Schnittstellen:** `ApplyTextEditAsync` ruft `ReplaceTextRunAsync`, erzeugt einen Schritt; Rückgängig stellt den Zustand laut Spike (e) wieder her, Wiederholen setzt `NewText` erneut; wirft `AnnotationHistoryFullException` **vor** der Änderung, wenn die Obergrenze (32 MiB) überschritten wird. Hinweis: `TabViewModel.TextEditNoticeShown` je Tab und Dokument, zurückgesetzt bei neuem Dokument.

- [ ] **Schritt 1: Tests:** `UndoRestoresTheOldTextAndRedoSetsTheNewOneAgain`, `UnchangedTextAddsNoStep`, `AnEditCountsAsAnUnsavedChange` (`IsDirty`, `AsksBeforeClosing`), `AStaleRunAfterUndoIsRefused`, `SaveIsIncrementalAndTheOriginalBytesStayThePrefix` (Muster `FormSaveTests`), `SavedFileGivesTheNewTextAfterReopen`, `HistoryIsEmptyAfterSaving`, `SignedDocumentWarnsAndNamesChangedText` (DocMDP 1 und 2), `SaveCardNotesTheOldTextOnceAndNeverSaysRemoved` (einmal je Tab und Dokument, kein „entfernt/gelöscht/sicher“, beide Sprachen), `EncryptedFileIsLockedWithAReason`.
- [ ] **Schritt 2–4:** rot, umsetzen, grün, Commit `feat(text): Textänderung als Verlaufsschritt, Speichern und Hinweis (M16)`.

## Aufgabe 6: Oberfläche

**Dateien:** Neu `ViewModels/TabViewModel.TextEdit.cs`, `Controls/TextEditOverlay`, `Views/TextRunEditor.axaml(.cs)` (Muster `TextBoxEditor`), `Views/TextEditHintBar.axaml(.cs)` (Muster `FormHintBar`), `Views/DocumentView.TextEdit.cs`; ändern `CommandCatalog.cs` (`text.edit`, freie Geste wählen und in `CommandCatalogTests` eindeutig halten), Sperren in `TabViewModel.Annotate/Forms/Pages/Redaction/Ocr` und `MainViewModel.Save/Print/Close`, Update-Weg, `DocumentView.TryEscape`, `Strings*.resx`, `Strings.cs`, Icon in `Icons.axaml`; Tests `TextEditUiTests`, `TextEditStartPathTests`, `CommandCatalogTests`.

**Schnittstellen:** Konsumiert `PdfDocument.GetTextRunsAsync`, `AnnotationEditor.ApplyTextEditAsync`, `TextEditCheck`, `TextEditOverlap`. Modus nur nach `HasShownSharpPage`, schließt Kommentieren, Ausfüllen, Organisieren, Schwärzen, OCR-Lauf aus und umgekehrt (Grund sichtbar). Läufe nur der sichtbaren Seiten plus Puffer, in kurzen Aufträgen. Feld zeigt live den Hinweis aus `TextEditDecision` („In dieser Schrift fehlen: …“, Schaltfläche „Ganze Zeile in Ersatzschrift“ nie vorgewählt) und `TextEditOverflow` (nicht blockierend).

- [ ] **Schritt 1: Tests:** `ModeNeedsAFirstSharpPageAndNeverOpensInMeasurementRuns`, `RunsGetAnOutlineOnHoverAndLockedRunsShowALockWithTheirReason` (Umriss, nie nur Farbe), `ClickOpensTheEditorOverRunAndEnterCommitsEscDiscards`, `TabNeverTrapsFocusAndArrowsMoveBetweenRuns`, `HintsNameMissingCharactersAndOverflowLive`, `RejectedTextStaysInTheFieldWithTheReason`, `ClearingTheFieldRemovesTheRun`, `OpenFieldCountsAsUnsavedAndIsFlushedBeforeSaveAndPrintAndClose`, `ModesExcludeEachOtherWithAVisibleReason`, `HintBarStatesLinesNotParagraphsInBothLanguages`, `ShortcutIsUniqueInTheCatalogAndListedInHelp`, `UndoRedoWorkFromTheKeyboard`, `AfterSaveTheViewKeepsPositionAndZoom` (Übernahme wie M9), `NothingOfTheFeatureIsCreatedBeforeUse`.
- [ ] **Schritt 2–4:** rot, umsetzen, grün (`dotnet test tests/Pagewing.App.Tests --filter-class "*TextEdit*"`), Sichtprüfung der Muster in Hell, Dunkel, Hochkontrast, „Kompakt“, Commit `feat(text): Modus Text bearbeiten in der App (M16)`.

## Aufgabe 7: Zusammenspiel, feindliche Eingaben, Messung

**Dateien:** Neu `tests/Pagewing.Core.Tests/TextEdit/TextEditInteractionTests.cs`, `TextEditHostileTests.cs`, `TextEditMeasurementTests.cs` (nur mit `PAGEWING_MEASURE_OUT`, prüft nie eine Zeit), Erweiterung `InteropSampleTests` (`WritesTextEditSamplesForOtherViewers`).

- [ ] **Schritt 1: Tests:** `RedactingEditedTextFindsItAndTheCheckPasses` (M14), `OrganizingPagesAfterAnEditKeepsTheNewText` (M13), `OcrLayerIsNotOfferedForEditing` (M15), `TextBoxAnnotationsAreNotRuns` (M10), `SignedDocumentSavesIncrementallyAndKeepsItsPrefix`, `HostileFixtureEndsWithLocksAndBoundedTime`, `TwoQuickEditsOfTheSameRunSecondIsStaleNotWrong`, `EditingInADarkPageRendersLikeTheOriginalFont`. Messung: Lesen der Läufe je Seite, Ersetzen in einem Auftrag, Zuwachs der Datei; Muster für die Handprüfung.
- [ ] **Schritt 2–4:** rot, ggf. Korrekturen in den vorigen Aufgaben, grün, Commit `test(text): Zusammenspiel und feindliche Eingaben (M16)`.

## Aufgabe 8: Dokumentation, Abnahme, Roadmap

- [ ] **Schritt 1:** `Docs/DECISIONS.md` (Einträge „Text bearbeiten: … (M16)“: Ersetzen, Abdeckung, Ersatzschrift, Sperren, Speichern/alter Text, Grenzen, Messwerte), `CLAUDE.md` Block „Text bearbeiten (seit M16)“ (Wo-was-liegt, Stolperfallen), `README.md`, `CHANGELOG.md` (`[Unreleased]`), `packaging/README.md` („Text bearbeiten prüfen (M16)“: Muster in Chrome und Acrobat, Weiterbearbeiten in Acrobat, eigene echte Datei, Karten und Modus hell/dunkel/Hochkontrast/150 %/200 %/Bildschirmleser, Linux-Lauf `*TextEdit*`, Startzeit-Check auf dem Haupt-PC).
- [ ] **Schritt 2:** `Docs/roadmap.json`: Aufgaben und Kriterien abhaken **nur mit Nachweis** (Testname, Befehl + Ergebnis), Scope-Änderungen (z. B. leerer Text entfernt den Lauf), Backlog (Suchen und Ersetzen, CLI `text replace`, eigene eingebettete Geist wiederverwenden, weitere Ersatzschriften, Ausrichtung beibehalten); `dotnet run tools/roadmap.cs -- generate` und `-- check`.
- [ ] **Schritt 3: Fertig heißt:** `dotnet build Pagewing.slnx` (0 Warnungen), `dotnet test Pagewing.slnx` Debug **und** `-c Release` (Zahlen nennen), `dotnet format Pagewing.slnx --verify-no-changes`, `make-testpdfs check`, AOT-Publish von App und CLI (win-x64) und `smoke-cli`. Offen markieren und mit Befehl nennen: Linux-Läufe, Startzeit-Check auf echter Hardware, Handprüfungen.
- [ ] **Schritt 4:** Commit `docs: Text bearbeiten (Milestone 16) abgenommen`, PR nach `dev`.
