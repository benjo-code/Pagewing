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

## 2026-10-16 – Testframework xunit.v3 4.0.2 mit Microsoft.Testing.Platform
**Kontext:** Für die Testprojekte war ein Framework zu wählen. Die UI-Tests (ab M3) laufen mit Avalonia.Headless.
**Entscheidung:** xunit.v3 4.0.2 auf Microsoft.Testing.Platform. `global.json` enthält den Test-Runner; `Microsoft.NET.Test.Sdk` und `xunit.runner.visualstudio` werden nicht referenziert.
**Begründung:** `Avalonia.Headless.XUnit` 12.1.4 hängt von `xunit.v3.extensibility.core` 3.2.2 ab. Dazu passt xunit v2 nicht.
**Verworfene Alternativen:** xunit v2 mit VSTest: passt nicht zu `Avalonia.Headless.XUnit`.
**Offener Punkt:** Die Verträglichkeit von `Avalonia.Headless.XUnit` mit xunit.v3 4.0.x wird in M3 geprüft.

## 2026-10-16 – Zentrale Paketverwaltung und Build-Einstellungen im Root, `tools/` isoliert
**Kontext:** `Directory.Build.props` und `Directory.Packages.props` im Root gelten für alle Projekte darunter, auch für die Einzeldatei-Werkzeuge in `tools/` (zum Beispiel `tools/roadmap.cs`, `tools/smoke-cli.cs`). Diese pinnen ihre Pakete selbst mit `#:package <Name>@<Version>`. Beim Spike brach `tools/roadmap.cs` deshalb mit NU1008 (bei zentraler Paketverwaltung darf die Version nicht am Verweis stehen).
**Entscheidung:** Versionen, Nullable, Warnungen als Fehler und `IsAotCompatible` stehen zentral im Root. `tools/Directory.Build.props` und `tools/Directory.Packages.props` isolieren die Werkzeuge davon.
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
