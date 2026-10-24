# Teilprojekt 2 – Annotieren, Ausfüllen, Unterschreiben, Milestone 9 im Detail (Entwurf)

Stand 2026-10-24, erarbeitet im Brainstorming mit dem Projektinhaber. Grundlage sind Milestone 9 bis 12 in [Docs/ROADMAP.md](../../ROADMAP.md), die [Gesamt-Spec](2026-10-16-pagewing-design.md) (Abschnitte 5.5 und 6) und die [Design-Spec](2026-10-17-designsystem.md) (7.3, 7.7). Dieser Entwurf legt fest, **was** gebaut wird und **warum**; das **wie** im Einzelnen klärt der Plan (`Docs/superpowers/plans/`). Milestone 9 steht im Detail da, Milestone 10 bis 12 nur als Rahmen (Abschnitt „Rahmen für M10 bis M12“).

## Absicht

Wer ein PDF mit Pagewing markiert und kommentiert, soll der Datei danach **in jedem Programm** trauen können: Die Markierung steht überall an derselben Stelle in derselben Farbe, eine vorhandene Signatur wird nicht angetastet, und die Originaldatei ist nie halb überschrieben. Das ist der Teil, an dem kleine Viewer scheitern (Annotationen ohne Erscheinungsbild, falsch geordnete Eckpunkte, gedrehte Seiten, signierte Dateien). Es ist auch die Grundlage für alles, was Teilprojekt 2 danach speichert (Zeichnungen, Formulare, Unterschrift).

Das wichtigste Merkmal von Pagewing bleibt die Geschwindigkeit. Annotieren darf Start, Öffnen und Scrollen nicht verlangsamen, und eine Änderung muss sofort auf der Seite stehen.

## Ziele (Milestone 9)

1. **Speicher-Pipeline:** Speichern (Strg+S), Speichern unter (Strg+Umschalt+S) und der Hinweis auf ungespeicherte Änderungen. Inkrementell, atomar, mit Prüfung vor dem Ersetzen.
2. **Textmarkierungen:** Hervorheben, Unterstreichen, Durchstreichen auf markiertem Text, mit Farbe.
3. **Kommentare:** Ein Kommentartext hängt an einer Textmarkierung. Die **Seitenleiste „Kommentare“** listet alle Annotationen des Dokuments (auch aus anderen Programmen), springt zur Stelle und zeigt den Kommentar.
4. **Löschen und Rückgängig/Wiederholen** für Markierungen und Kommentare (aus M10 vorgezogen, siehe „Scope-Änderung“).
5. **Überall sichtbar:** Eine gespeicherte Markierung zeigt sich in Chrome, Acrobat und Pagewing gleich. Jede Speicherung wird nach dem Schreiben neu geöffnet und geprüft.
6. **Signaturen:** Pagewing hängt Änderungen nur an. Die Bytes der Originaldatei bleiben nachweislich unverändert (die Prüfung im Speichervorgang). Pagewing liest die Rechte der Signatur (DocMDP) und warnt vor dem Speichern, wenn Annotationen die Signatur ungültig machen würden.
7. **Update-Weg:** Pagewing installiert kein Update und beendet sich nicht, ohne dass der Nutzer ungespeicherte Änderungen sichern oder verwerfen konnte (Voraussetzung aus der M8.1-Spec).

## Nicht-Ziele und Gegenziele

- Keine Zeichnungen, Formen, Textfelder, Haftnotizen, Stempel (M10, M12), keine Formulare (M11), kein Schwärzen (M14). Keine Wellenlinie („Squiggly“).
- Keine Antwortketten auf Kommentare, keine Rollen, keine gemeinsame Bearbeitung, kein Export nach XFDF oder FDF.
- Kein Bearbeiten von Annotationen, die Pagewing nicht versteht (Freihand, Textfeld, Stempel, Dateianhang, Formularfelder, Links): Sie erscheinen in der Liste, bleiben beim Speichern aber unangetastet.
- **Kein stilles Speichern in die Originaldatei** (kein Auto-Speichern). Eine Wiederherstellungsdatei nach Absturz gibt es in M9 nicht (Backlog); ungespeicherte Änderungen gehen bei einem Absturz verloren.
- Keine Prüfung oder Anzeige, ob eine Signatur „gültig“ ist (Zertifikate, Out of Scope). Pagewing sagt nur, was es mit den Bytes tut und was die Rechte der Signatur erlauben.
- Keine Werkzeuge in der Leseansicht (Design-Spec 7.7).
- Gegenziel (wäre ein Fehlschlag, auch wenn es technisch läuft): eine gespeicherte Markierung, die in einem gängigen Viewer fehlt oder verrutscht ist; ein verändertes Original nach einem Abbruch; eine langsamere Startzeit oder ein spürbar zähes Scrollen mit Annotationen; ein Verlust von Änderungen beim Schließen oder Aktualisieren, den der Nutzer nicht bestätigt hat.

## Festgelegt (vom Projektinhaber)

1. **Zuschnitt von M9** laut Brainstorming vom 2026-10-24: Speicher-Pipeline, die drei Markierungsarten, Kommentar-Seitenleiste, Löschen und Rückgängig, Prüfung nach dem Speichern, DocMDP lesen und warnen. Die Abnahme „Signatur bleibt gültig“ wird umformuliert (siehe „Abnahme“).
2. **Backlog statt M9:** Zusammenfassung der Kommentare als Text kopieren, Wiederherstellungsdatei bei Absturz, Antwortketten, Filter nach Autor oder Farbe, Bearbeiten fremder Annotationen.
3. **Autorname** ist eine Einstellung, Standard leer (kein Benutzername des Systems in einer weitergegebenen Datei).
4. **Reihenfolge:** erst Speicher-Pipeline und Fremdviewer-Prüfung, dann die Werkzeuge.

## Meine Entscheidungen (Vorschläge, im Plan zu bestätigen)

**Bedienung (richtet sich nach der Design-Spec, 7.3 und 7.7)**

- **Panel „Kommentare“** ist ein neuer Eintrag der Aktionsleiste (Strg+Umschalt+3, Tooltip mit Kürzel, Umschaltfläche für Screenreader wie die übrigen). Es entsteht wie jedes Panel erst beim Öffnen, und die Annotationen des Dokuments werden erst dann gelesen (Hintergrundpriorität). Die Liste ist virtualisiert, nach Seite und Position sortiert, mit Filter nach Art (Alle, Hervorhebung, Unterstreichung, Durchstreichung, Sonstige). Jeder Eintrag zeigt Seitenmarke in `type.mono`, Art als **Symbol und Text**, Autor, Datum, markierten Textausschnitt und Kommentar. Ein Klick oder Enter springt zur Stelle (Regeln der Suchtreffer, 7.4). Ohne Annotationen steht ein Hinweis im Panel.
- **Modus „Kommentieren“** (Design-Spec 7.7): Er startet über den Knopf „Kommentieren“ im Kopf des Panels, den Befehl „Kommentieren“ (Palette, Kürzel im `CommandCatalog`) und über die Befehle „Hervorheben“, „Unterstreichen“, „Durchstreichen“ bei vorhandener Textauswahl (sie starten den Modus und wenden die Art sofort an). Die Werkzeugleiste über dem Dokument hat: Hervorheben, Unterstreichen, Durchstreichen, Farbe, Rückgängig, Wiederholen, **Fertig**; Esc und „Fertig“ beenden den Modus. Mit gewählter Art gilt: **Markieren mit der Maus wendet sie beim Loslassen an** (wie bei Acrobat), die Auswahl wird dabei zur Annotation. Die Befehle erscheinen in der Palette nur, wenn sie ausführbar sind (nichts ausgegraut).
- **Bearbeiten im Panel** gibt es nur im Modus: Kommentar bearbeiten (mehrzeilig, Strg+Enter übernimmt, Esc verwirft), Farbe ändern, Löschen. Außerhalb des Modus ist das Panel rein lesend.
- **Welche Annotationen bearbeitbar sind:** Hervorhebung, Unterstreichung und Durchstreichung, **unabhängig davon, in welchem Programm sie entstanden** (sonst wäre eine eigene, schon gespeicherte Markierung nicht mehr löschbar). Alle anderen Arten: nur Anzeige.
- **Keine schwebende Leiste an der Markierung in der Leseansicht:** Sie würde Design-Spec 7.7 verletzen. Entschieden im Abschnitt „Entschieden nach der Gegenlese“.
- **Ungespeichert** zeigen der Tab (Punkt) und die Statusleiste („Nicht gespeichert“), wie in 7.7 und 7.10 vorgesehen. Beim Schließen eines Tabs oder Fensters mit Änderungen erscheint eine **Karte im Dokumentbereich** (kein modaler Dialog, wie bei der Passwortkarte): „Speichern“, „Nicht speichern“, „Abbrechen“. Der Fokus liegt auf „Speichern“. Mehrere Tabs fragt Pagewing nacheinander.
- **Farben:** Eine kleine feste Palette (Gelb, Grün, Blau, Rosa, Orange). Jede Farbe hat einen Namen für Tooltip und Screenreader; Zustände stehen nie nur in Farbe. Die zuletzt gewählte Farbe je Art bleibt erhalten (`AppSettings`, nach der Regel „nur mit `with` ändern“). Standardfarbe Gelb. Die Markierungsfarben sind PDF-Inhalt und gehören nicht ins Designsystem; die Auswahlknöpfe müssen aber den Kontrast von Rändern und Fokus erfüllen (3:1).
- **Autor und Datum:** Der Autor steht im Feld `/T`, nur wenn die Einstellung nicht leer ist; `/M` und `/CreationDate` setzt Pagewing.

**Speichern**

- **Ablauf:** Der Speichervorgang schreibt über `FPDF_SaveAsCopy` mit dem Flag für inkrementelles Speichern in eine temporäre Datei **im Zielordner** (wie `SaveDecryptedCopyAsync`), dann folgen die Prüfungen, dann ersetzt Pagewing das Ziel. Schlägt eine Prüfung fehl, bleibt das Original unverändert, die temporäre Datei wird gelöscht, und der Nutzer sieht eine Fehlerkarte mit Grund.
- **Prüfungen vor dem Ersetzen:**
  1. Die ersten *n* Bytes der neuen Datei sind **byteweise gleich** der Originaldatei (*n* = Länge des Originals). Das ist der Beweis, dass signierte Bereiche unberührt sind.
  2. PDFium öffnet die temporäre Datei wieder; Seitenzahl und Zahl der Annotationen stimmen mit dem erwarteten Stand überein.
  3. Jede geänderte Seite lässt sich rendern (ein Bild kommt zurück; ein Vergleich mit dem Bild der Sitzung ist dem Builder überlassen, soweit er stabil ist).
- **Nach dem Ersetzen** öffnet der Tab die gespeicherte Datei neu und behält Zoom, Position, Layout, Suche und Auswahl; der Bildspeicher (`RenderCache`) bleibt gültig, weil das Bild unverändert ist (es gibt kein Neuzeichnen allein wegen des Speicherns). Das Rückgängig-Protokoll beginnt danach leer (meine Setzung; der Plan prüft, ob PDFium nach einem inkrementellen Speichern im selben Dokument weiterarbeiten kann, dann entfällt das Neuöffnen).
- **Speichern unter** schreibt dieselbe Pipeline an einen neuen Pfad (Original plus angehängte Änderungen); der Tab gehört danach zur neuen Datei. Das Zielverzeichnis muss beschreibbar sein; sonst bietet Pagewing „Speichern unter“ an.
- **Datei seit dem Öffnen verändert** (Länge oder Zeitstempel weichen ab, ein anderes Programm hat sie ersetzt): Vor dem Ersetzen fragt eine Karte „Überschreiben“, „Speichern unter“, „Abbrechen“. Standard ist „Speichern unter“.
- **Rechte des Dokuments:** Erlaubt das PDF laut Berechtigungsbits keine Annotationen (Kommentieren-Bit), bietet Pagewing den Modus nicht an und nennt den Grund im Panel. Das Bit ist gegen die PDF-Spezifikation zu prüfen.
- **Verschlüsselte Dateien:** Das Speichern erhält die Verschlüsselung. Ob PDFium das inkrementell sauber kann, belegt ein Test mit dem verschlüsselten Test-PDF; kann es das nicht, lehnt Pagewing das Speichern mit klarer Meldung ab und schreibt nie unverschlüsselt über das Original.
- **Linearisierte Dateien** verlieren durch das Anhängen die „schnelle Web-Ansicht“. Das ist in Ordnung und kommt in `DECISIONS`.
- **Bekannte Grenze, Eintrag in `DECISIONS` und kurzer Hinweis in der Hilfe:** Inkrementelles Speichern behält die **alte Fassung** in der Datei. Ein gelöschter Kommentar steht damit weiter in den Rohbytes früherer Revisionen. Wer das nicht will, braucht später einen vollständigen Neuschrieb (Backlog „Bereinigen beim Speichern“, gehört zum Schwärzen, M14). Pagewing sagt nicht „gelöscht“, ohne diese Grenze zu kennen: Der Löschen-Knopf entfernt die Annotation aus dem Dokument, der Text der Hilfe nennt die Grenze.

**Erscheinungsbild der Annotationen (das Kernrisiko)**

- Jede Annotation, die Pagewing anlegt oder ändert, bekommt ein **eigenes Erscheinungsbild (`/AP`) als Form-XObject** (PDFium hatte lange den Fehler, es nur ins Dictionary zu schreiben; Adobe zeigte solche Annotationen nicht). Die `QuadPoints` stehen in der Reihenfolge, die Acrobat liest (dritter und vierter Punkt gegenüber der Spec vertauscht). Hervorhebungen werden mit **Multiplizieren** über den Text gelegt (Mischmodus im Erscheinungsbild), damit der Text lesbar bleibt.
- **Geometrie:** Pagewing nimmt die Zeichenrahmen der Auswahl (Seitenpunkte, Ursprung oben links) und rechnet sie mit `FPDF_PageToDevice` bzw. dessen Umkehrung auf PDF-Koordinaten (die einzige Umrechnung, siehe `CLAUDE.md`). Je Zeile entsteht ein Viereck. Rotation und CropBox (auch mit Versatz) müssen stimmen; die Zeilenhöhe nimmt der Builder aus den „losen“ oder den „engen“ Zeichenrahmen, je nachdem, was in Chrome und Acrobat sauberer aussieht.
- **Erster Schritt des Plans ist ein Spike** (Wegwerf-Code, kein Merge): Reicht PDFium allein (`FPDFPage_CreateAnnot`, Eckpunkte anhängen, Farbe, `FPDFAnnot_SetAP`), um in Chrome und Acrobat korrekte Markierungen zu bekommen, auch mit Multiplizieren und auf gedrehten Seiten? Falls nicht, ist die Alternative ein **eigener Schreiber für den angehängten Teil** (neue Objekte, Querverweis, Trailer mit `/Prev`) in `Pagewing.Core`. Das Ergebnis des Spikes steht als Eintrag in `Docs/DECISIONS.md`. Es entscheidet auch über die Frage der Spec von M10 (Linie und Pfeil): Ein eigener Schreiber könnte `/Line`-Annotationen ohne QPDF anlegen.
- **Zu prüfen, nicht anzunehmen:** welche Annotationsarten und Funktionen die ausgelieferte PDFium-Version bietet (Header, `FPDFAnnot_IsSupportedSubtype`), die Werte der Konstanten (`FPDF_INCREMENTAL`) und ob `FPDFSignatureObj_GetDocMDPPermission` vorhanden ist. Alle Bindings kommen erst mit diesem Milestone dazu und werden gegen die Header der ausgelieferten Version geprüft.

**Signaturen (DocMDP)**

- Pagewing liest vor dem ersten Speichern, ob das Dokument signiert ist, und die DocMDP-Stufe (1: keine Änderung erlaubt, 2: nur Formulare, 3: auch Annotationen; keine Angabe: normale Unterschrift, bei der Acrobat Annotationen erlaubt).
- Meldungen (Karte vor dem Speichern, nur bei signierten Dateien):
  - Stufe 3 oder keine Angabe: „Dieses Dokument ist signiert. Pagewing hängt Ihre Änderungen nur an, die signierten Bytes bleiben unverändert.“ Mehr verspricht Pagewing nicht („gültig“ wird nie behauptet).
  - Stufe 1 oder 2: „Diese Signatur erlaubt keine Kommentare. Nach dem Speichern wird sie in Programmen wie Acrobat als ungültig angezeigt.“ Knöpfe „Abbrechen“ (Standard), „Speichern unter“, „Trotzdem speichern“.
- Die Texte gibt es in beiden Sprachen; sie stehen als Symbol und Text da.

**Daten und Zustand**

- Pagewing führt die Annotationen eines Tabs als **Schnappschuss-Modell** in `Pagewing.Core` (UI-frei, wie `PageText`): Art, Seite, Eckpunkte, Farbe, Kommentar, Autor, Datum, ob bearbeitbar. PDFium wird nur auf dem `PdfiumThread` aufgerufen, jede Änderung ist ein kurzer Auftrag (keine langen Blöcke). Die Befehlsliste für Rückgängig/Wiederholen ist reine Core-Logik und ohne PDFium testbar.
- Nach einer Änderung verwirft der Bildspeicher **nur die Kacheln der betroffenen Seite** (Epoche je Seite), die Vorschau kommt wie beim Scrollen zuerst, dann die scharfen Kacheln. Das Panel „Seiten“ aktualisiert die Vorschaubilder der Seite.
- **Suchtreffer und Auswahl** liegen weiter oben auf der Seite (`RefreshHighlights`). Ein gelber Suchtreffer auf einer gelben Hervorhebung ist unterscheidbar (aktueller Treffer hat den Rahmen, 7.4); der Builder prüft das.

**Setzungen aus der Gegenlese (meine Entscheidungen, bitte beim Lesen prüfen)**

- **Kommentartext** steht in `/Contents` der Markierung. Ob zusätzlich eine `/Popup`-Annotation nötig ist, damit Acrobat den Kommentar sauber zeigt, klärt der Spike (beide Varianten in der Prüfmatrix). Eine `/Popup` und ihre Antworten (`/IRT`) zählen nie als eigene Annotation: nicht in der Liste, nicht in der Zahl der Prüfung nach dem Speichern. Beim Bearbeiten entfernt Pagewing ein vorhandenes `/RC` (formatierter Text), damit es den neuen `/Contents` nicht überdeckt.
- **Fremde Markierungen mit Antworten** (andere Annotationen verweisen per `/IRT` darauf) sind nur lesbar, nicht bearbeitbar, damit keine verwaisten Antworten bleiben. Alle übrigen Hervorhebungen, Unterstreichungen und Durchstreichungen sind bearbeitbar; Löschen entfernt auch die zugehörige `/Popup`.
- **Rückgängig stellt durch Neuanlegen wieder her**, nicht Byte für Byte: Herstellerspezifische Schlüssel einer gelöschten fremden Markierung gehen dabei verloren (in `DECISIONS` festzuhalten). Der Builder darf stattdessen „Löschen erst beim Speichern ausführen“ wählen, wenn es das Modell einfacher macht. Eine Farbänderung an einer fremden Markierung erzeugt ihr Erscheinungsbild neu und verwirft das alte.
- **Neuöffnen nach dem Speichern, ohne dass je ein Tab ohne Dokument entsteht:** Das neue Dokument ist geöffnet und geprüft (das ist die Prüfung 2), bevor das alte geschlossen wird. Ob das Ersetzen der Zieldatei unter Windows klappt, während das alte Dokument sie noch offen hat (die Dateien werden mit `FileShare.ReadWrite | FileShare.Delete` gelesen), belegt ein Test unter Windows; sonst entscheidet der Plan die Reihenfolge, nie zum Preis eines Tabs ohne Dokument. Das Passwort eines geschützten Dokuments bleibt dafür **nur im Arbeitsspeicher** der Sitzung (wie es PDFium ohnehin hält) und wird nie geschrieben.
- **Update-Weg:** „Installieren“ fragt zuerst alle Fenster mit ungespeicherten Tabs der Reihe nach (das aktive Fenster zuerst) mit derselben Karte wie beim Schließen; ein „Abbrechen“ bricht die Installation ab. Die Prüfung sitzt im `UpdateCoordinator` **vor** dem Aufruf des `IUpdateApplier` (dessen Vertrag bleibt unverändert) und gehört zu M9.
- **Abmelden, Herunterfahren, SIGTERM:** Ohne Wiederherstellungsdatei können ungespeicherte Änderungen dabei verloren gehen; Pagewing versucht in M9 nicht, das Herunterfahren aufzuhalten. Die Grenze steht in `DECISIONS`; die Wiederherstellungsdatei (Backlog) schließt sie später.
- **Dunkle Seite und Druck:** Markierungen bleiben auf der dunklen Seite **sichtbar und unterscheidbar** (das Farbschema aus M6 mit „Füllung zu Strich“ könnte Hervorhebungen sonst zu Umrissen machen). Der Weg ist dem Builder überlassen; Vorbild sind die Suchtreffer, die auf der dunklen Seite als eigene halbtransparente Flächen gezeichnet werden. Markierungen werden gedruckt (Gesamt-Spec 5.7: Anmerkungen werden mitgedruckt, gedruckt wird immer hell).

## Randbedingungen

- **Startpfad:** Nichts Neues vor dem ersten scharfen Seitenbild. Annotationen, Panel, Werkzeugleiste und Speicher-Code entstehen erst bei Gebrauch (Datenvorlage, Laden beim ersten Öffnen). Kein Lesen der Annotationen beim Öffnen. Budgets und `bench/startup` bleiben unverändert streng.
- **Messen:** Der Builder misst und hält fest: Zeit von der Markierung bis zum neuen scharfen Bild, Dauer des Speicherns bei kleiner und großer Datei (Beispiel: 200 Seiten `text-dense.pdf` und eine mehrere hundert MB große Datei), Scroll-Messlauf (`--measure-scroll`) mit Annotationen. Bis eine Zahl vorliegt, gibt es kein Budget; wird eines nötig, ist es eine Entscheidung in `DECISIONS`.
- **Speichern und PDFium:** Ein Speichern ist ein langer Auftrag und hält den `PdfiumThread` für seine Dauer; während dessen steht das Rendern. Der Builder misst das bei großen Dateien und entscheidet über Fortschrittsanzeige und Hinweis. Der UI-Thread wartet nie.
- **Speicherkosten:** Inkrementelles Speichern schreibt die ganze Datei neu (Original plus Anhang), weil es atomar über eine temporäre Datei läuft; die Dauer wächst mit der Dateigröße.
- **Native AOT:** keine Reflection, JSON nur mit Source-Generator, AOT- und Trim-Warnungen sind Fehler. **Abhängigkeiten:** keine neuen; kommt eine hinzu, Lizenzmanifest und `DECISIONS`-Eintrag.
- **Tests schreiben nie in den echten Benutzerordner.** Test-PDFs mit klarer Lizenz in `testdata/pdfs/LICENSES.md` und im Render-Korpus (`RenderCorpus.Cases` oder `Skipped`). Annotierte Ausgaben aus Tests liegen in Temp-Ordnern, nicht in `testdata`.
- **Tastatur und Barrierefreiheit:** Kürzel nur im `CommandCatalog` (Strg+S, Strg+Umschalt+S, Strg+Z, Strg+Umschalt+Z und Strg+Y, die Kürzel des Modus); feldeigene Bindungen bleiben (Strg+Z im Kommentarfeld macht Text rückgängig, nicht die Annotation). Das Panel, die Werkzeugleiste und die Karten sind per Tastatur bedienbar, haben Automation-Namen und geben Zustände nicht nur über Farbe an. **Bekannte Grenze:** Das Erzeugen einer Markierung braucht eine Textauswahl, und die gibt es seit M5 nur mit der Maus (Auswahl per Tastatur ist Backlog); Anlegen per Tastatur ist damit in M9 nicht möglich, Lesen, Springen, Bearbeiten und Löschen schon.
- **Texte** in Deutsch und Englisch (resx). Keine Bezeichnung als „rechtsgültig“ (Regel aus `CLAUDE.md` gilt für alle Texte des Teilprojekts).
- **Dokumentation:** `Docs/DECISIONS.md` (Bindings, Speicher-Pipeline, Erscheinungsbild-Strategie, Rückgängig-Modell, DocMDP-Texte, Grenze „alte Fassung bleibt in der Datei“, Neuöffnen nach dem Speichern), `CHANGELOG.md` unter `[Unreleased]`, `CLAUDE.md` (Abschnitt „Projektspezifisches“, Speichern und neue Stolperfallen), Design-Spec 7.3 und 7.7 (Panel „Kommentare“, Modus, Karten), Gesamt-Spec 5.5, die Hilfekarte (Kürzel) und die Roadmap.

## Abnahme

Die drei Kriterien der Roadmap bleiben, das dritte ändert sich:

1. Gespeicherte Annotationen erscheinen in einem anderen Viewer (Chrome und Acrobat Reader) korrekt, auch auf gedrehten Seiten und mit versetzter CropBox (manuelle Prüfung mit den von Pagewing erzeugten Dateien; ein Round-Trip-Test sichert den Teil, der sich in PDFium prüfen lässt).
2. Ein Abbruch während des Speicherns (Ausnahme im Schreiber, Abbruch durch den Nutzer, Schreibfehler, volle Platte) lässt das Original **byteweise unverändert** und räumt die temporäre Datei auf (Test mit Fehlereinspeisung).
3. **Neu:** An einer signierten Test-PDF sind die ersten *n* Bytes nach dem Speichern identisch mit dem Original (Test); die Warnkarte erscheint bei DocMDP-Stufe 1 und 2 und nicht bei Stufe 3 (Test an je einer Datei); ob Acrobat die Signatur danach als gültig anzeigt, ist eine manuelle Prüfung. Pagewing sagt nirgends „gültig“.

Dazu kommen Tests für: Löschen und Rückgängig/Wiederholen (Core), Modus und Karten (Headless-UI), Update-Weg bei ungespeicherten Änderungen, verschlüsselte Datei, linearisierte Datei, schreibgeschützte Datei und Ordner, seit dem Öffnen verändertes Original, Kopf- und Fußfälle der Geometrie (gedrehte Seiten, CropBox).

## Scope-Änderung (in der Roadmap festzuhalten)

- M10 verliert „Rückgängig/Wiederholen“ und „Löschen“ für Markierungen (kommen in M9); M10 behält Auswählen und Verschieben und erweitert die Befehlsliste um die neuen Arten.
- Abnahmekriterium 3 von M9 ändert sich wie oben.
- Backlog-Einträge: Zusammenfassung der Kommentare als Text kopieren, Wiederherstellungsdatei bei Absturz, Antwortketten, Filter nach Autor oder Farbe, Bearbeiten fremder Annotationen, Haftnotiz, Bereinigen beim Speichern (vollständiger Neuschrieb), Textauswahl per Tastatur.

## Rahmen für M10 bis M12 (noch nicht entschieden)

- **M10 Zeichnen & Formen:** nutzt das Annotationsmodell, den Schreibweg und die Befehlsliste aus M9. Offen bleibt Linie/Pfeil (Stempel mit Pfaden, QPDF oder der eigene Schreiber aus dem Spike); die Entscheidung fällt in der Spec zu M10 auf Grundlage des Spike-Ergebnisses.
- **M11 Formulare:** braucht PDFiums Formular-Umgebung (Rückrufe) und teilt die Speicher-Pipeline; die Spec zu M11 prüft, ob Formularwerte über dieselbe Pipeline laufen können (Annotationen und Formularwerte in einem Speichervorgang).
- **M12 Sichtbare Unterschrift:** baut auf Freihand- und Stempel-Annotation auf; Hinweis „Ersetzt keine handschriftliche Unterschrift, wo Schriftform verlangt ist.“, nie „rechtsgültig“.
- **Release v0.2.0** nach M12. Vor dem Release prüft eine Gesamtabnahme den Weg „Formular ausfüllen, markieren, unterschreiben, speichern“ in Chrome und Acrobat.

## Entschieden nach der Gegenlese (Projektinhaber, 2026-10-24: Empfehlung übernommen)

1. **Keine schwebende Leiste in der Leseansicht.** Design-Spec 7.7 bleibt unverändert. Es bleibt beim Modus „Kommentieren“ mit gewählter Art (Loslassen der Maus wendet sie an) und den Befehlen „Hervorheben“, „Unterstreichen“, „Durchstreichen“ mit Kürzel bei vorhandener Auswahl. Eine schwebende Leiste wäre eine spätere, bewusste Änderung der Design-Spec (Backlog).
2. **Gleiches Aussehen in jedem Viewer hat Vorrang vor einem kleinen Umfang.** Zeigt der Spike, dass PDFium allein kein sauberes Erscheinungsbild schafft (`FPDFAnnot_SetAP` nimmt nur den Inhaltsstrom ohne Ressourcen, Multiplizieren braucht aber eine Ressource), dann gilt **Weg (a): ein eigener Schreiber für den angehängten Teil** (Querverweis, Trailer mit `/Prev`, die Verschlüsselung der neuen Objekte bei geschützten Dateien, Sonderfälle wie Objektströme). Eine schlichtere Hervorhebung ohne Multiplizieren (Weg b) ist **kein** Rückfall, weil sie je Viewer anders aussehen kann. Der Plan sieht deshalb den Spike, die Entscheidung danach und beide Wege vor; in diesem Fall wird M9 in zwei Teile geteilt (Roadmap-Änderung dann im Plan: Speichern mit Schreiber zuerst, Werkzeuge danach). Der Plan nennt die Prüfmatrix des Spikes und was als „bestanden“ gilt (Chrome und Acrobat Reader, Windows; Hervorhebung, Unterstreichung und Durchstreichung, gedrehte Seite, CropBox mit Versatz, mit und ohne `/Popup`).

## Dem Builder überlassen

Zuschnitt der Typen in `Pagewing.Core` und `Pagewing.Pdfium`, Reihenfolge der Aufgaben nach dem Spike, die genaue Form der Befehlsliste und des Protokolls, Gestaltung von Panel, Werkzeugleiste und Karten innerhalb der Design-Spec, Kürzel des Modus (ohne Kollision im `CommandCatalog`), Palettenwerte und Namen der Farben, Texte, Schwellen für Fortschrittsanzeige beim Speichern, der Weg zu einer signierten Test-PDF (Herkunft und Lizenz nach den Regeln in `CLAUDE.md`), Verhalten, wenn dieselbe Datei in zwei Tabs oder Fenstern geöffnet ist und eines davon Änderungen hat.
