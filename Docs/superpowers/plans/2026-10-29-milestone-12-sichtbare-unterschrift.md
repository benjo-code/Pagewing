# Milestone 12: Sichtbare Unterschrift – Implementierungsplan

> **Für ausführende Agenten:** PFLICHT-SKILL: `superpowers:subagent-driven-development` (empfohlen) oder `superpowers:executing-plans`, Aufgabe für Aufgabe. Schritte sind Checkboxen (`- [ ]`).

**Ziel:** Eine Unterschrift lässt sich zeichnen oder (aus einem Bild, lokal freigestellt) importieren, lokal speichern und wiederverwenden, auf einer Seite platzieren, verschieben, skalieren und mit der normalen Pipeline speichern; sie sieht in jedem Viewer gleich aus, und der Dialog sagt offen, dass sie keine handschriftliche Unterschrift ersetzt.

**Architektur:** Wie in M9 und M10: Rechnerisches (Freistellen, Platzierung, Bibliothek) liegt UI-frei in `Pagewing.Core`; PDFium wird nur in `Pagewing.Pdfium` und nur auf dem `PdfiumThread` angefasst, jede Änderung ist ein kurzer Auftrag. Eine **gezeichnete** Unterschrift ist eine Ink-Annotation mit `/Subj` `Signature` (Muster von Pfeil und Linie; Auswahl, Farbe, Stärke und dunkle Seite kommen aus M10). Eine **importierte** ist ein Stempel mit Bildobjekt und einer Marke im `/AP` (Muster des Textfelds). Bild-Dekodierung und -Kodierung liegen in der App (Avalonia/Skia), nie auf dem `PdfiumThread`. Aufgabe 1 (Spike) entscheidet beides, bevor gebaut wird.

**Tech Stack:** .NET 10 Native AOT, Avalonia 12.1, PDFium (`bblanchon.PDFium.*`, chromium/8100) über eigene `[LibraryImport]`-Bindings, xunit.v3 (Microsoft.Testing.Platform), Avalonia.Headless.

**Spec:** [2026-10-29-m12-sichtbare-unterschrift-design.md](../specs/2026-10-29-m12-sichtbare-unterschrift-design.md) (maßgeblich); Rahmen: [Teilprojekt 2](../specs/2026-10-24-teilprojekt2-annotieren-design.md), [M10](../specs/2026-10-25-m10-zeichnen-formen-design.md); Roadmap „Milestone 12“; Design: [Designsystem](../specs/2026-10-17-designsystem.md), 7.3 und 7.7.

## Globale Randbedingungen

Gelten für jede Aufgabe (Wortlaut aus Spec und `CLAUDE.md`):

- PDFium nur über den `PdfiumThread`; Änderungen sind **kurze** Aufträge; der UI-Thread wartet nie auf PDFium; Bilder werden **vor** dem Auftrag in der App dekodiert, nie auf dem `PdfiumThread`. Konstanten und Funktionen gegen die Header von `chromium/8100` prüfen (`NativeBindingsTests.ConstantsMatchThePackageHeaders`), `unsigned long` immer `CULong`. Ein neuer `[UnmanagedCallersOnly]`-Rückruf kommt erst nach Prüfung in `UnmanagedCallbackTests.EveryUnmanagedCallbackIsReviewed` (geplant ist keiner).
- Jede angelegte oder geänderte Annotation hat ein eigenes `/AP` als Form-XObject und das Flag Print; fehlt es nach dem Erzeugen, ist es ein Fehler und die Annotation wird entfernt. `FPDF_PageToDevice` bzw. `DisplayTransform` ist die einzige Koordinatenumrechnung.
- Die Unterschrift bleibt eine **verschiebbare Annotation**, nie in den Seiteninhalt eingebrannt. Sie ist ein Bild, keine digitale Signatur: kein Beschreiben eines `/Sig`-Felds, kein Einrasten in ein Signaturfeld; die vorhandene `SignaturePolicy` gilt unverändert.
- Der Dialog zeigt: „Ersetzt keine handschriftliche Unterschrift, wo Schriftform verlangt ist.“ und „Das ist ein Bild Ihrer Unterschrift, keine digitale Signatur.“ (englisch sinngemäß), in beiden Reitern sichtbar ohne Scrollen, nie dauerhaft wegklickbar. Kein UI-Text nennt etwas „rechtsgültig“, „legally binding“ oder „legally valid“ (in keiner Sprache), keiner nennt eine Signatur „gültig“.
- Grenzen: höchstens 10 gespeicherte Unterschriften; Importdatei höchstens 10 MiB und höchstens 6.000 × 6.000 px (aus dem Kopf der Datei gelesen, **vor** dem Dekodieren); gespeichertes Bild längste Seite höchstens 1.200 px; Anfangsbreite 160 pt, höchstens ein Drittel der Seitenbreite; Punkte einer gezeichneten Unterschrift höchstens `PdfiumDrawings.MaxInkPoints`; Grenzen von M9 und M10 (10.000 Annotationen je Dokument) gelten weiter.
- Zustände nie nur über Farbe; Kontrast WCAG 2.2 AA (Text 4,5:1, Ränder/Fokus/Griffe 3:1; neue Farbpaare vorher prüfen); Zielgröße der Griffe mindestens 24 DIP; Automation-Namen, `AccessibilityView="Content"` an `Grid`/`Border` mit Namen; keine Farb- oder Maßliterale im XAML. Nichts ausgegraut für spätere Versionen.
- Startpfad: nichts Neues vor dem ersten scharfen Seitenbild; Karte, Bibliothek (Ordner wird erst beim Öffnen des Menüs gelesen), Dekodierung und Vorschau entstehen erst beim Gebrauch; Messläufe unverändert; Startzeit-Budgets nie lockern.
- Unterschriften verlassen das Programm nie außer in der PDF-Datei, die der Nutzer speichert: kein Netz, kein Protokoll, keine Fehlermeldung mit Bildinhalt. Keine neuen Abhängigkeiten (sonst Lizenzmanifest, `ThirdPartyNotices.txt`, `DECISIONS`). Native AOT: keine Reflection, JSON nur mit Source-Generator.
- Tests schreiben nie in den echten Benutzerordner (`UserSettings.Detached()`, Temp-Ordner) und nie neben die Eingaben in `testdata`; Testbilder erzeugen die Tests selbst im Speicher (Herkunft: selbst erzeugt). Neue Test-PDFs: Herkunft in `testdata/pdfs/LICENSES.md`, Eintrag in `RenderCorpus.Skipped`, Erzeugung in `tools/make-testpdfs.cs` (`check` bleibt grün).
- UI-Texte Englisch **und** Deutsch (`Strings.en.resx`, `Strings.de.resx`, `Strings.cs`). Code und Kommentare Englisch; Doku und Commits Deutsch (Conventional Commits, gezielt stagen, nie `git add .`, danach `git show --stat HEAD` prüfen). Testnamen ohne Unterstriche.
- Befehle: Build `dotnet build Pagewing.slnx`, Test `dotnet test Pagewing.slnx`, Format `dotnet format Pagewing.slnx --verify-no-changes`, Roadmap `dotnet run tools/roadmap.cs -- check`. Einzelne Tests: `dotnet test tests/Pagewing.Core.Tests --filter-class "*Name"` (analog `--filter-method`, App: `tests/Pagewing.App.Tests`).
- Commit-Trailer jeder Aufgabe: `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.
- **[Bild]** kennzeichnet Aufgaben und Schritte des Bildimports. Besteht der Spike für das Bild nicht (Aufgabe 1, Halt), entfallen alle mit **[Bild]** markierten Teile aus M12 und wandern in einen eigenen Milestone (Spec, „Festgelegt“ Punkt 5).

## Festgelegte Werte (aus Spec; Startwerte des Plans sind markiert)

| Größe | Wert |
|---|---|
| Tinte gezeichneter Unterschrift | `DrawColor` wie M10; Standard **Schwarz**, Stärke mittel (2 pt, nicht mitskaliert); Farbe und Stärke nach dem Platzieren änderbar |
| `/Subj` der gezeichneten Unterschrift | `Signature` (fest englisch, wie `Arrow` und `Line`) |
| Marke des Bild-Stempels (Startwert, Spike bestätigt) | `%PagewingSignature 1 <Fingerabdruck der Pixel>` am Anfang des `/AP` |
| Freistellen (Startwert) | Alpha aus Helligkeit, Schwelle 0–255 (Vorschlag der Otsu-Schwelle), weicher Übergang 32 Stufen unterhalb der Schwelle; Farbe der Pixel bleibt; danach auf den Inhalt beschneiden (Rand 2 px) |
| Verlauf mit Bildern | Pixelpuffer deflate-komprimiert; Obergrenze 32 MiB je Verlauf, danach lehnt der Editor den Schritt mit „Bitte zuerst speichern“ ab (nie ein Schritt fällt still weg) |
| Kürzel (Vorschlag) | `annotate.signature` Strg+Umschalt+G (Kollisionstest im Katalog) |

## Review-Fokus

Eingaben und Zustände, die die Spec nicht ausspricht, wahrscheinlichste zuerst; jeder Punkt hat seinen Test in der genannten Aufgabe:

1. **Leere oder unbrauchbare Unterschrift** (ein Punkt, ganz weißes Bild, nach dem Freistellen nichts übrig; ein ganz schwarzes Bild wird ein einziger Block und stürzt nicht ab): „Speichern“ im Dialog bleibt aus und nennt den Grund, nichts landet in der Bibliothek (Aufgabe 3 und 10).
2. **Kaputte oder feindliche Bilddatei** (0 Byte, JPG mit Endung .png, 16-Bit-PNG, progressives oder CMYK-JPG, 60.000 × 60.000 px, 10 MiB und mehr): klare Meldung im Dialog, kein Absturz, Maße vor dem Dekodieren geprüft (Aufgabe 3, 9 und 10).
3. **Platzieren am Rand, auf winziger oder gedrehter Seite, mit CropBox-Versatz:** die Unterschrift liegt ganz auf der Seite, steht aufrecht und genau dort, wo der Zeiger war, auch nach Speichern und Neuladen (Aufgabe 6 und 11).
4. **Beschädigte Bibliothek** (`signatures.json` kaputt oder zu groß, PNG fehlt, elfte Unterschrift, Name leer oder mit Pfadzeichen, zwei Fenster gleichzeitig): nichts wirft, Dateinamen entstehen nie aus Namen, die Datei bleibt lesbar (Aufgabe 4).
5. **Zustandswechsel:** Tab- oder Dokumentwechsel, Speichern oder Esc, während die Vorschau am Zeiger hängt; Dokument mit Passwort, ohne Annotationsrecht oder DocMDP 1 und 2; Rückgängig nach dem Löschen eines Bildes über viele Schritte (Aufgabe 7, 11 und 12).

---

## Dateistruktur

| Datei | Verantwortung |
|---|---|
| `src/Pagewing.Core/Signatures/` (neu): `SignatureImage.cs`, `SignatureCutout.cs`, `ImageHeader.cs`, `SignatureLimits.cs`, `SignaturePlacement.cs`, `SavedSignature.cs`, `SignatureStore.cs` | rein rechnerisch bzw. Dateien im Benutzerordner, ohne PDFium und ohne UI |
| `src/Pagewing.Core/Annotations/AnnotationKind.cs`, `DrawingGeometry.cs`, `PdfAnnotation.cs`, `AnnotationEdit.cs`, `AnnotationHistory.cs`, `AnnotationEditor.cs` (erweitert) | Art `Signature`, Fabrik `DrawingGeometry.Signature`, Merkmal `IsSignatureImage`, Verlauf mit Bild |
| `src/Pagewing.Core/Pdf/PdfDocument.Signatures.cs` (neu), `PdfDocument.Drawings.cs` (erweitert) | Anlegen, Lesen, Verschieben der Unterschrift |
| `src/Pagewing.Core/Settings/AppSettings.cs`, `UserDataPaths.cs` (erweitert) | Werkzeugstil `Signature`, Ordner der Bibliothek |
| `src/Pagewing.Pdfium/Native/Fpdf.cs`, `FpdfConstants.cs`, `PdfiumDrawings.cs` (erweitert), `PdfiumSignatures.cs` (neu), `PdfiumDarkDrawings.cs` (erweitert) | Bindings, Ink mit Marke, Bild-Stempel, dunkle Seite |
| `src/Pagewing.App/Services/SignatureImageCodec.cs` (neu) | Dekodieren und PNG-Kodieren (Avalonia), Vormultiplikation |
| `src/Pagewing.App/ViewModels/TabViewModel.Signature.cs`, `SignatureCardViewModel.cs`, `OverlayKind.cs` (neu/erweitert) | Platzier-Werkzeug, Bibliothek, Dialog |
| `src/Pagewing.App/Views/SignatureCard.axaml(.cs)`, `Controls/SignaturePad.cs`, `Views/MainWindow.Overlays.cs`, `Views/DocumentView.Signature.cs`, `Controls/DrawingOverlay.cs`, `Views/AnnotationToolbar.axaml(.cs)` (neu/erweitert) | Dialog, Zeichenfläche, Vorschau am Zeiger, Platzieren, Leiste |
| `src/Pagewing.App/Commands/CommandCatalog.cs`, `Resources/Strings.*`, `ViewModels/CommentsViewModel.cs` (erweitert) | Befehl, Texte, Panel |
| `tools/make-testpdfs.cs`, `testdata/pdfs/stamps-foreign.pdf` (neu) | fremde Stempel |
| `tests/Pagewing.Core.Tests/Signatures/*`, `tests/Pagewing.App.Tests/Signature*` | Tests je Aufgabe |

---

### Aufgabe 1: Spike – Erscheinungsbild im PDF (Gate, mit Halt)

Wegwerf-Test (`tests/Pagewing.Core.Tests/Spikes/SignatureSpikeTests.cs`, nicht einchecken), Bindings vorläufig im Test. Beantwortet, was die folgenden Aufgaben festschreiben.

**Files:** Create (nicht einchecken): `tests/Pagewing.Core.Tests/Spikes/SignatureSpikeTests.cs`; Modify: `Docs/DECISIONS.md`.

- [ ] **Schritt 1 [Bild]:** Stempel mit Bildobjekt bauen (`FPDFPage_CreateAnnot` Stamp, `FPDFPageObj_NewImageObj`, `FPDFImageObj_SetBitmap` mit BGRA-Bitmap und halbtransparenten Kanten, Matrix auf `/Rect`, `FPDFAnnot_AppendObject`, Flag Print) auf `text-basic.pdf`, `rotated-page.pdf`, `cropbox.pdf`; einmal mit `FPDF_ANNOT` rendern, inkrementell speichern, neu öffnen.
- [ ] **Schritt 2 [Bild]:** Messen und festhalten: Schreibt PDFium Alpha als Maske ins `/AP` (kein weißer Kasten im Rendern von PDFium)? Steht das Bild auf gedrehter Seite aufrecht, in der CropBox am richtigen Ort? Genügt zum **Verschieben und Skalieren** `FPDFAnnot_SetRect` (das `/AP` folgt dem Rechteck), oder muss neu angelegt werden? Lässt sich das Bild für Rückgängig auslesen (`FPDFAnnot_GetObject`, `FPDFImageObj_GetBitmap`)? Dateigröße je Bild; wird dasselbe Bild beim zweiten und fünften Platzieren doppelt geschrieben? Bleibt eine Marke am Anfang des `/AP` erhalten, und wie sind `/AP`-Strom und Marke zu lesen (`FPDFAnnot_GetAP`, Muster `PdfiumTextBoxes.MarkerMatches`)?
- [ ] **Schritt 3:** Gezeichnet: (a) Ink mit `/Subj` `Signature` gegen (b) Stempel mit Pfadobjekten, beide mit Farbe und 2 pt. Erwartung: (a). Prüfen, ob (a) in einem zweiten PDFium-Durchlauf wiedererkannt wird und das Aussehen gleich ist.
- [ ] **Schritt 4:** Dunkle Seite messen (`PageTheme.Dark`): Pixelwerte von (a), (b) und des Bild-Stempels mit dunkler und heller Tinte.
- [ ] **Schritt 5:** Dem Projektinhaber die erzeugten Dateien zur Handprüfung in Chrome und Acrobat Reader geben (gedrehte Seite, CropBox, weißer Kasten?, `/Subj` nach Acrobats eigenem Speichern erhalten?). Ergebnis als Eintrag in `Docs/DECISIONS.md` („Sichtbare Unterschrift: Erscheinungsbild und Entscheidung (M12)“) mit den Ausgängen: gezeichnet = (a) oder (b); Bild trägt / trägt nicht.
- [ ] **Schritt 6 (HALT):** Spike löschen, Eintrag committen (`docs: Unterschrift – Spike-Ergebnisse (M12)`), dem Projektinhaber das Ergebnis zeigen und **auf seine Antwort warten**, bevor Aufgabe 2 beginnt. Trägt das Bild nicht: alle **[Bild]**-Teile entfallen, `Docs/roadmap.json` bekommt unter „Scope-Änderungen“ den neuen Milestone (Nummer legt der Projektinhaber fest). Gewinnt (b), wird Aufgabe 5 Teil 1 angepasst.

### Aufgabe 2: Testdaten

**Files:** Modify: `tools/make-testpdfs.cs`, `testdata/pdfs/LICENSES.md`, `tests/Pagewing.Render.Tests/RenderCorpus.cs`; Create: `testdata/pdfs/stamps-foreign.pdf`.

**Interfaces:** Produces: `stamps-foreign.pdf` mit Seite 1: Stempel mit Text-`/AP`, Stempel mit Bild-`/AP` ohne Marke (fremd), Stempel ohne `/AP`, Ink mit `/Subj` `Signature` aber drei Strichen (keine eigene Form) – alle müssen gelistet und **nicht** als eigene Unterschrift erkannt werden.

- [ ] **Schritt 1 (Test zuerst):** `AnnotationTestFiles.StampsForeign` und `StampsForeignFixtureListsAllEntries` (`AnnotationReadTests`): `GetAnnotationsAsync(0)` liefert vier Einträge, keiner mit `IsSignatureImage`; rot.
- [ ] **Schritt 2:** Datei in `tools/make-testpdfs.cs` erzeugen (deterministisch, unkomprimiert), `dotnet run tools/make-testpdfs.cs -- generate`, Eintrag in `LICENSES.md` (selbst erzeugt) und `RenderCorpus.Skipped`; `-- check` grün. Commit `test: Testdatei stamps-foreign.pdf (M12)`.

### Aufgabe 3: Bild und Freistellen (Core, rein) **[Bild]**

**Files:** Create: `src/Pagewing.Core/Signatures/SignatureImage.cs`, `SignatureCutout.cs`, `ImageHeader.cs`, `SignatureLimits.cs`; Test: `tests/Pagewing.Core.Tests/Signatures/SignatureCutoutTests.cs`, `ImageHeaderTests.cs`.

**Interfaces:** Produces: `sealed record SignatureImage(int Width, int Height, byte[] Bgra)` (unvormultipliziertes BGRA, `Bgra.Length == Width*Height*4`, geprüft im Konstruktor) mit `bool IsEmpty` (kein Pixel mit Alpha > 8) und `PackedSignatureImage Pack()` / `static SignatureImage Unpack(PackedSignatureImage)` (Deflate, BCL); `sealed record PackedSignatureImage(int Width, int Height, byte[] Deflated)`; `static class SignatureCutout { int AutoThreshold(SignatureImage); SignatureImage Apply(SignatureImage, int threshold); SignatureImage TrimToContent(SignatureImage); SignatureImage FitWithin(SignatureImage, int maxSide); }`; `static class ImageHeader { bool TryReadSize(ReadOnlySpan<byte> file, out int width, out int height); }` (PNG-IHDR, JPEG-SOF0 bis SOF15 ohne DHT/DAC/RST; sonst `false`); `static class SignatureLimits` mit den Konstanten `MaxSignatures = 10`, `MaxFileBytes = 10 * 1024 * 1024`, `MaxSourceSide = 6000`, `MaxStoredSide = 1200`, `StartWidthPoints = 160`, `MaxStartWidthFraction = 1.0 / 3`, `MaxHistoryImageBytes = 32 * 1024 * 1024`.

- [ ] **Schritt 1 (Tests zuerst, rot):** `SignatureCutoutTests.WhitePaperBecomesTransparent`, `…DarkInkKeepsItsColour` (blaue Tinte bleibt blau), `…SoftEdgeGivesPartialAlpha`, `…AutoThresholdSeparatesInkFromPaper`, `…AllWhiteImageIsEmpty`, `…AllBlackImageBecomesOneOpaqueBlockWithoutCrash`, `…TrimKeepsTwoPixelMargin`, `…FitWithinNeverEnlarges`, `…FitWithinKeepsAspectRatio`; `ImageHeaderTests.ReadsPngSize`, `…ReadsJpegSizeOfBaselineAndProgressive`, `…RejectsTruncatedAndUnknownFiles`, `…RejectsZeroSize`, `…ReadsHugeSizeWithoutDecoding` (60.000 × 60.000 aus 30 Byte Kopf).
- [ ] **Schritt 2:** Typen wie unter Interfaces; Helligkeit nach Rec. 601; `Apply` setzt Alpha `clamp((threshold - lum) * 255 / 32)` je Pixel bis Schwelle (Rampe 32 Stufen), RGB bleibt. Tests grün (`--filter-namespace "*Signatures"`). Commit `feat(core): Bild und Freistellen der Unterschrift (M12)`.

### Aufgabe 4: Bibliothek der Unterschriften (Core)

**Files:** Create: `src/Pagewing.Core/Signatures/SavedSignature.cs`, `SignatureStore.cs`; Modify: `src/Pagewing.Core/Settings/UserDataPaths.cs`, `SettingsJsonContext.cs` (oder eigener Kontext), `src/Pagewing.Core/Settings/AppSettings.cs`; Test: `tests/Pagewing.Core.Tests/Signatures/SignatureStoreTests.cs`, `…/Settings/SettingsStoreTests.cs`.

**Interfaces:**
- Consumes: Aufgabe 3 (`SignatureImage`, `SignatureLimits`), `JsonFile` und `DrawColor`, `DisplayPoint`.
- Produces: `enum SavedSignatureKind { Drawn, Image }`; `sealed record SavedSignature(string Id, string Name, SavedSignatureKind Kind, double Aspect, DrawColor Color, IReadOnlyList<IReadOnlyList<DisplayPoint>>? Strokes)` (`Id` = GUID ohne Bindestriche, nie aus dem Namen; Striche auf das Einheitsquadrat normiert, `Aspect` = Breite/Höhe); `UserDataPaths.SignaturesDirectory() -> string` (Unterordner `signatures` der Config); `sealed class SignatureStore` mit `static SignatureStore Load(string directory)`, `IReadOnlyList<SavedSignature> Items`, `SavedSignature AddDrawn(string name, IReadOnlyList<IReadOnlyList<DisplayPoint>> strokes, DrawColor color)`, `[Bild] SavedSignature AddImage(string name, byte[] png, double aspect)`, `void Rename(string id, string name)`, `void Remove(string id)`, `[Bild] byte[]? ReadPng(string id)`; wirft `SignatureLimitException` ab der elften; Schreiben atomar über `.tmp` im Zielordner (`JsonFile`), unter Unix Rechte `0600`; `AppSettings.ToolStyleKinds` bekommt `AnnotationKind.Signature`, `AppSettings.StyleOf(Signature)` ohne Eintrag liefert **Schwarz/mittel** (`DefaultSignatureStyle`).

- [ ] **Schritt 1 (Tests zuerst, rot, Temp-Ordner):** `SignatureStoreTests.AddedSignaturesSurviveReload`, `…ElevenSignaturesAreRefused`, `…NameWithPathCharactersDoesNotChangeTheFileName`, `…EmptyOrOverlongNameIsRepaired`, `…CorruptIndexIsMovedToBadAndStartsEmpty`, `…MissingPngDropsOnlyThatEntry`, `…OversizedIndexIsIgnored`, `…TwoStoresOnTheSameFolderDoNotCorruptEachOther`, `…RemoveDeletesTheFile`, `…UnixFilesAreOwnerOnly` (nur Linux, sonst übersprungen); `SettingsStoreTests.SignatureStyleDefaultsToBlack`.
- [ ] **Schritt 2:** Umsetzen; Indexgrenze 1 MiB wie die übrigen JSON-Dateien; Tests grün (`--filter-namespace "*Signatures"`, `--filter-class "*SettingsStore*"`). Commit `feat(core): Bibliothek der Unterschriften (M12)`.

### Aufgabe 5: Pdfium – gezeichnete Unterschrift und Bild-Stempel

**Files:** Modify: `src/Pagewing.Pdfium/PdfiumDrawings.cs`, `Native/Fpdf.cs`, `Native/FpdfConstants.cs`, `PdfiumAnnotations.cs`; Create: `src/Pagewing.Pdfium/PdfiumSignatures.cs`; Test: `tests/Pagewing.Core.Tests/Pdfium/NativeBindingsTests.cs`, `tests/Pagewing.Core.Tests/Signatures/SignatureReadTests.cs`.

**Interfaces:**
- Consumes: Aufgabe 1 (Entscheid), Aufgabe 2 (`stamps-foreign.pdf`), `PdfiumDrawings` (`ArrowSubject`-Muster).
- Produces (intern): `PdfiumDrawingKind.Signature` und `PdfiumDrawings.SignatureSubject = "Signature"`; `Create` schreibt `/Subj` für diese Art, `Read` erkennt sie nur mit `/Subj` und mindestens einem Strich von zwei Punkten (sonst Ink); **[Bild]** `PdfiumSignatures.CreateImage(PdfiumPage page, (double Left, double Top, double Right, double Bottom) box, int width, int height, ReadOnlySpan<byte> bgra, string? author, string name, string date) -> int` (Stempel mit Bildobjekt, Marke, Flag Print, `/NM`, `/M`, `/CreationDate`; bei Fehler bleibt nichts auf der Seite), `PdfiumSignatures.IsOwnImage(PdfiumPage, int index) -> bool` (nur mit Marke, Fingerabdruck der Pixel stimmt, nur Flag Print, nur ein Bildobjekt), `PdfiumSignatures.ReadImage(PdfiumPage, int index) -> (int Width, int Height, byte[] Bgra)?` (Grenzen gegen feindliche Dateien), `PdfiumSignatures.SetBox(PdfiumPage, int index, box)` (oder Neuanlegen, je Spike).

- [ ] **Schritt 1 (Tests zuerst, rot):** `NativeBindingsTests` um neue Konstanten; `SignatureReadTests.InkWithSignatureSubjectIsRecognisedAfterReload`, `…ThreeStrokeInkWithTheSubjectIsPlainInk`, `…ForeignStampsAreListedButNotOwn` (`stamps-foreign.pdf`), `…OwnImageStampIsRecognisedAfterReload` **[Bild]**, `…AlteredImageLosesTheMark` **[Bild]** (Bildpixel im `/AP` ändern → gesperrt), `…ReadImageReturnsTheSamePixels` **[Bild]**, `…HostileImageSizeIsRefused` **[Bild]** (Maße im Stempel über der Grenze: `null`, kein Speicherstoß).
- [ ] **Schritt 2:** Umsetzen (Funktionen und Konstanten gegen `fpdf_annot.h`, `fpdf_edit.h` prüfen); Lesen folgt `PdfiumAnnotations.ReadOne` (Grenzen, `IsSane`) und der Budget-Regel des Textfelds (`/AP` nur innerhalb eines Budgets lesen, sonst fremd). Tests grün. Commit `feat(pdfium): gezeichnete Unterschrift und Bild-Stempel (M12)`.

### Aufgabe 6: `PdfDocument` und Modell

**Files:** Create: `src/Pagewing.Core/Pdf/PdfDocument.Signatures.cs`; Modify: `AnnotationKind.cs` (`Signature` vor `Other`), `DrawingGeometry.cs`, `PdfAnnotation.cs`, `AnnotationEdit.cs`, `PdfDocument.Drawings.cs`, `PdfDocument.Annotations.cs`; Test: `tests/Pagewing.Core.Tests/Signatures/SignatureRoundTripTests.cs`.

**Interfaces:**
- Consumes: Aufgabe 3, 5.
- Produces: `AnnotationKind.Signature`; `DrawingGeometry.Signature(IReadOnlyList<IReadOnlyList<DisplayPoint>> strokes, double width)` (Kind `Signature`, `Box` aus den Punkten); `PdfAnnotation` bekommt `bool IsSignatureImage = false` am Ende (**[Bild]**; `Kind` = `Signature`, `Drawing` = `null`, `SelectionShape` = Rechteck der `Bounds`, `GeometryEditable` nur für eigene); `Editable` und `GeometryEditable` für eine gezeichnete Unterschrift wie für Ink; **[Bild]** `PdfDocument.CreateSignatureImageAsync(int pageIndex, PageRect box, SignatureImage image, string? author, CancellationToken ct = default) -> Task<PdfAnnotation>` (internal Überladung mit `string? name` für Rückgängig), `PdfDocument.ReadSignatureImageAsync(AnnotationLocator locator, CancellationToken ct = default) -> Task<SignatureImage>`; Verschieben/Skalieren eines Bild-Stempels läuft über `UpdateAnnotationAsync` mit `AnnotationEdit.Geometry` (Kind `Rectangle` = neue Box, Seitenverhältnis wird erzwungen); `Color` ist für Bilder nicht änderbar (`InvalidOperationException`). Die Box wird auf die Seite begrenzt.

- [ ] **Schritt 1 (Tests zuerst, rot, Temp-Kopien):** `SignatureRoundTripTests.DrawnSignatureSurvivesSaveAndReload` (Kind `Signature`, Striche, Farbe, Stärke, Kommentar), `…DrawnSignatureCanBeRecolouredAndMovedAsOneNamedAnnotation`, `…ImageSignatureSurvivesSaveAndReload` **[Bild]** (Pixel gleich, Box gleich), `…ImageMovesAndScalesKeepingAspect` **[Bild]**, `…ImageAppearanceHasPrintFlagAndAppearanceObject` **[Bild]**, `…RotatedPageKeepsTheSignatureUpright`, `…CropBoxOffsetKeepsItInPlace`, `…BoxIsClampedToThePage`, `…ForeignStampCannotBeChanged`, `…ExistingBytesStayThePrefix`, `…LimitIsReported` (`PdfAnnotationLimitException`).
- [ ] **Schritt 2:** Umsetzen (Anlegen nach dem Muster von `CreateDrawingAsync`/`FinishCreated`, `EnsureRoomForOneMore`); Tests grün (`--filter-class "*SignatureRoundTrip*"`). Commit `feat(core): Unterschrift anlegen, lesen und speichern (M12)`.

### Aufgabe 7: Editor und Verlauf

**Files:** Modify: `src/Pagewing.Core/Annotations/AnnotationEditor.cs`, `AnnotationHistory.cs`; Test: `tests/Pagewing.Core.Tests/Annotations/AnnotationEditorTests.cs`, `AnnotationHistoryTests.cs`.

**Interfaces:**
- Consumes: Aufgabe 6.
- Produces: `AnnotationEditor.ApplyDrawingAsync` akzeptiert `DrawingGeometry.Signature` unverändert; **[Bild]** `AnnotationEditor.ApplySignatureImageAsync(int page, PageRect box, SignatureImage image, string? author) -> Task<PdfAnnotation?>` (`null`, wenn `image.IsEmpty`); `AnnotationCommand.Delete(PdfAnnotation Removed, PackedSignatureImage? Image = null)` (der Editor liest das Bild vor dem Löschen und hält es gepackt); Rückgängig legt es unter demselben `/NM` neu an; überschreitet die Summe der gepackten Bilder im Verlauf `SignatureLimits.MaxHistoryImageBytes`, wirft der Editor `AnnotationHistoryFullException` (Meldung „Bitte zuerst speichern“) und ändert nichts; nach erfolgreichem Speichern ist der Verlauf (samt Bildern) leer.

- [ ] **Schritt 1 (Tests zuerst, rot):** `AnnotationEditorTests.EmptyImageSignatureChangesNothing` **[Bild]**, `…UndoOfPlacingTheDrawnSignatureDeletesIt`, `…UndoOfDeletingAnImageSignatureRecreatesItUnderTheSameName` **[Bild]**, `…MovingAnImageSignatureIsOneUndoStep` **[Bild]**, `…HistoryRefusesPastTheImageBudgetAndChangesNothing` **[Bild]**, `…SavingClearsTheImagesFromTheHistory` **[Bild]**; `AnnotationHistoryTests.PackedImageCountsTowardTheBudget` **[Bild]**.
- [ ] **Schritt 2:** Umsetzen; Tests grün (`--filter-class "*AnnotationEditor*"`). Commit `feat(core): Editor-Operationen für Unterschriften (M12)`.

### Aufgabe 8: Dunkle Seite und Druck

**Files:** Modify: `src/Pagewing.Pdfium/PdfiumDarkDrawings.cs`, `PdfiumRenderWork.cs`; Test: `tests/Pagewing.Core.Tests/Pdfium/DarkPageSignatureTests.cs`, `tests/Pagewing.App.Tests/PrintSupportTests.cs`.

**Interfaces:** Consumes: Messwerte aus Aufgabe 1, Schritt 4. Produces: die gezeichnete Unterschrift behält auf `PageTheme.Dark` ihre Farbe (auf 3:1 gegen `page.darkBackground` aufgehellt wie die Linien); **[Bild]** der Bild-Stempel ist auf der dunklen Seite sichtbar mit mindestens 3:1 zwischen Tinte und Seite (Weg dem Builder überlassen, Vorbild `PdfiumDarkDrawings`); helle Seite und Druck lesen und malen nichts zusätzlich; gedruckt wird hell mit der Unterschrift, auch ungespeichert.

- [ ] **Schritt 1 (Tests zuerst, rot):** `DarkPageSignatureTests.BlackDrawnSignatureIsLightenedToThreeToOne`, `…BlueDrawnSignatureKeepsItsHue`, `…DarkInkImageIsVisibleAtThreeToOne` **[Bild]**, `…LightPageReadsNothing` (Zähler wie `DarkMarkupScans`), `…SearchHitOnTopStillVisible`; Druck: `PrintSupportTests.UnsavedSignatureAppearsInThePrint`.
- [ ] **Schritt 2:** Umsetzen; Tests grün (`--filter-class "*DarkPageSignature*"`). Commit `feat(pdfium): Unterschrift auf der dunklen Seite und im Druck (M12)`.

### Aufgabe 9: Bild dekodieren und kodieren (App) **[Bild]**

**Files:** Create: `src/Pagewing.App/Services/SignatureImageCodec.cs`; Test: `tests/Pagewing.App.Tests/SignatureImageCodecTests.cs`.

**Interfaces:** Consumes: Aufgabe 3. Produces: `static class SignatureImageCodec { Task<SignatureImage> DecodeAsync(byte[] file, CancellationToken ct); byte[] EncodePng(SignatureImage image); }`; `DecodeAsync` prüft `file.Length <= SignatureLimits.MaxFileBytes` und `ImageHeader.TryReadSize` (Seite höchstens `MaxSourceSide`) **vor** dem Dekodieren, wirft sonst `SignatureImageException` mit `Reason` (`TooLarge`, `TooManyPixels`, `Unreadable`, `Empty`); läuft auf dem Pool-Thread (nie UI, nie `PdfiumThread`); wandelt Avalonias vormultipliziertes BGRA in unvormultipliziertes um; `EncodePng` ist verlustfrei.

- [ ] **Schritt 1 (Tests zuerst, rot):** `SignatureImageCodecTests.PngRoundTripKeepsPixelsAndAlpha`, `…PremultipliedAlphaIsUndone`, `…TooLargeFileIsRefusedWithoutDecoding`, `…HugeDimensionsAreRefusedBeforeDecoding`, `…JpegNamedPngIsStillRead`, `…TruncatedFileIsUnreadable`, `…ZeroByteFileIsUnreadable`, `…DoesNotRunOnTheUiThread`.
- [ ] **Schritt 2:** Umsetzen; Tests grün. Commit `feat(app): Bild der Unterschrift lesen und schreiben (M12)`.

### Aufgabe 10: Dialog „Neue Unterschrift“

**Files:** Create: `ViewModels/SignatureCardViewModel.cs`, `Views/SignatureCard.axaml(.cs)`, `Controls/SignaturePad.cs`; Modify: `ViewModels/OverlayKind.cs` (`Signature`), `Views/MainWindow.Overlays.cs`, `Resources/Strings.en.resx`, `Strings.de.resx`, `Strings.cs`; Test: `tests/Pagewing.App.Tests/SignatureCardTests.cs`.

**Interfaces:**
- Consumes: Aufgabe 3, 4, 9, `StrokeSimplifier`, `DrawPalette`.
- Produces: Overlay `OverlayKind.Signature` (entsteht samt View-Model erst beim ersten Zeigen, Fokus-Rückkehr wie die anderen Overlays); `SignatureCardViewModel` mit `Name`, `Tab` (`Draw`/`Image`), `Color`, `Strokes`, `Threshold`, `PreviewImage`, `Problem` (`SignatureProblem?`: `Empty`, `TooLarge`, `TooManyPixels`, `Unreadable`, `LibraryFull`), `SaveCommand` (nur ausführbar ohne Problem und mit Inhalt), `CancelCommand`, `ClearCommand`, `UndoStrokeCommand`, `ChooseFileCommand` **[Bild]**; `Controls/SignaturePad` (Zeiger-Eingabe wie `DocumentView.Drawing`: Maus und Stift zeichnen, Glättung mit `StrokeSimplifier`, Hilfslinie); der Hinweis (zwei Zeilen) steht in beiden Reitern; Esc schließt die Karte (`overlay.hide`).

- [ ] **Schritt 1 (Tests zuerst, rot, Headless):** `SignatureCardTests.HintIsShownInBothTabsWithoutScrolling` (deutscher Satz „Ersetzt keine handschriftliche Unterschrift, wo Schriftform verlangt ist.“ und englischer „Does not replace a handwritten signature where written form is required.“, dazu die zweite Zeile), `…NoUiTextCallsAnythingLegallyBinding` (alle Texte der Karte, Menüs und Fehler in beiden Sprachen enthalten weder „rechtsgültig“ noch „legally“ noch „gültig“/„valid“ als Wort; Muster `FormModeTests`), `…SingleDotCannotBeSaved`, `…BlankImageCannotBeSavedAndSaysWhy` **[Bild]**, `…EleventhSignatureShowsTheLimit`, `…DrawnStrokesAreSmoothed`, `…ThresholdSliderChangesThePreview` **[Bild]**, `…CardIsCreatedOnlyOnFirstShow`, `…KeyboardReachesEveryControl`, `…ColoursMeetThreeToOne`, `…SavingWritesNothingToTheRealUserFolder`.
- [ ] **Schritt 2:** Umsetzen; Texte beider Sprachen; `LocalizationTests` grün. Optional, nur wenn es ohne Aufwand geht: Datei per Ablegen auf die Karte. Commit `feat(app): Dialog für neue Unterschriften (M12)`.

### Aufgabe 11: Befehl, Platzieren und Vorschau

**Files:** Create: `ViewModels/TabViewModel.Signature.cs`, `Views/DocumentView.Signature.cs`; Modify: `Commands/CommandCatalog.cs`, `ViewModels/TabViewModel.Annotate.cs` (`IsToolKind` kennt `Signature`), `Controls/DrawingOverlay.cs`, `Views/DocumentView.axaml.cs` (`TryEscape`), `Resources/Strings.*`; Test: `tests/Pagewing.App.Tests/SignaturePlacementTests.cs`, `CommandCatalogTests.cs`; Core: `src/Pagewing.Core/Signatures/SignaturePlacement.cs`, Test `tests/Pagewing.Core.Tests/Signatures/SignaturePlacementTests.cs`.

**Interfaces:**
- Consumes: Aufgabe 4, 7, 10.
- Produces: `static class SignaturePlacement { PageRect Fit(double aspect, DisplayPoint center, double pageWidth, double pageHeight); }` (Breite `min(160, pageWidth/3)`, Seitenverhältnis fest, ganz auf der Seite, auch auf winziger); `TabViewModel.ChooseSignature(string id, bool fromKeyboard)` (Zeiger: Platzier-Werkzeug mit Vorschau; Tastatur: setzt sofort in die Mitte des sichtbaren Teils der aktuellen Seite und wählt aus), `TabViewModel.PlaceSignatureAsync(int page, DisplayPoint center) -> Task` (gezeichnet: `Editor.ApplyDrawingAsync` mit `DrawingGeometry.Signature`, Stil aus `StyleOf(Signature)`; **[Bild]** Bild: `Editor.ApplySignatureImageAsync`; danach ist die Unterschrift ausgewählt, das Werkzeug beendet), ein `SignatureStore` je Prozess (vom `Workspace` gehalten, damit zwei Fenster dieselbe Bibliothek sehen; geladen erst beim ersten Öffnen des Menüs), Katalogeintrag `annotate.signature` („Unterschrift einfügen“, Strg+Umschalt+G) startet den Modus „Kommentieren“ wie die Markierungskürzel (auch aus „Formular ausfüllen“ heraus; `FlushFormDraft` wie bisher) und öffnet das Menü bzw. bei leerer Bibliothek die Karte; `DrawingOverlay.SignatureGhost` zeichnet die Vorschau am Zeiger (nur ihr Rechteck wird ungültig); Esc, Tab-/Dokumentwechsel, Speichern und `PointerCaptureLost` verwerfen die Vorschau; nicht ausführbar beim Speichern, Drucken, in der Update-Sperre, ohne Annotationsrecht (`MayAnnotateAsync`).

- [ ] **Schritt 1 (Tests zuerst, rot):** Core: `SignaturePlacementTests.WidthIsOneHundredSixtyOrAThirdOfThePage`, `…KeepsAspectRatio`, `…StaysOnTheTinyPage`, `…StaysOnThePageAtTheEdge`. App: `SignaturePlacementTests.ClickPlacesAtThePointerOnARotatedPage`, `…KeyboardChoicePlacesInTheCentreOfTheVisiblePage`, `…EscDiscardsTheGhost`, `…TabSwitchDiscardsTheGhost`, `…SavingDiscardsTheGhost`, `…PlacedSignatureIsSelectedAndMovableWithArrowKeys`, `…ColourAndWidthOfADrawnOneCanBeChanged`, `…ImageSignatureOffersNeitherColourNorWidth` **[Bild]**, `…NothingRunsBeforeTheFirstSharpPage`, `…CommandIsNotRunnableWithoutAnnotationRights`, `…CommandFromTheFormModeSwitchesTheMode`, `…LibraryIsReadOnlyWhenTheMenuOpens`; `CommandCatalogTests` um den Eintrag (eindeutig, in der Hilfekarte).
- [ ] **Schritt 2:** Umsetzen; Vorschau-Bild aus dem Pixelpuffer bzw. den Strichen; Seitenzuordnung wie `DocumentView.Drawing`. Tests grün (`--filter-class "*SignaturePlacement*"`). Commit `feat(app): Unterschrift platzieren (M12)`.

### Aufgabe 12: Werkzeugleiste und Panel „Kommentare“

**Files:** Modify: `Views/AnnotationToolbar.axaml(.cs)`, `ViewModels/CommentsViewModel.cs`, `Views/CommentsPanel.axaml`, `ViewModels/TabViewModel.SelectionStyle.cs`, `Resources/Strings.*`; Test: `tests/Pagewing.App.Tests/AnnotationToolbarTests.cs`, `CommentsViewModelTests.cs`.

**Interfaces:** Consumes: Aufgabe 11. Produces: Leisteneintrag „Unterschrift“ mit Menü (gespeicherte Unterschriften mit Vorschau und Löschen, „Neue Unterschrift …“); er wandert bei Platzmangel nach Rang in das Menü „⋯“ (nie vor den Formen); Panel: Art „Unterschrift“ (eigene Zeile, nicht „Freihand“ oder „Stempel“), Kommentar möglich, Filter „Zeichnungen“ enthält sie; `TabViewModel.CanStyleSelection` ist für Bild-Unterschriften falsch.

- [ ] **Schritt 1 (Tests zuerst, rot):** `AnnotationToolbarTests.SignatureEntryIsReachableInANarrowDocumentArea`, `…MenuListsTheLibraryAndTheNewEntry`, `…KeyboardOnlyUseWorks`; `CommentsViewModelTests.ListsSignaturesWithTheirOwnKind`, `…ForeignStampStaysOther`, `…SignatureCommentCanBeEdited`.
- [ ] **Schritt 2:** Umsetzen; Texte beider Sprachen. Commit `feat(app): Leiste und Panel für Unterschriften (M12)`.

### Aufgabe 13: Interop-Muster und Messungen

**Files:** Modify: `tests/Pagewing.Core.Tests/Annotations/InteropSampleTests.cs`, `AnnotationMeasurementTests.cs`, `packaging/README.md` („Annotationen im Fremdviewer prüfen“); `Docs/DECISIONS.md`.

- [ ] **Schritt 1:** `InteropSampleTests` schreibt Muster (`signature-drawn`, `signature-image` **[Bild]**, `-rotated`, `-cropbox`, `-moved`, `-recoloured`, `-on-signed-document`) nur mit `PAGEWING_INTEROP_OUT=<ordner>`; Prüfliste in `packaging/README.md` ergänzen (weißer Kasten? aufrecht? genau einmal nach Verschieben? `/Subj` nach Acrobats Speichern?). Muster liefern; die Handprüfung ist ein Kriterium der Abnahme von `v0.2.0`, wenn der Projektinhaber es so entscheidet.
- [ ] **Schritt 2:** `AnnotationMeasurementTests` (nur mit `PAGEWING_MEASURE_OUT`) messen: Klick bis sichtbare Unterschrift, Dauer des Auftrags, Dateigröße je Unterschrift (gezeichnet; Bild ein- und fünfmal platziert **[Bild]**), `--measure-scroll` auf einer Seite mit zehn Unterschriften. Werte als Eintrag „Unterschrift: Messwerte (M12)“ in `DECISIONS`. Startzeit unverändert: `dotnet run bench/startup/startup.cs -- --case cold`. Commit `test: Interop-Muster und Messwerte für Unterschriften (M12)`.

### Aufgabe 14: Dokumentation, Roadmap und Abschluss

**Files:** Modify: `Docs/DECISIONS.md`, `CHANGELOG.md`, `CLAUDE.md`, `Docs/superpowers/specs/2026-10-17-designsystem.md` (7.3, 7.7), `Docs/superpowers/specs/2026-10-16-pagewing-design.md`, `Docs/roadmap.json`, `Docs/ROADMAP.md` (generiert).

- [ ] **Schritt 1:** `DECISIONS`-Einträge (Erscheinungsbild und Entscheidung des Spikes, Marke und Eigenerkennung, Freistellen, Bibliothek und Datenschutz, Verlauf mit Bildern und Obergrenze, Hinweis im Dialog, Grenzen, Messwerte); `CHANGELOG.md` unter `[Unreleased]`; `CLAUDE.md` (Abschnitt „Zeichnen“ bzw. neuer Block „Unterschrift (seit M12)“ mit Typen und Stolperfallen); Design-Spec 7.7 (Eintrag „Unterschrift“, Dialog, Vorschau am Zeiger) und 7.3 (Panel).
- [ ] **Schritt 2:** Roadmap pflegen: Aufgaben abhaken mit Nachweis; Abnahmekriterium („Test prüft, dass der Hinweis im Dialog steht und ‚rechtsgültig‘ in keinem UI-Text vorkommt“) nur mit Test-Namen (`SignatureCardTests.HintIsShownInBothTabsWithoutScrolling`, `…NoUiTextCallsAnythingLegallyBinding`) und Ergebnis; Handprüfung im Fremdviewer als offen markieren oder auf Wunsch des Projektinhabers zu `v0.2.0` verschieben; bei entfallenem Bildimport der neue Milestone unter „Scope-Änderungen“. `dotnet run tools/roadmap.cs -- generate`, `-- check`.
- [ ] **Schritt 3:** Gesamtprüfung und Nachweis: `dotnet build Pagewing.slnx`, `dotnet test Pagewing.slnx` (Debug und `-c Release`), `dotnet format Pagewing.slnx --verify-no-changes`, `dotnet run tools/roadmap.cs -- check`, `dotnet run tools/make-testpdfs.cs -- check`, `dotnet run tools/licenses.cs -- check`, Native-AOT-Publish von App und CLI (`win-x64`) ohne Warnungen, `dotnet run tools/smoke-cli.cs -- <pagewing.exe>`. Nicht prüfbare Punkte (Linux-Tests, Handprüfung in Chrome und Acrobat, Hardware-Messung) ausdrücklich als offen mit Befehl eintragen.
- [ ] **Schritt 4:** PR nach `dev` (Squash), Claude merged nach grüner CI (CLAUDE.md „Git & Branches“).
