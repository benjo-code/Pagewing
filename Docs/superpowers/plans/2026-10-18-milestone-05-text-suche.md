# Milestone 5 – Text & Suche – Implementierungsplan

> **Für agentische Ausführer:** Pflicht-Skill: `superpowers:subagent-driven-development` (empfohlen) oder `superpowers:executing-plans`. Die Schritte nutzen Checkbox-Syntax (`- [ ]`).

**Ziel:** Text lässt sich durchsuchen (Hintergrundsuche, Trefferliste, Markierung, Weiter/Zurück), mit der Maus markieren und als formatierter Fließtext kopieren, und Links im PDF funktionieren (intern ohne Rückfrage, extern erst nach Bestätigung mit voller Adresse); dazu `pagewing search` in der CLI.

**Architektur:** Eine UI-freie Textschicht in `Pagewing.Core.Text`: `PageText` ist ein unveränderlicher Schnappschuss einer Seite (Zeichen mit Rahmen in Seitenpunkten, Flags, Schrift, Links) und trägt alle Rechnungen (Treffer unter dem Zeiger, Wort, Zeile, Rechtecke), `TextFlow` macht daraus Fließtext, Rohtext und HTML, `TextMatcher`/`DocumentSearch` suchen. PDFium wird nur in `Pagewing.Pdfium` gelesen (ein Auftrag je Seite, Hintergrundpriorität); die App arbeitet danach nur mit dem Schnappschuss, der UI-Thread wartet nie auf PDFium. Das Suchpanel ist ein dritter `SidePanelKind`; Markierungen zeichnet `DocumentCanvas` aus Seitenpunkt-Rechtecken.

**Tech-Stack:** .NET 10 mit Native AOT, Avalonia 12.1.4, PDFium 157.0.8100 über eigene `[LibraryImport]`-Bindings, xunit.v3, Avalonia.Headless, BenchmarkDotNet. Keine neuen Pakete.

**Spec:** [Designsystem](../specs/2026-10-17-designsystem.md) Abschnitte 7.3 und 7.4 (Suche; wird in Task 17 nachgezogen), [Gesamt-Spec](../specs/2026-10-16-pagewing-design.md) Abschnitte 5.4, 6 (M5) und 7 (Sicherheit: externe Links). Roadmap: [Milestone 5](../../ROADMAP.md). Vorgänger: [Plan M4](2026-10-17-milestone-04-navigation.md).

## Scope dieses Plans (am 2026-10-18 mit dem Projektinhaber abgestimmt)

- **Enthalten:** Volltextsuche (Panel „Suche“, Strg+F), Treffermarkierung, Weiter/Zurück (Enter, Umschalt+Enter, F3, Umschalt+F3), Textauswahl mit der Maus (Ziehen, Doppelklick = Wort, Dreifachklick = Zeile), Kopieren mit Strg+C als **formatierter Fließtext** (HTML mit Fett, Kursiv, Überschriften und Absätzen, dazu Klartext) und Strg+Umschalt+C als **Rohtext** mit den Zeilenumbrüchen des PDFs, interne Links (Seite und Position, Alt+Links/Alt+Rechts als Zurück/Vor), externe Links mit Bestätigungskarte, Klartext-Adressen als Links, `pagewing search`, Benchmark der Suche.
- **Bewusst nicht enthalten:** Suche über mehrere Dateien und ein Suchindex (Backlog), Ersetzen, reguläre Ausdrücke, Strg+A (alles markieren), Spaltenerkennung (die Auswahl folgt der Lesereihenfolge des PDFs, siehe Entscheidungen), Kopf- und Fußzeilen beim Kopieren erkennen, `--json` für die CLI, OCR (gescannte Seiten bekommen nur einen Hinweis), Hervorhebung von Text mit Farbe (Textmarkierungen sind M9), Ligaturen in den **Test-PDFs** (nur die Normalisierung ist mit synthetischen Zeichen getestet).
- Die Textschicht ist die Grundlage für spätere Milestones (Schwärzen, OCR, Textmarkierungen); deshalb liegt sie UI-frei in Core und hängt nicht an Avalonia.

## Global Constraints

- .NET 10 (`net10.0`), Native AOT Pflicht; `TreatWarningsAsErrors`; AOT- und Trim-Warnungen sind Fehler; keine Reflection, kein XAML zur Laufzeit; `IsAotCompatible` je Projekt; Avalonia 12.1.4. Testnamen ohne Unterstriche (CA1707). Code und Kommentare Englisch; Doku und Commits Deutsch.
- „PDFium ist nicht thread-sicher“: PDFium wird nur in `Pagewing.Pdfium` und nur auf dem `PdfiumThread` aufgerufen (Aufrufe von anderen Threads lösen `PdfiumThreadViolationException` aus). Kein paralleles PDFium. Der UI-Thread wartet nie auf PDFium; Core-Rechnungen im PDFium-Auftrag dürfen kurz sein (eine Seite).
- Plattformabhängige C-Typen: `unsigned long` immer `CULong`. Konstanten und Signaturen gegen die Header der ausgelieferten PDFium-Version prüfen (`~/.nuget/packages/bblanchon.pdfium.win32/157.0.8100/build/native/include/pdfium/fpdf_text.h`, `fpdf_doc.h`, `fpdfview.h`); `NativeBindingsTests` ergänzen, wo Konstanten dazukommen.
- **Nichts Neues im Startpfad:** Budgets bleiben (400 / 200 / 300 / 100 ms; Fehlschlag ab Budget + 25 %), nie lockern. Textindex und Textladen laufen erst nach dem ersten scharfen Seitenbild (`TabViewModel.HasShownSharpPage`). Die Aktionsleiste ist Teil des ersten Frames: Der neue Knopf „Suche“ ist ein Icon-Knopf wie die anderen, das Panel entsteht erst beim Öffnen. Der Startzeit-Check muss grün bleiben (Task 12 und 17).
- Design: Tokens und Maße nur aus dem Designsystem; Gelb ist nur für Suchtreffer (`search.hit`, `search.current`); Zustände nie nur über Farbe; ein neues Farbpaar vor Verwendung auf WCAG 2.2 AA prüfen (Text 4,5:1, Ränder/Icons 3:1); Seitenmarkierungen sind PDF-Inhalt und in beiden Themes gleich. Icons: Tabler outline, 2 px Strich, nur genutzte Pfade, mit `StrokeIcon`. Panels bringen ihre Styles in der eigenen `.axaml` mit; die Tastatur in Listen führt das Panel selbst (`KeyBindings` des Fensters greifen vor jedem Key-Event, auch vor `TextBox`).
- Sicherheit: Links werden nie automatisch geöffnet; externe Links nur nach Klick **und** Bestätigung mit der vollständigen Adresse; erlaubt sind nur die Schemata `http`, `https` und `mailto`; Datei-, Programm- und `javascript:`-Ziele werden nie ausgeführt und sind nicht klickbar. Keine Netzwerkzugriffe durch Pagewing selbst.
- Git: Branch `feature/m05-text-suche` (liegt an). Gezielt stagen (`git add <datei>`), nach jedem Commit `git show --stat HEAD` gegen die erwartete Dateiliste prüfen, Conventional Commits auf Deutsch, Trailer `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`. Kein Force-Push, kein `--no-verify`. Ein PR nach `dev` am Ende; vor dem Merge alle CI-Checks grün. Die unversionierte Datei `Docs/design/pagewing-icons.zip` gehört nicht zur Aufgabe und wird nie gestaged.
- Roadmap: Status von M5 beim Start der Umsetzung `in-progress`; nach jedem Task die genannten Aufgaben abhaken, `dotnet run tools/roadmap.cs -- generate`, beide Dateien mit committen. Abnahmekriterien erst abhaken, wenn geprüft, mit Nachweis.
- „Fertig“ heißt: `dotnet build Pagewing.slnx`, `dotnet test Pagewing.slnx`, `dotnet format Pagewing.slnx --verify-no-changes`, `dotnet run tools/roadmap.cs -- check` nachweislich grün, dazu der Startzeit-Check in der CI. Einzelne Tests: `dotnet test tests/Pagewing.Core.Tests --filter-class "*TextMatcherTests"` (Microsoft.Testing.Platform), analog `--filter-method` und für `tests/Pagewing.App.Tests`.
- Wo eine Avalonia- oder PDFium-API in diesem Plan anders heißt als in den ausgelieferten Versionen, gilt der Compiler bzw. der Header; die Absicht des Schritts bleibt.

## Review Focus

Eingaben und Zustände, die weder Spec noch Roadmap nennen, die aber vorkommen. Die Prüfung steht jeweils im genannten Task.

1. **Sehr kurze oder häufige Suchbegriffe („e“, „ “) in einem großen Dokument:** Die Trefferzahl ist auf `DocumentSearch.MaxHits` = 10 000 begrenzt, die Zusammenfassung sagt „Mehr als 10 000 Treffer“, die Oberfläche bleibt flüssig, das Panel bleibt bedienbar (Tasks 8, 12).
2. **Seite ohne Text (Scan, Vektorgrafik) und Dokument ganz ohne Text:** Suche überspringt ohne Fehler und ohne Treffer, das Panel sagt, dass kein durchsuchbarer Text vorhanden ist (OCR kommt später); Auswahl zeigt einen Hinweis statt still nichts zu tun; Kopieren einer leeren Auswahl ändert die Zwischenablage nicht (Tasks 7, 12, 14, 15).
3. **Dokument oder Tab ändert sich mitten in Suche, Textladen oder Auswahl** (Tab geschlossen, Datei ersetzt, Fenster geschlossen): Aufträge werden abgebrochen, späte Ergebnisse verworfen, kein Zugriff auf ein freigegebenes Dokument, keine Ausnahme auf einem Pool-Thread (Tasks 8, 12, 14).
4. **Gedrehte Seiten und CropBox:** Trefferrechtecke, Auswahl und Linkflächen liegen auf dem Text (`rotated-page.pdf`, `cropbox.pdf`) (Tasks 3, 7, 13, 14, 16).
5. **Täuschende oder gefährliche Link-Ziele** (`javascript:`, `file:`, `\\server\share`, Adresse mit `user@host`, `xn--` im Host, sehr lange oder mit Steuerzeichen durchsetzte Adressen): nie ohne Bestätigung, gefährliche Schemata gar nicht, Warnhinweis in der Karte (Tasks 9, 16).

## Entscheidungen dieses Plans (eigene sind markiert; sie bekommen einen Eintrag in `Docs/DECISIONS.md`, Task 17)

- **Strg+C kopiert formatierten Fließtext** (Entscheidung des Projektinhabers): Zeilen eines Absatzes werden verbunden, Trennstriche am Zeilenende entfernt, Ligaturen als Buchstaben geschrieben, Absätze bleiben erhalten; dazu HTML mit Fett, Kursiv und Überschriften (`h1`–`h3`), wenn eine Zeile deutlich größer als der Fließtext ist. Schriftart, Schriftgröße im Fließtext und Farbe werden **nicht** kopiert (eingefügter Text soll nicht wie ein zusammengewürfeltes PDF aussehen). Strg+Umschalt+C kopiert Klartext mit den Zeilenumbrüchen des PDFs. *Absatz-, Überschriften- und Silbentrennungsregeln: eigene Heuristik, siehe Task 6; Absätze erkennt nur der senkrechte Abstand, eingerückte Absatzanfänge ohne Mehrabstand bleiben ein Absatz.*
- **Fett und Kursiv** hängen am Schriftnamen, an den Schrift-Flags und am Gewicht, die im PDF stehen (`PdfiumChar.Bold` = Gewicht ≥ 600 oder Flag ForceBold oder Name enthält „Bold“; `Italic` = Flag Italic oder Name enthält „Italic“/„Oblique“). Schriften ohne diese Hinweise kommen ohne Auszeichnung an. Das steht so in der Doku; mehr wird nicht versprochen.
- **Auswahl folgt der Lesereihenfolge des PDFs** (Zeichenindex von PDFium, nicht ein geometrisches Rechteck). Damit mischt eine zweispaltige Seite die Spalten nicht, solange das PDF seinen Text spaltenweise schreibt; eine eigene Spaltenerkennung gibt es nicht. *Eigene Entscheidung.*
- **Auswahl-Modell:** `TextSelection(Anchor, Focus)` aus `TextPosition(Page, Caret)`, wobei `Caret` die Einfügeposition 0…n zwischen Zeichen einer Seite ist; sie gehört zum Tab (wie `SavedView`) und überlebt Zoom und Layoutwechsel, nicht ein neues Dokument. Auswahl über mehrere Seiten ist erlaubt; die Seiten dazwischen werden beim Kopieren geladen. *Eigene Entscheidung.*
- **Suche:** Eigener Abgleich auf dem Text der Seite statt PDFium-`FPDFText_Find*`, damit Ligaturen, Zeilenumbrüche und Trennstriche berücksichtigt werden. Normalisierung beider Seiten mit Unicode-Normalform **FormKC** (löst Ligaturen auf und setzt zerlegte Umlaute zusammen) und `ToLowerInvariant` (außer bei „Groß-/Kleinschreibung beachten“); Leerraum und PDFium-Zeilenumbrüche werden zu einem Leerzeichen zusammengezogen; ein Trennstrich am Zeilenende (Buchstabe, Trennstrich, Zeilenumbruch, Kleinbuchstabe) fällt weg. Das heißt auch: Eine Abfrage mit dem Bindestrich eines **echten** Bindestrich-Worts über einen Zeilenumbruch findet es nicht. Treffer überlappen nicht (nach einem Treffer geht es hinter ihm weiter). Diakritika werden **nicht** ignoriert („uber“ findet „über“ nicht). *Eigene Entscheidung.*
- **Suchreihenfolge:** ab der aktuellen Seite bis zum Ende, dann von Seite 1 bis vor die aktuelle Seite, ein Auftrag je Seite mit Hintergrundpriorität (Rendern bleibt vorrangig). Die Trefferliste ist nach Seite und Zeichenindex sortiert; der „aktuelle Treffer“ ist der erste ab der Ansicht, sobald er da ist. *Eigene Entscheidung.*
- **Suche endet** mit Esc im Suchfeld, mit leerem Suchfeld, mit Esc in der Dokumentansicht und mit einem neuen Dokument im Tab; das Schließen des Panels über den Knopf versteckt nur das Panel, Markierungen und F3/Umschalt+F3 bleiben (Abweichung von Spec 7.4, dort nachgezogen). In schmalen Fenstern schließt ein Klick auf einen Treffer das Panel wie bei Seiten (Spec 7.3). *Eigene Entscheidung.*
- **Links:** Anker-Links (`FPDFLink_Enumerate`) und von PDFium im Text erkannte Adressen (`FPDFLink_LoadWebLinks`); überschneidet sich eine erkannte Adresse mit einer Linkfläche, gilt die Linkfläche. `GoTo` mit Seite und optionaler Position, `URI` nur für `http`, `https`, `mailto`; `Launch`, `RemoteGoTo`, JavaScript und alles andere ist weder klickbar noch hat es einen Hand-Cursor. Die Bestätigungskarte liegt im Fenster (wie `ErrorCard`), kein neues Fenster; Standard ist „Abbrechen“; es gibt kein „Immer erlauben“. *Eigene Entscheidung.*
- **Treffer-Obergrenze** 10 000 (`DocumentSearch.MaxHits`). *Eigene Entscheidung.*
- **CLI `pagewing search`:** Ausgabe pro Treffer eine Zeile `<seite>\t<zeichenindex>\t<vorher>[<treffer>]<nachher>` (Seiten ab 1, Tabulator getrennt, Zeilenumbrüche im Ausschnitt als Leerzeichen); Exit-Code 0 auch ohne Treffer (1 bleibt Fehler, 2 Aufruf); Optionen `--pages`, `--match-case`, `--whole-word`, `--password`. Kein `--json`. *Eigene Entscheidung.*
- **Text-Test-PDFs:** `text-rich.pdf` und `text-dense.pdf` (Task 1) statt Ligaturen im PDF; die Ligatur-Normalisierung testen synthetische `TextChar`-Listen. *Eigene Entscheidung.*

## Ergebnis des Versuchs zur Zwischenablage (Task 2, Wegwerf-Code, nicht übernommen)

**Windows: geht durch (Avalonia 12.1.4).** Geprüft am 2026-10-18 unter Windows 11 mit der Debug-App und einem Wegwerf-Tastenhandler (Strg+Umschalt+F12, danach verworfen). Eine Entscheidung für Task 15:

- **Name und Nutzlast:** ein `DataTransfer` mit **einem** `DataTransferItem`, das beide Formate trägt: `item.SetText(plain)` (Klartext) und `item.Set(DataFormat.CreateBytesPlatformFormat("HTML Format"), bytes)` mit den **UTF-8-Bytes** (ohne BOM, ohne abschließende NUL) der CF_HTML-Nutzlast. Danach `data.Add(item)`, `await topLevel.Clipboard.SetDataAsync(data)` und `await topLevel.Clipboard.FlushAsync()`. Avalonia reicht den Namen unverändert an `RegisterClipboardFormat` weiter (Doku von `CreateBytesPlatformFormat`: „passed AS IS to the underlying platform“); ein eigener Eintrag in `DataFormat.ToSystemName`/`FromSystemName` ist nicht nötig.
- **`CreateStringPlatformFormat("HTML Format")` ist falsch:** Avalonia legt den String als UTF-16 ab (478 statt 239 Bytes, NUL-Bytes zwischen den Zeichen, drei NULs am Ende); die Offsets stimmen dann nicht mehr, ein Leser findet kein Fragment. Immer die Byte-Form nehmen.
- **Offsets-Regel:** `StartHTML`, `EndHTML`, `StartFragment`, `EndFragment` sind **Byte-Offsets** in der UTF-8-Nutzlast (nicht Zeichen; das `Ü` im Fragment ist zwei Bytes, deshalb ist `EndFragment` 205, in Zeichen wären es 204). Kopf: vier Zeilen `Name:` plus **zehnstellige**, mit Nullen aufgefüllte Zahl, Zeilenende `\r\n`; weil die Zahlen feste Breite haben, ist die Kopflänge vorab bekannt (Kopf einmal mit Nullen bauen, `GetByteCount`, dann die Offsets einsetzen). `StartHTML` = Kopflänge, `StartFragment` = `StartHTML` + Bytes von `<html><body>\r\n<!--StartFragment-->`, `EndFragment` = `StartFragment` + Bytes des Fragments, `EndHTML` = `EndFragment` + Bytes von `<!--EndFragment-->\r\n</body></html>` (= Gesamtlänge). Messwerte: `StartHTML` 105, `StartFragment` 139, `EndFragment` 205, `EndHTML` 239.
- **Kopfzeile `Version`:** `0.9` und `1.0` liefern byte-für-byte dieselbe Struktur und denselben gelesenen Fragmenttext; beides nimmt Windows an. Task 15 nimmt `0.9` (wie die Beispiele der Microsoft-Doku und Chromium).
- **Was geprüft ist:** Die App wurde gestartet (`text-basic.pdf`), ihr Fenster per `AppActivate` aktiviert und die Taste per `SendKeys` ausgelöst. Ein **getrennter** PowerShell-Prozess (`-STA`) las die Zwischenablage mit `System.Windows.Forms.Clipboard` (`GetFormats` ergab `UnicodeText, HTML Format`; `GetText()` ergab `Das ist fett und kursiv.` plus Zeilenumbruch plus `Überschrift`) und die rohen Bytes über `RegisterClipboardFormat("HTML Format")`/`GetClipboardData`/`GlobalLock` (Win32). Das Zerlegen der Bytes bei `StartFragment` bis `EndFragment` ergab exakt `<p>Das ist <b>fett</b> und <i>kursiv</i>.</p><h2>Überschrift</h2>` (Umlaut richtig); `EndHTML` ist gleich der Länge der Nutzlast. Die Daten blieben nach dem Beenden der App erhalten (`FlushAsync` wirkt, Lauf `bytes09` danach erneut gelesen). Varianten `bytes09`, `bytes10`, `string09` ausgeführt, nur die Bytes-Form ist gültig.
- **Nicht geprüft:** das tatsächliche Einfügen in Word (Fett, Kursiv und Überschrift formatiert) und in andere Programme; die Aufgabe lief ohne Word. Offen, Befehl: Task-15-Taste bzw. der fertige Kopierbefehl, dann in Word einfügen (Strg+V). Gleichfalls offen ist, ob Word eine fehlende `SourceURL`-Zeile ohne Folgen annimmt (der Kopf hier hat keine).
- **Linux: offen.** Hier nicht prüfbar. Vorgesehen: Name `text/html`, Nutzlast das reine HTML-Fragment als UTF-8-Bytes (`DataFormat.CreateBytesPlatformFormat("text/html")`, gleicher Aufbau wie oben, ohne CF_HTML-Kopf). Prüfen auf einem Linux-Rechner: dieselbe Taste/denselben Kopierbefehl auslösen, dann `xclip -selection clipboard -t TARGETS -o` (X11) bzw. `wl-paste --list-types` (Wayland) auf `text/html` prüfen, `xclip -selection clipboard -t text/html -o` zeigt die Nutzlast, und in LibreOffice Writer einfügen. Fällt das aus, gilt der Rückfall aus Task 2 Step 3 (Klartext mit Fließtext-Regeln); dann den Projektinhaber fragen.

## Dateistruktur

**Neu**
- `src/Pagewing.Pdfium/PdfiumChar.cs`, `PdfiumLink.cs`, `PdfiumPageText.cs` – gelesene Zeichen und Links einer Seite.
- `src/Pagewing.Core/Text/PageRect.cs`, `TextChar.cs`, `PageText.cs`, `PageLink.cs`, `TextSelection.cs`, `TextFlow.cs`, `TextSlice.cs`, `PageTextSource.cs`, `ExternalLink.cs`, `SearchOptions.cs`, `SearchHit.cs`, `TextMatcher.cs`, `DocumentSearch.cs`.
- `src/Pagewing.Cli/Commands/SearchCommand.cs`.
- `src/Pagewing.App/ViewModels/SearchViewModel.cs`, `SearchHitViewModel.cs`, `src/Pagewing.App/Views/SearchPanel.axaml(.cs)`, `Views/DocumentView.Text.cs`, `Views/LinkConfirmCard.axaml(.cs)`, `Services/IClipboardService.cs`, `AvaloniaClipboardService.cs`, `ILinkLauncher.cs`, `AvaloniaLinkLauncher.cs`, `Controls/PageHighlight.cs`.
- Tests: `tests/Pagewing.Core.Tests/Pdfium/PdfiumPageTextTests.cs`, `Text/PageTextTests.cs`, `TextFlowTests.cs`, `TextMatcherTests.cs`, `DocumentSearchTests.cs`, `PageLinksTests.cs`, `ExternalLinkTests.cs`, `Cli/SearchCliTests.cs`; `tests/Pagewing.App.Tests/SearchPanelTests.cs`, `HighlightTests.cs`, `TextSelectionTests.cs`, `CopyTests.cs`, `LinkTests.cs`.
- `testdata/pdfs/text-rich.pdf`, `text-dense.pdf` (erzeugt).

**Geändert**
- `tools/make-testpdfs.cs`, `testdata/pdfs/LICENSES.md`.
- `src/Pagewing.Pdfium/Native/Fpdf.cs`, `FpdfConstants.cs`, `PdfiumTextPage.cs`, `PdfiumPage.cs` (nur Zugriffe).
- `src/Pagewing.Core/Pdf/PdfDocument.cs` (`GetPageTextAsync`, `SearchPageAsync`), `src/Pagewing.Core/Viewing/ViewerSession.cs` (`EnsurePageTexts`).
- `src/Pagewing.Cli/CliApp.cs`, `CliOptions.cs`.
- `src/Pagewing.App`: `ViewModels/SidePanelKind.cs`, `TabViewModel.cs`, `MainViewModel.cs`, `Views/SidePanel.axaml(.cs)`, `ActionBar.axaml`, `MainWindow.axaml(.cs)`, `DocumentView.axaml(.cs)`, `Controls/DocumentCanvas.cs`, `Styles/Tokens.axaml`, `Styles/Icons.axaml`, `Resources/Strings.cs`, `Strings.en.resx`, `Strings.de.resx`.
- `bench/Pagewing.Benchmarks/TextBenchmarks.cs`.
- Doku: `Docs/superpowers/specs/2026-10-17-designsystem.md`, `Docs/DECISIONS.md`, `CLAUDE.md`, `CHANGELOG.md`, `Docs/roadmap.json`, `Docs/ROADMAP.md`.

---

### Task 1: Test-PDFs `text-rich.pdf` und `text-dense.pdf`

**Files:**
- Modify: `tools/make-testpdfs.cs`, `testdata/pdfs/LICENSES.md`
- Create (erzeugt): `testdata/pdfs/text-rich.pdf`, `testdata/pdfs/text-dense.pdf`
- Test: `tests/Pagewing.Core.Tests/TestDataTests.cs` (vorhandene Prüfung der Dateiliste, ggf. um die zwei Namen ergänzen)

**Interfaces:**
- Produces: die zwei Dateien mit dem unten festgelegten Inhalt. Alle späteren Tests verlassen sich auf genau diese Texte und Zahlen.

`text-rich.pdf` (2 Seiten, je 400 × 400 pt, unkomprimiert, deterministisch wie die anderen; Schriften Standard 14 mit `WinAnsiEncoding`: `/F1` Helvetica, `/F2` Helvetica-Bold, `/F3` Helvetica-Oblique). Texte mit `Td` positioniert, Koordinaten in PDF-Punkten mit Ursprung unten links:

| Seite | Schrift/Größe | Position (x, y der Grundlinie) | Text |
|---|---|---|---|
| 1 | F2 24 | 40, 350 | `Pagewing Handbuch` |
| 1 | F1 12 | 40, 310 | `Pagewing ist ein schneller PDF-Viewer für Win-` |
| 1 | F1 12 | 40, 294 | `dows und Linux. Er startet sofort und bleibt` |
| 1 | F1 12 | 40, 278 | `auch bei großen Dokumenten flüssig.` |
| 1 | F1 12 / F2 12 / F1 12 / F3 12 / F1 12 | 40, 246 | `Das ist ` · `fett` · ` und ` · `kursiv` · ` im Satz.` (eine Zeile, vier Schriftwechsel) |
| 1 | F1 12 | 40, 230 | `Zweite Zeile des zweiten Absatzes.` |
| 1 | F1 12 | 40, 190 | `Mehr unter https://example.org/pagewing oder www.example.org.` |
| 1 | F1 12 | 40, 160 | `Dokumentation` – Link-Annotation `[40 156 140 172]`, `/A << /S /URI /URI (https://example.org/docs) >>` |
| 1 | F1 12 | 40, 130 | `Weiter zu Seite zwei` – Link-Annotation `[40 126 170 142]`, `/A << /S /GoTo /D [<Seite 2> /XYZ 0 150 null] >>` |
| 2 | F2 24 | 40, 350 | `Seite zwei` |
| 2 | F1 12 | 40, 150 | `Ziel: Pagewing am Ende.` |

`text-dense.pdf`: 200 Seiten je 612 × 792, Helvetica 10 pt, je Seite 45 Zeilen im Abstand 14 pt ab y = 760; Zeile `r` (0…44) von Seite `p` (1…200) besteht aus 8 Wörtern `W[(p*7 + r*3 + k) mod 20]` für k = 0…7, getrennt durch Leerzeichen, aus der festen Liste `W = [alpha, beta, gamma, delta, epsilon, zeta, eta, theta, iota, kappa, lambda, mu, nu, xi, omicron, rho, sigma, tau, upsilon, phi]`. Zusätzlich steht auf jeder Seite, deren Nummer durch 10 teilbar ist, als Zeile 7 statt der Wörter der Text `Needle in Seite <p>` (Seite 10, 20, …, 200 → 20 Treffer für `Needle`).

- [ ] **Step 1: Failing check** – in `TestDataTests` die zwei Dateinamen in die erwartete Liste aufnehmen. Run: `dotnet test tests/Pagewing.Core.Tests --filter-class "*TestDataTests"`. Expected: FAIL (Dateien fehlen).
- [ ] **Step 2: Implementieren** – in `tools/make-testpdfs.cs` zwei Einträge `new("text-rich.pdf", TextRich())` und `new("text-dense.pdf", TextDense())` im Stil der vorhandenen Fabriken (`TextBasic()`, `ThousandPages()`); die Link-Annotationen und das `/XYZ`-Ziel wie in `annotations.pdf` aufbauen (Objektnummern der Zielseite vorab festlegen).
- [ ] **Step 3: Erzeugen und prüfen** – `dotnet run tools/make-testpdfs.cs -- generate`, danach `dotnet run tools/make-testpdfs.cs -- check`. Expected: Exit 0, beide Dateien vorhanden, `text-dense.pdf` unter 1,5 MB.
- [ ] **Step 4: Lizenzen** – in `testdata/pdfs/LICENSES.md` zwei Zeilen im Format der vorhandenen („selbst erzeugt, Pagewing-Generator, keine Fremdteile“) mit dem Inhalt der Tabelle in Kurzform.
- [ ] **Step 5: Test grün** – `dotnet test tests/Pagewing.Core.Tests --filter-class "*TestDataTests"` → PASS.
- [ ] **Step 6: Commit** – `git add tools/make-testpdfs.cs testdata/pdfs/text-rich.pdf testdata/pdfs/text-dense.pdf testdata/pdfs/LICENSES.md tests/Pagewing.Core.Tests/TestDataTests.cs`; `test: Test-PDFs für Text, Suche und Links`.

---

### Task 2: Versuch zur Zwischenablage (Wegwerf, nicht übernommen)

Ziel: früh wissen, ob formatierter Text in die Zwischenablage kommt, bevor die Textschicht gebaut wird. Der Versuch ändert keine Dateien dauerhaft.

**Files:**
- Temporär ändern: `src/Pagewing.App/Views/MainWindow.axaml.cs` (Taste Strg+Umschalt+F12), danach `git checkout -- src/Pagewing.App/Views/MainWindow.axaml.cs`.
- Ändern: dieser Plan, Abschnitt „Ergebnis des Versuchs zur Zwischenablage“.

**Interfaces:**
- Produces: die Entscheidung für Task 15: Name und Nutzlast des HTML-Formats je Betriebssystem.

- [ ] **Step 1: Versuchscode** – im Tastenhandler des Fensters mit `TopLevel.Clipboard` ein `DataTransfer` mit zwei Einträgen setzen: Klartext (`DataTransferItem.CreateText`) und HTML (`DataFormat.CreateBytesPlatformFormat(name)` mit UTF-8-Bytes). Windows: Name `HTML Format`, Nutzlast im CF_HTML-Format (Kopfzeilen `Version:0.9`, `StartHTML:`, `EndHTML:`, `StartFragment:`, `EndFragment:` mit zehnstelligen Byte-Offsets, `<!--StartFragment-->`/`<!--EndFragment-->` um das Fragment), Fragment `<p>Das ist <b>fett</b> und <i>kursiv</i>.</p><h2>Überschrift</h2>`. Linux: Name `text/html`, Nutzlast das reine HTML-Fragment. Über `IClipboard.SetDataAsync` ablegen und `FlushAsync` aufrufen.
- [ ] **Step 2: Prüfen** – App bauen (`dotnet build src/Pagewing.App -c Debug`), starten, Taste drücken, in Word (Windows) bzw. einen Browser-Editor/LibreOffice (Linux) einfügen. Erwartet: Fett, Kursiv und Überschrift erscheinen formatiert, der Umlaut ist richtig; in Notepad erscheint der Klartext. Wenn Word den Text unformatiert einfügt: Offsets im CF_HTML-Kopf prüfen (Bytes, nicht Zeichen), `Version:1.0` versuchen; wenn Avalonia den Namen nicht an das System weitergibt, in `DataFormat.ToSystemName`/`FromSystemName` nachlesen und die Variante festhalten. Linux nur prüfen, wenn ein Linux-Rechner verfügbar ist, sonst als **offen** vermerken (Befehl: dieselbe Taste, Einfügen in LibreOffice Writer).
- [ ] **Step 3: Ergebnis festhalten** – Abschnitt „Ergebnis des Versuchs“ dieses Plans mit Format, Offsets-Regel und Beobachtung füllen. Geht HTML in keinem Fall durch, den Projektinhaber fragen, bevor Task 15 gebaut wird (Rückfall wäre Klartext mit Fließtext-Regeln, ohne Fett/Kursiv).
- [ ] **Step 4: Aufräumen und Commit** – `git checkout -- src/Pagewing.App/Views/MainWindow.axaml.cs`; `git status` zeigt nur die Plan-Datei geändert; `git add Docs/superpowers/plans/2026-10-18-milestone-05-text-suche.md`; `docs: Ergebnis des Zwischenablage-Versuchs (M5)`.

---

### Task 3: PDFium – Bindings, Zeichen und Links einer Seite lesen

**Files:**
- Modify: `src/Pagewing.Pdfium/Native/Fpdf.cs`, `src/Pagewing.Pdfium/Native/FpdfConstants.cs`, `src/Pagewing.Pdfium/PdfiumTextPage.cs`
- Create: `src/Pagewing.Pdfium/PdfiumChar.cs`, `PdfiumLink.cs`, `PdfiumPageText.cs`
- Test: `tests/Pagewing.Core.Tests/Pdfium/PdfiumPageTextTests.cs`, `tests/Pagewing.Core.Tests/Pdfium/NativeBindingsTests.cs` (Konstanten)

**Interfaces:**
- Consumes: `PdfiumDocument.LoadPage(int)`, `PdfiumPage.LoadTextPage()`, `PdfiumTextPage` (Handle über neues `internal nint Access()`), `PdfiumThread`.
- Produces (alles `internal`, Aufruf nur auf dem PDFium-Thread):
```csharp
internal readonly record struct PdfiumChar(
    int CodePoint,          // Unicode scalar; U+FFFD if PDFium has none
    double Left, double Top, double Right, double Bottom, // page points, origin top-left of the displayed page, Top < Bottom; all 0 when geometry was not read
    bool Generated,         // inserted by PDFium: spaces and line breaks (CR, LF)
    bool Hyphen,
    float FontSize,         // 0 when style was not read
    bool Bold, bool Italic);

internal readonly record struct PdfiumLink(
    IReadOnlyList<(double Left, double Top, double Right, double Bottom)> Rects, // page points, origin top-left
    PdfiumLinkKind Kind,    // Page, Uri, Unsupported
    int PageIndex,          // Kind == Page, else -1
    double? Top,            // Kind == Page: target position in points from the page top
    string? Uri);           // Kind == Uri

internal enum PdfiumLinkKind { Page, Uri, Unsupported }

internal static class PdfiumPageText
{
    // pageCount: pages of the document (to validate targets).
    public static PdfiumChar[] ReadChars(PdfiumPage page, PdfiumTextPage text, bool geometry, bool style);
    public static PdfiumChar[] ReadChars(PdfiumPage page, PdfiumTextPage text, int start, int count); // geometry and style, for hits
    public static PdfiumLink[] ReadLinks(PdfiumDocument document, PdfiumPage page, PdfiumTextPage text);
}
```
- Koordinaten: PDFium liefert Benutzerraum (Ursprung unten links, vor Drehung). Umrechnung mit `FPDF_PageToDevice(page, 0, 0, W, H, 0, x, y, &dx, &dy)` mit `W = round(page.Width * 16)`, `H = round(page.Height * 16)` und Division durch 16 (Ganzzahl-API, 1/16 pt Genauigkeit); sie berücksichtigt Drehung und CropBox. Die Ecken eines Zeichenrahmens (`FPDFText_GetCharBox` → left, right, bottom, top) werden beide umgerechnet und zu einem achsenparallelen Rahmen vereinigt.
- Zu binden (Namen/Signaturen gegen Header prüfen): `FPDFText_GetUnicode` (liefert `uint`), `FPDFText_IsGenerated`, `FPDFText_IsHyphen`, `FPDFText_GetFontSize`, `FPDFText_GetFontInfo`, `FPDFText_GetFontWeight`, `FPDFText_GetCharBox`, `FPDF_PageToDevice`, `FPDFLink_LoadWebLinks`, `FPDFLink_CountWebLinks`, `FPDFLink_GetURL`, `FPDFLink_CountRects`, `FPDFLink_GetRect`, `FPDFLink_CloseWebLinks`, `FPDFLink_Enumerate`, `FPDFLink_GetAnnotRect`, `FPDFLink_GetAction`, `FPDFLink_GetDest`, `FPDFAction_GetURIPath`, `FPDFDest_GetLocationInPage`. `FpdfConstants`: `ActionUri` (3, gegen `fpdf_doc.h` prüfen), Schrift-Flags `FontFlagForceBold` (0x40000) und `FontFlagItalic` (0x40).
- Zeichenindex = PDFium-Zeichenindex (`0 … CountChars-1`); ein Zeichen kann aus zwei UTF-16-Einheiten bestehen, `CodePoint` trägt den ganzen Wert.

- [ ] **Step 1: Failing tests** in `PdfiumPageTextTests` (Hilfsmethode wie in `PdfiumTextPageTests`: Auftrag auf `PdfiumThread.Shared`):
```csharp
[Fact] public async Task CharsOfTheHeadingHaveBoxesAndBold()
{   // text-rich.pdf page 1: "Pagewing Handbuch"
    var chars = await ReadPage("text-rich.pdf", 0, geometry: true, style: true);
    var heading = chars.Where(c => !c.Generated).Take(17).ToArray();
    Assert.Equal("Pagewing Handbuch", string.Concat(heading.Select(c => char.ConvertFromUtf32(c.CodePoint))));
    Assert.All(heading, c => { Assert.True(c.Bold); Assert.False(c.Italic); Assert.InRange(c.FontSize, 23.5f, 24.5f); });
    Assert.InRange(heading[0].Left, 38, 42);              // x = 40 pt
    Assert.InRange(heading[0].Top, 20, 60);               // y from the top: 400 - 350 - ascent
    Assert.True(heading[0].Bottom > heading[0].Top);
}
[Fact] public async Task LineBreaksAreGeneratedAndTheHyphenIsFlagged() { /* "Win-" then generated CR/LF then "dows": Hyphen true on '-', Generated true on the CR LF pair */ }
[Fact] public async Task BoldAndItalicRunsAreMarkedInOneLine() { /* "fett" Bold && !Italic, "kursiv" Italic && !Bold, " und " neither */ }
[Fact] public async Task GeometryFollowsRotationAndCropBox() { /* rotated-page.pdf: first char box lies inside 0..Width/0..Height of the rotated page; cropbox.pdf: boxes inside the cropped size */ }
[Fact] public async Task LinksOfThePageAreAnchorsAndWebAddresses()
{   // page 1 of text-rich.pdf
    // 1 Uri "https://example.org/docs" over "Dokumentation"; 1 Page link PageIndex 1, Top ≈ 400-150 (±2) over "Weiter zu Seite zwei";
    // web links "https://example.org/pagewing" and a "www.example.org" address (Uri starts with "http" or "www", contains "example.org")
}
[Fact] public async Task ForeignThreadCallThrows() { /* ReadChars/ReadLinks from the test thread -> PdfiumThreadViolationException */ }
```
und in `NativeBindingsTests` die Konstanten (`ActionUri`, Schrift-Flags) gegen die Header.
- [ ] **Step 2: Run** `dotnet test tests/Pagewing.Core.Tests --filter-class "*PdfiumPageTextTests"` → FAIL (Typen fehlen).
- [ ] **Step 3: Implement** Bindings und Konstanten, `PdfiumTextPage.Access()` auf `internal` und `PdfiumPageText` nach den Interfaces. Regeln: `Generated` aus `FPDFText_IsGenerated`; `Hyphen` aus `FPDFText_IsHyphen`; `Bold`/`Italic` wie in „Entscheidungen“; Linktyp aus `FPDFAction_GetType` (GoTo → `FPDFAction_GetDest` → `FPDFDest_GetDestPageIndex`, Position aus `FPDFDest_GetLocationInPage` nur mit gesetztem y, in Seitenpunkte von oben umgerechnet; URI → `FPDFAction_GetURIPath`; alles andere `Unsupported`); Web-Links über `FPDFLink_*WebLinks`, Linkflächen derselben Adresse, die eine Anker-Linkfläche schneiden, entfallen; `ReadLinks` schließt den Web-Link-Handle in `finally`. Kein Wurf aus `ReadChars` bei leerer Seite (leeres Feld).
- [ ] **Step 4: Run** beide Testklassen → PASS; `dotnet build Pagewing.slnx` ohne Warnungen.
- [ ] **Step 5: Commit** – gezielt stagen; `feat(pdfium): Zeichen, Schriftdaten und Links einer Seite lesen`.

---

### Task 4: Core – Textmodell und `PdfDocument.GetPageTextAsync`

**Files:**
- Create: `src/Pagewing.Core/Text/PageRect.cs`, `TextChar.cs`, `PageLink.cs`, `PageText.cs` (nur Daten und Fabrik; Rechnungen in Task 5)
- Modify: `src/Pagewing.Core/Pdf/PdfDocument.cs`
- Test: `tests/Pagewing.Core.Tests/Text/PageTextTests.cs`

**Interfaces:**
- Consumes: `PdfiumPageText.ReadChars/ReadLinks`, `PdfiumDocument.LoadPage`, `PdfPriority`.
- Produces:
```csharp
namespace Pagewing.Core.Text;

/// <summary>Rectangle in PDF points, origin at the top left of the displayed page (after rotation and CropBox), y down.</summary>
public readonly record struct PageRect(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
    public bool Contains(double x, double y);
    public PageRect Union(PageRect other);
    public PageRect Inflate(double amount);
}

[Flags] public enum TextCharFlags { None = 0, Generated = 1, Hyphen = 2, Bold = 4, Italic = 8 }

public readonly record struct TextChar(int CodePoint, PageRect Box, TextCharFlags Flags, float FontSize)
{
    public bool IsGenerated => (Flags & TextCharFlags.Generated) != 0;
    public bool IsLineBreak => IsGenerated && CodePoint is '\r' or '\n';
    public string Text { get; }                 // the character as a string
}

public abstract record LinkTarget;
public sealed record PageTarget(int PageIndex, double? Top) : LinkTarget;   // Top: points from the page top, null = top of the page
public sealed record UriTarget(string Url) : LinkTarget;
public sealed record PageLink(IReadOnlyList<PageRect> Rects, LinkTarget Target);

public sealed class PageText
{
    public PageText(int pageIndex, double width, double height, IReadOnlyList<TextChar> chars, IReadOnlyList<PageLink> links);
    public int PageIndex { get; }
    public double Width { get; }
    public double Height { get; }
    public IReadOnlyList<TextChar> Chars { get; }
    public IReadOnlyList<PageLink> Links { get; }
    public bool HasText { get; }                // any char that is not generated
    public float BodyFontSize { get; }          // most frequent font size of the non-generated chars, rounded to 0.5; 0 without text
    public string GetText(int start, int end);  // chars [start, end) as a string, no processing
}

// PdfDocument
public Task<PageText> GetPageTextAsync(int pageIndex, CancellationToken cancellationToken = default); // Interactive priority
```
`PageLink` aus `PdfiumLink`: `Page` → `PageTarget`, `Uri` → `UriTarget`, `Unsupported` entfällt.

- [ ] **Step 1: Failing tests** (`PageTextTests`, mit `PdfEngine` und `text-rich.pdf`):
```csharp
[Fact] public async Task PageTextHasCharsLinksAndSize()
{
    var text = await document.GetPageTextAsync(0, ct);
    Assert.Equal(0, text.PageIndex); Assert.Equal(400, text.Width, 0.5); Assert.Equal(400, text.Height, 0.5);
    Assert.True(text.HasText);
    Assert.Equal("Pagewing Handbuch", text.GetText(0, 17));
    Assert.Equal(12f, text.BodyFontSize);
    Assert.Single(text.Links, l => l.Target is UriTarget { Url: "https://example.org/docs" });
    var jump = Assert.Single(text.Links, l => l.Target is PageTarget);
    var target = (PageTarget)jump.Target;
    Assert.Equal(1, target.PageIndex);
    Assert.InRange(target.Top!.Value, 248, 252);              // 400 - 150
}
```
```csharp
[Fact] public async Task PageWithoutTextHasNoText() { /* vector-shapes.pdf: HasText false, BodyFontSize 0, Chars empty or all generated */ }
[Fact] public async Task GetPageTextRejectsAPageOutsideTheDocument() { /* ArgumentOutOfRangeException */ }
[Fact] public async Task CancelledRequestThrows() { /* OperationCanceledException */ }
```
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement** die Typen und `GetPageTextAsync`: ein `document.Thread.InvokeAsync`-Auftrag mit `PdfiumPriority.Interactive`, der Seite und Textseite lädt, `ReadChars(..., geometry: true, style: true)` und `ReadLinks` aufruft und zu `PageText` abbildet; `TextChar.Text` mit `char.ConvertFromUtf32` (Ersatz U+FFFD bei ungültigem Wert).
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat(core): PageText – Zeichen, Rahmen, Schrift und Links einer Seite`.

---

### Task 5: Core – Rechnungen auf `PageText` (Treffer, Wort, Zeile, Rechtecke)

**Files:**
- Modify: `src/Pagewing.Core/Text/PageText.cs`
- Create: `src/Pagewing.Core/Text/TextSelection.cs`
- Test: `tests/Pagewing.Core.Tests/Text/PageTextTests.cs` (synthetische Seiten, ohne PDFium)

**Interfaces:**
- Consumes: Typen aus Task 4. Tests bauen `PageText` aus einer Hilfsfabrik `Line(text, x, baselineTop, size)`, die je Zeichen einen Rahmen `size × size`-Breite 0,5·size und Zeilenumbruchzeichen (`Generated`, `\r`, `\n`) zwischen Zeilen erzeugt.
- Produces:
```csharp
// PageText
public int CharAt(double x, double y);                        // index of the non-generated char whose box (inflated by 1 pt) contains the point, else -1
public int CaretAt(double x, double y);                       // insertion position 0..Chars.Count nearest to the point (always valid; text order, nearest line then left/right half of the nearest char)
public (int Start, int End) WordAt(int charIndex);            // [Start, End): run of letters/digits/apostrophes around the char; a single char otherwise
public (int Start, int End) LineAt(int charIndex);           // [Start, End): between line-break chars, without them
public IReadOnlyList<PageRect> RectsOf(int start, int end);  // one rectangle per line segment: union of the boxes of the non-generated chars in [start, end) between line breaks
public PageLink? LinkAt(double x, double y);                  // topmost link whose rect contains the point

public readonly record struct TextPosition(int Page, int Caret) : IComparable<TextPosition>;
public readonly record struct TextSelection(TextPosition Anchor, TextPosition Focus)
{
    public TextPosition Start { get; }     // the smaller of Anchor and Focus
    public TextPosition End { get; }
    public bool IsEmpty { get; }           // Start == End
}
```

- [ ] **Step 1: Failing tests** (jeweils mit zwei Zeilen „Hello world“ / „second line“, Größe 10, Zeilenabstand 14):
```csharp
[Fact] public void CharAtFindsTheCharUnderThePointAndNothingBetweenLines() { /* point in 'w' of "world" -> index of 'w'; point in the gap between lines -> -1 */ }
[Fact] public void CaretAtPicksTheNearestInsertionPosition() { /* left half of 'w' -> caret before 'w'; right half -> after; far below the text -> Chars.Count; far above -> 0 */ }
[Fact] public void WordAtSelectsTheWordAndPunctuationAloneIsOneChar() { /* "Hello, world": index in 'e' -> (0,5); the comma -> (5,6) */ }
[Fact] public void LineAtStopsAtGeneratedLineBreaks() { /* index in "second line" -> its first and last char, the CR LF before it excluded */ }
[Fact] public void RectsOfGivesOneRectanglePerLine() { /* selection across both lines: 2 rects, the first ends at the last char of line 1, rects do not contain the line-break chars */ }
[Fact] public void RectsOfAnEmptyRangeIsEmpty() { }
[Fact] public void LinkAtReturnsTheLinkUnderThePoint() { }
[Fact] public void TextSelectionOrdersAnchorAndFocus() { /* Focus before Anchor -> Start == Focus; same page compares Caret, else Page */ }
```
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement** nach den Kommentaren der Signaturen (reine Rechnung, keine Allokation je Mausbewegung über eine Listenkopie hinaus; `CaretAt`/`CharAt` dürfen linear über die Zeichen laufen: eine Seite hat einige tausend). **Step 4: Run** → PASS. **Step 5: Commit** `feat(core): Treffer, Wort, Zeile und Rechtecke auf PageText`.

---

### Task 6: Core – Kopieren: Fließtext, Rohtext, HTML

**Files:**
- Create: `src/Pagewing.Core/Text/TextSlice.cs`, `TextFlow.cs`
- Test: `tests/Pagewing.Core.Tests/Text/TextFlowTests.cs` (synthetisch) und `tests/Pagewing.Core.Tests/Text/TextFlowDocumentTests.cs` (`text-rich.pdf`)

**Interfaces:**
- Consumes: `PageText`, `TextChar`, `TextSelection`.
- Produces:
```csharp
public readonly record struct TextSlice(PageText Page, int Start, int End);   // chars [Start, End) of one page

public static class TextFlow
{
    /// <summary>Selected text as continuous prose: lines of a paragraph joined, hyphens at line ends removed, ligatures expanded, paragraphs separated by one blank line.</summary>
    public static string Plain(IReadOnlyList<TextSlice> slices);

    /// <summary>The text as the PDF has it: PDFium line breaks become "\n", nothing joined, nothing removed.</summary>
    public static string Raw(IReadOnlyList<TextSlice> slices);

    /// <summary>An HTML fragment (no html/body): p, h1-h3, b, i, text escaped; no fonts, sizes in text, or colors.</summary>
    public static string Html(IReadOnlyList<TextSlice> slices);

    /// <summary>The slices a selection covers on the given pages (pages are the loaded PageText objects in page order); empty ranges are dropped.</summary>
    public static IReadOnlyList<TextSlice> SlicesOf(TextSelection selection, Func<int, PageText> pageOf);
}
```
Regeln (alle Schwellen sind eigene Entscheidungen):
1. **Zeilen:** Zeilenumbruchzeichen (`IsLineBreak`) trennen Zeilen; generierte Leerzeichen bleiben Leerzeichen; Zeilen ohne sichtbare Zeichen entfallen. Die Grenze zwischen zwei Seiten zählt wie ein Zeilenumbruch.
2. **Absätze:** Eine neue Zeile beginnt einen neuen Absatz, wenn der senkrechte Abstand zwischen Unterkante der vorigen und Oberkante der nächsten Zeile größer als `0,5 × FontSize` der vorigen Zeile ist. An der Seitengrenze gibt es keinen Abstand; dort beginnt nur dann ein neuer Absatz, wenn die vorige Zeile mit `.`, `!`, `?` oder `:` endet.
3. **Verbinden:** Zeilen eines Absatzes werden mit einem Leerzeichen verbunden, **außer** die vorige Zeile endet auf einen Trennstrich (`Hyphen`-Flag, `-` oder U+00AD) direkt hinter einem Buchstaben **und** die nächste Zeile beginnt mit einem Kleinbuchstaben: dann entfällt der Strich und es gibt kein Leerzeichen („Win-“ + „dows“ → „Windows“). Mehrfache Leerzeichen bleiben als eines.
4. **Ligaturen:** U+FB00…U+FB06 → `ff`, `fi`, `fl`, `ffi`, `ffl`, `st`, `st`.
5. **Überschriften (nur `Html`):** Eine Zeile mit höchstens 100 sichtbaren Zeichen, deren größte Schrift `≥ 1,6 × BodyFontSize` ist, wird `h1`, `≥ 1,3 ×` `h2`, `≥ 1,15 ×` `h3`; sie ist ein eigener Absatz. `BodyFontSize` kommt aus der Seite, nicht aus der Auswahl.
6. **Auszeichnung (nur `Html`):** zusammenhängende Zeichenfolgen mit `Bold` bzw. `Italic` in `<b>`/`<i>` (ineinander verschachtelt erlaubt); Leerzeichen übernehmen die Auszeichnung des vorigen Zeichens; in `h1`–`h3` entfällt `<b>`.
7. HTML-Escaping von `&`, `<`, `>`, `"`; Ausgabe ohne Zeilenumbrüche zwischen den Blöcken außer `\n` nach jedem Block.

- [ ] **Step 1: Failing tests**:
```csharp
[Fact] public void PlainJoinsLinesOfAParagraphAndKeepsParagraphs()
{   // three tight lines, a larger gap, two tight lines
    Assert.Equal("Erste Zeile zusammen weiter.\n\nZweiter Absatz hier.", TextFlow.Plain(slices));
}
[Fact] public void PlainRemovesAHyphenAtTheLineEndBeforeALowercaseLetter() { /* "Win-" / "dows" -> "Windows" */ }
[Fact] public void PlainKeepsTheHyphenBeforeACapitalLetterAndAfterADigit() { /* only letter + hyphen + lowercase joins: "Bindestrich-" / "Wort" -> "Bindestrich- Wort"; "A1-" / "b" -> "A1- b" */ }
[Fact] public void PlainExpandsLigatures() { /* U+FB01 U+FB03 -> "fi" "ffi" */ }
[Fact] public void RawKeepsLineBreaksAsNewlines() { /* "Hello\nworld" */ }
[Fact] public void HtmlMarksBoldItalicAndHeadings()
{   // line of size 24 over body 12 -> <h1>…</h1>; bold word -> <b>fett</b>
}
[Fact] public void HtmlEscapesMarkup() { /* "a < b & c" -> "a &lt; b &amp; c" */ }
[Fact] public void SelectionAcrossPagesIsOneFlow() { /* last line of page 0 + first of page 1 are joined as lines, not as paragraphs */ }
[Fact] public async Task RichDocumentCopiesAsProse()
{   // text-rich.pdf page 1, selection = whole text of the page
    Assert.StartsWith("Pagewing Handbuch\n\nPagewing ist ein schneller PDF-Viewer für Windows und Linux. Er startet sofort und bleibt auch bei großen Dokumenten flüssig.\n\nDas ist fett und kursiv im Satz. Zweite Zeile des zweiten Absatzes.", plain);
    Assert.Contains("<h1>Pagewing Handbuch</h1>", html);
    Assert.Contains("<b>fett</b>", html); Assert.Contains("<i>kursiv</i>", html);
}
[Fact] public void SlicesOfDropsEmptyRangesAndSpansPages() { }
```
(Der Test `RichDocumentCopiesAsProse` prüft die Absatzregel am echten PDF: Überschrift → Absatz mit 26 pt, Absatz → Absatz mit 20 pt Abstand, Zeilen im Absatz mit 4 pt; schlägt er fehl, wird die Regel überprüft, nicht der Test angepasst.)
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement** (ein statischer Zeilen-Aufbau, den `Plain`, `Raw` und `Html` teilen). **Step 4: Run** → PASS. **Step 5: Commit** `feat(core): TextFlow – Fließtext, Rohtext und HTML aus der Auswahl`.

---

### Task 7: Core – Suche: `TextMatcher` und `PdfDocument.SearchPageAsync`

**Files:**
- Create: `src/Pagewing.Core/Text/SearchOptions.cs`, `SearchHit.cs`, `TextMatcher.cs`
- Modify: `src/Pagewing.Core/Pdf/PdfDocument.cs`
- Test: `tests/Pagewing.Core.Tests/Text/TextMatcherTests.cs` (synthetisch), `tests/Pagewing.Core.Tests/Text/SearchPageTests.cs`

**Interfaces:**
- Consumes: `TextChar`, `PdfiumPageText.ReadChars(page, text, start, count)`, `PageText.RectsOf`-Logik (die Rechnung wird in ein `internal static` `TextGeometry.RectsOf(IReadOnlyList<TextChar> chars, int start, int end)` verschoben, das `PageText.RectsOf` und die Suche teilen).
- Produces:
```csharp
public readonly record struct SearchOptions(bool MatchCase = false, bool WholeWord = false);

public readonly record struct TextMatch(int FirstChar, int LastChar);   // inclusive char indices of a page

public sealed record SearchSnippet(string Before, string Match, string After);   // up to 30 chars each side, never across a line break of the match's first/last line; a match over several lines has its text with line breaks as spaces

public sealed record SearchHit(int PageIndex, int FirstChar, int LastChar, IReadOnlyList<PageRect> Rects, SearchSnippet Snippet);

public static class TextMatcher
{
    /// <summary>Finds the query in the chars of one page. An empty or whitespace-only query finds nothing.</summary>
    public static IReadOnlyList<TextMatch> Find(IReadOnlyList<TextChar> chars, string query, SearchOptions options);
}

// PdfDocument
public Task<IReadOnlyList<SearchHit>> SearchPageAsync(int pageIndex, string query, SearchOptions options, CancellationToken cancellationToken = default); // Background priority
```
Normalisierung wie in „Entscheidungen“ (FormKC, `ToLowerInvariant`, Leerraum zusammenziehen, Trennstrich am Zeilenende entfernen); der Zuordnungsvektor Suchtext-Position → Zeichenindex entsteht beim Aufbau. `WholeWord`: vor und hinter dem Treffer steht im Suchtext kein Buchstabe oder keine Ziffer. `SearchPageAsync`: ein Auftrag auf dem PDFium-Thread: Zeichen **ohne** Geometrie lesen, `TextMatcher.Find`, für die Treffer die Zeichen des Bereichs mit Geometrie lesen und `RectsOf` anwenden, Ausschnitt aus den Zeichen bilden; Seiten ohne Text liefern eine leere Liste.

- [ ] **Step 1: Failing tests** (`TextMatcherTests`, Zeichen aus Strings über eine Hilfsfabrik; `\n` = Generated-Zeilenumbruch):
```csharp
[Theory]
[InlineData("Hello World", "world", false, 1)]
[InlineData("Hello World", "world", true, 0)]
public void MatchCaseSwitchesCaseSensitivity(string text, string query, bool matchCase, int expected) { }
[Fact] public void LigatureCharsMatchTheirLetters() { /* chars U+FB01 'n' 'e' (ﬁne) -> query "fine" finds FirstChar 0, LastChar 2; query "ﬁne" also finds it */ }
[Fact] public void DecomposedUmlautsMatchComposedOnes() { /* "u"+U+0308 vs "ü" both ways */ }
[Fact] public void AMatchCrossesALineBreak() { /* "hello\nworld", query "hello world" -> one match over both lines */ }
[Fact] public void AHyphenAtTheLineEndIsIgnored() { /* "Win-\ndows" query "Windows" -> 1; "Bindestrich-\nWort" query "Bindestrich-Wort" -> 0 (documented) */ }
[Fact] public void WholeWordIgnoresPartsOfWords() { /* "cat catalog" query "cat" whole word -> 1, otherwise 2 */ }
[Fact] public void MatchesDoNotOverlap() { /* "aaaa" query "aa" -> 2 matches */ }
[Fact] public void EmptyAndWhitespaceQueriesFindNothing() { }
[Fact] public void DiacriticsAreNotIgnored() { /* "über" is not found by "uber" */ }
```
`SearchPageTests` (mit `text-rich.pdf`, `rotated-page.pdf`, `vector-shapes.pdf`, `text-basic.pdf`):
```csharp
[Fact] public async Task FindsEveryOccurrenceOnAPageWithPositions()
{   // "pagewing" case-insensitive on page 0: 3 hits (heading, first sentence, address in the URL line)
    Assert.Equal(3, hits.Count);
    Assert.Equal(0, hits[0].FirstChar); Assert.Equal(7, hits[0].LastChar);
    Assert.InRange(hits[0].Rects[0].Left, 38, 42); Assert.InRange(hits[0].Rects[0].Top, 20, 60);
    Assert.Equal(new SearchSnippet("", "Pagewing", " Handbuch"), hits[0].Snippet);
}
[Fact] public async Task MatchCaseOnlyFindsTheCapitalisedOnes() { /* 2 on page 0 */ }
[Fact] public async Task FindsAWordSplitByAHyphenatedLineBreak() { /* "Windows" on page 0 -> 1 hit with two rects (two lines) */ }
[Fact] public async Task FindsAcrossFontChanges() { /* "fett und kursiv" -> 1 hit */ }
[Fact] public async Task RotatedPageHitsLieOnTheText() { /* rotated-page.pdf "Rotated": rect inside the page size and taller than wide for a 90° page, or as per the page's rotation */ }
[Fact] public async Task PageWithoutTextHasNoHits() { /* vector-shapes.pdf -> empty */ }
[Fact] public async Task UmlautsAreFound() { /* text-basic.pdf page 1: "größe" and "ÄÖÜ"? -> "größe" 1 hit; "GRÖSSE" 0 (no ß folding) */ }
[Fact] public async Task CancelledSearchThrows() { }
```
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS. **Step 5: Commit** `feat(core): Suche auf einer Seite mit Normalisierung und Trefferpositionen`.

---

### Task 8: Core – `DocumentSearch` (Reihenfolge, Fortschritt, Obergrenze)

**Files:**
- Create: `src/Pagewing.Core/Text/DocumentSearch.cs`
- Test: `tests/Pagewing.Core.Tests/Text/DocumentSearchTests.cs`

**Interfaces:**
- Consumes: `PdfDocument.SearchPageAsync`, `PdfDocument.PageCount`.
- Produces:
```csharp
public readonly record struct SearchPageResult(int PageIndex, IReadOnlyList<SearchHit> Hits, int PagesDone, int PageCount, bool LimitReached);

public static class DocumentSearch
{
    public const int MaxHits = 10_000;

    /// <summary>Searches the pages one request at a time, from startPage to the end, then from page 0 up to startPage - 1.
    /// Yields one result per page (also pages without hits). Stops after the page that reaches MaxHits (LimitReached).
    /// A page whose search fails with InvalidDataException is skipped.</summary>
    public static async IAsyncEnumerable<SearchPageResult> SearchAsync(
        PdfDocument document, string query, SearchOptions options, int startPage,
        [EnumeratorCancellation] CancellationToken cancellationToken = default);
}
```
- [ ] **Step 1: Failing tests**:
```csharp
[Fact] public async Task SearchesFromTheStartPageAndWrapsAround()
{   // text-dense.pdf, startPage 100, "Needle": page order 100, 101, ..., 199, 0, ..., 99; hits only on multiples of 10 (Seite 10, 20 … 200 -> indices 9, 19 … 199)
}
[Fact] public async Task FindsAllTwentyNeedlesInTheDenseDocument() { Assert.Equal(20, total); Assert.Equal(200, last.PagesDone); }
[Fact] public async Task ResultsCarryProgress() { /* PagesDone grows by one per result up to PageCount */ }
[Fact] public async Task StopsAtTheHitLimit() { /* query "a" in text-dense.pdf: total == 10_000 or the page that reached it, LimitReached true, fewer than 200 pages done */ }
[Fact] public async Task CancellationStopsTheSearch() { /* cancel after the first result -> OperationCanceledException, no further requests */ }
[Fact] public async Task SearchRunsAtBackgroundPriorityAndLetsRendersThrough() { /* start a search of the dense document and render page 0 at the same time; the render completes before the search is done */ }
[Fact] public async Task ADocumentWithoutTextYieldsNoHits() { /* vector-shapes.pdf */ }
[Fact] public async Task ADisposedDocumentThrowsObjectDisposed() { }
```
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement** (Schleife über `SearchPageAsync`, Abbruchprüfung je Seite; `LimitReached` setzt das Ergebnis der Seite, die `MaxHits` erreicht; die Hits dieser Seite werden auf die Obergrenze gekürzt). **Step 4: Run** → PASS. **Step 5: Commit** `feat(core): DocumentSearch – seitenweise, ab aktueller Seite, mit Obergrenze`.

---

### Task 9: Core – Links: Prüfung externer Adressen, `PageTextSource`, Sitzung

**Files:**
- Create: `src/Pagewing.Core/Text/ExternalLink.cs`, `src/Pagewing.Core/Text/PageTextSource.cs`
- Modify: `src/Pagewing.Core/Viewing/ViewerSession.cs`
- Test: `tests/Pagewing.Core.Tests/Text/ExternalLinkTests.cs`, `tests/Pagewing.Core.Tests/Text/PageLinksTests.cs`, `tests/Pagewing.Core.Tests/Text/PageTextSourceTests.cs`, `tests/Pagewing.Core.Tests/Viewing/ViewerSessionTests.cs` (Ergänzung)

**Interfaces:**
- Consumes: `PageLink`/`UriTarget`, `PdfDocument.GetPageTextAsync`.
- Produces:
```csharp
public enum ExternalLinkWarning { None, UserInfo, Punycode }

public sealed class ExternalLink
{
    public const int MaxLength = 2048;
    /// <summary>Accepts only absolute http, https and mailto addresses without control characters and not longer than MaxLength.</summary>
    public static bool TryParse(string? url, out ExternalLink link);
    public string Url { get; }                   // as given (trimmed)
    public string Scheme { get; }                // lower case
    public string Host { get; }                  // as written; empty for mailto without a domain
    public string UnicodeHost { get; }           // IDN decoded for display
    public ExternalLinkWarning Warning { get; }  // UserInfo: "user@" before the host; Punycode: an "xn--" label
}

public sealed class PageTextSource : IDisposable
{
    public const int MaxCachedPages = 32;
    internal PageTextSource(PdfDocument document);
    public bool TryGet(int pageIndex, out PageText text);
    public Task<PageText> GetAsync(int pageIndex, PdfPriority priority = PdfPriority.Interactive, CancellationToken cancellationToken = default);  // joins a running load of the same page
    public event Action<int>? Loaded;            // raised on a pool thread after a page is cached; handlers never throw
    public void Clear();
    public void Dispose();
}

// ViewerSession
public PageTextSource? EnsurePageTexts();       // like EnsureThumbnails: created on first call per document, disposed with it
```
- [ ] **Step 1: Failing tests**:
```csharp
[Theory]
[InlineData("https://example.org/a", true)] [InlineData("http://example.org", true)] [InlineData("mailto:me@example.org", true)]
[InlineData("javascript:alert(1)", false)] [InlineData("file:///C:/Windows/notepad.exe", false)] [InlineData("\\\\server\\share", false)]
[InlineData("ftp://example.org", false)] [InlineData("relative/path", false)] [InlineData("", false)] [InlineData(null, false)]
public void OnlyWebAndMailAddressesAreAccepted(string? url, bool expected) { }
[Fact] public void ControlCharactersAndOverlongAddressesAreRejected() { /* "https://a.org/\u0000", new string('a', 3000) */ }
[Fact] public void UserInfoAndPunycodeAreFlagged() { /* "https://paypal.com@evil.example/" -> UserInfo; "https://xn--pple-43d.com" -> Punycode, UnicodeHost "аpple.com" */ }
[Fact] public void CleanAddressesHaveNoWarning() { }
[Fact] public async Task RichDocumentLinksAreTheAnchorsAndTheWebAddresses() { /* link rects of Dokumentation/Weiter zu Seite zwei and the two text addresses; LinkAt on each rect's centre returns it; no link elsewhere */ }
[Fact] public async Task UnsupportedActionsAreNotLinks() { /* annotations.pdf: no PageLink with a launch or JavaScript target; every UriTarget passes ExternalLink.TryParse or is documented */ }
[Fact] public async Task PageTextSourceCachesAndJoinsLoads() { /* two GetAsync of one page -> one PDFium load (count via a counting wrapper or Loaded raised once); TryGet true afterwards */ }
[Fact] public async Task PageTextSourceKeepsAtMostThirtyTwoPages() { /* load 40 pages of thousand-pages.pdf -> TryGet(0) false, TryGet(39) true */ }
[Fact] public async Task EnsurePageTextsIsDisposedWithTheDocument() { }
```
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement** (`PageTextSource` mit Sperre und LRU; geteilte Ladevorgänge über ein `Dictionary<int, Task<PageText>>`; ein abgebrochener Aufruf bricht den gemeinsamen Ladevorgang nur ab, wenn kein anderer wartet — einfachste Lösung: der gemeinsame Ladevorgang läuft ohne Token des einzelnen Aufrufers, jeder Aufrufer wartet mit `WaitAsync(token)`; `ViewerSession.Reset` verwirft die Quelle wie `thumbnails`). **Step 4: Run** → PASS. **Step 5: Commit** `feat(core): externe Adressen prüfen und Seitentexte zwischenspeichern`.

---

### Task 10: CLI – `pagewing search`

**Files:**
- Create: `src/Pagewing.Cli/Commands/SearchCommand.cs`
- Modify: `src/Pagewing.Cli/CliApp.cs`, `src/Pagewing.Cli/CliOptions.cs`
- Test: `tests/Pagewing.Core.Tests/Cli/SearchCliTests.cs`

**Interfaces:**
- Consumes: `DocumentSearch.SearchAsync`, `CliApp.SelectPages`.
- Produces: `pagewing search <file> <query> [--pages <range>] [--match-case] [--whole-word] [--password <pw>]`. `CliOptions` bekommt `string? Query`, `bool MatchCase`, `bool WholeWord`; `Parse` nimmt für `search` das **zweite** Positionsargument als Suchbegriff (fehlt es oder gibt es ein drittes, `UsageException`); `Usage`/`Help` nennen den Befehl.
Ausgabe je Treffer eine Zeile `<seite>\t<zeichenindex>\t<vorher>[<treffer>]<nachher>`; Reihenfolge nach Seite und Zeichenindex (nicht in Suchreihenfolge: für die CLI wird je Seite in aufsteigender Reihenfolge gesucht, `startPage` = erste gewählte Seite, gewählte Seiten außerhalb des Bereichs werden übersprungen); Exit 0 auch ohne Treffer.

- [ ] **Step 1: Failing tests** (Stil von `CliTests.RunAsync`):
```csharp
[Fact] public async Task SearchPrintsOneTabSeparatedLinePerHit()
{   // search text-rich.pdf Pagewing --match-case: 3 hits (heading, first sentence, "Ziel: Pagewing am Ende." on page 2)
    Assert.Equal(0, result.ExitCode);
    Assert.Equal(string.Empty, result.Error);
    var lines = result.OutputLines;
    Assert.Equal(3, lines.Length);
    Assert.Equal("1\t0\t[Pagewing] Handbuch", lines[0]);
    Assert.StartsWith("1\t", lines[1]);
    Assert.EndsWith("\t[Pagewing] ist ein schneller PDF-Viewer ", lines[1]);   // 30 chars after, within the line
    Assert.StartsWith("2\t", lines[2]);
    Assert.EndsWith("\tZiel: [Pagewing] am Ende.", lines[2]);
}
[Fact] public async Task SearchWithoutHitsPrintsNothingAndExitsZero() { }
[Fact] public async Task SearchHonoursPagesAndCaseOptions() { /* --pages 2 -> only "2\t…" lines; without --match-case 4 hits in total */ }
[Fact] public async Task SearchWithoutAQueryIsAUsageError() { /* exit 2, usage text on stderr */ }
[Fact] public async Task SearchIsListedInHelpAndUsage() { }
[Fact] public async Task SearchInAPasswordFileNeedsThePassword() { /* encrypted_hello_world_r3.pdf: without -> exit 1 message; with PAGEWING_PASSWORD -> works */ }
```
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** `dotnet test tests/Pagewing.Core.Tests --filter-class "*SearchCliTests"` und `dotnet run tools/smoke-cli.cs` bleibt grün; per Hand: `dotnet run --project src/Pagewing.Cli -- search testdata/pdfs/text-rich.pdf pagewing`. **Step 5: Commit** `feat(cli): pagewing search`.

---

### Task 11: Benchmark der Suche

**Files:**
- Modify: `bench/Pagewing.Benchmarks/TextBenchmarks.cs`
- Test: `tests/Pagewing.Core.Tests/Text/SearchBenchmarkDataTests.cs`

**Interfaces:**
- Consumes: `DocumentSearch.SearchAsync`, `BenchmarkData.Pdf`.
- Produces: zwei Benchmarks `SearchThousandPages` (`thousand-pages.pdf`, Suchbegriff `Page 99`, erwartet 11 Treffer: Seite 99 und 990–999) und `SearchDenseDocument` (`text-dense.pdf`, `Needle`, 20 Treffer); beide lesen die Ergebnisse vollständig; je ein eigenes geöffnetes Dokument im `GlobalSetup`.

- [ ] **Step 1: Test** – ein Core-Test `SearchBenchmarkDataTests` prüft die erwarteten Trefferzahlen (11 und 20) mit `DocumentSearch`, damit der Benchmark nichts Falsches misst. Run → FAIL/PASS je nach Stand von Task 8 (PASS erwartet).
- [ ] **Step 2: Implementieren** – Benchmarks nach dem Muster von `GetTextOfFirstPage` mit `[MemoryDiagnoser]`.
- [ ] **Step 3: Messen** – `dotnet run -c Release --project bench/Pagewing.Benchmarks -- --filter "*TextBenchmarks*"`; Mittelwerte und Zuteilung notieren (Zahl und Rechner für Task 17). Liegt `SearchThousandPages` über 2 s oder `SearchDenseDocument` über 1 s, **nicht** weiterbauen, sondern die Ursache suchen (typisch: Geometrie wird für alle Seiten gelesen statt nur für Treffer; Zeichenweises `FPDFText_GetUnicode` statt `GetText`) und im `DECISIONS`-Eintrag festhalten.
- [ ] **Step 4: Commit** `test(bench): Suche in 1000 Seiten und im dichten Dokument`.

---

### Task 12: App – Panel „Suche“ (Suchfeld, Optionen, Trefferliste, Tastatur)

**Files:**
- Create: `src/Pagewing.App/ViewModels/SearchViewModel.cs`, `SearchHitViewModel.cs`, `src/Pagewing.App/Views/SearchPanel.axaml`, `SearchPanel.axaml.cs`
- Modify: `ViewModels/SidePanelKind.cs` (neu `Search`), `ViewModels/TabViewModel.cs`, `ViewModels/MainViewModel.cs` (`IsSearchPanelOpen`, `RaiseActivePanelChanged`), `Views/SidePanel.axaml` (Datenvorlage `Search`), `Views/SidePanel.axaml.cs` (`TitleOf`), `Views/ActionBar.axaml` (Knopf `SearchButton`), `Views/MainWindow.axaml` (KeyBindings), `Styles/Icons.axaml` (`Pw.Icon.Search`, `Pw.Icon.ChevronUp`), `Resources/Strings.cs`, `Strings.en.resx`, `Strings.de.resx`
- Test: `tests/Pagewing.App.Tests/SearchPanelTests.cs`, `LocalizationTests.cs` (Schlüssel vorhanden)

**Interfaces:**
- Consumes: `DocumentSearch.SearchAsync`, `SearchOptions`, `SearchHit`, `TabViewModel.GoToPage`, `TabViewModel.ShowPageFromPanel`, `PanelPagePicked`.
- Produces:
```csharp
// TabViewModel
public SearchViewModel Search { get; }            // created lazily on first access (not in the startup path); owned and disposed by the tab
public event Action<int, PageRect>? RevealRequested;   // page index, rect in points: the view scrolls to it (Task 13)
// SidePanelKind.Search; MainViewModel.IsSearchPanelOpen

public sealed class SearchViewModel : ViewModelBase, IDisposable
{
    public string Query { get; set; }             // change restarts the search after 200 ms; empty ends it
    public bool MatchCase { get; set; }
    public bool WholeWord { get; set; }
    public SearchState State { get; }             // Idle, Searching, Done
    public ObservableCollection<SearchHitViewModel> Hits { get; }   // sorted by (PageIndex, FirstChar); filled while searching
    public int CurrentIndex { get; }              // -1 without a current hit
    public SearchHitViewModel? Current { get; }
    public string Summary { get; }                // texts of the spec 7.4, see Strings
    public bool IsActive { get; }                 // a search is running or has hits to show (drives highlights, F3, Esc in the document)
    public ICommand NextCommand { get; }          // Enter / F3
    public ICommand PreviousCommand { get; }      // Shift+Enter / Shift+F3
    public ICommand EndCommand { get; }           // Esc in the field: clears query, hits, highlights; the tab restores the panel it showed before
    public void Choose(SearchHitViewModel hit);   // click in the list
    public event Action? HighlightsChanged;       // raised on the UI thread
    public IReadOnlyList<(int Page, PageRect Rect, bool IsCurrent)> Highlights { get; }
}
public sealed record SearchHitViewModel(SearchHit Hit) { string PageText ("3"); string Before; string Match; string After; }
```
Verhalten: Ein neues `Query`/`MatchCase`/`WholeWord` bricht die laufende Suche ab (Token), leert die Liste und startet nach 200 ms (`DispatcherTimer` oder `Task.Delay`) `DocumentSearch.SearchAsync` ab `tab.CurrentPage - 1`; die Ergebnisse kommen auf Pool-Threads und werden mit höchstens einem `Dispatcher.UIThread.Post` je 50 ms gesammelt eingefügt (sortiert); der erste Treffer ab der Ansicht (Seite ≥ aktuelle Seite, sonst der erste) wird `Current`, sobald er da ist, und löst `RevealRequested` aus, jedoch nur einmal je Suche und nur, solange der Nutzer nicht selbst gewählt hat. `Next`/`Previous` laufen umlaufend. `Summary`: `Searching` → `Strings.SearchRunning`; `Done`: 0 Treffer → `SearchNone`, 1 Treffer → `SearchSummaryOne`, alle auf einer Seite → `SearchSummaryOnePage`, sonst `SearchSummaryPages`; Obergrenze → `SearchLimit`; Dokument ganz ohne Text (alle Seiten ohne Treffer **und** `PageText.HasText` auf keiner Seite — dafür prüft die Suche zusätzlich `HasText` der ersten drei Seiten) → `SearchNoText`. Neues Dokument im Tab, Schließen des Tabs und `Dispose` brechen ab und leeren alles. `TabViewModel.SidePanel = Search` merkt sich das vorige Panel (nicht `Search`) für `EndCommand`; Esc im Suchfeld ruft `EndCommand`.

Fenster-Tasten (`MainWindow.axaml`): `Ctrl+F` → `ToggleSidePanelCommand`-ähnlich, aber **öffnen und Fokus ins Suchfeld** (`ActiveTab.OpenSearchCommand`; ist es schon offen, Fokus und Inhalt markieren), `F3` → `ActiveTab.Search.NextCommand`, `Shift+F3` → `PreviousCommand`. Enter/Umschalt+Enter im Suchfeld und die Tastatur in der Trefferliste (Pfeile, Bild auf/ab, Pos1, Ende) führt `SearchPanel` mit eigenen `KeyBindings`.

UI (Spec 7.4): Suchfeld (Platzhalter), darunter zwei `CheckBox` („Groß-/Kleinschreibung beachten“, „Ganze Wörter“), eine Zeile mit `Summary` und den Knöpfen Zurück/Weiter (`ChevronUp`/`ChevronDown`, `Classes="icon"`, Tooltips nennen Enter bzw. Umschalt+Enter), darunter die Trefferliste (`ListBox`, virtualisiert; Eintrag: Seitenmarke `type.mono`, Ausschnitt mit `Run` für den Treffer auf `Pw.Brush.Search.Hit` / `Pw.Brush.Search.On`; der aktuelle Treffer `Accent.Subtle` mit 2 px `Accent.Default`-Kante links). Icons: Tabler `search` (`M3 10a7 7 0 1 0 14 0a7 7 0 1 0 -14 0 M21 21l-6 -6`) und `chevron-up` (`M6 15l6 -6l6 6`), gegen das Tabler-Original prüfen. Der Knopf `SearchButton` steht in `ActionBar` zwischen „Inhalt“ und dem Rest (Tooltip „Suchen (Strg+F)“), `Classes.active="{Binding IsSearchPanelOpen}"`.
Neue Strings (en/de): `SearchPanelTitle` (Search/Suche), `SearchTooltip` (Search (Ctrl+F)/Suchen (Strg+F)), `SearchPlaceholder` (Search in document/Im Dokument suchen), `SearchMatchCase`, `SearchWholeWord`, `SearchRunning` (Searching …/Suche läuft …), `SearchNone` (No matches/Keine Treffer), `SearchSummaryOne` (1 match on page {0}/1 Treffer auf Seite {0}), `SearchSummaryOnePage` ({0} matches, all on page {1}/{0} Treffer, alle auf Seite {1}), `SearchSummaryPages` ({0} matches on {1} pages/{0} Treffer auf {1} Seiten), `SearchLimit` (More than {0} matches/Mehr als {0} Treffer), `SearchNoText` (This document contains no searchable text. Scanned pages need text recognition (OCR), which Pagewing does not have yet./Dieses Dokument enthält keinen durchsuchbaren Text. Gescannte Seiten brauchen eine Texterkennung (OCR), die Pagewing noch nicht hat.), `SearchNext`, `SearchPrevious`, `SearchHitName` (Page {0}: {1}/Seite {0}: {1}).

- [ ] **Step 1: Failing tests** (`SearchPanelTests`, Stil von `OutlinePanelTests`: `Show(1024)`, `viewModel.OpenAsync(TestData.Pdf("text-dense.pdf"))`, `WaitUntilAsync`):
```csharp
[AvaloniaFact] public async Task SearchPanelIsNotBuiltBeforeItOpens() { /* tab.Search not created (no Search property access) and FindOrNull<SearchPanel>(window, "SearchContent") null until the button is clicked */ }
[AvaloniaFact] public async Task CtrlFOpensThePanelWithTheFocusInTheField() { /* key gesture Ctrl+F -> tab.SidePanel == Search, focused element is the TextBox named "SearchField" */ }
[AvaloniaFact] public async Task TypingFillsTheListAndTheSummary()
{   // "Needle" in text-dense.pdf: 20 hits, Summary "20 Treffer auf 20 Seiten" (de) / "20 matches on 20 pages" (en); Hits sorted by page; Current is the first hit at or after the current page
}
[AvaloniaFact] public async Task EnterAndShiftEnterStepThroughTheHitsAndWrap() { /* Current moves 0 -> 1 -> ... ; Shift+Enter at 0 wraps to 19 */ }
[AvaloniaFact] public async Task F3WorksWithThePanelClosed() { }
[AvaloniaFact] public async Task EscapeInTheFieldEndsTheSearchAndRestoresThePanelShownBefore() { /* Pages open -> Ctrl+F -> Esc -> SidePanel == Pages, Hits empty, IsActive false */ }
[AvaloniaFact] public async Task TheSearchStartsAtTheCurrentPage() { /* GoToPage(100) then search "Needle": the first result processed is page index 99+; Current is Seite 100 (index 99) because 100 % 10 == 0 */ }
[AvaloniaFact] public async Task AShortQueryEndsAtTheHitLimit() { /* "a" in text-dense.pdf: Hits.Count == DocumentSearch.MaxHits (10000), State Done, Summary == string.Format(Strings.SearchLimit, 10000); UI thread stays responsive: a Dispatcher job posted at Normal priority runs within 500 ms while the search is running */ }
[AvaloniaFact] public async Task ADocumentWithoutTextShowsTheHint() { /* vector-shapes.pdf: Summary == Strings.SearchNoText */ }
[AvaloniaFact] public async Task ReplacingTheDocumentOrClosingTheTabCancelsTheSearch() { /* no exception, Hits empty, State Idle */ }
[AvaloniaFact] public async Task ClickingAHitInANarrowWindowClosesThePanel() { }
[AvaloniaFact] public void SearchStringsExistInBothLanguages() { /* every new key resolves in en and de */ }
```
- [ ] **Step 2: Run** `dotnet test tests/Pagewing.App.Tests --filter-class "*SearchPanelTests"` → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS; `dotnet test tests/Pagewing.App.Tests` komplett (andere Panel-Tests dürfen nicht brechen). **Step 5: Startzeit prüfen** – `dotnet run bench/startup/startup.cs -- --case warm` (Aufruf laut `bench/startup/README.md`) auf dem eigenen Rechner (Hinweis zur Messumgebung in CLAUDE.md beachten); der Median darf gegenüber dem Stand vor dem Task nicht auffällig steigen (Aktionsleiste bekommt einen Knopf); Zahl vorher/nachher in die Commit-Nachricht. **Step 6: Commit** `feat(app): Panel „Suche“ mit Trefferliste, Optionen und Tastatur`.

---

### Task 13: App – Markierungen im Dokument und Sprung zum Treffer

**Files:**
- Create: `src/Pagewing.App/Controls/PageHighlight.cs`
- Modify: `Controls/DocumentCanvas.cs`, `Views/DocumentView.axaml.cs` (oder `DocumentView.Text.cs`, siehe Task 14), `ViewModels/TabViewModel.cs`, `Styles/Tokens.axaml`
- Test: `tests/Pagewing.App.Tests/HighlightTests.cs`, `TokenTests.cs`

**Interfaces:**
- Consumes: `SearchViewModel.Highlights`/`HighlightsChanged`/`RevealRequested`, `DocumentLayout.GetPageRect`, `DocumentLayout.DipsPerPoint`, `DocumentLayout.ShowPage`.
- Produces:
```csharp
internal enum HighlightKind { SearchHit, SearchCurrent, Selection }
internal readonly record struct PageHighlight(int Page, PageRect Rect, HighlightKind Kind);

// DocumentCanvas
internal void SetHighlights(IReadOnlyList<PageHighlight> highlights);   // replaces all; InvalidateVisual
internal IReadOnlyList<(PageHighlight Highlight, Rect Destination)> LastHighlightDestinations { get; }   // for tests: where the last frame drew them, canvas coordinates
```
Zeichnen in `Render`, nach dem Seitenbild und innerhalb des Seitenclips: Rechteck in Canvas-Koordinaten `pageRect.X + rect.Left * dipsPerPoint` usw. (`dipsPerPoint = DocumentLayout.DipsPerPoint * layout.Zoom`); `SearchHit` gefüllt mit `Pw.Brush.Search.Hit`, `SearchCurrent` mit `Pw.Brush.Search.Current` plus 1,5 px Rahmen in `Pw.Brush.Search.On`, `Selection` mit dem neuen Token `Pw.Brush.Selection.Page` (`#3B48C4` mit Deckkraft 30 % in beiden Themes, wie die Suchtokens themeunabhängig: Seiten bleiben weiß). Highlights werden einmal je `SetHighlights` nach Seite gruppiert (`Dictionary<int, PageHighlight[]>`); `Render` zeichnet nur die sichtbaren Seiten. Die Brushes kommen als Eigenschaften wie `PlaceholderBrush` (aus `DocumentView.axaml` per `DynamicResource` gesetzt).
`DocumentView`: verbindet `tab.Search.HighlightsChanged` mit `SetHighlights` (zusammen mit der späteren Auswahl, Task 14) und `tab.RevealRequested(page, rect)`: im Einzelseiten-Layout `layout.ShowPage(page)`; Rechteck in Inhaltskoordinaten; liegt es nicht vollständig im Viewport, scrollt die Ansicht vertikal so, dass seine Mitte in der Mitte des Viewports liegt (begrenzt auf den Inhalt), horizontal nur so weit wie nötig; aktuelle Seite der Ansicht folgt über `ShowCurrentRow` wie bei `OnPageRequested`.

- [ ] **Step 1: Failing tests**:
```csharp
[AvaloniaFact] public async Task AllHitsAreDrawnAndTheCurrentOneDiffers() { /* text-rich.pdf, search "Pagewing": canvas.LastHighlightDestinations has the SearchHit/SearchCurrent entries of page 0; destination of the first lies at pageRect + rect*dipsPerPoint (±1 DIP) */ }
[AvaloniaFact] public async Task HighlightsFollowTheZoom() { /* zoom 100 % -> 200 %: destination sizes double */ }
[AvaloniaFact] public async Task HighlightsAreGoneAfterTheSearchEnds() { }
[AvaloniaFact] public async Task NextHitOnAnotherPageScrollsThere() { /* text-dense.pdf "Needle": Next from page 10 to 20 -> tab.CurrentPage == 20 and the hit rect lies inside the viewport */ }
[AvaloniaFact] public async Task AVisibleHitDoesNotScroll() { /* offset unchanged */ }
[AvaloniaFact] public async Task RotatedPageHighlightsLieOnTheText() { /* rotated-page.pdf: highlight destination lies inside the page rect */ }
[AvaloniaFact] public async Task SinglePageLayoutShowsThePageOfTheHit() { }
[AvaloniaFact] public void SelectionOverlayKeepsPageTextReadable() { /* blend #3B48C4 @ 30 % over white vs black text: Wcag.Contrast >= 4.5 */ }
```
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** `dotnet test tests/Pagewing.App.Tests` → PASS. **Step 5: Commit** `feat(app): Treffermarkierung im Dokument und Sprung zum Treffer`.

---

### Task 14: App – Textauswahl mit der Maus

**Files:**
- Create: `src/Pagewing.App/Views/DocumentView.Text.cs` (Teilklasse von `DocumentView`)
- Modify: `Views/DocumentView.axaml` (Hinweis-Element `NoTextHint`), `Views/DocumentView.axaml.cs` (Verbindung, `OnPointerPressed`), `ViewModels/TabViewModel.cs` (`TextSelection`, `PageTexts`), `Controls/DocumentCanvas.cs` (Auswahl über `SetHighlights`), `Resources/Strings.cs`/`.resx` (`NoTextOnPage`)
- Test: `tests/Pagewing.App.Tests/TextSelectionTests.cs`

**Interfaces:**
- Consumes: `PageTextSource` (über `tab.Session.EnsurePageTexts()` erst nach `HasShownSharpPage`), `PageText.CharAt/CaretAt/WordAt/LineAt/RectsOf/LinkAt`, `DocumentCanvas.PagePointAt`, `TextSelection`.
- Produces:
```csharp
// TabViewModel
public TextSelection? TextSelection { get; set; }        // cleared on a new document and on Dispose; raises PropertyChanged
public bool HasTextSelection { get; }
public event Action? CopyRequested;                      // used by Task 15

// DocumentView (partial, DocumentView.Text.cs)
internal Task<TextSelection?> SelectAsync(int page, int firstChar, int lastCharExclusive);  // for tests: select chars of a page
```
Verhalten:
- **Vorladen:** Nach `HasShownSharpPage` und bei jeder Änderung der sichtbaren Seiten lädt die Ansicht die sichtbaren Seiten **und je eine davor und dahinter** mit `PdfPriority.Background` in die `PageTextSource` (nie vor dem ersten scharfen Bild; nie für inaktive Tabs; pro Viewport-Änderung höchstens eine Planung, bereits gecachte oder laufende Seiten werden übersprungen).
- **Cursor:** Über einem Zeichen `IBeam` (`Cursor` des `Scroller`), über einem Link `Hand`, sonst Standard; solange die Seite nicht geladen ist, Standard. Sicherheit: Hand nur für Links, die `PageTarget` oder gültige `ExternalLink` sind.
- **Drücken (linke Taste, nicht auf einem `ScrollBar`, nicht mit Strg gedrückt):** Seite und Punkt über `PagePointAt`; ist die `PageText` noch nicht da, wird sie mit `Interactive` geladen und die Auswahl beginnt danach, falls die Taste noch gedrückt ist. `ClickCount` 1: Anker = `CaretAt`, leere Auswahl; 2: `WordAt(CharAt)`; 3: `LineAt(CharAt)`. Bei `CharAt == -1` und Klick 2/3 passiert nichts. Ein Klick (ohne Ziehen, < 4 DIP Weg) auf einer leeren Stelle löscht die Auswahl. Ziehen setzt `Focus` auf `CaretAt` der Seite unter dem Zeiger (auch über andere Seiten; deren `PageText` wird bei Bedarf geladen, bis dahin gilt die letzte bekannte); am Rand des Viewports scrollt die Ansicht nicht automatisch (nicht in M5). Die Auswahl wird als `PageHighlight(…, Selection)` über `RectsOf` je Seite gezeichnet (für Seiten dazwischen alle Zeichen). Der Zeiger-Fang bleibt bis zum Loslassen bei der Ansicht.
- **Trifft das Drücken einen Link** (`LinkAt` ≠ null), beginnt keine Auswahl; der Link gehört Task 16.
- **Seite ohne Text:** Das Drücken auf eine geladene Seite mit `HasText == false` zeigt für 3 s `NoTextHint` (Text `NoTextOnPage`: „There is no selectable text on this page. It may be a scan.“ / „Auf dieser Seite gibt es keinen auswählbaren Text. Sie ist vielleicht ein Scan.“) unten im Dokumentbereich (`Pw.Brush.Surface.Raised`, `Pw.Brush.Border.Subtle`, `radius.overlay`), eine Zeile, kein Fokus; ein weiterer Klick verlängert nicht.
- Esc in der Ansicht: löscht zuerst eine Auswahl; ohne Auswahl beendet es eine aktive Suche (`tab.Search.IsActive` → `EndCommand`).
- Die Auswahl bleibt über Tab-Wechsel (sie gehört zum Tab; `SetHighlights` der Ansicht folgt dem aktiven Tab) und wird bei einem neuen Dokument gelöscht.

- [ ] **Step 1: Failing tests** (`text-rich.pdf` bzw. `text-basic.pdf`, Maus über `window.MouseDown/MouseMove/MouseUp` des Headless-Testsystems auf berechneten Bildschirmpunkten: Punkt = `canvas.LastHighlightDestinations` bzw. `layout.GetPageRect` + Seitenpunkt × `dipsPerPoint`):
```csharp
[AvaloniaFact] public async Task DraggingOverTextSelectsIt() { /* press before 'P' of "Pagewing", release after 'g': tab.TextSelection covers chars 0..8 of page 0; canvas shows one Selection highlight */ }
[AvaloniaFact] public async Task DoubleClickSelectsAWord() { /* on "Handbuch" -> chars 9..17 */ }
[AvaloniaFact] public async Task TripleClickSelectsALine() { }
[AvaloniaFact] public async Task ClickingEmptySpaceClearsTheSelection() { }
[AvaloniaFact] public async Task DraggingAcrossPagesSelectsBothPages() { /* press on page 0, release on page 1: Start.Page 0, End.Page 1 */ }
[AvaloniaFact] public async Task SelectionSurvivesZoomAndTabSwitch() { }
[AvaloniaFact] public async Task SelectionClearsOnANewDocument() { }
[AvaloniaFact] public async Task CursorIsIBeamOverTextAndArrowElsewhere() { }
[AvaloniaFact] public async Task ClickingAScrollBarDoesNotSelect() { }
[AvaloniaFact] public async Task PagesWithoutTextShowTheHint() { /* vector-shapes.pdf: press -> NoTextHint visible, hides after 3 s (fake dispatcher timer / WaitUntil) */ }
[AvaloniaFact] public async Task TextIsNotLoadedBeforeTheFirstSharpPage() { /* PageTextSource.TryGet false and EnsurePageTexts not called until HasShownSharpPage */ }
[AvaloniaFact] public async Task SelectionOnARotatedPageSitsOnTheText() { }
[AvaloniaFact] public async Task EscapeClearsTheSelectionThenEndsTheSearch() { }
[AvaloniaFact] public async Task ClosingTheTabWhileTheTextLoadsDoesNotThrow() { }
```
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** `dotnet test tests/Pagewing.App.Tests`; `dotnet test tests/Pagewing.Core.Tests` unberührt grün. **Step 5: Scroll-Messung** – `--measure-scroll` auf `thousand-pages.pdf` vor/nach dem Task (Befehl in `bench/startup/README.md`); die p99-Werte dürfen nicht auffällig steigen (das Vorladen läuft im Hintergrund); beide Zahlen in die Commit-Nachricht. **Step 6: Commit** `feat(app): Text mit der Maus markieren`.

---

### Task 15: App – Kopieren in die Zwischenablage

**Files:**
- Create: `src/Pagewing.App/Services/IClipboardService.cs`, `AvaloniaClipboardService.cs`
- Modify: `Views/DocumentView.Text.cs` (`Ctrl+C`, `Ctrl+Shift+C`), `ViewModels/MainViewModel.cs` oder `TabViewModel.cs` (Dienst-Zugriff), `Views/MainWindow.axaml.cs`/`App.axaml.cs` (Dienst bereitstellen)
- Test: `tests/Pagewing.App.Tests/CopyTests.cs`

**Interfaces:**
- Consumes: `TextFlow.Plain/Html/Raw/SlicesOf`, `tab.TextSelection`, `PageTextSource.GetAsync`, Ergebnis des Versuchs aus Task 2.
- Produces:
```csharp
public interface IClipboardService
{
    /// <summary>Puts plain text and, when given, an HTML fragment on the clipboard.</summary>
    Task SetTextAsync(string plainText, string? htmlFragment);
}
// DocumentView.Text.cs
internal Task CopySelectionAsync(bool raw);   // Ctrl+C -> raw: false; Ctrl+Shift+C -> raw: true
```
Der Avalonia-Dienst setzt ein `DataTransfer` mit Klartext (`DataTransferItem.CreateText`) und, wenn `htmlFragment` nicht `null` ist, mit dem HTML-Format des Betriebssystems laut Task 2 (Windows: `HTML Format` mit CF_HTML-Hülle aus `StartHTML/EndHTML/StartFragment/EndFragment` in **Bytes**, Hülle in einer `internal static` Methode `ClipboardHtml.WrapForWindows(string fragment)` testbar; Linux: `text/html`); danach `FlushAsync`. Fehler der Zwischenablage (`IOException`, Sperre durch ein anderes Programm) werden verschluckt und einmal nach 100 ms wiederholt, nie geworfen.
Tastatur: **Strg+C/Strg+Umschalt+C werden in `DocumentView.OnKeyDown` behandelt, nicht als `KeyBinding` des Fensters**, damit Kopieren in `TextBox`en (Suchfeld, Seitenfeld) unberührt bleibt; sie wirken nur, wenn die Ansicht den Fokus hat. Fehlt eine `PageText` zwischen Anfang und Ende der Auswahl, wird sie mit `Interactive` nachgeladen. Eine leere oder fehlende Auswahl ändert die Zwischenablage nicht. `raw`: `Raw(...)` als Klartext, **kein** HTML; sonst `Plain(...)` und `Html(...)`.

- [ ] **Step 1: Failing tests** (mit einem Test-`IClipboardService`, der Aufrufe aufzeichnet):
```csharp
[AvaloniaFact] public async Task CtrlCCopiesProseAndHtml() { /* text-rich.pdf, whole page 0 selected: plain == expected prose of Task 6, html contains "<b>fett</b>" and "<h1>Pagewing Handbuch</h1>" */ }
[AvaloniaFact] public async Task CtrlShiftCCopiesRawTextWithLineBreaksAndNoHtml() { /* plain contains "Win-\ndows"? -> the PDF's own breaks; html null */ }
[AvaloniaFact] public async Task CopyWithoutASelectionLeavesTheClipboardAlone() { }
[AvaloniaFact] public async Task CtrlCInTheSearchFieldCopiesTheFieldNotThePdf() { /* focus in SearchField -> the service is not called */ }
[AvaloniaFact] public async Task CopyAcrossPagesLoadsTheMissingPages() { }
[Fact] public void WindowsHtmlEnvelopeHasByteOffsetsThatPointAtTheFragment() { /* with "ü" in the fragment: StartFragment/EndFragment are UTF-8 byte offsets, the sliced bytes equal the fragment */ }
[AvaloniaFact] public async Task AClipboardFailureDoesNotCrash() { }
```
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS. **Step 5: Manuell** (nicht automatisierbar): Release-Build, `text-rich.pdf` öffnen, Absatz markieren, Strg+C, in Word einfügen: Absätze, **fett**, *kursiv* und Überschrift erscheinen; Strg+Umschalt+C in Notepad: Zeilenumbrüche des PDFs. Ergebnis in den Nachweis der Roadmap (Task 17); Linux als **offen** markieren, falls kein Linux-Rechner da ist (Befehl: dasselbe in LibreOffice Writer). **Step 6: Commit** `feat(app): Kopieren als Fließtext mit Format (Strg+C) und als Rohtext (Strg+Umschalt+C)`.

---

### Task 16: App – Links (Hover, interne Sprünge, externe mit Bestätigung)

**Files:**
- Create: `src/Pagewing.App/Services/ILinkLauncher.cs`, `AvaloniaLinkLauncher.cs`, `src/Pagewing.App/Views/LinkConfirmCard.axaml`, `LinkConfirmCard.axaml.cs`
- Modify: `Views/DocumentView.Text.cs` (Klick auf Link), `ViewModels/TabViewModel.cs` (`FollowLink`, Verlauf), `ViewModels/MainViewModel.cs` (Bestätigungszustand), `Views/MainWindow.axaml` (Karte, KeyBindings `Alt+Left`/`Alt+Right`), `Resources/Strings.cs`/`.resx`
- Test: `tests/Pagewing.App.Tests/LinkTests.cs`

**Interfaces:**
- Consumes: `PageLink`, `PageTarget`, `UriTarget`, `ExternalLink`, `TabViewModel.GoToPage`, `ViewState`/`DocumentLayout.GetAnchor`.
- Produces:
```csharp
public interface ILinkLauncher { Task LaunchAsync(Uri uri); }   // the only place that opens an address

// TabViewModel
public void FollowLink(PageLink link);                 // PageTarget: jump (remember the position for Back); UriTarget: ExternalLink.TryParse then MainViewModel asks for confirmation
public ICommand BackCommand { get; }                   // Alt+Left: previous view (page and scroll position before the last link jump)
public ICommand ForwardCommand { get; }                // Alt+Right
public bool CanGoBack { get; } public bool CanGoForward { get; }

// MainViewModel
public PendingLink? PendingLink { get; }               // record PendingLink(ExternalLink Link): shown by LinkConfirmCard
public ICommand ConfirmLinkCommand { get; }            // opens via ILinkLauncher, then clears PendingLink
public ICommand CancelLinkCommand { get; }             // clears PendingLink (also Esc, also a tab switch or a new document)
```
Verhalten:
- **Klick** (Drücken und Loslassen auf demselben Link, < 4 DIP Weg, kein Ziehen, linke Taste ohne Umschalt) → `FollowLink`. Interne Ziele springen ohne Rückfrage zur Seite; `Top` (Punkte von oben) wird als Position im Viewport-Rand genutzt (`layout.GetPageRect(page).Y + Top * dipsPerPoint - DocumentLayout.Margin`, begrenzt), im Einzelseiten-Layout zeigt die Seite. Vor dem Sprung merkt der Tab die aktuelle Ansicht (Seite + Anker, wie `SavedView`), `Back` stellt sie her, `Forward` die Ansicht vor dem Zurück; ein neuer Sprung leert den Vorwärtsverlauf; Verlauf höchstens 50 Einträge.
- **Externe Ziele:** `ExternalLink.TryParse` scheitert → nichts passiert (kein Cursor, kein Klick); sonst setzt `MainViewModel` `PendingLink`. **`ILinkLauncher.LaunchAsync` wird ausschließlich von `ConfirmLinkCommand` aufgerufen.**
- **Karte** (`LinkConfirmCard`, im Fenster über dem Dokument wie `ErrorCard`, Spec 7.8-Stil: `surface.raised`, `radius.overlay`, `border.subtle`; modal in dem Sinn, dass Esc abbricht und der Fokus auf „Abbrechen“ liegt): Titel `LinkConfirmTitle`, Text `LinkConfirmText`, darunter die **vollständige Adresse** in `type.mono`, umbrechend, mit dem Host hervorgehoben (`Run` fett), bei `Warning` eine Zeile `LinkWarningUserInfo` bzw. `LinkWarningPunycode` mit `AlertTriangle`-Icon, bei Punycode zusätzlich der `UnicodeHost`; Knöpfe „Abbrechen“ (Standard) und „Öffnen“. Texte (en/de): `LinkConfirmTitle` (Open this web address?/Diese Webadresse öffnen?), `LinkConfirmText` (This PDF wants to open a link in your browser. Only continue if you trust the address./Dieses PDF möchte einen Link in Ihrem Browser öffnen. Fahren Sie nur fort, wenn Sie der Adresse vertrauen.), `LinkConfirmOpen` (Open/Öffnen), `LinkConfirmCancel` (Cancel/Abbrechen), `LinkWarningUserInfo` (The address contains a user name before the “@”. The real host is {0}./Die Adresse enthält vor dem „@“ einen Benutzernamen. Der eigentliche Host ist {0}.), `LinkWarningPunycode` (The host uses special characters and is shown as {0}./Der Host nutzt Sonderzeichen und wird als {0} dargestellt.).
- Standard-`AvaloniaLinkLauncher` nutzt `TopLevel.Launcher.LaunchUriAsync`.

- [ ] **Step 1: Failing tests** (mit Test-`ILinkLauncher`; Link-Flächen aus `text-rich.pdf`):
```csharp
[AvaloniaFact] public async Task ClickingAnInternalLinkJumpsWithoutAsking() { /* "Weiter zu Seite zwei": tab.CurrentPage == 2, PendingLink null, launcher not called, view top ≈ target Top */ }
[AvaloniaFact] public async Task BackAndForwardReturnToTheViewBeforeTheJump() { /* Alt+Left -> page 1 at the old offset; Alt+Right -> page 2 */ }
[AvaloniaFact] public async Task AnExternalLinkOpensOnlyAfterConfirmation()
{   // click "Dokumentation": launcher.Calls == 0, PendingLink.Link.Url == "https://example.org/docs", card visible and shows the full address
    // ConfirmLinkCommand: launcher.Calls == 1 with exactly that Uri
}
[AvaloniaFact] public async Task CancelingOrEscapeNeverOpensTheAddress() { /* Esc and the Cancel button: launcher.Calls == 0, PendingLink null */ }
[AvaloniaFact] public async Task SwitchingTabsOrReplacingTheDocumentDropsAPendingLink() { }
[AvaloniaFact] public async Task AddressesFoundInTheTextAreLinksToo() { /* the https://example.org/pagewing address: click -> pending, not opened */ }
[AvaloniaFact] public async Task DangerousTargetsAreNotClickable() { /* a PageLink with UriTarget("javascript:alert(1)") / "file:///x" injected into the PageText: click does nothing, cursor stays an arrow, launcher.Calls == 0 */ }
[AvaloniaFact] public async Task DeceptiveAddressesShowAWarning() { /* "https://paypal.com@evil.example/" -> warning line with host "evil.example" */ }
[AvaloniaFact] public async Task ClickingAndDraggingAcrossALinkDoesNotFollowIt() { }
[AvaloniaFact] public async Task LinkRectsOfARotatedPageAreClickable() { }
[AvaloniaFact] public void LinkStringsExistInBothLanguages() { }
```
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** `dotnet test tests/Pagewing.App.Tests` → PASS. **Step 5: Manuell** – Release-Build, `text-rich.pdf`: „Dokumentation“ anklicken → Karte mit Adresse, „Abbrechen“ → nichts passiert, erneut „Öffnen“ → Browser mit `https://example.org/docs`; „Weiter zu Seite zwei“ springt, Alt+Links kehrt zurück. Ergebnis für Task 17 notieren. **Step 6: Commit** `feat(app): Links – interne Sprünge, externe Adressen nach Bestätigung`.

---

### Task 17: Abschluss – Doku, Roadmap, Gesamtprüfung, PR

**Files:**
- Modify: `Docs/superpowers/specs/2026-10-17-designsystem.md` (Abschnitte 7.3, 7.4, 7.5, Tokens/Selektion), `Docs/DECISIONS.md`, `CLAUDE.md`, `CHANGELOG.md`, `Docs/roadmap.json`, `Docs/ROADMAP.md` (nur erzeugt), `bench/startup/README.md` (falls Messbefehle ergänzt)

**Interfaces:** keine.

- [ ] **Step 1: Spec nachziehen** – 7.3: Aktionsleiste enthält jetzt Seiten, Inhalt, Suche; 7.4: Abweichungen dieses Plans (Panel schließen versteckt nur, Esc in der Ansicht, F3/Umschalt+F3, Optionen „Groß-/Kleinschreibung“ und „Ganze Wörter“, Treffer-Obergrenze, Hinweis ohne Text); 7.5 (oder neu 7.5a): Auswahl (Maus, Doppel-/Dreifachklick, Strg+C/Strg+Umschalt+C), Links (Cursor, Bestätigungskarte, Alt+Links/Rechts), Token `Selection.Page`. Mockup-Quellen bleiben unverändert (die Spec gilt).
- [ ] **Step 2: DECISIONS** – ein Eintrag je Entscheidung des Abschnitts „Entscheidungen dieses Plans“, im Format der vorhandenen („Kontext / Entscheidung / Begründung / Verworfene Alternativen“): Fließtext-Kopie und HTML-Umfang, Auswahlmodell (Lesereihenfolge, keine Spaltenerkennung), eigener Suchabgleich statt `FPDFText_Find*` (mit Messwerten aus Task 11), Normalisierung/Trennstrich, Suchreihenfolge und Obergrenze, Link-Regeln (Schemata, Karte, kein „Immer erlauben“), Zwischenablage-Formate (Ergebnis aus Task 2), CLI-Ausgabe und Exit-Code. Verworfen u. a.: PDFium-`FPDFText_Find*` (Ligaturen/Trennstriche), Suchindex, Acrobat-„Trust Manager“-Modell mit dauerhafter Erlaubnis, geometrische Rechteckauswahl.
- [ ] **Step 3: CLAUDE.md** – in „Architektur“ einen Punkt „Textschicht (seit M5)“: `Pagewing.Core.Text` (UI-frei: `PageText` als Schnappschuss, `TextFlow`, `TextMatcher`, `DocumentSearch`, `PageTextSource`, `ExternalLink`), PDFium-Zugriff nur in `PdfiumPageText`, App: `SearchViewModel`/`SearchPanel`, `DocumentView.Text.cs`, `LinkConfirmCard`; Stolperfallen (Strg+C in `DocumentView.OnKeyDown`, nicht als Fenster-`KeyBinding`; `ILinkLauncher` nur über `ConfirmLinkCommand`; `FPDF_PageToDevice` als einzige Koordinatenumrechnung; nichts davon vor `HasShownSharpPage`). Verweise nach Dateien/Typen, ohne Zeilennummern.
- [ ] **Step 4: CHANGELOG** – unter `[Unreleased]` / `Added`: Suche im Dokument (Strg+F) mit Trefferliste und Markierung; Text markieren und kopieren (formatiert mit Strg+C, roh mit Strg+Umschalt+C); Links im PDF (interne Sprünge, externe nach Bestätigung); CLI `pagewing search`.
- [ ] **Step 5: Gesamtprüfung** – nacheinander, jeweils Befehl und Ergebnis notieren: `dotnet build Pagewing.slnx`; `dotnet test Pagewing.slnx`; `dotnet format Pagewing.slnx --verify-no-changes`; `dotnet run tools/make-testpdfs.cs -- check`; `dotnet run tools/roadmap.cs -- check`; Startzeit-Check lokal (`dotnet run bench/startup/startup.cs -- --case cold`, `warm`, `thousand`, `second`, Messumgebung beachten) und danach in der CI; Benchmarks aus Task 11 mit den Werten vor Task 12 vergleichen; Scroll-Messung aus Task 14. Offen markieren, was nicht prüfbar war (Linux-Zwischenablage, Linux-Cursor, Hardware-Messung: der Haupt-PC-Runner muss vom Projektinhaber gestartet werden, **vorher Bescheid sagen**).
- [ ] **Step 6: Roadmap** – in `Docs/roadmap.json` die vier Aufgaben abhaken; Abnahmekriterien mit Nachweis: *Suchtests* (`SearchPageTests`, `DocumentSearchTests`, `TextMatcherTests`, `SearchPanelTests` mit Zahl der bestandenen Tests), *Externer Link erst nach Bestätigung* (`LinkTests.AnExternalLinkOpensOnlyAfterConfirmation`, `CancelingOrEscapeNeverOpensTheAddress`), *Benchmark* (Messwerte aus Task 11 mit Rechner); Status `done` mit `completedOn`. Scope-Änderungen (nicht enthalten: Mehrdatei-Suche, Index, Strg+A, Spaltenerkennung) als Backlog-Einträge in `backlog` und, wo sie den Umfang ändern, unter „Scope-Änderungen“. Danach `dotnet run tools/roadmap.cs -- generate` und `-- check`.
- [ ] **Step 7: Commit und PR** – gezielt stagen, `docs: Milestone 5 abschließen (Spec, Entscheidungen, Roadmap)`; PR nach `dev` mit Zusammenfassung, Nachweisen und der Liste des Offenen; Merge (Squash) erst, wenn alle CI-Checks grün sind; Branch-Löschung nach dem Merge ist automatisch.

---

## Selbstprüfung

- **Abdeckung der Roadmap:** Volltextsuche mit Markierung und Weiter/Zurück, im Hintergrund (Tasks 7, 8, 12, 13) · Markieren und Kopieren (Tasks 5, 6, 14, 15) · interne und externe Links (Tasks 3, 9, 16) · CLI `search` (Task 10) · Suchtests mit Treffern und Positionen (Tasks 7, 8) · externer Link erst nach Bestätigung (Task 16) · Benchmark (Task 11).
- **Typkonsistenz:** `PageRect`, `TextChar`, `PageText`, `PageLink`/`PageTarget`/`UriTarget`, `TextSelection`/`TextPosition`, `SearchHit`, `SearchOptions` stammen aus den Tasks 4, 5 und 7 und werden in 8–16 unverändert verwendet. `PdfiumChar`/`PdfiumLink` bleiben `internal` in Pdfium.
- **Review Focus:** jede der fünf Zeilen hat einen Test (1: `AShortQueryEndsAtTheHitLimit`, `StopsAtTheHitLimit`; 2: `ADocumentWithoutTextShowsTheHint`, `PagesWithoutTextShowTheHint`, `CopyWithoutASelectionLeavesTheClipboardAlone`, `PageWithoutTextHasNoHits`; 3: `ReplacingTheDocumentOrClosingTheTabCancelsTheSearch`, `ClosingTheTabWhileTheTextLoadsDoesNotThrow`, `CancellationStopsTheSearch`; 4: `GeometryFollowsRotationAndCropBox`, `RotatedPageHitsLieOnTheText`, `RotatedPageHighlightsLieOnTheText`, `SelectionOnARotatedPageSitsOnTheText`, `LinkRectsOfARotatedPageAreClickable`; 5: `OnlyWebAndMailAddressesAreAccepted`, `DangerousTargetsAreNotClickable`, `DeceptiveAddressesShowAWarning`, `AnExternalLinkOpensOnlyAfterConfirmation`).
- **Bekannte Grenzen (stehen in der Doku):** Fett/Kursiv nur mit Hinweisen im PDF; keine Spaltenerkennung; Kopf-/Fußzeilen bleiben beim Kopieren; Ligaturen nur synthetisch getestet; Auswahl scrollt am Rand nicht selbst; Linux-Zwischenablage und -Cursor sind ungeprüft, solange kein Linux-Rechner mit Oberfläche bereitsteht.
