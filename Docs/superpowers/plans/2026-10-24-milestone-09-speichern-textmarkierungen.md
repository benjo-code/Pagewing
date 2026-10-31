# Milestone 9: Speichern & Textmarkierungen – Implementierungsplan

> **Für ausführende Agenten:** PFLICHT-SKILL: `superpowers:subagent-driven-development` (empfohlen) oder `superpowers:executing-plans`, Aufgabe für Aufgabe. Schritte sind Checkboxen (`- [ ]`).

**Ziel:** Text lässt sich hervorheben, unter- und durchstreichen und kommentieren; Änderungen werden inkrementell, atomar und geprüft gespeichert, sehen in Chrome und Acrobat gleich aus und lassen vorhandene Signaturen unberührt.

**Architektur:** Rechnerisches (Annotationsmodell, Geometrie, Erscheinungsbild, Rückgängig-Verlauf, Signaturrichtlinie, Speicher-Pipeline) liegt UI-frei in `Pagewing.Core`; PDFium wird nur in `Pagewing.Pdfium` und nur auf dem `PdfiumThread` angefasst, jede Änderung ist ein kurzer Auftrag. Ein Spike entscheidet vor allem anderen, ob PDFium allein ein Erscheinungsbild schreiben kann, das überall stimmt (Aufgabe 2); sonst folgt Aufgabe 7 (eigener Schreiber). Die App bekommt das Panel „Kommentare“, den Modus „Kommentieren“ (Design-Spec 7.7) und Karten für Speichern, Schließen und Warnungen.

**Tech Stack:** .NET 10 Native AOT, Avalonia 12.1, PDFium (`bblanchon.PDFium.*`) über eigene `[LibraryImport]`-Bindings, xunit.v3 (Microsoft.Testing.Platform), Avalonia.Headless.

**Spec:** [2026-10-24-teilprojekt2-annotieren-design.md](../specs/2026-10-24-teilprojekt2-annotieren-design.md) (maßgeblich); Milestone und Abnahme: [Docs/ROADMAP.md](../../ROADMAP.md), „Milestone 9“. Design: [Designsystem](../specs/2026-10-17-designsystem.md), 7.3 und 7.7.

## Globale Randbedingungen

Gelten für jede Aufgabe (Wortlaut aus Spec, `CLAUDE.md` und Roadmap):

- PDFium nur über den `PdfiumThread`; ein Aufruf von anderswo wirft. Jede Änderung ist ein **kurzer** Auftrag; der UI-Thread wartet nie auf PDFium. Alle Konstanten und Funktionen gegen die Header der ausgelieferten PDFium-Version prüfen (`NativeBindingsTests.ConstantsMatchThePackageHeaders`); `unsigned long` immer als `CULong`.
- **Speichern immer atomar:** temporäre Datei im Zielordner, Prüfung, erst dann ersetzen; das Original darf nie halb überschrieben sein. Normalfall inkrementell (`FPDF_INCREMENTAL`); die ersten *n* Bytes der neuen Datei sind **byteweise gleich** der Originaldatei (*n* = Länge des Originals).
- Pagewing behauptet nie, eine Signatur sei „gültig“, und nennt nichts „rechtsgültig“. Meldungen bei DocMDP-Stufe 1 und 2 warnen vor dem Speichern, Stufe 3 oder keine Angabe nur ein Hinweis.
- **Erscheinungsbild:** jede angelegte oder geänderte Annotation hat ein eigenes `/AP` als Form-XObject, `QuadPoints` in der Reihenfolge, die Acrobat liest, Hervorhebung mit Mischmodus Multiplizieren, Flag „Print“ gesetzt. `FPDF_PageToDevice` (und seine Umkehrung) ist die einzige Koordinatenumrechnung.
- **Kein stilles Speichern** in die Originaldatei; keine Wiederherstellungsdatei (Backlog). Keine Werkzeuge in der Leseansicht (Design-Spec 7.7): Bearbeiten nur im Modus „Kommentieren“.
- **Startpfad:** nichts Neues vor dem ersten scharfen Seitenbild; Panel, Werkzeugleiste, Karten, Annotationen-Lesen und Speicher-Code entstehen erst bei Gebrauch. Startzeit-Budgets und `bench/startup` nie lockern.
- Kürzel nur im `CommandCatalog`, nie als `KeyBinding` des Fensters; feldeigene Bindungen (Kommentarfeld) bleiben. Strg+Z im Kommentarfeld macht Text rückgängig, nicht die Annotation.
- Tab-Punkt und Statusleisten-Text „Nicht gespeichert“ (7.7, 7.10); Zustände nie nur über Farbe; Kontrast WCAG 2.2 AA (Text 4,5:1, Ränder und Fokus 3:1); Automation-Namen, `AccessibilityView="Content"` an `Grid`/`Border` mit Namen; keine Farb- oder Maßliterale im XAML.
- Dunkle Seite: Markierungen bleiben sichtbar und unterscheidbar. Gedruckt wird immer hell, Anmerkungen werden mitgedruckt.
- Native AOT: keine Reflection, JSON nur mit Source-Generator, AOT-/Trim-Warnungen sind Fehler. Keine neuen Abhängigkeiten (sonst Lizenzmanifest, `ThirdPartyNotices.txt`, `DECISIONS`).
- Tests schreiben nie in den echten Benutzerordner (`UserSettings.Detached()`, `RecentFilesService.Detached()`, Temp-Ordner) und nie neben die Eingabedateien in `testdata`. Test-PDFs: Herkunft und Lizenz in `testdata/pdfs/LICENSES.md`, Eintrag in `RenderCorpus.Cases` oder `Skipped`.
- UI-Texte Englisch **und** Deutsch (`Strings.en.resx`, `Strings.de.resx`, `Strings.cs`). Code und Kommentare Englisch; Doku und Commits Deutsch (Conventional Commits, gezielt stagen, nie `git add .`, danach `git show --stat HEAD` prüfen). Testnamen ohne Unterstriche.
- Befehle: Build `dotnet build Pagewing.slnx`, Test `dotnet test Pagewing.slnx`, Format `dotnet format Pagewing.slnx --verify-no-changes`, Roadmap `dotnet run tools/roadmap.cs -- check`. Einzelne Tests: `dotnet test tests/Pagewing.Core.Tests --filter-class "*Name"` (analog `--filter-method`).
- Commit-Trailer jeder Aufgabe: `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.

## Review-Fokus

Eingaben und Zustände, die die Spec nicht ausspricht und an denen Nutzer scheitern könnten, wahrscheinlichste zuerst; jeder Punkt hat seinen Test in der Aufgabe, die den Code besitzt:

1. **Dieselbe Datei in zwei Tabs oder Fenstern, eines mit Änderungen:** Speichern im einen darf die Änderungen im anderen nicht still überschreiben; das andere zeigt beim Speichern „Datei seit dem Öffnen verändert“ (Aufgabe 9 und 13).
2. **Defekte oder feindliche Annotationen beim Lesen** (fehlendes `/Rect`, `/QuadPoints` mit falscher Länge oder NaN, `/Annots` kein Array, tausende Annotationen): werden übersprungen oder gekappt (höchstens 10.000 je Dokument), nie ein Absturz und nie eine endlose Liste (Aufgabe 6).
3. **Kommentar mit Unicode, Emoji, Zeilenumbruch, sehr lang (bis 32 KiB):** übersteht Speichern und Neuöffnen unverändert; formatiertes `/RC` einer fremden Markierung überdeckt den neuen Text nicht (Aufgabe 6).
4. **Ziel nicht beschreibbar, Platte voll, Dateiname mit Sonderzeichen oder sehr lang, Datei gesperrt von einem anderen Programm:** klare Fehlerkarte, Original unberührt, temporäre Datei weg, „Speichern unter“ wird angeboten (Aufgabe 9).
5. **Auswahl über mehrere Seiten, Auswahl ohne Geometrie (Leerzeichen, Seite ohne Text), Doppelklick-Markierung zweimal hintereinander:** je Seite höchstens eine Annotation, eine leere Auswahl legt nichts an, Rückgängig entfernt genau eine (Aufgabe 4 und 16).

---

## Dateistruktur

| Datei | Verantwortung |
|---|---|
| `src/Pagewing.Pdfium/Native/Fpdf.cs`, `FpdfConstants.cs` (erweitert) | Bindings für Annotationen, inkrementelles Speichern, `FPDF_DeviceToPage`, Signaturen, Berechtigungen |
| `src/Pagewing.Pdfium/PdfiumAnnotations.cs` | Lesen, Anlegen, Ändern, Löschen von Annotationen einer Seite (nur `PdfiumThread`) |
| `src/Pagewing.Pdfium/PdfiumDocument.cs` (erweitert) | `SaveIncremental(Stream)`, Signaturen, Berechtigung „Annotieren“ |
| `src/Pagewing.Core/Annotations/` | `AnnotationKind`, `AnnotationColor`, `MarkupColor`, `AnnotationPalette`, `PagePoint`, `Quad`, `PdfAnnotation`, `AnnotationLocator`, `AnnotationEdit`, `MarkupGeometry`, `MarkupAppearance`, `AnnotationHistory`, `AnnotationEditor`, `AnnotationExcerpt` |
| `src/Pagewing.Core/Saving/` | `DocumentSaver`, `SaveFailure`, `FileStamp`, `SignatureAdvice`, `SignaturePolicy` |
| `src/Pagewing.Core/Pdf/PdfDocument.cs` (erweitert) | öffentliche Annotations- und Speicher-API |
| `src/Pagewing.Core/Viewing/RenderCache.cs`, `RenderScheduler.cs`, `ViewerSession.cs` (erweitert) | seitenweise Invalidierung, Dokument nach dem Speichern austauschen |
| `src/Pagewing.Core/Settings/AppSettings.cs` (erweitert) | Autor, zuletzt gewählte Farben |
| `src/Pagewing.App/Commands/CommandCatalog.cs` (erweitert) | Speichern, Rückgängig, Modus, Panel „Kommentare“ |
| `src/Pagewing.App/ViewModels/TabViewModel.Annotations.cs`, `MainViewModel.Save.cs`, `CommentsViewModel.cs` | Zustand: Modus, Werkzeug, ungespeichert, Speichern, Liste |
| `src/Pagewing.App/Views/CommentsPanel.axaml(.cs)`, `AnnotationToolbar.axaml(.cs)`, `SaveChangesCard`, `SignatureWarningCard`, `FileChangedCard`, `SaveErrorCard` (je `.axaml(.cs)`) | Oberfläche |
| `src/Pagewing.App/Views/DocumentView.Annotations.cs`, `Controls/DocumentCanvas.cs` (erweitert) | Anwenden beim Loslassen, dunkle Seite |
| `tools/make-testpdfs.cs` (erweitert), `testdata/pdfs/*` | Signatur-Attrappen, linearisiert, fremde Annotationen |

---

### Aufgabe 1: Bindings und inkrementelles Speichern (Pdfium)

**Dateien:**
- Ändern: `src/Pagewing.Pdfium/Native/{Fpdf,FpdfConstants}.cs`, `src/Pagewing.Pdfium/PdfiumDocument.cs`, `src/Pagewing.Core/Pdf/PdfDocument.cs`
- Test: `tests/Pagewing.Core.Tests/Pdfium/NativeBindingsTests.cs`, neu `tests/Pagewing.Core.Tests/Pdfium/PdfiumIncrementalSaveTests.cs`

**Schnittstellen:**
- Erzeugt:
  - Bindings (nur diese, `[LibraryImport]`): `FPDFPage_GetAnnotCount`, `FPDFPage_GetAnnot`, `FPDFPage_CloseAnnot`, `FPDFPage_CreateAnnot`, `FPDFPage_RemoveAnnot`, `FPDFAnnot_IsSupportedSubtype`, `FPDFAnnot_GetSubtype`, `FPDFAnnot_GetRect`, `FPDFAnnot_SetRect`, `FPDFAnnot_CountAttachmentPoints`, `FPDFAnnot_GetAttachmentPoints`, `FPDFAnnot_AppendAttachmentPoints`, `FPDFAnnot_GetColor`, `FPDFAnnot_SetColor`, `FPDFAnnot_GetStringValue`, `FPDFAnnot_SetStringValue`, `FPDFAnnot_HasKey`, `FPDFAnnot_SetAP`, `FPDFAnnot_GetFlags`, `FPDFAnnot_SetFlags`, `FPDFAnnot_GetLinkedAnnot`, `FPDF_DeviceToPage`, `FPDF_GetSignatureCount`, `FPDF_GetSignatureObject`, `FPDFSignatureObj_GetDocMDPPermission`.
  - Konstanten in `FpdfConstants`: `SaveIncremental`, `PermissionAnnotate`, die Annotationsarten (`AnnotHighlight`, `AnnotUnderline`, `AnnotStrikeout`, `AnnotPopup`, …), `AnnotFlagPrint`, `AnnotAppearanceNormal`. Jede **gegen den Header** lesen, nicht aus dem Gedächtnis eintragen.
  - `internal void PdfiumDocument.SaveIncremental(Stream destination)` (Muster: `SaveWithoutSecurity`, Flag `SaveIncremental`, kein `SaveRemoveSecurity`).
  - `public Task PdfDocument.SaveIncrementalAsync(Stream destination, CancellationToken cancellationToken = default)` (Priorität `Background`).
- Verbraucht: `Fpdf.FPDF_SaveAsCopy`, `SaveContext` aus `PdfiumDocument`.

- [ ] **Schritt 1: Test erweitern** – `NativeBindingsTests.ConstantsMatchThePackageHeaders` um jede neue Konstante (Name wie im Header); `PdfiumIncrementalSaveTests.IncrementalSaveOfAnUnchangedDocumentStartsWithTheOriginalBytes` (öffnet `text-basic.pdf`, speichert in einen `MemoryStream`, erwartet `stream.Length >= original.Length` und die ersten `original.Length` Bytes gleich) sowie `IncrementalSaveOfAnEncryptedDocumentDoesNotThrow` (`encrypted_hello_world_r6.pdf`, Passwort `hôtel`; Ergebnis fließt in Aufgabe 9).
- [ ] **Schritt 2: Test laufen lassen, Fehlschlag prüfen.** Run: `dotnet test tests/Pagewing.Core.Tests --filter-class "*PdfiumIncrementalSaveTests"` – erwartet FAIL (Member fehlt).
- [ ] **Schritt 3: Bindings, Konstanten und `SaveIncremental`/`SaveIncrementalAsync` umsetzen** (Signaturen oben). Struktur der Rückgabewerte (`FS_RECTF`, `FS_QUADPOINTSF`, `FPDF_BYTESTRING`-Ausgabepuffer) aus den Headern ableiten.
- [ ] **Schritt 4: Tests laufen lassen.** Run: `dotnet test tests/Pagewing.Core.Tests --filter-class "*NativeBindingsTests"` und `"*PdfiumIncrementalSaveTests"` – erwartet PASS. Schlägt der Verschlüsselungstest fehl, ist das ein Ergebnis für Aufgabe 9, kein Grund, ihn zu lockern.
- [ ] **Schritt 5: Commit** – `feat(pdfium): Bindings für Annotationen, Signaturen und inkrementelles Speichern`.

---

### Aufgabe 2: Spike – Erscheinungsbild und Entscheidung (Gate)

Wegwerf-Code, **kein Merge**. Ergebnis ist ein Eintrag in `Docs/DECISIONS.md` und die Entscheidung, ob Aufgabe 7 entfällt.

**Dateien:**
- Neu (nicht committen): `tests/Pagewing.Core.Tests/Spikes/MarkupSpikeTests.cs` (läuft nur mit `PAGEWING_SPIKE_OUT=<Ordner>`; ohne die Variable übersprungen)
- Ändern: `Docs/DECISIONS.md`

**Schnittstellen:** Verbraucht Aufgabe 1 (Bindings, `SaveIncremental`). Erzeugt: Datei-Matrix in `PAGEWING_SPIKE_OUT` und die Entscheidung.

- [ ] **Schritt 1: Matrix erzeugen.** Je Quelle `annotations.pdf` (Text „Highlighted text“), `rotated-page.pdf`, `cropbox.pdf`, `text-basic.pdf` und je Art (Hervorhebung, Unterstreichung, Durchstreichung) entstehen Dateien mit den Varianten: (A) nur `FPDFPage_CreateAnnot` + Eckpunkte + Farbe, ohne `SetAP`; (B) zusätzlich `FPDFAnnot_SetAP` mit Inhaltsstrom (Fläche mit Farbe); (C) wie B, soweit PDFium Ressourcen (`/ExtGState` mit `/BM /Multiply`) zulässt; je mit und ohne `/Popup`; `/F` = Print. Eckpunkte in Acrobat-Reihenfolge, Umrechnung mit `FPDF_DeviceToPage`.
- [ ] **Schritt 2: Rohbytes prüfen.** Für jede Datei: steht `/AP << /N n 0 R >>` mit eigenem Objekt, hat der Strom `/Resources` mit `/ExtGState`/`/Multiply` (sonst Variante C unmöglich)? Ergebnis tabellarisch festhalten.
- [ ] **Schritt 3: Handprüfung durch den Projektinhaber.** Claude nennt den Ordner und bittet, die Dateien in **Chrome** und **Acrobat Reader (Windows)** zu öffnen. Bestanden heißt je Datei: Farbe sichtbar; Text unter der Hervorhebung lesbar (Multiplizieren); Unterstreichung unten, Durchstreichung mittig an der Zeile; auf gedrehter Seite und bei versetzter CropBox an der richtigen Stelle; Kommentar in der Kommentarliste bzw. beim Überfahren sichtbar, mit und ohne `/Popup`.
- [ ] **Schritt 4: Entscheiden und festhalten.** Eintrag in `Docs/DECISIONS.md` („Erscheinungsbild der Annotationen: …“) mit Matrix und Wahl: **Weg 1** (PDFium allein genügt, Aufgabe 7 entfällt, `/Popup` ja/nein) oder **Weg a** (eigener Schreiber, Aufgabe 7 gilt; M9 wird in zwei Teile geteilt: Aufgaben 1 bis 11 mit Speichern und Schreiber zuerst, danach Oberfläche ab Aufgabe 12). Bei Weg a ergänzt Claude vor Aufgabe 7 die Roadmap um die Teilung und stimmt den Zuschnitt von Aufgabe 7 mit dem Projektinhaber ab. Wegwerf-Test löschen, `git status` zeigt ihn nicht.
- [ ] **Schritt 5: Commit** – nur `Docs/DECISIONS.md`: `docs: Entscheidung zum Erscheinungsbild der Annotationen (M9)`.

---

### Aufgabe 3: Testdaten

**Dateien:**
- Ändern: `tools/make-testpdfs.cs`, `testdata/pdfs/LICENSES.md`, `tests/Pagewing.Render.Tests/RenderCorpus.cs`
- Neu (erzeugt): `testdata/pdfs/{signed-docmdp-none,signed-docmdp-1,signed-docmdp-2,signed-docmdp-3,linearized,annotations-foreign}.pdf`

**Schnittstellen:** Erzeugt Eingabedateien für Aufgaben 6, 8, 9. Die Signaturen sind **Strukturattrappen** (Signaturfeld mit `/ByteRange`, `/Contents` mit Platzhalter, `/Reference` mit DocMDP-Transform und `/P` 1, 2, 3 bzw. ohne), keine gültige Kryptografie: Pagewing prüft keine Zertifikate. `annotations-foreign.pdf` enthält: Hervorhebung mit `/Popup`, `/RC` und Antwort (`/IRT`), Hervorhebung ohne `/NM`, ohne `/AP`, Freihand (`Ink`), `FreeText`, `Text`-Notiz, Link; zwei Seiten, Text zum Markieren.

- [ ] **Schritt 1: Test** – `tools/make-testpdfs.cs check` schlägt fehl (Dateien fehlen); `EveryTestPdfIsInTheCorpusOrExplicitlySkipped` schlägt fehl, sobald die Dateien da, aber nicht eingetragen sind. Dazu `TestDataTests.SignatureFixturesCarryTheExpectedDocMdpLevel` (Core.Tests, liest die Rohbytes: `/P 1`, `/P 2`, `/P 3`, beim vierten kein `/DocMDP`).
- [ ] **Schritt 2: Erzeugen** mit `dotnet run tools/make-testpdfs.cs -- generate`; deterministisch und unkomprimiert wie die übrigen. `linearized.pdf` trägt als erstes Objekt ein `/Linearized`-Wörterbuch.
- [ ] **Schritt 3: Lizenz und Korpus** – je Datei Zeile in `LICENSES.md` (selbst erzeugt, Attrappe nennen), `RenderCorpus.Skipped` (mit Begründung) oder `Cases`.
- [ ] **Schritt 4: Prüfen.** Run: `dotnet run tools/make-testpdfs.cs -- check` (Exit 0), `dotnet test tests/Pagewing.Render.Tests`, `dotnet test tests/Pagewing.Core.Tests --filter-class "*TestDataTests"` – PASS.
- [ ] **Schritt 5: Commit** – `test: Testdaten für Signaturen, linearisierte und fremde Annotationen (M9)`.

---

### Aufgabe 4: Modell, Palette, Geometrie, Erscheinungsbild (Core, rein)

**Dateien:**
- Neu: `src/Pagewing.Core/Annotations/{AnnotationKind,AnnotationColor,MarkupColor,AnnotationPalette,PagePoint,Quad,PdfAnnotation,AnnotationLocator,AnnotationEdit,MarkupGeometry,MarkupAppearance,AnnotationExcerpt}.cs`
- Test: `tests/Pagewing.Core.Tests/Annotations/{AnnotationPaletteTests,MarkupGeometryTests,MarkupAppearanceTests,AnnotationExcerptTests}.cs`

**Schnittstellen:**
- Erzeugt:
  - `enum AnnotationKind { Highlight, Underline, StrikeOut, Other }`; `readonly record struct AnnotationColor(byte R, byte G, byte B)`; `enum MarkupColor { Yellow, Green, Blue, Pink, Orange }`.
  - `static class AnnotationPalette`: `AnnotationColor ColorOf(MarkupColor color)`, `MarkupColor? Match(AnnotationColor color)`, `string NameKey(MarkupColor color)` (Schlüssel der resx), `IReadOnlyList<MarkupColor> All`.
  - `readonly record struct PagePoint(double X, double Y)` (PDF-Benutzerraum, Ursprung unten links); `readonly record struct Quad(PagePoint TopLeft, PagePoint TopRight, PagePoint BottomLeft, PagePoint BottomRight)` (Z-Reihenfolge wie Acrobat).
  - `sealed record PdfAnnotation(int Page, int Index, string? Name, AnnotationKind Kind, string Subtype, AnnotationColor? Color, IReadOnlyList<Quad> Quads, PageRect Bounds, string? Contents, string? Author, DateTimeOffset? Modified, bool Editable)`; `Bounds` in Seitenpunkten wie `PageRect` (Ursprung oben links).
  - `readonly record struct AnnotationLocator(int Page, int Index, string? Name)`; `sealed record AnnotationEdit(string? Contents = null, AnnotationColor? Color = null)` (`null` = unverändert, `""` löscht den Kommentar).
  - `static class MarkupGeometry`: `IReadOnlyList<PageRect> LinesFor(IEnumerable<PageRect> selectionRects)` (verbindet Nachbarn derselben Zeile, verwirft leere, negative und nicht endliche Rechtecke); `Quad ToQuad(PagePoint topLeft, PagePoint topRight, PagePoint bottomRight, PagePoint bottomLeft)` (ordnet in die Z-Reihenfolge von Acrobat).
  - `sealed record AppearanceStream(string Content, double Left, double Bottom, double Right, double Top, bool UsesMultiply)`; `static class MarkupAppearance`: `AppearanceStream Build(AnnotationKind kind, AnnotationColor color, IReadOnlyList<Quad> quads)`.
  - `static class AnnotationExcerpt`: `string For(PdfAnnotation annotation, PageText text, int maxLength = 120)` (Text unter den Vierecken, in Lesereihenfolge, gekürzt mit „…“).
- Verbraucht: `PageRect`, `PageText`, `TextGeometry.RectsOf`.

- [ ] **Schritt 1: Tests schreiben** (Namen): `PaletteHasFiveDistinctNamedColors`; `QuadIsOrderedLikeAcrobatReadsIt` (Eingabe TL (10,20), TR (50,20), BR (50,8), BL (10,8) → `[TL, TR, BL, BR]`); `LinesMergeNeighboursOfOneLine`; `LinesDropEmptyInvertedAndNonFiniteRects`; `HighlightAppearanceUsesMultiply` (`UsesMultiply` wahr, Inhalt enthält die Farbe als `rg`); `UnderlineAppearanceDrawsAtTheBottomOfEachQuad`; `StrikeOutAppearanceDrawsAtTheMiddleOfEachQuad`; `BoundingBoxCoversAllQuads`; `ExcerptReadsTheTextUnderTheQuadsAndShortens`.
- [ ] **Schritt 2: Tests laufen lassen, FAIL prüfen.** Run: `dotnet test tests/Pagewing.Core.Tests --filter-namespace "*Annotations"`.
- [ ] **Schritt 3: Typen und Funktionen mit den Signaturen oben umsetzen.** Linienstärke der Unter- und Durchstreichung, Palettenwerte und Farbnamen wählt der Umsetzer (Kontrast der Auswahlknöpfe 3:1 prüfen, Aufgabe 16); Standard: Hervorhebung Gelb, Unterstreichung Blau, Durchstreichung Rosa.
- [ ] **Schritt 4: Tests laufen lassen** – PASS.
- [ ] **Schritt 5: Commit** – `feat(core): Annotationsmodell, Palette, Geometrie und Erscheinungsbild (M9)`.

---

### Aufgabe 5: Rückgängig-Verlauf (Core, rein)

**Dateien:**
- Neu: `src/Pagewing.Core/Annotations/AnnotationHistory.cs`
- Test: `tests/Pagewing.Core.Tests/Annotations/AnnotationHistoryTests.cs`

**Schnittstellen:**
- Erzeugt: `abstract record AnnotationCommand` mit `Create(PdfAnnotation Created)`, `Delete(PdfAnnotation Removed)`, `Edit(PdfAnnotation Before, PdfAnnotation After)`; `sealed class AnnotationHistory` mit `const int MaxCommands = 500`, `bool CanUndo`, `bool CanRedo`, `void Push(AnnotationCommand command)` (leert Wiederholen), `AnnotationCommand? TakeUndo()` (gibt den Befehl zum Umkehren, verschiebt ihn auf den Wiederholen-Stapel), `AnnotationCommand? TakeRedo()`, `void Clear()`, `int Version` (steigt bei Push, TakeUndo, TakeRedo).
- Verbraucht: `PdfAnnotation`.

- [ ] **Schritt 1: Tests** – `PushThenUndoReturnsTheCommand`; `UndoThenRedoReturnsTheSameCommand`; `PushAfterUndoClearsRedo`; `HistoryKeepsAtMostMaxCommandsAndDropsTheOldest`; `ClearEmptiesBothStacksButKeepsVersionIncreasing`; `VersionChangesWithEveryStep`.
- [ ] **Schritt 2: FAIL prüfen** (`--filter-class "*AnnotationHistoryTests"`), **Schritt 3: umsetzen**, **Schritt 4: PASS prüfen**.
- [ ] **Schritt 5: Commit** – `feat(core): Rückgängig-Verlauf für Annotationen (M9)`.

---

### Aufgabe 6: Annotationen in `PdfDocument` (Weg 1)

Setzt die Entscheidung aus Aufgabe 2 voraus. Bei Weg a bleibt die **öffentliche** Schnittstelle gleich; die Implementierung des Erscheinungsbilds richtet sich nach Aufgabe 7.

**Dateien:**
- Neu: `src/Pagewing.Pdfium/PdfiumAnnotations.cs`
- Ändern: `src/Pagewing.Core/Pdf/PdfDocument.cs`
- Test: `tests/Pagewing.Core.Tests/Annotations/{AnnotationReadTests,MarkupRoundTripTests}.cs`

**Schnittstellen:**
- Erzeugt (alle `async`, Priorität `Interactive` für Anlegen/Ändern/Löschen, `Background` für Lesen):
  - `Task<IReadOnlyList<PdfAnnotation>> PdfDocument.GetAnnotationsAsync(int pageIndex, CancellationToken ct = default)`: `Popup` und Antworten (`/IRT`) erscheinen nie; höchstens 10.000 Einträge je Dokument; `Editable` nur für Hervorhebung, Unterstreichung, Durchstreichung ohne Antworten.
  - `Task<PdfAnnotation> PdfDocument.CreateMarkupAsync(int pageIndex, AnnotationKind kind, IReadOnlyList<PageRect> lineRects, AnnotationColor color, string? contents, string? author, CancellationToken ct = default)`: rechnet die Seitenpunkte mit `FPDF_DeviceToPage` in PDF-Raum um, baut Vierecke (`MarkupGeometry.ToQuad`), setzt `/NM` (GUID), `/T` (nur wenn `author` nicht leer), `/M`, `/CreationDate`, `/F` = Print, `/C`, `/Contents` und das Erscheinungsbild (`MarkupAppearance.Build`).
  - `Task DeleteAnnotationAsync(AnnotationLocator locator, CancellationToken ct = default)`: entfernt auch die zugehörige `/Popup`.
  - `Task<PdfAnnotation> UpdateAnnotationAsync(AnnotationLocator locator, AnnotationEdit edit, CancellationToken ct = default)`: Kommentar ändern entfernt ein vorhandenes `/RC`; Farbe ändern erzeugt das Erscheinungsbild neu; vergibt `/NM`, falls es fehlt.
- Verbraucht: Aufgabe 1 (Bindings), Aufgabe 4 (Modell, Geometrie, Erscheinungsbild).

- [ ] **Schritt 1: Tests** – `AnnotationReadTests`: `ReadsHighlightColorQuadsAndContentsOfTheForeignFixture` (`annotations.pdf`); `SkipsPopupsAndReplies` (`annotations-foreign.pdf`); `MarksOnlyPlainMarkupAsEditable`; `ForeignMarkupWithRepliesIsReadOnly`; `HostileAnnotationsAreSkippedAndCapped` (fehlendes `/Rect`, falsche `QuadPoints`-Länge, NaN; Obergrenze 10.000 mit synthetischer Datei). `MarkupRoundTripTests` (Datei jeweils in Temp-Ordner, `SaveIncrementalAsync`, neu öffnen): `HighlightSurvivesSaveAndReopen` (Art, Farbe, Vierecke gleich, `Contents`), `UnderlineAndStrikeOutSurvive`, `RotatedPageKeepsTheQuadsOnTheText` (`rotated-page.pdf`: Rechteck der Wiederöffnung deckt dasselbe Zeichenrechteck wie die Auswahl, Toleranz 0,5 pt), `CropBoxOffsetKeepsTheQuadsOnTheText` (`cropbox.pdf`), `UnicodeAndLongCommentsSurvive` (Emoji, Zeilenumbruch, 32 KiB), `EditingTheCommentRemovesRichText`, `DeletingAMarkupRemovesItsPopup`, `ChangingTheColorRegeneratesTheAppearance` (Rendering vor und nach der Änderung unterscheidet sich an der Stelle), `CreatedMarkupIsDrawnByPdfiumWithAnnotationFlag` (Rendering mit `FPDF_ANNOT` zeigt die Farbe an der Stelle), `SavedFileHasPrintFlagAndAppearanceObject` (Rohbytes enthalten `/AP` mit eigenem Objekt, `/F 4`).
- [ ] **Schritt 2: FAIL prüfen** (`--filter-namespace "*Annotations"`).
- [ ] **Schritt 3: `PdfiumAnnotations` und die vier `PdfDocument`-Methoden umsetzen** (Signaturen oben). Strategie des Erscheinungsbilds laut `DECISIONS`-Eintrag aus Aufgabe 2; Zeichenrahmen „lose“ oder „eng“ nach dem Ergebnis des Spikes.
- [ ] **Schritt 4: PASS prüfen**, außerdem `dotnet test tests/Pagewing.Render.Tests` (keine Änderung der Referenzen).
- [ ] **Schritt 5: Commit** – `feat(pdfium): Annotationen lesen, anlegen, ändern und löschen (M9)`.

---

### Aufgabe 7: Eigener Schreiber (nur bei Weg a)

**Entfällt** bei Weg 1. Bei Weg a gilt: Der Zuschnitt (Objekt-Änderungen statt `FPDF_SaveAsCopy`, oder PDFium schreibt und der Schreiber hängt Erscheinungsbild-Objekte in einer zweiten Revision an) steht nach dem Spike fest und wird vor Beginn dieser Aufgabe mit dem Projektinhaber abgestimmt. Die **Verträge** sind in jedem Fall:

- `PdfDocument.SaveIncrementalAsync(Stream)` liefert eine Datei, deren Anfang byteweise der Originaldatei entspricht und die alle Tests aus Aufgabe 6 (`MarkupRoundTripTests`) bestehen lässt, **ohne dass diese Tests geändert werden**.
- Der Schreiber beherrscht Querverweistabelle **und** Querverweisstrom (`/Prev`), Dateien mit Objektströmen, linearisierte Dateien (`linearized.pdf`) und die Verschlüsselung der neuen Objekte mit dem Crypt-Filter des Dokuments (`encrypted_hello_world_r3.pdf`, `r6.pdf`).
- Ein Fehler in einer Sonderform lässt das Speichern **ablehnen** (`SaveFailure.WriterNotSupported`), nie eine unlesbare Datei entstehen (Aufgabe 9 prüft die Datei vor dem Ersetzen).

Die Schritte (Test, FAIL, Umsetzung, PASS, Commit) schreibt Claude nach der Abstimmung in dieser Aufgabe aus; die Tests dazu: `WriterOutputReopensInPdfium`, `WriterKeepsTheOriginalPrefix`, `WriterHandlesXrefStreams`, `WriterHandlesObjectStreams`, `WriterHandlesLinearizedFiles`, `WriterEncryptsNewObjects`, `WriterRejectsMalformedTrailers`.

---

### Aufgabe 8: Signaturen und Berechtigung (Core + Pdfium)

**Dateien:**
- Neu: `src/Pagewing.Core/Saving/{SignatureAdvice,SignaturePolicy}.cs`
- Ändern: `src/Pagewing.Pdfium/PdfiumDocument.cs`, `src/Pagewing.Core/Pdf/PdfDocument.cs`
- Test: `tests/Pagewing.Core.Tests/Saving/SignaturePolicyTests.cs`, `tests/Pagewing.Core.Tests/Pdf/SignatureInfoTests.cs`

**Schnittstellen:**
- Erzeugt: `enum DocMdpPermission { NotSet = 0, NoChanges = 1, FormsOnly = 2, FormsAndAnnotations = 3 }`; `sealed record PdfSignatureInfo(int SignatureCount, DocMdpPermission Permission)`; `Task<PdfSignatureInfo> PdfDocument.GetSignatureInfoAsync(CancellationToken ct = default)`; `Task<bool> PdfDocument.MayAnnotateAsync(CancellationToken ct = default)` (Berechtigungsbit „Annotationen ändern“ der PDF-Spezifikation, Wert gegen die Spezifikation prüfen); `enum SignatureAdvice { None, AppendedOnly, WouldInvalidate }`; `static SignatureAdvice SignaturePolicy.Evaluate(PdfSignatureInfo info)`.
- Verbraucht: Aufgabe 1 (Bindings).

- [ ] **Schritt 1: Tests** – `SignaturePolicyTests`: `NoSignatureGivesNone`; `LevelThreeGivesAppendedOnly`; `SignedWithoutDocMdpGivesAppendedOnly`; `LevelOneAndTwoGiveWouldInvalidate`. `SignatureInfoTests` (an den Attrappen aus Aufgabe 3): `ReadsLevelsOfTheFixtures` (keine/1/2/3), `UnsignedDocumentHasNoSignatures`; `EncryptedDocumentWithoutAnnotatePermissionReportsFalse` (falls ein Test-PDF das hergibt, sonst als `Skip` mit Begründung; die Berechtigungslogik prüft zusätzlich ein Test an einer Hilfsfunktion `MayAnnotate(uint permissions)`).
- [ ] **Schritt 2: FAIL prüfen**, **Schritt 3: umsetzen** (PDFium meldet 0 sowohl für „kein DocMDP“ als auch für ungültig; die Attrappen aus Aufgabe 3 prüfen, ob die gebundene Version beides unterscheidet; sonst Rohbytes-Fallback im Pdfium-Projekt und den Befund in `DECISIONS` festhalten), **Schritt 4: PASS prüfen**.
- [ ] **Schritt 5: Commit** – `feat(core): Signaturrechte und Annotationsberechtigung lesen (M9)`.

---

### Aufgabe 9: Speicher-Pipeline (Core)

**Dateien:**
- Neu: `src/Pagewing.Core/Saving/{DocumentSaver,SaveFailure,FileStamp}.cs`
- Test: `tests/Pagewing.Core.Tests/Saving/{DocumentSaverTests,FileStampTests}.cs`

**Schnittstellen:**
- Erzeugt:
  - `readonly record struct FileStamp(long Length, DateTime LastWriteUtc)` mit `static FileStamp? Of(string path)`.
  - `enum SaveFailureReason { NotWritable, DiskFull, WriteFailed, VerificationFailed, OriginalChanged, WriterNotSupported, EncryptedNotSupported, Canceled }`; `sealed class SaveFailureException(SaveFailureReason Reason, string message, Exception? inner)`.
  - `sealed record SaveRequest(string OriginalPath, string TargetPath, FileStamp? OriginalStamp, string? Password, int ExpectedPageCount, IReadOnlyDictionary<int, int> ExpectedAnnotationCounts, bool OverwriteChangedOriginal)`; `sealed record SaveResult(PdfDocument Reopened, FileStamp NewStamp)`.
  - `static class DocumentSaver`: `Task<SaveResult> SaveAsync(PdfDocument document, PdfEngine engine, SaveRequest request, IProgress<double>? progress = null, CancellationToken ct = default)`. Ablauf: Original-Stempel prüfen (`OriginalChanged`, außer `OverwriteChangedOriginal`; bei „Speichern unter“ auf anderen Pfad entfällt die Prüfung des Ziels) → `SaveIncrementalAsync` in `<Ziel>.<guid>.tmp` im Zielordner (`FileMode.CreateNew`) → Prüfung 1: ersten *n* Bytes gleich dem Original (n = Länge des Originals, aus `OriginalPath`) → Prüfung 2: mit `engine.OpenAsync(temp, Password)` öffnen, Seitenzahl und Annotationszahlen der geänderten Seiten vergleichen → Prüfung 3: jede geänderte Seite rendert (`RenderPageAsync`) → `File.Move(temp, target, overwrite: true)` → `SaveResult` (das geprüfte Dokument bleibt offen und wird dem Aufrufer übergeben). Jeder Fehlerpfad löscht die temporäre Datei, schließt das geprüfte Dokument und lässt das Ziel unberührt.
  - internes Test-Haken-Objekt `SaveFaults` (`Func<Stream, Stream>? WrapTemporary`, `Action? AfterWrite`) nur für Tests.
- Verbraucht: `PdfDocument.SaveIncrementalAsync`, `GetAnnotationsAsync`, `RenderPageAsync`, `PdfEngine.OpenAsync`.

- [ ] **Schritt 1: Tests** (`DocumentSaverTests`, Temp-Ordner je Test): `SavedFileStartsWithTheOriginalBytes`; `CanceledSaveLeavesTheOriginalByteIdenticalAndRemovesTheTemporaryFile` (Abbruch mitten im Schreiben); `WriteFailureLeavesTheOriginalAndRemovesTheTemporaryFile` (Haken wirft nach 1 KiB, Ergebnis `WriteFailed`); `FailedVerificationLeavesTheOriginal` (Haken verfälscht den Anfang → `VerificationFailed`); `AnnotationCountMismatchFailsVerification`; `ChangedOriginalIsReportedBeforeAnythingIsWritten`; `OverwriteChangedOriginalSkipsThatCheck`; `UnwritableFolderReportsNotWritable`; `SaveAsWritesANewFileAndKeepsTheOriginal`; `ReplacingAnOpenTargetWorks` (das Original ist im Test offen geöffnet; läuft unter Windows und Linux); `SignedFixtureKeepsTheSignedBytes` (`signed-docmdp-3.pdf`: Anfang identisch); `EncryptedDocumentStaysEncryptedOrIsRefused` (`encrypted_hello_world_r6.pdf`: entweder `EncryptedNotSupported` oder die Zieldatei verlangt das Passwort; nie unverschlüsselt); `LinearizedFileSaves`. `FileStampTests`: `StampChangesWhenLengthOrTimeChanges`, `MissingFileGivesNull`.
- [ ] **Schritt 2: FAIL prüfen**, **Schritt 3: umsetzen**, **Schritt 4: PASS prüfen** (`--filter-namespace "*Saving"`; unter Windows und auf dem Linux-Runner).
- [ ] **Schritt 5: Commit** – `feat(core): atomare Speicher-Pipeline mit Prüfung vor dem Ersetzen (M9)`.

---

### Aufgabe 10: Seitenweise Invalidierung (Core)

**Dateien:**
- Ändern: `src/Pagewing.Core/Viewing/{RenderCache,RenderScheduler,ViewerSession}.cs`
- Test: `tests/Pagewing.Core.Tests/Viewing/{RenderCacheTests,RenderSchedulerTests,ViewerSessionTests}.cs`

**Schnittstellen:**
- Erzeugt: `void RenderCache.RemovePages(IReadOnlySet<int> pages)`; `void RenderScheduler.InvalidatePages(IReadOnlySet<int> pages)` (eine Seiten-Epoche: Ergebnisse älterer Epochen derselben Seite werden verworfen, wie bei der Theme-Epoche; wartet nie auf einen Handler, nie aus einem Handler aufrufen); `void ViewerSession.InvalidatePages(IReadOnlySet<int> pages)` (Cache, Scheduler, Seitenvorschau und Seitentext-Zwischenspeicher der Seiten; hebt kein Budget und löst `Changed` einmal aus).
- Verbraucht: bestehende Typen.

- [ ] **Schritt 1: Tests** – `RemovePagesDropsOnlyThoseTiles`; `InvalidatedPageIsRenderedAgainAndAnOldResultIsDiscarded` (ein laufender Auftrag der Seite liefert nach `InvalidatePages` nichts mehr in den Cache); `OtherPagesKeepTheirTiles`; `SessionInvalidatePagesDropsThumbnailsAndPageText` (über `HasPageTexts` und `ThumbnailSource`); `InvalidatePagesRaisesChangedOnce`.
- [ ] **Schritt 2: FAIL prüfen**, **Schritt 3: umsetzen**, **Schritt 4: PASS prüfen** (`--filter-namespace "*Viewing"`).
- [ ] **Schritt 5: Commit** – `feat(core): Kacheln einzelner Seiten verwerfen (M9)`.

---

### Aufgabe 11: `AnnotationEditor` und Dokumenttausch nach dem Speichern (Core)

**Dateien:**
- Neu: `src/Pagewing.Core/Annotations/AnnotationEditor.cs`
- Ändern: `src/Pagewing.Core/Viewing/ViewerSession.cs`
- Test: `tests/Pagewing.Core.Tests/Annotations/AnnotationEditorTests.cs`, `tests/Pagewing.Core.Tests/Viewing/ViewerSessionTests.cs`

**Schnittstellen:**
- Erzeugt:
  - `sealed class AnnotationEditor(ViewerSession session)`: `bool IsDirty`, `bool CanUndo`, `bool CanRedo`, `event Action? Changed` (UI-Thread-unabhängig, wie `ViewerSession.Changed`), `Task<PdfAnnotation?> ApplyMarkupAsync(int page, AnnotationKind kind, IReadOnlyList<PageRect> lineRects, AnnotationColor color, string? contents, string? author)` (leere Geometrie → `null`, nichts angelegt), `Task DeleteAsync(AnnotationLocator locator)`, `Task<PdfAnnotation> UpdateAsync(AnnotationLocator locator, AnnotationEdit edit)`, `Task UndoAsync()`, `Task RedoAsync()`, `Task<SaveResult> SaveAsync(PdfEngine engine, string targetPath, bool overwriteChangedOriginal, IProgress<double>? progress, CancellationToken ct)`, `IReadOnlySet<int> ChangedPages`. Jede Änderung ruft `session.InvalidatePages` für ihre Seite; Rückgängig stellt durch Neuanlegen her (herstellerspezifische Schlüssel einer gelöschten fremden Markierung gehen verloren, in `DECISIONS`). `IsDirty` = Verlauf-`Version` ≠ Version beim letzten Speichern.
  - `Task ViewerSession.AdoptSavedDocumentAsync(PdfDocument reopened, string path)`: tauscht das Dokument aus, **ohne** dass ein Zustand ohne Dokument entsteht; `Cache` bleibt dieselbe Instanz (Bilder bleiben gültig), ein neuer Scheduler wird erzeugt, das alte Dokument wird danach geschlossen, `State` bleibt `Ready`, `Changed` genau einmal; das Passwort des letzten Öffnens bleibt nur im Arbeitsspeicher der Sitzung.
- Verbraucht: Aufgaben 5, 6, 9, 10.

- [ ] **Schritt 1: Tests** – `AnnotationEditorTests`: `ApplyMarkupMakesTheDocumentDirtyAndInvalidatesItsPage`; `EmptyGeometryCreatesNothing`; `UndoRemovesTheMarkupAndRedoRestoresIt`; `DeleteThenUndoRestoresColorAndComment`; `SaveClearsDirtyAndTheHistory`; `SaveFailureKeepsDirtyAndTheHistory`; `TwoRapidSelectionsOfTheSameWordCreateTwoAndUndoRemovesExactlyOne`. `ViewerSessionTests`: `AdoptSavedDocumentKeepsTheCacheAndNeverShowsAnEmptyState`; `AdoptSavedDocumentClosesTheOldDocument`; `PasswordProtectedDocumentIsAdoptedWithoutAskingAgain`.
- [ ] **Schritt 2: FAIL prüfen**, **Schritt 3: umsetzen**, **Schritt 4: PASS prüfen**.
- [ ] **Schritt 5: Commit** – `feat(core): Editor mit Verlauf und Dokumenttausch nach dem Speichern (M9)`.

---

### Aufgabe 12: Einstellungen für Autor und Farben

**Dateien:**
- Ändern: `src/Pagewing.Core/Settings/AppSettings.cs`, `src/Pagewing.App/Views/SettingsCard.axaml(.cs)`, `src/Pagewing.App/ViewModels/SettingsViewModel.cs`, `src/Pagewing.App/Resources/Strings.{en,de}.resx`, `Strings.cs`
- Test: `tests/Pagewing.Core.Tests/Settings/AppSettingsTests.cs`, `tests/Pagewing.App.Tests/SettingsCardTests.cs`

**Schnittstellen:**
- Erzeugt: `AppSettings.AuthorName` (`string?`, Standard `null`, höchstens 128 Zeichen, sonst `null`), `HighlightColor`, `UnderlineColor`, `StrikeOutColor` (alle `MarkupColor`; Standard Gelb, Blau, Rosa; `Normalized()` setzt ungültige Werte zurück; Eigenschaften mit `{ get; set; }`). Einstellungskarte: Gruppe „Kommentare“ mit Textfeld „Autor“ (Hinweis, dass der Name in der Datei steht, `type.caption`, `text.secondary`).
- Verbraucht: bestehende Einstellungskarte (Muster: „Aktualisierungen“).

- [ ] **Schritt 1: Tests** – `AppSettingsTests`: `AuthorNameIsDroppedWhenEmptyOrTooLong`, `InvalidMarkupColorsFallBackToTheDefaults`, `OldSettingsFileWithoutTheNewFieldsLoadsWithDefaults`. `SettingsCardTests`: `AuthorFieldChangesTheSettingAndSavesAtOnce`, `CardColoursStillMeetAA` (bestehend, bleibt grün).
- [ ] **Schritt 2: FAIL prüfen**, **Schritt 3: umsetzen** (Texte beider Sprachen), **Schritt 4: PASS prüfen** (`--filter-class "*AppSettingsTests"`, `dotnet test tests/Pagewing.App.Tests --filter-class "*SettingsCardTests"`).
- [ ] **Schritt 5: Commit** – `feat(app): Einstellungen für Autor und Markierungsfarben (M9)`.

---

### Aufgabe 13: Befehle, Tab-Zustand und Speichern in der App

**Dateien:**
- Ändern: `src/Pagewing.App/Commands/CommandCatalog.cs`, `src/Pagewing.App/Services/{IFilePicker,AvaloniaFilePicker}.cs`, `src/Pagewing.App/ViewModels/{TabViewModel,MainViewModel}.cs`, `src/Pagewing.App/Views/{TabView.axaml,StatusBar.axaml,StatusBar.axaml.cs}`, `Resources/Strings*`
- Neu: `src/Pagewing.App/ViewModels/{TabViewModel.Annotations,MainViewModel.Save}.cs`
- Test: `tests/Pagewing.App.Tests/{SaveCommandTests,CommandCatalogTests}.cs`

**Schnittstellen:**
- Erzeugt:
  - Katalog-Einträge (Titel in beiden Sprachen): `file.save` (Strg+S), `file.saveAs` (Strg+Umschalt+S), `edit.undo` (Strg+Z), `edit.redo` (Strg+Umschalt+Z, Alternative Strg+Y), jeweils nur angeboten, wenn ausführbar (`Offered`); `annotate.mode` (Kürzel ohne Kollision im Katalog wählen), `annotate.highlight`, `annotate.underline`, `annotate.strikeout` (bei vorhandener Textauswahl; Aufgabe 16), `panel.comments` (Strg+Umschalt+3, `SidePanelKind.Comments`).
  - `TabViewModel`: `AnnotationEditor Editor` (entsteht erst bei Bedarf), `bool IsDirty`, `ICommand SaveCommand`, `SaveAsCommand`, `UndoCommand`, `RedoCommand`; Tab-Punkt und Statusleisten-Text „Nicht gespeichert“ (7.7, 7.10) mit Symbol **und** Text, `AutomationProperties.Name`.
  - `IFilePicker.PickSavePdfAsync(string suggestedName, string? folder) : Task<string?>`.
  - `MainViewModel.SaveActiveTabAsync()` / `SaveActiveTabAsAsync()`: liest `PdfSignatureInfo`; `SignatureAdvice.WouldInvalidate` zeigt die Signaturkarte (Aufgabe 14), sonst Speichern mit Fortschritt (Hintergrund); Fehler → Fehlerkarte (Aufgabe 14); `OriginalChanged` → Karte „Datei verändert“ (Aufgabe 14).
- Verbraucht: Aufgaben 8, 9, 11, 12.

- [ ] **Schritt 1: Tests** – `CommandCatalogTests`: `SaveAndUndoAreOnlyOfferedWhenTheyCanRun`, `NewShortcutsDoNotCollide`, `EveryNewCommandHasATitleInBothLanguages`. `SaveCommandTests` (mit `FakePdfEngine`-freier Variante: echte Dokumente in Temp-Ordnern; `UserSettings.Detached()`): `SaveWritesTheFileAndClearsTheDirtyMarker`; `SaveAsChangesTheTabToTheNewFile`; `TabShowsDotAndStatusTextWhileDirty`; `SecondTabOfTheSameFileGetsTheFileChangedCardOnSave` (Review-Fokus 1); `MeasurementRunsCreateNoEditor` (Startpfad).
- [ ] **Schritt 2: FAIL prüfen**, **Schritt 3: umsetzen**, **Schritt 4: PASS prüfen** (`dotnet test tests/Pagewing.App.Tests --filter-class "*SaveCommandTests"`, `"*CommandCatalogTests"`).
- [ ] **Schritt 5: Commit** – `feat(app): Speichern, Rückgängig und Ungespeichert-Anzeige (M9)`.

---

### Aufgabe 14: Karten für Schließen, Signatur, geänderte Datei und Fehler

**Dateien:**
- Neu: `src/Pagewing.App/Views/{SaveChangesCard,SignatureWarningCard,FileChangedCard,SaveErrorCard}.axaml(.cs)`, `src/Pagewing.App/ViewModels/PendingSave.cs`
- Ändern: `src/Pagewing.App/ViewModels/MainViewModel.cs`, `src/Pagewing.App/Views/MainWindow.axaml.cs` (`OnClosing`), `Services/Workspace.cs`, `Resources/Strings*`, `Docs/superpowers/specs/2026-10-17-designsystem.md` (neuer Abschnitt 7.14)
- Test: `tests/Pagewing.App.Tests/{SaveCardsTests,CloseWithChangesTests}.cs`

**Schnittstellen:**
- Erzeugt: Karten im Dokumentbereich (kein modaler Dialog; Stil der Passwort- und Link-Karte): `SaveChangesCard` („Speichern“ Standard und Fokus, „Nicht speichern“, „Abbrechen“; Esc bricht ab), `SignatureWarningCard` (Texte laut Spec; bei Stufe 3 oder ohne Angabe nur ein Hinweis mit „Speichern“/„Abbrechen“, bei Stufe 1/2 Warnsymbol und Text, „Abbrechen“ Standard, „Speichern unter“, „Trotzdem speichern“), `FileChangedCard` („Überschreiben“, „Speichern unter“ Standard, „Abbrechen“), `SaveErrorCard` (Grund je `SaveFailureReason`, „Speichern unter“ angeboten). `MainViewModel.CloseTab` und `MainWindow.OnClosing` fragen bei `IsDirty` zuerst (mehrere Tabs nacheinander; „Abbrechen“ bricht das Schließen ab); `PendingSave` hält die wartende Karte.
- Verbraucht: Aufgaben 9, 13.

- [ ] **Schritt 1: Tests** – `CloseWithChangesTests`: `ClosingADirtyTabAsksFirstAndCancelKeepsTheTab`; `DontSaveClosesWithoutWriting`; `SaveThenCloseWritesBeforeClosing`; `ClosingTheWindowAsksForEachDirtyTabInTurn`; `CancelOnTheSecondTabKeepsBothWindowAndTabs`. `SaveCardsTests`: `SignatureCardAtLevelOneDefaultsToCancelAndNeverSaysValid` (kein „gültig“/„valid“ in den Texten beider Sprachen); `SignatureCardAtLevelThreeIsOnlyANote`; `FileChangedCardDefaultsToSaveAs`; `SaveErrorCardOffersSaveAs`; `CardsAreKeyboardOperableAndNamed` (Fokus, Automation-Namen); `CardColoursMeetAA`; `NoUiTextCallsAnythingLegallyBinding` („rechtsgültig“/„legally“ kommt in keinem Text vor).
- [ ] **Schritt 2: FAIL prüfen**, **Schritt 3: umsetzen** (Design-Spec 7.14 beschreibt die vier Karten kurz), **Schritt 4: PASS prüfen** (`dotnet test tests/Pagewing.App.Tests --filter-class "*CloseWithChangesTests"`, `"*SaveCardsTests"`).
- [ ] **Schritt 5: Commit** – `feat(app): Karten für Schließen, Signatur, geänderte Datei und Fehler (M9)`.

---

### Aufgabe 15: Panel „Kommentare“ (lesend)

**Dateien:**
- Neu: `src/Pagewing.App/ViewModels/CommentsViewModel.cs`, `src/Pagewing.App/Views/CommentsPanel.axaml(.cs)`
- Ändern: `SidePanelKind.cs` (`Comments`), `Views/ActionBar.axaml(.cs)` (Eintrag, `ActionToggleButton`), `Views/SidePanel.axaml`, `Views/MainWindow.Panel.cs`, `Resources/Strings*`, Design-Spec 7.3
- Test: `tests/Pagewing.App.Tests/{CommentsPanelTests,CommentsViewModelTests}.cs`

**Schnittstellen:**
- Erzeugt: `CommentsViewModel` (entsteht erst beim Öffnen, liest die Annotationen aller Seiten im Hintergrund seitenweise): `ObservableCollection<CommentItemViewModel> Items` (virtualisiert, sortiert nach Seite und Position), `CommentFilter Filter` (`All`, `Highlight`, `Underline`, `StrikeOut`, `Other`), `string Summary` (Muster der Suche: „Keine Kommentare“, „3 Kommentare auf 2 Seiten“), `ICommand JumpCommand` (Regeln der Suchtreffer, Design-Spec 7.4), `bool IsEditing` (Aufgabe 16). `CommentItemViewModel`: Seitenmarke in `type.mono`, Art als **Symbol und Text**, Autor, Datum, Textausschnitt (`AnnotationExcerpt`), Kommentar. Das Panel ist ohne Modus rein lesend. Esc/Bild auf/ab wie die Miniaturenliste (`CardLists`-Muster, eigene `KeyBindings`).
- Verbraucht: Aufgaben 4, 6, 10, `PageTextSource`.

- [ ] **Schritt 1: Tests** – `CommentsViewModelTests`: `ListsAnnotationsOfAllPagesSortedByPageAndPosition` (`annotations-foreign.pdf`); `FilterByKindShowsOnlyThatKind`; `PopupsAndRepliesAreNotListed`; `SummaryCountsCommentsAndPages`; `ListIsNotReadBeforeThePanelOpens` (Startpfad). `CommentsPanelTests` (Headless): `PanelIsReadOnlyOutsideTheMode` (keine Knöpfe zum Bearbeiten); `ClickJumpsToThePage`; `EmptyDocumentShowsAHint`; `ActionBarEntryReportsToggleStateToScreenReaders`; `EntriesShowKindAsIconAndText`; `LargeListStaysVirtualized` (5.000 synthetische Einträge).
- [ ] **Schritt 2: FAIL prüfen**, **Schritt 3: umsetzen**, **Schritt 4: PASS prüfen** (`dotnet test tests/Pagewing.App.Tests --filter-class "*CommentsViewModelTests"`, `"*CommentsPanelTests"`).
- [ ] **Schritt 5: Commit** – `feat(app): Panel „Kommentare“ mit Liste, Filter und Sprung (M9)`.

---

### Aufgabe 16: Modus „Kommentieren“

**Dateien:**
- Neu: `src/Pagewing.App/Views/AnnotationToolbar.axaml(.cs)`, `src/Pagewing.App/Views/DocumentView.Annotations.cs`
- Ändern: `ViewModels/TabViewModel.Annotations.cs`, `ViewModels/CommentsViewModel.cs`, `Views/CommentsPanel.axaml`, `Views/DocumentView.axaml.cs`, `Views/DocumentView.Text.cs`, `Controls/DocumentCanvas.cs`, `Resources/Strings*`, Design-Spec 7.7
- Test: `tests/Pagewing.App.Tests/{AnnotateModeTests,AnnotationToolbarTests}.cs`

**Schnittstellen:**
- Erzeugt:
  - `TabViewModel`: `bool IsAnnotating`, `AnnotationKind? ActiveTool`, `MarkupColor ActiveColor`, `ICommand BeginAnnotateCommand`, `EndAnnotateCommand` („Fertig“, Esc), `SetToolCommand` (Parameter `AnnotationKind`), `SetColorCommand` (Parameter `MarkupColor`); die gewählte Farbe je Art wird in `AppSettings` gemerkt (mit `with`).
  - Werkzeugleiste über dem Dokument (Hervorheben, Unterstreichen, Durchstreichen, Farbe, Rückgängig, Wiederholen, **Fertig**); Farbknöpfe mit Namen für Tooltip/Screenreader, Auswahl über Rand **und** Häkchen, nicht nur Farbe.
  - Mit gewähltem Werkzeug wendet das **Loslassen der Maus** nach einer Textauswahl die Markierung an (`AnnotationEditor.ApplyMarkupAsync`; je Seite der Auswahl eine Annotation, Zeilenrechtecke aus `TextGeometry.RectsOf`); die Auswahl wird danach gelöscht. Die Befehle `annotate.highlight|underline|strikeout` wirken bei vorhandener Auswahl auf sie, starten den Modus und wählen das Werkzeug.
  - Im Modus bearbeitbar im Panel (nur `Editable`): Kommentar (mehrzeilig, Strg+Enter übernimmt, Esc verwirft; Strg+Z macht dort Text rückgängig), Farbe, Löschen; die Zuordnung `Autor` kommt aus `AppSettings.AuthorName`.
- Verbraucht: Aufgaben 11, 12, 13, 15, `TextSelection`, `PageTextSource`.

- [ ] **Schritt 1: Tests** – `AnnotateModeTests` (Headless, `FakeClipboardService` wie bei `DocumentView.Text`): `ReleasingTheMouseWithAToolAppliesTheMarkup`; `WithoutAToolSelectionOnlySelects`; `SelectionAcrossTwoPagesCreatesOneAnnotationPerPage`; `SelectionWithoutGeometryCreatesNothing`; `EscEndsTheModeButFirstClearsTheSelection`; `CommandWithSelectionStartsTheModeAndAppliesAtOnce`; `ReadingViewShowsNoEditingTools` (vor dem Modus nirgends Werkzeugleiste oder Bearbeiten-Knöpfe); `CommentFieldKeepsItsOwnUndo`; `ModeIsUnavailableWhenTheDocumentForbidsAnnotations` (Hinweis im Panel mit Grund); `NothingIsReadOrCreatedBeforeTheFirstSharpPage` (Startpfad). `AnnotationToolbarTests`: `ColourButtonsAreNamedAndMeetThreeToOneContrast`; `SelectedToolAndColourAreNotOnlyShownByColour`; `ToolbarIsKeyboardOperable`.
- [ ] **Schritt 2: FAIL prüfen**, **Schritt 3: umsetzen**, **Schritt 4: PASS prüfen** (`dotnet test tests/Pagewing.App.Tests --filter-class "*AnnotateModeTests"`, `"*AnnotationToolbarTests"`).
- [ ] **Schritt 5: Commit** – `feat(app): Modus „Kommentieren“ mit Werkzeugleiste und Markieren (M9)`.

---

### Aufgabe 17: Dunkle Seite und Druck

**Dateien:**
- Ändern: `src/Pagewing.App/Controls/DocumentCanvas.cs`, `src/Pagewing.App/Views/DocumentView.Highlights.cs`, `src/Pagewing.Pdfium/PdfiumRenderWork.cs` (nur falls nötig), Design-Spec 5.1 (Tokens für Markierungen auf dunkler Seite, falls neu)
- Test: `tests/Pagewing.Core.Tests/Pdfium/DarkPageRenderTests.cs`, `tests/Pagewing.App.Tests/DarkPageMarkupTests.cs`, `tests/Pagewing.Core.Tests/Printing/…` (vorhandene Druck-Tests erweitern)

**Schnittstellen:** Verbraucht Aufgabe 6. Erzeugt: auf der dunklen Seite sind Hervorhebung, Unterstreichung und Durchstreichung **sichtbar und unterscheidbar** (das Farbschema aus M6 mit „Füllung zu Strich“ darf Hervorhebungen nicht zu Umrissen machen). Der Weg ist dem Umsetzer überlassen, Vorbild sind die Suchtreffer (`Search.HitOnDark`, halbtransparente Flächen im `DocumentCanvas`); neue Farben nur als Tokens in allen drei Sätzen (hell, dunkel, Hochkontrast) und gegen WCAG 2.2 AA geprüft.

- [ ] **Schritt 1: Tests** – `DarkPageRenderTests.HighlightStaysAFilledAreaOnTheDarkPage` (Pixel in der Fläche weicht vom dunklen Grund ab und entspricht nicht nur einem Umriss); `DarkPageMarkupTests.UnderlineAndStrikeOutAreDistinguishableOnTheDarkPage`; `SearchHitOnTopOfAMarkupIsStillVisible` (hell und dunkel); `PrintedPageIncludesTheMarkupInLightColours` (Druckpfad rendert mit `FPDF_ANNOT`, `FPDF_PRINTING`).
- [ ] **Schritt 2: FAIL prüfen**, **Schritt 3: umsetzen**, **Schritt 4: PASS prüfen** (`--filter-class "*DarkPageRenderTests"`, `"*DarkPageMarkupTests"`; dazu `dotnet test tests/Pagewing.Render.Tests`).
- [ ] **Schritt 5: Commit** – `feat: Markierungen auf der dunklen Seite und im Druck (M9)`.

---

### Aufgabe 18: Update-Weg bei ungespeicherten Änderungen

**Dateien:**
- Ändern: `src/Pagewing.App/Services/UpdateCoordinator.cs`, `src/Pagewing.App/Services/Workspace.cs`, `src/Pagewing.App/ViewModels/UpdateViewModel.cs`, `Resources/Strings*`
- Test: `tests/Pagewing.App.Tests/UpdateWithChangesTests.cs` (mit `FakeUpdateSourceApp`/`UpdateTestHost`, kein Netz)

**Schnittstellen:**
- Erzeugt: `Func<Task<bool>>? UpdateCoordinator.BeforeInstall` (vom `Workspace` gesetzt): „Installieren“ fragt zuerst alle Fenster mit ungespeicherten Tabs der Reihe nach (das aktive zuerst) mit der Karte aus Aufgabe 14; `false` (Abbrechen) bricht die Installation ab, bevor `IUpdateApplier.ApplyAsync` gerufen wird (dessen Vertrag bleibt unverändert). Ein zweiter Klick während der Frage startet nichts.
- Verbraucht: Aufgabe 14, bestehender `UpdateCoordinator.BeginInstallAsync`.

- [ ] **Schritt 1: Tests** – `InstallAsksForEveryWindowWithChangesInTurn`; `CancelOnTheCardStopsTheInstallationBeforeTheApplier`; `NoDirtyTabsMeansNoQuestion`; `SecondClickWhileAskingStartsNothing`; `SavedChangesAreWrittenBeforeTheApplierRuns`.
- [ ] **Schritt 2: FAIL prüfen**, **Schritt 3: umsetzen**, **Schritt 4: PASS prüfen** (`dotnet test tests/Pagewing.App.Tests --filter-class "*UpdateWithChangesTests"`; die bestehenden Update-Tests bleiben grün).
- [ ] **Schritt 5: Commit** – `feat(app): Update fragt vorher nach ungespeicherten Änderungen (M9)`.

---

### Aufgabe 19: Interop-Muster und Messungen

**Dateien:**
- Neu: `tests/Pagewing.Core.Tests/Annotations/InteropSampleTests.cs` (läuft nur mit `PAGEWING_INTEROP_OUT=<Ordner>`, sonst übersprungen), Messnotizen in `Docs/DECISIONS.md`
- Ändern: `packaging/README.md` (Abschnitt „Annotationen im Fremdviewer prüfen“)

**Schnittstellen:** Verbraucht Aufgaben 6, 9, 11.

- [ ] **Schritt 1: Muster erzeugen.** Der Test schreibt je Quelle (`annotations.pdf`, `rotated-page.pdf`, `cropbox.pdf`, `text-basic.pdf`, `signed-docmdp-3.pdf`, `annotations-foreign.pdf`) eine mit Pagewing annotierte Datei **über die Speicher-Pipeline**. Run: `$env:PAGEWING_INTEROP_OUT="<Ordner>"; dotnet test tests/Pagewing.Core.Tests --filter-class "*InteropSampleTests"` – PASS, Dateien liegen im Ordner.
- [ ] **Schritt 2: Handprüfung durch den Projektinhaber** in Chrome und Acrobat Reader nach der Matrix aus Aufgabe 2 plus: Kommentar sichtbar; bei `signed-docmdp-3.pdf` zeigt Acrobat (sobald eine echt signierte Datei vorliegt, die der Projektinhaber selbst signiert und nicht eincheckt) die Signatur nach dem Annotieren nicht als verändert-ungültig. Ergebnis als Abnahmenachweis in der Roadmap; was nicht geprüft werden konnte, bleibt als offen markiert.
- [ ] **Schritt 3: Messen.** (a) Zeit vom Loslassen der Maus bis zum neuen scharfen Bild der Seite; (b) Speicherdauer bei `text-dense.pdf` (200 Seiten) und einer mehrere hundert MB großen Datei, mit der Dauer, in der der `PdfiumThread` belegt ist; (c) `--measure-scroll` mit einem annotierten Dokument gegen dasselbe ohne; (d) Startzeit-Check unverändert (`bench/startup`). Zahlen und Befund als Eintrag in `Docs/DECISIONS.md` („Annotieren und Speichern: Messwerte (M9)“); ein Budget nur, wenn ein Messwert es verlangt, dann als eigene Entscheidung.
- [ ] **Schritt 4: Commit** – `test: Interop-Muster und Messungen für Annotationen (M9)`.

---

### Aufgabe 20: Dokumentation und Abschluss

**Dateien:**
- Ändern: `Docs/DECISIONS.md`, `CLAUDE.md` (Abschnitt „Projektspezifisches“: Speichern, Annotationen, Stolperfallen), `CHANGELOG.md` (`[Unreleased]`), `Docs/superpowers/specs/2026-10-17-designsystem.md` (7.3, 7.7, neue Abschnitte), `Docs/superpowers/specs/2026-10-16-pagewing-design.md` (5.5), `src/Pagewing.App/Views/HelpCard` (Kürzel), `Docs/roadmap.json` + erzeugtes `Docs/ROADMAP.md`

**Schnittstellen:** Verbraucht alles.

- [ ] **Schritt 1: `DECISIONS`-Einträge** (je mit Datum und „(M9)“): Bindings; Speicher-Pipeline (Reihenfolge, Prüfungen, Neuöffnen, Passwort nur im Speicher); Erscheinungsbild-Strategie (aus Aufgabe 2); Rückgängig durch Neuanlegen und der Verlust herstellerspezifischer Schlüssel; DocMDP-Texte und die Aussage „nie gültig“; Grenze „alte Fassung und gelöschte Kommentare bleiben in der Datei“; Herunterfahren ohne Wiederherstellungsdatei; Update-Weg; dunkle Seite.
- [ ] **Schritt 2: `CLAUDE.md`** – Abschnitt „Speichern/Annotationen (seit M9)“ mit Typen, Orten und Stolperfallen (Reihenfolge der Prüfungen, `Editable`-Regel, `/NM`, Kürzel nur im Katalog, Tests schreiben nie neben `testdata`); `CHANGELOG.md` und Hilfekarte (Kürzel) nachziehen.
- [ ] **Schritt 3: Roadmap pflegen** – Aufgaben abhaken, Abnahmekriterien nur mit Nachweis (Test-Name oder Befehl und Ergebnis, Handprüfungen als offen, wenn nicht erfolgt), Status, Datum. `dotnet run tools/roadmap.cs -- generate`, dann `-- check` (Exit 0).
- [ ] **Schritt 4: Gesamtprüfung.** Run: `dotnet build Pagewing.slnx`, `dotnet test Pagewing.slnx`, `dotnet format Pagewing.slnx --verify-no-changes`, `dotnet run tools/roadmap.cs -- check` – alles grün, keine Warnungen; AOT-Publish von App und CLI (`dotnet publish src/Pagewing.App -c Release -r win-x64`, analog CLI) ohne neue Warnungen; Startzeit-Check in der CI unverändert.
- [ ] **Schritt 5: PR** nach `dev` (Titel `feat: Milestone 9 – Speichern & Textmarkierungen`), Beschreibung mit Befehlen und Ergebnissen, den offenen Handprüfungen (Acrobat, Chrome) und den Messwerten; Merge nach grüner CI.
