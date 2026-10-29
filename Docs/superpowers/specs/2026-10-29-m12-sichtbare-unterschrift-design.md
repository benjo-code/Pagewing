# Milestone 12 – Sichtbare Unterschrift (Entwurf)

Stand 2026-10-29, erarbeitet im Brainstorming mit dem Projektinhaber. Grundlage sind Milestone 12 in [Docs/ROADMAP.md](../../ROADMAP.md), die [Spec von Teilprojekt 2](2026-10-24-teilprojekt2-annotieren-design.md) (Abschnitt „Rahmen für M10 bis M12“), die [Spec von M10](2026-10-25-m10-zeichnen-formen-design.md) und die [Design-Spec](2026-10-17-designsystem.md). Dieser Entwurf legt fest, **was** gebaut wird und **warum**; das **wie** im Einzelnen klärt der Plan (`Docs/superpowers/plans/`).

## Absicht

Wer einen Vertrag, ein Formular oder ein Schreiben mit Pagewing „unterschreiben“ will, soll es in wenigen Klicks tun: Unterschrift einmal anlegen, danach wählen, auf die Seite klicken, fertig. Es geht um das **Bild** einer Unterschrift auf der Seite, nicht um eine digitale Signatur mit Zertifikat (Out of Scope laut Roadmap). Pagewing sagt das offen und nennt eine solche Unterschrift nie „rechtsgültig“.

Gegenüber verbreiteten Programmen soll Pagewing in vier Punkten besser sein (Ergebnis einer Websuche am 2026-10-29; die Foren-Treffer zu Adobe reichen bis 2024, die Rechtsquellen sind Anbieterseiten, nicht der Gesetzestext):

1. **Verlässlich und schnell.** Acrobat Readers „Fill & Sign“ fällt in der Adobe-Community durch Abstürze, Einfrieren beim Hinzufügen einer Signatur und Verzögerungen auf; der verbreitete Rat ist, das PDF erst in ein neues PDF zu drucken. Das Anlegen und Platzieren bei Pagewing blockiert nie die Oberfläche und nie das Rendern.
2. **Ein Bild ohne Umwege.** Okular braucht dafür einen selbst angelegten Stempel und eine vorher mit ImageMagick freigestellte Datei; der Wunsch nach einer echten Funktion ist seit Jahren offen. Bei Pagewing genügt „Bild wählen“.
3. **Freistellen offline.** Gängig sind Online-Dienste, bei denen man die eigene Unterschrift hochlädt. Pagewing stellt den Papierhintergrund lokal frei, mit Vorschau.
4. **Ehrlich über die Wirkung.** Eine Unterschriftsgrafik beweist rechtlich nichts; der Hinweis steht gut sichtbar im Dialog (siehe unten).

## Ziele

1. Eine Unterschrift **zeichnen** (Maus, Stift, Touch) oder aus einem **Bild importieren** (PNG, JPG) mit lokalem Freistellen.
2. Unterschriften **lokal speichern, benennen, löschen und wiederverwenden** (höchstens 10).
3. Eine Unterschrift **platzieren, verschieben und skalieren** (Seitenverhältnis fest), auch mit der Tastatur; rückgängig, wiederholen, löschen, kommentieren wie jede Annotation.
4. **Speichern** durch dieselbe Pipeline wie alle Annotationen (atomar, geprüft, inkrementell), das Aussehen in Chrome und Acrobat gleich, auch auf gedrehten Seiten und mit versetzter CropBox.
5. Sichtbar auf der **dunklen Seite** und im **Druck**.
6. Den Hinweis „Ersetzt keine handschriftliche Unterschrift, wo Schriftform verlangt ist.“ im Dialog anzeigen.

## Nicht-Ziele und Gegenziele

- Keine digitale Signatur mit Zertifikat, kein Beschreiben eines vorhandenen Signaturfelds (`/Sig`), kein Einrasten in ein Signaturfeld (Backlog: ein Bild in einem Feld wäre kein Signieren und könnte so wirken).
- Kein „Name tippen“ mit Schreibschrift (wirkt unecht, bringt Schriftlizenzen mit), keine Paraphen, kein Datums- oder Textstempel, keine Cloud-Synchronisierung, keine Konten.
- Kein Einbrennen in den Seiteninhalt (Projektinhaber: die Unterschrift bleibt eine verschiebbare Annotation; „Fixieren“ ist Backlog).
- Keine Bearbeitung fremder Stempel (die M10-Regel „Stempel sind nicht bearbeitbar“ gilt weiter); keine Neufärbung eines importierten Bildes.
- Gegenziel (Fehlschlag, auch wenn es technisch läuft): eine gespeicherte Unterschrift, die in einem gängigen Viewer fehlt, verrutscht ist oder mit weißem Kasten erscheint; ein Text oder eine Anzeige, die die Unterschrift „rechtsgültig“ nennt oder als digitale Signatur erscheinen lässt; ein Unterschriftsbild, das das Programm verlässt (Upload, Protokoll, Absturzbericht); langsamerer Start; Änderungen, die verloren gehen, ohne dass der Nutzer es bestätigt hat.

## Festgelegt (vom Projektinhaber, 2026-10-29: Vorschlag bestätigt)

1. Zeichnen **und** Bild-Import, lokales Freistellen mit Regler und Vorschau auf Schachbrett; Speichern lokal im Benutzerordner (höchstens zehn, benennen, löschen); Platzieren mit Vorschau am Zeiger, Verschieben und Skalieren wie bei den Zeichnungen, mit festem Seitenverhältnis und per Tastatur.
2. Im PDF ist die Unterschrift eine **Annotation**, die bis zum Schluss verschiebbar bleibt, nicht in den Seiteninhalt eingebrannt.
3. Zweizeiliger Hinweis im Dialog: der vorgeschriebene Satz (Deutsch und Englisch sinngemäß) plus „Das ist ein Bild Ihrer Unterschrift, keine digitale Signatur.“ (Formulierung der zweiten Zeile war ein Vorschlag von Claude, vom Projektinhaber mit „passt“ bestätigt). Ein Test prüft den Hinweis und dass „rechtsgültig“ in keinem UI-Text vorkommt.
4. Nicht in M12: die Punkte unter „Nicht-Ziele“.
5. **Fällt der Spike für das importierte Bild durch** (Stempel mit Bildobjekt erscheint in Chrome oder Acrobat mit Kasten, verschoben oder gar nicht), erscheint M12 mit **gezeichneten Unterschriften allein**, und der Bildimport mit Freistellen wird ein eigener Milestone (Eintrag in der Roadmap unter „Scope-Änderungen“). Weder ein undurchsichtiges Bild mit weißem Kasten noch das Vektorisieren eines Fotos noch ein eigener PDF-Schreiber ist als Notlösung vorgesehen. Der Plan enthält nach dem Spike einen festen Halt, an dem der Projektinhaber das Ergebnis sieht, bevor der Rest gebaut wird (Empfehlung von Claude, vom Projektinhaber am 2026-10-29 übernommen).

## Zuerst klären: Spike zur Darstellung im PDF

PDFium kann ein Bild nur über ein Stempel-Objekt in eine Annotation legen (`FPDFAnnot_AppendObject` ist nur für Ink und Stempel erlaubt, siehe `Docs/DECISIONS.md`, Textfeld-Spike). `FPDFAnnot_SetAP` mit einem Textstrom reicht für ein Bild nicht, weil dafür ein Bild-XObject nötig ist. Bindings für Bildobjekte gibt es noch nicht (`FPDFPageObj_NewImageObj`, `FPDFImageObj_SetBitmap`; Bindings nur für Gebrauchtes, Konstanten gegen die Header von `chromium/8100` prüfen). Der Spike beantwortet, bevor der Rest geplant wird:

- **Importiertes Bild:** Stempel mit Bildobjekt, Alpha (Bild mit Weichzeichner-Kante) landet als Maske im `/AP` und erscheint in Chrome, Acrobat Reader und Pagewing ohne weißen Kasten; gedrehte Seite (Bild steht aufrecht), CropBox mit Versatz; Dateigröße je Bild; ob PDFium ein Bild bei zweimal derselben Unterschrift doppelt schreibt (zu messen, nicht anzunehmen).
- **Gezeichnet:** zwei Wege vergleichen. (a) **Ink mit `/Subj` „Signature“**, wiedererkannt wie Pfeil und Linie (`ArrowGeometry.TryRecognize`-Muster): Auswahl, Farbe, dunkle Seite und Geometrie sind aus M10 da. (b) Stempel mit Pfadobjekten (Vektor, eine Art für beide Quellen). **Erwartung (Claude): (a)**, weil es fast alles wiederverwendet; (b) nur, wenn (a) in einem Viewer anders aussieht oder die Skalierung der Strichstärke stört.
- **Erkennen und Bearbeitbarkeit:** „Eigen“ ist ein Stempel nur mit einer Marke im `/AP` (Muster wie beim Textfeld: Marke, Fingerabdruck, Box gegen `/Rect`); jeder andere Stempel bleibt gesperrt. Zu prüfen, ob die Marke bei einem Neuschreiben durch Acrobat stehen bleibt (sonst gesperrt: löschbar, nicht änderbar, das ist hinnehmbar).
- **Dunkle Seite:** Dunkle Tinte auf dunklem Grund ist unsichtbar. Zu messen, was PDFiums Farbschema mit einem Bildobjekt und mit Pfaden macht; Ziel ist Sichtbarkeit mit 3:1 gegen die dunkle Seite, der Weg ist dem Builder überlassen (Vorbild `PdfiumDarkDrawings`).

Das Ergebnis steht als Eintrag in `Docs/DECISIONS.md` und kann die Aufteilung der Aufgaben ändern, aber nicht die Ziele oben.

## Meine Entscheidungen (Vorschläge, im Plan zu bestätigen)

**Bedienung** (nach Design-Spec 7.7; die Werkzeugleiste des Modus „Kommentieren“ wächst um den Eintrag „Unterschrift“, es entsteht kein neuer Modus)

- Der Eintrag „Unterschrift“ öffnet ein Menü der gespeicherten Unterschriften plus „Neue Unterschrift …“. Eine Wahl macht das Platzier-Werkzeug aktiv: dem Zeiger folgt eine Vorschau, ein Klick setzt sie, danach ist sie ausgewählt (Rahmen, Griffe, Pfeiltasten wie in M10). **Tastaturweg:** Wahl per Tastatur setzt sie in die Mitte des sichtbaren Teils der aktuellen Seite und wählt sie aus; Verschieben dann mit den Pfeiltasten. Kürzel nur im `CommandCatalog` (Titel in beiden Sprachen, keine Kollision), Esc wie in M9/M10 (`DocumentView.TryEscape`).
- Der Dialog „Neue Unterschrift“ ist eine Karte im Fenster (kein `Popup`, wie die anderen Karten) mit zwei Reitern „Zeichnen“ und „Bild“, Name, Vorschau, „Speichern“ und „Abbrechen“. Der Hinweis steht in beiden Reitern sichtbar, ohne Scrollen, und lässt sich nicht dauerhaft wegklicken. Zustände sind nie nur über Farbe gekennzeichnet; neue Farbpaare vorher auf WCAG 2.2 AA prüfen.
- **Zeichnen:** Zeichenfläche mit Hilfslinie, Strichglättung und Ausdünnung mit `StrokeSimplifier` aus M10, „Löschen“ und „Zurück“ im Dialog; Tinte aus der Palette von `DrawPalette` (Standard Schwarz; Blau naheliegend). Speichern als Vektor (Striche in normierten Koordinaten), skaliert bei jeder Größe scharf.
- **Bild:** PNG und JPG, Dateiwahl und Ablegen/Einfügen aus der Zwischenablage (Letzteres ist ein Vorschlag, nur wenn es ohne Aufwand geht). Freistellen: Helligkeit wird zu Alpha, ein Regler setzt die Schwelle, die Vorschau liegt auf einem Schachbrett; „Beschneiden auf den Inhalt“ automatisch. Gespeichert wird als PNG mit Alpha, längste Seite höchstens 1.200 px (größere Bilder werden verkleinert).
- **Platzierte Größe:** Anfangsbreite so, dass eine Unterschrift auf der Seite normal wirkt (Vorschlag: 160 pt breit, höchstens ein Drittel der Seitenbreite); Seitenverhältnis immer fest.
- **Panel „Kommentare“:** Eintrag „Unterschrift“ als eigene Art (nicht „Freihand“ oder „Stempel“), Kommentar möglich.
- **Rechenlogik UI-frei in `Pagewing.Core`** (Vorschlag): Pixelpuffer und Freistellen (`SignatureImage`, Schwelle zu Alpha, Beschneiden), Speicher (`SignatureStore`, JSON nur mit Source-Generator, atomar wie `JsonFile`, Ordner unter `UserDataPaths`), die Geometrie der gezeichneten Unterschrift; **Dekodieren und Kodieren von PNG/JPG in der App** (Avalonia/Skia), PDFium nur in `Pagewing.Pdfium`.

**Speichern und Signaturen**

- Läuft durch `DocumentSaver` unverändert. Die vorhandene `SignaturePolicy` gilt weiter (DocMDP 1 und 2 warnen, Stufe 3 oder ohne Angabe: Hinweis einmal). Pagewing sagt zur Signatur eines Dokuments, in das es eine Unterschrift setzt, nie „gültig“.
- Nach einem Speichern zählt die Unterschrift wie jede Annotation als gespeichert, der Verlauf ist leer.

**Datenschutz:** Unterschriften liegen als normale Dateien im Benutzerordner (Rechte des Benutzers, nicht verschlüsselt; im Plan zu prüfen, ob die Datei beim Anlegen unter Unix auf `0600` gesetzt wird). Sie verlassen das Programm nie außer in der PDF-Datei, die der Nutzer selbst speichert. Kein Eintrag in Protokollen, Fehlermeldungen, Verlauf oder Einstellungen (`settings.json` enthält höchstens den Namen der zuletzt gewählten Unterschrift, falls überhaupt).

**Grenzen gegen feindliche und große Eingaben:** Größe der Importdatei (Vorschlag 10 MiB) und Maße (Vorschlag 6.000 × 6.000 px, **vor** dem Dekodieren aus dem Kopf gelesen, gegen Dekompressionsbomben), Höchstzahl von Punkten einer gezeichneten Unterschrift (Wert aus `PdfiumDrawings.MaxInkPoints`), Höchstzahl von 10 gespeicherten Unterschriften. Eine Ablehnung nennt den Grund im Dialog.

## Randbedingungen

- **Startpfad:** nichts Neues vor dem ersten scharfen Seitenbild. Dialog, Speicher, Dekodierung und Vorschau entstehen erst beim Gebrauch; der Ordner wird nicht vor dem ersten Öffnen des Menüs gelesen. Messläufe bleiben unverändert (`MeasurementRun.Start`), Tests auch.
- **PDFium:** nur über den `PdfiumThread`, jede Änderung ein kurzer Auftrag (Anlegen, Verschieben, Skalieren, Löschen), der UI-Thread wartet nie. Ein neuer Rückruf aus PDFium kommt erst nach Prüfung in `UnmanagedCallbackTests`. Bildobjekte erzeugt der Auftrag aus einem Pixelpuffer, den die App vorher dekodiert hat (nie auf dem `PdfiumThread` dekodieren).
- **Messen und festhalten:** Zeit vom Klick bis zur sichtbaren Unterschrift, Dauer des Auftrags, Dateigröße je Unterschrift (importiert und gezeichnet, ein- und fünfmal platziert), Scroll-Messlauf auf einer Seite mit mehreren Unterschriften. Bis eine Zahl vorliegt, gibt es kein Budget; wird eines nötig, ist es eine Entscheidung in `DECISIONS`.
- **Native AOT:** keine Reflection, JSON nur mit Source-Generator. **Abhängigkeiten:** keine neuen (Bildcodec liefert Avalonia/Skia); kommt eine hinzu, Lizenzmanifest und `DECISIONS`-Eintrag.
- **Tests:** schreiben nie in den echten Benutzerordner (Temp-Ordner), nie neben die Eingaben in `testdata`; Test-Bilder selbst erzeugt, Herkunft in `testdata/pdfs/LICENSES.md`, neue PDFs im Render-Korpus oder in `Skipped`. Round-Trip-Tests je Art (importiert, gezeichnet) auf gedrehter Seite und mit CropBox, Marke, Verschieben/Skalieren genau einmal vorhanden, dunkle Seite, Druck. Muster für die Handprüfung in Chrome und Acrobat über `InteropSampleTests` (Prüfliste `packaging/README.md`); ob der Projektinhaber sie im Milestone oder erst in der Abnahme von `v0.2.0` prüft, entscheidet er bei der Abnahme.
- **Texte** in Deutsch und Englisch (resx). Keine Bezeichnung als „rechtsgültig“, „legally binding“ oder „legally valid“ in keiner Sprache.

## Setzungen aus der Prüfung der Spec (Claude, vom Projektinhaber zu prüfen)

- **Farbe und Stärke einer gezeichneten Unterschrift** bleiben nach dem Platzieren änderbar, mit denselben Bedienelementen und demselben Weg (`ReplaceDrawingAsync`) wie bei Zeichnungen aus M10; die gespeicherte Unterschrift in der Bibliothek behält ihre Farbe. Die Strichstärke wächst beim Skalieren **nicht** mit (wie bei Ink). Eine Bibliothek-Unterschrift kann beim Platzieren eine andere Tinte bekommen, ohne dass die gespeicherte sich ändert. Importierte Bilder bieten weder Farbe noch Stärke an, die Auswahlleiste blendet beides für sie aus.
- **Formular-Modus:** Der Befehl „Unterschrift einfügen“ im `CommandCatalog` startet den Modus „Kommentieren“ selbst, wie es die Markierungskürzel tun (im Plan zu prüfen); aus dem Modus „Formular ausfüllen“ heraus wechselt er also den Modus (der Entwurf eines Formularfelds wird dabei wie bisher übernommen, `FlushFormDraft`). Der Eintrag erscheint nicht zusätzlich in der Formularleiste (`FormToolbar`).
- **Rückgängig beim Löschen eines Bildes:** möglich. Der Verlauf hält die **kodierten** Bytes (PNG, höchstens rund 1,5 MiB je Bild), nie den dekodierten Puffer. Eine Obergrenze für den Speicher des Verlaufs legt der Plan fest; ein Schritt fällt nie still weg (wie bei `AnnotationHistory`, falls dort eine Grenze gilt, mit Meldung).
- **Handprüfung in Chrome und Acrobat:** wie bei M9 bis M11 ein offenes Kriterium der Abnahme von `v0.2.0` und kein Hindernis für „M12 fertig“, **solange der Projektinhaber es so entscheidet**. Der Milestone wird dann mit dem Satz abgenommen, dass belegt sind die Tests, nicht der Blick in Chrome und Acrobat. Die Muster für die Prüfung gehören zur Lieferung.

## Was dem Builder überlassen bleibt

Die Reihenfolge der Aufgaben, die genauen Maße und Farben der Karte (Design-Spec), Form des Reglers und der Schachbrett-Vorschau, die Namen der Typen, das Dateiformat im Benutzerordner (eine Datei oder je Unterschrift eine), ob die gezeichnete Unterschrift als Ink oder als Stempel mit Pfaden umgesetzt wird (nach dem Spike), und der Weg für die Sichtbarkeit auf der dunklen Seite.
