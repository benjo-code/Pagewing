# Teilprojekt 3 – Seiten organisieren (M13): Design

Stand: 2026-10-31 · Status: Entwurf zur Freigabe · Gehört zu [Gesamt-Spec](2026-10-16-pagewing-design.md), Abschnitt 6 · Roadmap: Milestone 13

Diese Spec gilt für **M13 (Seiten organisieren)**. M14 (Schwärzen) bekommt vor seinem Plan einen eigenen Abschnitt oder eine eigene Spec; hier steht dazu nur, was M13 dafür bereitstellt (Abschnitt 11).

Kennzeichnung: **[Entscheidung]** hat der Projektinhaber getroffen oder bestätigt. **[Mein Vorschlag]** ist eine Annahme von Claude, die der Projektinhaber beim Lesen prüfen soll. **[Baumeister]** überlässt der Plan der Umsetzung, mit der genannten Grenze.

## 1. Absicht

Pagewing soll Seiten so umbauen können, wie man es von Acrobat kennt (umsortieren, drehen, löschen, einfügen, zusammenführen, teilen, als Bild exportieren), aber schneller, ohne Konto und ohne stillen Datenverlust. Der Anlass für die Sorgfalt: Die Recherche (Abschnitt 2) zeigt, dass gerade Acrobat beim Zusammenführen Formularfelder gleichen Namens still verkoppelt und Lesezeichen verliert. Dieses Verhalten ist der Maßstab, den Pagewing nicht erreichen soll.

Erfolg heißt: Eine Datei mit Lesezeichen, Formular und Seitenbeschriftungen kommt nach „Seiten drehen, löschen, umsortieren“ mit allem zurück, was nicht ausdrücklich betroffen ist, und was verloren geht, sagt die App **vorher**.

## 2. Befund der Wettbewerbs- und Technikrecherche (2026-10-31)

Belege sind teils dünn (Nutzermeinungen zur Bedienung sind selten, PDFiums Verhalten beim Import ist nirgends dokumentiert). Das ist der Grund für den Spike in Abschnitt 6.

- **Acrobat:** Organisieren per Miniaturen funktioniert; beim Zusammenführen koppeln Felder gleichen Namens und gehen Lesezeichen verloren (Adobe-Community, mehrere Threads).
- **PDFsam/Sejda:** Seitenbereiche je Datei (`1-10,14,25-`); drei Lesezeichen-Optionen beim Zusammenführen (zusammenführen, verwerfen, neu mit einem Eintrag je Datei); Teilen nach Lesezeichen-Ebene.
- **PDF Arranger (pikepdf/QPDF), Stirling-PDF:** Miniaturenraster mit Ziehen, Rückgängig/Wiederholen, Seite duplizieren, Export von Seiten als Datei oder Bild, Ziehen zwischen Fenstern.
- **QPDF:** Lesezeichen, Formularfelder und Artikel-Tags kommen nur aus der **ersten („primären“) Datei**, Seitenbeschriftungen aus allen; beim Teilen gehen Lesezeichen verloren. Die C-API kann Seiten aus anderen Dateien übernehmen, hat aber keine Outline-Funktionen (Zugriff nur über Objekt-Handles).
- **PDFium (ausgelieferte Version 157.0.8100):** `FPDF_ImportPagesByIndex` (`fpdf_ppo.h`), `FPDF_MovePages` (`fpdf_edit.h`) und die Seitenfunktionen für Löschen, Drehen und Anlegen sind vorhanden; Lesezeichen kann PDFium nur lesen. Was der Import von Annotationen, Links, Formularfeldern, Lesezeichen und Seitenbeschriftungen mitnimmt, ist nicht dokumentiert. In `Pagewing.Pdfium` gebunden sind bisher nur `FPDF_CreateNewDocument` und `FPDF_SaveAsCopy`.

## 3. Ziele, Nicht-Ziele, Anti-Ziele

**Ziele (M13)**
- Seiten in der Seitenleiste umsortieren, drehen, löschen, duplizieren, leere Seiten und Seiten aus anderen PDFs einfügen. Rückgängig/Wiederholen.
- PDFs zusammenführen (über „Datei einfügen“ und die CLI) und teilen (nach Bereichen, alle *N* Seiten, nach Lesezeichen der obersten Ebene).
- Ausgewählte Seiten als neue PDF extrahieren und als Bilder (PNG) exportieren.
- CLI: `merge`, `split`, `rotate`, `export`.

**Nicht-Ziele (M13)** – alle als Backlog-Einträge, nicht nebenbei beginnen
- Zuschneiden, Wasserzeichen, Kopf-/Fußzeilen, Seitenzahlen, Seitenbeschriftungen bearbeiten, Bilder als neue Seiten einfügen, JPEG-Export (Core hat nur einen PNG-Encoder; ein JPEG-Encoder wäre eine neue Abhängigkeit oder Eigenbau), eigener Dialog „Dateien zusammenführen“ mit Dateiliste, Ziehen von Seiten zwischen Tabs oder Fenstern, Metadaten bearbeiten.
- Nichts an Annotationen, Formularen oder Unterschriften neu erfinden.

**Anti-Ziele (das wäre ein Fehlschlag, auch wenn alles „funktioniert“)**
- Lesezeichen, Formularfelder oder Seitenbeschriftungen gehen verloren oder werden verkoppelt, **ohne dass die App es vorher sagt**.
- Das Original wird angefasst, bevor die neue Datei geprüft ist.
- Eine verschlüsselte Datei wird still unverschlüsselt gespeichert.
- Der `PdfiumThread` ist für Rendern und Scrollen lange blockiert, oder der UI-Thread wartet auf PDFium.
- Die Startzeit-Budgets leiden (nichts Neues im Startpfad).

## 4. Rahmen aus dem Projekt (gilt, ohne dass es neu entschieden wird)

- Alles PDFium nur über den `PdfiumThread`; Aufträge kurz und in Teilschritten (Importe in Stapeln, `PdfiumPriority.Background`), Abbruch zwischen den Schritten. Ein Speichern bleibt ein Auftrag (bekannte Grenze aus M9).
- Native AOT, eigene `[LibraryImport]`-Bindings nur für gebrauchte Funktionen, Konstanten gegen die Header der ausgelieferten Version prüfen.
- Seitenoperationen erzwingen einen **vollständigen Neuschrieb** (`FPDF_NO_INCREMENTAL`), nie inkrementell; Warnung bei signierten Dokumenten (Gesamt-Spec 5.x).
- Atomar speichern: temporäre Datei im Zielordner, wieder öffnen und prüfen, erst dann ersetzen. Kein stilles Speichern.
- UI-Texte Englisch und Deutsch (resx), Kürzel nur im `CommandCatalog`, Design nach Design-Spec, keine Zustände nur über Farbe, Tastaturbedienung ohne Tastaturfalle (Tab fängt den Fokus nie).
- Leseansicht zeigt keine Bearbeitungswerkzeuge: die Werkzeuge erscheinen nur im Modus „Organisieren“ (Abschnitt 5.3).
- Tests schreiben nie neben die Eingabedateien in `testdata` und nie in den echten Benutzerordner.

## 5. Entwurf

### 5.1 Modell: ein Plan, nichts wird sofort geschrieben **[Entscheidung]**

Seitenänderungen werden in der Seitenleiste **vorgemerkt** und erst mit „Anwenden“ geschrieben. Die Hauptansicht zeigt bis dahin das unveränderte Dokument. Begründung: Annotationen, Suche, Formulare, Layout und Cache hängen an Seitennummern der geöffneten Datei; sie live zu verschieben wäre ein Umbau der ganzen Ansicht.

Core (UI-frei, neuer Ordner `Pagewing.Core.Pages`; Namen **[Baumeister]**):
- Ein **`PageEditPlan`** ist eine geordnete Liste von Einträgen. Jeder Eintrag verweist auf eine Quellseite (Quelle = das geöffnete Dokument oder eine eingefügte Datei, Seitenindex), trägt eine zusätzliche Drehung (0/90/180/270) oder ist eine leere Seite mit Größe. Derselbe Quellindex darf mehrfach vorkommen (Duplizieren).
- Der Plan ist unveränderlich oder wird als Schnappschuss gehalten; ein **Verlauf** aus Plänen liefert Rückgängig/Wiederholen billig. Der Plan weiß nichts von PDFium.
- Ein **Ausführer** baut aus dem Plan das Ergebnis (5.2).

### 5.2 Ausführen: auf einer Arbeitskopie des Dokuments, nicht per Neuaufbau **[Entscheidung, Technik]**

Der entscheidende Grundsatz: **Operationen am geöffneten Dokument laufen auf einer Arbeitskopie desselben Dokuments** (Löschen, Verschieben, Drehen in der Kopie), nicht durch Kopieren aller Seiten in ein leeres Dokument. Nur so bleiben Lesezeichen, AcroForm, Seitenbeschriftungen und Metadaten der Datei erhalten. Beim Zusammenführen ist die **erste Datei die primäre** (wie bei QPDF); Seiten aus anderen Dateien kommen per Import dazu.

Ablauf (die Reihenfolge der Schritte ist **[Baumeister]**, die Invarianten nicht):
1. Arbeitskopie aus dem Zustand öffnen, den das Speichern des Tabs heute benutzt (`ViewerSession.SnapshotForSave`, Passwort aus der Sitzung). **Invariante:** Ungespeicherte Annotations- oder Formularänderungen des Tabs gehen nie still verloren. **[Mein Vorschlag, festgelegt nach der Prüfung]** Voreinstellung ist „erst speichern“: Sind beim Anwenden Annotations- oder Formularänderungen ungespeichert, fragt eine Karte, ob sie zuerst (inkrementell, über den bestehenden Speicherweg) gespeichert werden sollen; erst danach läuft der Neuschrieb. Das Einfließen der Änderungen in die Arbeitskopie (Verlauf nachspielen, Seitenindizes umrechnen) ist **nicht** Teil von M13 und kommt als Backlog-Eintrag. Wer ablehnt, bleibt im Modus mit offenem Plan.
2. Fremde Seiten importieren (Stapel), nicht benötigte Seiten löschen, umsortieren (`FPDF_MovePages`), drehen, leere Seiten anlegen, Duplikate importieren (Import aus dem eigenen Dokument ist im Spike zu prüfen).
3. Vollständig neu schreiben in eine temporäre Datei, dann dieselbe Prüfkette wie `DocumentSaver` (Quelle unverändert, Ziel schreibbar, Neuöffnen, Prüfrendern, atomares Ersetzen) – mit einem **neuen Modus „vollständiger Neuschrieb“**: die Präfixprüfung der Originalbytes entfällt (sie gilt nur inkrementell), dafür gilt: Seitenzahl = Länge des Plans, Reihenfolge und Drehung stimmen (Prüfmerkmal je Seite **[Baumeister]**), Verschlüsselung bleibt erhalten.
4. Das geprüfte Dokument wird das Dokument des Tabs (`AdoptSavedDocumentAsync`), aber mit **neuer Cache-Instanz**, weil sich die Seiten ändern (heute heißt „dieselbe Cache-Instanz“ = Dokument gleicher Seiten). Annotationsverlauf und Auswahl sind danach leer, Position und Zoom bleiben so weit sinnvoll (die aktuelle Seite folgt ihrem Eintrag im Plan, sonst die nächste vorhandene).

Ergebnis der Prüfkette bei Fehlern: Ziel byteweise unverändert, temporäre Datei weg, Plan bleibt erhalten (der Nutzer verliert die vorgemerkten Änderungen nie durch einen Fehler).

### 5.3 Oberfläche **[Mein Vorschlag], Feinheiten [Baumeister] nach Design-Spec**

- **Modus „Organisieren“ im Panel „Seiten“.** Ein Knopf (und ein Kürzel im `CommandCatalog`) schaltet die Miniaturenliste in den Organisiermodus: Mehrfachauswahl (Strg/Umschalt), eine Werkzeugzeile (Links drehen, Rechts drehen, Löschen, Duplizieren, Leere Seite einfügen, Datei einfügen …, Auswahl extrahieren …, Rückgängig, Wiederholen, **Anwenden**, **Verwerfen**), Ziehen zum Umsortieren mit Einfügemarke, Kontextmenü. Außerhalb des Modus bleibt die Liste unverändert (Navigation).
- **Vorgemerkte Änderungen sichtbar, nie nur über Farbe:** Symbol + Text für gedrehte Seiten, neue und leere Seiten und Seiten aus anderen Dateien; gelöschte Seiten verschwinden aus der Liste, ein Zähler zeigt „12 → 10 Seiten“. Das Rückgängig bringt sie zurück. Die Miniaturen kommen von der `ThumbnailSource` des Tabs (Quellindex), die Drehung wird beim Zeichnen angewandt; eingefügte Dateien haben eine eigene Quelle; leere Seiten werden gezeichnet, nicht gerendert.
- **Tastatur:** Alles ohne Maus erreichbar (Auswahl, Verschieben um eine Position, Löschen, Drehen, Einfügen, Anwenden, Verwerfen). Tab fängt den Fokus nie. Bild auf/ab und Esc nach den bestehenden Regeln der Miniaturenliste. Konkrete Kürzel **[Baumeister]**, nur im `CommandCatalog`.
- **Sperren, solange ein Plan offen ist [Mein Vorschlag]:** Der Annotations- und Formularmodus ist gesperrt (Begründung sichtbar: „Seitenänderungen warten auf Anwenden“), wie bei der Update-Sperre. Das verhindert, dass Annotationen an Seitenindizes hängen, die sich gleich ändern. Drucken und Speichern bleiben, aber Speichern (Strg+S) mit offenem Plan öffnet die Anwenden-Karte.
- **Anwenden-Karte [Mein Vorschlag]:** Zeigt die Zusammenfassung („12 → 10 Seiten, 3 gedreht, 1 Datei eingefügt“), die **Verlustliste** (siehe 5.5), und bietet „Speichern unter …“ (**Standardknopf, hat den Fokus**), „In der Originaldatei speichern“ und „Zurück“. **[Mein Vorschlag, festgelegt nach der Prüfung]** Grund: Ein vollständiger Neuschrieb ist nicht rückgängig zu machen (gelöschte Seiten und alte Fassungen sind danach aus der Datei), anders als das inkrementelle Speichern. Die Karte sagt das in einem Satz. Eine Sicherungskopie legt Pagewing nicht an (das wäre ein stilles Schreiben); „Speichern unter …“ ist der Weg dazu. Kein Auto-Speichern. Mindestens eine Seite muss bleiben (Löschen der letzten Seite ist abgelehnt mit Grund).
- **Schließen/Update:** Ein offener Plan zählt als ungespeicherte Änderung (`AsksBeforeClosing`, Rückfrage „Seitenänderungen verwerfen?“, `ResolveUnsavedChangesAsync` für den Update-Weg).
- **Datei einfügen:** Dateidialog (vorhandener Öffnen-Dialog), bei verschlüsselter Datei die vorhandene Passwortabfrage, danach eine Karte mit Seitenbereich der eingefügten Datei (Syntax von `PageRange`, die auch das CLI nutzt) und Einfügeposition (nach der Auswahl, sonst am Ende).
- **Extrahieren/Teilen/Exportieren:** Karten für „Auswahl als neue PDF“, „Teilen …“ (alle *N* Seiten / Bereiche / nach Lesezeichen der obersten Ebene, Zielordner) und „Als Bilder exportieren …“ (PNG, Auflösung, Zielordner, Auswahl oder alle). **[Mein Vorschlag, festgelegt nach der Prüfung]** Sie lesen immer das **geöffnete Dokument ohne Plan**; die Seitennummern und die Auswahl sind die der Liste ohne vorgemerkte Änderungen. Solange ein Plan offen ist, sind sie gesperrt, mit sichtbarem Grund („Erst anwenden oder verwerfen“) – es gibt also keinen Ausführer-Modus „Plan in neue Datei schreiben“ in M13. Ebenso sind sie gesperrt, solange ungespeicherte Annotations- oder Formularänderungen des Tabs bestehen (Grund: „Erst speichern“), damit nie eine Datei ohne die sichtbaren Änderungen entsteht und nichts still fehlt. Sie schreiben nur neue Dateien und lassen das Original unberührt. Fortschritt und Abbruch.

### 5.4 Zusammenführen, Teilen, Export: Regeln

- **Zusammenführen (App):** über „Datei einfügen“ in einem geöffneten Dokument; ein eigener Dialog mit Dateiliste ist Backlog. **CLI:** Abschnitt 5.6.
- **Seitenbereiche** überall in der Syntax von `Pagewing.Core.Pdf.PageRange` (`3`, `2-5`, `1,3,5-7`, `all`); Grenzfälle (Doppelungen, Rückwärtsbereiche, außerhalb) **[Baumeister]** nach dem bestehenden Verhalten.
- **Teilen nach Lesezeichen:** nutzt `GetOutlineAsync`; Dateinamen aus Lesezeichentiteln werden bereinigt (kein Pfadtrenner, keine reservierten Windows-Namen, Länge begrenzt, Eindeutigkeit durch Nummer, nie ein Name, der aus dem Zielordner herauszeigt). Nie eine vorhandene Datei überschreiben ohne ausdrückliche Wahl.
- **Bilderexport:** PNG über die vorhandene Render-Pipeline (`PngEncoder`, gleiche Benennung `<Name>-p<NNN>.png` wie `pagewing render`), hell, mit Annotationen und Formularfeldern wie beim Drucken; Obergrenze für die Pixelzahl je Seite gegen feindliche Seitengrößen. JPEG ist Backlog (ich hatte im Gespräch „PNG/JPEG“ genannt; wegen des fehlenden Encoders ist das hier zurückgenommen, **[Mein Vorschlag]**).
- **Obergrenzen gegen feindliche Dateien [Baumeister], Werte festlegen und testen:** Gesamtzahl der Seiten eines Plans (Duplizieren in Schleife), Zahl eingefügter Dateien, Größe der Arbeitskopie im Speicher. Importe in Stapeln mit Abbruch.

### 5.5 Was verloren gehen kann, und was die App darüber sagt **[Entscheidung: vorher sagen, nie still verlieren]**

Die Verlustliste steht in der Anwenden-Karte und im CLI-Ausgabetext. Was sie im Einzelnen enthält, hängt vom Spike ab. Erwartet (zu bestätigen):
- Lesezeichen der **eingefügten** Dateien (nur die der primären Datei bleiben).
- Lesezeichen, die auf **gelöschte** Seiten zeigen (sie zeigen ins Leere; ob PDFium sie entfernen kann, klärt der Spike).
- **Formularfelder gleichen Namens** aus verschiedenen Dateien: die App erkennt die Kollision (Namensindex gibt es seit M11) und meldet sie **vor** dem Anwenden; Acrobat koppelt sie still. Wie die App sie behandelt (umbenennen, ablehnen oder melden und zulassen) hängt davon ab, was PDFium beim Import tut **[Baumeister nach Spike]**; Mindestanforderung: gemeldet, nie still. **[Mein Vorschlag, festgelegt nach der Prüfung]** Dasselbe gilt für das **Duplizieren** einer Seite mit Formularfeldern innerhalb einer Datei: Die Felder behalten ihren Namen, sind also in der Datei ein Feld mit zwei Erscheinungen (so ist es in PDF vorgesehen, und so macht es Acrobat auch); die Anwenden-Karte sagt es („Duplizierte Seiten enthalten Felder: ihr Wert gilt auf beiden Seiten“), Umbenennen ist Backlog. Der Spike prüft, wie PDFium beim Import aus dem eigenen Dokument und bei gleichen Namen aus fremden Dateien tatsächlich schreibt (ein Feld mit mehreren Widgets, zwei getrennte Felder gleichen Namens oder ein Verlust); die Verlustliste beschreibt, was passiert.
- Die **digitale Signatur** eines signierten Dokuments (jede DocMDP-Stufe): Warnkarte vor dem Anwenden, wie beim Speichern; kein Text nennt eine Signatur „gültig“.
- Annotationen/Links, die auf Seiten verweisen, die es nicht mehr gibt.

Verschlüsselte Dateien: Bleibt die Verschlüsselung beim vollständigen Neuschreiben nicht erhalten, wird das Anwenden **abgelehnt** (wie `EncryptedNotSupported` in M9), nie still entschlüsselt gespeichert, und es gibt in M13 auch keinen Weg „entschlüsselt speichern“ (das wäre eine eigene Entscheidung, Backlog). Der Plan bleibt dann erhalten; Extrahieren, Teilen und Zusammenführen aus einer verschlüsselten Datei gelten genauso (Ergebnis nur, wenn die Verschlüsselung nachweislich erhalten bleibt, sonst Ablehnung mit Grund).

### 5.6 CLI **[Mein Vorschlag]**

Gemeinsamer Kern mit der App (`Pagewing.Core.Pages`), Ausgaben englisch und stabil, Exit-Codes `0/1/2`, Passwort aus `PAGEWING_PASSWORD` oder `--password`, Abbruch mit Strg+C. Ausgabedateien werden nie ohne `--force` überschrieben (**jeder** Befehl, der schreibt, kennt `--force`: `merge`, `split`, `rotate`, `export`, `pages`); Eingabe und Ausgabe dürfen nicht dieselbe Datei sein. Die Ausgabe läuft durch dieselbe Prüfkette wie die App (temporär, prüfen, ersetzen).

```
pagewing merge  --output <out.pdf> <file> [--pages <range>] [--password <pw>] <file> [--pages <range>] ... [--force]
pagewing split  <file> --output <dir> (--every <n> | --ranges "1-3,4-9" | --bookmarks) [--force]
pagewing rotate <file> --pages <range> --angle <90|180|270|-90> --output <out.pdf> [--force]
pagewing export <file> [--pages <range>] [--dpi <n>] [--output <dir>] [--no-annotations] [--force]
pagewing pages  <file> --select <spec> --output <out.pdf> [--force]   (optional, nur wenn billig: Reihenfolge/Löschen/Duplizieren/Extrahieren in einem)
```

`export` bedeutet in der CLI **nur** Bilder (wie in der Roadmap); Seiten als neue PDF zu extrahieren geht mit `split --ranges` (oder `pages`). `split --bookmarks` teilt **nur an Lesezeichen der obersten Ebene**: Die Teile beginnen auf den Zielseiten dieser Lesezeichen, sortiert nach Zielseite; ein Teil reicht bis zur Seite vor dem Beginn des nächsten, der letzte bis zum Ende. Seiten vor dem ersten Lesezeichen bilden einen eigenen ersten Teil. Lesezeichen ohne auflösbares Ziel werden übergangen, mehrere Lesezeichen auf derselben Seite bilden einen Teil (Name des ersten). Hat eine Datei kein Lesezeichen der obersten Ebene, ist das ein Fehler (Exit 1) mit Meldung, kein stilles Ergebnis „eine Datei“.

`--pages` und `--password` nach einer Datei gelten für diese Datei (wie bei `qpdf --pages`). `export` schreibt Bilder wie `render` (gleiche Optionen, gleiche Dateinamen); `render` bleibt unverändert, beide teilen die Umsetzung. `--help`-Text, Beispiele und Fehlermeldungen in der CLI-Hilfe ergänzen.

## 6. QPDF: Entscheidung durch einen Spike am Anfang **[Entscheidung]**

QPDF kommt in M13 **nur dazu, wenn der Spike echte Lücken zeigt**. Kosten, falls doch: kein NuGet-Paket; Linux-Bau im Container `ubuntu:22.04` wegen glibc 2.35; Windows-DLL; Lizenzhinweise (Apache-2.0, Eintrag in `third-party/licenses.json` und `ThirdPartyNotices.txt`); eigene Bindings (C-API, Outlines nur über Objekt-Handles); eine zweite Bibliothek im Paket und `check-binary`. Es wird beim Start nie geladen.

Der Spike (erste Aufgabe des Plans, wegwerfbar, mit Testdateien, ohne Produktivcode; Ergebnis als Eintrag in `Docs/DECISIONS.md`) klärt:
- (a) Bleiben Lesezeichen, AcroForm, Seitenbeschriftungen und Metadaten bei Löschen, Verschieben, Drehen auf der Arbeitskopie erhalten? Was geschieht mit Lesezeichen auf gelöschte Seiten?
- (b) Was nimmt `FPDF_ImportPagesByIndex` mit: Annotationen, Links innerhalb der Datei, Formularfelder (und bei gleichen Namen)? Funktioniert der Import aus dem eigenen Dokument (Duplizieren)? Wie verhalten sich Ressourcen (doppelte Schriften/Bilder, Dateigröße)?
- (c) Bleibt eine verschlüsselte Datei beim vollständigen Neuschreiben mit `FPDF_SaveAsCopy` verschlüsselt?
- (d) Zeigen Chrome und `qpdf --check` (nur als externes Prüfwerkzeug im Spike, keine Abhängigkeit) die Ergebnisse ohne Fehler an; Acrobat ist eine Handprüfung (nach `v1.0.0` verschoben wie bei den anderen Fremdviewer-Prüfungen).

**Entscheidungsregel:** QPDF wird ein eigener Eintrag in `DECISIONS.md` (und die Bindings Teil von M13) nur, wenn (a) Lesezeichen/Beschriftungen/Formulare der **primären** Datei bei den Operationen am Dokument selbst beschädigt werden (nicht nur ins Leere zeigen), oder (b) der Import Formulare/Annotationen so verändert, dass die Verlustliste nicht mehr ehrlich benennen kann, was passiert, oder (c) die Verschlüsselung nicht erhalten bleibt und das Nutzer häufig trifft. Auch **strukturelle Fehler im Ergebnis** der Operationen am Dokument selbst zählen als Auslöser unter (a): Meldet `qpdf --check` einen *Fehler* (keine bloße Warnung), etwa ungültige Verweise in `/Outlines`, `/Dests`, `/Names` oder einen kaputten `/PageLabels`-Baum nach dem Umsortieren, oder zeigt Chrome die Datei falsch an, und der Fehler entsteht durch PDFiums Neuschrieb, den Pagewing nicht selbst beheben soll (kein eigener Reparaturcode in M13), dann ist QPDF zu prüfen. Ins Leere zeigende Lesezeichen und verlorene Lesezeichen der eingefügten Dateien sind **kein** Grund, solange die App es vorher sagt und die Viewer keine Fehler melden. Sonst bleibt es bei PDFium; „Lesezeichen der eingefügten Dateien gehen verloren“ steht dann sichtbar in der Oberfläche und in `CHANGELOG.md`/Doku.

## 7. Umfang und Reihenfolge **[Mein Vorschlag]**

Ein Milestone, zwei Stufen in dieser Reihenfolge:
1. **Stufe 1:** Spike und Entscheidung; Bindings (`FPDF_ImportPagesByIndex`, `FPDF_MovePages`, Löschen, Drehen, Seite anlegen, vollständiger Neuschrieb); `PageEditPlan` und Ausführer; Neuschrieb-Modus im `DocumentSaver`; Modus „Organisieren“ mit Drehen, Löschen, Umsortieren, Duplizieren, leere Seite; Anwenden-Karte mit Verlustliste und Signaturwarnung; CLI `rotate`.
2. **Stufe 2:** Datei einfügen und Zusammenführen; Extrahieren und Teilen; Bilderexport; CLI `merge`, `split`, `export` (und `pages`, falls billig).

Wird Stufe 1 deutlich größer als geplant, ist ein Schnitt in M13 und M13.1 möglich (Scope-Änderung in der Roadmap festhalten).

## 8. Tests und Abnahme

Abnahme laut Roadmap: **Tests für alle Seitenoperationen (Seitenzahl, Reihenfolge, Drehung, Inhalt) grün.** Dazu:
- **Neue Testdateien** (erzeugt von `tools/make-testpdfs.cs`, in `testdata/pdfs/LICENSES.md`, in `RenderCorpus.Cases` oder `Skipped`): eine Datei mit Seiten, die ihre Nummer als Text tragen (Reihenfolge und Inhalt prüfbar), mit Lesezeichen auf jede Seite, Seitenbeschriftungen, gemischten Formaten und einer Seite mit `/Rotate`; ein Formular mit gleich benannten Feldern in zwei Dateien; eine kleine verschlüsselte Datei; eine signierte Attrappe gibt es schon (`signed-docmdp-*`).
- **Core-Tests je Operation:** Seitenzahl, Reihenfolge (über den Seitentext), effektive Drehung, Inhalt (Render-Vergleich der unveränderten Seiten), Lesezeichen/AcroForm/Beschriftungen der primären Datei erhalten, Verlustliste vollständig, Originalbytes unverändert bei jedem Fehler, Abbruch, feindliche Pläne (Obergrenzen), verschlüsselt, signiert (Warnung).
- **Plan-Tests:** Verlauf (Rückgängig/Wiederholen) und Zusammenführen von Operationen ohne PDFium.
- **App-Tests (Headless):** Modus, Mehrfachauswahl, Ziehen und Tastatur, Sperre der Annotationsmodi, Anwenden-Karte, Schließen mit offenem Plan, Update-Weg, Tab-Übernahme nach dem Anwenden (Position folgt dem Eintrag), Tastaturbedienung ohne Falle, Texte in beiden Sprachen, kein Text nennt eine Signatur „gültig“ oder etwas „rechtsgültig“.
- **CLI-Tests** über `CliApp.RunAsync` (Exit-Codes, nie überschreiben, Pfade, Passwort).
- **Startzeit:** keine neue Arbeit im Startpfad (Modus, Ausführer, Karten, Bibliotheken entstehen erst beim Gebrauch); der Startzeit-Check der CI bleibt in den Budgets, kein Budget wird gelockert. Messung mit einer Datei von 1000 Seiten im Organisiermodus (Scrollen der Liste) als Information, nicht als Budget.
- **Offen bleiben (Handprüfung, nach `v1.0.0` verschoben wie die anderen Fremdviewer-Prüfungen):** Ergebnisdateien in Acrobat Reader; Linux-Läufe der Speicher-Tests, soweit sie nur dort laufen.
- Build, Test, Format, `roadmap check`, `licenses check`, AOT-Publish von App und CLI mit Befehl und Ergebnis im Nachweis.

## 9. Doku, die M13 mitliefert

`Docs/DECISIONS.md` (Spike-Ergebnis und QPDF-Entscheidung, Modell „Plan statt Sofort-Änderung“, Arbeitskopie statt Neuaufbau, Neuschrieb-Modus der Prüfkette, Verlustliste, Obergrenzen), `CLAUDE.md` (Abschnitt „Seiten organisieren (seit M13)“ mit Wo-was-liegt und Stolperfallen), `CHANGELOG.md` `[Unreleased]`, CLI-Hilfe und README, Roadmap (Plan verlinken, Aufgaben abhaken mit Nachweis), neue Backlog-Einträge für alle Nicht-Ziele aus Abschnitt 3.

## 10. Was dem Baumeister überlassen bleibt

Namen und Zuschnitt der Typen, Reihenfolge der PDFium-Schritte im Ausführer, Stapelgrößen, Obergrenzen (mit Test), Kürzel, genaue Gestaltung des Modus und der Karten nach Design-Spec, Prüfmerkmale je Seite für die Prüfung nach dem Neuschreiben, Umgang mit Randfällen von Seitenbereichen, ob `pages` in M13 kommt.

## 11. Was M13 für M14 (Schwärzen) bereitstellt

Der vollständige Neuschrieb mit Prüfkette (`DocumentSaver`-Modus), die Warnung bei signierten Dokumenten und die Arbeitskopie-Mechanik sind die Grundlage des Schwärzens, das immer vollständig neu schreibt. Die Entscheidung über QPDF aus Abschnitt 6 gilt auch für M14 (Metadaten und Info-Dictionary entfernen, Rohbytes prüfen) und ist dort zu prüfen, falls M13 sie mit „nein“ beantwortet: PDFium kann Info-Dictionary und XMP nicht schreiben.
