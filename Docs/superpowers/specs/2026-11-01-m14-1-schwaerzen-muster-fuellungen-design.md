# Teilprojekt 3 – Schwärzen M14.1 (Muster, Füllungen, Seite als Bild): Design

Stand 2026-11-01. Ergänzt die Spec von M14 ([2026-11-01-m14-schwaerzen-design.md](2026-11-01-m14-schwaerzen-design.md)); deren Rahmen, Anti-Ziele und Begriffe gelten unverändert. Markierungen: **[Entscheidung]** vom Projektinhaber (2026-11-01, im Gespräch), **[Mein Vorschlag]** vom Entwurf, vom Projektinhaber ohne Einwand freigegeben, **[Baumeister]** überlässt die Spec dem, der plant und baut.

## 1. Worum es geht

M14 schwärzt Text, Bilder und Flächen nachweislich und prüft das Ergebnis selbst. Drei Dinge fehlen im Alltag:

1. **Man muss wissen, wo die Daten stehen.** Wer eine Akte mit hundert IBANs und E-Mail-Adressen schwärzt, will nicht jeden Begriff tippen. Acrobat und PDF-XChange bieten dafür Muster; Pagewing kann bisher nur Wörter suchen.
2. **Die Fläche ist immer ein schwarzer oder weißer Balken.** Andere Programme kennen Farbe, Überlagerungstext („geschwärzt“, ein Aktenzeichen) und, für Fotos, Weichzeichnen und Verpixeln.
3. **Manche Seiten lehnt M14 ab** (`PageNotRedactable`: Tiling-Muster, Schattierungen, Type-3-Schrift, Inline-Bilder ohne Rettung, `Tc`/`Tw`/`Tz`, `CIDToGIDMap`). Für den Nutzer ist das eine Sackgasse. Wer die Datei trotzdem schwärzen muss, braucht einen ehrlichen Ausweg.

**Ziele**
- Muster für E-Mail, IBAN, Telefon und Kreditkarte in „Suchen und Schwärzen“ und in der CLI, mit Prüfsumme wo es eine gibt.
- Füllfarbe frei wählbar, Überlagerungstext je Markierung, Weichzeichnen und Verpixeln für Bildflächen.
- „Seite in Bild umwandeln“, nur auf ausdrückliche Wahl, auch als Rettung für von M14 abgelehnte Seiten.
- Die Prüfung von M14 deckt alles Neue ab; der Bericht verspricht nie mehr, als sie leistet.

**Nicht-Ziele** (Backlog, nicht nebenbei beginnen)
- Text, Gesichter oder Namen in Bildern erkennen (OCR ist M15); automatisches Erkennen von Namen und Adressen; Datum und Sozialversicherungsnummern als Muster (Fehltreffer beziehungsweise US-Muster); eigene Regex-Muster in der App (Laufzeitrisiko, AOT); Stapel in der App.
- Vorinstallierte juristische Schwärzungscodes (die US-FOIA-Sätze von Acrobat passen nicht).
- Die Seite durchsuchbar machen (OCR, M15).
- Eine Datei mit `UnremovablePageKeys` retten. Eine frisch angelegte Rasterseite erbt `/Thumb`, `/PieceInfo`, `/Metadata` und `/AA` der alten Seite nicht; das wäre eine echte Rettung, ist aber **nicht geprüft** und ändert die Ablehnungsregel von M14. Backlog, mit eigenem Spike.

**Anti-Ziele** (Fehlschlag, auch wenn alles „funktioniert“)
- Ein Muster-Lauf gilt als vollständig. Er findet nur, was als zusammenhängender Text in einer Zeile steht (Abschnitt 4.1). Kein UI-Text, keine CLI-Meldung und kein Bericht sagt „alle IBANs gefunden“.
- Eine IBAN mit Tippfehler bleibt unbemerkt stehen, weil die Prüfsumme nicht stimmt.
- Weichzeichnen oder Verpixeln wird als Schutz für Text im Bild verkauft, oder die Rechnung lässt eine Rückrechnung zu.
- Eine umgewandelte Seite enthält noch Text, Annotationen, Links oder Formularfelder, oder der Inhalt der Markierungen steckt in ihren Pixeln.
- Der Nutzer erfährt erst nach dem Anwenden, dass der Text einer Seite weg ist.
- Der Überlagerungstext enthält selbst den geschwärzten Begriff, oder er lässt Text im Ergebnis entstehen, den die Prüfung als Fund missversteht oder übersieht.
- Im Startpfad entsteht Arbeit; der `PdfiumThread` ist lange blockiert; Speicher wächst mit der Seitengröße ohne Grenze.

## 2. Rahmen (gilt ohne neue Entscheidung)

Wie in M14, Abschnitt 4, und `CLAUDE.md`: Vollständiger Neuschrieb über `DocumentSaver.RewriteAsync`, Arbeitskopie aus der Datei am Pfad, **der Prüfer liest nur das geschriebene Ergebnis**, Erwartungen kommen aus Plan und Original, Analyse und Anwenden teilen die Entscheidungsfunktion, jede Ablehnung beachtet `IsRefused`, ein Term wird nie als Muster gelesen (die Muster erzeugen **konkrete Zeichenketten**, die wie Textmarken weiterlaufen), UI-Texte in beiden Sprachen, kein Text nennt etwas „sicher“ im Rechtssinn, „gültig“ oder „rechtsgültig“, Kürzel nur im `CommandCatalog`, nichts im Startpfad, Messläufe öffnen nie den Modus. Neue Verluste und Rückfälle brauchen den Satz in `RedactionReportText`, in `RedactionApplyInfo` (beide Sprachen) und einen Test.

## 3. Reihenfolge **[Mein Vorschlag]**

Drei weitgehend unabhängige Stufen, in dieser Reihenfolge gebaut und je mit eigenem Abnahmetest:

1. **Seite in Bild umwandeln** (beseitigt die Sackgasse).
2. **Muster** (der sichtbarste Gewinn).
3. **Füllungen und Überlagerungstext.**

Wird eine Stufe deutlich größer als geplant, kann Stufe 3 als M14.2 abgeschnitten werden; das ist eine Scope-Änderung in der Roadmap, nie stillschweigend.

## 4. Entwurf

### 4.1 Stufe 2: Muster

**Wo:** Core, neuer Teil von `Pagewing.Core.Redaction` (Name **[Baumeister]**), UI- und PDFium-frei. Die Scanner arbeiten auf denselben Quelltexten wie `RedactionSearch` (Seitentext, Kommentare, Formularwerte) und liefern Treffer mit Zeichenbereich; daraus entstehen dieselben `RedactionHit`/`RedactionMark` wie bei der Wortsuche, mit dem **gefundenen Text als Begriff** (so, wie er auf der Seite steht, samt Trennzeichen). Prüfung, Zählregel und Rohbytes-Grenze (`MinRawCheckLength` = 4) bleiben unverändert; jedes Muster liefert mindestens 4 Zeichen.

**Handgeschriebene, begrenzte Scanner, kein `Regex`** **[Mein Vorschlag]**: lineare Laufzeit, feste Obergrenzen je Kandidat, AOT-sicher. Treffer müssen von Zeichen begrenzt sein, die nicht zum Muster gehören (kein Treffer mitten in einer längeren Ziffern- oder Buchstabenfolge). Obergrenzen wie die Suche (`RedactionSearch.MaxHits`, `MaxSourceTextLength`).

**Sicherheitsstufe je Treffer:** `ChecksumOk` (Prüfsumme stimmt), `ShapeOnly` (die Art hat keine Prüfsumme: E-Mail, Telefon), `ChecksumFailed` (Form passt, Prüfsumme oder Länge nicht). **Beim Schwärzen ist ein übersehener Treffer schlimmer als ein überflüssiger**, ein IBAN-Tippfehler ist immer noch eine sensible Zahl. Deshalb:
- `ChecksumOk` und `ShapeOnly` sind in der Trefferliste vorgewählt; `ChecksumFailed` steht getrennt, mit eigener Überschrift und Zähler, **nicht** vorgewählt, ein Klick nimmt alle hinzu.
- Die CLI meldet `ChecksumFailed`-Treffer auf stderr (Anzahl und Seiten, nie die Werte) und schwärzt sie nur mit einem Schalter (Name **[Baumeister]**, etwa `--include-unchecked`).

**Begriff und Zählregel [Mein Vorschlag]:** Muster-Treffer sind `Hit` mit `Everywhere = false` (die Muster finden jede Stelle, die sie erkennen). Der Begriff ist der Text, wie PDFium ihn liefert, auch mit erzeugten Leerzeichen. Weil Leerzeichen aus Laufabständen im Rohinhalt nicht stehen, prüft der Rohbytes-Weg bei Muster-Treffern zusätzlich die Form **ohne Trenner** (Leerzeichen, geschütztes Leerzeichen, `-`, `/`, `.`, Klammern entfernt, mindestens 4 Zeichen) nach derselben Zählregel; so ist die Prüfung für gruppierte IBANs und Karten nicht schwächer als für ungruppierte. Ein zweites Vorkommen, das der Scanner nicht fand (etwa über einen Zeilenumbruch geteilt), lässt die Prüfung nicht scheitern; es steht unter „nicht gefunden“. Wie der Textweg die Form ohne Trenner zählt, ist **[Baumeister]**; Invariante: keine falschen Fehlschläge durch Vorkommen, die der Scanner nicht erkennen konnte, und kein schwächerer Weg als bei Wörtern.

**IBAN-Tabelle [Mein Vorschlag]:** Quelle ist das öffentliche IBAN-Register der SWIFT (Länder, Längen, Beispiele; Tatsachendaten). Quelle und Standdatum stehen im Quelltext und in `DECISIONS`. Ein Selbsttest verlangt, dass **jedes Beispiel** des Registers Länge und Mod-97 besteht; er fängt Tippfehler in der übertragenen Tabelle.

**Schwellen von `ChecksumFailed` [Mein Vorschlag]:** IBAN: bekannter Ländercode, zwei Ziffern, Länge 15 bis 34 und die Form (Viererblöcke oder ungruppiert); ein unbekannter Ländercode ist kein Kandidat. Karte: gruppiert (4-4-4-4, 4-6-5 oder 4-4-4-4-3) oder mit Präfix eines großen Netzes (Visa 4; Mastercard 51–55 und 2221–2720; Amex 34, 37; Discover 6011, 65, 644–649; Diners 36, 38, 39, 300–305; JCB 3528–3589; UnionPay 62). Telefon: als Datum gelten `TT.MM.JJJJ`, `TT.MM.JJ`, `TT/MM/JJJJ` und `JJJJ-MM-TT` mit gültigem Tag und Monat, auch mit angehängter Uhrzeit. Die Listen stehen in `DECISIONS`.

**Die vier Muster**
- **IBAN:** zwei Buchstaben, zwei Ziffern, Rest alphanumerisch; Länge je Land aus einer festen Tabelle (ISO-13616-Register, mit Standdatum im Quelltext und einem Test je Land mit einem bekannten Beispiel); Mod-97 mit Blockrechnung (keine große Ganzzahl). Gruppen in Viererblöcken mit Leerzeichen oder geschütztem Leerzeichen sind erlaubt. `ChecksumOk` = Länge und Mod-97 stimmen; `ChecksumFailed` = Form passt, Länge oder Mod-97 nicht.
- **Kreditkarte:** 13 bis 19 Ziffern, Gruppen mit Leerzeichen oder Bindestrich (4-4-4-4, Amex 4-6-5, auch ungruppiert); Luhn. `ChecksumFailed` verlangt zusätzlich die gruppierte Form oder ein bekanntes Präfix, sonst wäre jede lange Zahl ein Kandidat.
- **Telefon:** international und deutsch (`+49 711 1234567`, `0711/1234567`, `(0711) 123 45 67`); 7 bis 15 Ziffern (E.164), Beginn mit `+`, `0` oder `(`; Trenner Leerzeichen, `-`, `/`, `.`, Klammern. Keine Prüfsumme (`ShapeOnly`). Nicht gezählt werden Datumsformen, ungetrennte reine Ziffernfolgen ohne Präfix und alles, was als `ChecksumOk`-IBAN oder -Karte schon erkannt ist.
- **E-Mail:** `lokal@domain.tld` mit Punkten und üblichen Zeichen im lokalen Teil, mindestens ein Punkt im Domainteil, höchstens 254 Zeichen; Unicode-Buchstaben erlaubt; keine Kommentare oder Anführungszeichen-Formen (RFC-Randfälle sind Backlog). `ShapeOnly`.

**Was nicht gefunden wird** (steht in `RedactionSearch.NotSearched`, bei jedem Lauf sichtbar): Muster, die über einen Zeilenumbruch oder eine Seitengrenze geteilt sind; Zahlen in Bildern; Ziffern, die durch andere Zeichen als die Trenner oben unterbrochen sind. Der Bericht sagt „Muster finden nur, was der Text zeigt“.

**Oberfläche:** Im Panel „Schwärzen“ bekommt „Suchen und Schwärzen“ eine zweite Betriebsart „Muster“ (Umschalter, Tastatur erreichbar, Tab fängt den Fokus nie): vier Kontrollkästchen mit Zähler je Art, Trefferliste wie bisher mit Haken je Treffer und Gruppen nach Art und Sicherheitsstufe; Texte nennen die Sicherheitsstufe in Worten („Prüfsumme stimmt“, „Prüfsumme stimmt nicht“), nie nur über Farbe. Unvollständig durchsuchte Seiten verlangen wie bei der Wortsuche das ausdrückliche Wort des Nutzers. Muster-Treffer sind `RedactionKind.Hit`; ob sie ein Merkmal „aus Muster“ tragen (für Anzeige und Rückgängig) ist **[Baumeister]**.

**CLI:** `--pattern <email|iban|phone|card>` (wiederholbar, kombinierbar mit `--text`, `--rect`, `--pages`), Schalter für `ChecksumFailed` (oben). Bericht und Trockenlauf wie in M14 (`showValues: false`, Exit 1 bei erreichter Suchgrenze oder nicht vollständig durchsuchten Seiten).

### 4.2 Stufe 3: Füllungen und Überlagerungstext

**Modell:** `RedactionFill` (heute `Black`/`White`, planweit) wird zu einer Beschreibung aus **Farbe** (Schwarz, Weiß, frei als RGB) und planweiter **Behandlung der Bildflächen** (`Solid`, `Pixelate`, `Blur`); Name und Gestalt **[Baumeister]**, die planweite Geltung ist gesetzt. Dazu **Überlagerungstext je Markierung** (Text, höchstens 200 Zeichen) und planweite Gestaltung (Wiederholen, automatische Größe, Textfarbe). Die Textbausteine („Codes“) sind eine benutzerdefinierte Liste in den Einstellungen (höchstens 20 Einträge zu je höchstens 40 Zeichen, nichts vorinstalliert außer „geschwärzt“/„redacted“ je nach Sprache **[Mein Vorschlag]**), auswählbar im Feld für den Überlagerungstext. `AppSettings` bleibt `{ get; set; }` und wird nur mit `with` geändert.

**Weichzeichnen und Verpixeln gelten nur für Bildflächen [Entscheidung].** Text- und Vektorflächen im selben Bereich bekommen immer die Volltonfläche in der gewählten Farbe (M14 entfernt den Text; es gäbe nichts zu verpixeln). Die Analyse nennt vorher, wie viele Bildflächen verpixelt und wie viele Flächen voll gefüllt werden. **Mischflächen [Mein Vorschlag]:** M14 entfernt Text und Vektoren im Bereich, übrig bleiben Bildpixel. Die deckende Fläche wird beim Anwenden nur über den Teil des Rechtecks gezeichnet, der **nicht** von einem behandelten Bild bedeckt ist; über einem behandelten Bild liegt keine deckende Fläche, sie würde die Behandlung verdecken. Der Prüfer erwartet im Bildteil die Blockmittel, im Rest die Füllfarbe.

**Nicht rückrechenbar [Entscheidung der Anforderung; Rechnung Mein Vorschlag]:** Beides ist dieselbe Rechnung: Der Bildbereich wird in **wenige grobe Blöcke** geteilt (höchstens 6 je Achse bei „grob“, höchstens 3 bei „sehr grob“, jeder Block mindestens 8 Bildpixel breit und hoch; die Blöcke füllen den Bereich genau, Pixel außerhalb bleiben bitgleich), jeder Block bekommt den Mittelwert seiner Pixel. „Verpixeln“ zeigt die Blöcke hart, „Weichzeichnen“ glättet **ausschließlich aus den Blockmitteln** (keine Rechnung auf den Originalpixeln). Es gibt keine feineren Stufen. **Abbildung und Rückfall [Mein Vorschlag]:** Eine Core-Funktion bildet Bereich und Bild auf ein ganzzahliges Pixelrechteck ab (nach außen gerundet, jedes berührte Pixel zählt); Erzeuger und Prüfer benutzen genau sie. Wo die Blockregel nicht anwendbar ist (Pixelrechteck kleiner als 2 × 2 Blöcke zu je 8 Pixeln; gedrehtes, geschrägtes oder zugeschnittenes Bild; Farbmodell oder Maske, die Pagewing nicht sicher neu schreiben kann; mehrere Bereiche, die sich im selben Bild überlappen), fällt die Fläche auf die **Volltonfläche** zurück, und die Karte nennt Zahl und Grund (nie raten, wie in M14). Der Alphakanal im Bereich trägt keine Originaldetails (Mittelwert je Block oder 255; **[Baumeister]**). **Der Prüfer erwartet das** und rechnet aus dem **Original** die Blockmittel nach; weicht das Ergebnis im Bereich von dem aus den Blockmitteln gebildeten Bild ab, ist das ein Fund (`ImagePixelNotOverwritten` oder eine neue Art, **[Baumeister]**). Ein Test belegt, dass zwei Originale mit gleichen Blockmitteln und verschiedenen Pixeln dasselbe Ergebnis liefern.

**Ehrliche Karte:** Sie sagt, dass Text in Bildern auch verpixelt oder weichgezeichnet lesbar bleiben kann, Pagewing ihn nicht erkennt (M15), und für Text im Bild die Volltonfläche zu wählen ist. Der Standard bleibt Schwarz, Volltonfläche.

**Überlagerungstext [Mein Vorschlag]**
- Wird als **Glyphenpfade der Geist-Schrift** in den Seiteninhalt gezeichnet (wie das Textfeld in M10: `TextBoxFont`, `TextBoxLayout`, `TextBoxCharacters`), also **ohne Schrift in der Datei und ohne extrahierbaren Text**. Folge: Die Prüfung findet nichts, was sie missverstehen könnte; der Text ist nicht auswählbar (für Barrierefreiheit kein Verlust, die Fläche ist ohnehin geschwärzt, die Karte nennt es).
- Zeichenvorrat wie `TextBoxCharacters`; ein anderer Text wird **als Ganzes** abgelehnt, nie ersetzt.
- **Enthält der Überlagerungstext einen geschwärzten Begriff, ist das Anwenden abgelehnt.** Maßgeblich ist die Analyse (neue Ablehnungsart, Name **[Baumeister]**, mit den betroffenen Markierungen), weil bei der CLI die Begriffe erst nach der Suche feststehen und Markierungen auch nach dem Text dazukommen; die Oberfläche warnt schon beim Eintippen und beim Markieren. Verglichen wird ohne Beachtung der Schreibung als Teilzeichenkette, für Begriffe ab `MinRawCheckLength` (4 Zeichen); kürzere Begriffe zählen nur, wenn der ganze Überlagerungstext ihnen gleicht (sonst lehnte ein Begriff „ab“ den Baustein „geschwärzt“ ab). Die Prüfung des Ergebnisses ändert sich dadurch nicht.
- **Schrift:** Die Geist-Bytes liegen **einmal im Core** als eingebettete Ressource; App und CLI lesen dieselbe Kopie (die Lizenz, SIL OFL, steht schon in den Drittlizenzen), der Weg `TextBoxFontSource` der App wird darauf umgestellt. Die CLI braucht keine Datei neben dem Programm. Zuschnitt **[Baumeister]**.
- Pro Rechteck einer Markierung ein Text, zentriert und auf die Fläche geschnitten; **Wiederholen** kachelt ihn; **automatische Größe** passt ihn ein (Untergrenze 6 pt). Passt er auch dann nicht, bleibt die Fläche leer gefüllt und die Karte zählt „Text passt nicht in N Flächen“ (nie ein Überlaufen über die Fläche).
- Textfarbe automatisch mit Kontrast von mindestens 4,5:1 zur Füllfarbe; eine freie Wahl unter 4,5:1 wird gewarnt, nicht verboten. Auf **behandelten Bildflächen** (Verpixeln, Weichzeichnen) wird kein Überlagerungstext gezeichnet, ihr Hintergrund hängt vom Bild ab; die Karte zählt diese Flächen. Auf Rasterseiten ist der Hintergrund immer die Füllfarbe.
- Der Text liegt **auf** der Füllfläche, innerhalb des Markierungsrechtecks; das Prüfrendern „außerhalb der Bereiche“ bleibt unberührt.

**Oberfläche:** Im Modus „Schwärzen“ eine Gruppe „Füllung“ (Farbe, Bildflächen), ein Feld „Text auf der Fläche“ für die ausgewählte Markierung mit Auswahl der Bausteine und den drei Optionen; entsteht erst beim Gebrauch. Die Vorschau zeigt Farbe und Überlagerungstext; bei verpixelten Bildflächen zeigt sie die Fläche gekennzeichnet („verpixelt“), die exakten Pixel sind **[Baumeister]**.

**CLI:** `--fill <black|white|#RRGGBB>`, `--image-fill <solid|pixelate|blur>`, `--overlay-text <Text>` (gilt für alle Markierungen des Aufrufs, die CLI hat keine einzelnen), `--overlay-repeat`. (Abweichung von der ersten Skizze im Gespräch: Bildbehandlung als eigener Schalter, weil sie nicht die Farbe ersetzt.)

### 4.3 Stufe 1: Seite in Bild umwandeln

**Zwei Wege [Entscheidung]:** (a) Rettung: jede Seite, die M14 mit `PageNotRedactable` ablehnt, lässt sich in der Anwenden-Karte **je Seite** zur Umwandlung wählen, **nie vorgewählt**; solange eine abgelehnte Seite weder umgewandelt noch aus dem Plan genommen wird, bleibt „Anwenden“ gesperrt. (b) Freie Wahl: jede Seite lässt sich im Plan als „als Bild“ vormerken, auch ohne Markierung (Befehl und Kürzel im `CommandCatalog`, Name **[Baumeister]**). `Encrypted` und `UnremovablePageKeys` bleiben Ablehnungen der Datei; die Umwandlung hilft dagegen nicht.

**Karte, vor dem Anwenden [Entscheidung]:** Sie nennt je Seite „Der gesamte Text dieser Seite geht verloren; sie ist danach ein Bild. Links, Kommentare und Formularfelder der Seite entfallen.“, die gewählte Auflösung (Wahl 150, 200 oder 300 dpi, Standard 200 **[Mein Vorschlag]**), eine Größenschätzung der Datei und, falls die Seite die Obergrenze von 32 Megapixeln überschreitet, die **tatsächlich** verwendete, niedrigere Auflösung (nie still herabgesetzt, nie unter 72 dpi; darunter wird die Seite abgelehnt). Nur diese Seiten werden umgewandelt; alle anderen behalten Text und Vektoren.

**Ablauf [Mein Vorschlag, Reihenfolge Baumeister]:** Die Seite wird aus einem **unveränderten, getrennt geöffneten** Dokument (nicht der Arbeitskopie, nach der Regel „Arbeitskopie nie rendern“) hell gerendert, mit Annotationen und Formularfeldern, wie sie auf der Seite stehen, ohne Hervorhebung; Gerendert wird in Streifen in **einen** Puffer (kurze `Background`-Aufträge, Vorbild `PdfiumPrint`); die Seite wird als **ein** Bild geschrieben, deshalb liegt die Obergrenze bei **32 Megapixeln** je Seite (rund 128 MiB BGRA; die Auflösung sinkt bei Bedarf, die Karte nennt sie, unter 72 dpi wird abgelehnt). Streifen als mehrere Bildobjekte könnten in manchen Betrachtern sichtbare Nähte zeigen. Die Markierungsflächen werden **in das Bitmap** gemalt (Rechtecke, nach außen auf ganze Pixel gerundet, ohne Glättung), bevor es als Flate-RGB-Bild in eine **neue Seite gleicher Größe, Drehung und Boxen** (MediaBox, CropBox) des Zieldokuments geschrieben wird. Die neue Seite hat genau ein Bildobjekt; ein Überlagerungstext liegt als **Pfadobjekte** (Glyphenpfade, wie auf nicht umgewandelten Seiten) innerhalb der Markierungsrechtecke darüber, es entsteht kein Text, und der Core braucht keinen eigenen Rasterisierer. Verpixeln und Weichzeichnen gibt es hier nicht (die Seite ist ein Bild; die Fläche ist voll gefüllt), die Karte sagt es.

**Prüfung [Mein Vorschlag]:** neue Erwartung „Seite als Bild“: die Seite des Ergebnisses hat genau ein Bildobjekt und sonst höchstens Pfadobjekte innerhalb der Markierungsrechtecke (Überlagerungstext), **keinen** extrahierbaren Text, keine Annotationen, keine Links, keine Formularfelder; die Pixel in den Bereichen sind die Füllung; außerhalb gleicht ein Prüfrender dem Original (Toleranz und Faktor wie das Prüfrendern von M14). Der Textvergleich der Seite entfällt (`CompareText = false` für diese Seiten, wie für markierte Seiten); die **Zählregel** der Begriffe rechnet Vorkommen auf umgewandelten Seiten als entfernt, damit ein Begriff dokumentweit nicht vor seiner Zahl scheitert (Näheres **[Baumeister]**). Die Rohbytes-Prüfung läuft unverändert auf der ganzen Datei. Der Bericht führt umgewandelte Seiten unter „Geprüft“ mit Seitenzahlen und nennt, was daran **nicht** geprüft ist (der Bildinhalt außerhalb der Bereiche ist absichtlich sichtbar geblieben).

**Plan und Durchsetzung [Mein Vorschlag]:** Die Seiten zur Umwandlung (mit der Auflösung) gehören zum unveränderlichen `RedactionPlan`, mit Rückgängig. Ein Plan nur mit solchen Seiten ist nicht leer; eine Seite ohne Markierung ist eine gültige Wahl (die Prüfung hat dann nichts Entferntes zu zählen, und der Bericht sagt es). Ablauf: Die erste Analyse nennt die abgelehnten Seiten, der Nutzer wählt, die zweite Analyse überspringt die gewählten Seiten im Durchlauf der Arbeitskopie (eine abgelehnte Seite wird nie bearbeitet); Zuschnitt **[Baumeister]**. Der **Core erzwingt** die Ablehnung: `RedactionApplier` wirft `RedactionRefusedException`, solange eine `PageNotRedactable`-Seite nicht umgewandelt ist; die CLI endet ohne `--rasterize-*` mit Exit 1, nicht nur die Oberfläche sperrt. **Grenze des Vertrags:** Eine umgewandelte Seite wird nur in den **Markierungen** gefüllt wie jede Seite. Ein sichtbares Vorkommen eines Begriffs außerhalb davon bleibt im Bild stehen, und weil die Seite keinen Text mehr hat, kann die Prüfung es ohne OCR nicht finden. Das steht im Bericht unter „Nicht geprüft“.

**CLI:** `--rasterize-pages <Liste>` (Seitenbereiche wie `--pages`), `--rasterize-unredactable` (wandelt genau die abgelehnten Seiten um und nennt sie auf stderr), `--dpi <72..300>`. Ohne einen dieser Schalter bleibt eine abgelehnte Seite eine Ablehnung (Exit 1).

## 5. Obergrenzen und Startpfad

Seiten je Plan zur Umwandlung, Dateigröße-Schätzung, Speicherbedarf je Seite (Streifen), Länge des Überlagerungstexts und Zahl der Codes sind Obergrenzen mit Test **[Baumeister]**; jede wird vor dem Allokieren geprüft. Nichts davon im Startpfad: Muster-Scanner, Füllungs-Gruppe, Kartenteile, Codes-Liste und Rasterweg entstehen erst beim Gebrauch (`RedactionStartPathTests` erweitern); Messläufe öffnen den Modus nie.

## 6. Tests und Abnahme

Die Abnahme der Roadmap (Mustertreffer inklusive falscher Prüfsummen und Grenzfälle; Weichzeichnen und Verpixeln ohne Rückrechnung, Pixel ersetzt statt überlagert; umgewandelte Seiten ohne Text) gilt unverändert. Zusätzlich:

- **Fixtures** (erzeugt von `tools/make-testpdfs.cs`, in `LICENSES.md` und `RenderCorpus.Skipped`): `redact-patterns.pdf` (gültige und ungültige IBANs mehrerer Länder, Luhn-gültige und -ungültige Karten, Telefonformen, E-Mails mit Randfällen, Datums- und ISBN-artige Fallen, Muster über Zeilenumbruch, in Kommentar und Formularwert), ein Bild mit bekanntem Inhalt für Blockmittel, und die vorhandene `redact-everywhere.pdf` als Fall der Rettung.
- **Muster:** je Art gültig, ungültig, Grenzfall (Anfang/Ende der Zeile, Trennzeichen, geschütztes Leerzeichen, längere Folge davor und danach), Datum ist kein Telefon, `ChecksumFailed` getrennt und nicht vorgewählt, CLI schwärzt es nur mit Schalter und meldet sonst ohne Werte.
- **Füllungen:** Pixel im Bereich = Blockmittel aus dem Original (zwei Originale mit gleichen Blockmitteln geben dasselbe Ergebnis), Pixel außerhalb bitgleich, Text und Vektor im Bereich voll gefüllt, Überlagerungstext mit dem geschwärzten Begriff abgelehnt, kein extrahierbarer Text aus dem Überlagerungstext, Text passt nicht → gezählt.
- **Seite in Bild:** `redact-everywhere.pdf` wird mit Rettung angewendet (das Anwenden ist ohne Wahl weiter abgelehnt); danach hat die Seite keinen Text, der Begriff steht nirgends in Text und Rohbytes, die Bereiche sind gefüllt, andere Seiten behalten Text; die Prüfung lässt eine Seite mit Resttext oder ungefülltem Bereich **durchfallen** (Mutationstest wie `CoveredButLiveTextFailsTheCheck`); das Original bleibt bei jedem Fehler unverändert.
- **Mensch:** Handprüfpunkte in `packaging/README.md`, „Schwärzen prüfen (M14)“, werden um die neuen Fälle ergänzt (Acrobat und Chrome öffnen die Ergebnisdateien, Aussehen der Füllungen und des Überlagerungstexts, Rasterseiten); sie bleiben offen bis `v1.0.0`, wie die Handprüfungen von M14.
- **Dokumentation:** Einträge „Schwärzen: … (M14.1)“ in `Docs/DECISIONS.md`, `README.md` (CLI), `CHANGELOG.md` `[Unreleased]`, `CLAUDE.md` Block „Schwärzen“, Roadmap.

## 7. Dem Baumeister überlassen

Namen und Zuschnitt der Typen, Stufenform der Sicherheitsangabe in der Oberfläche, Kürzel (ohne Kollision im `CommandCatalog`), die Form des erweiterten `RedactionFill`, Art der neuen Funde des Prüfers, Alphabehandlung im Bildbereich, Hintergrundaufträge und Streifengröße der Rasterung, JPEG statt Flate (nur wenn es die Prüfung nicht schwächt), Feinheiten der Karten nach Design-Spec.
