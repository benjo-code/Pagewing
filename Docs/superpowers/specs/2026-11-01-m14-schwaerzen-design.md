# Teilprojekt 3 – Schwärzen (M14, M14.1): Design

Stand: 2026-11-01 · Status: freigegeben vom Projektinhaber (2026-11-01, einschließlich PDFium-only mit Verlustliste des Neuaufbaus und Ablehnung verschlüsselter Dateien) · Gehört zu [Gesamt-Spec](2026-10-16-pagewing-design.md), Abschnitt 6 (Regeln fürs Schwärzen) und zur [Spec von M13](2026-10-31-teilprojekt3-seiten-design.md), Abschnitt 11 · Roadmap: Milestone 14 (neu: 14 und 14.1)

Kennzeichnung wie in der Spec von M13: **[Entscheidung]** hat der Projektinhaber getroffen oder bestätigt. **[Mein Vorschlag]** ist eine Annahme von Claude, die der Projektinhaber beim Lesen prüfen soll. **[Baumeister]** überlässt der Plan der Umsetzung, mit der genannten Grenze. **[Spike]** heißt: nicht geprüft, die erste Aufgabe des Plans klärt es, und die Entscheidungsregel steht dabei.

## 1. Absicht

Pagewing soll Inhalte so aus einem PDF entfernen, dass sie **nachweislich nicht mehr in der Datei stehen**: nicht als Text, nicht in Bildpixeln, nicht in Metadaten, Kommentaren, Lesezeichen, älteren Revisionen oder einer unsichtbaren OCR-Schicht. Der Anlass: Die meisten Pannen (Manafort, TSA, Apple gegen Samsung) entstanden, weil ein schwarzes Rechteck über lebendem Text lag, und Werkzeuge, die richtig entfernen, lassen die Bereinigung der versteckten Daten als Option oder überlassen die Kontrolle dem Menschen (Acrobat: „Sanitize“ ist ein Haken im Dialog; die Behörden-Anleitungen sind Handarbeits-Checklisten).

Erfolg heißt: Wer eine Datei schwärzt und „Anwenden“ wählt, bekommt entweder eine Datei, von der Pagewing **selbst nachgeprüft** hat, dass die geschwärzten Inhalte weder extrahierbar noch in den entpackten Rohbytes zu finden sind, oder eine klare Absage ohne Ausgabedatei. Der Prüfbericht ist Teil des Ergebnisses.

## 2. Befund der Recherche (2026-11-01)

Die Quellen sind überwiegend Herstellerblogs und Dokumentation; nichts davon wurde selbst nachgemessen. Eine unabhängige Prüfung, ob eines dieser Programme alles entfernt, fand sich nicht.

- **Verbreiteter Fehler:** Schwarzes Rechteck über lebendem Text; bei Scans bleibt die OCR-Textschicht ([Redactable](https://www.redactable.com/blog/most-embarrassing-redaction-failures-in-history-and-how-they-can-be-avoided), [Datalogics](https://www.datalogics.com/pdf-redaction-fails)).
- **Acrobat:** Markieren, dann Anwenden; „Sanitize“ (Metadaten, Lesezeichen, Kommentare, Anhänge, Skripte, versteckte Ebenen) ist im Anwenden-Dialog ein Schalter ([Adobe](https://helpx.adobe.com/acrobat/current/removing-sensitive-content-pdfs.html), [NSA-Anleitung](https://www.cs.columbia.edu/~Smb/doc/Redaction-of-PDF-Files-Using-Adobe-Acrobat-Professional-X.pdf)). Suchen und Schwärzen mit Mustern (Telefon, Kontonummern).
- **PDF-XChange:** Markieren, Suchen mit Mustern (Telefon, Kreditkarte, E-Mail, Datum), Füllfarbe, Weichzeichnen, Verpixeln, Überlagerungstext, „Alle anwenden“/„Ausgewählte anwenden“; Sanitize mit Rasterauflösung ([Hilfe](https://help.pdf-xchange.com/pdfxe11/redacting-pages-and-sanitizing-documents.html)).
- **Okular:** legt nur eine Ebene darüber, der Text bleibt ([dedoimedo](https://www.dedoimedo.com/computers/okular-redact-pdf-files.html), 2021, möglicherweise veraltet).
- **MuPDF/PyMuPDF** (AGPL, bei uns ausgeschlossen): Redact-Annotation, Anwenden entfernt Text und Bildpixel; Schwachstelle sind Bilder mit Transparenz ([Issue 434](https://github.com/pymupdf/PyMuPDF/issues/434)).
- **pdf-redactor** (Python): nur Text, Info-Dictionary und XMP; Grafiken, Bilder, Anhänge, Formulare nicht ([GitHub](https://github.com/JoshData/pdf-redactor)).
- **Stirling-PDF:** Entfernen von Text ab 1.3.0; als Notlösung „Seite in Bild umwandeln“ (aller Text der Seite geht verloren) ([Release 1.3.0](https://piefed.jeena.net/post/234279)).
- **PDFium-Komponente (Delphi)** als Bauvorlage ([loslab](https://blog.loslab.com/en-us/pdfium-component/pdfium-component-operator-level-redaction-resource-prune-delphi.html)): trifft ein Textobjekt den Bereich teilweise, werden die überlebenden Zeichen einzeln als neue Textobjekte mit demselben Schriftobjekt neu erzeugt; achsenparallele Rechtecke und Bilder werden zugeschnitten, alles andere ganz entfernt; Zeichen ohne verlässliche Unicode-Zuordnung brechen den Vorgang ab, statt zu raten; danach neu öffnen und den Text erneut lesen.
- **PDFium selbst (ausgelieferte Version 157.0.8100, Header geprüft):** Es gibt `FPDFPage_RemoveObject`, `FPDFFormObj_CountObjects/GetObject/RemoveObject`, `FPDFImageObj_SetBitmap`, `FPDFTextObj_GetText`, `FPDFTextObj_GetFont`, `FPDFText_SetText`, `FPDFText_SetCharcodes`, `FPDFText_SetPositions`, `FPDFPageObj_GetBounds`. Es gibt **keine** Funktion zum Lesen der rohen Zeichencodes eines Textobjekts und keine zum Schreiben von Info-Dictionary oder XMP. Ob Änderungen an Objekten **innerhalb** eines Form-XObjects beim Speichern in dessen Stream zurückgeschrieben werden, ist nicht dokumentiert **[Spike]**.

## 3. Ziele, Nicht-Ziele, Anti-Ziele

**Ziele (M14)**
- Inhalte markieren (Bereich ziehen, Textstelle auswählen, Suchen und alle Treffer markieren), Markierungen ansehen und ändern, Vorschau, Anwenden.
- Anwenden entfernt Text zeichengenau, überschreibt Bildpixel, behandelt Vektorpfade, Form-XObjects (rekursiv) und Annotationen im Bereich und bereinigt die Datei von versteckten Daten (Abschnitt 5.4), immer mit vollständigem Neuschrieb.
- Pagewing prüft das Ergebnis selbst (Abschnitt 5.5) und zeigt den Prüfbericht.
- CLI `pagewing redact`.

**Ziele (M14.1)**: Muster (E-Mail, IBAN, Telefon, Kreditkarte nach Prüfsumme), Weichzeichnen und Verpixeln als Füllung, Überlagerungstext je Markierung, optional die Notlösung „Seite in Bild umwandeln“ (mit ausdrücklicher Wahl).

**Nicht-Ziele** – als Backlog-Einträge, nicht nebenbei beginnen
- Markierungen als PDF-`/Redact`-Annotationen in der Datei speichern und später fortsetzen (M14: Markierungen leben nur in der Sitzung wie der Plan aus M13).
- Schwärzen in Bildern erkennen (OCR/Gesichter), automatisches Erkennen von Namen oder Adressen, Schwärzen mehrerer Dateien als Stapel in der App.
- Bearbeiten von Text (Teilprojekt 4), Zertifikatssignaturen, Entfernen von Wasserzeichen.
- Eine Sicherungskopie des Originals anlegen (stilles Schreiben); „Speichern unter …“ ist der Weg.

**Anti-Ziele (Fehlschlag, auch wenn alles „funktioniert“)**
- Etwas Geschwärztes steht nach „Anwenden“ noch in der Datei, und Pagewing hat es **nicht gemerkt**. (Ein Fehler, den die Prüfung findet, ist kein Anti-Ziel; ein Fehler, den sie übersieht, schon. Deshalb prüft sie nach zwei unabhängigen Wegen, Abschnitt 5.5, und ihre Grenzen stehen im Bericht.)
- Pagewing **rät**: ein Zeichen, Objekt oder Bild zerlegen, obwohl es nicht sicher geht.
- Die Datei wird still verändert: Original angefasst, Signatur ohne Warnung ungültig gemacht, verschlüsselte Datei unverschlüsselt gespeichert, Bereinigung abschaltbar vergessen.
- Wesentlich mehr verschwindet als markiert (Überschwärzen ohne Hinweis), etwa Hintergrundflächen der ganzen Seite oder Text außerhalb der Markierung.
- Im Startpfad entsteht Arbeit; der `PdfiumThread` ist lange blockiert (Rendern und Scrollen bleiben flüssig, wie in M13).
- Ein UI-Text nennt etwas „sicher“ im Sinn einer Rechtsgarantie, „unwiderruflich geprüft“ ohne Angabe der Grenzen, oder eine Signatur „gültig“.

## 4. Rahmen aus dem Projekt (gilt ohne neue Entscheidung)

- PDFium nur über den `PdfiumThread`, Aufträge kurz in Teilschritten (Seite für Seite, `PdfiumPriority.Background`), Abbruch zwischen den Schritten; Speichern bleibt ein Auftrag. Native AOT, Bindings nur für Gebrauchtes, Konstanten gegen die Header prüfen.
- **Vollständiger Neuschrieb, nie inkrementell.** Die Prüfkette ist `DocumentSaver.RewriteAsync` (M13): temporäre Datei im Zielordner, Neuöffnen mit Passwort, Prüfrendern, atomares Ersetzen, Quelle unverändert Handle gegen Handle, nie ein Überschreiben einer außerhalb geänderten Originaldatei, `ReplaceExisting = false` bei Ausgabe ohne `--force`. Signierte Dokumente: Warnkarte vor dem Anwenden, jede Stufe.
- Arbeitskopie aus der Datei am Pfad (M13), nie das angezeigte Dokument rendern oder verändern; Fingerabdrücke nie vom veränderten Dokument (Abschnitt 5.5 sagt, was an die Stelle tritt).
- Kürzel nur im `CommandCatalog`; Modus statt Dauerwerkzeug, die Leseansicht bleibt frei von Bearbeitungswerkzeugen; Tab fängt den Fokus nie; Zustände nie nur über Farbe; UI-Texte Englisch und Deutsch.
- Nichts im Startpfad, Messläufe öffnen nie einen Modus; Startzeit-Budgets werden nicht gelockert.
- Tests schreiben nur auf Kopien im Temp-Ordner, nie neben `testdata`, nie in den echten Benutzerordner.

## 5. Entwurf

### 5.1 Modell: Markierungen sind ein Plan in der Sitzung **[Entscheidung: markieren, dann anwenden; Rest Mein Vorschlag]**

Wie in M13: Nichts wird sofort geschrieben. Der Modus „Schwärzen“ sammelt **Markierungen**, „Anwenden“ schreibt. Core (UI-frei, neuer Ordner `Pagewing.Core.Redaction`; Namen **[Baumeister]**):

- Eine **Markierung** gehört zu einer Seite und besteht aus einem oder mehreren Rechtecken in Seitenpunkten (Ursprung oben links, wie überall im Projekt; die Umrechnung bleibt `FPDF_PageToDevice`/`DisplayTransform`). Es gibt drei Arten: **Bereich** (gezogenes Rechteck), **Text** (Auswahl; die Rechtecke sind die Zeichenrahmen je Zeile, der Text wird als Begriff für die Prüfung gemerkt) und **Treffer** (Ergebnis von Suchen und Schwärzen, wie Text). Alles reduziert sich beim Anwenden auf Rechtecke je Seite; die Begriffe dienen der Prüfung (5.5).
- Der **Plan** hält die Markierungen mit einem Verlauf (Rückgängig/Wiederholen, wie `PageEditHistory`; Obergrenzen gegen feindliche Eingaben, **[Baumeister]**, etwa Markierungen je Dokument und Rechtecke je Markierung, mit Test).
- **Zeichen zählen als betroffen, sobald ihr Rahmen das Rechteck schneidet** (nicht erst, wenn der Mittelpunkt drin liegt). Das ist absichtlich streng: lieber ein halbes Zeichen zu viel als eines zu wenig.
- **Füllfläche [Mein Vorschlag]:** Beim Anwenden wird je Markierungsrechteck eine deckende Fläche in der Füllfarbe in den Seiteninhalt geschrieben, **genau das Rechteck der Markierung** (bei Text: die Rechtecke der Zeichenrahmen je Zeile, so wie die Markierung sie zeigt, nicht größer). Die Vorschau zeigt dieselben Flächen.
- **Was die Prüfung als „gemerkt“ führt [Mein Vorschlag]:** Beim Markieren oder spätestens beim Anwenden liest Pagewing den Text, der tatsächlich unter jedem Rechteck steht (Zeichen, deren Rahmen es schneiden), und merkt ihn als **entfernten Text** der Markierung; bei *Text* und *Treffer* ist das der gewählte Begriff, bei *Bereich* der abgeleitete Text. Die Prüfung ist **zählend**, nie „kommt irgendwo vor“: Für jeden entfernten Text darf die Zahl seiner Vorkommen im Ergebnis nicht größer sein als die Zahl vorher minus die der entfernten Stellen. Ein gleicher, nicht markierter Text an anderer Stelle (derselbe Name auf Seite 5) lässt das Anwenden also nicht scheitern. Nur wenn der Nutzer in *Suchen und Schwärzen* „alle Vorkommen“ gewählt hat (und in der CLI mit `--text` ohne `--pages`), gilt der Begriff dokumentweit: dann darf er nirgends mehr stehen, auch nicht in Annotationen, Formularwerten oder unsichtbarem Text. Entfernter Text unter einer Mindestlänge (**[Baumeister]**, etwa vier Zeichen) ist für den Rohbytes-Weg zu kurz (zufällige Treffer in Streams); der Bericht sagt „zu kurz für die Rohbytes-Prüfung, nur Textweg und Lage geprüft“.

### 5.2 Was „entfernen“ je Inhaltsart heißt **[Entscheidung: Regeln der Gesamt-Spec; Umsetzung Mein Vorschlag, Spike-abhängig]**

Gemeinsamer Grundsatz: **Im Zweifel ganz entfernen und es vorher sagen; nie raten.** „Berührt“ heißt, die Grenzen des Objekts (`FPDFPageObj_GetBounds`, bei Text die Zeichenrahmen) schneiden ein Markierungsrechteck. Clip-Pfade und Sichtbarkeit zählen nicht: auch Verdecktes und Unsichtbares wird entfernt, wenn es den Bereich berührt.

| Inhalt | Regel | Rückfall (ganz entfernen, in der Liste) |
|---|---|---|
| **Text** (auch unsichtbar, Render-Modus 3, weiße Schrift, OCR-Schicht) | Berührte Zeichen entfallen; die übrigen Zeichen bleiben an ihrer Stelle als neue Textobjekte mit demselben Schriftobjekt, Größe, Matrix, Modus, Farbe | Ein Zeichen ohne verlässliche Unicode-Zuordnung (`FPDFText_SetText` braucht Unicode, Zeichencodes lassen sich nur setzen, nicht lesen) → das ganze Textobjekt entfällt |
| **Bild** | Pixel im Bereich werden überschrieben (Farbe der Füllung), das Bild wird verlustfrei neu kodiert; Maske (`/SMask`) und Alpha bleiben richtig (nicht `FPDFImageObj_GetBitmap`, siehe M12) | Gedrehtes oder geschrägtes Bild, Bild mit nicht unterstützter Form → das ganze Bild entfällt; Inline-Bild je Spike |
| **Pfad** | Achsenparalleles Rechteck → in Streifen außerhalb des Bereichs zerlegt | Alles andere (Kurven, Dreiecke, gedrehte Pfade, Muster) → ganz entfernt. Schutz gegen Überschwärzen: ein Pfad, der **mehr als die Hälfte der Seitenfläche** bedeckt (Hintergrund), wird nie ganz entfernt, sondern wie ein Rechteck zugeschnitten oder, geht das nicht, in der Liste ausdrücklich genannt **[Mein Vorschlag, Zahl Baumeister]** |
| **Form-XObject** | Rekursiv wie die Seite. **Geteilte XObjects** (von mehreren Seiten benutzt) dürfen eine andere Seite nicht verändern: Spike klärt, ob PDFium einen eigenen Stream schreibt | Lässt sich das nicht sicher, wird das XObject auf dieser Seite ganz entfernt |
| **Annotation** | Im Bereich → gelöscht (mit `/Popup`, Antworten); Annotationen mit Treffer im Text (Kommentar, Formularwert) bei Suchen und Schwärzen | – |
| **Schattierungen, Tiling-Muster, Type-3-Zeichenprozeduren, Soft Masks** | Nicht als Seitenobjekte sichtbar; Behandlung je Spike | Nicht entfernbar, aber den Bereich berührend → Anwenden wird **abgelehnt** mit Grund, es sei denn, die Prüfung (5.5) findet nichts in diesen Streams |

Ob ein Textobjekt zerlegbar ist, erfährt die Analyse **durch einen Trockenlauf der Zerlegung** auf der Arbeitskopie (nichts wird gespeichert, die Kopie wird verworfen): Sie liefert je Objekt die Entscheidung „zerlegt“ oder „ganz entfernt, weil …“. Welche Merkmale den Rückfall auslösen (fehlendes `ToUnicode`, Abweichung beim Zurücklesen, Prüfrendern außerhalb des Bereichs), legt Spike 2 fest **[Spike, Baumeister]**.

Die **Analyse** läuft vor dem Bauen (wie `AnalyzeAsync` in M13) und liefert: je Seite, was berührt wird (Zeichen, Bilder, Pfade, Form-XObjects, Annotationen), die Rückfälle („3 Textobjekte werden ganz entfernt, weil die Schrift …“), und ob der Plan abgelehnt wird. Dieselbe Analyse speist die Vorschau (5.6).

### 5.3 Rückfallleiter **[Entscheidung: nie raten; Stufen Mein Vorschlag]**

1. Zeichen-/Pixelgenau (Normalfall).
2. Objekt ganz entfernen (Rückfall in der Tabelle), immer in der Analyse genannt.
3. Anwenden ablehnen (nicht entfernbarer berührter Inhalt, Prüfung schlägt an).
4. **M14.1:** „Seiten in Bild umwandeln“ nur auf ausdrückliche Wahl je Anwenden; der gesamte Text dieser Seiten geht verloren, die Karte sagt es.

### 5.4 Bereinigen: immer an **[Entscheidung: Bereinigen ist kein Schalter]**

Beim Anwenden wird die Datei von allem befreit, was Inhalt tragen kann, ohne im Bereich zu liegen: Info-Dictionary, XMP (auch seitenweise), Lesezeichen (Titel können den Begriff enthalten), eingebettete Dateien und Dateianhänge, JavaScript und Aktionen, Seitenbeschriftungen, Vorschaubilder (`/Thumb`), Formularwerte außerhalb des Bereichs bleiben (sie sind sichtbarer Inhalt), versteckte Ebenen (`/OC`) nach Spike, ältere Revisionen und verwaiste Objekte (vollständiger Neuschrieb). Die Anwenden-Karte **nennt, was vorhanden war und entfernt wird** („Autor: …“, „3 Lesezeichen“, „1 Anhang“), damit der Nutzer weiß, was bei seiner Datei zählte. Wer Lesezeichen oder Formulare behalten will, schwärzt nicht: das ist ein Zielkonflikt, den die Karte nicht auflöst (Backlog: wählbare Behaltenliste). **Verlustliste des Neuaufbaus [Entscheidung, 2026-11-01; QPDF nur über das Gate nach dem Spike]:** Lesezeichen, Seitenbeschriftungen, Anhänge und Skripte entfallen; interne Links (GoTo) verlieren ihr Ziel; Formularfelder bleiben als Widgets sichtbar, sind danach aber **nicht mehr zuverlässig ausfüllbar** (M13-Befund: kein Eintrag in `/Fields`); Annotationen außerhalb der Markierungen kommen mit (Befund aus M13). Die Anwenden-Karte nennt jeden dieser Punkte mit der Zahl in dieser Datei. Der frühere Satz „Formularwerte bleiben“ meint nur: Ihr **angezeigter** Wert bleibt sichtbar. **Verschlüsselte Dateien werden in M14 abgelehnt** (mit Grund): Ein neues Dokument ist unverschlüsselt, PDFium kann nicht verschlüsselt schreiben, und still entschlüsseln ist ein Anti-Ziel; „entschlüsselt schwärzen auf ausdrücklichen Wunsch“ ist Backlog. Der Test dazu prüft die Ablehnung mit der verschlüsselten Datei.

Wie das mit PDFium geht **[Spike, Mein Vorschlag als Hypothese]**: PDFium kann Info-Dictionary und XMP nicht schreiben. Die Seiten werden mit `FPDF_ImportPagesByIndex` in ein **neues** Dokument kopiert (kein altes Info-Dictionary, kein XMP im Katalog, keine Outlines, keine Names/EmbeddedFiles/JavaScript), das neu geschrieben und geprüft wird. Das Gerüst (Import, `KeepOpenUntilClosed` für Widget-Seiten) gibt es seit M13. Der Spike prüft, was dabei an Seitenebene wirklich mitkommt (`/Metadata`, `/PieceInfo`, `/Thumb`, `/AA`, Annotationsinhalte). **Entscheidungsregel QPDF:** QPDF (Apache-2.0, C-API) wird ein eigener `DECISIONS`-Eintrag und Teil von M14 nur, wenn nach dem Neuaufbau über Import Metadaten oder anderer bereinigbarer Inhalt in den Rohbytes bleiben, den PDFium nicht entfernen kann, oder wenn der Import Formularfelder oder Annotationen außerhalb des Bereichs so verändert, dass die Verlustliste es nicht mehr ehrlich benennen kann. Sonst bleibt es bei PDFium, wie in M13.

### 5.5 Ausführen und Prüfen **[Entscheidung: Pagewing prüft selbst; Aufbau Mein Vorschlag]**

Ablauf (Reihenfolge **[Baumeister]**, Invarianten nicht):
1. Arbeitskopie aus der Datei am Pfad (Stempel gegen das angezeigte Dokument wie in M13; Abbruch mit Karte bei Änderung von außen; offene Entwürfe übernehmen; ungespeicherte Annotationen → Karte „Zuerst speichern“, wie `Anwenden` in M13).
2. Analyse (5.2). Abgelehnt → Karte mit Grund, nichts geschrieben.
3. Anwenden-Karte: Zusammenfassung, Rückfallliste, Bereinigungsliste, Signaturwarnung; „Speichern unter …“ ist Standard und hat den Fokus (Vorschlag für den Namen: `<Name>-redacted.pdf`), „In der Originaldatei speichern“ gefährlich gekennzeichnet, nie Standard, 400 ms gesperrt (wie M13).
4. Entfernen auf der Arbeitskopie, Neuaufbau über Import (5.4), vollständig neu schreiben.
5. **Prüfung** (`RedactionVerifier`, Core): das **geschriebene** Ergebnis wird neu geöffnet und auf zwei unabhängigen Wegen gelesen.
   - *Text:* Textextraktion jeder Seite einschließlich unsichtbaren Texts; kein gemerkter Begriff darf vorkommen, kein Zeichen darf in einem Markierungsrechteck liegen.
   - *Rohbytes:* alle Streams werden entpackt (Flate über `ZLibStream`, Obergrenzen gegen Dekompressionsbomben, **[Baumeister]**; Filter, die Pagewing nicht entpacken kann, stehen im Bericht) und mit den Begriffen verglichen, in ASCII, UTF-16BE und PDFDocEncoding; zusätzlich die unverarbeiteten Dateibytes für Metadaten. **Grenze, die im Bericht steht:** Bei Schriften mit eigener Zeichenzuordnung steht der Text nicht lesbar im Stream; dort trägt der erste Weg, der zweite fängt Metadaten, Kommentare, Lesezeichen, Anhänge, Muster, alte Revisionen.
   - *Bild:* für überschriebene Bildbereiche liest die Prüfung das Bild zurück und prüft die Pixel im Bereich auf die Füllfarbe (stichprobenfrei, wenn das Bild klein genug ist, sonst Raster **[Baumeister]**).
   - *Außerhalb unverändert:* Text außerhalb der Markierungen bleibt gleich (Zeichenmenge je Seite = vorher minus Entferntes); der Fingerabdruck aus M13 wird für den Neuschrieb-Modus **angepasst**, weil sich der Text absichtlich ändert: erwartet wird der Text der Quelle minus die Zeichen in den Rechtecken.
   - *Verschlüsselte Dateien:* werden vor dem Anwenden abgelehnt (5.4), die Prüfung kennt nur unverschlüsselte Ergebnisse.
6. Findet die Prüfung etwas, wird **nichts ersetzt**: kein Ausgabefile, das Original unverändert, die temporäre Datei weg, die Karte nennt Art und Seite des Fundes (und für Begriffe die Stelle, nicht mehr als nötig im Protokoll), der Plan bleibt.
7. Besteht sie, ersetzt die Prüfkette atomar, das geprüfte Dokument wird das des Tabs **am Zielpfad** wie nach dem Anwenden in M13 (`AdoptRewrittenDocumentAsync`, neue Cache-Instanz, `ExpectRewrittenDocument`; bei „Speichern unter“ zeigt der Tab danach die geschwärzte Datei unter dem neuen Pfad, das Original bleibt unverändert auf der Platte und ist nicht mehr offen **[Mein Vorschlag, wie M13]**), der Modus endet, der **Prüfbericht** erscheint (was entfernt wurde, was bereinigt wurde, was geprüft wurde und was nicht geprüft werden konnte). Der Nutzer sieht das Ergebnis sofort im Tab.

Der Bericht verspricht nie mehr, als die Prüfung leistet: „Geprüft: Text, Rohbytes (außer: verschlüsselt/Filter X), Pixel.“ Ob ein anderes Programm die Datei anders liest, bleibt eine Handprüfung (Abschnitt 8).

### 5.6 Oberfläche **[Mein Vorschlag; Feinheiten Baumeister nach Design-Spec]**

- **Modus „Schwärzen“** (Aktionsleiste und Kürzel im `CommandCatalog`; Kürzel **[Baumeister]**, ohne Kollision): sperrt Kommentieren, Ausfüllen, Organisieren und umgekehrt, mit sichtbarem Grund. Werkzeugleiste (entsteht erst mit dem Modus): *Bereich*, *Text markieren*, *Suchen und Schwärzen …*, Rückgängig/Wiederholen, *Vorschau*, *Anwenden*, *Verwerfen*.
- **Markierungen** erscheinen auf der Seite als rote Fläche mit Schraffur und Rahmen (nie nur Farbe; Kontrast 3:1 gegen helle **und** dunkle Seite), mit Zähler „4 Markierungen auf 2 Seiten“. Ziehen, Verschieben, Größe ändern, Löschen per Griffe und Tastatur, nach dem Muster von Auswahl und Griffen aus M10; Tab fängt den Fokus nie.
- **Panel „Schwärzen“** (entsteht beim ersten Gebrauch): Liste der Markierungen nach Seite (Art, Begriff, Seite), Klick springt hin, Entf löscht; Suchfeld für *Suchen und Schwärzen* mit „Beachtung der Schreibung“ und Trefferliste mit Haken je Treffer; Treffer aus Annotationen und Formularwerten stehen mit eigener Zeile (Art „Kommentar“, „Formularfeld“); **ein Treffer in unsichtbarem Text** ist als solcher gekennzeichnet (Text, der ohne Hilfsmittel nicht zu sehen ist, wird sonst übersehen).
- **Vorschau:** zeigt die Seiten, wie sie nach dem Anwenden aussähen (schwarze Flächen deckend), und die Zahlen aus der Analyse („entfernt: 38 Zeichen, 1 Bild, 2 Annotationen; 1 Textobjekt ganz“). Die Vorschau verändert nichts und rendert nie die Arbeitskopie.
- **Dunkle Seite und Druck:** Markierungen zeichnen sich auf beiden; gedruckt wird nie mit Markierungen, die noch nicht angewandt sind (es ist ein Plan, keine Annotation).
- **Schließen/Update:** Ein Plan mit Markierungen zählt als ungespeicherte Änderung (Rückfrage „Markierungen verwerfen?“, Update-Weg `ResolveUnsavedChangesAsync`).
- **Füllfarbe:** M14 schwarz (weiß als zweite Wahl **[Mein Vorschlag]**); weitere Füllungen und Text M14.1.

### 5.7 CLI **[Mein Vorschlag]**

Gemeinsamer Kern mit der App (`Pagewing.Core.Redaction`), Ausgaben englisch und stabil, Exit-Codes `0/1/2`, Passwort aus `PAGEWING_PASSWORD` oder `--password`, `--force` überschreibt nur Ausgaben, nie eine Eingabe (Dateiidentität wie `OutputGuard`).

```
pagewing redact <file> --output <out.pdf>
    [--text <term>]...           alle Vorkommen (Beachtung der Schreibung mit --match-case)
    [--rect <page>:<x>,<y>,<w>,<h>]...   Seitenpunkte, Ursprung oben links
    [--pages <range>]             beschränkt --text auf diese Seiten
    [--dry-run]                   nur Analyse und Treffer, nichts schreiben
    [--force] [--password <pw>]
```

Ausgabe auf stderr: Zusammenfassung, Rückfälle, Bereinigtes, Prüfbericht. Schlägt die Prüfung an, Exit 1 und **keine** Ausgabedatei. `--dry-run` listet die Treffer je Seite, ohne den Text zu wiederholen, wenn `--text` mehr als einen Treffer ergibt (Zahl und Seite). Ein `--text`, der keinen Treffer ergibt, ist ein Fehler (Exit 1, „no match“), kein stilles Ergebnis. Signierte Dokumente: Hinweis auf stderr, die Signatur wird ungültig; `--force` ist dafür nicht nötig, die Meldung ist Teil der Ausgabe. `--help`, Beispiele und `README.md` ergänzen. Das Muster-Flag `--pattern` kommt mit M14.1.

## 6. Spike am Anfang des Plans **[Entscheidung: zuerst klären, dann den Umfang festlegen]**

Wegwerfbar, mit Testdateien, ohne Produktivcode; Ergebnis als Eintrag in `Docs/DECISIONS.md` samt Entscheidungsregeln. Die Fragen, in dieser Reihenfolge der Wichtigkeit:

1. **Form-XObject (Hauptrisiko):** Schreibt PDFium Änderungen an `FPDFFormObj_*` beim Speichern in den Stream des XObjects? Wird ein von zwei Seiten benutztes XObject in beiden verändert? *Regel:* Geht es nicht sicher, wird das XObject auf der berührten Seite ganz entfernt (Rückfall) und die Seite mit `FPDFPage_GenerateContent` neu geschrieben; ein XObject, das auf **anderen** Seiten benutzt wird, bleibt dort unverändert (Test).
2. **Textzerlegung:** Funktioniert die Zerlegung mit `FPDFText_SetText` für eingebettete Subset-Schriften (TrueType/CID mit `ToUnicode`, Type 1, Standardschriften)? Welche Textobjekte lässt sich nicht verlässlich neu erzeugen (Rückgabe, Abweichung des Rendersatzes)? *Regel:* Maßstab ist ein Prüfrendern außerhalb des Bereichs (pixelgleich oder innerhalb der Toleranz aus der Render-Regression); sonst Rückfall ganzes Objekt.
3. **Bild:** Überschreiben und Neukodieren für Flate, DCT (JPEG), Maske, `/SMask`, Indexed, CMYK, Inline-Bild; Verhalten des alten Streams nach dem Neuschrieb (verwaist, entfernt?). *Regel:* Nicht verlustfrei oder mit Alphaverlust zu machen → Rückfall ganzes Bild.
4. **Bereinigen durch Import (5.4):** Was bleibt in den Rohbytes von Info, XMP, Outlines, Anhängen, JavaScript, `/Thumb`, `/PieceInfo`, seitenweisem `/Metadata`, Annotationsinhalten, Formularwerten (inkl. Elternfelder)? Entscheidet über QPDF (Regel in 5.4).
5. **Vollständiger Neuschrieb** (`FPDF_NO_INCREMENTAL`) lässt keine alte Revision zurück (Rohbytes-Test an einer Datei mit älterer Revision).
6. **Nicht als Seitenobjekt sichtbarer Inhalt:** Tiling-Muster, Type-3, Soft Masks, Schattierungen: gibt es Text darin, den PDFium nicht herausgibt; findet ihn die Rohbytes-Prüfung? *Regel:* siehe Tabelle 5.2 (ablehnen, wenn nicht entfernbar und berührt).
7. **Pfad-Zuschnitt und Überschwärzen** bei Rechtecken, Hintergründen und Clip-Pfaden.

Das Ergebnis legt fest, ob M14 den ganzen Umfang aus Abschnitt 3 trägt; fällt etwas heraus (etwa Form-XObjects nur als Ganzes), steht es in der Verlustliste und im Backlog, nicht still im Code.

**Gate nach dem Spike [Mein Vorschlag]:** Der Plan hält nach dem Spike an und legt dem Projektinhaber das Ergebnis vor, bevor die Umsetzung beginnt. Pflicht zur Rückfrage ist es, wenn (a) die Textzerlegung für übliche Schriften (eingebettete TrueType-/CID-Subsets, Standardschriften) in mehr als etwa einem Viertel der Textobjekte der Testdateien auf „ganz entfernt“ zurückfällt (das Produkt wäre kaum brauchbar: ganze Zeilen verschwänden), (b) Form-XObjects nur ganz entfernt werden können, (c) das Bereinigen QPDF verlangt. Sonst läuft der Plan durch, die Ergebnisse stehen im `DECISIONS`-Eintrag.

## 7. Umfang und Reihenfolge **[Entscheidung: zwei Meilensteine]**

**M14:** Spike und Entscheidung; Bindings für Objekt-Lesen und -Entfernen, Textzerlegung, Bild-Überschreiben; Core `Pagewing.Core.Redaction` (Plan, Analyse, Ausführer, Verifier mit Bericht); Neuschrieb-Modus erhält die Redaktionsprüfung; Bereinigen; CLI `redact` (Text und Rechteck); App: Modus mit Bereich, Text markieren, Suchen und Schwärzen (nur Text), Panel, Vorschau, Anwenden-Karte mit Rückfällen, Bereinigungsliste, Signaturwarnung, Prüfbericht; Teststücke und Tests nach Abschnitt 8; Doku.

**M14.1:** Muster (E-Mail, IBAN, Telefon, Kreditkarte), Weichzeichnen, Verpixeln, Überlagerungstext, „Seite in Bild umwandeln“, CLI `--pattern`. Die Roadmap wird entsprechend angepasst (Scope-Änderung: M14 geteilt, M14.1 neu, Voraussetzung M14). Wird M14 größer als geplant, ist ein weiterer Schnitt möglich, der in der Roadmap festgehalten wird.

## 8. Tests und Abnahme

Abnahme laut Roadmap: **Nach dem Schwärzen enthält weder die Textextraktion noch der dekomprimierte Rohinhalt der Datei den geschwärzten Text (Form-XObject, Annotation, Metadaten, ältere inkrementelle Revision)**, und **Bildpixel im geschwärzten Bereich sind im gespeicherten Bild überschrieben.** Dazu:

- **Teststücke** (`tools/make-testpdfs.cs`, `testdata/pdfs/LICENSES.md`, `RenderCorpus.Skipped`): eine Datei mit dem Begriff an allen Stellen: im Seitentext (Standardschrift), in einer eingebetteten Subset-Schrift mit eigener Zuordnung, als unsichtbarer Text (OCR-Schicht über einem Bildscan), in einem Form-XObject, das von zwei Seiten benutzt wird, in einer Annotation und einer Antwort, in einem Formularwert, im Info-Dictionary und in XMP, in einem Lesezeichentitel, in einem Anhang, in einem Tiling-Muster; ein Bild mit bekannten Pixeln (Flate, JPEG, mit `/SMask`); eine Seite mit `/Rotate`; eine Datei, deren **ältere inkrementelle Revision** den Begriff trägt; eine verschlüsselte; eine signierte Attrappe (gibt es).
- **Core-Tests je Inhaltsart:** Begriff weg (beide Wege der Prüfung), Pixel im Bereich = Füllung, Pixel **außerhalb** unverändert, Text außerhalb unverändert (Zeichenmenge und Render-Vergleich außerhalb des Bereichs), Rückfälle genannt, geteiltes XObject verändert die andere Seite nicht, Original unverändert bei jedem Fehler, Abbruch, feindliche Pläne (Obergrenzen), verschlüsselt, signiert.
- **Der Prüfer selbst wird geprüft:** Eine absichtlich undichte Datei (nur schwarzes Rechteck über lebendem Text; ein Begriff, der in einem Anhang bleibt) muss **durchfallen**; das ist der wichtigste Test der Reihe.
- **Plan-Tests ohne PDFium:** Verlauf, Rechtecke, Zeichenregel „Rahmen schneidet“.
- **App-Tests (Headless):** Modus, Bereich ziehen, Text markieren, Suche, Panel, Vorschau, Anwenden-Karte mit Rückfallliste, Prüfbericht, Schließen mit Markierungen, Update-Weg, Sperre der Modi, Tastaturbedienung ohne Falle, Texte in beiden Sprachen, kein UI-Text nennt eine Signatur „gültig“, nichts „rechtsgültig“.
- **CLI-Tests** über `CliApp.RunAsync` (Exit-Codes, `--dry-run`, kein Treffer, nie überschreiben, Passwort, Prüfung schlägt an → keine Datei).
- **Startpfad** (`RedactionStartPathTests`): Plan, Modus, Panel, Karten, Verifier entstehen erst beim Gebrauch; Startzeit-Check bleibt in den Budgets. Messung des Anwendens (Seiten je Sekunde, Dauer der Prüfung) als Information, nie als geprüfte Zeit.
- **Handprüfung, offen nach `v1.0.0`:** Ergebnisdateien in Acrobat Reader und Chrome (sehen sie aus wie die Vorschau, kein Text unter den Flächen), Auswahl und Kopieren in diesen Programmen, Linux.
- Build, Test, Format, `roadmap check`, `licenses check`, `make-testpdfs.cs check`, AOT-Publish von App und CLI und `smoke-cli.cs`, jeweils mit Befehl und Ergebnis im Nachweis.

## 9. Doku, die M14 mitliefert

`Docs/DECISIONS.md` (Spike-Ergebnis und QPDF-Entscheidung, Modell, Regeln je Inhaltsart und Rückfälle, Bereinigen immer an, Prüfung auf zwei Wegen und ihre Grenzen, Anpassung des Fingerabdrucks im Neuschrieb-Modus, Obergrenzen), `CLAUDE.md` (Abschnitt „Schwärzen (seit M14)“ mit Wo-was-liegt und Stolperfallen), `CHANGELOG.md` `[Unreleased]`, CLI-Hilfe und README, `packaging/README.md` (Handprüfpunkte), Roadmap (Scope-Änderung, Plan verlinken, Aufgaben mit Nachweis), neue Backlog-Einträge für die Nicht-Ziele aus Abschnitt 3.

## 10. Was dem Baumeister überlassen bleibt

Namen und Zuschnitt der Typen, Reihenfolge der PDFium-Schritte, Stapelgrößen und Obergrenzen (mit Test), Kürzel, Gestaltung von Modus, Panel und Karten nach Design-Spec, Schwellen (Hintergrundfläche, Rasterung der Pixelprüfung, Entpack-Grenzen), welche Filter die Rohbytes-Prüfung entpackt, Aufbau des Prüfberichts, Dateiname-Vorschlag für „Speichern unter“, ob der Neuaufbau über Import für alle Dokumente oder nur für Dokumente mit Bereinigungsbedarf läuft (mit Begründung).
