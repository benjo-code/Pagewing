# Milestone 10 – Zeichnen & Formen (Entwurf)

Stand 2026-10-25, erarbeitet im Brainstorming mit dem Projektinhaber. Grundlage sind Milestone 10 in [Docs/ROADMAP.md](../../ROADMAP.md), die [Spec von Teilprojekt 2](2026-10-24-teilprojekt2-annotieren-design.md) (Abschnitt „Rahmen für M10 bis M12“) und die [Design-Spec](2026-10-17-designsystem.md) (7.3, 7.7). Dieser Entwurf legt fest, **was** gebaut wird und **warum**; das **wie** im Einzelnen klärt der Plan (`Docs/superpowers/plans/`). Er ergänzt die Spec von Teilprojekt 2 und ersetzt deren Rahmen für M10.

## Absicht

Wer ein PDF mit Pagewing mit Stift, Formen und Textfeldern bearbeitet, soll sich keine Gedanken über das Werkzeug machen: Der Strich steht beim Ziehen sofort da, die Zeichnung lässt sich danach anfassen, verschieben und ändern, und nach dem Speichern sieht sie in jedem Programm gleich aus. Das knüpft an M9 an (gleiches Aussehen in jedem Viewer, sicheres Speichern) und bleibt dem Hauptmerkmal verpflichtet: Geschwindigkeit.

Gegenüber verbreiteten Programmen soll Pagewing in vier Punkten besser sein (Ergebnis einer Recherche; die Websuche war dünn, ein Teil der Einschätzung beruht auf allgemeinem Wissen):

1. **Gleiches Aussehen überall.** Die häufigste Ursache verschwundener Zeichnungen in anderen Programmen ist eine Annotation ohne eigenes Erscheinungsbild (`/AP`). Jede Annotation von Pagewing hat eines, und ein Round-Trip-Test sichert es.
2. **Kein Verzug beim Zeichnen.** Der Strich entsteht beim Ziehen auf der Ansicht ohne PDFium; erst beim Loslassen läuft ein kurzer Auftrag.
3. **Werkzeug bleibt, Eigenschaften merken sich.** Kein Neu-Einrichten je Farbe (bekannte Schwäche von Okular), kein Zurückspringen nach einem Strich.
4. **Nachträglich ändern, auch per Tastatur.** Auswählen, verschieben, Größe, Farbe und Dicke ändern, löschen, rückgängig. M9 hatte die Lücke, dass Anlegen nur mit der Maus geht; für Auswählen und Verschieben schließt M10 sie.

## Ziele

1. **Freihand** zeichnen (Stift, Maus), mit Glättung.
2. **Formen:** Rechteck, Ellipse, Linie, Pfeil.
3. **Textfelder** hinzufügen und bearbeiten (abhängig vom Spike, siehe „Zwei Risiken“).
4. **Auswählen, verschieben, Größe ändern**, Farbe und Dicke nachträglich ändern, löschen. Rückgängig und Wiederholen für alle neuen Arten (Grundlage `AnnotationHistory`/`AnnotationEditor` aus M9).
5. **Kommentar an jede Zeichnung** über das vorhandene Panel „Kommentare“.
6. **Zeichnungen auf der dunklen Seite und im Druck** behalten ihre Farbe und sind sichtbar.
7. **Stift zeichnet, Finger scrollt** (nach Zeigertyp), wo das System ihn meldet.

## Nicht-Ziele und Gegenziele

- Keine druckempfindliche Strichstärke (Ink kennt keine Breite je Punkt, Viewer zeigten es verschieden), keine Wolken, Polygone, Polylinien, Maßwerkzeuge, Stempel, keine Formenerkennung beim Loslassen (später möglich), keine Ebenen.
- Kein Bearbeiten von Annotationen, die Pagewing nicht versteht (siehe „Bearbeitbar“); kein Schwärzen (M14), keine Formulare (M11), keine Unterschrift (M12).
- Kein echtes `/Line`-Objekt und kein eigener PDF-Schreiber in M10 (Backlog, gehört eher zu Teilprojekt 3, QPDF-Entscheidung).
- Die Leseansicht zeigt keine Werkzeuge (Design-Spec 7.7); alles läuft im Modus „Kommentieren“.
- Gegenziel (Fehlschlag, auch wenn es technisch läuft): eine gespeicherte Zeichnung, die in einem gängigen Viewer fehlt oder verrutscht ist; ein Strich, der beim Zeichnen sichtbar hinterherhinkt; langsamerer Start oder zähes Scrollen mit vielen Strichen; Umlaute oder „€“, die in einem Textfeld in einem Viewer fehlen; Änderungen, die verloren gehen, ohne dass der Nutzer es bestätigt hat.

## Festgelegt (vom Projektinhaber, 2026-10-25: Vorschlag bestätigt)

1. Der Zuschnitt oben, einschließlich der Auswahl „Auswählen und Verschieben per Tastatur“ und „Stift zeichnet, Finger scrollt“.
2. **Pfeil und Linie sind in der Datei Freihand-Annotationen** (Ink, zwei bis drei Striche), das gleiche Aussehen in jedem Viewer hat Vorrang vor einem „bearbeitbaren Pfeil“ in Acrobat. Das echte `/Line` kommt nicht in M10.
3. **Textfelder beginnen mit einem Spike.** Trägt weder echtes FreeText noch der Ersatz (Stempel mit Text), wird M10 geteilt: M10 (alles außer Textfeld) und M10.1 (Textfeld).
4. Nicht in M10: druckempfindliche Breite, Formenerkennung, Wolken, Polygone, Maßwerkzeuge.

## Zwei Risiken (zuerst klären)

**Pfeil/Linie als Ink.** PDFium legt kein `/Line` an (`FPDFAnnot_IsSupportedSubtype` nennt Circle, FileAttachment, FreeText, Highlight, Ink, Link, Popup, Square, Squiggly, Stamp, StrikeOut, Text, Underline). Der Spike prüft, wie Pagewing eigene Pfeile und Linien später wiedererkennt (z. B. ein Merkmal in einem Textschlüssel wie `/Subj`; Zielwert „Pfeil“/„Linie“ im Panel statt „Freihand“) und ob das Merkmal bei Fremdprogrammen erhalten bleibt. Ohne Merkmal bleibt der Pfeil in Pagewing eine Freihand-Zeichnung (Anzeige „Freihand“), das ist hinnehmbar.

**Textfeld.** Die Schwierigkeit ist das Erscheinungsbild mit Schrift: Umlaute und „€“ müssen in Chrome, Acrobat Reader und Pagewing gleich erscheinen, ohne dass der Viewer eine Ersatzschrift raten muss. Zu prüfen sind (a) FreeText mit PDFiums Generator (`/DA`, `/DS`, Schrift als Ressource, Umbruch), (b) FreeText mit selbst gebautem `/AP`, (c) ein Stempel, dessen Inhalt Textobjekte sind (`FPDFAnnot_AppendObject` erlaubt nur Ink und Stempel), (d) die Auswirkung auf Bearbeitbarkeit nach dem Speichern und in Acrobat. Prüfmatrix wie in M9: Chrome und Acrobat Reader unter Windows, gedrehte Seite, CropBox mit Versatz, Umlaute, „€“, Zeilenumbruch, mehrere Schriftgrößen. Das Ergebnis steht als Eintrag in `Docs/DECISIONS.md` und entscheidet über die Teilung.

## Meine Entscheidungen (Vorschläge, im Plan zu bestätigen)

**Bedienung** (richtet sich nach Design-Spec 7.7; die Leiste des Modus „Kommentieren“ wächst um die neuen Werkzeuge, ein neuer Modus entsteht nicht)

- **Werkzeuge:** Auswählen, Freihand, Rechteck, Ellipse, Linie, Pfeil, Textfeld (nach Spike) zusätzlich zu Hervorheben, Unterstreichen, Durchstreichen. Ein Werkzeug bleibt aktiv, bis ein anderes gewählt wird oder Esc/„Fertig“ den Modus beendet; ein Strich beendet es nicht. Kürzel im `CommandCatalog` (Titel in beiden Sprachen, keine Kollision); Esc des Modus bleibt wie in M9 (`DocumentView.TryEscape`).
- **Eigenschaften je Werkzeug gemerkt:** Farbe und Dicke je Werkzeug in den Einstellungen (nur mit `with` ändern). Farbe: eine kleine feste Palette; Vorschlag: Rot, Schwarz, Blau, Grün, Orange, mit Namen für Tooltip und Screenreader, Standard Rot (**mein Vorschlag**; Markierungsfarben sind PDF-Inhalt und gehören nicht ins Designsystem, die Auswahlknöpfe müssen 3:1 erfüllen). Dicke: drei Stufen (Vorschlag 1, 2, 4 pt). Füllung bei Rechteck und Ellipse: aus oder halbtransparent (Vorschlag 25 %), falls PDFiums Generator `/IC` und `/CA` sauber schreibt (Spike), sonst nur Umriss.
- **Zeichnen:** Der Strich erscheint beim Ziehen sofort auf der Ansicht (eigene Zeichenschicht, nur das Rechteck des Strichs wird neu gezeichnet), PDFium läuft erst beim Loslassen in einem kurzen Auftrag (`PdfiumThread`, nie der UI-Thread). Punkte werden geglättet und ausgedünnt (Vorschlag: Vereinfachung nach Douglas-Peucker mit einer Toleranz in Seitenpunkten, dann geglättet); das hält Dateien klein. **Umschalt** zwingt eine Linie/einen Pfeil auf Vielfache von 45° und ein Rechteck/eine Ellipse auf Quadrat/Kreis.
- **Pfeil:** Schaft plus zwei Striche der Spitze, Spitzenlänge ein Vielfaches der Dicke mit Mindestgröße (Zahlen im Plan).
- **Ein Strich = eine Annotation**, eine Form = eine Annotation. Das Panel „Kommentare“ darf mit vielen Strichen nicht unbenutzbar werden (Plan: Gruppieren oder Filter „Zeichnungen“); die Grenze von 10.000 Annotationen je Dokument (`PdfDocument.MaxAnnotations`) gilt weiter und eine Meldung, wenn sie erreicht ist, ist Teil des Plans.
- **Auswählen:** Klick in einem Zeichenwerkzeug oder das Werkzeug „Auswählen“ wählt die oberste bearbeitbare Annotation an der Stelle. Mit Griffen: Verschieben, Größe ändern (bei Ink alle Punkte mitskaliert, bei Formen das Rechteck), Entf löscht. **Tastatur:** Tab geht durch die auswählbaren Annotationen der Seite, Pfeile verschieben um 1 pt (Umschalt 10 pt), Entf löscht, Enter öffnet den Kommentar (bzw. den Text beim Textfeld). Die Auswahl trägt Rahmen und Griffe, nie nur Farbe als Zustand (Design-Spec).
- **Stift und Finger:** In einem Zeichenwerkzeug zeichnet der Stift (`PointerType.Pen`), die Maus zeichnet, ein Finger (`PointerType.Touch`) scrollt und zoomt wie sonst. Wo das System keinen Stift meldet, ändert sich nichts.

**Bearbeitbar** (Erweiterung der Regel von M9, `PdfAnnotation.Editable`)

- Auch bearbeitbar sind Freihand (Ink), Rechteck und Ellipse, auch fremde, solange keine Antwort (`/IRT`) auf sie zeigt. Das Verschieben und Ändern der Größe **erzeugt das Erscheinungsbild neu** (Neuanlegen mit demselben `/NM`, wie der Rückgängig-Weg in M9). Trägt eine fremde Annotation Eigenschaften, die Pagewing beim Neuerzeugen verlöre (gestrichelte Rahmenart, `/BE` Wolke, `/BS` außer „durchgezogen“, Verlauf, mehrere Farben), sind Verschieben und Größe für sie gesperrt; Löschen, Kommentar und Farbe bleiben (**meine Setzung**, im Plan zu bestätigen).
- Nicht bearbeitbar bleiben Linien (`/Line`) fremder Programme, Stempel, Dateianhänge, Formularfelder, Links, Haftnotizen und fremde FreeText-Felder ohne bekanntes Erscheinungsbild; sie erscheinen in der Liste und bleiben unberührt.
- Rückgängig, Wiederholen und Speichern verhalten sich wie in M9 (Verlauf wächst um die Operationen „anlegen“, „verschieben/Größe“, „Farbe/Dicke“, „löschen“, „Text ändern“; ein Verschieben mit der Maus ist **ein** Schritt).

**Erscheinungsbild und dunkle Seite**

- Wie in M9 „Variante D“: anlegen, einmal mit `FPDF_ANNOT` rendern, PDFiums Generator schreibt das `/AP`; Flag Print; fehlt das `/AP` danach, ist es ein Fehler und die Annotation wird entfernt. Der Plan prüft, welche Arten der Generator sauber schreibt (Ink, Square, Circle laut Header und M9-Erkenntnissen ja) und wo ein selbst gebautes `/AP` nötig wird (vermutlich nur das Textfeld).
- **Dunkle Seite:** Ich vermute, dass PDFiums Farbschema (`FPDF_CONVERT_FILL_TO_STROKE`, Farben auf Text/Hintergrund umgestellt) Zeichnungen auf die Textfarbe zieht; M9 hat Ink und FreeText ausdrücklich ausgenommen. Der Spike misst das. Zeichnungen sollen ihre Farbe behalten (aufgehellt auf 3:1, wie bei den Linien in `PdfiumDarkMarkups`); der Weg ist dem Builder überlassen, Vorbild ist `PdfiumDarkMarkups`.
- **Druck:** gedruckt wird hell, mit Zeichnungen (Test wie bei den Markierungen).

**Grenzen gegen feindliche Seiten:** Grenzen von M9 gelten weiter (10.000 Annotationen, 20.000 Einträge in `/Annots` je Seite, `MaxLinkWork`). Neu: Höchstzahl von Punkten je Ink-Annotation beim Lesen und beim Anlegen (Zahl im Plan, mit Test), damit eine fremde Datei mit riesigen `InkList` weder die Ansicht noch das Neuerzeugen bremst.

## Randbedingungen

- **Startpfad:** nichts Neues vor dem ersten scharfen Seitenbild; die Zeichenschicht, Werkzeugerweiterungen und Griffe entstehen erst, wenn der Modus beginnt. Messläufe bleiben unverändert.
- **Messen und festhalten:** Zeit vom Zeigerereignis bis zum sichtbaren Pixel des Strichs (Vorschlag: höchstens ein Frame hinterher; Messung statt Annahme), Dauer des Auftrags beim Loslassen, Scroll-Messlauf (`--measure-scroll`) mit 1000 Strichen auf einer Seite und mit 200 Seiten, Dateigröße je 100 Striche mit und ohne Glättung. Bis eine Zahl vorliegt, gibt es kein Budget; wird eines nötig, ist es eine Entscheidung in `DECISIONS`.
- **PDFium:** nur über den `PdfiumThread`, jede Änderung ein kurzer Auftrag, der UI-Thread wartet nie; ein neuer Rückruf aus PDFium kommt erst nach Prüfung in `UnmanagedCallbackTests`. Bindings nur für das, was gebraucht wird, Konstanten gegen die Header von `chromium/8100` prüfen.
- **Native AOT:** keine Reflection, JSON nur mit Source-Generator. **Abhängigkeiten:** keine neuen; kommt eine hinzu, Lizenzmanifest und `DECISIONS`-Eintrag.
- **Tests:** schreiben nie in den echten Benutzerordner und nie neben die Eingaben in `testdata`; Test-PDFs mit klarer Lizenz in `testdata/pdfs/LICENSES.md` und im Render-Korpus. Tests, die Theme oder Dichte umschalten, laufen in der Collection „Localization“.
- **Texte** in Deutsch und Englisch (resx). Keine Bezeichnung als „rechtsgültig“.
- **Dokumentation:** `Docs/DECISIONS.md` (Spike-Ergebnisse, Pfeil als Ink, Merkmal zur Wiedererkennung, Glättung, Bearbeitbar-Regel, dunkle Seite, Textfeld-Entscheidung), `CHANGELOG.md` unter `[Unreleased]`, `CLAUDE.md` („Speichern und Annotationen“), Design-Spec 7.7 (Werkzeugleiste, Auswahl) und 7.3, Gesamt-Spec 5.5, Hilfekarte, Roadmap (bei Teilung M10.1).

## Abnahme

Die Kriterien der Roadmap bleiben, die Überschrift konkretisiert sich:

1. **Round-Trip-Tests** je Art (Freihand, Rechteck, Ellipse, Linie, Pfeil, Textfeld soweit gebaut): angelegt, gespeichert, neu geladen sind Art, Geometrie, Farbe, Dicke, Kommentar und Text unverändert; auf gedrehter Seite und bei CropBox mit Versatz; `/AP` ist ein Form-XObject und die Annotation hat das Flag Print; der Anfang der Datei ist byteweise das Original (Speicher-Pipeline aus M9).
2. **Bearbeiten:** Verschieben, Größe, Farbe/Dicke, Löschen und Rückgängig/Wiederholen für jede Art (Core-Test ohne PDFium, Round-Trip mit PDFium); fremde Annotationen mit nicht erhaltbaren Eigenschaften bleiben beim Verschieben gesperrt (Test).
3. **Fremdviewer:** Muster aus der endgültigen Pipeline erscheinen in Chrome und Acrobat Reader korrekt (manuelle Prüfung nach `packaging/README.md`, „Annotationen im Fremdviewer prüfen“; `InteropSampleTests` erzeugen die Muster). Wie in M9 kann der Projektinhaber die Handprüfung auf `v0.2.0` verschieben.
4. **Dunkle Seite und Druck:** Zeichnungen behalten Farbe und sind sichtbar (Test mit gemessenen Pixeln); der Druck liefert sie.
5. **Tastatur und Automation:** Auswählen, Verschieben und Löschen per Tastatur ohne Maus (Headless-Test); Werkzeugleiste, Griffe und Auswahl haben Automation-Namen und geben Zustände nicht nur über Farbe an.
6. **Geschwindigkeit:** die oben genannten Messungen liegen als Eintrag in `DECISIONS` vor; Start-Check unverändert grün.

## Setzungen nach der Gegenlese (meine Entscheidungen, bitte beim Lesen prüfen)

1. **Textfeld-Spike und Teilung.** Wie in M9 prüft der Projektinhaber die Spike-Dateien in Chrome und Acrobat Reader (Windows) von Hand; Acrobat gehört zum Maßstab „bestanden“, weil Acrobat die Referenz für FreeText ist. Der Plan stellt den Spike an den Anfang und plant die Textfeld-Aufgaben **bedingt**: Sie folgen erst, wenn das Ergebnis des Spikes in `DECISIONS` steht. Alles andere in M10 (Freihand, Formen, Pfeil, Auswählen) hängt vom Textfeld nicht ab und läuft davor. Fällt die Prüfung negativ aus, wird das Textfeld M10.1. Mindestumfang der Zeichen: Latin-1 plus „€“ (Deutsch und Englisch); andere Schriftsysteme sind kein Ziel von M10. Ob dafür eine eingebettete Schrift nötig ist, klärt der Spike.
2. **Pfeil und Linie sind parametrisch, solange sie erkannt sind.** Eine von Pagewing erkannte Linie oder ein Pfeil besteht im Modell aus Anfang, Ende und Dicke; die Striche (Schaft, Spitze) werden daraus neu erzeugt. Griffe: zwei Endpunkte, die Spitze bleibt unverzerrt. Fehlt das Erkennungsmerkmal (Fremdprogramm, Merkmal verloren), ist die Annotation „Freihand“: verschiebbar und in der Größe änderbar als Punktmenge, auch ungleichmäßig, mit acht Randgriffen.
3. **Fremde Zeichnungen und Verschieben.** Verschieben und Größe ändern gelten nur für Annotationen, deren Erscheinungsbild Pagewing selbst bzw. PDFiums Generator geschrieben hat (erkennbar am privaten Schlüssel `/PDFIUM_HasGeneratedAP`, zusätzlich zu den Sperrgründen oben). Hat eine fremde Annotation ein eigenes `/AP` (Schatten, Deckkraft, Muster), bleiben Auswählen, Kommentar und Löschen; Verschieben, Größe, Farbe und Dicke sind gesperrt und das Panel nennt den Grund. Ein Verschieben des vorhandenen `/AP` per Matrix ist kein Teil von M10 (Backlog).
4. **Markierungen im Werkzeug „Auswählen“.** Hervorhebung, Unterstreichung und Durchstreichung sind auswählbar (Rahmen statt Griffe), aber nicht verschiebbar und nicht in der Größe änderbar, weil sie am Text hängen; Farbe, Kommentar und Löschen gehen wie bisher. Zeichnungen liegen im Hit-Test über Markierungen; die Tab-Reihenfolge folgt der Lesereihenfolge der Seite (oben nach unten, links nach rechts nach der Oberkante).

## Dem Builder überlassen

Zuschnitt der Typen in `Pagewing.Core` und `Pagewing.Pdfium`; Aufteilung der Aufgaben und ihre Reihenfolge nach den Spikes; Algorithmus und Toleranzen der Glättung; Form und Größe der Griffe; Kürzel der Werkzeuge; Palettenwerte (innerhalb der Vorgaben); Gestaltung von Leiste und Auswahl innerhalb der Design-Spec; Verhalten bei zwei Tabs derselben Datei; Darstellung vieler Striche im Panel; das genaue Merkmal zur Wiedererkennung von Pfeil und Linie, soweit der Spike eines trägt.
