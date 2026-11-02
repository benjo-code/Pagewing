# Teilprojekt 4 – Text bearbeiten (Milestone 16): Design

Stand 2026-11-02. Nachtrag zur [Spec von Teilprojekt 4](2026-11-02-teilprojekt4-ocr-design.md) (dort Abschnitt 8) und zum Rahmen in der [Gesamt-Spec](2026-10-16-pagewing-design.md), Abschnitt 6. Diese Spec legt **M16 (Text bearbeiten)** fest. Grundlage sind die Schriftbefunde aus dem Spike der OCR-Textebene (`DECISIONS.md`, „OCR: Textebene, Spike (M15, Aufgabe 1)“), die Textobjekt-Bausteine des Schwärzens (`PdfiumRedactionNative`) und der Textfelder (M10, `PdfiumGlyphs`).

Markierungen: **[Entscheidung]** vom Projektinhaber (2026-11-02, im Gespräch, „passt“), **[Mein Vorschlag]** vom Entwurf, **[Baumeister]** überlässt die Spec dem, der plant und baut, **[Spike]** ist unbekannt und wird in der ersten Aufgabe des Plans geklärt, bevor etwas darauf gebaut wird.

## 1. Worum es geht

Der Nutzer sieht in einem PDF einen Tippfehler, eine veraltete Zahl oder ein falsches Datum und will es dort ändern, wo es steht, ohne die Quelldatei zu suchen. Das ist die häufigste Bearbeitung eines fertigen PDFs und die, an der Werkzeuge am meisten scheitern.

Was Wettbewerber tun und wo es weh tut (Stand der Recherche 2026-11-02, Herstellerdoku nur teilweise zugänglich, vieles aus Foren):
- **Acrobat** bearbeitet mit lokal installierten Schriften; bei einer nur als Teilmenge eingebetteten Schrift erscheint häufig „no available system font“, und der Rat der Foren ist, die Quelldatei zu ändern.
- **PDF-XChange und Qoppa** warnen „Some characters are not supported by this font and may be lost“; der Nutzer muss selbst eine andere Schrift wählen, und Zeichen können verloren gehen.
- **Foxit** bearbeitet nur einen Textblock je Seite; Absätze laufen in einem eigenen Reflow-Modus.
- **Offene Werkzeuge:** Stirling-PDF kann vorhandenen Text nicht ändern, Xournal++ nur annotieren. Eine echte Zeilenbearbeitung ist dort eine Lücke.

Pagewings Antwort: eine **ehrliche, begrenzte** Zeilenbearbeitung. Sie sagt vor dem Übernehmen, was fehlt und was geschieht, und verliert nie still ein Zeichen.

**Ziele**
- Eine vorhandene Textzeile (genauer: ein Stück Text in einer Schrift, Größe und Farbe) auswählen, den Text ändern, übernehmen; Rückgängig und Wiederholen im gemeinsamen Verlauf; speichern wie Annotationen.
- Der alte Text steht danach **nicht mehr im Seiteninhalt der neuen Fassung** (er wird ersetzt, nie überdeckt). Textextraktion, Suche und Kopieren liefern den neuen Text.
- Bleibt die vorhandene Schrift tauglich (alle neuen Zeichen sind in ihr), ändert sich am Aussehen nichts außer dem geänderten Text.
- Fehlen Zeichen in der Schrift, springt eine Ersatzschrift ein, und die Zeile zeigt **vorher**, welche Zeichen das sind und was geschieht.
- Die Seite außerhalb des geänderten Laufs bleibt unverändert (Render-Vergleich, Abnahmekriterium der Roadmap). **[Mein Vorschlag]** Der Vergleich nimmt den Bereich aus, den der Lauf vorher und nachher einnimmt (Vereinigung beider Rahmen).
- Die Grenzen (Zeilen, keine Absätze) stehen dauerhaft in der Oberfläche (Aufgabe der Roadmap).

**Nicht-Ziele** (Backlog, nicht nebenbei beginnen)
- Absatz-Bearbeitung, Umbruch, Reflow, Ausrichtung, Spalten; Text über mehrere Zeilen oder Seiten verbinden. Weder in der Oberfläche noch in der Doku versprechen.
- Schrift, Größe, Farbe, fett/kursiv ändern; Text verschieben, drehen oder skalieren; neuen Text einfügen (das ist das Textfeld aus M10).
- Suchen und Ersetzen über das Dokument; CLI-Befehl zum Ersetzen (`pagewing text replace`).
- Eine vom Nutzer gewählte Ersatzschrift oder eine Systemschrift (nie Teil von Pagewing: Ergebnis hinge am Rechner).
- Text in Bildern, in Formularfeldern (gehört zu M11) oder in Annotationen (gehört zu M9/M10); Ersatz für Zeichen außerhalb von Geist (siehe 3.3: andere Schriftsysteme lassen sich bearbeiten, soweit die vorhandene Schrift die neuen Zeichen trägt).
- Das alte Wort aus früheren Fassungen der Datei entfernen (das ist Schwärzen, M14).

**Anti-Ziele** (Fehlschlag, auch wenn alles „funktioniert“)
- Ein Zeichen wird still durch ein anderes ersetzt, weggelassen oder als Kästchen/`.notdef` gezeichnet; der Nutzer erfährt es erst im Ergebnis.
- Der alte Text bleibt unter einem weißen Rechteck oder in einem weiteren Textobjekt im Seiteninhalt der neuen Fassung stehen.
- Unveränderte Zeichen ändern Schrift, Größe, Farbe oder ihren Abstand zueinander, oder die Seite ändert sich außerhalb des Laufs (der Rest eines Laufs rückt bei geänderter Breite mit, siehe 3.4; das ist gewollt).
- Ein UI-Text oder die Doku sagt „entfernt“, „sicher gelöscht“ oder „wie Word“ über den alten Text oder die Bearbeitung.
- Die Datei wird beim Bearbeiten ohne Nutzen größer: Geist wird nur eingebettet, wenn ein Lauf sie braucht (nie bei Korrekturen in der vorhandenen Schrift).
- Nichts davon im Startpfad; Messläufe öffnen den Modus nie.

## 2. Rahmen (gilt ohne neue Entscheidung)

Wie in `CLAUDE.md`: PDFium nur auf dem `PdfiumThread` in kurzen Aufträgen; der UI-Thread wartet nie; Kürzel nur im `CommandCatalog`; UI-Texte in beiden Sprachen (kein Text nennt etwas „rechtsgültig“, „sicher“ im Rechtssinn oder eine Signatur „gültig“); `FPDF_PageToDevice`/`DisplayTransform` ist die einzige Koordinatenquelle; Native AOT ohne Reflection; Einstellungen als `AppSettings` mit `with`; Tests auf Kopien im Temp-Ordner; kein neuer Rückruf aus PDFium; Rückfälle und Verluste stehen immer in der Oberfläche (Muster „im Zweifel ablehnen und vorher sagen, nie raten“ aus M10 bis M15).

## 3. Entwurf

### 3.1 Schichten **[Mein Vorschlag]**

- **`Pagewing.Core.TextEdit`** (neu, UI- und PDFium-frei): `TextRun` (ein bearbeitbares Stück: Seite, Rahmen in Seitenpunkten, Text, Schriftname, ob Teilmenge/eingebettet/Standardschrift, Größe, Farbe, Fingerabdruck), `TextEditRequest`/`TextEditPlan` (Lauf, neuer Text), `TextEditCheck` (Entscheidung nach 3.3: Ergebnis mit „bleibt“, „Ersatz für diese Zeichen“, „abgelehnt wegen …“), `TextEditLimits`, `TextEditLockReason` (warum ein Lauf nicht bearbeitbar ist, mit Satz in beiden Sprachen).
- **`Pagewing.Core.Pdf/PdfDocument.TextEdit.cs`** (Zugang zu PDFium: Läufe einer Seite lesen, Lauf ersetzen) und **`Pagewing.Pdfium/PdfiumTextEdit`** (Spiegeltypen wie bei Zeichnungen). Wiederverwendet werden darf, was Schwärzen und Textfeld schon haben: `PdfiumRedactionNative.ReadTextFacts`/`CreateRuns` (Textobjekt neu aufbauen), `PdfiumGlyphs` (Glyphenbreiten und -pfade), Geist Regular aus dem Core (`TextBoxFont`, `TextBoxCharacters`).
- **Verlauf:** ein neuer `AnnotationCommand` (Arbeitsname `TextEdit`) im gemeinsamen Verlauf (`AnnotationEditor`), wie `FormValue` und `OcrLayer`; ein Bearbeiten ist ein Schritt, zählt als ungespeicherte Änderung (`IsDirty`, `AsksBeforeClosing`, Update-Weg, Warnkarte bei Signaturen). Wie Rückgängig den Zustand vor dem Ersetzen herstellt (Textobjekt neu aufbauen oder Seiteninhalt sichern) ist **[Baumeister]**, abhängig vom Spike.
- **App:** `TabViewModel.TextEdit.cs` (Modus, Lauf-Auswahl, Entwurf), `Controls/TextEditOverlay` (Umrandung bearbeitbarer Läufe beim Überfahren, Sperrgrund), `Views/TextRunEditor` (Eingabefeld über dem Lauf, nach dem Muster von `TextBoxEditor`), `Views/TextEditToolbar`/Hinweisleiste, `Views/DocumentView.TextEdit.cs`, Kürzel nur im `CommandCatalog` (Arbeitsname `text.edit`, Strg+Umschalt+E ist schon `annotate.ellipse`; die Geste wählt der Baumeister aus den freien **[Baumeister]**).

### 3.2 Was bearbeitet wird: der Lauf

- **Einheit [Mein Vorschlag]:** ein **Lauf** = aufeinanderfolgende Zeichen einer Zeile mit gleichem Schriftobjekt, gleicher Größe, Farbe und Matrix. Eine Zeile mit mehreren Stilen besteht aus mehreren Läufen, jeder mit eigenem Eingabefeld; der Cursor wechselt nie über eine Stilgrenze. So bleibt jedes Zeichen in dem Stil, in dem es steht, und es gibt keine Stilmischung zu entscheiden. Wie Pagewing „eine Zeile“ erkennt, folgt der Zeilenregel von `TextFlow` (Grundlinie, Abstand), ergänzt um die Zusammenführung von Textobjekten, die PDFium wegen `TJ`-Verschiebungen oder Kerning zerlegt hat **[Spike]**.
- **Gesperrt, mit sichtbarem Grund (nie still)** **[Mein Vorschlag, Umfang nach Spike]**: unsichtbarer Text (Darstellungsmodus 3; das ist auch die OCR-Ebene, deren Wörter man nicht bearbeitet), Type-3-Schriften, senkrechte Schreibrichtung, Text mit Schnitt- oder Strichmodus, den Pagewing nicht sicher neu schreibt, Text in verschachtelten Form-XObjects (falls der Spike zeigt, dass PDFium ihn nicht erreichbar oder nicht sicher ersetzbar macht), **Text in einem Form-XObject, das mehrere Seiten teilen** (Kopf- und Fußzeilen: Ersetzen änderte andere Seiten), **Text mit `/ActualText`, `/Alt` oder `/E` im Markierten Inhalt oder in der Strukturebene** (Extraktion und Suche lieferten weiter den alten Text), Text in Dokumenten, die Pagewing nicht speichern kann (verschlüsselt: `EncryptedNotSupported`, `SourceModified`). Gedrehter Text und gedrehte Seiten gehören **nicht** dazu (die Matrix trägt die Drehung; wie bei M15 ohne eigene Rotations- oder CropBox-Rechnung).
- **Identität:** Ein Lauf wird über Seite, Position in der Reihenfolge der Seitenobjekte **und** einen Fingerabdruck (Text, Matrix, Schrift) angesprochen; stimmt der Fingerabdruck beim Ersetzen nicht mehr, lehnt Pagewing ab (Seite hat sich geändert), statt irgendein Objekt zu treffen **[Baumeister]**.

### 3.3 Der Zeichenvorrat und die Ersatzschrift **[Entscheidung]**

**Grundregel [Mein Vorschlag]:** Geprüft werden nur die **neu getippten** Zeichen. Unveränderte Zeichen bleiben immer in ihrer vorhandenen Schrift, auch wenn Geist sie nicht hätte (ein Lauf mit „ł“, „→“ oder kyrillischen Zeichen bleibt für einen Tippfehler an anderer Stelle bearbeitbar). Ein Lauf in einem anderen Schriftsystem ist bearbeitbar, soweit seine Schrift die neuen Zeichen trägt; fehlen sie und sind sie nicht in Geist, wird abgelehnt.

Beim Tippen (und vor dem Übernehmen) entscheidet `TextEditCheck` für die neuen Zeichen eines Laufs, **in dieser Reihenfolge**:

1. **Alle Zeichen stehen in der vorhandenen Schrift:** Die Schrift bleibt. Das ist der Normalfall bei Korrekturen und bei Standardschriften und vollständig eingebetteten Schriften. Für eine Teilmenge sind „vorhanden“ die Zeichen, die die Schrift tatsächlich zeichnen kann (nicht nur die, die das Dokument schon benutzt hat, wenn der Spike zeigt, dass die Schrift mehr trägt); wie das gemessen wird (Zuordnung Unicode → Glyphe, `/ToUnicode`, `/Encoding`, cmap, `FPDFFont_GetGlyphWidth`) ist **[Spike]**.
2. **Einzelne Zeichen fehlen:** Nur diese Zeichen werden in **Geist Regular** gesetzt (Größe, Farbe, Grundlinie wie der Lauf), der Rest bleibt in der vorhandenen Schrift. Die Oberfläche nennt vorher die Zeichen („In dieser Schrift fehlen: ä, ß – sie werden in Geist gesetzt“).
3. **Der Nutzer wählt „ganze Zeile in Ersatzschrift“** (eine Schaltfläche im Hinweis, nie vorgewählt; nur angeboten, wenn **alle** Zeichen des Laufs, alte wie neue, in Geist stehen): der ganze Lauf in Geist.
4. **Ein neues Zeichen fehlt auch in Geist** (`TextBoxCharacters`) und in der vorhandenen Schrift: Der neue Text wird **als Ganzes abgelehnt**, nie ersetzt oder gekürzt (Muster der Textfelder und der Formulare). Der Hinweis nennt die Zeichen.

Die Ersatzschrift ist **Geist, nichts anderes** (sie liegt einmal im Core, keine Systemschrift, damit das Ergebnis auf jedem Rechner gleich ist). Dass sie nicht zu einer Serifen- oder Spezialschrift passt, ist eine bekannte Grenze, die der Hinweis benennt; weitere eingebettete Ersatzschriften sind Backlog. Eingebettet wird sie wie in M10 und M15 (ganze Schrift, rund 25 KB nach Flate, einmal je Dokument und Sitzung, solange Läufe ein Schrift-Handle teilen) **und nur, wenn ein Lauf sie braucht**. Nach einem Speichern und Neuöffnen kennt die Sitzung die schon eingebettete Schrift nicht mehr und bettet sie bei der nächsten Ersetzung noch einmal ein; das nimmt M16 hin (Backlog: eine eigene eingebettete Geist erkennen und wiederverwenden, wie bei der OCR-Ebene). Wie das Setzen einer Folge aus zwei Schriften in einer Zeile positioniert wird (Breiten beider Schriften, Abstände) ist **[Baumeister]**.

### 3.4 Breite, Überlappung, Platz **[Mein Vorschlag]**

- Der Lauf behält seinen **Startpunkt** (links, an der Grundlinie, in Leserichtung); ein längerer Text wächst nach rechts, ein kürzerer lässt Platz. Es gibt keinen Umbruch.
- **Der Rest des Laufs hinter der Änderung rückt mit** (um die Breitendifferenz), damit die Zeile zusammenhängend bleibt; ein Lauf wird als Ganzes ersetzt (kein Wort- oder Zeichenweises Umschreiben), sein unveränderter Text behält Schrift, Größe, Farbe und Abstände.
- Der unveränderte Teil vor der Änderung behält seine Zeichenabstände (`TJ`, `Tc`, `Tw`, `Tz`); der Teil nach der Änderung wird um die Breitendifferenz verschoben, mit seinen eigenen Abständen **[Spike: ob PDFium das ohne eigene Positionsrechnung erlaubt; `FPDFText_SetPositions` kennt `PdfiumRedactionNative.CreateRuns`]**.
- Überlappt der neue Rahmen anderen Text der Seite oder ragt er über den Seitenrand, zeigt die Hinweisleiste das **nicht blockierend** an („Der Text ragt über den Rand / überdeckt anderen Text“). Pagewing prüft das gegen den Schnappschuss `PageText` (Zeichenrahmen) und die Seitengröße.

### 3.5 Bedienung **[Mein Vorschlag]**

- Ein eigener Modus „Text bearbeiten“ neben Kommentieren, Ausfüllen, Organisieren, Schwärzen (gegenseitig ausschließend, Grund sichtbar, wie dort). Nur nach dem ersten scharfen Bild, nie im Startpfad; Messläufe öffnen ihn nie (`TextEditStartPathTests`).
- Im Modus werden bearbeitbare Läufe beim Überfahren umrandet (Umriss, nie nur Farbe; gesperrte mit Schloss und Grund wie bei den Formularen). Ein Klick öffnet ein Eingabefeld über dem Lauf; **Enter** übernimmt, **Esc** verwirft (über `DocumentView.TryEscape`, nicht über den Katalog), Tab fängt den Fokus nie (WCAG 2.1.2). Mit der Tastatur: Pfeile/Tab zum nächsten Lauf, Enter zum Bearbeiten.
- Hinweisleiste im Modus (nicht wegklickbar): „Zeilen bearbeiten, keine Absätze. Fehlende Zeichen setzt Pagewing in einer Ersatzschrift.“ (Wortlaut **[Baumeister]**, beide Sprachen).
- Text im offenen Feld ist eine ungespeicherte Änderung und wird vor Speichern, Drucken, Modusende, Schließen und Update übernommen (Muster `FlushTextBoxDraft`); was nicht geht, bleibt mit Grund im Feld, nie still verworfen.

### 3.6 Speichern **[Entscheidung]**

Normalfall ist **inkrementelles Speichern** über die vorhandene Pipeline (`DocumentSaver.SaveAsync`): schnell, signierte Bereiche bleiben der Anfang der Datei. Folge, die Pagewing **offen sagt**: Der alte Text bleibt in der früheren Fassung der Datei erhalten. Die Speicherkarte nennt es einmal je Tab und Dokument bei der ersten Textänderung, **als Hinweis ohne Bestätigung** (kein neuer Zustand von `PendingSave`, Muster des Signaturhinweises; „Der alte Text bleibt in früheren Fassungen der Datei. Zum echten Entfernen: Schwärzen.“), die Hinweisleiste des Modus kurz dauerhaft. Es gibt keinen Zwang zum vollständigen Neuschrieb. Kein Text sagt „entfernt“ oder „gelöscht“ über den alten Text. Die vorhandene `SignaturePolicy` gilt unverändert (DocMDP 1 und 2: Warnkarte); Pagewing sagt nie, ob eine Signatur danach „gültig“ ist.

### 3.7 Grenzen gegen feindliche Dateien **[Baumeister]**

Obergrenzen für Läufe je Seite, Zeichen je Lauf (Vorschlag: gleich `TextBoxSpec`, 2.000 Zeichen), Textobjekte je Seite, Zeit je Lesen/Ersetzen (kurze Aufträge, `Background`, Abbruch zwischen Läufen); Überschreitung = Lauf gesperrt mit Grund, nie ein abgeschnittenes Ergebnis.

## 4. Spike (erste Aufgabe des Plans)

Vor allem anderen klärt ein Spike mit Wächtertests (Befunde, die ein PDFium-Update brechen kann, nie Erwartungen zum Lockern) und Mustern für die Handprüfung (`PAGEWING_SPIKE_OUT`), auf Fixtures mit eingebetteter Teilmenge (TrueType-Teilmenge, Type0/Identity-H wie aus Word, Type1-Teilmenge), mit Standard-14-Schrift ohne Einbettung, mit vollständig eingebetteter Schrift, mit mehreren Stilen in einer Zeile, mit `TJ`-Verschiebungen, mit gedrehtem Text und gedrehter Seite mit CropBox:

1. **Lesen:** Zuordnung Textobjekt ↔ Zeichen, Zusammenführen zu Läufen, Erkennen des Schrifttyps (Teilmenge, eingebettet, Standard), Erreichbarkeit von Text in Form-XObjects, ob sich geteilte Form-XObjects und `/ActualText` erkennen lassen (sie sperren den Lauf, siehe 3.2).
2. **Zeichenabdeckung:** Wie stellt man fest, ob eine Schrift ein Unicode-Zeichen zeichnen kann? Was tut `FPDFText_SetText` mit einem fehlenden Zeichen (Glyphe 0, nichts, Fehler)? Reicht ein Rücklesen (wie bei den Formularen) oder braucht es `FPDFText_SetCharcodes` mit eigener Zuordnung (bei Identity-H-Teilmengen ist der Zeichencode die Glyphennummer)? Das ist die wichtigste offene Frage.
3. **Ersetzen:** Textobjekt ersetzen (neu aufbauen, altes entfernen), Reihenfolge im Seiteninhalt, Beibehalten von Abständen, Matrix, Farbe, Markierungen (`/PagewingOcr` bleibt unberührt, weil unsichtbarer Text gesperrt ist).
4. **Zwei Schriften in einer Zeile** (vorhandene + Geist), Positionierung des Rests.
5. **Rückgängig:** Was reicht, um den Zustand vor dem Ersetzen wiederherzustellen (Textobjekt neu aufbauen oder Seiteninhalt sichern)? Verlauf-Obergrenze (Vorbild: 32 MiB der Signaturbilder).
6. **Inkrementelles Speichern und Neuöffnen**, Originalbytes als Anfang, Dateigröße je Bearbeiten.
7. **Fremdviewer:** Chrome und Acrobat Reader zeigen und extrahieren das Ergebnis richtig; bearbeitet Acrobat die Zeile danach selbst weiter (eine ehrliche Antwort, kein Ziel)?

Hängt Punkt 2 so, dass sich die Abdeckung für Teilmengen nicht sicher bestimmen lässt, gilt der Rückfall **[Mein Vorschlag]**: Teilmengenschriften gelten nur für Zeichen als „vorhanden“, die im Lauf selbst oder an anderer Stelle des Dokuments mit dieser Schrift schon stehen; alles andere kommt aus Geist. Das ist konservativ, kann aber nie ein falsches Zeichen zeichnen.

## 5. Tests und Abnahme

- **Roadmap-Kriterium:** korrigierte Zeile wird extrahiert wie erwartet, übrige Seite unverändert (Render-Vergleich), zusätzlich: alter Text steht in der neuen Fassung nicht mehr im Seiteninhalt (Textextraktion der neuen Fassung und die dekomprimierten Bytes des Seiteninhalts der neuen Fassung, **nicht** der früheren Revision) und in der Originalfassung der Datei bleibt er (die Anfangsbytes sind gleich).
- **Fälle:** Korrektur mit vorhandenen Zeichen (Schrift bleibt), mit fehlenden Zeichen (Ersatz nur dieser), „ganze Zeile in Ersatzschrift“, Zeichen auch in Geist fehlend (Ablehnung als Ganzes, nichts geändert), längerer und kürzerer Text, Überlappungshinweis, mehrere Stile in einer Zeile, gedrehte Seite mit CropBox, Standardschrift, gesperrte Läufe (unsichtbarer Text, Type 3), Rückgängig/Wiederholen, Speichern + Neuöffnen, Signatur-Warnkarte, Wechsel in andere Modi, Nichts-im-Startpfad.
- **Muster für die Handprüfung** (`packaging/README.md`, neuer Abschnitt „Text bearbeiten prüfen (M16)“): `text-edit-*.pdf` in Chrome und Acrobat Reader, darunter die ausdrückliche Antwort, ob Acrobat die Zeile weiter bearbeiten kann; eine eigene echte Datei.
- **Messung:** Öffnen des Modus und Ersetzen in einem Auftrag auf einer Seite mit vielen Läufen; Startzeit-Check auf dem Haupt-PC bleibt wie bei M14/M15 eine Handprüfung (`v1.0.0`).

## 6. Verhältnis zu anderen Funktionen

- **Schwärzen (M14):** Ein bearbeiteter Lauf ist gewöhnlicher Text; Schwärzen findet ihn, die Prüfung bleibt gültig (Test).
- **OCR (M15):** Die OCR-Ebene ist unsichtbarer Text und gesperrt; wer einen erkannten Scan korrigieren will, kann es nicht, weil das Bild das Sichtbare ist (Hinweis im Sperrgrund). Ein „Prüfmodus für unsichere Wörter“ bleibt Backlog.
- **Seiten organisieren (M13):** Der Modus sperrt umgekehrt Organisieren; ein Plan der Seiten und offene Textänderungen schließen sich aus (Grund sichtbar).
- **Textfeld (M10):** Neuer Text auf einer Seite bleibt das Textfeld; Text bearbeiten ändert nur vorhandenen.
