# Pagewing – Entscheidungen

Log der Architektur- und Technikentscheidungen. Neueste Einträge oben.

**Wann ein Eintrag fällig ist:** Wahl von Framework, Bibliothek oder Version, Architektur- und Schnittstellenentscheidungen, bewusst verworfene Ansätze, Workarounds mit Tragweite – alles, was man in einem halben Jahr sonst neu diskutieren würde.

**Regeln:** Einträge nicht nachträglich umschreiben. Wird eine Entscheidung revidiert, einen neuen Eintrag anlegen und im alten „Abgelöst durch: <Titel>“ ergänzen.

<!-- Vorlage:

## YYYY-MM-DD – <Titel>
**Kontext:** <Welches Problem, welche Rahmenbedingungen?>
**Entscheidung:** <Was wurde entschieden?>
**Begründung:** <Warum?>
**Verworfene Alternativen:** <Was noch in Frage kam und warum nicht>
-->

## 2026-10-17 – Single-Instance: Sperrdatei und Named Pipe
**Kontext:** Ein zweiter Aufruf von Pagewing soll die Datei an die laufende Instanz übergeben und sich beenden (Budget „weitere Datei in laufender Instanz bis erste Seite“ ≤ 100 ms, siehe Roadmap). Dafür braucht es eine verlässliche Wahl der primären Instanz und einen Übergabekanal, beides auf Windows und Linux mit demselben Code und ohne Reflection (Native AOT).
**Entscheidung:** `Pagewing.Core.Hosting.SingleInstance` (`src/Pagewing.Core/Hosting/SingleInstance.cs`):
- **Wahl der primären Instanz über eine exklusive Sperrdatei** (`FileStream` mit `FileShare.None` in `Path.GetTempPath()`, Name aus dem Instanznamen `pagewing-<Benutzername>`, auf `[A-Za-z0-9._-]` bereinigt, bei Änderung oder Länge über 64 mit angehängtem Kurz-Hash). Hält ein anderer Prozess die Sperre, ist die eigene Instanz sekundär. Das Betriebssystem gibt die Sperre bei jedem Prozessende frei (auch nach Absturz oder Kill), es gibt keine veraltete Sperre. Unter Linux emuliert .NET `FileShare.None` mit `flock`. Die Datei wird beim Beenden bewusst nicht gelöscht (Löschen würde mit einem gerade öffnenden Prozess um die Sperre konkurrieren; die leere Datei stört nicht).
- **Übergabe über eine Named Pipe** (`NamedPipeServerStream`/`NamedPipeClientStream`, `PipeOptions.CurrentUserOnly`; .NET nutzt unter Linux Unix-Domain-Sockets, derselbe Code). Der primäre Prozess öffnet die Pipe erst nach dem ersten Frame; `TrySend` wiederholt die Verbindung deshalb bis zum Timeout. Eine leere Pfadliste heißt „nur in den Vordergrund holen“.
- **Protokoll:** Eine Nachricht je Verbindung, vom Sender bis zum Verbindungsende geschrieben: `BinaryWriter` mit Anzahl (int32) und je Pfad ein UTF-8-String mit Längenpräfix. Kein Reflection-Serialisierer. Mehr als 64 Pfade sendet `TrySend` als mehrere Nachrichten.
- **Eingang gilt als nicht vertrauenswürdig:** Nachrichten bis höchstens rund 6 MiB werden mit 3 s Zeitlimit komplett gelesen und erst dann geparst. Verworfen wird alles mit mehr als 64 Pfaden, einem Pfad über 32 768 Zeichen, ungültigem UTF-8, abgeschnittenem Inhalt oder überzähligen Bytes. Ein fehlerhafter Client stoppt die Annahmeschleife nicht; auch eine Ausnahme im Rückruf nicht. Vor jeder Bearbeitung wird bereits die nächste Pipe-Instanz angelegt, damit Clients die Pipe nie fehlend vorfinden.
- Rückruf auf einem Pool-Thread; `Dispose` beendet die Schleife, wartet auf sie (nach `Dispose` kommt kein Rückruf mehr) und gibt die Sperre frei. Der Rückruf darf nicht auf den Thread warten, der `Dispose` aufruft.

**Begründung:** Die Sperrdatei ist plattformübergreifend zuverlässig und braucht keine Aufräumlogik. Die Pipe liefert Übergabe und Verfügbarkeitsprüfung in einem, und `CurrentUserOnly` schützt vor Zugriff anderer Benutzer. Der Pfad des zweiten Aufrufs bleibt kurz, weil nur ein paar Bytes über einen bereits bestehenden Kanal laufen.
**Verworfene Alternativen:**
- *Wahl über die Pipe selbst* (`NamedPipeServerStream` mit `maxNumberOfServerInstances = 1`, wer die Pipe anlegen kann, ist primär): Der Primärprozess öffnet die Pipe erst nach dem ersten Frame, davor wäre die Wahl nicht eindeutig. Unter Unix hängt das Verhalten außerdem an einer Socket-Datei, die nach einem Absturz liegen bleiben kann.
- *Benannter `Mutex`:* Unter Unix wird er in .NET über Shared-Memory-Dateien emuliert und verhält sich prozessübergreifend nicht zuverlässig (verwaiste Mutexe, Berechtigungen, Zugriff aus anderen Containern); er löst außerdem den Übergabekanal nicht.
- *Sperrdatei mit Prozess-ID prüfen:* Anfällig für Wiederverwendung der PID und für Wettläufe; das Betriebssystem erledigt das mit der Dateisperre ohnehin richtig.
- *Lokaler TCP-Port:* Netzwerk-Socket ohne Zugriffsschutz auf den Benutzer, verträgt sich schlecht mit „keine Netzwerkzugriffe“.

## 2026-10-17 – Render-Scheduler und Kachelparameter (M3)
**Kontext:** M3 schließt die Render-Pipeline aus Gesamt-Spec 5.3 an die App an: nur sichtbare Seiten plus Puffer, zuerst eine grobe Vorschau, dann scharfe Kacheln, veraltete Aufträge werden abgebrochen, ein LRU-Cache mit Speicherbudget hält die Ergebnisse. Die App ruft bei jedem Scroll-Ereignis vom UI-Thread aus `RenderScheduler.Update` auf; der UI-Thread darf dabei nie auf PDFium warten. Der Eintrag „Offene Punkte für den Render-Scheduler (M3)“ nennt Eigenschaften des Render-Kerns, die der Scheduler berücksichtigen muss.
**Entscheidung:**
- **Genau ein Auftrag des Schedulers gleichzeitig auf dem `PdfiumThread`.** Die Warteschlange liegt im Scheduler (`src/Pagewing.Core/Viewing/RenderScheduler.cs`), nicht im `PdfiumThread`. Ein einziger Worker-Task (geweckt über einen `SemaphoreSlim`) reicht den jeweils nächsten Auftrag an `PdfDocument.RenderPageAsync` (ganze Seite) bzw. `RenderTileAsync` (Kachel) weiter; `RenderRequest.Background` wird zu `PdfPriority.Background`.
- `Update` ersetzt die Wunschliste, die Reihenfolge ist die Priorität. Schlüssel im Cache, früher fehlgeschlagene und doppelte Schlüssel werden übersprungen. Der laufende Auftrag läuft weiter, wenn er noch gewünscht ist (egal an welcher Stelle), sonst wird er über sein `CancellationToken` abgebrochen. `Update` hält nur kurz eine Sperre und rendert nie auf dem aufrufenden Thread.
- **Abbruch ist prompt:** Der Scheduler bricht mit `CancellationTokenSource.CancelAsync` ab (das Flag gilt sofort, die Callbacks laufen auf einem Pool-Thread, nie auf dem UI-Thread). Der Worker wartet über eine Registrierung am Token nicht auf das Ende des abgebrochenen Auftrags, sondern startet sofort den nächsten. Der `PdfiumThread` verwirft den abgebrochenen Auftrag beim nächsten Schritt bzw. beim Herausnehmen aus der Warteschlange, ohne weiterzurendern; nicht abgebrochen ist also immer höchstens ein Auftrag des Schedulers. Ein Bild, das nach dem Abbruch doch noch eintrifft, wird verworfen: kein Cache-Eintrag, kein `TileReady`.
- `TileReady` (nachdem das Bild im Cache liegt) und `TileFailed` kommen auf einem Pool-Thread. Ein fehlgeschlagener Schlüssel wird einmal gemeldet und bis `Dispose` nicht erneut versucht. Nach `Dispose` gibt es keine Ereignisse mehr; `Dispose` wartet dafür nur auf einen gerade laufenden Handler, nie auf PDFium.
- **Kachelparameter** (Konstanten, Bestätigung durch die Messung in Task 13 des M3-Plans steht aus):
  - Kachel 512 × 512 Pixel (`TilePlanner.TileSize`).
  - Seiten bis 4 000 000 Pixel werden als **eine** Ganzseiten-Kachel gerendert (`TilePlanner.WholePageMaxPixels`, A4 bis etwa 200 %), größere im 512er-Raster.
  - Vorschau mit 320 Pixeln an der längsten Seite (`TilePlanner.PreviewMaxSide`), höchstens 48 Vorschau-Aufträge je Plan (`ViewportPlanner.MaxPreviewRequests`).
  - Cache 256 MiB (`RenderCache.DefaultBudgetBytes`).
  - Zoom 10 % bis 800 % in den Stufen 10, 25, 50, 75, 100, 125, 150, 200, 300, 400, 600, 800 (`ZoomSteps`); 100 % = 96 dpi (1 pt = 96/72 DIP).
- **Bezug auf „Offene Punkte für den Render-Scheduler (M3)“:**
  - Verschränkung nur Background mit Interactive (`AddFirst`): betrifft den Scheduler nicht mehr, weil er nur einen Auftrag gleichzeitig einreiht; Reihenfolge und Umpriorisierung entscheidet er selbst.
  - Abbruch eingereihter Aufträge erst beim Herausnehmen: gelöst durch die Registrierung am Token (siehe oben).
  - Gepinnte Arrays größer als das Bild: `RenderCache.CostOf` rechnet mit der Länge des Backing-Arrays. Pooling oder nicht verwaltete Puffer werden in M3 nicht umgesetzt.
  - `Path.GetFullPath`-Ausnahmen: werden laut Plan in `ViewerSession` (Task 5) auf `PdfOpenError.CannotRead` abgebildet, nicht im Scheduler.
  - Billige Seitengrößen: genutzt über `PdfDocument.GetPageSizesAsync` (Stapel).
- Zum offenen Punkt aus „Kacheln mit 64 px Überlapp nach links …“: Durch die Grenze von 4 Mpx haben übliche Seiten bis etwa 200 % keine Kachelnähte. Darüber bleiben die dort beschriebenen Abweichungen an Nähten möglich.

**Begründung:** Liegt die Warteschlange im Scheduler, kann jede Wunschliste sie billig neu ordnen und Veraltetes verwerfen, ohne dass der `PdfiumThread` etwas davon wissen muss; dort läuft ohnehin alles nacheinander, mehrere eingereihte Aufträge brächten keine Parallelität, nur späteren Abbruch. Ohne die Registrierung am Token würde ein abgebrochener Hintergrund-Auftrag, der hinter anderer Arbeit wartet, den nächsten interaktiven Auftrag aufhalten. Ein fehlgeschlagener Schlüssel würde sonst bei jedem Scroll-Ereignis neu gerendert.
**Verworfene Alternativen:**
- Alle Aufträge sofort an den `PdfiumThread` geben und dort umpriorisieren: ändert dessen Schnittstelle, und ein interaktiver Auftrag läuft dort bis zum Ende, bevor ein anderer beginnt.
- Auf das Ende des abgebrochenen Auftrags warten, bevor der nächste startet: einfacher, aber der nächste Auftrag hinge hinter der Arbeit, die vor dem abgebrochenen eingereiht ist.
- Bilder abgebrochener Aufträge, die im Wettlauf doch fertig werden, in den Cache legen: spart selten ein Rendern, macht aber den Vertrag „abgebrochen heißt kein Ereignis“ unscharf.
- Fehlgeschlagene Schlüssel erneut versuchen: Eine beschädigte Seite würde den Worker dauerhaft beschäftigen.

## 2026-10-17 – App-Icon und Schriftzug: Variante 1 „Flügelecke“ (vorläufig)
**Kontext:** Milestone 2 legt Schriftzug und Icon-Richtung als Grundlage für die Icon-Dateien in M8 fest. Drei Varianten im Stil der Designrichtung B standen als Artifact bereit (Design-Spec `Docs/superpowers/specs/2026-10-17-designsystem.md`, Abschnitt 8; Quelle `Docs/design/mockups/schriftzug-icon.html`): 1 „Flügelecke“, 2 „Gefächerte Seiten“, 3 „Seite mit Schwinge“.
**Entscheidung:** Variante 1 „Flügelecke“: eine Seite auf einer Indigo-Kachel, deren Eselsohr sich als Flügelspitze hebt, dazu der Schriftzug „Pagewing“ in Geist Semibold. Die Dateien liegen unter `Docs/design/` (`icon-entwurf.svg`, `icon-entwurf-einfarbig.svg`, `schriftzug.svg`). Die Wahl ist **vorläufig**; Wortlaut am 2026-10-17: „Erstmal 1, denke iwann wenn ich lust habe mach ich dann mal neues logo“. Ein späteres eigenes Logo ersetzt diesen Entwurf mit einem neuen Eintrag.
**Begründung:** Vom Projektinhaber gewählt, ohne weitere Gründe. Einschätzung des Entwurfs: Die Variante ist bei 16 px als Seite erkennbar, nutzt die Akzentfarbe der Oberfläche und unterscheidet sich von Acrobat und den Browser-Icons.
**Verworfene Alternativen:**
- Variante 2 „Gefächerte Seiten“ und Variante 3 „Seite mit Schwinge“: vom Projektinhaber ohne Angabe von Gründen nicht gewählt. Bei Variante 3 hatte der Entwurf selbst auf die Ähnlichkeit der drei Bögen mit einem WLAN- bzw. Kontaktlos-Symbol hingewiesen.

## 2026-10-17 – Icons: Tabler Icons (Pfade kopiert, kein Paket)
**Kontext:** Die gewählte Designrichtung B nutzt Linien-Icons. Geprüft wurden Fluent UI System Icons (MIT), Lucide (ISC, Feather-Anteil MIT), Phosphor (MIT) und Tabler (MIT); Lizenzen aus den LICENSE-Dateien der Repos, Details in `Docs/superpowers/specs/2026-10-17-designsystem.md`, Abschnitt 2.6.
**Entscheidung:** Tabler Icons 3.49.0 (MIT, https://github.com/tabler/tabler-icons), Variante „outline“, 2 px Strich auf 24er-Raster. Übernommen werden nur die Pfade der genutzten Icons (Liste in der Design-Spec, Abschnitt 5.5), als Geometrie im Code bzw. kompiliertem XAML. Kein NuGet-Paket, keine Icon-Schrift. Gezeichnet wird mit einem eigenen Icon-Control (`Path` mit Strich in einer 24×24-`Viewbox`), weil `PathIcon` nur füllt. Der MIT-Lizenztext von Tabler samt Copyright-Zeile kommt zu den Drittlizenzen (M8).
**Begründung:** MIT ist GPL-3.0-kompatibel. Kopierte Pfade sind AOT-sicher, kosten beim Start nichts und bringen nur die Icons mit, die gebraucht werden. Tabler hat anders als Lucide und Phosphor ein echtes Icon für „Seitenbreite“ (`arrow-autofit-width`) und einen Behelf für Schwärzen (`square-off`), und der Strich passt zur Schrift Geist.
**Verworfene Alternativen:**
- Fluent UI System Icons: flächige Konturen im Windows-Stil, passen zu Richtung A, nicht zu B.
- Lucide: sehr ähnlich zu Tabler, aber ohne „Seitenbreite“-Icon und mit zwei Lizenztexten (ISC und MIT).
- Phosphor: letzte Veröffentlichung 2024, ohne „Seitenbreite“-Icon.
- Icon-Schrift oder NuGet-Paket: lädt alle Icons, Startkosten und Größe ohne Nutzen.

## 2026-10-17 – Schrift: Geist und Geist Mono, als Teilmenge eingebettet (vorbehaltlich Messung in M3)
**Kontext:** Die gewählte Designrichtung B ist mit Geist entworfen. Die Systemschrift unterscheidet sich je Betriebssystem und Distribution (Segoe UI unter Windows, fontconfig unter Linux); eine eingebettete Schrift sieht überall gleich aus, kostet aber Startzeit. Die Startkosten sind nicht belegt (Design-Spec, Abschnitt 2.5).
**Entscheidung:** Geist und Geist Mono (OFL-1.1, ohne Reserved Font Name, https://github.com/vercel/geist-font; geprüft am 2026-10-16 in Version 1.8.1) werden als Teilmenge (Basis-Latein, Latin-1, Latin Extended-A, typografische Satzzeichen, ẞ) mit den Schnitten 400, 500 und 600 bzw. 400 und 500 als AvaloniaResource eingebettet und als eigene Datei mit `OFL.txt` ausgeliefert. Die genaue Version wird beim Einbinden in M3 festgelegt. Vorbehalt: M3 misst die Startkosten mit `--measure-startup`. Hält Pagewing damit die Budgets nicht ein, nutzt es von Anfang an die Systemschrift (Fallback in der Design-Spec, Abschnitt 5.2); das wird als eigener Eintrag festgehalten.
**Begründung:** OFL-1.1 ist als eigene Datei neben dem GPL-Programm ein Aggregat (GPL-FAQ „MereAggregation“), und ohne Reserved Font Name ist eine Teilmenge ohne Umbenennung erlaubt. Geist ist klein (Variable-TTF etwa 169 KB, als Teilmenge weniger) und hat Tabellenziffern für Seitenzahlen und Zoom.
**Verworfene Alternativen:**
- Geist erst nach dem ersten Frame nachladen: Die Oberfläche bricht dann einmal sichtbar neu um.
- Nur Systemschrift: uneinheitlich zwischen den Systemen und den Linux-Distributionen; bleibt der Fallback.
- Inter über `Avalonia.Fonts.Inter`: rund 1,9 MB in sechs Schnitten, ohne OFL-Datei im Paket, passt nicht zum gewählten Entwurf.
- IBM Plex Sans, Source Sans 3: Reserved Font Name, eine Teilmenge müsste umbenannt werden.

## 2026-10-17 – Designrichtung: B „Werkbank“ (überarbeitete Fassung)
**Kontext:** Milestone 2 verlangt mindestens drei Designrichtungen als HTML-Mockups und eine ausdrückliche Wahl des Projektinhabers. Am 2026-10-17 standen drei Richtungen als Artifacts bereit (Links, Szene und Prüfung in `Docs/superpowers/specs/2026-10-17-designsystem.md`, Abschnitt 3; Quellen unter `Docs/design/mockups/`): A „Nativ & leise“ (Fluent/GNOME-nah, Systemschrift), B „Werkbank“ (aufgebaut wie ein Code-Editor, Akzent Textmarker-Gelb), C „Papier“ (Leseraum mit schwebender Werkzeugleiste). Der Projektinhaber bevorzugte B, wollte aber, dass sie wie ein eigenständiges Software-Produkt wirkt und nicht wie ein Code-Editor. Er brachte dazu Verbesserungsvorschläge einer anderen KI mit, ließ sie prüfen und B danach im Artifact überarbeiten.
**Entscheidung:** Pagewing bekommt Richtung **B „Werkbank“ in der überarbeiteten Fassung** (Artifact-Version 2 vom 2026-10-17, Quelle `Docs/design/mockups/richtung-b-werkbank.html`). Wortlaut der Wahl am 2026-10-17: „ja passt ich nehme richtung B“. Die Überarbeitung gegenüber der ersten Fassung:
- Akzent gedecktes Indigo für Auswahl, Fokus und Primäraktion; Gelb nur für Suchtreffer und Markierungen.
- Oben nur Tabs, „Datei öffnen“ und ein kompakter Knopf „Suchen oder Befehl · Strg K“, der die Befehlspalette erst beim Aktivieren öffnet.
- Keine ausgegrauten Modus-Reiter für spätere Versionen. Die Leseansicht hat keine Werkzeugleiste; Bearbeitungsmodi ab v0.2 öffnen eine eigene Werkzeugleiste über dem Dokument mit sichtbarem „Fertig“.
- Schmale Aktionsleiste links (Seiten, Inhalt, Suche) mit einem Panel, das nur bei Bedarf offen ist: Vorschaubilder nach dem Öffnen, Trefferliste beim Suchen, nie beides.
- Statusleiste nur mit Seite, Zoom und ab v0.2 dem Änderungsstatus; Seitenformat, PDF-Version und Dateigröße unter „Dokumentinfos“.
- Weniger Rahmen, mehr Luft um die Seite; zwei Dichten, „Normal“ als Standard und „Kompakt“.
- Tabler Icons mit 2 px Strich; Schrift Geist und Geist Mono.
**Begründung:** Der Projektinhaber nannte keine Gründe über seine Vorliebe für B hinaus; die folgende Begründung ist die Einschätzung des Entwurfs. B wächst am besten mit den Funktionen der Teilprojekte 2 bis 4 (Modi mit eigener Werkzeugleiste statt einer immer volleren Leiste) und ist per Tastatur gut bedienbar. Die Überarbeitung nimmt die Informationshierarchie von A (klare Statusleiste, wenig gleichzeitig sichtbar) und die Ruhe von C (Dokument im Mittelpunkt) auf. Gelb als Akzent wäre mit Suchtreffern und Textmarkierungen verwechselbar gewesen.
**Verworfene Alternativen:**
- A „Nativ & leise“ und C „Papier“: vom Projektinhaber ohne Angabe von Gründen verworfen; Ideen aus beiden sind in die Überarbeitung von B eingeflossen (siehe Begründung).
- B in der ersten Fassung: wirkte wie ein Code-Editor; Akzent Gelb, immer sichtbare Modus-Reiter mit „v0.2“/„v0.3“, schwebendes Suchfenster und Dokumentdaten in der Statusleiste.
- Geist erst nach dem ersten Frame nachladen (Teil der Vorschläge): verworfen, weil die Oberfläche dann einmal sichtbar neu umbricht. Stattdessen wird eine Teilmenge eingebettet, und M3 misst die Startkosten; liegen sie über dem Budget, nutzt Pagewing von Anfang an die Systemschrift.

## 2026-10-16 – Vergleichsmessung und endgültige Geschwindigkeitsbudgets
**Kontext:** Die Budgets der Roadmap („Geschwindigkeitsziele“) waren vorläufig; M1 sollte sie nach einer Vergleichsmessung endgültig festlegen. Die Messung führte der Projektinhaber am 2026-10-16 auf seinem Rechner durch (WIN11-UNITY, Windows 11 Pro, Windows Defender als einziger Virenscanner). Gemessen wurde nur **Chrome 155.0.8059.80**: Acrobat und SumatraPDF sind dort nicht installiert und wurden nicht gemessen. Der PDF-Viewer von Chrome ist PDFium, also dieselbe Bibliothek wie in Pagewing; Chrome ist die engste Vergleichsgröße.
**Methode:** `tools/measure-open.cs` nach `bench/compare/README.md` (Befehle für Chrome mit eigenem `--user-data-dir` und `--allow-running`). Das Werkzeug kopiert den Seitenbereich des Fensters vom Bildschirm und liefert je Lauf zwei Zeiten ab unmittelbar vor dem Prozessstart: `t_first_content` (erster lesbarer Seiteninhalt) und `t_visually_complete` (letzte sichtbare Änderung vor 500 ms Stillstand; das ist „bis erste Seite scharf“). Vier Fälle mit je 10 Läufen: (a) Start und (b) Warmstart mit `text-basic.pdf`, (c) `thousand-pages.pdf`, (d) zweite Datei (`hello_world.pdf`) als Tab im laufenden Chrome. Alle 40 Läufe sind eingeschwungen (`settled`). Zeitauflösung: Bildabstand im Median etwa 70 ms (55 bis 92 ms); jeder Zeitstempel ist eine obere Schranke. Rohwerte und Begleitnotiz: `bench/compare/results/2026-10-16-WIN11-UNITY.csv` und `.md`.

| Fall | `t_first_content` (Median) | `t_visually_complete` (Median) | vorläufiges Budget | Chrome / Budget |
|---|---:|---:|---:|---:|
| (a) Start | 1372,2 ms | 1487,1 ms | 400 ms | 3,7 × |
| (b) Warmstart | 1443,3 ms | 1537,3 ms | 200 ms | 7,7 × |
| (c) 1000 Seiten | 1476,4 ms | 1738,7 ms | 300 ms | 5,8 × |
| (d) zweite Datei als Tab | 505,7 ms | 613,3 ms | 100 ms | 6,1 × |

Das Verhältnis bezieht sich auf `t_visually_complete`. In Fall (d) beziehen sich die Zeiten auf den Start des zweiten Prozesses; die erste Datei brauchte dort im Median 1399,9 ms bis zum ersten Bild.
**Entscheidung:** Die vier Budgets sind **endgültig und unverändert**: Kaltstart bis erste Seite scharf ≤ 400 ms, Warmstart ≤ 200 ms, weitere Datei in laufender Instanz ≤ 100 ms, 1000-seitige PDF bis erste Seite ≤ 300 ms. Der Projektinhaber hat das am 2026-10-16 bestätigt. Angewandt wurde die Regel aus dem README („Regel für den Vorschlag“): Vorschlag = Chrome-Median (`t_visually_complete`), auf 50 ms aufgerundet, höchstens das vorläufige Budget. Das ergibt (a) min(1500, 400) = 400, (b) min(1550, 200) = 200, (c) min(1750, 300) = 300, (d) min(650, 100) = 100. Gegenprobe bestanden: Die Kernzeit aus dem Eintrag „Benchmark-Basiswerte für den Render-Kern (M1)“ (`OpenThousandPagesAndRenderFirstPage` 3,4 ms) plus 50 ms für den Fensterstart liegt unter jedem Budget. Der Startzeit-Check ab M3 (`--measure-startup`, `bench/startup`) nutzt diese Budgets und schlägt ab Budget + 25 % fehl.
**Begründung:** Chrome braucht für alle vier Fälle ein Vielfaches der vorläufigen Budgets (3,7- bis 7,7-fach). Die Budgets liegen damit deutlich unter den gemessenen Chrome-Werten, und die Kernzeit zeigt, dass sie technisch erreichbar sind. Die Regel verbietet, Budgets zu lockern; eine Straffung ist nicht begründbar, weil die Messung nur eine obere Schranke mit etwa 70 ms Auflösung liefert.
**Verworfene Alternativen:**
- Budgets auf die Chrome-Werte lockern: widerspricht der Regel (nie lockern) und dem Ziel, deutlich unter den gemessenen Chrome-Werten zu bleiben.
- Budgets straffen: sinnvoll nicht möglich, die Messung ist dafür zu grob, und der Startzeit-Check von M3 prüft erst, was Pagewing mit Avalonia tatsächlich erreicht.
- Auf Messungen von Acrobat und SumatraPDF warten: nicht nötig, die Regel stützt sich nur auf den schnellsten Browser (Chrome); die beiden anderen dienen der Einordnung.

**Grenzen der Messung:**
- Nur Chrome gemessen; Acrobat und SumatraPDF fehlen, der Vergleich mit ihnen ist offen.
- Die Auflösung liegt bei etwa 70 ms, die Zeitstempel sind obere Schranken. Das 100-ms-Budget aus Fall (d) lässt sich mit diesem Werkzeug nicht auflösen.
- Fall (b) war nicht schneller als Fall (a); ein echter Kaltstart (Neustart direkt davor) ist daher nicht belegt.
- Monitor und Skalierung wurden nicht festgehalten.
- Ob Pagewing mit Avalonia die Budgets erreicht, prüft erst der Startzeit-Check in M3. Er misst von innen (`--measure-startup`), nicht per Bildschirmkopie; die Werte beider Verfahren sind nicht dasselbe.

## 2026-10-16 – Offene Punkte für den Render-Scheduler (M3)
**Kontext:** Die abschließende Review von Milestone 1 hat Eigenschaften des Render-Kerns festgestellt, die für den Render-Scheduler und den Kachel-Cache (M3) wichtig sind. Sie sind in M1 kein Fehler, weil es dort weder Scheduler noch Cache gibt. Festgestellt wurde:
- `PdfiumThread` reiht schrittweise Arbeit nach jedem Schritt vorne in ihre Prioritätsklasse ein (`AddFirst`). Verschränkt wird deshalb nur Background mit Interactive: Ein interaktives Rendern läuft bis zum Ende, bevor ein anderer interaktiver Auftrag beginnt.
- Der Abbruch eines eingereihten, noch nicht begonnenen Auftrags wird erst beim Herausnehmen aus der Warteschlange bemerkt. Für ein promptes Verwerfen veralteter Kachelaufträge muss der Scheduler eine Registrierung am `CancellationToken` nutzen.
- Die zurückgegebenen Bilder liegen in gepinnten Arrays (Pinned Object Heap). Bei Kacheln mit Überlapp bleibt das Array so groß wie das Bitmap samt der bis zu 64 px Überlapp-Spalten, auch nachdem diese entfernt sind. Die Speicherabrechnung des Caches muss die Array-Länge verwenden; Pooling oder nicht verwaltete Puffer sind zu prüfen.
- `PdfEngine.OpenAsync` übersetzt Ausnahmen von `Path.GetFullPath` (`ArgumentException`, `PathTooLongException`, `NotSupportedException`) nicht. Die App muss sie abfangen oder auf `PdfOpenError.CannotRead` abbilden; die CLI tut das bereits (`CliApp`).
- `PdfDocument.GetPageSizeAsync` nutzt `FPDF_GetPageSizeByIndexF` und lädt die Seite nicht mehr. Die Seitengrößen aller Seiten eines Dokuments für das Layout sind damit billig.
**Entscheidung:** Die Punkte gehen in die Planung von M3 ein; in M1 bleibt der Code dazu unverändert (außer der Seitengröße, die schon umgestellt ist).
**Begründung:** Scheduler, Cache und App-Fehlerbehandlung gehören zu M3. Ohne diese Notiz müssten die Eigenschaften dort neu ermittelt werden.
**Verworfene Alternativen:** Die Punkte schon in M1 zu ändern: Das nähme Architekturentscheidungen des Schedulers vorweg (CLAUDE.md, „Milestone-Workflow“).

## 2026-10-16 – Benchmark-Basiswerte für den Render-Kern (M1)
**Kontext:** `bench/Pagewing.Benchmarks` misst Öffnen, Rendern und Textextraktion des Kerns in-process. Die Werte sind der Ausgangspunkt für den Vergleich mit dem Render-Scheduler (M3). Sie sind **nicht** das Ergebnis der Budgetmessung „1000-seitige PDF öffnen bis erste Seite“ (≤ 300 ms, Roadmap „Geschwindigkeitsziele“): Diese Messung gehört zur Vergleichsmessung (`bench/compare`) und ist noch offen.
**Entscheidung:** Als Basiswerte gelten die Messungen vom 2026-10-16 mit `dotnet run -c Release --project bench/Pagewing.Benchmarks -- --filter "*" --job short` (BenchmarkDotNet 0.15.8, 3 Iterationen). Rechner: Windows 11 (10.0.26200.9711, 25H2), AMD Ryzen 7 3800X 3,90 GHz, 6 Kerne, .NET SDK 10.0.302, .NET 10.0.10 X64 RyuJIT x86-64-v3, JIT (nicht AOT).

| Benchmark | Mittelwert | Fehler | Allokiert |
|---|---:|---:|---:|
| `OpenSmall` (`text-basic.pdf`) | 543,9 µs | 127,5 µs | 1,41 KB |
| `OpenThousandPages` | 1,353 ms | 446,8 µs | 1,41 KB |
| `OpenThousandPagesAndRenderFirstPage` (Seite 1 bei Maßstab 2) | 3,423 ms | 1,698 ms | 314,87 KB |
| `RenderFirstPage144Dpi` (`vector-shapes.pdf`, 600 × 600 px) | 2,752 ms | 795 µs | 1,38 MB |
| `RenderTile512At200Percent` (`vector-shapes.pdf`, Kachel 512 × 512 ab 88/88) | 2,677 ms | 150 µs | 1,13 MB |
| `GetTextOfFirstPage` (`text-basic.pdf`) | 215,8 µs | 213,6 µs | 789 B |

**Einschränkungen (die Werte sind Untergrenzen, kein Budgetnachweis):**
- Alle Test-PDFs sind winzig (größte Seite 600 × 120 pt, die Seiten von `thousand-pages.pdf` 200 × 100 pt). Es gibt keine A4-Seite (rund 1190 × 1684 px bei 144 dpi); `OpenThousandPagesAndRenderFirstPage` misst deshalb vor allem das Öffnen.
- Die Render-Benchmarks nutzen `vector-shapes.pdf` (600 × 600 px), weil eine 512er-Kachel bei 200 % auf `text-basic.pdf` (600 × 400 px) nicht passt. Die Werte beider PDFs sind nicht vergleichbar.
- Die Open-Benchmarks laufen im warmen Zustand (PDFium-Thread in `GlobalSetup` gestartet, Datei im Cache). Kaltstart, Prozessstart und UI fehlen.
- Gemessen wurde mit JIT, nicht mit Native AOT.
- `--job short` (3 Iterationen) hat große Fehlerbereiche. Für belastbare Werte den Standard-Job verwenden.
- Nur Windows gemessen, keine Linux-Werte.

**Begründung:** Die Zahlen halten fest, dass Öffnen und Rendern des Kerns bei kleinen Seiten im Millisekundenbereich liegen und nicht den Engpass bilden. Der Ausgangspunkt für spätere Regressionen ist ein Standard-Job-Lauf mit einer A4-Test-PDF; eine passend lizenzierte Datei gibt es bisher nicht.
**Verworfene Alternativen:** Die Werte als Budgetnachweis zu führen: Die Einschränkungen oben machen sie dafür ungeeignet.

## 2026-10-16 – BenchmarkDotNet 0.15.8 als reine Entwicklungsabhängigkeit in bench/
**Kontext:** Die Geschwindigkeitsziele (Roadmap, „Geschwindigkeitsziele“) brauchen reproduzierbare Messungen für Öffnen, Rendern und Textextraktion, zuerst in M1 (`Pagewing.Benchmarks`), später als Vergleichsbasis für den Render-Scheduler (M3). Die Messwerte müssen Aufwärmphase, Ausreißer und Speicherverbrauch sauber behandeln.
**Entscheidung:** `BenchmarkDotNet` 0.15.8 (zentral in `Directory.Packages.props`) wird ausschließlich vom Projekt `bench/Pagewing.Benchmarks` referenziert. Das Projekt ist nicht packbar, läuft mit JIT, hat kein `IsAotCompatible` und wird von der CI nur gebaut, nie ausgeführt. Aufruf: `dotnet run -c Release --project bench/Pagewing.Benchmarks -- --filter "*"`.
**Begründung:** Lizenz MIT, damit GPL-3.0-kompatibel (die Abhängigkeiten wie Perfolizer und Iced sind ebenfalls MIT). Die Abhängigkeit liegt nur unter `bench/`, wird nie ausgeliefert und ist nicht Teil der AOT-Apps (App, CLI, Core, Pdfium); sie verletzt daher weder die Native-AOT-Pflicht noch die Offline-Regeln (BenchmarkDotNet läuft nur auf der Entwicklermaschine und sendet keine Telemetrie).
**Verworfene Alternativen:**
- Selbst geschriebene `Stopwatch`-Schleifen: keine Statistik, keine Aufwärmphase, keine Ausreißerbehandlung, kein Speicher-Diagnoser; Ergebnisse wären zwischen Läufen und Rechnern kaum vergleichbar.
- NBomber und ähnliche Werkzeuge: zielen auf Last- und Stresstests von Diensten, nicht auf Mikro-Benchmarks; hier nicht nötig.

## 2026-10-16 – Render-Regression: zwei Vergleichsmodi, ein gemeinsamer Referenzsatz
**Kontext:** `tests/Pagewing.Render.Tests` vergleicht jede Seite der Test-PDFs (`pagewing render --dpi 144`) mit einem Referenzbild aus `testdata/references`, erzeugt unter Windows. Geplant war ein pixelweiser Vergleich (Pixel weicht ab ab Δ > 16 je Kanal, höchstens 0,2 % abweichende Pixel), kalibriert auf das Doppelte der gemessenen Abweichung zwischen Windows und Linux, ohne dass eine Verschiebung um 1 Pixel durchrutscht. Die CI unter Linux (Lauf 38956332818) zeigte:
- Die Seiten ohne Text (`vector-shapes`, `image-rgb`, `transparency`) bestehen den pixelweisen Vergleich.
- Alle Seiten mit Text weichen ab. PDFium ersetzt die nicht eingebetteten Standardschriften je Betriebssystem durch andere Systemschriften. Layout und Tintenflächen sind gleich (gleiche Begrenzungsrahmen), Glyphenränder und Kantenglättung nicht.
- Anteil der Pixel mit Δ > 16 (größtes Δ):
  - `thousand-pages` S. 1000: 1,874 % (211); S. 1: 1,130 % (211)
  - `hello_world` und die verschlüsselten r3/r6: je 1,033 % (153)
  - `wide-page`: 1,001 % (179)
  - `rotated-page`: 0,979 % (226)
  - `text-basic` S. 1: 0,814 % (211); S. 2: 0,699 % (255)
  - `annotations`: 0,765 % (238)
  - `cropbox`: 0,568 % (140)
- Eine Verschiebung von `text-basic` S. 1 um 1 Pixel ergibt nur 1,36 %. Eine pixelweise Toleranz, die Linux besteht, übersieht sie also; das gilt auch für die Schwellen Δ > 32, 64 und 96.
- Mittelwerte über 8 × 8 Pixel, größte Abweichung eines Blocks je Seite:
  - `text-basic` S. 2: 44,22 (5 von 3750 Blöcken über 40 = 0,133 %)
  - `rotated-page`: 32,23
  - `thousand-pages` S. 1000: 26,03
  - `wide-page`: 23,27
  - `annotations`: 20,08
  - `text-basic` S. 1 und `thousand-pages` S. 1: je 18,59
  - `hello_world`, r3, r6: je 15,66
  - `cropbox`: 13,33
  - Bei 4 × 4 Blöcken sind es bis 116, zu viel.

**Entscheidung:** Ein gemeinsamer Referenzsatz (unter Windows erzeugt), zwei Vergleichsmodi. Der Modus steht ausdrücklich je Fall in `RenderCorpus.Cases`, mit einer Begründung in einer Zeile, und wird nicht automatisch erkannt.
- `CompareMode.Strict` für Seiten ohne Text (`vector-shapes`, `image-rgb`, `transparency`): pixelweise, `RenderTolerance(MaxChannelDelta: 16, MaxDifferingFraction: 0.002)`.
- `CompareMode.Tolerant` für Seiten mit Text (alle übrigen, auch die PDFium-Dateien): Tiefpass über Blockmittelwerte, `BlockTolerance(BlockSize: 8, MaxBlockDelta: 40, MaxDifferingFraction: 0.0027, HardBlockDelta: 89)`. Ein Block weicht ab, wenn ein Kanalmittel um mehr als 40 abweicht. Der Vergleich scheitert bei anderer Größe, bei mehr als 0,27 % abweichenden Blöcken (das Doppelte der gemessenen 0,133 %, aufgerundet) oder wenn ein einzelner Block um mehr als 89 abweicht (2 × 44,22, aufgerundet).
- Nachweis der Mutationstests (`RenderRegressionTests`, Faktor = Messwert ÷ Grenze):
  - streng, `vector-shapes` um 1 px verschoben: 1,698 % der Pixel, Grenze 0,2 %, Faktor 8,5
  - tolerant, Textzeile von `text-basic` S. 1 geleert: 3,57 % der Blöcke (Faktor 13), größter Block 191,5 (Faktor 2,15 zur harten Grenze)
  - tolerant, `text-basic` S. 1 um 8 px verschoben: 2,96 % der Blöcke (Faktor 11), größter Block 191,3
  - tolerant, Farbänderung (Grünkanal −60 auf 40 × 40 px in der gefüllten Square-Annotation von `annotations`, ungünstigste Lage zum Blockraster): 0,427 % der Blöcke, Faktor 1,58
- Bei Abweichungen schreiben beide Modi `.actual.png` und `.diff.png` (im toleranten Modus sind ganze abweichende Blöcke rot markiert) und nennen Anteil, größtes Delta und Grenzen.

**Begründung:** Die Textabweichung zwischen den Betriebssystemen ist legitim (andere Ersatzschrift, gleiche Metrik) und sitzt nur an Glyphenrändern. Ein Tiefpass glättet sie weg, fehlende, verschobene oder umgefärbte Inhalte bleiben sichtbar. Seiten ohne Text behalten die volle Strenge.

**Verworfene Alternativen:**
- Großes `MaxChannelDelta` (128 bis 160) im pixelweisen Vergleich: würde zwar die 1-px-Verschiebung trennen, macht aber Farbänderungen bis zu dieser Größe auf allen Seiten unsichtbar.
- Referenzsätze je Betriebssystem: verdoppeln die Pflege bei jedem wöchentlichen PDFium-Update und brauchen zum Erzeugen einen Linux-Rechner.
- Nur eingebettete Schriften in den eigenen Test-PDFs: hilft nicht bei den unverändert übernommenen PDFium-Dateien (`hello_world`, `encrypted_hello_world_r3/r6`) mit nicht eingebetteten Schriften. Bleibt eine Option für später, um für die eigenen PDFs die pixelweise Strenge zurückzugewinnen.
- Blöcke von 4 × 4: Linux weicht dort bis 116 je Block ab, zu nah an echten Änderungen.

**Folgen:**
- Auf Seiten mit Text erkennt die Render-Regression keine Änderungen in der Größenordnung von 1 px oder der Kantenglättung; eine 1-px-Verschiebung ergibt dort höchstens 31,9 je Block. Ebenso unerkannt bleiben flächige Farbänderungen bis 40 je Kanal und kleine lokale Änderungen unter etwa 0,27 % der Blöcke.
- Exakte Prüfungen auf demselben Betriebssystem bleiben bestehen:
  - `RenderRegressionTests.RenderingTwiceIsBitIdentical` (Determinismus)
  - in `Pagewing.Core.Tests` u. a. der Kacheltest `RenderTests.TilesStitchToTheFullPage`

## 2026-10-16 – Kacheln mit 64 px Überlapp nach links; Kacheln sind in PDFium nicht allgemein bitgleich
**Kontext:** Der Kacheltest `RenderTests.TilesStitchToTheFullPage` (`wide-page.pdf`, Maßstab 1,5, Kacheln 128 × 128) soll exakt dem Ganzseiten-Rendering entsprechen, ersatzweise Δ ≤ 2 je Kanal bei höchstens 0,1 % der Pixel. Mit dem geplanten Verfahren (`FPDF_RenderPageBitmap_Start`, Start bei minus Kachel-Offset, Größe der ganzen Seite, Bitmap in Kachelgröße) wichen 282 von 162 000 Pixeln ab (0,17 %), maximal um 134 – beides über der Ersatzgrenze. Betroffen waren nur Glyphen, die von der **linken** Kante einer Kachel geschnitten werden (Spalten 384 und 512, nur in der Textzeile).
**Befund:**
- Ursache in PDFium `chromium/8100`, `CFX_RenderDevice::DrawNormalText` (`core/fxge/cfx_renderdevice.cpp`): Auf einem 32-Bit-Bildschirm-Bitmap rendert PDFium Glyphen im LCD-Modus mit Normalisierung (`kLcd`, `normalize = true`, auch ohne `FPDF_LCD_TEXT`) und berechnet die Subpixel-Phase als `static_cast<int>(glyph.device_origin_.x * 3) % 3`. Liegt der Ursprung einer Glyphe links vom Bitmap (negatives x), rundet der Cast zur Null hin und `%` liefert einen negativen Rest. Der sichtbare Teil der Glyphe wird dadurch mit falscher Phase gemischt.
- `FPDF_RenderPageBitmapWithMatrix` mit Matrix und Clipping zeigt bei denselben Schnitten **dieselben** Abweichungen (Wegwerf-Test, alle Linksschnitte durch `text-basic.pdf` bei 1,5). Die Wahl der progressiven API ist also nicht die Ursache.
- Obere, untere und rechte Kachelkanten durch Text sind exakt. Ab einer Schriftgröße von mehr als 50 Gerätepixeln je Geviert zeichnet PDFium Text als Pfad, dann tritt der Fehler nicht auf.
- Unabhängig davon sind Kacheln in PDFium nicht allgemein bitgleich zum Ganzseiten-Rendering, weil PDFium mit `float`-Matrizen rechnet. Ein Wegwerf-Sweep über alle Test-PDFs (Maßstäbe 1 bis 3, jede dritte Schnittposition links und oben) fand trotz Überlapp: Kantenglättung von Pfaden, Transparenz und Zuschnitt um Δ 1, Bildskalierung um Δ ≤ 15 (`image-rgb.pdf` bei 2,4), vereinzelt eine um einen Pixel verschobene Kante über eine ganze Zeile (Δ 255, `wide-page.pdf` bei 2,4, Oberkante) und bei `annotations.pdf` Abweichungen bis Δ 79 an linken Kanten.
**Entscheidung:** `PdfiumRenderWork` rendert jede Kachel mit einem Überlapp von `PdfiumRenderWork.TileOverlap` = 64 Pixeln nach links (begrenzt durch den Seitenrand und das Größenlimit von 256 Mpx) und verwirft diese Spalten danach im selben Puffer (Zeilen werden zusammengeschoben, keine zweite Allokation). Damit liegt der Ursprung praktisch jeder Glyphe, die in die Kachel reicht, im Bitmap. Der Kacheltest bleibt unverändert und ist damit exakt gleich. Der Test `PdfiumRenderWorkTests.GlyphsCutByTheLeftTileEdgeMatchTheWholePage` prüft jeden Linksschnitt durch beide Seiten von `text-basic.pdf` bei 1,5 und schlägt ohne Überlapp fehl.
**Begründung:** Der Fehler ist an Kachelnähten als halb verblasste Glyphe sichtbar. 64 Pixel decken Glyphen bis zu 1,28 Geviert bei der größten Glyphen-Bitmap-Schrift (50 Pixel) ab. Die Kosten sind 64 Spalten je Kachel, bei 512er-Kacheln 12,5 % mehr Pixel; Kacheln am linken Seitenrand haben keinen Überlapp.
**Verworfene Alternativen:**
- `FPDF_RenderPageBitmapWithMatrix`: gleicher Fehler und nicht progressiv.
- Überlapp an allen vier Kanten: beseitigt die `float`-bedingten Abweichungen laut Sweep nicht und kostet bei 512er-Kacheln 56 % mehr Pixel. Einzige im Sweep beobachtete Ausnahme: gedrehter Text in `rotated-page.pdf` (Maßstab 1,25) wich an einer Oberkante um bis zu 234 ab, ab 16 Pixeln Überlapp nach oben nicht mehr.
- Ersatzgrenze anwenden: Die Abweichung lag über der Grenze aus dem Plan.
- Graustufen-Antialiasing (`kNormal`) erzwingen: geht über die API nur mit einem 8-Bit-Bitmap oder mit `FPDF_RENDER_NO_SMOOTHTEXT` (ohne Kantenglättung).
**Offener Punkt (M3):** Glyphen, die mehr als 64 Pixel rechts von ihrem Ursprung reichen, gedrehter Text an Ober- oder Unterkanten und die `float`-bedingten Kantenverschiebungen können an Kachelnähten weiterhin sichtbar sein. Kachelgröße und Scheduler (M3) sollten das berücksichtigen, etwa mit Kacheln über die volle Seitenbreite oder mit einer Toleranz in Kacheltests. Ein Fehlerbericht an PDFium (Phase mit `floor` statt Cast) steht aus.

## 2026-10-16 – PDFium-Anbindung: Version 157.0.8100, Dateizugriff, `CULong`, Handles, gemeinsamer Thread
**Kontext:** Milestone 1 bindet PDFium erstmals ein. Festzulegen waren die Paketversion, wie PDFium die Datei liest, wie plattformabhängige C-Typen gebunden werden, wie native Objekte freigegeben werden und wie Kacheln gerendert werden. Grundlage ist der Eintrag „PDFium: ein Thread, eigene Bindings, Pakete ohne V8, Dependabot“.
**Entscheidung:**
- `bblanchon.PDFium.Win32` und `bblanchon.PDFium.Linux` in Version `157.0.8100` (Release `chromium/8100`), zentral in `Directory.Packages.props`, referenziert nur von `Pagewing.Pdfium`. Lizenzen: Paket Apache-2.0, PDFium BSD-3 + Apache-2.0, mitgelinkte Bibliotheken siehe `third-party/pdfium/` (alle GPL-3.0-kompatibel). Native Bibliothek, keine Reflection, damit AOT-fähig.
- Die Drittlizenzen stehen in `third-party/pdfium/`, übernommen aus den Release-Archiven, weil das NuGet-Paket keine Lizenzdateien enthält (Herkunft und Prüfsummen in `third-party/pdfium/README.md`).
- Dateizugriff über `FPDF_LoadCustomDocument` mit `FPDF_FILEACCESS` und Lese-Callback. `FPDF_LoadDocument` und `FPDF_LoadMemDocument*` werden nicht gebunden.
- Jedes `unsigned long` der PDFium-API wird als `CULong` gebunden (`FPDF_GetLastError`, `FPDF_GetMetaText`, `FPDF_FILEACCESS`). Folge: Unter Windows lassen sich Dateien über 4 GiB nicht öffnen. Damit sich beide Betriebssysteme gleich verhalten, gilt die Grenze überall (`PdfiumDocument.MaxFileLength`, Fehler `PdfOpenError.CannotRead`).
- Handles (`PdfiumHandle` in `src/Pagewing.Pdfium/Native/Handles.cs`): Native Objekte werden nur auf dem `PdfiumThread` geschlossen. Wird ein Handle auf einem anderen Thread freigegeben oder finalisiert, reiht es das Schließen per `PdfiumThread.Post` ein. Kind-Handles (Seite, Textseite) halten per `DangerousAddRef` eine Referenz auf ihr Eltern-Handle, bis sie selbst geschlossen sind. `DocumentHandle` gibt nach `FPDF_CloseDocument` den nativ allokierten `FPDF_FILEACCESS` und den Lese-Kontext frei.
- Ein gemeinsamer Thread je Prozess: `PdfiumThread.Shared` ruft beim Start einmal `FPDF_InitLibrary`. Er wird nie beendet, `FPDF_DestroyLibrary` entfällt beim Prozessende.
- Kacheln werden mit `FPDF_RenderPageBitmap_Start` gerendert: Bitmap in Kachelgröße, Start bei minus Kachel-Offset, Größe der ganzen Seite in Pixeln (so rendert Chrome). `FPDF_RenderPageBitmapWithMatrix` wird nicht gebunden.
**Begründung:**
- `FPDF_LoadCustomDocument` liest lazy (schnell auch bei 1000 Seiten), hat keine Probleme mit der Pfadkodierung und erlaubt, die Datei mit `FileShare.ReadWrite | FileShare.Delete` zu öffnen, sodass atomares Ersetzen beim Speichern (M9) nicht blockiert wird. Unter Windows ersetzt `File.Move(…, overwrite: true)` (`MoveFileEx`) eine Datei mit offenen Handles trotzdem nicht („Zugriff verweigert“); `File.Replace` sowie Löschen mit anschließendem Verschieben funktionieren (Test `OpenTests.OpenFileDoesNotBlockDeleteOrReplace`).
- `unsigned long` ist unter Windows 32 Bit und unter Linux 64 Bit breit. Mit `uint` oder `ulong` wäre eine der beiden Plattformen falsch gebunden.
- PDFium ist nicht thread-sicher. Ein Finalizer läuft auf einem beliebigen Thread und darf PDFium deshalb nicht direkt aufrufen. Seiten und Textseiten verweisen auf ihr Dokument, das deshalb erst schließen darf, wenn keine Seite mehr offen ist.
- `FPDF_RenderPageBitmapWithMatrix` ist nicht progressiv, Pause und Abbruch wären damit nicht möglich.
**Verworfene Alternativen:**
- `FPDF_LoadDocument` mit Pfad: Pfadkodierung je Plattform, PDFium hält die Datei selbst offen.
- `FPDF_LoadMemDocument64`: liest die ganze Datei vorab in den Speicher, langsam bei großen Dateien.
- Kacheln über `FPDF_RenderPageBitmapWithMatrix` mit Matrix und Clipping: nicht progressiv. Weicht vom Wortlaut der Roadmap („Matrix + Clipping“) und der Spec (Abschnitt 5.3) ab; beides wird am Ende von Milestone 1 nachgetragen.
- Handles ohne Thread-Bindung beim Schließen: Ein Finalizer würde PDFium von einem fremden Thread aufrufen.

## 2026-10-16 – Testframework xunit.v3 4.0.2 mit Microsoft.Testing.Platform
**Kontext:** Für die Testprojekte war ein Framework zu wählen. Die UI-Tests (ab M3) laufen mit Avalonia.Headless.
**Entscheidung:** xunit.v3 4.0.2 auf Microsoft.Testing.Platform. `global.json` enthält den Test-Runner; `Microsoft.NET.Test.Sdk` und `xunit.runner.visualstudio` werden nicht referenziert.
**Begründung:** `Avalonia.Headless.XUnit` 12.1.4 hängt von `xunit.v3.extensibility.core` 3.2.2 ab. Dazu passt xunit v2 nicht.
**Verworfene Alternativen:** xunit v2 mit VSTest: passt nicht zu `Avalonia.Headless.XUnit`.
**Offener Punkt:** Die Verträglichkeit von `Avalonia.Headless.XUnit` mit xunit.v3 4.0.x wird in M3 geprüft.

## 2026-10-16 – Zentrale Paketverwaltung und Build-Einstellungen im Root, `tools/` isoliert
**Kontext:** `Directory.Build.props` und `Directory.Packages.props` im Root gelten für alle Projekte darunter, auch für die Einzeldatei-Werkzeuge in `tools/` (zum Beispiel `tools/roadmap.cs`, `tools/smoke-cli.cs`). Diese pinnen ihre Pakete selbst mit `#:package <Name>@<Version>`. Beim Spike brach `tools/roadmap.cs` deshalb mit NU1008 (bei zentraler Paketverwaltung darf die Version nicht am Verweis stehen).
**Entscheidung:** Die Paketversionen stehen zentral in `Directory.Packages.props` im Root. Die gemeinsamen Build-Einstellungen (Zielframework, Nullable, Warnungen als Fehler, Analyse-Stufe, deterministischer Build, Version) stehen in `Directory.Build.props` im Root; `IsAotCompatible` setzt jedes Projekt selbst. `tools/Directory.Build.props` und `tools/Directory.Packages.props` isolieren die Werkzeuge davon.
**Begründung:** Eine Stelle für alle Paketversionen macht Dependabot-Updates und Reviews einfach. Die Werkzeuge sind keine Produktcode-Projekte und brauchen weder die strengen Regeln noch die zentrale Verwaltung.
**Verworfene Alternativen:**
- Werkzeuge ebenfalls zentral verwalten: widerspricht dem `#:package`-Modell der Einzeldateien (NU1008).
- Zentrale Verwaltung ganz abschalten: verliert die einheitlichen Versionen für die Projekte.

## 2026-10-16 – Sprachen: Doku Deutsch, Code Englisch, UI Englisch und Deutsch
**Kontext:** Das Template gibt Deutsch für Doku und Commits vor. Pagewing ist aber Open Source und für ein internationales Publikum interessant.
**Entscheidung:**
- Doku und Commits auf Deutsch, Code und Kommentare auf Englisch.
- UI-Texte von Anfang an lokalisierbar (resx), in Englisch und Deutsch. Die Sprache folgt dem System und lässt sich in den Einstellungen ändern.
- Die README hat einen kurzen englischen Abschnitt.
**Begründung:** Das bleibt bei der Vorgabe des Templates und öffnet die App trotzdem für Nutzer außerhalb des deutschsprachigen Raums. Lokalisierung nachträglich einzubauen wäre deutlich teurer.
**Verworfene Alternativen:**
- Alles Englisch außer der Planung: weicht vom Template ab.
- UI nur Deutsch: zu kleine Zielgruppe.

## 2026-10-16 – CI auf eigenen Linux- und Windows-Runnern
**Kontext:** Native AOT kompiliert nicht über Betriebssystemgrenzen hinweg. Ein Windows-Binary muss deshalb auf Windows gebaut werden. Die Orga hat bisher nur selbst gehostete Linux-Runner (`dotnet-linux`), und das Repo ist privat.
**Entscheidung:**
- Ein eigener Windows-Runner mit Label `dotnet-windows`, den der Projektinhaber in M0 einrichtet.
- Build, Test, Format und AOT-Publish laufen auf beiden Runnern, ab M1 kommt die Render-Regression dazu, ab M3 der Startzeit-Check.
**Begründung:** Selbst gehostete Runner liefern die stabilsten Startzeit-Messwerte und kosten keine Actions-Minuten.
**Verworfene Alternativen:**
- `windows-latest` mit öffentlichem Repo: Die Messwerte streuen auf geteilten Runnern stärker.
- `windows-latest` mit privatem Repo: kostenpflichtig.
- Nur Linux-CI: Windows-Regressionen würden zu spät auffallen.
**Hinweis:** Wird das Repo öffentlich, müssen PRs aus Forks vor dem Lauf auf den eigenen Runnern freigegeben werden.

## 2026-10-16 – Drucken plattformeigen, Linux zuerst über X11
**Kontext:** Avalonia hat keine Druck-API. Die Maintainer sehen das nicht als Aufgabe des Frameworks (Issue AvaloniaUI/Avalonia#12567). Wayland unterstützt Avalonia 12.1 nur experimentell und als Opt-in.
**Entscheidung:**
- Drucken ist ein eigener Milestone (M7) hinter der Abstraktion `IPrintService`.
- Windows: Win32-Druckdialog, Ausgabe mit `FPDF_RenderPage` auf den Drucker-HDC.
- Linux: Übergabe an CUPS mit eigenem Avalonia-Dialog.
- Unter Linux nutzt Pagewing zunächst das Standard-Backend X11 bzw. XWayland.
**Begründung:** Es gibt keine reife, AOT-fähige Druckbibliothek für Avalonia (`PrintingTools` ist Alpha, `Avae.Printables` braucht GtkSharp). PDFium kann unter Windows direkt auf den HDC drucken, und CUPS nimmt PDF direkt an.
**Verworfene Alternativen:**
- Drittbibliotheken (siehe Begründung).
- Natives Wayland jetzt: experimentell, steht im Backlog.

## 2026-10-16 – Zusatzbibliotheken: QPDF als Kandidat, Tesseract über eigene Bindings, kein PDFsharp
**Kontext:** PDFium kann Metadaten (Info-Dictionary, XMP) und Lesezeichen nicht schreiben und keine Linien- oder Polygon-Annotationen anlegen. Gebraucht wird das spätestens für Zusammenführen und Schwärzen. Für OCR ist Tesseract gesetzt.
**Entscheidung:**
- QPDF (Apache-2.0, C-API `qpdf-c.h`/`qpdfjob-c.h`) ist der vorgemerkte Kandidat für Strukturoperationen. Die Entscheidung fällt in der Spec von Teilprojekt 3; die Frage Linie/Pfeil schon in der Spec von Teilprojekt 2.
- Tesseract 5.5 läuft über eigene `[LibraryImport]`-Bindings auf `capi.h` im Projekt `Pagewing.Ocr` und wird erst bei Nutzung geladen.
**Begründung:**
- QPDF deckt genau die Lücken von PDFium ab: Info-Keys, Objekt-Handles für Outlines und XMP, Neuschrieb ohne verwaiste Objekte.
- Die .NET-Wrapper für Tesseract (charlesw/tesseract, Sicos1977/TesseractOCR) erzeugen zur Laufzeit Code mit Reflection.Emit und laufen deshalb nicht mit Native AOT.
**Verworfene Alternativen:**
- PDFsharp: ab 6.5.0 (Oktober 2026) AGPL oder kommerziell; 6.2.4 ist die letzte MIT-Version und eingefroren.
- PdfPig: kann bestehende Dokumente nur sehr begrenzt ändern.
- iText und MuPDF: AGPL oder kommerziell.
- RapidOCR/PaddleOCR: bringt ONNX Runtime und SkiaSharp mit, deutlich schwerer.
- Windows.Media.Ocr: nur unter Windows.

## 2026-10-16 – PDFium: ein Thread, eigene Bindings, Pakete ohne V8, Dependabot
**Kontext:** PDFium ist die Engine von Chrome und das Fundament für Geschwindigkeit. Laut `fpdfview.h` ist keine PDFium-API thread-sicher, auch nicht über verschiedene Dokumente hinweg.
**Entscheidung:**
- Pakete `bblanchon.PDFium.Win32` und `bblanchon.PDFium.Linux` (ohne V8 und damit auch ohne XFA).
- Eigene `[LibraryImport]`-Bindings nur für benötigte Funktionen, mit SafeHandles.
- Alle Aufrufe laufen auf einem eigenen `PdfiumThread`, der die Thread-Bindung erzwingt.
- Gerendert wird progressiv mit Pause und Abbruch, statt Kacheln parallel auf mehreren Threads.
- Updates über Dependabot mit Gruppe `bblanchon.PDFium*`, ohne Filter auf minor/patch, weil die Versionsnummer die Chromium-Buildnummer ist.
**Begründung:**
- Paralleles Rendern in einem Prozess ist mit PDFium nicht möglich. Chrome lagert PDFium deshalb in eigene Prozesse aus.
- Eigene Bindings geben volle Kontrolle über AOT, Trimming und Thread-Bindung und bleiben klein.
- Dependabot ist im Template ohnehin vorgesehen, kann gruppieren und versteht Central Package Management.
**Verworfene Alternativen:**
- PDFiumCore: hängt mit 157.0.8100 vom 2026-10-15 inzwischen kaum hinterher, ist aber mit CppSharp generiert, zielt höchstens auf net8 und hat eine riesige Oberfläche.
- Pakete mit V8 (`bblanchon.PDFiumV8.*`): JavaScript in PDFs ist ein Sicherheitsrisiko und Out of Scope.
- Mehrere Render-Threads: PDFium ist nicht thread-sicher.
- Worker-Prozesse jetzt: steht im Backlog, nur bei Bedarf laut Messung.
- Renovate: bringt hier keinen Mehrwert gegenüber Dependabot.
- MuPDF: AGPL oder kommerziell.

## 2026-10-16 – .NET 10 mit Native AOT und Avalonia 12.1
**Kontext:** Startzeit ist das wichtigste Merkmal. Pagewing soll unter Windows und Linux gleich aussehen und modern wirken.
**Entscheidung:**
- C# auf .NET 10 (LTS, Support bis 2028-11-14), App und CLI mit Native AOT.
- UI mit Avalonia 12.1.x (MIT).
- Struktur laut Spec, Abschnitt 5.1: Pdfium, Core, App, Cli plus Testprojekte und `bench/`.
**Begründung:**
- Native AOT spart den JIT-Start und eine installierte Laufzeit.
- Avalonia 12 unterstützt Native AOT auf dem Desktop offiziell und zeichnet über Skia auf beiden Systemen gleich.
- Avalonia 12 bringt unter Linux AT-SPI2-Barrierefreiheit mit.
**Verworfene Alternativen:**
- .NET 11: STS, erscheint erst im November 2026.
- Uno Platform: unter Linux nur X11, keine Linux-Barrierefreiheit.
- .NET MAUI: keine Linux-Unterstützung.
- Electron, Tauri oder Webtechnik: widerspricht dem Startzeit-Ziel bzw. dem Wunsch nach C#.

## 2026-10-16 – Lizenz GPL-3.0-or-later
**Kontext:** Pagewing ist Open Source. Alle Abhängigkeiten sind GPL-3.0-kompatibel: PDFium (BSD-3 + Apache-2.0), Avalonia (MIT), Tesseract (Apache-2.0), QPDF (Apache-2.0).
**Entscheidung:** GPL-3.0-or-later.
**Begründung:**
- Pagewing ist eine Endnutzer-App, so wie die nächsten Vorbilder (SumatraPDF unter GPL-3.0, Okular und Evince unter GPL-2.0+).
- Verbesserungen bleiben offen, Closed-Source-Klone sind ausgeschlossen.
- Die freie Code-Signierung der SignPath Foundation akzeptiert die GPL.
**Verworfene Alternativen:**
- Apache-2.0 und MIT: würden die Einbettung des Kerns in proprietäre Software erlauben, aber auch Closed-Source-Klone.
- MPL-2.0: schwächeres Copyleft, ohne erkennbaren Vorteil für eine App.
