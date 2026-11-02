# Teilprojekt 4 – OCR (Milestone 15): Design

Stand 2026-11-02. Rahmen: [Gesamt-Spec](2026-10-16-pagewing-design.md), Abschnitt 6 „Teilprojekt 4“; Entscheidung „Zusatzbibliotheken: QPDF als Kandidat, Tesseract über eigene Bindings, kein PDFsharp“ in `Docs/DECISIONS.md`. Diese Spec legt **M15 (OCR)** fest. M16 (Text bearbeiten) bekommt vor seinem Plan einen eigenen Nachtrag; hier gilt für ihn nur, was in Abschnitt 8 steht.

Markierungen: **[Entscheidung]** vom Projektinhaber (2026-11-02, im Gespräch), **[Mein Vorschlag]** vom Entwurf, vom Projektinhaber ohne Einwand freigegeben („Passt“), **[Baumeister]** überlässt die Spec dem, der plant und baut, **[Spike]** ist unbekannt und wird in der ersten Aufgabe des Plans geklärt, bevor etwas darauf gebaut wird.

## 1. Worum es geht

Ein gescanntes PDF ist für Pagewing ein Bild: Suchen, Kopieren und Auswählen finden nichts. Der Nutzer will einen Vertrag oder eine Rechnung öffnen, ein Wort suchen und es finden, markieren, kopieren, auch als Grundlage für das Schwärzen (M14: „Text in Bildern erkennen“ stand dort im Backlog). Alles offline, ohne Konto, ohne Cloud.

**Ziele**
- Aus einem Scan wird ein durchsuchbares PDF: Das Seitenbild bleibt **bitgleich**, dahinter (unsichtbar) liegt eine Textebene mit den erkannten Wörtern an den richtigen Stellen.
- In der App und in der CLI, mit demselben Kern; im Hintergrund, mit Fortschritt und Abbruch, ohne die Oberfläche oder das Rendern zu stören.
- Schnell und ehrlich: Seiten, die schon Text haben, werden übersprungen; der Bericht sagt, was erkannt wurde und wie sicher, und verspricht nie Genauigkeit.
- Tesseract wird erst bei Nutzung geladen; Start-, Öffnen- und Scroll-Budgets bleiben unverändert.

**Nicht-Ziele** (Backlog, nicht nebenbei beginnen)
- „Editierbarer Text statt Bild“ (Acrobat: ClearScan / „Editable Text and Images“): Pagewing ersetzt nie Bildinhalt durch erkannten Text.
- Bildverbesserung am Ergebnis: Deskew, Entrauschen, Binarisieren, Neukodieren oder Komprimieren der Bilder (OCRmyPDF `--deskew`, `--clean`, `--optimize`). Das Bild wird nie neu geschrieben.
- Automatische Erkennung gedrehter Scans (Tesseract OSD, `osd.traineddata`). Bis dahin: Seite mit M13 drehen, dann erkennen **[Entscheidung]**.
- Layoutanalyse, Tabellen, Formeln, Handschrift, PDF/A-Erzeugung, Barcode.
- Sprachdaten aus dem Netz laden (zweite Netz-Ausnahme, Backlog) **[Entscheidung]**.
- Ein Prüfen-und-Korrigieren-Modus für unsichere Wörter (Acrobat „Suspects“).
- GPU-Beschleunigung, andere Engines (RapidOCR/PaddleOCR bleiben verworfen).

**Anti-Ziele** (Fehlschlag, auch wenn alles „funktioniert“)
- Ein Text- oder Zeilenversatz, sodass ein Treffer neben dem Wort im Bild markiert wird; gedrehte Seiten, `CropBox` und Seitengrößen sind die üblichen Verdächtigen.
- Das Original-Bild wird verändert, neu kodiert oder dupliziert; die Datei wird (bis auf die Textebene) größer, als nötig ist.
- Ein Bericht oder UI-Text sagt „fehlerfrei“, „korrekt erkannt“ oder „alle Wörter gefunden“.
- Stilles Überspringen: Eine Seite, die nicht erkannt wurde (zu groß, Zeitgrenze, Fehler), steht nicht im Bericht.
- Tesseract-Dateien (Bibliothek oder `.traineddata`) werden im Startpfad angefasst, oder Pagewing lädt eine Bibliothek aus einem vom Nutzer beschreibbaren Suchpfad.
- Netzzugriff in irgendeiner Form (auch indirekt über Tesseract-Funktionen, siehe 5.2); Seitenbilder landen auf der Festplatte.
- Das Rendern wartet auf Tesseract, oder der `PdfiumThread` ist für Sekunden blockiert; Speicher wächst mit der Seitenzahl.
- Eine zweite OCR-Ebene wird über die erste gelegt (doppelte Treffer).

## 2. Rahmen (gilt ohne neue Entscheidung)

Wie in `CLAUDE.md`: PDFium nur im `PdfiumThread` in kurzen Aufträgen (`Background`), der UI-Thread wartet nie; `AppContext` initialisiert, bevor ein Thread startet (gilt auch für Tesseract-Worker); Kürzel nur im `CommandCatalog`; UI-Texte in beiden Sprachen; kein Text nennt etwas „rechtsgültig“, „sicher“ im Rechtssinn oder eine Signatur „gültig“; Einstellungen als `AppSettings` mit `with`; nichts im Startpfad, Messläufe öffnen die Funktion nie (`OcrStartPathTests` nach dem Muster von `RedactionStartPathTests`); Tests schreiben nie in den echten Benutzerordner; Native AOT, keine Reflection; neue Abhängigkeiten brauchen Lizenzprüfung, `licenses.json`-Eintrag samt Lizenzdatei, neu erzeugte `ThirdPartyNotices.txt` und einen Eintrag in `DECISIONS.md`.

## 3. Entwurf: Verarbeitung

### 3.1 Projekte und Schichten **[Mein Vorschlag]**

- **`Pagewing.Ocr`** (neu): nur die Tesseract-Bindings (`[LibraryImport]` auf `capi.h`, Bibliothek erst beim ersten Gebrauch geladen, `NativeLibrary` mit Pfad neben der Anwendung, **nie** über PATH oder Arbeitsverzeichnis, wie bei PDFium) und eine kleine verwaltete Hülle (`OcrEngine`: Sprache setzen, Pixelpuffer übergeben, Wörter lesen). Kennt weder PDFium noch Core-Typen. Bindings nur für gebrauchte Funktionen; Konstanten und Strukturen gegen die Header der ausgelieferten Version prüfen (`NativeBindingsTests`-Muster).
- **`Pagewing.Core.Ocr`** (neu, UI- und plattformfrei): `OcrPlan`/`OcrOptions`, Seitenmodi, `OcrPageResult` (Wortliste mit Box, Text, Konfidenz), Bewertung „sieht aus wie ein Scan“, Umrechnung Pixel → Seitenpunkte, Bericht, Grenzen, Orchestrierung (Rendern, Erkennen, Übernehmen). Zugriff auf PDFium nur über `PdfDocument` (`PdfDocument.Ocr.cs`) und `Pagewing.Pdfium/PdfiumOcr*`.
- **Abhängigkeitsrichtung:** App und CLI → Core.Ocr → (`Pagewing.Ocr`, Core.Pdf → Pdfium). `Pagewing.Ocr` ist die einzige Stelle, die Tesseract kennt.

### 3.2 Ablauf je Seite

1. **Entscheiden** (billig, vor dem Rendern): Hat die Seite schon Text? Maßgeblich ist, was die Textextraktion liefert (auch unsichtbarer Text zählt, Extraktion filtert nach Darstellungsmodus nicht). **Schwelle [Mein Vorschlag]:** Eine Seite „hat Text“, wenn die Extraktion mindestens **50 Zeichen ohne Leerraum** liefert; Scans tragen oft nur eine Seitenzahl, einen Stempel oder eine Bates-Nummer, und solche Seiten sollen erkannt werden. Seiten mit weniger Text werden erkannt, ihr vorhandener Text bleibt unberührt, und der Bericht nennt sie („enthielt wenig Text, erkannt“). Seiten über der Schwelle im Modus „überspringen“ stehen mit Grund im Bericht (nie still). Wert **[Baumeister]**. Pagewings eigene OCR-Ebene wird erkannt (Marke, siehe 3.4) und zählt **nicht** als vorhandener Text; im Modus „ersetzen“ wird sie entfernt, in den anderen Modi liegt eine zweite nie darüber (Seite mit eigener Ebene = „bereits erkannt“, übersprungen).
2. **Rendern** der Seite **wie angezeigt** (Drehung und CropBox berücksichtigt) in Graustufen. **Auflösung [Mein Vorschlag]:** automatisch aus dem Bild der Seite (wirksame Auflösung des größten Bildes, begrenzt auf 150 bis 300 dpi; ohne Bild 300); die Karte bietet „Automatisch (empfohlen)“, 150, 200, 300 und 400 dpi, die CLI `--dpi`. Eine Scan-Auflösung von 150 dpi wird so nie hochgerechnet, eine von 600 dpi nicht in voller Größe erkannt. **Rangfolge der Grenzen:** erst die Pixelobergrenze (senkt die Auflösung, Bericht nennt es), dann die Mindestauflösung (darunter wird die Seite mit Grund übersprungen). Das Rendern geschieht als eine Folge kurzer Aufträge auf dem `PdfiumThread` (`RenderPageAsync` mit Zeitscheiben wie bei der Rasterseite in M14.1, Eintrag zum Vorbild). Obergrenze der Pixelzahl (Vorschlag 50 Megapixel nach OCRmyPDF; Wert **[Baumeister]**): Die Auflösung sinkt **nie still** (Bericht nennt sie), unter einer Mindestauflösung (Vorschlag 150 dpi) wird die Seite mit Grund übersprungen und gemeldet. Das Seitenbild existiert nur im Arbeitsspeicher, nie auf der Festplatte.
3. **Erkennen** auf einem eigenen Worker-Thread mit eigener Tesseract-Instanz (Instanzen werden nicht geteilt). Der `PdfiumThread` wartet nie auf Tesseract. Parallel arbeiten höchstens `min(Kerne − 1, 4)` Seiten **[Mein Vorschlag]**, einstellbar in der CLI (`--jobs`); das Rendern der nächsten Seite läuft, während die vorige erkannt wird, mit Rückstau-Grenze (höchstens so viele gerenderte Seiten im Speicher wie Worker, plus eine). Zeitgrenze je Seite (Vorschlag 300 s, Wert **[Baumeister]**); bei Überschreitung wird die Seite übersprungen und gemeldet. Die Bibliothek wird mit einem Thread je Instanz betrieben (keine eigene Parallelität von Tesseract; Umsetzung **[Baumeister]**, siehe 5.2).
4. **Ergebnis:** je Seite eine Liste von Wörtern (Text, Box in Pixeln, Konfidenz); Pixel werden über die Abbildung des Renderns in **Seitenpunkte** umgerechnet. Dafür gilt wie überall: `FPDF_PageToDevice`/`DisplayTransform` ist die einzige Koordinatenquelle, es gibt keine eigene Rotations- oder CropBox-Rechnung.
5. **Übernehmen** (Abschnitt 4): erst wenn der Lauf fertig ist (oder abgebrochen und der Nutzer das Teilergebnis wählt), nicht seitenweise in die Datei.

Hostile Eingaben (große Seiten, Seiten mit Millionen kleiner Wörter): Grenzen je Seite für Wörter und Zeichen (Werte **[Baumeister]**), Überschreitung = Seite mit Grund übersprungen, nie ein abgeschnittenes Ergebnis ohne Meldung.

### 3.3 Sprachen und Sprachdaten **[Entscheidung]**

- Im Paket liegen **`eng` und `deu` aus `tessdata_fast`** (zusammen rund 5,6 MB; Quelle: Gesamt-Spec). Die Modelle sind unter Apache-2.0 und gehören in die Drittlizenzen.
- Weitere Sprachen: Der Nutzer wählt eine `.traineddata`-Datei über einen Knopf („Sprachdatei hinzufügen …“); Pagewing kopiert sie in `tessdata/` im Benutzerordner (`UserDataPaths`, Rechte `0600`/`0700` unter Linux). Kein Netz. Ebenso können `tessdata_best`-Dateien eingelegt werden; sie gewinnen nur, wenn der Nutzer sie ausdrücklich wählt. Die CLI nimmt `--tessdata <Ordner>`.
- **Importierte Dateien sind nicht vertrauenswürdig:** Sie werden in nativem Code geparst. Grenzen: Dateigröße (Vorschlag höchstens 200 MB), nur über eine ausdrückliche Wahl des Nutzers, Dateiname aus `[a-z_]+`, nie aus dem Inhalt abgeleitet, nie ein Ordner, der dem Programm gehört. Ein Absturz im nativen Code kann nicht abgefangen werden; die Karte sagt deshalb beim Hinzufügen, dass Sprachdateien aus vertrauenswürdiger Quelle stammen sollen. Mitgelieferte Dateien werden beim Packen auf SHA-256 geprüft.
- **Auswahl:** Die Karte zeigt die installierten Sprachen (mitgelieferte und importierte) als Mehrfachauswahl, Reihenfolge = Priorität (Tesseract `deu+eng`). Standard: Systemsprache der Oberfläche plus Englisch, höchstens zwei; die zuletzt gewählte Kombination merkt `AppSettings` (`OcrLanguages`). Mehr als drei Sprachen: Hinweis, dass es langsamer und ungenauer wird.
- CLI: `--language deu+eng` (Standard wie die App, aus `--lang`/System), unbekannte Sprache = Exit 1 mit Liste der gefundenen.

### 3.4 Die Textebene **[Spike]**

Das Wesentliche steht fest, die Umsetzung in PDFium ist **nicht verifiziert** und wird in Aufgabe 1 des Plans geklärt:

- **Unsichtbarer Text** (Textdarstellungsmodus 3), ein Wort oder eine Zeile je Textobjekt, im Seiteninhalt **hinter dem Seitenbild** oder davor (beides darf nichts sichtbar ändern; die Reihenfolge entscheidet das Spike an Chrome, Acrobat, pdf.js und Pagewing), Skalierung so, dass die Glyphenbreite zur Wortbox passt (damit Auswahl und Treffer dem Wort im Bild folgen). Auf `/Rotate`-Seiten und mit CropBox wird die Textmatrix mitgedreht, sodass Auswahl und Suche aufrecht laufen.
- **Schrift und Unicode-Zuordnung:** Zwei Wege sind zu vergleichen: (a) Geist Regular (liegt im Core, deckt Latin-1, Latin Extended-A und Typografie ab) als Unicode-Schrift mit Zuordnung (`ToUnicode`), (b) eine „glyphlose“ Schrift wie bei Tesseracts eigener PDF-Ausgabe. Kriterium: Chrome, Acrobat und Pagewing extrahieren den Text **gleich** (Umlaute, `ß`, „–“, Ligatur-Auflösung), Auswahl und Suche stimmen mit dem Bild überein, die Dateigröße wächst nur um wenige Kilobyte je Seite. Die zu nehmende Schrift darf **nicht** je Seite neu eingebettet werden. Zeichen außerhalb des Zeichenvorrats der gewählten Schrift: Wort mit Grund übersprungen und im Bericht gezählt, nie still ersetzt (wie `TextBoxCharacters`/`FormCharacters`).
- **Eigene Marke:** Die Ebene trägt eine Marke, an der Pagewing sie wiedererkennt (Vorschlag: markierter Inhalt im Seiteninhalt oder ein Schlüssel am Objekt; **[Spike]**), nur eigene Ebenen werden im Modus „ersetzen“ entfernt. Fremde unsichtbare Textebenen (von Acrobat, OCRmyPDF) gelten als Text und werden nie angefasst.
- **Das Spike prüft auch das Zusammenspiel mit M13 und M14:** Die Marke und die Ebene überstehen (a) einen Neuschrieb mit `FPDF_ImportPagesByIndex` (Seiten organisieren), (b) das Entfernen von Textobjekten im Schwärzen-Bereich und den vollständigen Neuschrieb von M14 (nur die Wörter im Bereich verschwinden, die Marke der übrigen Wörter bleibt), (c) inkrementelles Speichern und Neuöffnen. Fällt die Marke weg, gilt die Ebene danach als fremder Text, das ist hinzunehmen und im Bericht zu dokumentieren, aber nicht still.
- **Ausgabe des Spikes:** zwei Beispieldateien an Chrome, Acrobat Reader und Pagewing (Handprüfung des Projektinhabers, Ablauf in `packaging/README.md`) und der Eintrag in `DECISIONS.md`.

## 4. Entwurf: Übernahme, Speichern, Rückgängig

- **Die Ebene wird beim Übernehmen in das geöffnete Dokument geschrieben** (PDFium-Seiteninhalt im Speicher, so sehen Suche, Auswahl, Kopieren und Speichern dasselbe) **[Mein Vorschlag]**. Der Tab hält zusätzlich die Wortlisten des Laufs als Verlaufsdaten: **Rückgängig** entfernt die Textobjekte mit der Marke auf den Seiten des Laufs, **Wiederholen** schreibt sie aus den Wortlisten neu. Die Wortlisten gehören zum Verlaufsschritt (zählen zur Obergrenze von 32 MiB) und werden nach dem Speichern verworfen (der Verlauf ist dann leer).
- **Ein Lauf ist eine ungespeicherte Änderung** **[Mein Vorschlag]**, wie eine Annotation oder ein Formularwert: ein Schritt im gemeinsamen Verlauf (`AnnotationCommand` oder ein eigener Befehl; **[Baumeister]**), rückgängig machbar (entfernt die Ebene aller Seiten des Laufs). Speichern ist das normale **inkrementelle** Speichern von M9 (Strg+S, „Speichern unter“); die Originalbytes samt signierter Bereiche bleiben der unveränderte Anfang. Die Signaturwarnung (`SignaturePolicy`) gilt unverändert und nennt, dass Text hinzugefügt wird.
- **Verlauf und Speicher:** Das Ergebnis (Wortlisten) ist klein (Kilobyte je Seite); die Obergrenze des Rückgängig-Stapels (M12: 32 MiB) gilt unverändert, ein Lauf, der sie sprengt, wird **vor** der Änderung abgelehnt (`AnnotationHistoryFullException`, „Bitte zuerst speichern“). Bis zum Speichern hält der Tab die Wortlisten; ein Dokument mit 2.000 Seiten ist ein eigener Messfall.
- **Nach der Übernahme:** Seitentext-Zwischenspeicher, Suche, Auswahl und Textschicht der Ansicht werden für die betroffenen Seiten neu gelesen (`InvalidatePages`; die Suche startet neu wie nach dem Speichern). Das Bild der Seite ändert sich nicht, also kein Neuzeichnen nötig.
- **Sperren:** Während ein Lauf läuft, ist die Seite lesbar (Scrollen, Suchen); Bearbeiten, Organisieren, Schwärzen, Speichern und Drucken sind gesperrt (Grund sichtbar, `SaveBlockedReason`-Muster), Tab- und Fensterschluss sowie ein Update fragen oder brechen ab wie bei den anderen langen Aufträgen. Die gleichzeitige Verwendung mit dem Modus „Organisieren“ oder „Schwärzen“ ist ausgeschlossen (wie dort gegenseitig).
- **Verschlüsselte Dateien:** erlaubt (inkrementelles Speichern verschlüsselter Dateien gilt wie in M9: bleibt verschlüsselt oder wird abgelehnt). Ausnahmen entscheidet die Prüfung des Speicherns, nicht OCR.
- **Abbruch:** Esc oder „Abbrechen“ in der Karte. Bereits erkannte Seiten bleiben **auswählbar** („Fertige Seiten übernehmen“ / „Verwerfen“, Standard „Verwerfen“); nie wird ein Teilergebnis ohne Frage geschrieben.

## 5. Entwurf: Auslieferung

### 5.1 Native Bibliotheken **[Entscheidung: selbst bauen]**

- **Tesseract 5.5.x samt Leptonica wird selbst gebaut** (feste Version, Quellen mit Prüfsumme), je Betriebssystem: Windows (`win-x64`, MSVC, auf dem Windows-Runner) und Linux (`linux-x64` im Container `ubuntu:22.04`, damit die Mindest-glibc 2.35 und `check-binary --max-glibcxx 3.4.30` gelten). Eine einzige Bibliothek je System (`tesseract` mit statisch eingebautem Leptonica), **ohne OpenMP** (keine `vcomp140.dll`, Parallelität machen wir selbst, siehe 3.2) und **ohne** die optionalen Teile für Netz, Archive und Bildformate (`libcurl`, `libarchive`, Bilddecoder: die Eingabe ist ein roher Pixelpuffer, nichts wird von Tesseract gelesen).
- **Wo gebaut wird [Mein Vorschlag]:** In einem eigenen, von Hand startbaren Workflow (nicht bei jedem Lauf), dessen Ergebnis als Release-Assets des Repos liegt. **Das Release ist eine Vorabversion mit einem Tag ohne `v` (`native-tesseract-<Version>-<n>`)**: Die Update-Prüfung fragt `releases/latest` ab (`UpdateTrust.ReleasesLatestUrl`), das Vorabversionen und Entwürfe auslässt, und `release.yml` verlangt einen SemVer-Tag `vX.Y.Z`; ein Release mit Tesseract-Assets darf beides nie berühren (Test: der Tag passt nicht auf `UpdateVersion.TryParse` und das Release ist `prerelease`). Abruf mit `gh` und `GH_TOKEN` (das Repo ist privat; der Workflow-Token reicht für das eigene Repo), die Prüfsumme steht im Quelltext. Die Windows-Bibliothek baut ein Lauf auf `dotnet-windows` oder einem gehosteten `windows-2025`-Runner (Wahl **[Baumeister]**, beide haben MSVC), die Linux-Bibliothek der Container `ubuntu:22.04`. **Der Bau ist ein eigener Schritt mit Tor:** Scheitert er (statisches Leptonica, ohne OpenMP, ohne curl/archive, C-API-Export), folgt nichts darauf; Ausweg wäre eine nur für die Entwicklung vorgebaute Bibliothek, die nie ins Paket kommt. `tools/package.cs` (neuer Befehl, Muster `runtime-fetch`) und die Tests holen sie mit **festgehaltener SHA-256** (Version und Prüfsumme stehen im Quelltext wie bei `InnoSetup` und `AppImageRuntime`). Ein Update der Version ist eine neue Prüfsumme samt `DECISIONS`-Eintrag. Wie das Repo-intern organisiert wird, ist **[Baumeister]**; ein neues Repo wird nur auf Anweisung angelegt.
- **Paket:** Die Bibliothek liegt neben der Anwendung (wie `pdfium`), im Paketlayout von App **und** CLI (`cli/`). Die Sprachdaten liegen unter `tessdata/` neben der Anwendung. Größe der Bibliothek und Auswirkung auf die Paketgröße **messen** und im Eintrag festhalten (nicht bekannt).
- **Lizenzen:** Tesseract (Apache-2.0), Leptonica (BSD-2-Clause) und die Sprachdaten kommen in `third-party/licenses.json` mit Lizenzdatei, die `ThirdPartyNotices.txt` wird neu erzeugt (`licenses check` in der CI). Alles, was statisch eingebaut wird, steht ebenfalls drin; die Wahl der abgeschalteten Teile in 5.1 hält diese Liste kurz. Die Lizenzangaben stammen bislang aus dem Gedächtnis des Entwurfs und sind beim Bauen gegen die Quellen zu prüfen **[Baumeister]**.
- **Prüfung:** `check-binary` (NEEDED-Bibliotheken, glibc/GLIBCXX) für die Tesseract-Bibliothek; ein Test sucht in der gebauten Bibliothek nach Netzfunktionen (`curl_`), die fehlen müssen.

### 5.2 Eigenheiten von Tesseract, auf die der Plan achten muss

- **Kein Netz, keine Dateizugriffe außerhalb der Wahl:** Konfigurationsvariablen von Tesseract (`TESSDATA_PREFIX`, `debug_file`, `tessedit_write_*`, Dumps) dürfen vom Prozess-Umfeld nicht beeinflusst werden; Pagewing setzt `datapath` ausdrücklich, übergibt Optionen nur aus einer festen Liste und schreibt nie Debug- oder Hilfsdateien.
- **Threads:** Eine Instanz gehört einem Worker; `OMP_THREAD_LIMIT` ist ohne OpenMP ohne Wirkung, der Bau (5.1) soll das sicherstellen. Aufrufe in nativen Bibliotheken starten nie vor der `AppContext`-Initialisierung neue Threads (gilt auch für Worker beim Programmstart in der CLI).
- **Speicher je Instanz** ist nicht gemessen; die Messung entscheidet die Obergrenze der Worker. Ein Seitenbild im Arbeitsspeicher belegt Breite × Höhe Byte (Graustufen); die Rückstau-Grenze (3.2) hält die Summe klein.
- **Die Zeigerführung der Bindings** folgt den Regeln der anderen Bindings: `SafeHandle`, Ownership von Strings (`TessDeleteText`, Iteratoren löschen), keine Rückrufe von Tesseract in verwalteten Code (kein neuer `[UnmanagedCallersOnly]`; falls ein Fortschrittsrückruf von Tesseract nötig scheint, gilt die Regel „Rückrufe aus PDFium“ sinngemäß, Eintrag und Wächtertest `UnmanagedCallbackTests`). Fortschritt wird zwischen den Seiten gemeldet, nicht innerhalb einer Seite.
- **Wortlesen** über den Ergebnis-Iterator (Wortgrenzen, Konfidenz); ob Leptonica-Funktionen (`GetWords` liefert Leptonica-Typen) gebraucht werden, ist **[Spike]** und soll, wenn möglich, vermieden werden.

## 6. Entwurf: Oberfläche und CLI

### 6.1 App

- **Hinweisleiste „Das sieht wie ein Scan aus. Durchsuchbar machen?“** erscheint **einmal je Tab und Dokument** nach dem ersten scharfen Bild, wenn die ersten (Vorschlag: bis zu 5, mindestens 1 aber höchstens 20 % der Seiten, **[Baumeister]**) Seiten keinen Text, aber ein Bild über den größten Teil der Seite haben. Sie schließt sich mit „Nicht jetzt“ und „Für diese Datei nicht mehr fragen“ (nur im Speicher des Tabs, nichts auf der Festplatte). Sie startet nie von selbst. Die Erkennung läuft im Hintergrund in kurzen Aufträgen, nach `HasShownSharpPage`, nie im Startpfad, in Messläufen aus (wie `FormFeaturesEnabled`).
- **Befehl `ocr.run`** („Texterkennung (OCR) …“, Kürzel **[Baumeister]**, nur im `CommandCatalog`) öffnet die **Karte** (`OverlayKind` oder Karte wie bei M13/M14; entsteht erst beim Gebrauch): Sprachen (3.3), Seiten (Standard: alle ohne Text; wählbar „aktuelle Seite“, „Bereich“), Modus (Seiten mit Text überspringen / eigene Ebene ersetzen / auch Seiten mit Text), Auflösung als Auswahl, kurz erklärt, **nicht** als freie Zahl. Die Karte nennt vorher, **was geschieht**: Anzahl Seiten, geschätzte Dauer (nur als Spanne, nach einer Messung der ersten Seite, nie vor dem Start erfunden), „das Bild bleibt unverändert“, „Fehler sind möglich; die Erkennung ist nicht fehlerfrei“.
- **Lauf:** Fortschrittsanzeige (Seiten von Seiten, ungefähre Restzeit erst nach drei Seiten) in der Karte und in der Statuszeile des Tabs, Abbruch mit Esc/Knopf. Bildschirmleser-Ansagen für Start, Fortschritt in Schritten und Ende (`AutomationProperties`).
- **Ergebnis:** Karte mit Bericht: erkannte Seiten, Wörter, mittlere Konfidenz je Seite und insgesamt, Seiten mit **niedriger Konfidenz** (Schwelle **[Baumeister]**, im Text „könnte gedreht oder schlecht lesbar sein – drehen und erneut versuchen“, mit Sprung zur Seite), übersprungene Seiten mit Grund (hatte Text, zu groß, Zeitgrenze, unbekannte Zeichen). Der Text sagt nie „korrekt“ oder „vollständig“.
- **Panel/Leiste:** keine Dauerelemente; der Befehl steht in der Befehlspalette und im Menü der Datei-/Seitenaktionen **[Baumeister]**.
- **Barrierefreiheit und Darstellung:** wie die übrigen Karten (Fokusführung, Hochkontrast, „Kompakt“, 150 % und 200 %, Bildschirmleser); Zustände nie nur über Farbe.

### 6.2 CLI

`pagewing ocr <Datei> --output <aus.pdf> [--language <deu+eng>] [--tessdata <Ordner>] [--pages <…>] [--mode skip|redo|force] [--dpi <150-300>] [--jobs <n>] [--force] [--password …]`

- Folgt den Regeln der schreibenden Befehle (M13/M14): `OutputGuard` (Dateiidentität, `--force` ersetzt nie eine Eingabe), `RewriteRequest.ReplaceExisting = false` ohne `--force`; die CLI schreibt **nur inkrementell** (Original bleibt der exakte Anfang, signierte Bereiche und Dateigröße bleiben schonend; derselbe Weg wie das Speichern der App, `DocumentSaver.SaveAsync` mit Präfixprüfung). Der Neuschrieb-Modus der Prüfkette (`CompareText`) kommt für OCR nicht vor.
- Bericht auf stderr (Seiten, Wörter, mittlere Konfidenz, übersprungene Seiten mit Grund, niedrige Konfidenz), nie der erkannte Text; Ausgabe englisch und stabil. Exit 1 bei Fehlern, 0 auch bei übersprungenen Seiten (die stehen im Bericht); ein Lauf, bei dem **keine** Seite erkannt wurde, ist Exit 1.
- `--dry-run`: zeigt, welche Seiten erkannt würden und welche Auflösung, ohne Tesseract zu laden **[Mein Vorschlag]**.

## 7. Tests und Abnahme

**Fixtures** (in `tools/make-testpdfs.cs`, deterministisch, in `RenderCorpus.Skipped`, in `LICENSES.md`): `ocr-scan.pdf` (zwei bis drei Seiten, jeweils **nur ein Bild**: gerenderter Text mit bekannten Wörtern an bekannten Stellen, eine Seite mit deutschen Umlauten, eine mit zweispaltigem Layout), `ocr-scan-rotated.pdf` (`/Rotate 90` und CropBox-Versatz), `ocr-mixed.pdf` (eine Seite mit echtem Text, eine als Scan), `ocr-hostile.pdf` (riesige Seite, Seite mit sehr vielen winzigen Wörtern). Kein echter Scan privater Dokumente.

**Abnahmekriterien** (Entwurf; die Roadmap bekommt sie zusätzlich zu den zwei bestehenden):
1. Nach OCR findet die Suche die bekannten Wörter von `ocr-scan.pdf`, und die Trefferrechtecke liegen innerhalb einer Toleranz (Vorschlag: höchstens ein halbes Zeichenhoch Versatz) an den bekannten Positionen; gilt auch für die gedrehte Seite und die CropBox (Test).
2. Das Seitenbild ist nach Anwenden und Speichern **bitgleich** (Hash der Bildstreams) und die Seite rendert pixelgleich zu vorher (Test mit Render-Vergleich, Strict-Modus).
3. Eine Seite mit Text wird im Modus „überspringen“ nicht angefasst; im Modus „ersetzen“ gibt es nach zweimaligem Lauf genau **eine** Ebene (kein doppelter Treffer).
4. Abbruch lässt Datei und Tab unverändert (oder wählbar das Teilergebnis); Original byteweise gleich, bis gespeichert wird (Test).
5. Nach dem inkrementellen Speichern sind die Originalbytes der exakte Anfang der Datei (Test wie `FormSaveTests`).
6. **Zusammenspiel mit Schwärzen:** OCR, dann M14-Schwärzen eines Wortes aus dem Scan entfernt Pixel **und** die Textebene; die Prüfung von M14 besteht (Test; schließt den M14-Backlogpunkt „Text in Bildern erkennen“ für Scans). Ein geschwärztes OCR-Wort zählt als erwartete Entfernung (`RemovedText` nimmt den extrahierten Text der Marke, auch wenn er unsichtbar ist).
7. Hostile Eingaben: `ocr-hostile.pdf` führt zu gemeldeten Übersprüngen, nicht zu Absturz, unbegrenztem Speicher oder blockiertem `PdfiumThread` (Test mit Grenzen).
8. Nichts im Startpfad: Tesseract wird nicht geladen, bevor ein Lauf startet (`OcrStartPathTests`); die Startzeit-Budgets bleiben (Startzeit-Check auf echter Hardware **offen**, wie in M14).
9. CLI: `RunAsync`-Tests für Erfolg, Ablehnungen, `--force`, `--dry-run`, Exit-Codes; `smoke-cli` am AOT-Binary mit einem `ocr`-Aufruf (die Bibliothek liegt neben dem Binary).
10. Paket: `check-binary` bestanden, `licenses check` grün, Bibliothek ohne Netzfunktionen, Prüfsumme der Sprachdaten.

**Hinweis zu Tests ohne Bibliothek [Mein Vorschlag]:** Lokal melden sich OCR-Tests ohne die Bibliothek als übersprungen; die CI holt sie und setzt `PAGEWING_REQUIRE_OCR=1`, dann ist ein Fehlen ein Fehler (nichts wird nur zum Grünwerden übersprungen). Ergebnisse von Tesseract schwanken mit der Version: Die Version ist fest, die Tests prüfen Wörter und Lage mit Toleranz, nie die Konfidenz auf Dezimalstellen.

**Messungen** (nur mit `PAGEWING_MEASURE_OUT`, prüfen nie eine Zeit): Seiten je Sekunde, Spitzenspeicher je Instanz, Größe der Bibliothek, Zeit bis zum Laden der Bibliothek, Wirkung auf die Dateigröße je Seite, Verhalten bei 2.000 Seiten.

**Plattformen [Mein Vorschlag]:** Wie in M13 und M14 ist die Abnahme auf dem Entwicklungsrechner Windows; die Linux-Bibliothek wird im Container gebaut und die Tests laufen auf dem Runner `dotnet-linux` (CI nach dem Merge auf `dev`). Läuft dort etwas nicht, ist das offen und steht mit dem Befehl in der Roadmap, nie als erledigt.

**Handprüfung (Projektinhaber, in `packaging/README.md`):** Muster in Chrome, Acrobat Reader und Pagewing (Auswahl, Suche, Kopieren, Trefferlage, auch gedrehte Seite, CropBox); Linux-Läufe (`*Ocr*`, `*Cli`, `check-binary`); Karte hell, dunkel, Hochkontrast, 150 % und 200 %, mit Bildschirmleser; ein echter, eigener Scan; Startzeit-Check auf dem Haupt-PC.

## 8. Verhältnis zu M16 (Text bearbeiten)

M16 braucht Schriften, Ersatzschriften und das Schreiben von Text in Seiten. Dass das Spike zur Textebene (3.4) Schrift, Unicode-Zuordnung und PDFium-Textobjekte klärt, ist Vorarbeit; die Ergebnisse stehen in `DECISIONS.md`, damit M16 darauf aufbauen kann. M16 wird darüber hinaus **nicht** in dieser Spec vorweggenommen.

## 9. Dokumentation, die M15 mitzieht

- `Docs/DECISIONS.md`: Einträge zu Auslieferung (Bau, Version, Prüfsumme), Textebene (Spike-Ergebnis), Sprachdaten und Grenzen, Messwerte.
- `CLAUDE.md`: Block „OCR (seit M15)“ mit Wo-was-liegt und Stolperfallen; Anpassung der Angaben zu `Pagewing.Ocr`.
- `README.md` (Syntax `pagewing ocr`), `CHANGELOG.md` (`[Unreleased]`), `packaging/README.md` (Handprüfung, Bau der Bibliothek), Roadmap (`roadmap.json` samt erzeugter `ROADMAP.md`, Scope-Änderungen, Backlog: Sprachdatei-Download, OSD, Prüfmodus).

## 10. Offen für den Baumeister

- Ob `Pagewing.Core.Ocr` eigene Befehls-Typen im Verlauf hat oder `AnnotationCommand` erweitert wird.
- Namen und Kürzel (`ocr.run`), Platz des Befehls im Menü, Schwellen (Konfidenz, Zeitgrenze, Pixelgrenze, Wortgrenze).
- Aufteilung in Milestone-Aufgaben und Reihenfolge; vorgeschlagen: (1) Spike Textebene und Bibliothek bauen, (2) Bindings und Engine, (3) Kern, Textebene und Speichern, (4) CLI, (5) App, (6) Paket, Lizenzen, Messung, Dokumentation.
