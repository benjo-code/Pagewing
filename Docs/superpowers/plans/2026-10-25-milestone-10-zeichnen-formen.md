# Milestone 10: Zeichnen & Formen – Implementierungsplan

> **Für ausführende Agenten:** PFLICHT-SKILL: `superpowers:subagent-driven-development` (empfohlen) oder `superpowers:executing-plans`, Aufgabe für Aufgabe. Schritte sind Checkboxen (`- [ ]`).

**Ziel:** Freihandzeichnungen, Rechtecke, Ellipsen, Linien, Pfeile und (je nach Spike) Textfelder lassen sich anlegen, auswählen, verschieben, in Größe und Aussehen ändern, löschen, rückgängig machen und sicher speichern; sie sehen in jedem Viewer gleich aus.

**Architektur:** Wie in M9: Rechnerisches (Geometrie, Glättung, Pfeil, Treffertest, Griffe, Verlauf) liegt UI-frei in `Pagewing.Core`; PDFium wird nur in `Pagewing.Pdfium` und nur auf dem `PdfiumThread` angefasst, jede Änderung ist ein kurzer Auftrag. Das Erscheinungsbild schreibt PDFiums Generator („Variante D“). Der Strich entsteht beim Ziehen auf einer eigenen Zeichenschicht der Ansicht ohne PDFium. Verschieben und Größe ändern ersetzen die Annotation unter demselben `/NM` (Löschen plus Neuanlegen in einem Auftrag). Zwei Spikes (Zeichenarten und Pfeil, Textfeld) stehen vor dem Bau.

**Tech Stack:** .NET 10 Native AOT, Avalonia 12.1, PDFium (`bblanchon.PDFium.*`, chromium/8100) über eigene `[LibraryImport]`-Bindings, xunit.v3 (Microsoft.Testing.Platform), Avalonia.Headless.

**Spec:** [2026-10-25-m10-zeichnen-formen-design.md](../specs/2026-10-25-m10-zeichnen-formen-design.md) (maßgeblich); Rahmen: [Teilprojekt 2](../specs/2026-10-24-teilprojekt2-annotieren-design.md); Roadmap „Milestone 10“; Design: [Designsystem](../specs/2026-10-17-designsystem.md), 7.3 und 7.7.

## Globale Randbedingungen

Gelten für jede Aufgabe (Wortlaut aus Spec und `CLAUDE.md`):

- PDFium nur über den `PdfiumThread`; Änderungen sind **kurze** Aufträge; der UI-Thread wartet nie auf PDFium. Konstanten und Funktionen gegen die Header von `chromium/8100` prüfen (`NativeBindingsTests.ConstantsMatchThePackageHeaders`), `unsigned long` immer `CULong`. Ein neuer `[UnmanagedCallersOnly]`-Rückruf kommt erst nach Prüfung in `UnmanagedCallbackTests.EveryUnmanagedCallbackIsReviewed`.
- Jede angelegte oder geänderte Annotation hat ein eigenes `/AP` als Form-XObject und das Flag Print; fehlt das `/AP` nach dem Erzeugen, ist es ein Fehler und die Annotation wird entfernt. `FPDF_PageToDevice` (bzw. `DisplayTransform`) ist die einzige Koordinatenumrechnung.
- Pfeil und Linie sind in der Datei Freihand-Annotationen (Ink); kein echtes `/Line`, kein eigener PDF-Schreiber, keine druckempfindliche Breite, keine Formenerkennung.
- Werkzeuge nur im Modus „Kommentieren“ (Design-Spec 7.7), nie in der Leseansicht. Kürzel nur im `CommandCatalog`; feldeigene Bindungen und `DocumentView.OnKeyDown` (Pfeile, Entf bei Auswahl) bleiben. Esc des Modus bleibt `DocumentView.TryEscape`.
- Startpfad: nichts Neues vor dem ersten scharfen Seitenbild; Zeichenschicht, Auswahl, Werkzeugerweiterungen und Lesen der Zeichnungen entstehen erst im Modus bzw. beim Öffnen des Panels. Startzeit-Budgets nie lockern.
- Grenzen von M9 gelten weiter (10.000 Annotationen je Dokument, 20.000 Einträge `/Annots` je Seite, `MaxLinkWork`); neu: Höchstzahl Punkte und Striche je Ink-Annotation beim Lesen und Anlegen.
- Zustände nie nur über Farbe; Kontrast WCAG 2.2 AA (Text 4,5:1, Ränder/Fokus/Griffe 3:1); Zielgröße der Griffe mindestens 24 DIP; Automation-Namen, `AccessibilityView="Content"` an `Grid`/`Border` mit Namen; keine Farb- oder Maßliterale im XAML. Nichts ausgegraut für spätere Versionen.
- Keine neuen Abhängigkeiten (sonst Lizenzmanifest, `ThirdPartyNotices.txt`, `DECISIONS`). Native AOT: keine Reflection, JSON nur mit Source-Generator.
- Tests schreiben nie in den echten Benutzerordner (`UserSettings.Detached()`, Temp-Ordner) und nie neben die Eingaben in `testdata`. Neue Test-PDFs: Herkunft/Lizenz in `testdata/pdfs/LICENSES.md`, Eintrag in `RenderCorpus.Skipped` (oder `Cases`), Erzeugung in `tools/make-testpdfs.cs` (`check` muss grün bleiben).
- UI-Texte Englisch **und** Deutsch (`Strings.en.resx`, `Strings.de.resx`, `Strings.cs`); kein Text sagt „rechtsgültig“. Code und Kommentare Englisch; Doku und Commits Deutsch (Conventional Commits, gezielt stagen, nie `git add .`, danach `git show --stat HEAD` prüfen). Testnamen ohne Unterstriche.
- Befehle: Build `dotnet build Pagewing.slnx`, Test `dotnet test Pagewing.slnx`, Format `dotnet format Pagewing.slnx --verify-no-changes`, Roadmap `dotnet run tools/roadmap.cs -- check`. Einzelne Tests: `dotnet test tests/Pagewing.Core.Tests --filter-class "*Name"` (analog `--filter-method`).
- Commit-Trailer jeder Aufgabe: `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.

## Festgelegte Werte (aus Spec; Startwerte aus dem Plan sind markiert)

| Größe | Wert |
|---|---|
| Farben der Zeichenwerkzeuge | Rot (Standard), Schwarz, Blau, Grün, Orange; Namen `DrawColor<Name>` in den Resources |
| Dicken | dünn 1 pt, mittel 2 pt (Standard), dick 4 pt |
| Füllung Rechteck/Ellipse | entfällt in M10 (Spike A: `/CA` gilt für Rand und Füllung gemeinsam; Spec: „sonst nur Umriss“) |
| Einrasten mit Umschalt | Linie/Pfeil auf Vielfache von 45°, Rechteck/Ellipse auf Quadrat/Kreis |
| Pfeilspitze (Startwert) | zwei Striche, Länge `max(8 pt, 4 × Dicke)`, Winkel 25° zum Schaft |
| Glättung (Startwert) | Vereinfachung Douglas-Peucker mit 0,5 pt, dann ein Chaikin-Durchgang; Spike A misst Dateigröße je 100 Striche |
| Treffertoleranz / Griffe (Startwert) | 6 DIP um die Linie; Griff gezeichnet 8 DIP, Trefferfläche 24 DIP |
| Verschieben per Tastatur | Pfeile 1 pt, Umschalt 10 pt |
| Grenzen Ink (Startwert) | `MaxInkStrokes = 1_000`, `MaxInkPoints = 100_000` je Annotation (Lesen und Anlegen) |

## Review-Fokus

Eingaben und Zustände, die die Spec nicht ausspricht und an denen Nutzer scheitern könnten, wahrscheinlichste zuerst; jeder Punkt hat seinen Test in der genannten Aufgabe:

1. **Klick ohne Ziehen, Strich mit einem Punkt, Rechteck/Linie ohne Ausdehnung:** legt nichts an, keine leere Annotation, Rückgängig bleibt unberührt (Aufgabe 4, 7 und 11).
2. **Abbruch mitten im Strich** (Esc, Zeiger verloren, Tab- oder Werkzeugwechsel, Dokument nach dem Speichern ausgetauscht): kein Rest-Strich, kein hängender Zustand, nichts im Verlauf (Aufgabe 11).
3. **Gedrehte Seite (90/180/270) und CropBox mit Versatz:** angelegt, verschoben und in der Größe geändert liegt die Zeichnung dort, wo der Zeiger war, und nach Speichern und Neuladen an derselben Stelle (Aufgabe 6).
4. **Feindliche oder fremde Zeichnungen** (`/InkList` mit Millionen Punkten, NaN, leeren Strichen, Square ohne `/Rect`, gestrichelter Rand, eigenes `/AP`): kein Absturz, Grenzen greifen, Verschieben/Größe gesperrt mit Grund, Löschen geht (Aufgabe 5 und 6).
5. **Verschieben/Ziehen über den Seitenrand oder auf Größe 0 und negativ, danach Rückgängig und Speichern:** auf die Seite begrenzt bzw. Mindestgröße, ein Schritt im Verlauf, gespeicherte Datei stimmt (Aufgabe 4, 7 und 12).
6. **Grenze von 10.000 Annotationen erreicht und tausende Striche auf einer Seite:** klare Meldung statt stillem Verwerfen; Scrollen und Panel bleiben bedienbar (Aufgabe 6, 13 und 14).

---

## Dateistruktur

| Datei | Verantwortung |
|---|---|
| `src/Pagewing.Pdfium/Native/Fpdf.cs`, `FpdfConstants.cs` (erweitert) | Bindings: Anlegen Ink/Square/Circle, `AddInkStroke`, Ink lesen, Rand/Dicke, Innenfarbe/Deckkraft |
| `src/Pagewing.Pdfium/PdfiumAnnotations.cs` (erweitert), `PdfiumDrawings.cs` (neu) | Zeichnungen lesen, anlegen, ersetzen (nur `PdfiumThread`); Grenzen gegen feindliche Dateien |
| `src/Pagewing.Pdfium/PdfiumDarkMarkups.cs` oder `PdfiumDarkDrawings.cs` | Zeichnungen auf der dunklen Seite |
| `src/Pagewing.Core/Annotations/AnnotationKind.cs` (erweitert), `DisplayPoint.cs`, `DrawingGeometry.cs`, `StrokeSimplifier.cs`, `ArrowGeometry.cs`, `DrawingTransform.cs`, `DrawingHitTest.cs`, `SelectionHandles.cs`, `DrawColor.cs`, `StrokeWidth.cs`, `ToolStyle.cs` (neu) | rein rechnerisch, ohne PDFium |
| `src/Pagewing.Core/Annotations/PdfAnnotation.cs`, `AnnotationEdit.cs`, `AnnotationHistory.cs`, `AnnotationEditor.cs` (erweitert) | Modell, Änderung, Verlauf, Editor-Operationen |
| `src/Pagewing.Core/Pdf/PdfDocument.Annotations.cs` (erweitert) | `CreateDrawingAsync`, `ReplaceDrawingAsync` |
| `src/Pagewing.Core/Settings/AppSettings.cs` (erweitert) | `ToolStyles` je Werkzeug |
| `src/Pagewing.App/ViewModels/TabViewModel.Drawing.cs` (neu), `TabViewModel.Annotate.cs` (erweitert) | Werkzeuge, Auswahl, Anlegen/Verschieben/Größe |
| `src/Pagewing.App/Controls/DrawingOverlay.cs` (neu) | Live-Strich, Vorschau der Form, Auswahlrahmen und Griffe |
| `src/Pagewing.App/Views/DocumentView.Drawing.cs`, `DocumentView.Selection.cs` (neu) | Zeiger- und Tastaturführung |
| `src/Pagewing.App/Views/AnnotationToolbar.axaml(.cs)`, `Commands/CommandCatalog.cs`, `Resources/Strings.*` (erweitert) | Leiste, Befehle, Texte |
| `src/Pagewing.App/ViewModels/CommentsViewModel.cs`, `Views/CommentsPanel.axaml` (erweitert) | neue Arten, Filter „Zeichnungen“, Sperrgrund |
| `tools/make-testpdfs.cs`, `testdata/pdfs/*` (erweitert) | Testdaten für Zeichnungen |
| `tests/Pagewing.Core.Tests/Annotations/*`, `tests/Pagewing.App.Tests/*` | Tests je Aufgabe |

---

### Aufgabe 1: Spike A – Erscheinungsbild der Zeichnungen und Erkennung des Pfeils (Gate)

Wegwerf-Test (`tests/Pagewing.Core.Tests/Spikes/DrawingSpikeTests.cs`, nicht einchecken). Beantwortet, was die folgenden Aufgaben festschreiben.

**Files:** Create (nicht einchecken): `tests/Pagewing.Core.Tests/Spikes/DrawingSpikeTests.cs`; Modify: `Docs/DECISIONS.md`.

- [ ] **Schritt 1:** Mit `FPDFPage_CreateAnnot` Ink, Square, Circle auf `annotations.pdf`, `rotated-page.pdf`, `cropbox.pdf`, `text-basic.pdf` anlegen (Farbe `FPDFAnnot_SetColor`, Innenfarbe/Deckkraft für die Füllung, Dicke über `FPDFAnnot_SetBorder`), einmal mit `FPDF_ANNOT` rendern (Variante D), speichern. Linie und Pfeil als Ink mit 1 bzw. 2–3 Strichen.
- [ ] **Schritt 2:** Messen und festhalten: Schreibt der Generator Dicke, Farbe, Füllung samt Deckkraft (Form-XObject, Flag Print)? Bleibt ein Merkmal zur Wiedererkennung von Pfeil/Linie (Kandidaten `/Subj`, `/IT`, `/NM`-Präfix) beim Speichern und in einem zweiten PDFium-Durchlauf erhalten? Welche Glättungstoleranz gibt kleine Dateien ohne sichtbare Ecken (Dateigröße je 100 Striche)?
- [ ] **Schritt 3:** Dunkle Seite messen: Wie zeichnet PDFium (Farbschema aus M6, `FPDF_CONVERT_FILL_TO_STROKE`) Ink, Square, Circle mit Rot/Schwarz/Blau auf `PageTheme.Dark`? (Erwartung: Strich wird zur Textfarbe; Pixelwerte festhalten.)
- [ ] **Schritt 4:** Dem Projektinhaber die erzeugten Dateien zur Handprüfung in Chrome und Acrobat Reader (Windows) geben (Prüfmatrix wie M9: gedrehte Seite, CropBox, Füllung, Pfeil). Ergebnis als Eintrag in `Docs/DECISIONS.md` („Zeichnungen: Erscheinungsbild, Pfeil als Ink, Merkmal zur Wiedererkennung, Glättung (M10)“); bei negativem Ergebnis für die Füllung entfällt sie aus M10, bei fehlendem Merkmal bleibt jeder Pfeil „Freihand“ (Spec).
- [ ] **Schritt 5:** Spike löschen, Eintrag committen (`docs: Zeichnungen – Spike-Ergebnisse (M10)`).

### Aufgabe 2: Spike B – Textfeld (Gate für Aufgabe 15)

**Files:** Create (nicht einchecken): `tests/Pagewing.Core.Tests/Spikes/TextBoxSpikeTests.cs`; Modify: `Docs/DECISIONS.md`.

- [ ] **Schritt 1:** Varianten bauen: (a) FreeText mit PDFiums Generator (`/DA`, Schrift als Ressource), (b) FreeText mit selbst gebautem `/AP`, (c) Stempel, dessen Inhalt Textobjekte sind (`FPDFAnnot_AppendObject`, `FPDFText_LoadStandardFont` bzw. eingebettete Schrift). Prüfmatrix: „Grüße aus Köln – 5 €“, ein Umbruch, zwei Schriftgrößen, gedrehte Seite, CropBox.
- [ ] **Schritt 2:** Handprüfung durch den Projektinhaber in Chrome und Acrobat Reader (Acrobat gehört zum Maßstab, Spec). Danach `DECISIONS`-Eintrag („Textfeld: Erscheinungsbild und Entscheidung (M10)“) mit einem der Ausgänge: Variante X trägt → Aufgabe 15 wird gebaut; keine trägt → Aufgabe 15 entfällt aus M10, Roadmap bekommt Milestone 10.1 „Textfelder“ (Scope-Änderung in `Docs/roadmap.json`).
- [ ] **Schritt 3:** Spike löschen, Eintrag committen.

### Aufgabe 3: Testdaten

**Files:** Modify: `tools/make-testpdfs.cs`, `testdata/pdfs/LICENSES.md`, `tests/Pagewing.Render.Tests/RenderCorpus.cs`; Create: `testdata/pdfs/drawings-foreign.pdf`.

**Interfaces:** Produces: `drawings-foreign.pdf` mit Seite 1: Ink (zwei Striche, eigenes `/AP`), Ink ohne `/AP`, Square mit gestricheltem `/BS`, Square mit `/BE` (Wolke), Square mit eigenem `/AP` und `/CA 0.5`, Circle durchgezogen ohne `/AP`, `/Line` mit `/LE` (Pfeil), Ink mit `/IRT`-Antwort darauf; Seite 2 gedreht (`/Rotate 90`) mit CropBox-Versatz und einem Ink.

- [ ] **Schritt 1 (Test zuerst):** In `AnnotationTestFiles` (Tests) die Konstante `DrawingsForeign` und einen Test `DrawingsForeignFixtureListsAllEntries` (`AnnotationReadTests`): `GetAnnotationsAsync(0)` liefert acht Einträge (Subtypes wie oben); schlägt fehl, weil die Datei fehlt.
- [ ] **Schritt 2:** Datei in `tools/make-testpdfs.cs` erzeugen (deterministisch, unkomprimiert), `dotnet run tools/make-testpdfs.cs -- generate`, Eintrag in `LICENSES.md` (selbst erzeugt) und `RenderCorpus.Skipped`. Test grün, `make-testpdfs.cs -- check` grün.
- [ ] **Schritt 3:** Commit `test: Testdatei drawings-foreign.pdf (M10)`.

### Aufgabe 4: Modell und Geometrie (Core, rein)

**Files:** Create: `DisplayPoint.cs`, `DrawingGeometry.cs`, `StrokeSimplifier.cs`, `ArrowGeometry.cs`, `DrawingTransform.cs`, `DrawColor.cs`, `StrokeWidth.cs`, `ToolStyle.cs` (alle `src/Pagewing.Core/Annotations/`); Modify: `AnnotationKind.cs`; Test: `tests/Pagewing.Core.Tests/Annotations/DrawingGeometryTests.cs`, `StrokeSimplifierTests.cs`, `ArrowGeometryTests.cs`, `DrawingTransformTests.cs`.

**Interfaces:**
- Produces: `AnnotationKind` bekommt `Ink, Rectangle, Ellipse, Line, Arrow, TextBox` vor `Other`. `readonly record struct DisplayPoint(double X, double Y)` (Seitenpunkte, Ursprung oben links). `sealed record DrawingGeometry` mit `Kind`, `IReadOnlyList<IReadOnlyList<DisplayPoint>> Strokes`, `PageRect Box`, `double Width` und Fabriken `Ink(strokes, width)`, `Line(start, end, width)`, `Arrow(start, end, width)`, `Rectangle(box, width)`, `Ellipse(box, width)`; `bool IsEmpty` (kein Punkt/keine Ausdehnung). `enum DrawColor { Red, Black, Blue, Green, Orange }` mit `DrawPalette.ColorOf`, `NameKey` (`DrawColor<Name>`); `enum StrokeWidth { Thin, Medium, Thick }` mit `PointsOf` (1, 2, 4); `sealed record ToolStyle(DrawColor Color, StrokeWidth Width)`. `static class StrokeSimplifier { IReadOnlyList<DisplayPoint> Simplify(IReadOnlyList<DisplayPoint> points, double tolerance); IReadOnlyList<DisplayPoint> Smooth(IReadOnlyList<DisplayPoint> points); }`. `static class ArrowGeometry { IReadOnlyList<IReadOnlyList<DisplayPoint>> Strokes(DisplayPoint start, DisplayPoint end, double width, bool head); bool TryRecognize(IReadOnlyList<IReadOnlyList<DisplayPoint>> strokes, out DisplayPoint start, out DisplayPoint end, out bool head); DisplayPoint SnapToAngle(DisplayPoint anchor, DisplayPoint point, double stepDegrees); PageRect SquareBox(DisplayPoint anchor, DisplayPoint point); }`. `static class DrawingTransform { DrawingGeometry Move(DrawingGeometry g, double dx, double dy, PageRect page); DrawingGeometry Resize(DrawingGeometry g, PageRect newBox, PageRect page); }` (begrenzt auf die Seite, Mindestgröße 4 pt je Seite des Rahmens; Linie/Pfeil: `MoveEndpoint(g, bool start, DisplayPoint to)`).

- [ ] **Schritt 1 (Tests zuerst, rot):** `StrokeSimplifierTests.CollinearPointsCollapseToTwo`, `…KeepsACornerBeyondTolerance`, `…SinglePointStaysSingle`; `ArrowGeometryTests.ArrowHasShaftAndTwoHeadStrokesOfTheStartValue` (Länge `max(8, 4×Breite)`, 25°), `…TryRecognizeFindsItsOwnArrowAndLine`, `…RejectsFreehandWithThreeStrokes`, `…SnapToAngleRoundsToMultiplesOf45`, `…SquareBoxKeepsTheLongerSide`; `DrawingTransformTests.MoveIsClampedToThePage`, `…ResizeKeepsMinimumSize`, `…MoveEndpointKeepsTheOtherEnd`, `…ScaleOfInkStrokesIsNonUniform`; `DrawingGeometryTests.ZeroExtentRectangleIsEmpty`, `…SinglePointStrokeIsEmpty`.
- [ ] **Schritt 2:** Typen wie unter Interfaces umsetzen; Tests laufen lassen (`--filter-namespace "*Annotations"`), grün.
- [ ] **Schritt 3:** Commit `feat(core): Zeichengeometrie, Glättung und Pfeil (M10)`.

### Aufgabe 5: Bindings und Zeichnungen in `Pagewing.Pdfium`

**Files:** Modify: `Native/Fpdf.cs`, `Native/FpdfConstants.cs`, `PdfiumAnnotations.cs`; Create: `PdfiumDrawings.cs`; Test: `tests/Pagewing.Core.Tests/Pdfium/NativeBindingsTests.cs`, `tests/Pagewing.Core.Tests/Annotations/DrawingReadTests.cs`.

**Interfaces:**
- Consumes: Aufgabe 1 (Merkmal `/Subj` = `Arrow`/`Line` plus Formprüfung der InkList, Randdicke über `/Border`; `/Rect` eines Pfeils vor dem Erzeugen des Erscheinungsbilds um die Spitze erweitern, sonst schneidet die BBox die Spitze ab; `/Rect` eines Ink wächst bei jedem Erzeugen um die halbe Dicke je Seite und wird deshalb vor jedem Erzeugen neu gesetzt), Aufgabe 3 (`drawings-foreign.pdf`), Aufgabe 4 (`DrawingGeometry`).
- Produces (intern): `PdfiumDrawings.Create(PdfiumPage page, DrawingGeometry geometry, (byte R, byte G, byte B) color, string? contents, string? author, string name, string date) -> int` (Index; legt Ink/Square/Circle an, setzt `/Rect` mit Dicke als Rand, Striche, Farbe, Dicke als `/Border`, Flag Print, `/NM`, `/M`, `/CreationDate`, Merkmal für Pfeil/Linie; bei Fehler bleibt nichts auf der Seite); `PdfiumDrawings.ReadGeometry(PdfiumPage, int index, PdfiumPageText.PageTransform) -> DrawingGeometry?`; `PdfiumDrawings.IsGeometryEditable(PdfiumPage, int index) -> bool` (kein `/IRT` darauf, Rand durchgezogen, kein `/BE`, `/PDFIUM_HasGeneratedAP` gesetzt oder kein `/AP`); `PdfiumAnnotations.IsDrawing(int subtype)`. Konstanten `MaxInkStrokes`, `MaxInkPoints` laut Tabelle.

- [ ] **Schritt 1 (Tests zuerst, rot):** `NativeBindingsTests` um die neuen Konstanten ergänzen. `DrawingReadTests.ReadsInkStrokesInPageCoordinates` (Seite 1 von `drawings-foreign.pdf`: erster Ink hat zwei Striche), `…ReadsSquareAndCircleBox`, `…RotatedPageMapsPointsToTheDisplayedPage`, `…HostileInkListIsCapped` (Datei im Test aus `drawings-foreign.pdf` mit `/InkList` aus 200.000 Punkten und einem NaN-Wert abgeleitet: kein Absturz, höchstens `MaxInkPoints`, NaN-Strich übersprungen), `…ForeignOwnAppearanceIsNotGeometryEditable`, `…DashedBorderAndCloudAreNotGeometryEditable`, `…AnnotationWithRepliesIsNotEditable`.
- [ ] **Schritt 2:** Bindings und `PdfiumDrawings` umsetzen (Funktionen und Konstanten gegen `fpdf_annot.h` prüfen); Lesen folgt dem Muster von `PdfiumAnnotations.ReadOne` (Grenzen, `IsSane`).
- [ ] **Schritt 3:** Tests grün (`--filter-class "*DrawingRead*"` und `*NativeBindings*`), Format prüfen. Commit `feat(pdfium): Zeichnungen lesen und anlegen (M10)`.

### Aufgabe 6: `PdfDocument` – Zeichnungen anlegen, ersetzen, lesen

**Files:** Modify: `PdfDocument.Annotations.cs`, `PdfAnnotation.cs`, `AnnotationEdit.cs`, `PdfiumAnnotations.cs`; Test: `tests/Pagewing.Core.Tests/Annotations/DrawingRoundTripTests.cs`.

**Interfaces:**
- Consumes: Aufgabe 4 und 5.
- Produces: `PdfAnnotation` bekommt `DrawingGeometry? Drawing` (gelesen für Ink, Square, Circle; Pfeil/Linie mit Merkmal als `Kind` Arrow/Line, sonst `Ink`) und `bool GeometryEditable`; `Editable` gilt jetzt auch für Ink/Square/Circle ohne Antworten (Löschen, Kommentar), `GeometryEditable` für Verschieben/Größe/Farbe/Dicke. `AnnotationEdit` bekommt `DrawingGeometry? Geometry`. `PdfDocument.CreateDrawingAsync(int page, DrawingGeometry geometry, AnnotationColor color, string? contents, string? author, CancellationToken ct = default) -> Task<PdfAnnotation>`; `PdfDocument.ReplaceDrawingAsync(AnnotationLocator locator, DrawingGeometry geometry, AnnotationColor color, CancellationToken ct = default) -> Task<PdfAnnotation>` (Löschen und Anlegen unter demselben `/NM`, Kommentar/Autor/Datum der Erstellung bleiben, in **einem** Auftrag; scheitert das Anlegen, wird das Alte wiederhergestellt); `UpdateAnnotationAsync` ändert für Zeichnungen Farbe/Dicke über Ersetzen. Fehlt das `/AP` nach dem Erzeugen: Annotation entfernt, `InvalidOperationException`. Erreichen von `MaxAnnotations`: `PdfAnnotationLimitException`.

- [ ] **Schritt 1 (Tests zuerst, rot), auf Temp-Kopien:** `DrawingRoundTripTests.InkSurvivesSaveAndReload` (Striche, Farbe, Dicke, Kommentar unverändert), `…RectangleAndEllipseSurvive`, `…ArrowIsRecognisedAfterReload` (Kind Arrow, Start/Ende gleich), `…AppearanceIsAFormXObjectWithPrintFlag`, `…RotatedPageKeepsPointsOnTheirPlace`, `…CropBoxOffsetKeepsPointsOnTheirPlace`, `…MoveKeepsNameCommentAndAuthor`, `…ReplaceRestoresTheOldOneWhenCreationFails` (Fehlerinjektion), `…ForeignWithOwnAppearanceCannotBeReplaced`, `…ExistingBytesStayThePrefix` (Speicher-Pipeline), `…LimitIsReported` (`PdfAnnotationLimitException`).
- [ ] **Schritt 2:** Umsetzen; Tests grün (`--filter-class "*DrawingRoundTrip*"`). Commit `feat(core): Zeichnungen anlegen, ersetzen und speichern (M10)`.

### Aufgabe 7: Editor, Verlauf und Rückgängig für Zeichnungen

**Files:** Modify: `AnnotationEditor.cs`, `AnnotationHistory.cs`; Test: `tests/Pagewing.Core.Tests/Annotations/AnnotationEditorTests.cs`, `AnnotationHistoryTests.cs`.

**Interfaces:**
- Consumes: Aufgabe 6.
- Produces: `AnnotationEditor.ApplyDrawingAsync(int page, DrawingGeometry geometry, AnnotationColor color, string? contents, string? author) -> Task<PdfAnnotation?>` (`null` bei `geometry.IsEmpty`, nichts ändert sich); `AnnotationEditor.ReplaceGeometryAsync(AnnotationLocator locator, DrawingGeometry geometry) -> Task<PdfAnnotation>` (Verschieben/Größe; **ein** Schritt im Verlauf als `AnnotationCommand.Edit(before, after)`); `DeleteAsync` und `UpdateAsync` akzeptieren Zeichnungen; Rückgängig/Wiederholen erzeugen Zeichnungen unter demselben `/NM` neu. `Move` mit der Maus ruft `ReplaceGeometryAsync` nur beim Loslassen.

- [ ] **Schritt 1 (Tests zuerst, rot):** `AnnotationEditorTests.EmptyDrawingChangesNothing`, `…UndoOfCreateDeletesTheDrawing`, `…UndoOfMoveRestoresTheOldGeometry`, `…RedoOfMoveAppliesItAgain`, `…UndoOfDeleteRecreatesUnderTheSameName`, `…ColorAndWidthChangeAreOneStep`, `…LockedForeignDrawingRefusesReplace`, `…DocumentLimitSurfacesAsException`; `AnnotationHistoryTests.MoveCountsAsOneVersionStep`.
- [ ] **Schritt 2:** Umsetzen; Tests grün (`--filter-class "*AnnotationEditor*"`, `*AnnotationHistory*`). Commit `feat(core): Editor-Operationen für Zeichnungen (M10)`.

### Aufgabe 8: Einstellungen für Werkzeugstile

**Files:** Modify: `src/Pagewing.Core/Settings/AppSettings.cs`, JSON-Kontext; Test: `tests/Pagewing.Core.Tests/Settings/SettingsStoreTests.cs`.

**Interfaces:** Produces: `AppSettings.ToolStyles` als `Dictionary<AnnotationKind, ToolStyle>` (`{ get; set; }`; Schlüssel nur Ink, Rectangle, Ellipse, Line, Arrow, TextBox; Standard Rot/mittel); Validierung in `Normalize` (unbekannte Schlüssel und Werte außerhalb der Enums fallen auf den Standard); `AppSettings.StyleOf(AnnotationKind) -> ToolStyle`.

- [ ] **Schritt 1 (Test zuerst):** `SettingsStoreTests.ToolStylesRoundTripAndFallBackToDefaults` (Schreiben/Lesen mit Source-Generator, `.bad`-Verhalten unverändert, ungültige Zahl → Standard); rot.
- [ ] **Schritt 2:** Umsetzen, grün (`--filter-class "*SettingsStore*"`). Commit `feat(core): Werkzeugstile als Einstellung (M10)`.

### Aufgabe 9: Dunkle Seite und Druck

**Files:** Modify: `PdfiumDarkMarkups.cs` (oder neu `PdfiumDarkDrawings.cs`), `PdfiumRenderWork.cs`; Test: `tests/Pagewing.App.Tests/DarkPageMarkupTests.cs`, `PrintSupportTests` (vorhanden, wo der Druck der Markierungen geprüft wird).

**Interfaces:** Consumes: Messwerte aus Aufgabe 1, Schritt 3. Produces: Ink, Square, Circle behalten auf `PageTheme.Dark` ihre Farbe (auf 3:1 gegen `page.darkBackground` aufgehellt, Schwarz → Grau wie bei den Linien; Spike A: PDFium zeichnet jeden Strich in der Vordergrundfarbe (227,229,232)); helle Seite und Druck lesen und malen nichts.

- [ ] **Schritt 1 (Tests zuerst, rot):** `DarkPageDrawingTests.RedInkKeepsItsHueOnTheDarkPage`, `…BlackInkIsLightenedToThreeToOne`, `…LightPageReadsNothing` (Zähler wie `PdfDocument.DarkMarkupScans`), `…RotatedPageStillCoversPdfiumsStroke`, `…SearchHitOnTopStillVisible`; im Druck-Test: ungespeicherte Zeichnung erscheint im gedruckten Bild (hell).
- [ ] **Schritt 2:** Umsetzen mit dem Muster von `PdfiumDarkMarkups` (Grenzen `MaxPaintedLayers` usw. mitführen); Tests grün (`--filter-class "*DarkPage*"`). Commit `feat(pdfium): Zeichnungen auf der dunklen Seite (M10)`.

### Aufgabe 10: Befehle, Werkzeugzustand und Texte

**Files:** Create: `src/Pagewing.App/ViewModels/TabViewModel.Drawing.cs`; Modify: `TabViewModel.Annotate.cs`, `Commands/CommandCatalog.cs`, `Resources/Strings.en.resx`, `Strings.de.resx`, `Strings.cs`; Test: `tests/Pagewing.App.Tests/DrawToolsTests.cs`, `CommandCatalogTests.cs`.

**Interfaces:**
- Consumes: Aufgabe 7 und 8.
- Produces: `TabViewModel.ActiveTool` akzeptiert die neuen Arten (`IsMarkupKind` wird `IsToolKind`); `bool IsSelectTool` und `SelectToolCommand` (schließt `ActiveTool` aus); `ToolStyle ActiveStyle`, `SetStyleCommand` (Parameter `ToolStyle`, schreibt in die Einstellungen mit `with`); das Werkzeug bleibt nach einem Strich aktiv. Katalogeinträge `annotate.select`, `annotate.ink`, `annotate.rectangle`, `annotate.ellipse`, `annotate.line`, `annotate.arrow` (`annotate.textbox` erst mit Aufgabe 15) in der Gruppe `edit`, Kürzel ohne Kollision (Test `CommandCatalogTests` prüft Eindeutigkeit und dass die Hilfekarte sie führt); Texte DE/EN inkl. `DrawColor<Name>` und Dickenstufen.

- [ ] **Schritt 1 (Tests zuerst, rot):** `DrawToolsTests.ToolStaysActiveAfterAStroke`, `…SelectToolExcludesDrawingTools`, `…StyleIsRememberedPerTool`, `…ToolsAreOnlyAvailableInTheMode`, `…CommandsAppearInThePaletteOnlyWhenRunnable`, `…EscFirstCancelsASelectionThenEndsTheMode`; `CommandCatalogTests` um die neuen Einträge.
- [ ] **Schritt 2:** Umsetzen, grün. Commit `feat(app): Zeichenwerkzeuge als Befehle und Zustand (M10)`.

### Aufgabe 11: Zeichnen – Zeichenschicht und Zeigerführung

**Files:** Create: `Controls/DrawingOverlay.cs`, `Views/DocumentView.Drawing.cs`; Modify: `Views/DocumentView.axaml(.cs)`, `TabViewModel.Drawing.cs`; Test: `tests/Pagewing.App.Tests/DrawingInputTests.cs`.

**Interfaces:**
- Consumes: Aufgabe 4 und 10. Produces: `DrawingOverlay` (zeichnet den laufenden Strich oder die Vorschau der Form in Viewport-Koordinaten, ungültig macht es nur das Rechteck des Strichs); `TabViewModel.ApplyDrawingAsync(int page, DrawingGeometry geometry) -> Task` (ruft `AnnotationEditor.ApplyDrawingAsync` mit Stil und Autor, meldet Fehler und die Grenze von 10.000 über die vorhandene Fehlerkarte/Meldung). Eingabe: Maus und Stift (`PointerType.Pen`) zeichnen, Finger (`PointerType.Touch`) scrollt/zoomt wie sonst; Umschalt rastet ein (Aufgabe 4); Loslassen legt an; Esc, `PointerCaptureLost`, Tab-/Werkzeugwechsel und Dokumenttausch verwerfen den Strich.

- [ ] **Schritt 1 (Tests zuerst, rot, Headless):** `DrawingInputTests.DragWithInkMakesOneAnnotationOnRelease`, `…ClickWithoutDragMakesNothing`, `…EscDuringTheStrokeDiscardsIt`, `…CaptureLostDiscardsIt`, `…TabSwitchDiscardsIt`, `…TouchDoesNotDrawButScrolls`, `…PenDraws`, `…ShiftSnapsALineTo45Degrees`, `…ShiftMakesASquare`, `…StrokeIsOnThePageUnderThePointerOnARotatedPage`, `…NothingIsCreatedBeforeTheFirstSharpPage`, `…OverlayInvalidatesOnlyTheStrokeRectangle` (Messung des ungültig gemachten Rechtecks).
- [ ] **Schritt 2:** Umsetzen; Seitenzuordnung über die vorhandene Seiten-Hit-Test-Logik der `DocumentView`, Umrechnung Viewport → Seitenpunkte über `DocumentLayout`. Tests grün (`--filter-class "*DrawingInput*"`). Commit `feat(app): Zeichnen mit Maus und Stift (M10)`.

### Aufgabe 12: Auswählen, Verschieben, Größe ändern, Tastatur

**Files:** Create: `Core/Annotations/DrawingHitTest.cs`, `Core/Annotations/SelectionHandles.cs`, `Views/DocumentView.Selection.cs`; Modify: `DrawingOverlay.cs`, `TabViewModel.Drawing.cs`, `DocumentView.axaml.cs` (`OnKeyDown`); Test: `tests/Pagewing.Core.Tests/Annotations/DrawingHitTestTests.cs`, `SelectionHandlesTests.cs`, `tests/Pagewing.App.Tests/DrawingSelectionTests.cs`.

**Interfaces:**
- Consumes: Aufgabe 4, 7, 10, 11. Produces: `static class DrawingHitTest { PdfAnnotation? Pick(IReadOnlyList<PdfAnnotation> onPage, DisplayPoint point, double tolerance); }` (Zeichnungen vor Markierungen, spätere vor früheren); `static class SelectionHandles { IReadOnlyList<Handle> For(PdfAnnotation annotation); PageRect ResizeBy(PageRect box, HandleKind handle, DisplayPoint to, bool keepAspect); }` (acht Randgriffe für Freihand/Formen, zwei Endpunkte für erkannte Linie/Pfeil, Rahmen ohne Griffe für Markierungen und gesperrte); `TabViewModel.SelectedAnnotation`, `SelectNextCommand`/`SelectPreviousCommand` (Lesereihenfolge), `NudgeCommand(dx, dy)`, `DeleteSelectionCommand`; Rahmen und Griffe tragen Automation-Namen, Zustand nicht nur über Farbe. Nur `DocumentView.OnKeyDown` bindet Pfeile, Tab, Entf, Enter bei vorhandener Auswahl im Modus (sonst scrollen die Pfeile wie bisher).

- [ ] **Schritt 1 (Tests zuerst, rot):** Core: `DrawingHitTestTests.DrawingWinsOverMarkup`, `…ToleranceHitsAThinLine`, `…ShapeHitsOnlyTheOutline`; `SelectionHandlesTests.ArrowHasTwoHandles`, `…InkHasEight`, `…MarkupHasNone`, `…ResizeKeepsMinimumSize`, `…ResizeNeverLeavesThePage`. App: `DrawingSelectionTests.ClickSelectsTopmostEditable`, `…DragMovesAsOneUndoStep`, `…DragBeyondThePageEdgeIsClamped`, `…ArrowKeysMoveByOnePointAndTenWithShift`, `…TabWalksTheAnnotationsOfThePageInReadingOrder`, `…DeleteKeyDeletes`, `…EnterOpensTheComment`, `…LockedForeignDrawingOnlyOffersCommentAndDelete`, `…MarkupIsSelectableButNotMovable`, `…HandlesHaveA24DipHitArea`, `…SelectionClearsWhenTheModeEnds`.
- [ ] **Schritt 2:** Umsetzen; Verschieben zeigt die Vorschau im Overlay und ruft `ReplaceGeometryAsync` nur beim Loslassen (Pfeiltasten: zusammenfassen, ein Schritt je Tastenfolge innerhalb 400 ms). Tests grün. Commit `feat(app): Zeichnungen auswählen, verschieben und ändern (M10)`.

### Aufgabe 13: Werkzeugleiste und Panel „Kommentare“

**Files:** Modify: `Views/AnnotationToolbar.axaml(.cs)`, `ViewModels/CommentsViewModel.cs`, `Views/CommentsPanel.axaml`, `Resources/Strings.*`; Test: `tests/Pagewing.App.Tests/AnnotationToolbarTests.cs`, `CommentsViewModelTests.cs`, `CommentsPanelTests.cs`.

**Interfaces:** Consumes: Aufgabe 10 und 12. Produces: Leiste mit Auswählen, Freihand, Rechteck, Ellipse, Linie, Pfeil, Farbe (fünf `DrawColor`), Dicke (drei Stufen); Panel: Einträge für Zeichnungen mit Symbol und Text (Art, kein Bild nötig), Filter „Zeichnungen“, Gruppierung so, dass 200 Striche das Panel nicht unbenutzbar machen (eine Zeile „Freihand, n Striche“ je Seite ab 5 kommentarlosen Strichen, aufklappbar), Sperrgrund bei gesperrten Fremdzeichnungen.

- [ ] **Schritt 1 (Tests zuerst, rot):** `AnnotationToolbarTests.ToolButtonsHaveAutomationNamesAndStateNotOnlyColor`, `…ColorButtonsMeetThreeToOne`, `…KeyboardReachesEveryTool`; `CommentsViewModelTests.ListsDrawingsWithKind`, `…GroupsManyCommentlessStrokes`, `…FilterDrawings`, `…LockedReasonIsShown`; `CommentsPanelTests.TwoHundredStrokesStayScrollable` (Headless, virtualisiert).
- [ ] **Schritt 2:** Umsetzen, Texte beider Sprachen, `LocalizationTests` grün. Commit `feat(app): Werkzeugleiste und Panel für Zeichnungen (M10)`.

### Aufgabe 14: Interop-Muster und Messungen

**Files:** Modify: `tests/Pagewing.Core.Tests/Annotations/InteropSampleTests.cs`, `AnnotationMeasurementTests.cs`, `packaging/README.md` („Annotationen im Fremdviewer prüfen“); Modify: `Docs/DECISIONS.md`.

- [ ] **Schritt 1:** `InteropSampleTests` um Muster je Art erweitern (Freihand, Rechteck, Ellipse, Linie, Pfeil; gedrehte Seite, CropBox, `drawings-foreign.pdf`, verschoben, Farbe geändert); Prüfliste in `packaging/README.md` ergänzen. Sie laufen nur mit `PAGEWING_INTEROP_OUT=<ordner>`.
- [ ] **Schritt 2:** `AnnotationMeasurementTests` (nur mit `PAGEWING_MEASURE_OUT`) messen: Zeigerereignis bis sichtbarer Strich (Ziel: höchstens ein Frame hinterher), Dauer des Auftrags beim Loslassen, `--measure-scroll` mit 1000 Strichen auf einer Seite und mit `text-dense.pdf`, Dateigröße je 100 Striche mit/ohne Glättung, Speichern mit 1000 Strichen. Werte als Eintrag „Zeichnen: Messwerte (M10)“ in `DECISIONS`.
- [ ] **Schritt 3:** `dotnet run bench/startup/startup.cs -- --case cold` (und `warm`) unverändert gegen Budget. Commit `test: Interop-Muster und Messwerte für Zeichnungen (M10)`.

### Aufgabe 15: Textfeld (bedingt: nur wenn Spike B ein tragendes Verfahren zeigt)

**Files:** Create: `Core/Annotations/TextBoxSpec.cs`, `Pdfium/PdfiumTextBoxes.cs`, `App/Views/TextBoxEditor.axaml(.cs)`; Modify: `PdfDocument.Annotations.cs`, `AnnotationEditor.cs`, `CommandCatalog.cs`, `AnnotationToolbar`, `Strings.*`; Test: `TextBoxRoundTripTests.cs`, `TextBoxEditorTests.cs`.

**Interfaces:** Consumes: `DECISIONS`-Eintrag aus Aufgabe 2. Produces: `TextBoxSpec(PageRect Box, string Text, double FontSize, AnnotationColor Color)`; `PdfDocument.CreateTextBoxAsync`, `AnnotationEditor.ApplyTextBoxAsync` / `ReplaceTextBoxAsync` (Text ändern, verschieben, Größe), `annotate.textbox`; ein einzeiliges bis mehrzeiliges Eingabefeld direkt auf der Seite (Strg+Enter/Fokusverlust übernimmt, Esc verwirft; Strg+Z im Feld macht Text rückgängig, nicht die Annotation).

- [ ] **Schritt 1 (Tests zuerst, rot):** `TextBoxRoundTripTests.GermanUmlautsAndEuroSurvive` („Grüße aus Köln – 5 €“), `…LineBreakAndTwoSizesSurvive`, `…RotatedPageAndCropBox`, `…AppearanceIsAFormXObjectWithPrintFlag`, `…EditingTheTextReplacesTheAppearance`; `TextBoxEditorTests.EmptyTextCreatesNothing`, `…EscDiscards`, `…CtrlZInTheFieldUndoesTextOnly`.
- [ ] **Schritt 2:** Gemäß Spike-Ausgang umsetzen, Tests grün; ohne tragenden Ausgang diese Aufgabe überspringen, `Docs/roadmap.json` bekommt M10.1 (Aufgabe 2, Schritt 2). Commit `feat: Textfelder (M10)`.

### Aufgabe 16: Dokumentation, Roadmap und Abschluss

**Files:** Modify: `Docs/DECISIONS.md`, `CHANGELOG.md`, `CLAUDE.md`, `Docs/superpowers/specs/2026-10-17-designsystem.md` (7.3, 7.7), `Docs/superpowers/specs/2026-10-16-pagewing-design.md` (5.5), Hilfekarte (Kürzel aus dem Katalog, automatisch), `Docs/roadmap.json`, `Docs/ROADMAP.md` (generiert).

- [ ] **Schritt 1:** `DECISIONS`-Einträge (Pfeil als Ink und Merkmal, Glättung, Bearbeitbar-Regel `GeometryEditable`, Ersetzen unter gleichem `/NM`, dunkle Seite, Grenzen Ink, Stift/Finger, Textfeld-Entscheidung); `CHANGELOG.md` unter `[Unreleased]`; `CLAUDE.md` „Speichern und Annotationen“ (neue Typen, Stolperfallen); Design-Spec 7.7 (Leiste, Auswahl, Griffe, Tastatur) und 7.3 (Panel).
- [ ] **Schritt 2:** Roadmap pflegen: Aufgaben abhaken mit Nachweis, Abnahmekriterien nur abhaken, wenn geprüft (Test-Namen, Befehl und Ergebnis); Handprüfungen im Fremdviewer als offen markieren oder auf Wunsch des Projektinhabers zu `v0.2.0` verschieben (Scope-Änderung); `dotnet run tools/roadmap.cs -- generate`, `-- check`.
- [ ] **Schritt 3:** Gesamtprüfung und Nachweis in der Roadmap: `dotnet build Pagewing.slnx`, `dotnet test Pagewing.slnx` (Debug und `-c Release`), `dotnet format Pagewing.slnx --verify-no-changes`, `dotnet run tools/roadmap.cs -- check`, `dotnet run tools/make-testpdfs.cs -- check`, `dotnet run tools/licenses.cs -- check`, Native-AOT-Publish von App und CLI (`win-x64`) ohne Warnungen, `dotnet run tools/smoke-cli.cs -- <pagewing.exe>`. Nicht prüfbare Punkte (Linux-Tests, Handprüfungen, Hardware) ausdrücklich als offen mit Befehl eintragen.
- [ ] **Schritt 4:** PR nach `dev` (Squash), Claude merged nach grüner CI (CLAUDE.md „Git & Branches“).
