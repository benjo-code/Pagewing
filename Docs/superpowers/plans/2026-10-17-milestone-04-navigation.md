# Milestone 4 – Navigation – Implementierungsplan

> **Für agentische Ausführer:** Pflicht-Skill: `superpowers:subagent-driven-development` (empfohlen) oder `superpowers:executing-plans`. Die Schritte nutzen Checkbox-Syntax (`- [ ]`).

**Ziel:** Mehrere PDFs lassen sich in Tabs eines Fensters öffnen (und per Menü in ein eigenes Fenster verschieben) und über Seitenvorschau, Inhaltsverzeichnis, Layouts (einzeln, fortlaufend, doppelseitig) und Zoom-Modi bequem navigieren, ohne die Startzeit-Budgets zu verletzen.

**Architektur:** Alles ohne UI bleibt in `Pagewing.Core` (Namespace `Pagewing.Core.Viewing` für Layout, Tab-Liste, Budgets, Vorschau-Planung; `Pagewing.Core.Pdf` für Lesezeichen und Dokumentinfos) und ist ohne Avalonia testbar. In `Pagewing.App` wird `MainViewModel` zum **Fenster**-ViewModel mit einer `TabList<TabViewModel>`; ein `TabViewModel` besitzt eine `ViewerSession` und den Ansichtszustand (Seite, Zoom, Zoom-Modus, Layout, Panel, Scroll-Anker). Ein `Workspace` kennt alle Fenster; ein Tab wechselt das Fenster samt Sitzung, ohne die PDF neu zu öffnen. Es gibt weiter genau eine `DocumentView` je Fenster, die beim Tab-Wechsel den Zustand des Tabs übernimmt (ihr `DataContext` ist der aktive Tab).

**Tech-Stack:** .NET 10 mit Native AOT, Avalonia 12.1.4 (kompilierte Bindungen), eigene Core-Typen statt MVVM-Bibliothek, `Avalonia.Headless.XUnit` und xunit.v3 (Microsoft.Testing.Platform), PDFium über eigene `[LibraryImport]`-Bindings.

**Spec:** [Gesamt-Spec](../specs/2026-10-16-pagewing-design.md) (Abschnitte 5.3, 5.4, Zeile M4 in 6) und [Designsystem](../specs/2026-10-17-designsystem.md) (Abschnitte 5 bis 7). Roadmap: [Milestone 4](../../ROADMAP.md). Vorgänger: [Plan M3](2026-10-17-milestone-03-app-shell.md).

## Scope dieses Plans (am 2026-10-17 mit dem Projektinhaber abgestimmt)

- **Enthalten:** Tabs (öffnen, schließen, wechseln, umsortieren per Ziehen innerhalb der Leiste), Menü „In neuem Fenster öffnen“ (verschiebt den Tab samt Sitzung), Aktionsleiste mit Panel „Seiten“ (Vorschaubilder) und „Inhalt“ (Lesezeichen), Layouts einzeln/fortlaufend/doppelseitig mit und ohne Deckblatt, Zoom-Modi Seitenbreite/ganze Seite/tatsächliche Größe/frei, „Gehe zu Seite“ (Strg+G), Dokumentinfos in der Statusleiste, gemeinsames Speicherbudget für Tabs, Messung von `GCConserveMemory` (Backlog „Arbeitsspeicher beim Scrollen“).
- **Bewusst nicht enthalten:** Herausziehen eines Tabs per Maus aus der Leiste und Zurückziehen in ein anderes Fenster (neuer **Milestone 4.1**, Voraussetzung M4; Scope-Änderung in der Roadmap); Panel „Suche“ (M5); Befehlspalette, Einstellungen, gemerkter Panelzustand (M6); Sitzung wiederherstellen (Backlog). Funktionen späterer Milestones erscheinen nicht ausgegraut.
- Der Aufbau (Tab besitzt seine Sitzung, `TabList<T>`, `Workspace`) ist so gewählt, dass M4.1 nur die Ziehgeste ergänzt.

## Global Constraints

- .NET 10 (`net10.0`), Native AOT Pflicht für App und CLI; `IsAotCompatible` je Projekt unter `src/`; AOT- und Trim-Warnungen sind Fehler; `TreatWarningsAsErrors`; keine Reflection-basierte Serialisierung, kein XAML zur Laufzeit; Avalonia 12.1.4.
- „PDFium ist nicht thread-sicher“: PDFium nur über den `PdfiumThread` (alle Fenster und Tabs teilen den einen); der UI-Thread wartet nie auf PDFium; kein paralleles Rendern. Neue PDFium-Konstanten gegen die Header des ausgelieferten Pakets prüfen (`NativeBindingsTests.ConstantsMatchThePackageHeaders` erweitern); `unsigned long` immer `CULong`.
- Budgets (endgültig, nie lockern): Kaltstart bis erste Seite scharf ≤ 400 ms · Warmstart ≤ 200 ms · weitere Datei in laufender Instanz ≤ 100 ms · 1000-seitige PDF ≤ 300 ms. Startzeit-Check: Median aus 10 Läufen, Fehlschlag ab Budget + 25 %. **Nichts Neues im Startpfad ohne Messung:** Aktionsleiste, Panel, Vorschaubilder und Lesezeichen entstehen erst nach `FirstSharpTileDrawn` bzw. erst beim Öffnen des Panels; Thumbnails, Lesezeichen und Dokumentinfos laufen mit `PdfPriority.Background`.
- Single-Instance: Ein zweiter Aufruf übergibt Dateien an die laufende Instanz; jede Datei öffnet als neuer Tab im zuletzt aktiven Fenster.
- Design: Tokens, Maße und Verhalten aus dem Designsystem (5 bis 7) wörtlich; Gelb nie Akzent; Zustände nie nur über Farbe (Rahmen, Form, Fettschrift, Häkchen); Tabler-Icons „outline“ über `StrokeIcon` (nur genutzte Pfade in `Styles/Icons.axaml`, Name im Tabler-Satz 3.49.0 vor dem Kopieren prüfen); Seitenschatten nie als `BoxShadow`; Mindestgröße 640 × 480; neue Farbpaare vor Verwendung auf WCAG 2.2 AA prüfen (keine neuen Farben geplant).
- UI-Texte Englisch und Deutsch aus resx (`Strings.cs` + beide resx, `LocalizationTests` prüft Vollständigkeit); Code und Kommentare Englisch; Doku und Commits Deutsch. Testnamen ohne Unterstriche (CA1707). Keine Netzwerkzugriffe.
- Nur M4-Umfang; Scope-Änderungen in `Docs/roadmap.json` unter `scopeChanges`, nicht stillschweigend.
- Git: Branch `feature/m04-navigation` (vorhanden). Gezielt stagen (`git add <datei>`), nach jedem Commit `git show --stat HEAD` gegen die erwartete Dateiliste prüfen, Conventional Commits auf Deutsch, Trailer `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`. Kein Force-Push, kein `--no-verify`. Ein PR nach `dev` am Ende; nach Task 9 und Task 14 den Branch pushen und die CI abwarten (frühe Rückmeldung der Startzeit-Checks).
- Roadmap: Status von M4 beim Start der Umsetzung `in-progress`; nach jedem Task die genannten Aufgaben abhaken (`"done": true`), `dotnet run tools/roadmap.cs -- generate`, beide Dateien mit committen. Abnahmekriterien erst abhaken, wenn geprüft, mit Nachweis.
- „Fertig“ heißt: `dotnet build Pagewing.slnx`, `dotnet test Pagewing.slnx`, `dotnet format Pagewing.slnx --verify-no-changes`, `dotnet run tools/roadmap.cs -- check` nachweislich grün, dazu der Startzeit-Check in der CI. Einzelne Tests: `dotnet test tests/Pagewing.Core.Tests --filter-class "*DocumentLayoutTests"` (Microsoft.Testing.Platform), analog `--filter-namespace`, `--filter-method`.
- Wo eine Avalonia-API in diesem Plan anders heißt als im Code von 12.1.4, gilt der Compiler (XML-Doku unter `~/.nuget/packages/avalonia/12.1.4/lib/net10.0/*.xml`); die Absicht des Schritts bleibt.

## Review Focus

Eingaben und Situationen, die die Specs nicht nennen, die im Alltag aber vorkommen. Die Prüfung steht jeweils im genannten Task.

1. **Tab schließen in jedem Zustand:** während die Datei lädt, bei der Passwortabfrage, mit Fehlerkarte, den aktiven, den letzten, den einzigen. Erwartung: Sitzung wird freigegeben, kein Ereignis danach, Nachbar wird aktiv, nach dem letzten Tab erscheint der Startbildschirm bzw. das Zweitfenster schließt (Tasks 2, 7, 10).
2. **Viele und doppelte Dateien:** dieselbe Datei zweimal öffnen (aktiviert den vorhandenen Tab), 200 Dateien auf einmal ablegen (höchstens 20 Tabs je Vorgang), Ordner oder 0-Byte-Datei im Mix (Fehlerkarte im eigenen Tab) (Task 7).
3. **Gemischte Seitenformate in Doppelseiten- und Fit-Modi:** Hoch- und Querformat im selben Spread, ungerade Seitenzahl, Dokument mit einer Seite im Deckblatt-Modus, Seiten ohne bekannte Größe. Erwartung: keine Ausnahme, kein Überlappen, Fit-Zoom innerhalb 10 % bis 800 % (Tasks 1, 11, 12).
4. **Kaputte Lesezeichen:** Zyklus (`/Next` zeigt zurück), 40 Ebenen tief, 50 000 Einträge, Ziel auf nicht vorhandene Seite, ohne Ziel, Titel leer oder mit Steuerzeichen. Erwartung: begrenzte Zeit und Größe, kein Absturz, Eintrag ohne Ziel ist nicht klickbar (Task 4, 15).
5. **Schneller Wechsel und Größenänderung:** mit Strg+Tab durch ladende Tabs wechseln, Fenstergröße ändern, während ein anderer Tab aktiv ist, Fit-Modus nach Größenänderung, Zoomen während Vorschaubilder rendern. Erwartung: jeder Tab behält Seite, Zoom und Layout; keine Aufträge für nicht angezeigte Tabs; Speicher bleibt unter dem Budget (Tasks 3, 9, 11, 14).

Außerdem durchgängig: 640 px Fensterbreite mit vielen Tabs (Task 8), Bildschirmskalierung 125 %/150 % bei Vorschaubildern (Task 14), Systemsprache ohne Übersetzung → Englisch (alle UI-Tasks).

## Entscheidungen dieses Plans (eigene Entscheidungen sind markiert; jede bekommt einen Eintrag in `Docs/DECISIONS.md`, Task 17)

- **Tab-Besitz:** Ein `TabViewModel` besitzt seine `ViewerSession` (und gibt sie beim Schließen frei); das Fenster-ViewModel hält nur die `TabList`. Dadurch kann ein Tab ohne erneutes Öffnen das Fenster wechseln. *Eigene Entscheidung.*
- **Eine `DocumentView` je Fenster**, die beim Tab-Wechsel ihren Zustand wechselt (kein versteckter Baum je Tab): ein Tab ohne Ansicht kostet nur das offene Dokument, den kleinen Cache und den gespeicherten Zustand. *Eigene Entscheidung.*
- **Speicherbudget:** aktiver Tab 256 MiB (`RenderCache.DefaultBudgetBytes`), jeder inaktive Tab 16 MiB (`TabBudget.InactiveBytes`); Vorschaubilder eines Tabs höchstens 48 MiB (`ThumbnailSource.BudgetBytes`). Beim Deaktivieren wird der Cache sofort gekürzt. *Eigene Entscheidung.*
- **Dieselbe Datei zweimal:** aktiviert den vorhandenen Tab des Fensters (Vergleich über den vollen Pfad, ordinal; unter Windows ohne Beachtung der Groß-/Kleinschreibung). *Eigene Entscheidung.*
- **Mehrere Dateien auf einmal** (Drop, Aufruf, Übergabe): höchstens 20 Tabs je Vorgang (`MainViewModel.MaxFilesPerOpen`), die letzte geöffnete wird aktiv. *Eigene Entscheidung.*
- **Fehlerkarte „Andere Datei öffnen“** ersetzt das Dokument im selben Tab; jeder andere Öffnen-Weg öffnet einen neuen Tab.
- **Schließen des letzten Tabs:** im einzigen Fenster erscheint der Startbildschirm; in einem weiteren Fenster schließt das Fenster. *Eigene Entscheidung.*
- **Layouts:** `PageLayoutMode` = `Continuous`, `Single`, `TwoPages`, `TwoPagesWithCover`; „doppelseitig“ ist fortlaufend (Zeilen aus zwei Seiten, oben bündig). Das Deckblatt steht allein rechts der Mittelachse, eine einzelne letzte Seite links. `SpreadGap` = 8 DIP, nicht zoomabhängig. In den Doppelseiten-Modi bewegen Bild auf/ab, Pfeile der Statusleiste und die Vorschau um eine Zeile; `CurrentPage` ist die erste Seite der am stärksten sichtbaren Zeile. *Eigene Entscheidung.*
- **Einzelseite:** zeigt nur die gewählte Seite (Inhalt = Seite + Ränder). Weiter-/Zurückblättern: Bild auf/ab, Pfeile der Statusleiste, Vorschau, Gehe zu Seite, Leertaste am Seitenende, und ein weiterer Mausradschritt in Richtung eines Randes, an dem schon ganz gescrollt ist. *Eigene Entscheidung.*
- **Zoom-Modi:** `Free`, `FitWidth`, `FitPage`, `ActualSize` (= 100 %, 96 dpi wie bisher). Fit bezieht sich auf die Zeile der **aktuellen Seite** und wird neu berechnet bei: Moduswahl, Layoutwechsel, Größenänderung des Viewports, Sprung zu einer Seite (nicht beim Scrollen). Stufen, Strg+Mausrad und Strg+Plus/Minus wechseln zu `Free`. Tastenkürzel Strg+0 ganze Seite, Strg+1 tatsächliche Größe, Strg+2 Seitenbreite, Strg+G Gehe zu Seite. *Eigene Entscheidung.*
- **Layout-Wahl** sitzt als eigener Knopf in der Statusleiste (Menü mit vier Einträgen, aktueller mit Häkchen), der Zoomwert öffnet das Menü mit Stufen und Zoom-Modi (Designsystem 7.5). Die Design-Spec wird in Task 17 nachgezogen. *Eigene Entscheidung.*
- **Panel:** Aktionsleiste (52 px) und Panel gelten ab einem aktiven Tab; Panel-Auswahl je Tab, Breite je Fenster (Standard 264, 200 bis 400, Doppelklick auf die Kante setzt zurück); unter 900 px liegt das Panel über dem Dokument. Beim ersten scharfen Bild eines Fensters öffnet „Seiten“, wenn das Fenster ≥ 900 px breit ist (Designsystem 7.3; das Merken kommt in M6). *Eigene Entscheidung für den Zeitpunkt.*
- **Lesezeichen:** höchstens 20 000 Einträge und 32 Ebenen (`PdfDocument.MaxOutlineItems`, `MaxOutlineDepth`), Zyklen über besuchte Handles abgefangen; geladen erst beim ersten Öffnen von „Inhalt“. *Eigene Entscheidung.*
- **Seitenformat-Namen** in den Dokumentinfos: A3 bis A6, Letter, Legal (Toleranz 1,5 mm, Hoch- oder Querformat), sonst „B × H mm“. *Eigene Entscheidung.*

## Dateistruktur

**Core (`src/Pagewing.Core`)**
- Create `Viewing/PageLayoutMode.cs` (`PageLayoutMode`, `ZoomMode`) · `Viewing/ViewState.cs` · `Viewing/TabList.cs` · `Viewing/TabStripLayout.cs` · `Viewing/TabBudget.cs` · `Viewing/ThumbnailPlanner.cs` · `Viewing/ThumbnailSource.cs`
- Create `Pdf/PdfOutlineItem.cs` · `Pdf/DocumentInfo.cs` (mit `DocumentInfoReader`) · `Pdf/PdfDate.cs` · `Pdf/PageFormat.cs`
- Modify `Viewing/DocumentLayout.cs` (Zeilen, Modi, `FitZoom`) · `Viewing/RenderCache.cs` (`SetBudget`) · `Viewing/ViewerSession.cs` (`SetActive`, `EnsureThumbnails`) · `Pdf/PdfDocument.cs` (`GetOutlineAsync`)

**Pdfium (`src/Pagewing.Pdfium`)**
- Modify `Native/Fpdf.cs`, `Native/FpdfConstants.cs`, `PdfiumDocument.cs` (Lesezeichen lesen); Create `PdfiumOutlineNode.cs`

**App (`src/Pagewing.App`)**
- Create `ViewModels/TabViewModel.cs` · `ViewModels/OutlineNodeViewModel.cs` · `ViewModels/DocumentInfoViewModel.cs` · `Services/Workspace.cs`
- Create `Controls/TabStripPanel.cs` · `Controls/ThumbnailList.cs` (ListBox mit Vorschaubildern) · `Views/TabStrip.axaml(.cs)` · `Views/ActionBar.axaml(.cs)` · `Views/SidePanel.axaml(.cs)` · `Views/PagesPanel.axaml(.cs)` · `Views/OutlinePanel.axaml(.cs)` · `Views/DocumentInfoPopup.axaml(.cs)`
- Modify `ViewModels/MainViewModel.cs` (Fenster-VM) · `Views/MainWindow.axaml(.cs)` · `Views/TitleBar.axaml(.cs)` · `Views/StatusBar.axaml(.cs)` · `Views/DocumentView.axaml(.cs)` · `Views/ErrorCard.axaml` · `Views/PasswordCard.axaml(.cs)` · `Views/StartScreen.axaml` · `App.axaml.cs` · `Measurement/*.cs` · `Styles/Icons.axaml` · `Resources/Strings.cs`, `Strings.en.resx`, `Strings.de.resx`

**Tests**
- `tests/Pagewing.Core.Tests/Viewing/` (`DocumentLayoutTests`, `ViewportPlannerTests`, neu `TabListTests`, `TabStripLayoutTests`, `ThumbnailPlannerTests`, `ViewerSessionTests`, `RenderCacheTests`) · `Pdf/` (neu `OutlineTests`, `DocumentInfoTests`, `PdfDateTests`, `PageFormatTests`) · `Pdfium/NativeBindingsTests.cs`
- `tests/Pagewing.App.Tests/` (angepasst: `OpenFlowTests`, `MainWindowTests`, `DocumentViewTests`, `MeasurementTests`, `HeadlessSmokeTests`; neu `TabsTests`, `TabStripTests`, `WorkspaceTests`, `ZoomModeTests`, `LayoutModeTests`, `PanelTests`, `PagesPanelTests`, `OutlinePanelTests`, `DocumentInfoTests`)
- `tools/make-testpdfs.cs` (+ `testdata/pdfs/outline.pdf`, `outline-cycle.pdf`, `LICENSES.md`)

---

### Task 1: Layout- und Zoom-Modi in `DocumentLayout`

**Files:**
- Create: `src/Pagewing.Core/Viewing/PageLayoutMode.cs`
- Modify: `src/Pagewing.Core/Viewing/DocumentLayout.cs`
- Test: `tests/Pagewing.Core.Tests/Viewing/DocumentLayoutTests.cs`, `ViewportPlannerTests.cs`

**Interfaces:**
- Produces:
  - `enum PageLayoutMode { Continuous, Single, TwoPages, TwoPagesWithCover }`, `enum ZoomMode { Free, FitWidth, FitPage, ActualSize }` (beide in `PageLayoutMode.cs`).
  - `DocumentLayout`: `const double SpreadGap = 8;` `PageLayoutMode Mode { get; set; }` (Standard `Continuous`); `int RowCount`; `int RowOf(int page)`; `(int First, int Last) PagesOfRow(int row)`; `void ShowPage(int page)` (nur in `Single` wirksam: nur die Zeile dieser Seite ist vorhanden; Standard Seite 0); `bool IsShown(int page)`; `double FitZoom(ZoomMode mode, int page, double viewportWidth, double viewportHeight)`.
  - Verhalten bestehender Member: `GetPageRect(page)` wirft `ArgumentOutOfRangeException` für nicht gezeigte Seiten (nur `Single`); `GetVisiblePages`, `GetMostVisiblePage`, `GetAnchor`, `GetOffset`, `ContentWidth/Height` arbeiten über die gezeigten Zeilen. Im Modus `Continuous` bleibt jedes Ergebnis wie bisher.
- Consumes: nichts.

Regeln (Absicht, Körper schreibt der Ausführer): Zeile von Seite p: `Continuous`/`Single` p; `TwoPages` p/2; `TwoPagesWithCover` (p+1)/2. Eine Zeile ist so hoch wie ihre höchste Seite; Seiten stehen oben bündig. Zwei Seiten: linke endet bei Achse − `SpreadGap`/2, rechte beginnt bei Achse + `SpreadGap`/2. Einzelne Seite: `Continuous`/`Single` zentriert; Deckblatt (Zeile 0 im Deckblatt-Modus, auch bei nur einer Seite) rechts der Achse; einzelne letzte Seite im `TwoPages`-Modus links. `ContentWidth` = 2 × Margin + 2 × größte Halbbreite (Abstand der äußeren Kante von der Achse) über die gezeigten Zeilen; die Achse liegt bei `ContentWidth / 2`. Zeilenoberkanten sind Präfixsummen (Margin, `PageGap` wie bisher). **Binärsuche über Zeilen-Unterkanten (monoton), nicht über die Unterkante der einzelnen Seite:** eine Seite, die kürzer ist als ihre Zeile, kann als sichtbar gelten, obwohl nur Leere unter ihr liegt; das ist gewollt. `FitZoom`: Zeile der Seite `page`; `s` = DIP je Punkt; Halbbreite(s) = `SpreadGap`/2 + max(w) × s für Zeilen mit zwei Seiten oder Deckblatt/letzte Einzelseite, sonst w × s / 2; `FitWidth`: 2 × Halbbreite(s) = viewportWidth − 2 × Margin; `FitPage`: Minimum aus `FitWidth` und (viewportHeight − 2 × Margin) / (höchste Seite der Zeile in Punkten); `ActualSize` 1; `Free` aktueller `Zoom`; Ergebnis = s / `DipsPerPoint`, mit `ZoomSteps.Clamp` begrenzt; ist der Viewport kleiner als die Ränder: `ZoomSteps.Min`. NaN oder ungültige Seite wirft nicht, sondern gibt den aktuellen `Zoom` zurück.

- [ ] **Step 1: Failing tests** in `DocumentLayoutTests` (alle Namen ohne Unterstrich), mit Letter 612 × 792 und einer Querformat-Seite 792 × 612:
  - `TwoPagesPlacesSpreadsSideBySideOnTheCentreAxis`: 4 Seiten; Seite 0 endet bei `ContentWidth/2 − 4`, Seite 1 beginnt bei `ContentWidth/2 + 4`, beide `Y` gleich; `ContentHeight` = 2 Zeilen + 1 `PageGap` + 2 Margin.
  - `CoverPageStandsAloneOnTheRightHalf`: `TwoPagesWithCover`, 5 Seiten: Seite 0 beginnt bei Achse + 4; Seiten 1/2 bilden Zeile 1; `RowOf(4) == 2`.
  - `CoverModeWithOnePageStaysOnTheRight`, `LastOddPageStandsAloneOnTheLeftHalf` (`TwoPages`, 3 Seiten).
  - `RowHeightIsTheTallerPageAndPagesAreTopAligned` (Hoch- neben Querformat).
  - `ContinuousModeIsUnchanged`: die vorhandenen Tests bleiben grün (kein Test geändert).
  - `SingleModeShowsOnlyTheChosenPage`: `ShowPage(2)`: `IsShown(1) == false`, `ContentHeight` = Seite + 2 × Margin, `GetVisiblePages(0, 99999)` = `(2, 2)`, `GetPageRect(1)` wirft.
  - `VisiblePagesIncludeBothPagesOfARow`, `MostVisiblePageInATwoPageRowIsTheFirstOnATie`.
  - `AnchorRoundTripsInTwoPageMode` (`GetOffset(GetAnchor(y)) ≈ y`).
  - `FitWidthFillsTheViewportMinusMargins` (Continuous: 1000 DIP breit → `(1000 − 64) / (612 × DipsPerPoint)`), `FitWidthInCoverModeUsesTheFullSpreadWidth`, `FitPageUsesTheSmallerRatio`, `ActualSizeIsOne`, `FitIsClampedToTheZoomRange` (Viewport 10 px breit → `ZoomSteps.Min`; 100000 px → `ZoomSteps.Max`), `FitZoomOfAnInvalidPageReturnsTheCurrentZoom`.
  In `ViewportPlannerTests`: `TwoPageRowsGetPreviewsAndTilesForBothPages`.
- [ ] **Step 2:** `dotnet test tests/Pagewing.Core.Tests --filter-class "*DocumentLayoutTests"` – erwartet: Build-Fehler (Typen fehlen).
- [ ] **Step 3:** Implementieren wie oben; `ViewportPlanner` bleibt unverändert, wenn die Tests grün sind (er nutzt nur `GetVisiblePages`/`GetPageRect`).
- [ ] **Step 4:** `dotnet test tests/Pagewing.Core.Tests --filter-namespace "*Viewing*"` – erwartet: alle grün.
- [ ] **Step 5: Commit** `feat(core): Layout-Modi und Zoom-Modi im DocumentLayout`.

**Roadmap:** noch nichts abhaken (Layouts/Zoom-Modi sind erst mit der UI fertig).

---

### Task 2: `TabList<T>` und `TabStripLayout`

**Files:**
- Create: `src/Pagewing.Core/Viewing/TabList.cs`, `src/Pagewing.Core/Viewing/TabStripLayout.cs`
- Test: `tests/Pagewing.Core.Tests/Viewing/TabListTests.cs`, `TabStripLayoutTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class TabList<T> : IReadOnlyList<T>, INotifyCollectionChanged where T : class` – nur UI-Thread, nicht thread-sicher. Member: `T? Active { get; }`; `event Action? ActiveChanged`; `void Add(T item)` (rechts vom aktiven Tab, am Ende ohne aktiven; wird aktiv); `T? Close(T item)` (entfernt; wirkt das auf den aktiven Tab, wird der rechte Nachbar aktiv, sonst der linke, sonst `null`; gibt den neuen aktiven Tab zurück; unbekanntes Element ändert nichts); `bool Activate(T item)`; `void Move(T item, int newIndex)` (begrenzt auf gültigen Bereich; aktiver Tab bleibt); `void ActivateNext()`/`ActivatePrevious()` (zyklisch); `int IndexOf(T item)`. `CollectionChanged` meldet `Add`, `Remove`, `Move` (nie `Reset`).
  - `public static class TabStripLayout`: Konstanten `MinTabWidth = 120`, `MaxTabWidth = 280`, `MinDragArea = 48`, `AllTabsButtonWidth = 50`; `static (double TabWidth, bool ShowOnlyActive) Compute(int tabCount, double availableWidth)`; `availableWidth` ist die Breite für Tabs und Zieh-Fläche zusammen (Fenster minus App-Symbol 52, „Datei öffnen“ 36, Fensterknöpfe 140 unter Windows).

- [ ] **Step 1: Failing tests**, `TabListTests`: `AddInsertsRightOfTheActiveTabAndActivatesIt`, `AddWithoutActiveTabAppends`, `ClosingTheActiveTabActivatesTheRightNeighbour`, `ClosingTheLastTabInTheListActivatesTheLeftNeighbour`, `ClosingAnInactiveTabKeepsTheActiveOne`, `ClosingTheOnlyTabLeavesNoActiveTab`, `ClosingAnUnknownItemChangesNothing`, `MoveReordersAndKeepsTheActiveTab`, `MoveClampsTheIndex`, `ActivateNextWrapsAround`, `ActivePreviousWrapsAround`, `ChangesRaiseCollectionChangedWithoutReset`, `ActiveChangedIsRaisedOnlyOnChange`. `TabStripLayoutTests`: `FewTabsGetTheMaximumWidth` (2 Tabs, 700 px → 280), `TabsShrinkEquallyDownToTheMinimum` (3 Tabs, 412 px → (412 − 48)/3 ≈ 121,3), `TooManyTabsShowOnlyTheActiveOne` (4 Tabs, 412 px → `ShowOnlyActive`, Breite 280 = Clamp(412 − 48 − 50)), `DragAreaIsNeverBelowItsMinimum` (für Breiten von 0 bis 2000 und 1 bis 50 Tabs: `Tabs × Breite + MinDragArea (+ AllTabsButton)` ≤ `availableWidth` oder Breite = `MinTabWidth` im Überlauf), `ZeroOrNegativeWidthDoesNotThrow`.
- [ ] **Step 2:** Test ausführen – erwartet Build-Fehler. **Step 3:** Implementieren. **Step 4:** `--filter-class "*TabListTests"` und `"*TabStripLayoutTests"` grün.
- [ ] **Step 5: Commit** `feat(core): TabList und Tableisten-Berechnung`.

---

### Task 3: Speicherbudget je Tab

**Files:**
- Create: `src/Pagewing.Core/Viewing/TabBudget.cs`
- Modify: `src/Pagewing.Core/Viewing/RenderCache.cs`, `src/Pagewing.Core/Viewing/ViewerSession.cs`
- Test: `RenderCacheTests.cs`, `ViewerSessionTests.cs`

**Interfaces:**
- Produces: `static class TabBudget { const long ActiveBytes = RenderCache.DefaultBudgetBytes; const long InactiveBytes = 16L * 1024 * 1024; }`; `RenderCache.BudgetBytes` bleibt lesbar, ist aber veränderbar über `void SetBudget(long budgetBytes)` (thread-sicher, `ArgumentOutOfRangeException` unter 1; evictet sofort bis zum neuen Budget); `ViewerSession.SetActive(bool active)` (Standard aktiv; setzt das Budget des aktuellen und jedes später angelegten Caches auf `ActiveBytes` bzw. `InactiveBytes`; wirft nicht nach `Dispose`).
- Der Scheduler liest `cache.BudgetBytes` schon bei jedem Ergebnis; es ist kein Eingriff nötig.

- [ ] **Step 1: Failing tests:** `SetBudgetEvictsLeastRecentlyUsedEntriesAtOnce`, `SetBudgetToALargerValueKeepsEverything`, `SetBudgetBelowOneThrows`; in `ViewerSessionTests`: `InactiveSessionKeepsAtMostTheInactiveBudget` (öffnen, Cache mit Bildern über 16 MiB füllen, `SetActive(false)` → `Cache.UsedBytes ≤ InactiveBytes`), `ActivatingRestoresTheFullBudget`, `CacheOfADocumentOpenedWhileInactiveStartsSmall`, `SetActiveAfterDisposeDoesNothing`.
- [ ] **Step 2–4:** fehlschlagen lassen, implementieren, `--filter-namespace "*Viewing*"` grün. **Step 5: Commit** `feat(core): Speicherbudget für aktive und inaktive Tabs`.

---

### Task 4: Lesezeichen (Inhaltsverzeichnis) lesen

**Files:**
- Modify: `src/Pagewing.Pdfium/Native/Fpdf.cs`, `FpdfConstants.cs`, `PdfiumDocument.cs`; `src/Pagewing.Core/Pdf/PdfDocument.cs`; `tools/make-testpdfs.cs`; `testdata/pdfs/LICENSES.md`
- Create: `src/Pagewing.Pdfium/PdfiumOutlineNode.cs`, `src/Pagewing.Core/Pdf/PdfOutlineItem.cs`; `testdata/pdfs/outline.pdf`, `outline-cycle.pdf` (erzeugt)
- Test: `tests/Pagewing.Core.Tests/Pdf/OutlineTests.cs`, `Pdfium/NativeBindingsTests.cs`

**Interfaces:**
- Produces:
  - Bindings (`fpdf_doc.h`, alle `[LibraryImport]` mit `CallConvCdecl`): `nint FPDFBookmark_GetFirstChild(nint document, nint bookmark)`, `nint FPDFBookmark_GetNextSibling(nint document, nint bookmark)`, `CULong FPDFBookmark_GetTitle(nint bookmark, void* buffer, CULong buflen)`, `int FPDFBookmark_GetCount(nint bookmark)`, `nint FPDFBookmark_GetDest(nint document, nint bookmark)`, `nint FPDFBookmark_GetAction(nint bookmark)`, `CULong FPDFAction_GetType(nint action)`, `nint FPDFAction_GetDest(nint document, nint action)`, `int FPDFDest_GetDestPageIndex(nint document, nint dest)`; Konstante für den Aktionstyp „GoTo“ aus dem Header (`PDFACTION_GOTO`) in `FpdfConstants` und im Test `ConstantsMatchThePackageHeaders` geprüft.
  - `internal sealed record PdfiumOutlineNode(string Title, int PageIndex, bool IsOpen, IReadOnlyList<PdfiumOutlineNode> Children)` (`PageIndex` −1 ohne Ziel); `PdfiumDocument.ReadOutline(int maxItems, int maxDepth)`.
  - `public sealed record PdfOutlineItem(string Title, int? PageIndex, bool IsOpen, IReadOnlyList<PdfOutlineItem> Children)`; `PdfDocument.MaxOutlineItems = 20_000`, `MaxOutlineDepth = 32`; `Task<IReadOnlyList<PdfOutlineItem>> PdfDocument.GetOutlineAsync(CancellationToken cancellationToken = default)` (Priorität `Background`).
- Regeln: Titel UTF-16LE wie `GetMetaText`; Steuerzeichen (< U+0020) werden zu Leerzeichen, leere Titel bleiben leer (die UI zeigt „(ohne Titel)“); Ziel zuerst `FPDFBookmark_GetDest`, sonst Aktion vom Typ GoTo; Seitenindex außerhalb `0..PageCount−1` oder −1 → `PageIndex = null`; `IsOpen` = `FPDFBookmark_GetCount > 0`; Zyklen: besuchte Lesezeichen-Handles (`HashSet<nint>`) – ein bekanntes Handle beendet die Geschwisterkette bzw. den Ast; Obergrenzen schneiden still ab (kein Fehler).

- [ ] **Step 1: Testdateien.** In `tools/make-testpdfs.cs` `Outline()` und `OutlineCycle()` ergänzen (deterministisch, unkomprimiert, 3 Seiten Letter): `outline.pdf` mit „Kapitel 1“ → Seite 1 (offen, Kinder „Abschnitt 1.1“ → Seite 2, „Abschnitt 1.2“ ohne Ziel, Titel „Über uns – 日本“ als UTF-16BE-Textstring), „Kapitel 2“ → Seite 3 (zu, ein Kind), „Fern“ → Ziel Seite 99 (ungültig); `outline-cycle.pdf` mit drei Geschwistern, deren letztes `/Next` auf das erste zeigt. `dotnet run tools/make-testpdfs.cs -- generate`, danach `-- check`. Herkunft/Lizenz (selbst erzeugt, wie die übrigen selbst erzeugten Dateien) in `LICENSES.md`; falls `TestDataTests` oder der `RenderCorpus` jede PDF im Ordner verlangen, nachziehen (Referenzbilder nur mit Sichtprüfung: `PAGEWING_UPDATE_REFERENCES=1 dotnet test tests/Pagewing.Render.Tests`).
- [ ] **Step 2: Failing tests** `OutlineTests`: `OutlineHasTheExpectedTree` (Titel, Ebenen, `PageIndex` 0/1/null/2, `IsOpen`), `UnicodeTitlesSurvive`, `InvalidPageTargetBecomesNull`, `CyclicOutlineEndsAndReturnsEachItemOnce`, `DocumentWithoutOutlineGivesAnEmptyList` (`hello_world.pdf`), `OutlineOfADisposedDocumentThrows`, `OutlineIsCancellable`; für die Grenzen ein synthetischer Test mit gekürztem Limit (interner Parameter `maxItems`/`maxDepth` von `PdfiumDocument.ReadOutline`): `OutlineStopsAtTheItemLimit`, `OutlineStopsAtTheDepthLimit`. `ConstantsMatchThePackageHeaders` um `PDFACTION_GOTO` erweitern.
- [ ] **Step 3:** Tests fehlschlagen lassen. **Step 4:** Bindings und Leser implementieren; `GetOutlineAsync` wie `GetMetadataAsync` über `document.Thread.InvokeAsync(..., PdfiumPriority.Background, ct)`.
- [ ] **Step 5:** `dotnet test tests/Pagewing.Core.Tests --filter-namespace "*Pdf*"` grün. **Step 6: Commit** `feat(core): Lesezeichen aus dem PDF lesen`.

---

### Task 5: Dokumentinfos

**Files:**
- Create: `src/Pagewing.Core/Pdf/DocumentInfo.cs`, `PdfDate.cs`, `PageFormat.cs`
- Test: `tests/Pagewing.Core.Tests/Pdf/DocumentInfoTests.cs`, `PdfDateTests.cs`, `PageFormatTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record DocumentInfo(string FileName, string Folder, int PageCount, string PdfVersion, long FileSizeBytes, string? Title, string? Author, DateTimeOffset? Created)`; `public static class DocumentInfoReader { public static Task<DocumentInfo> ReadAsync(PdfDocument document, string fullPath, CancellationToken cancellationToken = default); }` (Metadaten über `GetMetadataAsync`, Dateigröße über `FileInfo`; eine nicht lesbare Datei gibt Größe 0, wirft nicht).
  - `public static class PdfDate { public static DateTimeOffset? TryParse(string? value); }` – Format `D:YYYY[MM[DD[HH[mm[SS]]]]]` mit `Z`, `+HH'mm'`, `-HH'mm'` oder ohne Zone (dann Ortszeit); ungültig → `null`.
  - `public static class PageFormat { public static string Describe(PdfPageSize size, CultureInfo culture); }` → `"A4 · 210 × 297 mm"`, `"Letter · 216 × 279 mm"`, sonst `"200 × 300 mm"`; Hoch- und Querformat gleich benannt; Millimeter auf ganze Zahlen gerundet, Zahlenformat nach `culture`; eine ungültige Größe gibt `""`.
- [ ] **Step 1: Failing tests:** `PdfDateTests` (`ParsesFullDateWithOffset`, `ParsesDateOnly`, `ParsesUtcSuffix`, `GarbageGivesNull`, `NullGivesNull`, `MonthThirteenGivesNull`); `PageFormatTests` (`A4PortraitAndLandscape`, `LetterAndLegal`, `UnknownSizeIsGivenInMillimetres`, `ToleranceIsOnePointFiveMillimetres`, `InvalidSizeGivesAnEmptyString`); `DocumentInfoTests` (`InfoOfHelloWorld` mit Dateiname, Ordner, Seitenzahl 1, Version, Größe = `FileInfo.Length`; `MissingMetadataGivesNulls`).
- [ ] **Step 2–4:** fehlschlagen lassen, implementieren, `--filter-namespace "*Pdf*"` grün. **Step 5: Commit** `feat(core): Dokumentinfos, PDF-Datum und Seitenformat`.

---

### Task 6: Planung der Seitenvorschau

**Files:**
- Create: `src/Pagewing.Core/Viewing/ThumbnailPlanner.cs`, `ThumbnailSource.cs`
- Modify: `src/Pagewing.Core/Viewing/ViewerSession.cs`
- Test: `ThumbnailPlannerTests.cs`, `ViewerSessionTests.cs`

**Interfaces:**
- Produces:
  - `static class ThumbnailPlanner { const int Width = 136; const int MaxRequests = 64; static double HeightFor(PdfPageSize size) /* Width × Höhe / Breite */; static IReadOnlyList<RenderRequest> Plan(IReadOnlyList<PdfPageSize> sizes, IEnumerable<int> pages, double renderScaling); }` – `pages` sind die angezeigten Seiten (0-basiert), nächste zur ersten Reihenfolge-Seite zuerst; Auftrag je Seite: `Key = new TileKey(page, TileKey.PreviewScaleKey, 0, 0)`, `Scale = Width × renderScaling / Seitenbreite`, `Rect = (0, 0, size.ToPixels(scale))`, `WholePage = true`, `Background = true`; Seiten außerhalb von `sizes` oder mit ungültiger Größe werden übersprungen; höchstens `MaxRequests`; ungültiges `renderScaling` → `ArgumentOutOfRangeException`.
  - `sealed class ThumbnailSource : IDisposable { RenderCache Cache; RenderScheduler Scheduler; const long BudgetBytes = 48 MiB; }`; `ViewerSession.EnsureThumbnails(): ThumbnailSource?` – `null` außer im Zustand `Ready`; legt beim ersten Aufruf pro Dokument an (eigener `RenderCache(BudgetBytes)` und `RenderScheduler` auf dasselbe `PdfDocument`); wird zusammen mit dem Scheduler des Dokuments **vor** dem Dokument freigegeben (Wechsel des Dokuments, `Close`, `Dispose`).
- [ ] **Step 1: Failing tests:** `PlannerCreatesOneBackgroundWholePageRequestPerPage`, `ScaleGivesTheThumbnailWidthInDevicePixels` (Scaling 1,5 → 204 px), `PlannerSkipsPagesBeyondTheKnownSizes`, `PlannerCapsTheRequestCount`, `PlannerSkipsInvalidSizes`, `PlannerRejectsInvalidScaling`, `HeightKeepsTheAspectRatio`; Session: `EnsureThumbnailsIsNullBeforeReady`, `EnsureThumbnailsReturnsTheSameSourceUntilTheDocumentChanges`, `ThumbnailsAreDisposedWithTheDocument`, `ThumbnailsRenderIntoTheirOwnCache` (Auftrag für Seite 0 → `TileReady`, Bild in `Thumbnails.Cache`, nicht in `Session.Cache`).
- [ ] **Step 2–4:** fehlschlagen, implementieren, `--filter-namespace "*Viewing*"` grün. **Step 5: Commit** `feat(core): Planung und Besitz der Seitenvorschau`.

---

### Task 7: `TabViewModel` und `MainViewModel` als Fenster-ViewModel

Dies ist der große Umbau: das Dokument-Verhalten zieht vom Fenster in den Tab um; die Oberfläche bleibt zunächst einfach (ein Tab sichtbar wie in M3 plus die neue Liste), die Tableiste folgt in Task 8.

**Files:**
- Create: `src/Pagewing.App/ViewModels/TabViewModel.cs`, `src/Pagewing.Core/Viewing/ViewState.cs`
- Modify: `ViewModels/MainViewModel.cs`, `Views/MainWindow.axaml(.cs)`, `Views/TitleBar.axaml`, `Views/StatusBar.axaml(.cs)`, `Views/DocumentView.axaml.cs`, `Views/ErrorCard.axaml`, `Views/PasswordCard.axaml(.cs)`, `Views/StartScreen.axaml`, `App.axaml.cs`, `Measurement/*.cs`, `Resources/*`
- Test: `tests/Pagewing.App.Tests/TabsTests.cs` (neu); `OpenFlowTests`, `MainWindowTests`, `DocumentViewTests`, `MeasurementTests`, `HeadlessSmokeTests` anpassen

**Interfaces:**
- Produces (`Pagewing.Core.Viewing`): `public sealed record ViewState(PageAnchor Anchor, double HorizontalFromCentre)`.
- Produces `public sealed class TabViewModel : ViewModelBase, IDisposable`:
  - `TabViewModel(ViewerSession session)` (der Tab besitzt die Sitzung); `internal ViewerSession Session`; `Task OpenAsync(string path)`; `void Dispose()` (hebt Abonnements auf, `session.Dispose()`; mehrfach aufrufbar).
  - übernommen aus dem bisherigen `MainViewModel` unverändert (Name, Typ, Verhalten): `State`, `IsReady`, `HasStatusBarRow`, `IsPasswordPrompt`, `IsPasswordIncorrect`, `IsFailed`, `IsDocumentIcon`, `Password`, `PasswordPromptText`, `Error`, `ErrorCauseText`, `ErrorDetailsText`, `FileName`, `FullPath`, `PageCount`, `PageCountText`, `CurrentPage`, `PageFieldText`, `Zoom`, `ZoomText`, `ZoomInCommand`, `ZoomOutCommand`, `PreviousPageCommand`, `NextPageCommand`, `CommitPageFieldCommand`, `SubmitPasswordCommand`, `event Action<int>? PageRequested`, `event Action? PasswordRejected`, `GoToPage(int)`, `ResetPageField()`, `static CauseOf(PdfOpenError)`.
  - neu: `bool IsActive { get; set; }` (ruft `Session.SetActive`); `ZoomMode ZoomMode { get; set; }` (Standard `Free`; die Zoom-Setter der Stufen/des Rads setzen `Free`; `internal void ApplyFitZoom(double zoom)` setzt den Zoom, ohne den Modus zu ändern); `PageLayoutMode LayoutMode { get; set; }`; `SidePanelKind SidePanel { get; set; }` (`enum SidePanelKind { None, Pages, Contents }`); `ViewState? SavedView { get; set; }`; `int CurrentPageLast { get; set; }` (letzte Seite der aktuellen Zeile, für die Vorschau); `event Action? CloseRequested`, `event Action? OpenOtherRequested`; `ICommand CloseCommand` (löst `CloseRequested` aus), `ICommand CancelPasswordCommand` (ebenso), `ICommand OpenOtherCommand` (löst `OpenOtherRequested` aus); `event Action? PageFieldFocusRequested` und `ICommand FocusPageFieldCommand`.
  - Beim Wechsel des Dokuments (`isNewDocument`) bleiben `ZoomMode`, `LayoutMode`, `SidePanel` unverändert, `CurrentPage = 1`, `Zoom = ZoomSteps.Default` nur wenn `SavedView == null`.
- Produces `MainViewModel` (Fenster):
  - `MainViewModel(Func<ViewerSession> sessionFactory, IFilePicker picker)`; `TabList<TabViewModel> Tabs`; `TabViewModel? ActiveTab` (INPC, gesetzt aus `Tabs.ActiveChanged`; alter Tab `IsActive = false`, neuer `true`); `bool IsEmpty` (kein Tab), `bool HasTabs`; `string WindowTitle` („Dateiname – Pagewing“ des aktiven Tabs, sonst App-Name); `bool IsWindowsTitleBar`; `const int MaxFilesPerOpen = 20`.
  - Befehle: `OpenCommand` (Dialog, öffnet neuen Tab), `CloseActiveTabCommand` (Strg+W), `CloseTabCommand` (Parameter `TabViewModel`), `ActivateTabCommand` (Parameter `TabViewModel`), `NextTabCommand`, `PreviousTabCommand`.
  - `Task OpenAsync(string path)` – leerer Pfad tut nichts; ist der volle Pfad (`Path.GetFullPath`, bei ungültigem Pfad der Pfad wie gegeben) schon in einem Tab geöffnet, wird dieser aktiv; sonst legt ein neuer Tab (`new TabViewModel(sessionFactory())`) rechts vom aktiven an und öffnet; `Task OpenManyAsync(IReadOnlyList<string> paths)` – höchstens `MaxFilesPerOpen`, in Reihenfolge, der letzte wird aktiv; `TabViewModel AdoptSession(ViewerSession session)` – neuer Tab für eine schon (im Start) öffnende Sitzung; `void OnExternalOpen(IReadOnlyList<string> paths)` (beliebiger Thread → UI-Thread, `OpenManyAsync`, danach `ActivationRequested`); `event Action? ActivationRequested`; `event Action? CloseWindowRequested` (letzter Tab geschlossen und `CloseWindowWhenEmpty` gesetzt, Standard `false`).
  - Tab-Ereignisse: `CloseRequested` schließt den Tab; `OpenOtherRequested` öffnet den Dialog und ersetzt bei einem Ergebnis das Dokument **in diesem Tab** (`tab.OpenAsync(path)`); schlägt der Dialog fehl, zeigt der Tab die Fehlerkarte wie bisher (`ShowPickerFailure` zieht in den Tab um).
  - Schließen eines Tabs: `Tabs.Close(tab)`, danach `tab.Dispose()`; ein Tab im Zustand `Loading` wird genauso freigegeben (die Sitzung bricht die Anfrage ab).
- XAML: `DocumentArea`-Inhalt (`DocumentView`, `PasswordCard`, `ErrorCard`) und `StatusBar` bekommen `DataContext="{Binding ActiveTab}"` und `x:DataType="vm:TabViewModel"`; `StartScreen` und `TitleBar` bleiben am `MainViewModel` (`TitleBar` zeigt bis Task 8 den aktiven Tab wie bisher über `ActiveTab`-Pfade). `DocumentView` liest `TabViewModel` statt `MainViewModel` (`Connect`/`Disconnect` bleiben die Wechselstelle). `MainWindow.OnDrop` ruft `OpenManyAsync` mit **allen** abgelegten lokalen Pfaden (`DroppedPaths`, ersetzt `DroppedPath`). `Measurement/*` hält weiter das `MainViewModel`, liest `State`, `Error`, `FullPath`, `Session`, `Zoom` über `ActiveTab`; die Ausgaben der Schalter bleiben byte-gleich. `App.axaml.cs`: Fenster-ViewModel mit `() => new ViewerSession(new PdfEngine())`; ist `session.State != Empty`, wird `AdoptSession(session)` aufgerufen (Start mit Dateiargument ändert sein Timing nicht).

- [ ] **Step 1: Failing tests** in `TabsTests` (Headless, `[AvaloniaFact]`, Hilfsmethoden wie in `OpenFlowTests`): `OpeningASecondFileAddsATabRightOfTheActiveOne`, `OpeningAnOpenFileActivatesItsTab`, `ClosingTheActiveTabActivatesTheRightNeighbour`, `ClosingTheLastTabShowsTheStartScreen`, `ClosingALoadingTabDisposesItsSession` (Sitzung mit künstlich langsamem `open`; nach Schließen kein `Changed` mehr), `ClosingATabWithAPasswordPromptWorks`, `ErrorCardOpenOtherReplacesTheDocumentInItsTab` (Tab-Anzahl bleibt), `CancelOnThePasswordCardClosesTheTab`, `DroppingThreeFilesOpensThreeTabs`, `DroppingMoreThanTwentyFilesOpensTwenty`, `ADirectoryOrEmptyFileGetsItsOwnErrorTab`, `NextTabCommandCyclesThroughTheTabs`, `WindowTitleFollowsTheActiveTab`, `OnExternalOpenOpensTabsAndRequestsActivation`, `EachTabKeepsItsOwnPageAndZoom` (Tab A Zoom 200 %, Tab B 100 %, zurück zu A: 200 %), `InactiveTabsHaveTheInactiveBudget` (nach Task 3).
- [ ] **Step 2:** `dotnet test tests/Pagewing.App.Tests --filter-class "*TabsTests"` – erwartet: Build-Fehler.
- [ ] **Step 3: Umbau.** Reihenfolge: `ViewState`; `TabViewModel` (Logik 1:1 aus `MainViewModel`, die Dokument-Teile des alten Codes verschieben, nicht neu schreiben); `MainViewModel` neu; XAML und Code-Behind umstellen; `Measurement/*` und `App.axaml.cs`; vorhandene Tests an die neuen Pfade anpassen (`viewModel.State` → `viewModel.ActiveTab!.State` usw.; Testnamen und Absicht bleiben).
- [ ] **Step 4:** `dotnet test tests/Pagewing.App.Tests` – alle Tests grün, `dotnet test tests/Pagewing.Core.Tests` weiter grün.
- [ ] **Step 5: Commit** `refactor(app): Tab-ViewModel und Fenster-ViewModel mit Tab-Liste`.

**Roadmap:** „Tabs (öffnen, schließen, wechseln, Reihenfolge ändern)“ noch nicht abhaken (Task 8).

---

### Task 8: Tableiste (Oberfläche)

**Files:**
- Create: `src/Pagewing.App/Controls/TabStripPanel.cs`, `src/Pagewing.App/Views/TabStrip.axaml(.cs)`
- Modify: `Views/TitleBar.axaml(.cs)`, `Views/MainWindow.axaml` (KeyBindings), `Styles/Controls.axaml`, `Styles/Icons.axaml`, `Resources/*`
- Test: `tests/Pagewing.App.Tests/TabStripTests.cs`

**Interfaces:**
- Consumes: `TabList<TabViewModel>`, `TabStripLayout.Compute` (Task 2), `MainViewModel`-Befehle (Task 7).
- Produces: `TabStripPanel : Panel` (misst die Tabs mit `TabStripLayout.Compute(count, availableWidth)`; bei `ShowOnlyActive` bekommen nicht aktive Tabs Größe 0 und werden unsichtbar); `TabStrip : UserControl` (`ItemsControl` über `Tabs` mit Tab-Vorlage nach Designsystem 7.2, Knopf „Alle Tabs (n)“ bei `ShowOnlyActive` mit Menü der Tab-Namen, Knopf „Datei öffnen“ rechts der Tabs).
- Tab-Vorlage: wie der bisherige `Tab` in `TitleBar.axaml` (Symbol Dokument/Schloss/Warndreieck, `MiddleEllipsisTextBlock`, × mit `ElementRole="User"`, Tooltip voller Pfad, aktiver Tab: Fläche `Pw.Brush.Surface.Canvas`, `FontWeight` SemiBold), Name `Tab` bleibt für den aktiven Tab bzw. `TabItem` + Index je Tab (für Tests: `AutomationProperties.AutomationId="Tab"`).
- Bedienung: Klick aktiviert; Mittelklick schließt; × schließt; Rechtsklick öffnet ein Menü mit „Schließen“ und „In neuem Fenster öffnen“ (der zweite Eintrag ist ab Task 10 verdrahtet; in diesem Task fehlt er noch und wird dort ergänzt); Umsortieren: Drücken auf einen Tab, Ziehen entlang der Leiste (Schwelle 6 px, Zeiger gefangen, der Tab folgt dem Zeiger, Einfügeposition über `TabList.Move`), Esc bricht ab; Ziehen aus der Leiste heraus tut in M4 nichts (M4.1). Tastatur: Strg+Tab/Strg+Umschalt+Tab (`NextTabCommand`/`PreviousTabCommand`), Strg+W, Strg+1 bis 9 sind **nicht** belegt (Strg+1 ist tatsächliche Größe).
- Abstimmung mit Designsystem 6: Breite für Tabs = Fensterbreite − 52 − 36 − (140 unter Windows); die Zieh-Fläche (`ElementRole="TitleBar"`) bleibt mindestens 48 px breit.

- [ ] **Step 1: Failing tests:** `TabStripShowsOneItemPerTab`, `ClickingATabActivatesIt`, `MiddleClickClosesATab`, `CloseButtonClosesTheTab`, `ActiveTabIsMarkedBoldAndMergesIntoTheDocumentArea` (FontWeight + Hintergrund), `DraggingATabReordersTheTabs` (simulierte Zeigerereignisse, Reihenfolge ändert sich, aktiver Tab bleibt), `EscapeCancelsTheDrag`, `NarrowWindowShowsOnlyTheActiveTabAndTheAllTabsMenu` (640 px, 5 Tabs), `AllTabsMenuListsEveryTabAndActivatesTheChosenOne`, `DragAreaStaysAtLeast48PixelsWide`, `TabsHaveToolTipWithTheFullPath`, `CtrlTabSwitchesToTheNextTab`, `ErrorAndPasswordTabsShowTheirSymbol`, `LinuxLayoutShowsTheSameStripBelowTheSystemTitleBar` (`IsWindowsTitleBar = false`).
- [ ] **Step 2–4:** fehlschlagen lassen, implementieren (Texte `AllTabs`, `AllTabsTooltip`, `CloseTabTooltip` in `Strings` + beide resx), `dotnet test tests/Pagewing.App.Tests` grün; Sichtprüfung: Headless-Aufnahme (`FramePixels`) bei 640 und 1024 px, hell und dunkel, in beiden Sprachen, 1 und 6 Tabs.
- [ ] **Step 5: Commit** `feat(app): Tableiste mit Wechseln, Schließen und Umsortieren`.

**Roadmap:** „Tabs (öffnen, schließen, wechseln, Reihenfolge ändern)“ abhaken (Nachweis: Tests `TabsTests`, `TabStripTests`, Anzahl).

---

### Task 9: Ansichtszustand je Tab in der `DocumentView`

**Files:**
- Modify: `src/Pagewing.App/Views/DocumentView.axaml.cs`, `ViewModels/TabViewModel.cs`
- Test: `tests/Pagewing.App.Tests/DocumentViewTests.cs`

**Interfaces:**
- Consumes: `ViewState`, `TabViewModel.SavedView/Zoom/LayoutMode` (Task 7), `TabViewModel.IsActive` (Task 3/7).
- Verhalten: `Disconnect()` speichert in den abgetretenen Tab `SavedView = new ViewState(layout.GetAnchor(Scroller.Offset.Y), Scroller.Offset.X − Mitte-Verschiebung)` (nur mit Dokument, sonst unverändert); `ShowDocument(...)` setzt `layout.Zoom = tab.Zoom` (statt `Default`) und `layout.Mode = tab.LayoutMode`, stellt bei vorhandenem `SavedView` den Anker wieder her (`layout.GetOffset(anchor)`, horizontal von der Mitte aus) und lässt `tab.Zoom` unverändert; ohne `SavedView` Anfang und `ZoomSteps.Default` wie bisher. Der Canvas wird beim Wechsel vom alten Tab abgehängt (`Detach` setzt die Wunschliste leer), der inaktive Tab bekommt keine Aufträge mehr.

- [ ] **Step 1: Failing tests:** `SwitchingTabsRestoresPageZoomAndScrollPosition` (Tab A bei Seite 3, 200 %, kleiner Versatz; zu B und zurück: derselbe Anker innerhalb einer halben Seitenhöhe-Toleranz von 1 DIP), `ANewTabStartsAtTheTopWithDefaultZoom`, `InactiveTabsRequestNoRendering` (nach Wechsel: `Scheduler.PendingCount == 0` beim alten Tab), `SwitchingBackShowsCachedTilesWithoutRendering` (Vorschau eines gerade gesehenen Tabs ist sofort da), `ClosingTheActiveTabShowsTheNeighbourAtItsSavedPosition`, `ResizeWhileAnotherTabIsActiveKeepsTheSavedAnchor`.
- [ ] **Step 2–4:** fehlschlagen lassen, implementieren, `dotnet test tests/Pagewing.App.Tests` grün.
- [ ] **Step 5: Commit** `feat(app): Seite, Zoom und Scroll-Position je Tab`.
- [ ] **Step 6: Zwischenstand prüfen:** Branch pushen (`git push -u origin feature/m04-navigation`), CI abwarten (Build, Test, Format, Startzeit-Check beider Runner). Ergebnis in der Roadmap-`evidence` der späteren Abnahme verwenden; Abweichung vom Budget: Ursache beheben, nicht lockern.

---

### Task 10: `Workspace` und mehrere Fenster

**Files:**
- Create: `src/Pagewing.App/Services/Workspace.cs`
- Modify: `App.axaml.cs`, `Views/MainWindow.axaml.cs`, `Views/TabStrip.axaml(.cs)` (Menüeintrag), `ViewModels/MainViewModel.cs`, `Resources/*`
- Test: `tests/Pagewing.App.Tests/WorkspaceTests.cs`

**Interfaces:**
- Produces `internal sealed class Workspace`: `Workspace(Func<ViewerSession> sessionFactory, Func<Window> createWindow, Func<Window, IFilePicker> createPicker)`; `IReadOnlyList<MainViewModel> Windows`; `MainViewModel? ActiveWindow` (zuletzt aktiviertes, über `Window.Activated`); `MainViewModel OpenWindow(TabViewModel? firstTab = null)` (legt Fenster-VM und Fenster an, setzt `DataContext`, zeigt das Fenster, trägt es ein; das erste Fenster setzt der Aufrufer als `desktop.MainWindow`; jedes weitere Fenster hat `CloseWindowWhenEmpty = true`); `void MoveToNewWindow(MainViewModel source, TabViewModel tab)` (Tab aus `source.Tabs` entfernen **ohne** `Dispose`, in neuem Fenster als einziger Tab einfügen; hat `source` nur diesen einen Tab, tut es nichts, damit kein leeres oder gleichwertiges Fenster entsteht); `void OnExternalOpen(IReadOnlyList<string> paths)` (beliebiger Thread; leitet an `ActiveWindow` oder, ohne Fenster, das erste Fenster weiter).
- Fenster schließen: `Closed` gibt alle Tabs des Fensters frei (`Dispose`), entfernt es aus `Windows`. Das Fenster des Prozesses endet erst mit dem letzten Fenster (`ShutdownMode.OnLastWindowClose`). Der Tab-Kontextmenü-Eintrag „In neuem Fenster öffnen“ (Text `MoveToNewWindow` in `Strings` und beiden resx) ruft `Workspace.MoveToNewWindow`; er ist nur sichtbar, wenn das Fenster mindestens zwei Tabs hat.
- `App.axaml.cs`: baut den `Workspace`, öffnet das erste Fenster, hängt `instance.StartListening(workspace.OnExternalOpen)` statt `viewModel.OnExternalOpen`; Messläufe nutzen weiter das Fenster-VM des ersten Fensters.
- Neue Fenster: Größe und Position wie das Quellfenster, um 32 DIP nach rechts unten versetzt (`WindowStartupLocation.Manual`), mindestens sichtbar auf dem Bildschirm.

- [ ] **Step 1: Failing tests** (Headless, `Workspace` mit Test-Fabriken): `MovingATabToANewWindowKeepsItsSession` (gleiche `ViewerSession`-Instanz, kein erneutes `open`, Zustand `Ready`), `MovedTabKeepsPageAndZoom`, `SourceWindowKeepsItsOtherTabs`, `MovingTheOnlyTabOfAWindowDoesNothing` (Fensteranzahl und Tab bleiben), `MoveToNewWindowIsOnlyOfferedWithTwoOrMoreTabs`, `ClosingTheLastTabOfASecondWindowClosesIt`, `ClosingTheLastTabOfTheOnlyWindowShowsTheStartScreen`, `ClosingAWindowDisposesItsTabs`, `ExternalOpenGoesToTheActiveWindow`, `ExternalOpenWithNoWindowActivatedUsesTheFirstWindow`, `ClosingTheFirstWindowKeepsTheOthersAlive`.
- [ ] **Step 2–4:** fehlschlagen lassen, implementieren, `dotnet test tests/Pagewing.App.Tests` grün. Manuelle Prüfung (offen markieren, Befehl nennen): Tab über das Menü in ein neues Fenster verschieben, beide Fenster unabhängig scrollen und zoomen, auf Windows und Linux (X11).
- [ ] **Step 5: Commit** `feat(app): Workspace und Tab in neuem Fenster öffnen`.

---

### Task 11: Zoom-Modi und „Gehe zu Seite“ (Oberfläche)

**Files:**
- Modify: `Views/StatusBar.axaml(.cs)`, `Views/DocumentView.axaml.cs`, `Views/MainWindow.axaml` (KeyBindings), `ViewModels/TabViewModel.cs`, `Styles/Icons.axaml`, `Resources/*`
- Test: `tests/Pagewing.App.Tests/ZoomModeTests.cs`

**Interfaces:**
- Consumes: `DocumentLayout.FitZoom` (Task 1), `TabViewModel.ZoomMode/ApplyFitZoom/FocusPageFieldCommand` (Task 7).
- Produces: Befehle am Tab: `SetZoomModeCommand` (Parameter `ZoomMode`), `SetZoomCommand` (Parameter `double`, setzt `Free`); Zoom-Menü an `ZoomValueText` (Klick öffnet `Flyout` mit Stufen aus `ZoomSteps` (25 bis 400 %), dann „Seitenbreite“, „Ganze Seite“, „Tatsächliche Größe“, das gewählte mit Häkchen links (`Pw.Icon.Check`)); Tastenkürzel am Fenster: Strg+0 → `FitPage`, Strg+1 → `ActualSize`, Strg+2 → `FitWidth`, Strg+G → `FocusPageFieldCommand` (Seitenfeld bekommt Fokus und markiert den Inhalt; die Statusleiste hört auf `PageFieldFocusRequested`); `ZoomText` zeigt im Fit-Modus den berechneten Wert.
- `DocumentView`: berechnet bei `ZoomMode != Free` den Zoom neu über `layout.FitZoom(mode, currentPageIndex, viewport.Width, viewport.Height)` bei: Moduswechsel, Anzeige eines Dokuments, Viewport-Größenänderung (`OnScrollerPropertyChanged` bei `ViewportProperty`), Layoutwechsel, `PageRequested`; Anwenden über denselben Pfad wie `ApplyZoom`, dann `tab.ApplyFitZoom(zoom)`. Schleifenschutz: Eine Änderung des Viewports durch das Erscheinen der Scrollleiste darf nicht pendeln (nur neu rechnen, wenn sich die berechnete Zoomstufe um mehr als 0,0005 ändert).
- [ ] **Step 1: Failing tests:** `FitWidthZoomsSoThePageFillsTheViewportWidth` (Breite der Seite + 2 × 32 ≈ Viewport-Breite), `FitPageShowsTheWholeCurrentPage`, `ActualSizeIsOneHundredPercent`, `FitModeFollowsAWindowResize`, `FitModeIsKeptWhenSwitchingTabsAndRestored`, `ZoomStepsLeaveTheFitMode` (+ → `Free`), `CtrlWheelLeavesTheFitMode`, `FitWidthDoesNotOscillateWhenTheScrollBarAppears`, `ZoomMenuShowsTheCheckmarkOnTheActiveMode`, `CtrlGFocusesAndSelectsThePageField`, `InvalidPageEntryResetsTheFieldWithoutAMessage` (Regressionsschutz), `GoToPageFromTheFieldScrollsThePageToTheTop`, `FitPageRefitsAfterAJumpToAnotherPage`, `ShortcutsCtrl0Ctrl1Ctrl2SetTheModes`.
- [ ] **Step 2–4:** fehlschlagen lassen, implementieren (Texte `ZoomFitWidth`, `ZoomFitPage`, `ZoomActualSize`, `ZoomMenuTooltip`), Tests grün. **Step 5: Commit** `feat(app): Zoom-Modi und Gehe zu Seite`.

**Roadmap:** „Zoom-Modi …“ und „Gehe zu Seite“ abhaken (Nachweis: `ZoomModeTests`, Anzahl).

---

### Task 12: Layout-Modi (Oberfläche)

**Files:**
- Modify: `Views/StatusBar.axaml(.cs)`, `Views/DocumentView.axaml.cs`, `ViewModels/TabViewModel.cs`, `Styles/Icons.axaml`, `Resources/*`
- Test: `tests/Pagewing.App.Tests/LayoutModeTests.cs`

**Interfaces:**
- Consumes: `DocumentLayout.Mode/ShowPage/PagesOfRow/RowOf/IsShown` (Task 1), `TabViewModel.LayoutMode/CurrentPageLast`.
- Produces: `SetLayoutModeCommand` (Parameter `PageLayoutMode`); Knopf `LayoutButton` in der Statusleiste (rechts neben dem Zoom, Icon des aktuellen Layouts: einzeln `file`, fortlaufend `layout-rows`, doppelseitig `layout-columns`, mit Deckblatt `book`; Tabler-Namen vor dem Kopieren prüfen) mit Flyout aus vier Einträgen („Einzelseite“, „Fortlaufend“, „Doppelseitig“, „Doppelseitig mit Deckblatt“), aktueller mit Häkchen. Unter 760 px bleibt nur das Symbol (Tooltip nennt das Layout).
- `DocumentView`: Layoutwechsel behält die aktuelle Seite (`layout.Mode = …`; Anker der aktuellen Seite wiederherstellen; im Einzelseitenmodus `ShowPage(currentPage)` und oben beginnen); `CurrentPage` = erste Seite der am stärksten sichtbaren Zeile, `CurrentPageLast` = letzte; `PreviousPageCommand`/`NextPageCommand`/Bild auf/ab bewegen sich in den Doppelseiten-Modi um eine Zeile (`GoToPage(first of next row)`); Einzelseite blättert zusätzlich: Leertaste am Seitenende → nächste Seite (Umschalt+Leertaste am Anfang → vorige), ein weiterer Mausradschritt am Rand, an dem schon ganz gescrollt ist (ohne Strg) → nächste/vorige Seite (Position oben bzw. unten); Pfeiltasten scrollen nur.
- Der Canvas wird nicht geändert, wenn die Tests grün sind (er nutzt `GetVisiblePages/GetPageRect`); ein Dokument mit Querformat in der Doppelseitenansicht überlappt nicht (siehe Task 1).
- [ ] **Step 1: Failing tests:** `SwitchingToTwoPagesKeepsTheCurrentPage`, `TwoPageModeShowsBothPagesOfARowAtTheSameHeight` (zwei `LastSharpDestinations` mit gleichem `Y`), `CoverModeShowsThePageAloneOnTheRight`, `NextPageInTwoPageModeMovesByARow`, `PageFieldShowsTheFirstPageOfTheRow`, `SinglePageModeShowsOnlyOnePage` (Extent-Höhe = Seite + 64), `SpaceAtTheEndOfASinglePageGoesToTheNextPage`, `WheelAtTheEdgeFlipsTheSinglePage`, `WheelInTheMiddleOfASinglePageOnlyScrolls`, `LayoutModeIsKeptPerTab`, `LayoutButtonShowsTheCheckmarkOnTheCurrentLayout`, `OddPageCountLeavesTheLastPageAloneOnTheLeft`, `MixedPageSizesDoNotOverlapInTwoPageMode`, `FitWidthAdaptsToTheLayoutMode` (Wechsel von Fortlaufend zu Doppelseitig bei `FitWidth` rechnet den Zoom neu).
- [ ] **Step 2–4:** fehlschlagen lassen, implementieren (Texte `LayoutSingle`, `LayoutContinuous`, `LayoutTwoPages`, `LayoutTwoPagesCover`, `LayoutTooltip`), Tests grün. **Step 5: Commit** `feat(app): Layouts einzeln, fortlaufend und doppelseitig`.

**Roadmap:** „Layouts: einzeln, fortlaufend, doppelseitig (mit/ohne Deckblatt)“ abhaken (Nachweis: `LayoutModeTests`, `DocumentLayoutTests`, Anzahl).

---

### Task 13: Aktionsleiste und Panel

**Files:**
- Create: `Views/ActionBar.axaml(.cs)`, `Views/SidePanel.axaml(.cs)`
- Modify: `Views/MainWindow.axaml(.cs)`, `ViewModels/MainViewModel.cs`, `ViewModels/TabViewModel.cs`, `Styles/Icons.axaml`, `Resources/*`
- Test: `tests/Pagewing.App.Tests/PanelTests.cs`

**Interfaces:**
- Produces: `ActionBar` (52 px breit, `Pw.Brush.Surface.Window`, Knöpfe 40 px, Icons 20 px `text.secondary`, aktiv: Fläche `Pw.Brush.Accent.Subtle` + Icon `accent.default`): „Seiten“ (`layout-grid`) und „Inhalt“ (`list-tree`), Tooltips mit Kürzel; `SidePanel` (Kopf mit Titel `type.strong` und Schließen-Knopf, Inhalt per `ContentControl` mit Datenvorlagen je `SidePanelKind`); `MainViewModel.PanelWidth` (Standard 264, begrenzt 200 bis 400), `ToggleSidePanelCommand` (Parameter `SidePanelKind`: gleicher Eintrag schließt, anderer wechselt, höchstens ein Panel), `IsNarrowWindow` (Fensterbreite < 900, vom Fenster gesetzt).
- Layout (Designsystem 6, Punkt 3): Spalten `ActionBar | Panel | Dokumentbereich`; Trennkante zwischen Panel und Dokument ist ein `GridSplitter` (Doppelklick setzt 264 zurück); der Dokumentbereich behält oben links `radius.overlay`; ActionBar und Panel nur, wenn `HasTabs`. Unter 900 px liegt das Panel **über** dem Dokument (Überlagerung mit Kante `border.subtle`, ohne Schatten), schließt sich nach Auswahl einer Seite oder eines Eintrags. Öffnen mit 150 ms ease-out (Breite/Deckkraft), Schließen 100 ms; keine Animation vor dem ersten Frame.
- Beim ersten `FirstSharpTileDrawn` eines Fensters öffnet „Seiten“ im aktiven Tab, wenn `SidePanel == None`, das Fenster ≥ 900 px breit ist und der Nutzer in diesem Fenster noch nicht selbst ein Panel geöffnet oder geschlossen hat. Der Inhalt eines Panels wird erst beim Öffnen gebaut (kein Aufbau im Startpfad).
- [ ] **Step 1: Failing tests:** `ActionBarIsHiddenWithoutTabs`, `ClickingAnEntryOpensItsPanelAndClickingAgainClosesIt`, `OnlyOnePanelIsOpen`, `PanelChoiceIsPerTab`, `PanelWidthIsLimitedAndResetByDoubleClick`, `PanelOpensAfterTheFirstSharpTileOnAWideWindow`, `PanelStaysClosedOnANarrowWindow`, `PanelOverlaysTheDocumentBelow900Pixels` (Dokumentbreite unverändert), `NarrowPanelClosesAfterPickingAPage`, `UserClosingThePanelPreventsTheAutoOpen`, `PanelContentIsBuiltOnlyWhenOpened`, `ActiveEntryHasAFillNotOnlyAColour`, `FirstFrameDoesNotContainThePanel` (Startpfad: bis `FirstSharpTileDrawn` kein `SidePanel`-Inhalt im Baum).
- [ ] **Step 2–4:** fehlschlagen lassen, implementieren (Texte `PagesPanelTitle`, `ContentsPanelTitle`, `ClosePanel`, `PagesTooltip`, `ContentsTooltip`), Tests grün. **Step 5: Commit** `feat(app): Aktionsleiste und Panel`.

---

### Task 14: Seitenvorschau (Thumbnails)

**Files:**
- Create: `Views/PagesPanel.axaml(.cs)`, `Controls/ThumbnailList.cs`
- Modify: `Views/SidePanel.axaml`, `Views/DocumentView.axaml.cs` (setzt `HasShownSharpPage`), `ViewModels/TabViewModel.cs` (`HasShownSharpPage`), `Styles/Controls.axaml`, `Resources/*`
- Test: `tests/Pagewing.App.Tests/PagesPanelTests.cs`

**Interfaces:**
- Consumes: `ViewerSession.EnsureThumbnails`, `ThumbnailPlanner` (Task 6), `TabViewModel.CurrentPage/CurrentPageLast/GoToPage`.
- Produces: `PagesPanel` mit `ThumbnailList : ListBox` (Virtualisierung per `VirtualizingStackPanel`; ein Eintrag je Seite mit Platzhalter im Seitenformat: Breite 136, Höhe `ThumbnailPlanner.HeightFor`, `Pw.Brush.Surface.Raised` mit Kante `border.subtle`, darunter die Seitennummer in `type.mono`; Seitenverhältnis kommt aus `session.LoadedSizes`, unbekannte Seiten nehmen das der ersten). Aktuelle Seite(n) der Zeile: Fläche `accent.subtle`, 2 px Rahmen `accent.default`, **fette** Nummer (Designsystem 7.3/7.10). Klick, Enter und Leertaste → `GoToPage`; Pfeiltasten bewegen den Fokus; die Auswahl folgt dem Scrollen des Dokuments (aktuelle Seite sichtbar halten).
- Rendern: Realisierte Einträge melden sich an (Vorbereitungs-/Löse-Ereignis des Containers); gesammelt über `Dispatcher.UIThread.Post(…, DispatcherPriority.Background)` entsteht **ein** Aufruf `Thumbnails.Scheduler.Update(ThumbnailPlanner.Plan(sizes, pages, renderScaling))`, die Seiten nach Nähe zur aktuellen Seite geordnet. `TileReady` (Pool-Thread) wird auf den UI-Thread gepostet und setzt das `Bitmap` des Eintrags (`TileBitmaps.Create`); `TileFailed` lässt den Platzhalter stehen. Beim Schließen des Panels, Tab-Wechsel oder Dokumentwechsel: `Update([])`, Bitmaps freigeben. Vorschaubilder rendern erst, wenn der Tab mindestens ein scharfes Seitenbild gezeigt hat (der Tab meldet es: `TabViewModel.HasShownSharpPage`, vom `DocumentView` gesetzt).
- [ ] **Step 1: Failing tests:** `PanelShowsOnePlaceholderPerPageInThePageAspectRatio`, `OnlyRealizedItemsAreRequested` (1000-Seiten-PDF: `Plan`-Größe ≤ `MaxRequests`), `ThumbnailsRenderAfterTheFirstSharpPage` (vorher kein Auftrag am Thumbnail-Scheduler), `ThumbnailAppearsWhenRendered`, `ClickingAThumbnailJumpsToThePage`, `CurrentPageIsMarkedWithAFrameAndABoldNumber`, `BothPagesOfARowAreMarkedInTwoPageMode`, `SelectionFollowsTheDocumentScroll`, `ClosingThePanelCancelsThumbnailWork`, `SwitchingTabsReplacesTheThumbnails`, `ThumbnailsUseTheirOwnCacheBudget` (`Session.Cache.UsedBytes` wächst nicht durch Vorschaubilder), `ThumbnailsAreCrispAtScaling150` (Bildbreite 204 px bei 136 DIP), `NoThumbnailWorkForInactiveTabs`.
- [ ] **Step 2–4:** fehlschlagen lassen, implementieren, Tests grün; Sichtprüfung per Headless-Aufnahme (hell/dunkel, Deutsch/Englisch, 3 Seiten und 1000 Seiten). **Step 5: Commit** `feat(app): Seitenvorschau im Panel`.
- [ ] **Step 6: Zwischenstand prüfen:** Branch pushen, CI abwarten (Startzeit-Check darf nicht über Budget + 25 % liegen; Median der vier Fälle notieren).

**Roadmap:** „Seitenleiste mit Seitenvorschau …“ abhaken (Nachweis: `PagesPanelTests`, Anzahl).

---

### Task 15: Inhaltsverzeichnis (Oberfläche)

**Files:**
- Create: `Views/OutlinePanel.axaml(.cs)`, `ViewModels/OutlineNodeViewModel.cs`
- Modify: `Views/SidePanel.axaml`, `ViewModels/TabViewModel.cs`, `Resources/*`
- Test: `tests/Pagewing.App.Tests/OutlinePanelTests.cs`

**Interfaces:**
- Consumes: `PdfDocument.GetOutlineAsync` (Task 4), `TabViewModel.GoToPage`.
- Produces: `OutlineNodeViewModel(PdfOutlineItem item)` mit `Title` (leer → „(ohne Titel)“ / „(untitled)“), `PageIndex`, `CanJump`, `Children`, `IsExpanded` (= `IsOpen`); `TabViewModel.Outline` (`IReadOnlyList<OutlineNodeViewModel>?`, `null` = noch nicht geladen), `OutlineState` (`NotLoaded`, `Loading`, `Loaded`, `Failed`) und `Task EnsureOutlineAsync()` (lädt höchstens einmal je Dokument beim ersten Öffnen von „Inhalt“; verworfen, wenn das Dokument inzwischen wechselt). `OutlinePanel`: `TreeView` mit Vorlage (Titel, Seitennummer rechts in `type.mono`), Tastatur (Pfeile, Enter springt), Klick auf einen Eintrag mit Ziel springt (`GoToPage(PageIndex + 1)`); Eintrag ohne Ziel ist nicht klickbar und trägt Text `text.secondary`; ohne Lesezeichen steht „Dieses Dokument hat kein Inhaltsverzeichnis.“; bei `Failed` „Das Inhaltsverzeichnis konnte nicht gelesen werden.“. Der aktuell sichtbare Abschnitt wird **nicht** mitgeführt (YAGNI).
- [ ] **Step 1: Failing tests:** `OutlineIsNotLoadedBeforeThePanelOpens` (kein `GetOutlineAsync` im Startpfad), `OutlineShowsTheTreeOfTheTestFile` (`outline.pdf`), `ClickingAnEntryJumpsToItsPage`, `EntryWithoutTargetIsNotClickable`, `EntryWithInvalidPageIsNotClickable`, `ExpandedStateFollowsTheDocument`, `DocumentWithoutOutlineShowsTheHint`, `CyclicOutlineDoesNotHangTheUi` (`outline-cycle.pdf`), `EmptyTitleShowsAPlaceholder`, `KeyboardEnterJumps`, `SwitchingTabsKeepsEachTabsOutline`, `OutlineOfAClosedTabIsDiscarded`.
- [ ] **Step 2–4:** fehlschlagen lassen, implementieren (Texte `OutlineNoTitle`, `OutlineEmpty`, `OutlineFailed`), Tests grün. **Step 5: Commit** `feat(app): Inhaltsverzeichnis im Panel`.

**Roadmap:** „Inhaltsverzeichnis/Lesezeichen aus dem PDF anzeigen und anspringen“ abhaken (Nachweis: `OutlineTests`, `OutlinePanelTests`).

---

### Task 16: Dokumentinfos in der Statusleiste

**Files:**
- Create: `Views/DocumentInfoPopup.axaml(.cs)`, `ViewModels/DocumentInfoViewModel.cs`
- Modify: `Views/StatusBar.axaml(.cs)`, `ViewModels/TabViewModel.cs`, `Styles/Icons.axaml`, `Resources/*`
- Test: `tests/Pagewing.App.Tests/DocumentInfoTests.cs`

**Interfaces:**
- Consumes: `DocumentInfoReader.ReadAsync`, `PageFormat.Describe` (Task 5).
- Produces: Knopf „Dokumentinfos“ (`info-circle`, rechts in der Statusleiste, Beschriftung entfällt unter 760 px, Tooltip bleibt) öffnet ein `Popup` über der Statusleiste (Designsystem 7.6): Dateiname und Ordner, Seitenzahl, Seitenformat der **aktuellen** Seite (aus `Session.LoadedSizes[CurrentPage − 1]`, ändert sich beim Scrollen), PDF-Version, Dateigröße (menschenlesbar: KB/MB nach Kultur), Titel, Autor, Erstelldatum (kurzes Datum nach Kultur), fehlende Werte erscheinen nicht. Gelesen wird beim Öffnen des Popups (`DocumentInfoViewModel.LoadAsync`), nicht beim Öffnen des Dokuments. Esc oder Klick daneben schließt; Fokus kehrt zum Knopf zurück. Karte: `radius.overlay`, `surface.raised`, Kante `border.subtle`.
- [ ] **Step 1: Failing tests:** `InfoButtonOpensThePopupWithTheFileData`, `PopupShowsTheFormatOfTheCurrentPage` (Wechsel der Seite aktualisiert, `wide-page.pdf` zeigt das Querformat), `MissingMetadataIsLeftOut`, `InfoIsNotReadBeforeThePopupOpens`, `EscapeClosesThePopupAndReturnsTheFocus`, `FileSizeIsFormattedForTheCulture` (de/en), `PopupShowsNothingForALoadingOrFailedTab` (Knopf nur bei `IsReady`), `LabelDisappearsBelow760Pixels`.
- [ ] **Step 2–4:** fehlschlagen lassen, implementieren (Texte `DocumentInfo`, `InfoFile`, `InfoFolder`, `InfoPages`, `InfoPageSize`, `InfoPdfVersion`, `InfoFileSize`, `InfoTitle`, `InfoAuthor`, `InfoCreated`), Tests grün. **Step 5: Commit** `feat(app): Dokumentinfos in der Statusleiste`.

**Roadmap:** „Dokumentinfos in der Statusleiste …“ abhaken (Nachweis: `DocumentInfoTests`).

---

### Task 17: Messung, Dokumentation, Abschluss

**Files:**
- Modify: `src/Pagewing.App/Pagewing.App.csproj` bzw. Laufzeitkonfiguration (nur falls die Messung `GCConserveMemory` rechtfertigt), `Docs/DECISIONS.md`, `Docs/superpowers/specs/2026-10-17-designsystem.md`, `CLAUDE.md`, `CHANGELOG.md`, `Docs/roadmap.json`, `Docs/ROADMAP.md`, `bench/startup/README.md` (nur bei geänderten Messwerten)

- [ ] **Step 1: Speicher messen.** Scroll-Spitze (Working Set) mit `--measure-scroll` auf `thousand-pages.pdf` bei 100 % und 200 %, einmal wie bisher, einmal mit `DOTNET_GCConserveMemory=7` (Protokoll: `Docs/DECISIONS.md`, Eintrag „Render-Scheduler und Kachelparameter (M3)“, Abschnitt „Bestätigung“). Zusätzlich: fünf Tabs mit je einer 100-seitigen PDF geöffnet, vier inaktiv, Working Set nach 30 s notieren. Ziel: ≤ 1,5 × Budget beim Scrollen. Bringt `System.GC.ConserveMemory` in `runtimeconfig` eine Verbesserung ohne Start- oder Frame-Zeit-Verschlechterung (Startzeit-Check lokal vorher/nachher), wird es gesetzt; sonst nicht. Ergebnis mit Zahlen als Entscheidung festhalten, Backlog-Eintrag „Arbeitsspeicher beim Scrollen …“ in `roadmap.json` aktualisieren (erledigt oder mit dem Befund offen).
- [ ] **Step 2: Startzeit.** `dotnet run bench/startup/startup.cs -- --case cold`/`warm`/`thousand`/`second` lokal (Hinweis auf GPU/Anzeige beachten, `Warnung:`-Zeilen melden) und CI-Lauf des Branches; Medianwerte mit denen aus M3 vergleichen; die Aktionsleiste im Startpfad ist die Hauptquelle möglicher Mehrkosten. Über Budget + 25 %: Ursache beheben (nicht lockern), z. B. Aktionsleiste erst nach dem ersten Frame ins Layout nehmen.
- [ ] **Step 3: Dokumentation nachziehen** (Eintrag je Entscheidung des Abschnitts „Entscheidungen dieses Plans“ in `Docs/DECISIONS.md`, kurz mit Begründung und verworfener Alternative; `DocumentLayout` mit Zeilen und Fit-Regel; Tab-Besitz und Speicherbudget; Lesezeichen-Grenzen). Designsystem: 5.5 Belegung um `layout-rows`, `layout-columns`, `book`, `check` und die genutzten Panel-Icons ergänzen; 7.2 „Reihenfolge ändern (M4)“ erledigt, Menü „In neuem Fenster öffnen“, Überlauf „Alle Tabs“; 7.5 Layout-Knopf und Zoom-Menü (Kürzel Strg+0/1/2/G); 7.3 Panel-Verhalten wie umgesetzt; 9 „Dokumentinfos in M4“ als erledigt. `CLAUDE.md`, Abschnitt „App-Shell“: Tab-/Fenster-Architektur (`TabViewModel`, `MainViewModel` als Fenster, `Workspace`, eine `DocumentView` je Fenster, Speicherbudget). `CHANGELOG.md` `[Unreleased]` → `Added`: Tabs, Fenster aus Tab, Seitenvorschau, Inhaltsverzeichnis, Layouts, Zoom-Modi, Gehe zu Seite (Strg+G), Dokumentinfos.
- [ ] **Step 4: Roadmap.** `Docs/roadmap.json`: Alle Aufgaben von M4 abhaken, Abnahmekriterien mit Nachweis: „Headless-Tests für Tab-Wechsel, Layoutwechsel und Lesezeichen-Sprung grün“ (Testnamen und `dotnet test`-Ergebnis), „Startzeit-Budgets weiter eingehalten (CI)“ (Lauf-ID und Medianwerte); Milestone **4.1 „Tabs zwischen Fenstern ziehen“** und der `scopeChanges`-Eintrag vom 2026-10-17 stehen schon (beim Planen angelegt); falls sich der Umfang beim Umsetzen ändert, dort nachführen. M4 `status: "done"`, `completedOn` mit Datum. `dotnet run tools/roadmap.cs -- generate`, danach `-- check`.
- [ ] **Step 5: Gesamtprüfung:** `dotnet build Pagewing.slnx`, `dotnet test Pagewing.slnx`, `dotnet format Pagewing.slnx --verify-no-changes`, `dotnet run tools/roadmap.cs -- check`; AOT-Publish von App und CLI ohne Warnungen (`dotnet publish src/Pagewing.App -c Release -r win-x64`, analog CLI); Ausgaben notieren. Was nicht geprüft werden konnte (Linux/X11 von Hand, Snap Layouts, Hover-/Fokuszustände am echten Bildschirm, `hardware-measure`), als offen mit dem Befehl nennen. Review der ganzen Branch anstoßen (`superpowers:requesting-code-review`), Befunde einarbeiten.
- [ ] **Step 6: Commit** `docs: Milestone 4 abgeschlossen`; dann PR nach `dev` (`gh pr create --base dev`), CI grün abwarten, Squash-Merge laut `CLAUDE.md` („Git & Branches“).

---

## Selbstprüfung (Stand beim Schreiben)

- **Spec-Abdeckung:** Tabs (Tasks 2, 7, 8, 9, 10) · Seitenleiste mit Thumbnails nach dem ersten scharfen Bild (6, 13, 14) · Lesezeichen (4, 15) · Layouts (1, 12) · Zoom-Modi (1, 11) · Gehe zu Seite (11) · Dokumentinfos (5, 16) · Abnahme: Headless-Tests für Tab-Wechsel, Layoutwechsel und Lesezeichen-Sprung (9, 12, 15) · Startzeit (Task 17 und CI) · Designsystem 7.2 (Reihenfolge ändern), 7.3 (Panel), 7.5 (Zoom-Menü), 7.6 (Dokumentinfos) · Backlog „Arbeitsspeicher“ (3, 17).
- **Typkonsistenz:** `ZoomMode`/`PageLayoutMode` (Task 1) werden in 7, 11, 12 genutzt; `ViewState` (7) in 9; `TabBudget`/`SetActive` (3) in 7 und 9; `ThumbnailSource`/`EnsureThumbnails` (6) in 14; `PdfOutlineItem` (4) in 15; `DocumentInfoReader`/`PageFormat` (5) in 16; `TabList`/`TabStripLayout` (2) in 7 und 8.
- **Verhältnis:** Der Plan beschreibt Schnittstellen, Regeln und Tests; Körper schreibt der Ausführer.
