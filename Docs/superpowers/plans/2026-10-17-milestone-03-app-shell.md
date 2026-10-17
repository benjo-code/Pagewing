# Milestone 3 – App-Shell & schnelles Öffnen – Implementierungsplan

> **Für agentische Ausführer:** Pflicht-Skill: `superpowers:subagent-driven-development` (empfohlen) oder `superpowers:executing-plans`. Die Schritte nutzen Checkbox-Syntax (`- [ ]`).

**Ziel:** Pagewing öffnet eine PDF im gewählten Design „Werkbank“ (Richtung B) und scrollt flüssig durch alle Seiten, innerhalb der Startzeit-Budgets; ein Startzeit-Check in der CI belegt das.

**Architektur:** Alles ohne UI liegt in `Pagewing.Core` (Namespace `Pagewing.Core.Viewing` für Layout, Planung, Cache, Scheduler und die Sitzung `ViewerSession`, `Pagewing.Core.Hosting` für Single-Instance) und ist ohne Avalonia testbar. `Pagewing.App` ist eine dünne Avalonia-Schicht: Tokens und Stile, ein Fenster mit einem Dokument, ein eigenes Zeichen-Control `DocumentCanvas`, das nur die vom Scheduler gelieferten Bilder anzeigt. Der Scheduler hält genau einen Auftrag auf dem PdfiumThread und sortiert alles andere selbst um; der UI-Thread wartet nie auf PDFium. Gemessen wird mit versteckten Schaltern der App und dem Skript `bench/startup`.

**Tech-Stack:** .NET 10 mit Native AOT, Avalonia 12.1.4 (kompilierte Bindungen, kompiliertes XAML), eigene Core-Typen statt MVVM-Bibliothek, `Avalonia.Headless.XUnit` 12.1.4 für UI-Tests, xunit.v3.

**Spec:** [Gesamt-Spec](../specs/2026-10-16-pagewing-design.md) (Abschnitte 5.2 bis 5.4, 5.8, Zeile M3 in 6) und [Designsystem](../specs/2026-10-17-designsystem.md) (Abschnitte 5 bis 7 und 9). Roadmap: [Milestone 3](../../ROADMAP.md).

## Scope dieses Plans (am 2026-10-17 mit dem Projektinhaber abgestimmt)

M3 ist eine schlanke Leseansicht für **ein Dokument pro Fenster**, im festgelegten Design, mit Fokus auf Geschwindigkeit.

- **Enthalten:** Titelleiste mit einem Tab (Dateiname) und „Datei öffnen“, Dokumentbereich, Statusleiste mit Seitenfeld und Zoom, Startbildschirm, Passwort- und Fehlerkarte, Öffnen per Argument, Dialog (Strg+O) und Drag & Drop, Single-Instance, fortlaufendes Scrollen mit Scheduler und Cache, Zoom, resx (Englisch und Deutsch), Startzeit-Check.
- **Bewusst nicht enthalten:** Tabs (M4), Aktionsleiste und Panel mit Vorschaubildern, Inhalt, Suche (M4 bis M5), Dokumentinfos und Zoom-Modi (M4), Befehlspalette, Einstellungen, Dichte „Kompakt“, Umschalter für den dunklen Modus (M6). Funktionen späterer Milestones erscheinen nicht ausgegraut (CLAUDE.md). Die Tokens gibt es hell und dunkel; das Theme folgt dem System.
- Eine zweite Datei (Argument, Pipe, Drop) **ersetzt** das offene Dokument. Mehrere gedroppte Dateien: die erste wird geöffnet, die übrigen ignoriert (bis M4).

## Global Constraints

- .NET 10 (`net10.0`), Native AOT Pflicht für App und CLI; `IsAotCompatible` je Projekt unter `src/`; AOT- und Trim-Warnungen sind Fehler; keine Reflection-basierte Serialisierung, kein XAML zur Laufzeit. Avalonia 12.1.4. Warnungen sind Fehler (`TreatWarningsAsErrors`).
- „PDFium ist nicht thread-sicher“: PDFium wird nur über den `PdfiumThread` aufgerufen. Der UI-Thread wartet nie auf PDFium. Kein paralleles Rendern.
- Render-Pipeline laut Gesamt-Spec 5.3: nur sichtbare Seiten plus Puffer, erst grobe Vorschau, dann scharfe Kacheln (Richtwert 512×512 Gerätepixel), veraltete Aufträge werden abgebrochen, LRU-Cache mit Speicherbudget. Kacheln haben 64 px Überlapp nach links und sind nicht allgemein bitgleich zum Ganzseiten-Rendering (löst `PdfDocument.RenderTileAsync` selbst).
- Budgets (endgültig, nie lockern): Kaltstart bis erste Seite scharf ≤ 400 ms · Warmstart ≤ 200 ms · weitere Datei in laufender Instanz bis erste Seite ≤ 100 ms · 1000-seitige PDF öffnen bis erste Seite ≤ 300 ms. Startzeit-Check: Median aus 10 Läufen, Fehlschlag ab Budget + 25 %, Linux unter Xvfb. Nichts Neues im Startpfad ohne Messung; bis zum ersten Frame läuft keine Animation und braucht nichts PDFium.
- Single-Instance: Ein zweiter Aufruf übergibt die Datei an die laufende Instanz (Named Pipe bzw. Unix-Socket) und beendet sich.
- Design: Tokens, Maße und Verhalten aus dem Designsystem (Abschnitte 5 bis 7) wörtlich; Gelb nie Akzent (nur Suchtreffer); Zustände nie nur über Farbe; Tabler-Icons „outline“ mit 2 px Strich über ein eigenes Strich-Control (`PathIcon` füllt nur); Schrift Geist/Geist Mono als Teilmenge (OFL-1.1, mit `OFL.txt`), Fallback Systemschrift; Mindestgröße 640 × 480. Neue Farbpaare vor Verwendung auf WCAG 2.2 AA prüfen (Text 4,5:1, Icons/Ränder/Fokus 3:1).
- UI-Texte Englisch und Deutsch aus resx; Code und Kommentare Englisch; Doku und Commits Deutsch. Keine Netzwerkzugriffe, keine Telemetrie.
- Abhängigkeiten: Lizenz GPL-3.0-kompatibel, AOT-fähig, Eintrag in `Docs/DECISIONS.md`. Testnamen ohne Unterstriche (CA1707).
- Nur M3-Umfang (siehe oben); Scope-Änderungen in der Roadmap unter „Scope-Änderungen“, nicht stillschweigend.
- Git: Branch `feature/m03-app-shell` (vorhanden). Gezielt stagen (`git add <datei>`), nach jedem Commit `git show --stat HEAD` gegen die erwartete Dateiliste prüfen, Conventional Commits auf Deutsch, Trailer `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`. Kein Force-Push, kein `--no-verify`.
- Roadmap: Zu Beginn Status von M3 auf `in-progress` setzen. Nach jedem Task die unter **Roadmap** genannten Einträge abhaken (`"done": true`, Nachweis in `"evidence"`), `dotnet run tools/roadmap.cs -- generate` ausführen, beide Dateien mit committen. Abnahmekriterien erst abhaken, wenn geprüft.
- „Fertig“ heißt: `dotnet build Pagewing.slnx`, `dotnet test Pagewing.slnx`, `dotnet format Pagewing.slnx --verify-no-changes` und `dotnet run tools/roadmap.cs -- check` nachweislich grün, dazu der Startzeit-Check in der CI.
- Wo eine Avalonia-API in diesem Plan anders heißt als im Code von 12.1.4, gilt der Compiler (XML-Doku unter `~/.nuget/packages/avalonia/12.1.4/lib/net10.0/*.xml`); die Absicht des Schritts bleibt.

## Review Focus

Eingaben und Situationen, die die Specs nicht nennen, die im Alltag aber vorkommen. Die Prüfung steht jeweils im genannten Task.

1. **Ungewöhnliche Seiten:** Größe 0, NaN oder riesig (14 400 pt), gemischte Formate, gedrehte Seiten. Erwartung: keine Ausnahme, Platzhalter in sinnvoller Größe, nie mehr als eine begrenzte Zahl Aufträge (Tasks 1, 2, 11).
2. **Kaputte, gesperrte, verschlüsselte oder leere Dateien und Pfade mit Sonderzeichen** (Unicode, Leerzeichen, ungültige Zeichen, Ordner statt Datei, 0-Byte-Datei). Erwartung: verständliche Karte statt Absturz, das alte Dokument bleibt bei einem Fehler nicht halb sichtbar (Tasks 5, 10).
3. **Schnelles Wechseln:** neue Datei während das alte noch öffnet oder rendert, Zoomen während des Renderns, Fenster schließen. Erwartung: letzte Anfrage gewinnt, keine Ereignisse nach Dispose, keine Ausnahme (Tasks 4, 5, 10, 11).
4. **Schnelles Scrollen durch 1000 Seiten bei 10 % Zoom:** Erwartung: Auftragsflut begrenzt, UI-Thread frei, Speicher unter dem Cache-Budget (Tasks 2, 3, 4, 11).
5. **Zweiter Start:** relativer Pfad, Unicode und Leerzeichen, zweiter Aufruf bevor die erste Instanz die Pipe öffnet, Datenmüll auf der Pipe, abgestürzte erste Instanz. Erwartung: Datei öffnet in der laufenden Instanz bzw. der neue Prozess wird selbst primär; Datenmüll wird ignoriert (Tasks 6, 10).

Außerdem durchgängig getestet: Bildschirmskalierung 125 %/150 % (Task 2) und Systemsprache ohne Übersetzung → Englisch (Task 7).

## Entscheidungen dieses Plans (eigene Entscheidungen sind markiert)

- **Keine MVVM-Bibliothek** (eigene `ViewModelBase`, `RelayCommand`): weniger Abhängigkeiten, kompilierte Bindungen genügen. *Eigene Entscheidung.*
- **Lokalisierung ohne Satellitenassemblies:** `Strings.en.resx` und `Strings.de.resx` werden mit `WithCulture="false"` beide in die Haupt-Assembly eingebettet, `Strings` wählt nach `CurrentUICulture` (Fallback Englisch). Grund: Satellitenassemblies müssen zur Laufzeit geladen werden, was unter Native AOT riskant ist. *Eigene Entscheidung,* Eintrag in `DECISIONS.md`.
- **Kachelparameter** (in `TilePlanner` als Konstanten, bei der Messung in Task 13 zu bestätigen): Kachel 512 px; Seiten bis 4 000 000 Pixel werden als **eine** Ganzseiten-Kachel gerendert (A4 bis etwa 200 %), größere in 512er-Raster; Vorschau längste Seite 320 px; höchstens 48 Vorschau-Aufträge; Cache 256 MiB; Zoom 10 % bis 800 % in den Stufen 10, 25, 50, 75, 100, 125, 150, 200, 300, 400, 600, 800. 100 % = 96 dpi (1 pt = 96/72 DIP). *Eigene Entscheidung.*
- **Scheduler:** genau ein Auftrag gleichzeitig auf dem PdfiumThread (die Warteschlange liegt im Scheduler, nicht im PdfiumThread). Löst die offenen Punkte aus `DECISIONS.md`, Eintrag „Offene Punkte für den Render-Scheduler (M3)“: Abbruch wird prompt, Cache rechnet mit der Länge des Backing-Arrays, `Path.GetFullPath`-Fehler werden zu `CannotRead`. *Eigene Entscheidung.*
- **Seitengrößen** kommen in Stapeln (erste 32 sofort, der Rest in Hintergrund-Stapeln zu je 256); unbekannte Seiten nehmen die Größe der ersten Seite an, der Anker (oberste sichtbare Seite) bleibt beim Nachladen stabil. *Eigene Entscheidung.*
- **Früher Öffnen-Start:** `Program.Main` startet `ViewerSession.OpenAsync` für ein Dateiargument sofort, parallel zur Avalonia-Initialisierung (PDFium lädt dann auf dem eigenen Thread, während das Fenster entsteht). Task 13 vergleicht das mit „nach dem ersten Frame“. *Eigene Entscheidung.*
- **Messschalter** (versteckt): `--measure-startup <pdf>`, `--measure-second <pdfA> <pdfB>`, `--measure-scroll <pdf>`; Ausgabe als Zeilen auf stdout, die Zeit ab Prozessstart misst das Skript. Das Budget „weitere Datei“ wird in einem Prozess gemessen (ohne Prozessstart und Pipe), die Pipe hat einen eigenen Test. Ein echter Kaltstart (Datei-Cache leer) ist in der CI nicht herstellbar und bleibt eine manuelle Messung des Projektinhabers. *Eigene Entscheidung.*
- **`--lang <code>`** überschreibt die UI-Sprache (Test und manuelle Prüfung; Einstellung kommt in M6). *Eigene Entscheidung.*

## Dateistruktur

```
src/Pagewing.Core/
  Pdf/PdfDocument.cs                  # Task 1: GetPageSizesAsync
  Viewing/ZoomSteps.cs                # Task 1
  Viewing/DipRect.cs                  # Task 1
  Viewing/DocumentLayout.cs           # Task 1
  Viewing/TileKey.cs                  # Task 2 (TileKey, RenderRequest)
  Viewing/TilePlanner.cs              # Task 2
  Viewing/ViewportPlanner.cs          # Task 2
  Viewing/RenderCache.cs              # Task 3
  Viewing/RenderScheduler.cs          # Task 4
  Viewing/ViewerSession.cs            # Task 5
  Hosting/SingleInstance.cs           # Task 6
src/Pagewing.App/
  Program.cs, App.axaml(.cs)          # Tasks 7, 10
  StartupOptions.cs                   # Task 7
  Resources/Strings.{en,de}.resx, Strings.cs          # Task 7
  Styles/Tokens.axaml, Metrics.axaml, Fonts.axaml, Icons.axaml, Controls.axaml   # Task 8
  Assets/Fonts/*.ttf                  # Task 8
  Controls/StrokeIcon.cs              # Task 8
  Controls/MiddleEllipsisTextBlock.cs # Task 9
  ViewModels/ViewModelBase.cs, RelayCommand.cs, MainViewModel.cs   # Tasks 9, 10
  Views/MainWindow, TitleBar, StatusBar, StartScreen, PasswordCard, ErrorCard (.axaml/.cs)   # Tasks 9, 10
  Services/IFilePicker.cs, AvaloniaFilePicker.cs      # Task 10
  Controls/DocumentCanvas.cs, DocumentView.axaml(.cs), TileBitmaps.cs   # Task 11
  Measurement/StartupMeasurement.cs, ScrollMeasurement.cs               # Task 12
tests/Pagewing.Core.Tests/Viewing/*Tests.cs, Hosting/SingleInstanceTests.cs, Pdf/PageSizesTests.cs
tests/Pagewing.App.Tests/             # neu in Task 7
bench/startup/startup.cs, README.md   # Task 12
third-party/geist/, third-party/tabler-icons/   # Task 8 (Lizenztexte)
```

---

### Task 1: Seitengrößen im Stapel, Zoomstufen, Layout

**Files:**
- Modify: `src/Pagewing.Core/Pdf/PdfDocument.cs`
- Create: `src/Pagewing.Core/Viewing/ZoomSteps.cs`, `DipRect.cs`, `DocumentLayout.cs`
- Test: `tests/Pagewing.Core.Tests/Pdf/PageSizesTests.cs`, `tests/Pagewing.Core.Tests/Viewing/ZoomStepsTests.cs`, `DocumentLayoutTests.cs`

**Interfaces:**
- Produces:
  - `Task<IReadOnlyList<PdfPageSize>> PdfDocument.GetPageSizesAsync(int firstPage, int count, PdfPriority priority = PdfPriority.Interactive, CancellationToken cancellationToken = default)` – eine Anfrage an den PdfiumThread für den ganzen Stapel; eine Seite, deren Größe nicht lesbar ist, meldet A4 (595,28 × 841,89 pt) statt den Stapel scheitern zu lassen. `ArgumentOutOfRangeException` bei Bereich außerhalb des Dokuments.
  - `static class ZoomSteps { const double Min = 0.1, Max = 8.0, Default = 1.0; double Clamp(double); double Next(double); double Previous(double); }` (NaN → `Default`).
  - `readonly record struct DipRect(double X, double Y, double Width, double Height)` mit `Right`, `Bottom`.
  - `sealed class DocumentLayout` (nur UI-Thread): `const double DipsPerPoint = 96.0/72.0, PageGap = 20, Margin = 32`; `DocumentLayout(int pageCount, PdfPageSize estimate)`; `int PageCount`; `double Zoom {get;set;}` (geklemmt); `int KnownPageCount`; `void SetSizes(int firstPage, IReadOnlyList<PdfPageSize> sizes)`; `bool IsSizeKnown(int page)`; `PdfPageSize GetPageSize(int page)` (Punkte, Schätzwert wenn unbekannt); `double ContentWidth`, `double ContentHeight`; `DipRect GetPageRect(int page)` (Inhaltskoordinaten, Seite waagerecht zentriert); `(int First, int Last) GetVisiblePages(double top, double bottom)` (inklusiv, `(0, -1)` ohne Seiten); `int GetMostVisiblePage(double top, double bottom)`; `PageAnchor GetAnchor(double top)` und `double GetOffset(PageAnchor anchor)` mit `readonly record struct PageAnchor(int Page, double Fraction)`.

- [ ] **Step 1: Tests schreiben (schlagen fehl).**
  - `PageSizesTests`: `GetPageSizesAsync(0, 1000)` auf `thousand-pages.pdf` liefert 1000 Einträge `(200, 100)`; `(999, 1)` geht, `(990, 100)` und `(-1, 1)` werfen `ArgumentOutOfRangeException`; auf `rotated-page.pdf` und `cropbox.pdf` ist jeder Eintrag gleich `GetPageSizeAsync`.
  - `ZoomStepsTests` (Theory): `Next(1.0) == 1.25`, `Previous(1.0) == 0.75`, `Next(1.1) == 1.25`, `Previous(1.1) == 1.0`, `Next(8) == 8`, `Previous(0.1) == 0.1`, `Clamp(100) == 8`, `Clamp(0) == 0.1`, `Clamp(double.NaN) == 1.0`.
  - `DocumentLayoutTests`: `ContentHeightSumsPagesGapsAndMargins` (3 × 612×792 bei Zoom 1: `3*1056 + 2*20 + 2*32`); `PagesAreCenteredInTheContentWidth` (schmale Seite: `X == (ContentWidth - Width) / 2`); `UnknownPagesUseTheEstimate` (1000 Seiten, Schätzung 200×100, nach `SetSizes(0, …)` ändert sich nur die Höhe der bekannten); `ZoomScalesPagesButNotMarginAndGap`; `GetVisiblePagesReturnsAnInclusiveRange` (Bereich in der Lücke, teilweise Überlappung, hinter dem Ende, null Seiten); `MostVisiblePageHasTheLargestVisibleShare`; `AnchorSurvivesZoomAndSizeChanges` (Anker bei `top`, danach Zoom ändern und `SetSizes` für frühere Seiten, `GetOffset(anchor)` zeigt dieselbe Seite mit demselben Bruchteil); `NonFiniteOrNonPositiveSizesFallBackToTheEstimate` (0, NaN, negativ, ∞).
- [ ] **Step 2:** `dotnet test tests/Pagewing.Core.Tests --filter-namespace "*Viewing*"` – erwartet: Build-Fehler (Typen fehlen).
- [ ] **Step 3: `ZoomSteps`, `DipRect`, `DocumentLayout` und `GetPageSizesAsync` implementieren.** Layout: Präfixsummen der Seitenhöhen, bei `SetSizes` und `Zoom` lazy neu berechnet; Suche nach Seite per Binärsuche, damit 1000 Seiten pro Scroll-Ereignis billig bleiben.
- [ ] **Step 4:** Dieselben Tests – erwartet: grün.
- [ ] **Step 5: Commit** (`feat(core): Seitengrößen im Stapel, Zoomstufen und Dokument-Layout`).

**Roadmap:** nur Task-Kommentar, noch nichts abhaken.

### Task 2: Kachel-Schlüssel, Kachel- und Viewport-Planung

**Files:**
- Create: `src/Pagewing.Core/Viewing/TileKey.cs`, `TilePlanner.cs`, `ViewportPlanner.cs`
- Test: `tests/Pagewing.Core.Tests/Viewing/TilePlannerTests.cs`, `ViewportPlannerTests.cs`

**Interfaces:**
- Consumes: Task 1 (`DocumentLayout`, `DipRect`, `ZoomSteps`); `PdfPageSize.ToPixels`, `PixelRect` aus `Pagewing.Core.Pdf`.
- Produces:
  - `readonly record struct TileKey(int Page, int ScaleKey, int Column, int Row)` mit `const int PreviewScaleKey = 0`, `bool IsPreview`, `double Scale => ScaleKey / 1000.0`, `static int ToScaleKey(double scale)` (`round(scale * 1000)`, mindestens 1). Gerendert wird immer mit `ScaleKey / 1000.0`, damit alle Kacheln einer Seite dasselbe Pixelraster haben.
  - `readonly record struct RenderRequest(TileKey Key, double Scale, PixelRect Rect, bool WholePage, bool Background)`. **Die Reihenfolge der Liste ist die Priorität.**
  - `static class TilePlanner`: `const int TileSize = 512, PreviewMaxSide = 320; const long WholePageMaxPixels = 4_000_000;` `double PreviewScale(PdfPageSize)` (`PreviewMaxSide / längste Seite`); `IReadOnlyList<RenderRequest> PlanSharp(int page, PdfPageSize size, double scale, PixelRect visible, bool background)` – nur Kacheln, die `visible` (Seitenpixel) schneiden, nach Abstand ihrer Mitte zur Mitte von `visible` sortiert; passt die Seite in `WholePageMaxPixels`, genau eine Kachel mit `WholePage = true`. Rechnet mit `long`.
  - `static class ViewportPlanner`: `const int MaxPreviewRequests = 48; IReadOnlyList<RenderRequest> Plan(DocumentLayout layout, DipRect viewport, double renderScaling)` – Reihenfolge: Vorschauen der sichtbaren Seiten (nach Abstand zur Viewport-Mitte), scharfe Kacheln der sichtbaren Seiten, Vorschauen der Pufferseiten (Seiten im Bereich eine Viewport-Höhe über und unter dem Viewport, `Background = true`). Scale = `Zoom * DipsPerPoint * renderScaling`.

- [ ] **Step 1: Tests schreiben (schlagen fehl).**
  - `TilePlannerTests`: `ScaleKeyRoundTrips` (`ToScaleKey(1.3333) == 1333`); `PreviewScaleFitsTheLongestSide` (A4 → `320/841.89`; 200×100 pt → 1.6); `SmallPagesAreOneWholePageTile` (A4 bei 1.5 → eine Kachel, `WholePage`); `LargePagesAreSplitIntoA512Grid` (A4 bei 4.0: 2381×3368 px → Raster 5 × 7, Randkacheln 333 px breit und 296 px hoch); `OnlyTilesIntersectingTheVisibleRectArePlanned`; `TilesAreSortedByDistanceToTheVisibleCenter`; `HugePagePlansOnlyVisibleTiles` (14 400 × 14 400 pt bei Scale 10.67, sichtbar 1920×1080 → höchstens 12 Kacheln, kein Überlauf).
  - `ViewportPlannerTests`: `VisiblePreviewsComeFirstThenSharpTilesThenBufferPreviews`; `BufferPagesGetBackgroundPreviewsOnly`; `ARequestFloodIsCapped` (1000 kleine Seiten bei Zoom 0.1, Viewport 1080 hoch → höchstens `MaxPreviewRequests` Vorschauen, Gesamtzahl der Aufträge unter 200); `RenderScalingIsAppliedToTheScaleKey` (Zoom 1, Skalierung 1.25 und 1.5 → ScaleKey 1667 bzw. 2000); `HorizontalScrollLimitsTheTiles` (Zoom 8, Viewport schmaler als die Seite → keine Kachel außerhalb); `EmptyDocumentPlansNothing`.
- [ ] **Step 2:** `dotnet test tests/Pagewing.Core.Tests --filter-class "*PlannerTests"` – erwartet: Build-Fehler.
- [ ] **Step 3: Implementieren** (Signaturen oben). Die Vorschau ist immer `WholePage = true` mit `Rect = (0, 0, ToPixels(PreviewScale))`.
- [ ] **Step 4:** Tests grün.
- [ ] **Step 5: Commit** (`feat(core): Kachel- und Viewport-Planung`).

### Task 3: RenderCache (LRU mit Speicherbudget)

**Files:**
- Create: `src/Pagewing.Core/Viewing/RenderCache.cs`
- Test: `tests/Pagewing.Core.Tests/Viewing/RenderCacheTests.cs`

**Interfaces:**
- Consumes: `TileKey` (Task 2), `RenderedImage`.
- Produces: `sealed class RenderCache` – `const long DefaultBudgetBytes = 256L * 1024 * 1024; RenderCache(long budgetBytes = DefaultBudgetBytes); long BudgetBytes, UsedBytes; int Count; bool TryGet(TileKey, out RenderedImage) (setzt zuletzt benutzt); bool Contains(TileKey) (ohne Wirkung auf die Reihenfolge); void Add(TileKey, RenderedImage) (ersetzt, verdrängt die am längsten unbenutzten bis UsedBytes ≤ Budget; ein Bild über dem Budget wird nicht gespeichert); void Clear(); static long CostOf(RenderedImage)` – Länge des Backing-Arrays (`MemoryMarshal.TryGetArray`), sonst `Pixels.Length` (Begründung: Kacheln mit Überlapp behalten das ganze Array, siehe `DECISIONS.md`). Thread-sicher.

- [ ] **Step 1: Tests schreiben:** `EvictsLeastRecentlyUsedWhenOverBudget`; `TryGetRefreshesRecency`; `ContainsDoesNotRefreshRecency`; `ImageLargerThanTheBudgetIsNotStored`; `CostUsesTheBackingArrayLength` (`RenderedImage` über einen Ausschnitt eines größeren Arrays); `ReplacingAnEntryUpdatesUsedBytes`; `ParallelAddAndGetKeepTheBudget` (8 Tasks × 10 000 Zugriffe, danach `UsedBytes <= BudgetBytes` und `UsedBytes` gleich der Summe über alle noch enthaltenen Einträge).
- [ ] **Step 2:** Fehlschlag prüfen (`--filter-class "*RenderCacheTests"`), **Step 3:** implementieren (verkettete Liste plus Dictionary unter einem Lock), **Step 4:** grün.
- [ ] **Step 5: Commit** (`feat(core): LRU-Cache mit Speicherbudget für gerenderte Kacheln`).

### Task 4: RenderScheduler

**Files:**
- Modify: `src/Pagewing.Core/Pagewing.Core.csproj` (`InternalsVisibleTo Pagewing.Core.Tests`)
- Create: `src/Pagewing.Core/Viewing/RenderScheduler.cs`
- Test: `tests/Pagewing.Core.Tests/Viewing/RenderSchedulerTests.cs`
- Modify: `Docs/DECISIONS.md` (Eintrag „Render-Scheduler und Kachelparameter (M3)“: genau ein Auftrag auf dem PdfiumThread, Konstanten aus „Entscheidungen dieses Plans“, Bezug auf „Offene Punkte für den Render-Scheduler (M3)“)

**Interfaces:**
- Consumes: `RenderRequest`, `TileKey` (Task 2), `RenderCache` (Task 3), `PdfDocument.RenderPageAsync/RenderTileAsync`, `RenderOptions`.
- Produces: `sealed class RenderScheduler : IDisposable` – `RenderScheduler(PdfDocument document, RenderCache cache)` (öffentlich; wählt `RenderPageAsync`, wenn `WholePage`, sonst `RenderTileAsync`; `RenderOptions.Priority` = `Background` bzw. `Interactive` je `RenderRequest.Background`); interner Konstruktor `RenderScheduler(Func<RenderRequest, CancellationToken, Task<RenderedImage>> render, RenderCache cache)` für Tests; `void Update(IReadOnlyList<RenderRequest> wanted)`; `int PendingCount`; `event Action<TileKey>? TileReady` (auf einem Pool-Thread, nachdem das Bild im Cache liegt); `event Action<TileKey, Exception>? TileFailed`; `void Dispose()`.
- Verhalten: `Update` ersetzt die Wunschliste (Reihenfolge = Priorität); Schlüssel, die schon im Cache liegen, werden übersprungen; der laufende Auftrag wird abgebrochen (`CancellationToken`), wenn er nicht mehr gewünscht ist, sonst nicht neu gestartet; abgebrochene Aufträge lösen kein `TileReady` aus; ein fehlgeschlagener Schlüssel wird einmal gemeldet und nicht erneut versucht (bis `Dispose`); nach `Dispose` keine Ereignisse mehr. Ein Worker-Task, nie zwei Aufträge gleichzeitig.

- [ ] **Step 1: Tests schreiben (mit dem Fake-Renderer).** `RunsOneRenderAtATime`; `RendersInTheOrderOfTheList`; `ReprioritizesQueuedRequestsOnUpdate`; `UpdateDropsQueuedRequestsThatAreNoLongerWanted` (50 Aufträge, danach `Update` mit einem anderen → höchstens ein alter wird noch fertig); `UpdateCancelsTheInFlightRenderWhenNoLongerWanted` (Token wird abgebrochen); `UpdateKeepsTheInFlightRenderWhenStillWanted` (Token bleibt frei, der Renderer läuft nur einmal); `CancelledRenderDoesNotRaiseTileReady`; `SkipsKeysAlreadyInTheCache`; `FailedKeyIsReportedOnceAndNotRetried`; `DisposeCancelsAndSilencesEvents`; `BackgroundFlagReachesTheRenderer`. Integrationstest mit echtem PDFium: `RendersRealTilesOfAThousandPageDocument` (Seite 500: Vorschau und eine Kachel; Bildgrößen gleich `Rect`, beide im Cache).
- [ ] **Step 2:** Fehlschlag prüfen (`--filter-class "*RenderSchedulerTests"`), **Step 3:** implementieren (Registrierung am Token, Worker-Schleife mit `SemaphoreSlim`), **Step 4:** grün, auch 10-mal hintereinander (`for i in 1..10`), weil Nebenläufigkeit.
- [ ] **Step 5: Commit** (`feat(core): Render-Scheduler mit Abbruch veralteter Aufträge`).

**Roadmap:** Beim Abschluss von Task 11 abhaken (Scheduler und Cache sind erst dort angeschlossen).

### Task 5: ViewerSession

**Files:**
- Create: `src/Pagewing.Core/Viewing/ViewerSession.cs`
- Test: `tests/Pagewing.Core.Tests/Viewing/ViewerSessionTests.cs`

**Interfaces:**
- Consumes: `PdfEngine.OpenAsync`, `PdfOpenException`, `PdfDocument.GetPageSizesAsync` (Task 1), `RenderCache`, `RenderScheduler`.
- Produces: `enum ViewerState { Empty, Loading, PasswordRequired, PasswordIncorrect, Failed, Ready }`; `sealed class ViewerSession : IDisposable` – `ViewerSession(PdfEngine engine)`; `ViewerState State`; `string? Path` (voller Pfad der angefragten Datei); `PdfOpenError? Error`; `PdfDocument? Document`; `RenderCache? Cache`; `RenderScheduler? Scheduler`; `IReadOnlyList<PdfPageSize> LoadedSizes` (Präfix der bekannten Seitengrößen, atomar ersetzt); `event Action? Changed` (auf einem beliebigen Thread: bei jedem Zustandswechsel und nach jedem Größen-Stapel); `Task OpenAsync(string path, string? password = null)` – **die letzte Anfrage gewinnt**: eine ältere, noch laufende Anfrage endet ohne Ausnahme und ohne `Changed`; das vorher offene Dokument wird erst geschlossen, wenn die neue Anfrage angenommen ist; Zustand `Ready`, sobald die ersten 32 Seitengrößen da sind, der Rest folgt in Hintergrund-Stapeln zu 256; `void Close()` (zurück zu `Empty`, Dokument und Scheduler freigeben); `Dispose()`.
- Fehlerabbildung: `PdfOpenException.Error` wie sie ist; `Path.GetFullPath`-Ausnahmen (`ArgumentException`, `PathTooLongException`, `NotSupportedException`) → `CannotRead`. `PasswordRequired`/`PasswordIncorrect` behalten `Path`, sodass `OpenAsync(Path, passwort)` erneut aufgerufen werden kann.

- [ ] **Step 1: Tests schreiben (echte Test-PDFs):** `OpenReachesReadyAndLoadsAllPageSizes` (`thousand-pages.pdf`: bei `Ready` mindestens 32, später genau 1000 Größen); `EncryptedFileAsksForAPasswordThenAcceptsIt` (`encrypted_hello_world_r3.pdf`: `PasswordRequired`, falsches Passwort → `PasswordIncorrect`, richtiges Passwort aus `OpenTests` → `Ready`); `MissingFileFails`; `NotAPdfFails`; `EmptyFileFails` (`empty.pdf`, 0 Byte → `NotAPdf`); `DirectoryInsteadOfFileFails` (Ordner → `CannotRead`); `InvalidPathCharactersMapToCannotRead` (Pfad mit `\0`); `PathWithUnicodeAndSpacesOpens` (Kopie von `hello_world.pdf` in einem temporären Ordner `Ünï cödé`); `LatestOpenWins` (`thousand-pages.pdf`, sofort danach `text-basic.pdf`: am Ende `Ready` mit dem zweiten Pfad, kein `Changed` mit dem ersten Pfad nach dem zweiten Ready, beide `await` ohne Ausnahme); `FailedOpenKeepsNothingOfTheOldDocument` (zuerst gültige, dann ungültige Datei → `Failed`, `Document` ist `null`); `CloseReturnsToEmptyAndReleasesTheDocument`; `NoChangedEventsAfterDispose`.
- [ ] **Step 2:** Fehlschlag prüfen, **Step 3:** implementieren (Zähler pro Anfrage, `CancellationTokenSource` für die laufende), **Step 4:** grün (auch 10-mal wiederholt).
- [ ] **Step 5: Commit** (`feat(core): ViewerSession mit Öffnen, Passwort und Fehlerzuständen`).

### Task 6: Single-Instance (Core)

**Files:**
- Create: `src/Pagewing.Core/Hosting/SingleInstance.cs`
- Test: `tests/Pagewing.Core.Tests/Hosting/SingleInstanceTests.cs`
- Modify: `Docs/DECISIONS.md` (Eintrag „Single-Instance: Sperrdatei und Named Pipe“)

**Interfaces:**
- Produces: `sealed class SingleInstance : IDisposable` – `static string DefaultName` (`"pagewing-" + Benutzername`); `static SingleInstance? TryBecomePrimary(string name)` – nimmt eine exklusive Sperrdatei (`FileShare.None` in `Path.GetTempPath()`, Name aus `name`); `null`, wenn eine andere Instanz sie hält (das Betriebssystem gibt die Sperre bei einem Absturz frei, es gibt keine veraltete Sperre); `static bool TrySend(string name, IReadOnlyList<string> paths, TimeSpan timeout)` – verbindet sich mit der Pipe der primären Instanz (Wiederholungen bis `timeout`, weil sie die Pipe erst nach dem ersten Frame öffnet) und sendet die Pfade, leere Liste = nur aktivieren; `void StartListening(Action<IReadOnlyList<string>> onMessage)` (Annahme-Schleife auf einem Hintergrund-Thread, Rückruf auf einem Pool-Thread); `Dispose()`.
- Protokoll: `BinaryWriter`: Anzahl (int32), dann je Pfad ein UTF-8-String mit Längenpräfix. Der Empfänger verwirft Nachrichten mit mehr als 64 Pfaden, Pfaden über 32 768 Zeichen oder abgeschnittenem Inhalt. Pipe mit `PipeOptions.CurrentUserOnly`. Die Pipe (.NET nutzt unter Linux Unix-Sockets) ist auf beiden Systemen derselbe Code.

- [ ] **Step 1: Tests schreiben (eindeutiger Name je Test per `Guid`):** `FirstInstanceBecomesPrimaryAndSecondDoesNot`; `LockIsReleasedOnDispose`; `PathsArriveAtThePrimary` (Unicode, Leerzeichen, 3 Pfade); `EmptyListActivatesOnly`; `SendWaitsForTheListener` (`TrySend` startet 300 ms vor `StartListening`, kommt trotzdem an); `SendFailsAfterTheTimeoutWithoutAPrimary` (`false`, Dauer ungefähr `timeout`); `GarbageOnThePipeIsIgnoredAndTheListenerKeepsWorking` (zufällige Bytes, Länge 2 GB im Präfix, abgeschnittene Nachricht, danach eine gültige Nachricht kommt an).
- [ ] **Step 2:** Fehlschlag prüfen, **Step 3:** implementieren, **Step 4:** grün (auch 10-mal).
- [ ] **Step 5: Commit** (`feat(core): Single-Instance über Sperrdatei und Named Pipe`).

### Task 7: App.Tests-Gerüst, Lokalisierung, Startoptionen

**Files:**
- Create: `tests/Pagewing.App.Tests/Pagewing.App.Tests.csproj`, `TestAppBuilder.cs`, `LocalizationTests.cs`, `StartupOptionsTests.cs`; `src/Pagewing.App/Resources/Strings.en.resx`, `Strings.de.resx`, `Strings.cs`; `src/Pagewing.App/StartupOptions.cs`
- Modify: `Pagewing.slnx`, `Directory.Packages.props` (`Avalonia.Headless.XUnit` 12.1.4), `src/Pagewing.App/Pagewing.App.csproj` (`InternalsVisibleTo Pagewing.App.Tests`; beide resx als `EmbeddedResource` mit `WithCulture="false"` und `LogicalName`), `src/Pagewing.App/Program.cs`, `Docs/DECISIONS.md` (Einträge „Avalonia.Headless.XUnit als Testabhängigkeit“ [MIT, nur Tests], „Lokalisierung ohne Satellitenassemblies“, „Keine MVVM-Bibliothek“)

**Interfaces:**
- Produces:
  - `static class Strings` mit statischen String-Properties je UI-Text (Namen wie Resx-Schlüssel, PascalCase), `static CultureInfo Culture {get;set;}` (Start: `CurrentUICulture`, zwei Buchstaben `de` → Deutsch, alles andere Englisch). Anfangsschlüssel: `AppName`, `OpenFile`, `OpenFileTooltip`, `CloseTab`, `StartTitle`, `StartHint`, `StartOffline`, `StartDropHint`, `ChooseFile`, `PasswordTitle`, `PasswordPrompt` (mit `{0}` = Dateiname), `PasswordIncorrect`, `PasswordCancel`, `PasswordOpen`, `ErrorTitle`, `ErrorNotAPdf`, `ErrorFileNotFound`, `ErrorCannotRead`, `ErrorUnsupportedEncryption`, `ErrorUnknown`, `ErrorDetails`, `ErrorCloseTab`, `ErrorOpenOther`, `PageLabel`, `PageOf` (mit `{0}`), `PreviousPage`, `NextPage`, `ZoomOut`, `ZoomIn`, `ZoomValue`, `PagePlaceholderFailed`. Die deutschen Texte der Karten stehen wörtlich in Designsystem 7.8, Startbildschirm in 7.1, Tooltips mit Tastenkürzel in 7.9; die englischen Texte sinngemäß.
  - `sealed record StartupOptions(IReadOnlyList<string> Paths, string? MeasureStartup, (string First, string Second)? MeasureSecond, string? MeasureScroll, string? Language)` mit `static StartupOptions Parse(string[] args)`: Argumente ohne `--` sind Pfade (zu vollen Pfaden relativ zum Arbeitsordner gemacht), `--measure-startup <pdf>`, `--measure-second <a> <b>`, `--measure-scroll <pdf>`, `--lang <code>`; unbekannte `--`-Optionen werden ignoriert.

- [ ] **Step 1: Tests schreiben.** `LocalizationTests`: `BothLanguagesDefineTheSameKeys` (liest beide resx per `XDocument`); `EveryKeyHasAnAccessorAndEveryAccessorAKey` (Reflection nur im Test); `GermanDiffersFromEnglishForUiTexts` (außer `AppName`); `UnknownLanguageFallsBackToEnglish` (`Strings.Culture = new("fr")`); `PlaceholdersMatchBetweenLanguages` (gleiche `{n}`). `StartupOptionsTests`: relative Pfade werden voll; mehrere Pfade; `--lang de`; Pfad mit Leerzeichen; unbekannte Option ignoriert; `--measure-second` ohne zweites Argument → wird ignoriert (kein Absturz).
- [ ] **Step 2:** Fehlschlag prüfen (`dotnet test tests/Pagewing.App.Tests`), **Step 3:** implementieren; `TestAppBuilder` mit `[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]`, `UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })` und `UseSkia()` (für Bildaufnahmen). Lässt sich `Avalonia.Headless.XUnit` 12.1.4 mit `xunit.v3` 4.0.2 nicht wiederherstellen (es verlangt `xunit.v3.extensibility.core` ≥ 3.2.2), die Abweichung in `DECISIONS.md` festhalten und die kleinste funktionierende Kombination wählen.
- [ ] **Step 4:** grün; `dotnet build Pagewing.slnx` ohne Warnungen.
- [ ] **Step 5: AOT-Prüfung der Lokalisierung:** `dotnet publish src/Pagewing.App -c Release -r win-x64 -o out/app` ohne Warnungen; ein kleiner Test im Programm reicht nicht, deshalb in Task 9 beim ersten sichtbaren Text prüfen, dass `--lang de` und `--lang en` im AOT-Binary unterschiedliche Texte zeigen (Eintrag in der Abnahmeliste von Task 13).
- [ ] **Step 6: Commit** (`feat(app): Test-Gerüst, Lokalisierung (en/de) und Startoptionen`).

**Roadmap:** „`Pagewing.App.Tests` mit Avalonia.Headless“ abhaken (Nachweis: `dotnet test tests/Pagewing.App.Tests`, Anzahl).

### Task 8: Tokens, Themes, Schrift, Icons

**Files:**
- Create: `src/Pagewing.App/Styles/Tokens.axaml`, `Metrics.axaml`, `Fonts.axaml`, `Icons.axaml`, `Controls.axaml`; `src/Pagewing.App/Controls/StrokeIcon.cs`; `src/Pagewing.App/Assets/Fonts/` (Geist Regular/Medium/SemiBold, Geist Mono Regular/Medium als Teilmenge); `third-party/geist/OFL.txt` und `README.md` (Quelle, Version, Teilmengen-Befehl); `third-party/tabler-icons/LICENSE`
- Modify: `src/Pagewing.App/App.axaml` (Stile einbinden, `FluentTheme` mit `ColorPaletteResources` je Variante), `Pagewing.App.csproj` (`AvaloniaResource` für Schriften)
- Test: `tests/Pagewing.App.Tests/TokenTests.cs`, `StrokeIconTests.cs`, `tests/Pagewing.App.Tests/Wcag.cs`

**Interfaces:**
- Produces: Ressourcen `Pw.Color.*` (`Color`) und `Pw.Brush.*` (`SolidColorBrush`) für **jedes** Farb-Token aus Designsystem 5.1 (`color.surface.window` → `Pw.Color.Surface.Window` usw.), hell und dunkel über `ThemeDictionaries` mit den Werten der Spec; Maße `Pw.Space.0…6`, `Pw.Radius.Control/Overlay/Tab`, Typografie-Rollen aus 5.2 als Stile (`Classes="title|subtitle|strong|body|caption|mono"`); `Pw.Font.Sans` / `Pw.Font.Mono`; `StrokeIcon : Control` mit `Data` (`Geometry`), `Brush`, 24×24-Raster, 2 px Strich, runde Enden und Ecken, skaliert auf `Width`/`Height`; Icon-Geometrien (Tabler 3.49.0 outline, MIT) nur für die in M3 genutzten Icons: `file-text`, `folder-open`, `x`, `chevron-left`, `chevron-right`, `minus`, `plus`, `lock`, `alert-triangle`. Stile für Knopf (Primär, Sekundär mit `border.strong`, Icon-Knopf), Textfeld, ToolTip und Fokusring (2 px `focus.ring`, 1 px Abstand, nur bei Tastaturbedienung).
- Fluent-Abbildung: `Accent` = `accent.default`, `RegionColor` = `surface.window`, `BaseHigh` = `text.primary`, `BaseMedium` = `text.secondary`, `ErrorText` = `danger.default`; die übrigen Rollen (`Chrome*`, `Alt*`, `List*`) so setzen, dass Textfeld, Knopf und Liste zu den Tokens passen (Sichtvergleich mit [richtung-b-werkbank.html](../../design/mockups/richtung-b-werkbank.html) in Task 13).

- [ ] **Step 1: Tests schreiben.** `TokenTests`: `EveryTokenOfTheSpecExistsInBothThemes` (Namensliste aus 5.1 im Test); `TokenValuesMatchTheSpec` (Stichprobe: `Surface.Window` hell `#EEEFF2`, dunkel `#15171B`; `Accent.Default` hell `#3B48C4`, dunkel `#9DA7FF`; `Search.Hit` in beiden `#FBE38A`); `TextAndAccentContrastMeetsAA` (`Wcag.Contrast`-Hilfsfunktion; die Paare und Schwellen wörtlich aus der Regelliste unter Designsystem 5.1, beide Themes); `BordersAndFocusContrastMeets3To1`; `AccentIsNeverYellow`. `StrokeIconTests`: `PlusIconDrawsAStrokeNotAFill` (24×24 rendern, Pixel im Kreuzmittelpunkt gefärbt, Pixel in der Ecke leer, Pixel in der Mitte eines `x`-Dreiecks leer).
- [ ] **Step 2:** Fehlschlag prüfen, **Step 3:** Tokens, Stile, Icons und `StrokeIcon` implementieren. Schriften: Geist 1.8.1 (statische TTF) von https://github.com/vercel/geist-font, Teilmenge Basis-Latein, Latin-1, Latin Extended-A, `„“‚‘–—…€ẞ` mit Merkmalen `kern,liga,tnum,ccmp,locl,mark,mkmk`; der Befehl steht in `third-party/geist/README.md`. `Fonts.axaml` definiert `Pw.Font.Sans`/`Mono` einmal mit der eingebetteten Schrift, mit einer Zeile Wechsel auf die Systemschrift (Windows `Segoe UI`/`Cascadia Mono`, Linux `sans-serif`/`monospace`) für Task 13.
- [ ] **Step 4:** Tests grün; `dotnet format --verify-no-changes`.
- [ ] **Step 5: Commit** (`feat(app): Design-Tokens, Themes, Schrift und Strich-Icons`).

### Task 9: Hauptfenster (Gerüst ohne Öffnen-Logik)

**Files:**
- Create: `src/Pagewing.App/ViewModels/ViewModelBase.cs`, `RelayCommand.cs`, `MainViewModel.cs`; `Views/MainWindow.axaml(.cs)` (ersetzt das Platzhalter-Fenster), `TitleBar.axaml(.cs)`, `StatusBar.axaml(.cs)`, `StartScreen.axaml(.cs)`; `Controls/MiddleEllipsisTextBlock.cs`
- Test: `tests/Pagewing.App.Tests/MainWindowTests.cs`, `MiddleEllipsisTextBlockTests.cs`

**Interfaces:**
- Consumes: Tokens, Stile, `StrokeIcon`, `Strings` (Task 7, 8); `ViewerState` (Task 5).
- Produces:
  - `MainViewModel : ViewModelBase` mit `ViewerState State`, `string? FileName`, `string? FullPath`, `int PageCount`, `int CurrentPage` (1-basiert), `double Zoom`, `string ZoomText`, `bool IsWindowsTitleBar`; Befehle `ICommand OpenCommand, CloseCommand, ZoomInCommand, ZoomOutCommand, PreviousPageCommand, NextPageCommand` (hier nur Gerüst, belegt in Task 10/11). Der Konstruktor nimmt eine `ViewerSession`.
  - `MiddleEllipsisTextBlock : Control` – `Text`, `MaxWidth`; kürzt in der Mitte („Anfang…Endung“), sodass Anfang und Endung sichtbar bleiben; misst mit `FormattedText` per Bisektion.
  - Layout nach Designsystem Abschnitt 6, ohne Aktionsleiste und Panel: **Windows** `WindowDecorations="Full"` mit `ExtendClientAreaToDecorationsHint`, eine Titelleiste (40 px) mit App-Symbol, Tab, „Datei öffnen“, Zieh-Fläche (`WindowDecorationProperties.ElementRole="TitleBar"`, Tab und Knöpfe `User`); **Linux** Systemleiste plus Tab-Zeile (40 px) darunter. Dokumentbereich (`surface.canvas`, oben links `radius.overlay`), Statusleiste (32 px, `surface.window`): ‹ „Seite“ [Feld] „/ N“ ›, Trenner, Zoom − [Wert] +. Mindestgröße 640 × 480. Tab: Dateiname (`MiddleEllipsisTextBlock`, höchstens 280 px, Tooltip mit vollem Pfad), Symbol `file-text`, Schließen ×. Unter 760 px entfallen die Wörter „Seite“ in der Statusleiste (Symbole bleiben mit Tooltip); die Zieh-Fläche der Titelleiste bleibt mindestens 48 px breit (Tabs schrumpfen bis 120 px). Startbildschirm: Überschrift, Hinweis „Pagewing arbeitet offline …“, Primärknopf „Datei auswählen (Strg+O)“, Drag-&-Drop-Hinweis (Designsystem 7.1). Im Zustand `Empty` zeigt die Titelleiste keinen Tab. Jeder Knopf ohne Beschriftung hat Tooltip (mit Tastenkürzel) und `AutomationProperties.Name`.

- [ ] **Step 1: Tests schreiben (headless).** `MainWindowTests`: `OpensWithTheStartScreenAndNoTab`; `MinimumSizeIs640By480`; `TabShowsTheFileNameWithTheFullPathAsTooltip`; `StatusBarWordsDisappearBelow760Pixels` (Breite 700: Text „Seite“ nicht sichtbar, Seiten-Symbole und Feld sichtbar); `DragAreaOfTheTitleBarStaysAtLeast48PixelsWide` (Fenster 640 breit, beide Titelleisten-Varianten über `IsWindowsTitleBar` erzwungen); `EveryIconOnlyButtonHasATooltipAndAnAutomationName` (Durchlauf des visuellen Baums); `GermanAndEnglishUiShowDifferentStartTitles` (`Strings.Culture`). `MiddleEllipsisTextBlockTests`: `KeepsStartAndExtensionWhenShortened` (`"Sehr langer Dateiname für die Jahresabrechnung 2025 final v3.pdf"` bei 200 px → beginnt mit „Sehr“, endet mit „.pdf“, enthält „…“, Breite ≤ 200); `ShortTextIsUnchanged`; `NeverExceedsMaxWidthEvenForOneVeryLongWord`.
- [ ] **Step 2:** Fehlschlag prüfen, **Step 3:** implementieren, **Step 4:** grün.
- [ ] **Step 5:** `dotnet publish src/Pagewing.App -c Release -r win-x64 -o out/app` ohne AOT-/Trim-Warnungen; `out/app/Pagewing.exe` starten und die Leere-Fenster-Ansicht ansehen (hell und dunkel durch Umstellen des Windows-Themes, Deutsch und mit `--lang en`).
- [ ] **Step 6: Commit** (`feat(app): Hauptfenster mit Titelleiste, Statusleiste und Startbildschirm`).

**Roadmap:** „Hauptfenster nach Design-Spec, UI-Texte lokalisierbar“ erst nach Task 10 abhaken.

### Task 10: Öffnen (Argument, Dialog, Drop), Karten, Single-Instance verdrahten

**Files:**
- Create: `src/Pagewing.App/Services/IFilePicker.cs`, `AvaloniaFilePicker.cs`; `Views/PasswordCard.axaml(.cs)`, `ErrorCard.axaml(.cs)`
- Modify: `ViewModels/MainViewModel.cs`, `Views/MainWindow.axaml(.cs)` (Strg+O, Strg+W, Drop), `Program.cs`, `App.axaml.cs`, `StartupOptions.cs` (falls nötig)
- Test: `tests/Pagewing.App.Tests/OpenFlowTests.cs`
- Modify: `Docs/DECISIONS.md` (nur falls sich ein Ablauf von „Entscheidungen dieses Plans“ ändert)

**Interfaces:**
- Consumes: `ViewerSession` (Task 5), `SingleInstance` (Task 6), `Strings`, `MainViewModel` (Task 9).
- Produces:
  - `interface IFilePicker { Task<string?> PickPdfAsync(); }` (Dateifilter `*.pdf`, Eintrag „PDF-Dokumente“); der Konstruktor aus Task 9 wird zu `MainViewModel(ViewerSession session, IFilePicker picker)` erweitert (Tests aus Task 9 entsprechend anpassen).
  - `Task MainViewModel.OpenAsync(string path)`; `void MainViewModel.OnExternalOpen(IReadOnlyList<string> paths)` (beliebiger Thread, wechselt per `Dispatcher.UIThread.Post` auf den UI-Thread, öffnet den ersten Pfad, bringt das Fenster nach vorn; leere Liste = nur nach vorn); Passwort: `string Password`, `ICommand SubmitPasswordCommand`, `CancelPasswordCommand` (schließt das Dokument → `Empty`); Fehlertext je `PdfOpenError` aus `Strings`, aufklappbare technische Details (Pfad, Fehlerart).
  - Startreihenfolge in `Program.Main`: Optionen parsen → `SingleInstance.TryBecomePrimary(DefaultName)`; bei `null` die vollen Pfade per `TrySend(…, 3 s)` senden und mit Exit-Code 0 beenden (Avalonia wird nie geladen), schlägt das Senden fehl, als weitere Instanz ohne Sperre weiterlaufen → `ViewerSession` anlegen und bei Dateiargument **sofort** `OpenAsync` starten (parallel zur Avalonia-Initialisierung) → Fenster zeigen (erster Frame zeigt Tab mit Dateinamen und leeren Dokumentbereich) → nach dem ersten Frame `StartListening`.
  - Drag & Drop: nur Dateien (`StorageItem` Dateien) werden geöffnet; mehrere → die erste; Ordner und andere Inhalte werden ignoriert bzw. ergeben `CannotRead`.

- [ ] **Step 1: Tests schreiben (headless, Fake-`IFilePicker`).** `OpenCommandUsesThePickerAndOpensTheFile`; `CancelledPickerChangesNothing`; `StartWithAFileShowsTheTabBeforeTheDocumentIsReady` (State `Loading`, `FileName` sofort gesetzt); `PasswordCardAppearsAcceptsThePasswordAndKeepsFocus` (`encrypted_hello_world_r3.pdf`); `WrongPasswordShowsTheMessageAndKeepsTheField`; `CancelOnThePasswordCardClosesTheTab`; `ErrorCardTextMatchesEveryErrorKind` (Theory über alle `PdfOpenError`, beide Sprachen, Texte aus Designsystem 7.8); `ErrorCardOffersCloseTabAndOpenOther`; `DroppingAFileOpensIt`; `DroppingSeveralFilesOpensTheFirst`; `DroppingAFolderShowsACannotReadError`; `ExternalOpenReplacesTheDocument` (A offen, `OnExternalOpen([B])` von einem Pool-Thread → `Ready` mit B, ein Prozess; **Abnahme „weitere Datei im selben Prozess“**); `ExternalOpenWithoutPathsOnlyActivates`; `CtrlOWorksAndCtrlWClosesTheDocument`.
- [ ] **Step 2:** Fehlschlag prüfen, **Step 3:** implementieren, **Step 4:** grün.
- [ ] **Step 5: Manuell (AOT-Binary):** `out/app/Pagewing.exe` starten, aus einer zweiten Konsole `out/app/Pagewing.exe testdata/pdfs/hello_world.pdf` mit **relativem** Pfad und mit einem Pfad mit Leerzeichen aufrufen → die erste Instanz zeigt die Datei, der zweite Prozess ist sofort weg. Unter Linux zusätzlich `kill -9` der ersten Instanz und Neustart → startet normal.
- [ ] **Step 6: Commit** (`feat(app): Öffnen per Argument, Dialog und Drop, Passwort- und Fehlerkarte, Single-Instance`).

**Roadmap:** abhaken: „Hauptfenster nach Design-Spec …“, „Öffnen per Kommandozeilenargument, Dateidialog und Drag & Drop“, „Single-Instance …“. Abnahme „Weitere Datei bei laufender Instanz …“ erst nach Task 13 (Test `ExternalOpenReplacesTheDocument` + manuelle Prüfung).

### Task 11: Dokumentansicht, Scrollen, Zoom, Seitenfeld

**Files:**
- Create: `src/Pagewing.App/Controls/DocumentCanvas.cs`, `TileBitmaps.cs`; `Views/DocumentView.axaml(.cs)`
- Modify: `ViewModels/MainViewModel.cs`, `Views/MainWindow.axaml`, `Views/StatusBar.axaml`
- Test: `tests/Pagewing.App.Tests/DocumentViewTests.cs`

**Interfaces:**
- Consumes: `DocumentLayout`, `ViewportPlanner`, `TilePlanner`, `RenderScheduler`, `RenderCache`, `ViewerSession`, `ZoomSteps`.
- Produces:
  - `DocumentView` = `ScrollViewer` mit einem `DocumentCanvas` (Größe = `ContentWidth` × `ContentHeight`). `DocumentCanvas` zeichnet pro sichtbarer Seite: weiße Fläche mit Kante und Schatten (Designsystem 5.3, `page.border`, `page.shadow`), darunter Platzhalter (`surface.raised`, Kante `border.subtle`, Seitennummer klein in der Mitte, kein Spinner), darüber die Vorschau (gestreckt, mittlere Qualität), darüber die scharfen Kacheln (Rechteck = Kachel-Pixel ÷ Bildschirmskalierung + Seitenursprung). Bei jeder Änderung von Scroll-Offset, Viewport, Zoom oder Seitengrößen: `ViewportPlanner.Plan` → `scheduler.Update`. `TileReady` (Pool-Thread) → `Dispatcher.UIThread.Post`: nur wenn die Kachel noch gewünscht ist, wird aus dem Cache-Bild eine `WriteableBitmap` erzeugt (`Bgra8888`, opak, Zeilenweise kopiert wegen `Stride`) und `InvalidateVisual` ausgelöst (zusammengefasst, höchstens einmal pro Frame). `TileBitmaps` hält nur Bitmaps der gerade gewünschten Schlüssel und gibt andere frei.
  - `TileFailed` → die Seite bleibt leer mit dem Text `PagePlaceholderFailed`; kein erneuter Versuch.
  - Zoom: Strg+Mausrad (um den Zeiger: der Anker unter dem Zeiger bleibt), Strg+Plus/Strg+Minus und die Knöpfe der Statusleiste; Stufen und Grenzen aus `ZoomSteps`. Beim Nachladen von Seitengrößen (`session.Changed`) bleibt der Anker der obersten Seite stabil (`GetAnchor` vor, `GetOffset` nach `SetSizes`).
  - `MainViewModel.CurrentPage` folgt `GetMostVisiblePage`; Eingabe im Seitenfeld + Enter springt zur Seite (oberer Rand der Seite auf `Margin` Abstand), ungültige Eingabe setzt das Feld ohne Meldung auf die aktuelle Seite zurück; die Pfeile springen eine Seite weiter/zurück. Ein neues Dokument beginnt oben mit Zoom 100 %.
  - Test-Haken: `internal IReadOnlyList<RenderRequest> LastRequests` am `DocumentCanvas`.

- [ ] **Step 1: Tests schreiben (headless mit Skia, echte `ViewerSession`).** `ShowsPlaceholdersImmediatelyAndPageImagesAfterRendering` (nach dem Öffnen von `text-basic.pdf` erscheint im aufgenommenen Frame Seiteninhalt, `await` mit Zeitlimit 5 s); `ScrollingRequestsTheTilesOfTheVisiblePages` (`thousand-pages.pdf`, Offset auf Seite 500 → `LastRequests` enthält nur Seiten rund um 500); `FastScrollingThroughAThousandPagesNeverQueuesMoreThanTheCap` (100 Offsetwechsel hintereinander, `scheduler.PendingCount` bleibt unter 200); `CtrlWheelZoomsAroundThePointer`; `CtrlPlusAndCtrlMinusStepThroughTheZoomLevels` (1.0 → 1.25 → 1.0 → 0.75); `ZoomIsClampedTo10And800Percent`; `PageFieldJumpsToThePageAndResetsOnInvalidInput` (`"500"` springt, `"0"`, `"abc"`, `"1001"` setzen zurück); `CurrentPageFollowsTheScrollPosition`; `LoadingMorePageSizesKeepsTheTopPageInPlace`; `UnwantedTilesAreNotTurnedIntoBitmaps` (Kachel trifft nach dem Wegscrollen ein → nicht in `TileBitmaps`); `FailedTileShowsAnEmptyPageAndIsNotRetried`; `OpeningAnotherDocumentResetsScrollAndZoom`; `RenderScalingOf150PercentRendersAtTheDevicePixelSize` (Fensterskalierung 1.5 → ScaleKey in `LastRequests` passend, Kachel-Bild in Gerätepixeln).
- [ ] **Step 2:** Fehlschlag prüfen, **Step 3:** implementieren, **Step 4:** grün.
- [ ] **Step 5: Von Hand ansehen:** `dotnet run --project src/Pagewing.App -- testdata/pdfs/text-basic.pdf` und `thousand-pages.pdf`: Platzhalter → Vorschau → scharf, Scrollen mit Mausrad und Scrollleiste, Zoom hell und dunkel.
- [ ] **Step 6: Commit** (`feat(app): Dokumentansicht mit fortlaufendem Scrollen, Render-Anbindung und Zoom`).

**Roadmap:** abhaken: „Fortlaufendes Scrollen mit Virtualisierung, Render-Scheduler … und LRU-Cache …“, „Zoom (Strg+Mausrad, Strg+Plus/Minus)“.

### Task 12: Messschalter, Startzeit-Skript, CI

**Files:**
- Create: `src/Pagewing.App/Measurement/StartupMeasurement.cs`, `ScrollMeasurement.cs`; `bench/startup/startup.cs`, `bench/startup/README.md`; `tests/Pagewing.App.Tests/MeasurementTests.cs`
- Modify: `src/Pagewing.App/Program.cs`, `.github/workflows/ci.yml`, `CLAUDE.md` (Voraussetzungen: `xvfb` auf dem Linux-Runner; Windows-Runner in angemeldeter Desktop-Sitzung, falls der Dienst keine Fenster erzeugen kann), `Docs/DECISIONS.md` (Eintrag „Startzeitmessung: Definition und Grenzen (M3)“)

**Interfaces:**
- Produces:
  - App-Schalter (versteckt, nicht in der Hilfe): `--measure-startup <pdf>` öffnet die Datei, wartet auf das **erste scharfe Seitenbild der ersten Seite, nachdem es gezeichnet wurde** (`TopLevel.RequestAnimationFrame` nach dem Anwenden der Kachel), schreibt die Zeile `first-sharp` auf stdout und beendet mit Exit-Code 0. `--measure-second <a> <b>` öffnet a, wartet auf `first-sharp`, öffnet b über denselben Pfad wie eine externe Anfrage (`OnExternalOpen`) und schreibt `second-sharp <ms>` (Stopwatch vom Aufruf bis zum gezeichneten scharfen Bild von b). `--measure-scroll <pdf>` scrollt die Datei in 600 gleichen Schritten, einen pro Frame, von oben nach unten und schreibt `scroll frames=<n> p99-ms=<x> max-ms=<y>` (Abstände zwischen den Frames); kein Budget, nur ein Messwert. Bei Fehler: Exit-Code 1 mit `error: …` auf stderr, nach 30 s Zeitlimit Exit-Code 3.
  - `bench/startup/startup.cs` (Einzeldatei-Programm wie `tools/*.cs`): `-- --exe <pfad> --pdf <pdf> --case cold|warm|thousand|second [--second-pdf <pdf>] [--out <csv>]` und `-- selftest`. Pro Lauf misst es die Zeit **vom Prozessstart bis zur Zeile `first-sharp`** (bei `second`: den Wert aus `second-sharp`), 10 Läufe (`cold`: 5 s Pause zwischen den Läufen, Zeitwert ohne Datei-Cache-Garantie; `warm` und `thousand`: 3 Aufwärmläufe, 500 ms Pause), gibt Median, Min, Max aus und schreibt eine CSV (`case,run,ms`). Budgets: `cold` 400, `warm` 200, `second` 100, `thousand` 300 ms; **Exit-Code 1 ab Median > Budget × 1,25**, Meldung mit Fall, Median und Grenze. `selftest` prüft Median-Berechnung und Grenzentscheidung mit festen Werten.
  - CI (`ci.yml`, nach „Publish the app“): Schritt „Startup check“ ruft das Skript für `cold`, `warm`, `thousand` und `second` auf (Linux mit `xvfb-run -a`, Windows direkt), Schritt „Upload the startup measurements“ lädt `out/startup/*.csv` als Artefakt hoch (`if: always()`); im Schritt der Voraussetzungen prüft Linux zusätzlich `command -v xvfb-run`.

- [ ] **Step 1: Tests schreiben.** `MeasurementTests` (headless): `StartupMeasurementWritesFirstSharpOnceAndRequestsExit`; `MeasureSecondReportsAPositiveTime`; `MeasureStartupWithAMissingFileExitsWithOne`; `ScrollMeasurementReportsFramesAndPercentile` (kleine PDF, wenige Schritte). Zusätzlich `dotnet run bench/startup/startup.cs -- selftest`.
- [ ] **Step 2:** Fehlschlag prüfen, **Step 3:** implementieren; `Program.cs` verzweigt vor dem Fenster in die Messmodi.
- [ ] **Step 4:** Tests grün, `selftest` grün.
- [ ] **Step 5: Lokal messen:** `dotnet publish src/Pagewing.App -c Release -r win-x64 -o out/app`; prüfen, dass `pdfium.dll` neben `Pagewing.exe` liegt (sonst im `.csproj` die Kopie der nativen Assets sicherstellen und als Stolperfalle in `CLAUDE.md` notieren); dann `dotnet run bench/startup/startup.cs -- --exe out/app/Pagewing.exe --pdf testdata/pdfs/text-basic.pdf --case warm --out out/startup/warm.csv` und die übrigen Fälle. Werte notieren.
- [ ] **Step 6: CI-Schritte ergänzen, committen** (`ci: Startzeit-Check mit Messschaltern und bench/startup`), Branch pushen (`git push -u origin feature/m03-app-shell`), PR nach `dev` als Entwurf öffnen. Läuft der Startzeit-Check auf dem Windows-Runner (Dienst) nicht, weil keine Fenster erzeugt werden können, den **Projektinhaber** bitten, den Runner in einer angemeldeten Desktop-Sitzung zu betreiben (Anleitung in `CLAUDE.md` ergänzen); bis dahin bleibt die Abnahme „Startzeit-Check grün auf beiden Runnern“ **offen**.
- [ ] **Step 7:** Roadmap abhaken: „Schalter `--measure-startup`, Skript `bench/startup`, Startzeit-Check in der CI …“ (Nachweis: CI-Lauf-ID und Werte).

### Task 13: Messen, entscheiden, abnehmen, abschließen

**Files:**
- Modify: `src/Pagewing.App/Styles/Fonts.axaml` (je nach Ergebnis), `Docs/DECISIONS.md`, `CLAUDE.md` (Projektspezifisches: App-Architektur in einem Absatz, Messschalter, Runner-Voraussetzungen), `CHANGELOG.md` (`[Unreleased]` → Added), `Docs/superpowers/specs/2026-10-17-designsystem.md` (Abschnitt 9: erledigte Punkte mit Verweis auf `DECISIONS.md`), `Docs/roadmap.json` + `Docs/ROADMAP.md`

- [ ] **Step 1: Schrift messen.** Zwei AOT-Builds (Geist eingebettet / Systemschrift, Wechsel in `Fonts.axaml`), je `warm` und `cold` aus Task 12 mit 10 Läufen. Regel (eigene Entscheidung): Geist bleibt, wenn der Median des Warmstarts höchstens 15 ms über der Systemschrift liegt **und** alle Budgets eingehalten sind; sonst Systemschrift von Anfang an. Ergebnis als Eintrag „Schrift-Entscheidung nach Messung (M3)“ in `DECISIONS.md` (Werte, Rechner, Methode).
- [ ] **Step 2: Früher Öffnen-Start messen:** `Program.cs` einmal mit „Öffnen sofort“ (Plan) und einmal mit „Öffnen nach dem ersten Frame“, `warm`/`cold`; die schnellere Variante bleibt, Werte in `DECISIONS.md` (derselbe Eintrag).
- [ ] **Step 3: Kachelparameter bestätigen:** `--measure-scroll testdata/pdfs/thousand-pages.pdf` bei Zoom 100 % und auf einer Datei mit größeren Seiten (`vector-shapes.pdf`) sowie im Taskmanager die Speicherspitze; bei Auffälligkeiten (`p99-ms` über 20 ms, Speicher weit über 256 MiB) `WholePageMaxPixels`, Cache-Budget oder Puffer anpassen und begründen. Messwerte für die Abnahme notieren.
- [ ] **Step 4: Offene Punkte aus Designsystem 9 abarbeiten, soweit auf diesem Rechner möglich** (Windows 11): Snap Layouts beim Hover über Maximieren; Minimieren-/Maximieren-Animation bei `WindowDecorations="Full"`; Aussehen und Höhe der Fensterknöpfe (an 40 px Titelleiste anpassen); Sichtvergleich der Fluent-Palette mit dem Mockup in beiden Themes (Screenshots ansehen, Abweichungen korrigieren). Was nicht geprüft werden kann (Linux-Desktop, X11 mit/ohne Compositor, Standardschrift der Ziel-Distributionen, Mica), bleibt in Abschnitt 9 offen und in der Abschlussmeldung als **nicht geprüft** mit dem prüfenden Befehl benannt.
- [ ] **Step 5: Abnahme prüfen und abhaken (mit Nachweis):**
  - „Startzeit-Check in der CI grün auf beiden Runnern, Werte als Artefakt“ → CI-Lauf-ID des PR, Artefaktname.
  - „Weitere Datei bei laufender Instanz …“ → `ExternalOpenReplacesTheDocument` und `SingleInstanceTests.PathsArriveAtThePrimary`, dazu die manuelle Prüfung aus Task 10 Step 5.
  - „Schnelles Scrollen durch eine 1000-seitige Test-PDF …“ → Messwert aus `--measure-scroll` und manuelle Prüfung (Mausrad, Scrollleiste ziehen, Bild-ab-Taste gehalten).
  - Lokalisierung im AOT-Binary: `Pagewing.exe --lang de` und `--lang en` zeigen unterschiedliche Texte (aus Task 7 Step 5).
- [ ] **Step 6: Dokumentation:** `CHANGELOG.md`, `CLAUDE.md`, `DECISIONS.md` (jede neue Abhängigkeit und Entscheidung), Roadmap alle Aufgaben und Kriterien abhaken, Status `done` mit `completedOn`, `dotnet run tools/roadmap.cs -- generate`.
- [ ] **Step 7: Gesamtprüfung:** `dotnet build Pagewing.slnx`, `dotnet test Pagewing.slnx`, `dotnet format Pagewing.slnx --verify-no-changes`, `dotnet run tools/roadmap.cs -- check`, AOT-Publish von App und CLI ohne Warnungen; Ausgaben notieren. Einen Review der ganzen Branch anstoßen (`superpowers:requesting-code-review`) und Befunde einarbeiten.
- [ ] **Step 8: Commit, PR nach `dev` fertigstellen, Merge (Squash) erst wenn alle CI-Checks grün sind** (CLAUDE.md erlaubt das bei Feature-PRs).

---

## Selbstprüfung

- **Spec-Abdeckung:** Gesamt-Spec 5.2 (kein UI-Warten: Tasks 4, 11), 5.3 (Pipeline: Tasks 2 bis 4, 11), 5.4 (Single-Instance: 6, 10; Messung: 12), 5.8 (resx: 7); Designsystem 5.1 bis 5.5 (Tasks 8), 6 (Task 9), 7.1, 7.2 (Task 9, 10), 7.5 (Task 11), 7.8 (Task 10), 7.9 (Task 9), 9 (Task 13). Roadmap-Aufgaben: Hauptfenster/resx (7 bis 10), Öffnen (10), Single-Instance (6, 10), Scrollen/Scheduler/Cache (2 bis 5, 11), Zoom (11), Messschalter/CI (12), App.Tests (7). Abnahmekriterien: Task 13 Schritt 5.
- **Bewusst offen:** echter Kaltstart in der CI (nicht herstellbar), Linux-Desktop-Prüfung, Windows-Runner als Dienst (Task 12 Schritt 6), Snap Layouts auf Windows 10.
- **Typkonsistenz:** `RenderRequest`, `TileKey`, `ViewerSession`-Zustände und `DocumentLayout`-Methoden sind in Task 1 bis 5 definiert und in Task 9 bis 12 mit denselben Namen verwendet.
