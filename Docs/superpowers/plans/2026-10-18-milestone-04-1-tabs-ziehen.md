# Milestone 4.1 – Tabs zwischen Fenstern ziehen – Implementierungsplan

> **Für agentische Ausführer:** Pflicht-Skill: `superpowers:subagent-driven-development` (empfohlen) oder `superpowers:executing-plans`. Die Schritte nutzen Checkbox-Syntax (`- [ ]`).

**Ziel:** Ein Tab lässt sich mit der Maus aus der Tableiste ziehen; schon beim Ziehen entsteht ein Fenster, das am Zeiger hängt; über der Tableiste eines Fensters (auch des eigenen) zeigt eine Einfügemarke die Stelle, beim Loslassen landet der Tab dort mit derselben Sitzung, Seite und demselben Zoom.

**Architektur:** Die Rechnung (Schwelle zum Lösen, Einfügeposition) liegt UI-frei in `Pagewing.Core.Viewing` (`TabDragMath`, `TabList<T>.Insert`). `Workspace` bekommt eine Zieh-Sitzung `TabDrag` (neues Fenster oder das Fenster des einzigen Tabs folgt dem Zeiger, Ziel unter dem Zeiger, Ablegen, Abbrechen); `TabStrip` erkennt die Geste und ruft sie über `MainViewModel.TabDragHost` auf. Die Leiste behält den Zeiger-Fang des Drückens, die Bewegungen kommen weiter bei ihr an, auch über dem neuen Fenster (Windows-Versuch unten).

**Tech-Stack:** .NET 10 mit Native AOT, Avalonia 12.1.4, `Avalonia.Headless.XUnit` und xunit.v3 (Microsoft.Testing.Platform), keine neuen Pakete.

**Spec:** [Designsystem](../specs/2026-10-17-designsystem.md) Abschnitt 7.2 (Tabs; wird in Task 6 nachgezogen), [Gesamt-Spec](../specs/2026-10-16-pagewing-design.md) Abschnitt 5. Roadmap: [Milestone 4.1](../../ROADMAP.md). Vorgänger: [Plan M4](2026-10-17-milestone-04-navigation.md), Eintrag „Tabs und Fenster: Tab besitzt seine Sitzung …“ in `Docs/DECISIONS.md`.

## Scope dieses Plans (am 2026-10-18 mit dem Projektinhaber abgestimmt)

- **Enthalten:** Herausziehen eines Tabs mit am Zeiger hängendem Fenster (Stil Chrome), Ablegen über der Tableiste eines Fensters mit Einfügemarke, Ziehen des einzigen Tabs eines Fensters (das Fenster wandert, Zusammenführen über einer fremden Leiste), Esc und Verlust des Zeiger-Fangs, Verhalten unter Windows (geprüft) und Linux/X11 (manuelle Abnahme), Hinweis zu Wayland.
- **Bewusst nicht enthalten:** Tabs per Drag-and-drop aus dem Programm heraus (Datei im Explorer ablegen o. Ä.), Docking/Andocken von Fenstern, gemerkte Fensterpositionen (M6, Sitzung wiederherstellen), neue UI-Texte (die Marke ist ein Strich ohne Text).
- Der Aufbau aus M4 (`TabViewModel` besitzt die Sitzung, `Workspace`, `MainViewModel.DetachTab`) trägt die Geste unverändert; die PDF wird nie neu geöffnet.

## Ergebnis des Versuchs auf Windows (Wegwerf-Code, nicht übernommen)

Simulierter Zug mit der echten Maus (3 Tabs, Debug-Build, `SetCursorPos` und `mouse_event`):
- Nach `DetachTab` und dem Anzeigen des neuen Fensters unter dem Zeiger behält `TabPanel` den Zeiger-Fang und bekommt jede weitere Bewegung (42 von 42), auch mit aktivierendem `Show()`; `Window.Position` je Bewegung setzen lässt das Fenster mitwandern; das Loslassen kommt bei der alten Leiste an.
- **Stolperstein:** `TabStrip.OnTabsChanged` beendet den Druck, sobald der gedrückte Tab die Liste verlässt, und gibt dabei den Fang frei (`EndPress` → `Pointer.Capture(null)`). Die Geste braucht einen eigenen Zustand „Tab ist gelöst“.
- Das Anlegen des Fensters kostete im Debug-Build rund 110 ms (kleiner Ruck beim Lösen); im Release-Build in Task 6 nachmessen.
- Nicht geprüft: Linux/X11, Wayland, Monitore mit unterschiedlicher Skalierung. Headless-Fenster berücksichtigen `Position` in `PointToScreen`/`PointToClient` (Probe), die Tests können mit Fensterpositionen arbeiten.

## Global Constraints

- .NET 10 (`net10.0`), Native AOT Pflicht; `TreatWarningsAsErrors`; AOT- und Trim-Warnungen sind Fehler; keine Reflection, kein XAML zur Laufzeit; Avalonia 12.1.4. Testnamen ohne Unterstriche (CA1707). Code und Kommentare Englisch; Doku und Commits Deutsch.
- „PDFium ist nicht thread-sicher“, der UI-Thread wartet nie auf PDFium: Die Geste fasst die Sitzungen nur über die vorhandenen Wege an (`MainViewModel.DetachTab`/`AttachTab`); kein neuer PDFium-Aufruf.
- **Nichts Neues im Startpfad:** Budgets bleiben (400 / 200 / 300 / 100 ms, Fehlschlag ab Budget + 25 %); `TabDragHost` ist nur eine Zuweisung beim Anlegen eines Fensters, `TabDrag` entsteht erst beim Herausziehen. Der Startzeit-Check der CI muss grün bleiben, Budgets nie lockern.
- Design: Tokens und Maße nur aus dem Designsystem; Zustände nie nur über Farbe (die Einfügemarke ist ein Strich an der Stelle, kein Farbwechsel); ein neues Farbpaar vor Verwendung auf WCAG 2.2 AA prüfen (Ränder und Icons 3:1). Keine neuen UI-Texte.
- Git: Branch `feature/m4-1-tabs-ziehen`. Gezielt stagen (`git add <datei>`), nach jedem Commit `git show --stat HEAD` gegen die erwartete Dateiliste prüfen, Conventional Commits auf Deutsch, Trailer `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`. Kein Force-Push, kein `--no-verify`. Ein PR nach `dev` am Ende; vor dem Merge alle CI-Checks grün. Die unversionierte Datei `Docs/design/pagewing-icons.zip` gehört nicht zur Aufgabe und wird nie gestaged.
- Roadmap: Status von 4.1 beim Start der Umsetzung `in-progress`; nach jedem Task die genannten Aufgaben abhaken, `dotnet run tools/roadmap.cs -- generate`, beide Dateien mit committen. Abnahmekriterien erst abhaken, wenn geprüft, mit Nachweis.
- „Fertig“ heißt: `dotnet build Pagewing.slnx`, `dotnet test Pagewing.slnx`, `dotnet format Pagewing.slnx --verify-no-changes`, `dotnet run tools/roadmap.cs -- check` nachweislich grün, dazu der Startzeit-Check in der CI. Einzelne Tests: `dotnet test tests/Pagewing.Core.Tests --filter-class "*TabDragMathTests"` (Microsoft.Testing.Platform), analog `--filter-method`.
- Wo eine Avalonia-API in diesem Plan anders heißt als in 12.1.4, gilt der Compiler; die Absicht des Schritts bleibt.

## Review Focus

Situationen, die weder Spec noch Roadmap nennen, die beim Ziehen aber vorkommen. Die Prüfung steht jeweils im genannten Task.

1. **Abbruch mitten im Zug (Esc, Zeiger-Fang weg, Fenster geschlossen):** Der Tab geht nie verloren und seine Sitzung wird weder doppelt noch gar nicht freigegeben; Esc stellt Platz und aktiven Tab her (Tasks 3, 5).
2. **Einziger Tab eines Fensters:** Ziehen verschiebt das Fenster, Esc stellt seine Position her, Loslassen über einer fremden Leiste führt zusammen und schließt das leere Fenster, auch wenn es das älteste ist; die App läuft weiter (Tasks 3, 4).
3. **Tab in jedem Zustand:** aktiv, inaktiv, ladend, mit Passwortabfrage, mit Fehlerkarte; die Sitzung bleibt dieselbe, der Zustand bleibt erhalten (Task 3).
4. **Ziel mit besonderer Leiste:** Fenster ohne Tab (Startbildschirm), Leiste mit Überlauf (nur der aktive Tab sichtbar), viele Tabs, Leisten, die sich überdecken (Task 4).
5. **Dieselbe Datei im Zielfenster schon offen:** erlaubt beim Ablegen (eine bewusste Handlung, anders als Öffnen); beide Tabs bleiben, jeder mit seiner Sitzung (Task 4).

## Entscheidungen dieses Plans (eigene sind markiert; sie bekommen einen Eintrag in `Docs/DECISIONS.md`, Task 6)

- **Fenster hängt beim Ziehen am Zeiger** (Entscheidung des Projektinhabers). Das neue Fenster erscheint, sobald der Zeiger mehr als `TabDragMath.DetachDistance` (40 DIP) senkrecht von der Zugangsposition abweicht oder das Fenster verlässt; es hat die Größe des Quellfensters und der Zeiger greift es dort, wo er den Tab gefasst hat. *Schwelle 40: eigene Entscheidung.*
- **Fokus bleibt im Quellfenster:** Das neue Fenster zeigt sich ohne Aktivierung (`ShowActivated = false`), sonst bekäme ein Esc-Tastendruck das neue Fenster, und die Leiste des Quellfensters, die den Zug führt, hörte ihn nicht. Aktiviert wird erst beim Loslassen. *Eigene Entscheidung.*
- **Über einer fremden Leiste wird das schwebende Fenster außerhalb des Bildschirms geparkt** (`TabDrag.ParkedPosition` = −32000, −32000), statt es zu verstecken; die Leiste zeigt eine Einfügemarke. Grund: Das Fenster des einzigen Tabs führt den Zeiger-Fang selbst, `Hide()` würde ihn beenden. Der Tab wandert erst beim Loslassen (kein „Live-Zusammenführen“). *Eigene Entscheidung.*
- **Ziel unter dem Zeiger:** Die Leiste (ganze Zeile, auch Zieh-Fläche und Knöpfe) jedes sichtbaren, nicht minimierten Fensters außer dem schwebenden; bei Überdeckung gewinnt das zuletzt aktivierte Fenster, danach das jüngste. Z-Ordnung des Betriebssystems wird nicht gelesen. Die Einfügeposition ist `TabDragMath.InsertionIndex`; bei Überlauf (nur der aktive Tab sichtbar) wird am Ende eingefügt. *Eigene Entscheidung.*
- **Einziger Tab:** Das Fenster selbst wandert (kein neues Fenster); Esc stellt seine Position her. Nach dem Zusammenführen wird das leere Fenster geschlossen, auch das älteste (das nächste übernimmt dessen Rolle, `Workspace.Forget`). *Eigene Entscheidung.*
- **Dieselbe Datei im Zielfenster:** Ablegen ist erlaubt und erzeugt zwei Tabs derselben Datei (die Regel „aktiviert den vorhandenen Tab“ gilt nur fürs Öffnen). *Eigene Entscheidung.*
- **Verlust des Zeiger-Fangs** (Alt+Tab, Fenster verliert den Fokus) endet wie ein Loslassen an der letzten Position, wie beim Umsortieren in M4. *Eigene Entscheidung.*
- **Wayland nur als Hinweis:** Wo ein Programm sein Fenster nicht selbst setzen darf, bleibt das Menü „In neuem Fenster öffnen“ der Weg; Avalonia 12.1 läuft unter Wayland in der Regel über XWayland, dort ist die Geste wie unter X11 zu erwarten. Nicht geprüft, in der DECISIONS-Notiz so festhalten. Monitore mit unterschiedlicher Skalierung: nicht geprüft.

## Dateistruktur

**Core (`src/Pagewing.Core`)**
- Create `Viewing/TabDragMath.cs` · Modify `Viewing/TabList.cs` (`Insert`)

**App (`src/Pagewing.App`)**
- Create `Services/ITabDragHost.cs` · `Services/TabDrag.cs`
- Modify `Services/Workspace.cs` (`ITabDragHost`, `Open`/`PlaceNextTo`/`MoveToNewWindow` mit Position und Aktivierung, Ziel-Suche) · `ViewModels/MainViewModel.cs` (`AttachTab(tab, index)`, `TabDropIndex`, `TabDragHost`) · `Views/MainWindow.axaml.cs` (`TabDropIndexAt`) · `Views/TabStrip.axaml(.cs)` (Geste, Marke, `DropIndexAt`)

**Tests**
- `tests/Pagewing.Core.Tests/Viewing/` (`TabListTests` erweitern, neu `TabDragMathTests`)
- `tests/Pagewing.App.Tests/` (`TabsTests` erweitern, neu `TabDragTests` für Tasks 3 und 4 und `TabDragGestureTests` für Task 5, `TokenTests` ggf. Paar ergänzen)

**Doku:** `Docs/DECISIONS.md`, `Docs/superpowers/specs/2026-10-17-designsystem.md` (7.2), `CLAUDE.md` (App-Shell), `CHANGELOG.md`, `Docs/roadmap.json` + `Docs/ROADMAP.md`

---

### Task 1: Core – `TabList<T>.Insert` und `TabDragMath`

**Files:**
- Create: `src/Pagewing.Core/Viewing/TabDragMath.cs`
- Modify: `src/Pagewing.Core/Viewing/TabList.cs`
- Test: `tests/Pagewing.Core.Tests/Viewing/TabListTests.cs`, `tests/Pagewing.Core.Tests/Viewing/TabDragMathTests.cs`

**Interfaces:**
- Produces: `public void TabList<T>.Insert(T item, int index)` (setzt den Tab an `index`, begrenzt auf 0 bis `Count`, aktiviert ihn, löst `CollectionChanged` mit `Add` und danach `ActiveChanged` aus, wie `Add`); `public static class TabDragMath` mit `public const double DetachDistance = 40;`, `public static bool ShouldDetach(double verticalDistance, bool pointerOutsideWindow)` (wahr bei `pointerOutsideWindow` oder `Math.Abs(verticalDistance) > DetachDistance`; NaN → falsch) und `public static int InsertionIndex(double x, double tabWidth, int tabCount)` (`Math.Clamp((int)Math.Floor((x / tabWidth) + 0.5), 0, tabCount)`; 0 bei `tabCount <= 0`, `tabWidth <= 0` oder NaN).

- [ ] **Step 1: Tests schreiben**

In `TabListTests`: `InsertPlacesTheTabAtTheIndexAndActivatesIt` (Liste [a, b, c], a aktiv; `Insert(d, 1)` → [a, d, b, c], `Active` ist d, Ereignisse in der Reihenfolge `Add(d, 1)`, `ActiveChanged`), `InsertClampsTheIndex` (`Insert(e, -5)` → Position 0; `Insert(f, 99)` → Ende), `InsertIntoAnEmptyListActivatesTheTab`.
In `TabDragMathTests` (Theory-Daten mit den genauen Werten): `ShouldDetach`: (0, false) → falsch · (40, false) → falsch · (40.1, false) → wahr · (−41, false) → wahr · (0, true) → wahr · (NaN, false) → falsch. `InsertionIndex(x, 100, 3)`: 0→0 · 49→0 · 50→1 · 149→1 · 150→2 · 250→3 · 10000→3 · −20→0; außerdem `(x, 100, 0)` → 0, `(x, 0, 3)` → 0, `(NaN, 100, 3)` → 0.

- [ ] **Step 2: Fehlschlag prüfen**

Run: `dotnet test tests/Pagewing.Core.Tests --filter-namespace "Pagewing.Core.Tests.Viewing"`
Expected: Build-Fehler (`TabDragMath`, `Insert` fehlen).

- [ ] **Step 3: Implementieren** wie unter Interfaces; `Insert` teilt sich den Rumpf mit `Add` (private Hilfsmethode mit dem Index), kein doppelter Code.

- [ ] **Step 4: Tests grün**

Run: `dotnet test tests/Pagewing.Core.Tests --filter-namespace "Pagewing.Core.Tests.Viewing"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Pagewing.Core/Viewing/TabDragMath.cs src/Pagewing.Core/Viewing/TabList.cs tests/Pagewing.Core.Tests/Viewing/TabListTests.cs tests/Pagewing.Core.Tests/Viewing/TabDragMathTests.cs
git commit -m "feat(core): Einfügen eines Tabs an einer Stelle und Rechnung fürs Tab-Ziehen"
```

---

### Task 2: `MainViewModel` – Einfügen an einer Stelle, Marke, Zieh-Host

**Files:**
- Modify: `src/Pagewing.App/ViewModels/MainViewModel.cs`
- Create: `src/Pagewing.App/Services/ITabDragHost.cs`
- Test: `tests/Pagewing.App.Tests/TabsTests.cs`

**Interfaces:**
- Consumes: `TabList<T>.Insert` (Task 1).
- Produces:
  - `internal void MainViewModel.AttachTab(TabViewModel tab, int index)` (wie `AttachTab(tab)`, aber an der Stelle `index`, begrenzt; ein schon enthaltener Tab ändert nichts); `AddTab` bekommt dafür den Parameter `int? index = null` (mit `index` wird `Tabs.Insert` statt `Tabs.Add` benutzt).
  - `internal int? MainViewModel.TabDropIndex { get; set; }` (löst `PropertyChanged` aus; `null` = keine Marke).
  - `internal ITabDragHost? MainViewModel.TabDragHost { get; set; }`.
  - `internal interface ITabDragHost { TabDrag? BeginTabDrag(MainViewModel source, TabViewModel tab, PixelPoint pointer, PixelPoint grip); }` – `pointer` ist die Zeigerposition auf dem Bildschirm, `grip` der Abstand des Zeigers vom Ursprung des Fenster-Inhalts in physischen Pixeln; die Typen `TabDrag` und `PixelPoint` (`Avalonia`) kommen aus Task 3 bzw. Avalonia, die Datei entsteht hier mit dem Namen und Rückgabetyp, `TabDrag` als leere `internal sealed class` in `Services/TabDrag.cs` bis Task 3.

- [ ] **Step 1: Tests schreiben** (in `TabsTests`, Fenster-VM ohne Ansicht wie dort üblich)

`AttachTabAtAnIndexInsertsAndActivatesIt` (Fenster mit [a, b]; ein Tab c aus einem zweiten `MainViewModel` über `DetachTab` gelöst, `AttachTab(c, 1)` → [a, c, b], c aktiv und `IsActive`, Sitzung unverändert), `AttachTabClampsTheIndex` (−3 → vorn, 99 → hinten), `TabDropIndexRaisesPropertyChanged` (Wert setzen löst genau ein `PropertyChanged("TabDropIndex")` aus, gleicher Wert keines).

- [ ] **Step 2: Fehlschlag prüfen**

Run: `dotnet test tests/Pagewing.App.Tests --filter-class "*TabsTests"`
Expected: Build-Fehler.

- [ ] **Step 3: Implementieren** wie unter Interfaces.

- [ ] **Step 4: Tests grün**

Run: `dotnet test tests/Pagewing.App.Tests --filter-class "*TabsTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Pagewing.App/ViewModels/MainViewModel.cs src/Pagewing.App/Services/ITabDragHost.cs src/Pagewing.App/Services/TabDrag.cs tests/Pagewing.App.Tests/TabsTests.cs
git commit -m "feat(app): Tab an einer Stelle einfügen, Einfügemarke und Zieh-Host im Fenster-ViewModel"
```

---

### Task 3: `Workspace` und `TabDrag` – Fenster folgt dem Zeiger, Abbrechen, Loslassen ohne Ziel

**Files:**
- Modify: `src/Pagewing.App/Services/Workspace.cs`
- Create (ausfüllen): `src/Pagewing.App/Services/TabDrag.cs`
- Test: `tests/Pagewing.App.Tests/TabDragTests.cs` (neu; Hilfsmethoden `CreateWorkspace`, `OpenReadyAsync`, `Settle`, `WaitUntilAsync` nach dem Muster von `WorkspaceTests`)

**Interfaces:**
- Consumes: `ITabDragHost`, `MainViewModel.AttachTab(tab, index)`, `DetachTab` (Task 2).
- Produces:
  - `Workspace : ITabDragHost`; `public TabDrag? BeginTabDrag(MainViewModel source, TabViewModel tab, PixelPoint pointer, PixelPoint grip)`: `null`, wenn `source` nicht zu diesem Workspace gehört, `tab` nicht in `source.Tabs` ist oder schon ein Zug läuft. Mit mindestens zwei Tabs: der Tab wandert über `source.DetachTab` in ein neues Fenster (Größe des Quellfensters, `ShowActivated = false`), dessen Position `pointer − grab` ist, mit `grab = (Ursprung des Inhalts − Position des Quellfensters) + grip` (`source.PointToScreen(default) − source.Position`, damit der Zeiger auf Windows und Linux am selben Punkt des Tabs bleibt). Mit genau einem Tab: kein neues Fenster, das Fenster selbst folgt dem Zeiger, `grab = pointer − Position`. `Workspace.Open` bekommt dafür `PixelPoint? position = null, bool showActivated = true`; `PlaceNextTo(Window, Window)` nimmt optional die Position; `MoveToNewWindow` bleibt im Verhalten gleich (ruft `Open` mit den Vorgaben) und `TabDragHost` jedes Fensters wird in `Open` auf den Workspace gesetzt.
  - `internal sealed class TabDrag` mit `public MainViewModel Floating { get; }`, `public bool MovesWholeWindow { get; }`, `public bool IsEnded { get; }`, `public bool IsParked { get; }` (Task 4), `public static readonly PixelPoint ParkedPosition = new(-32000, -32000)`, `public void MoveTo(PixelPoint pointer)` (setzt `Floating`-Fenster auf `pointer − grab`), `public void Complete(PixelPoint pointer)`, `public void Cancel()`. Alle drei tun nach `IsEnded` oder bei einem schon geschlossenen Fenster nichts und werfen nie. `Complete` ohne Ziel: Fenster bleibt, wird aktiviert (`Activate()`), `IsEnded`. `Cancel`: mit neuem Fenster `Floating.DetachTab(tab)`, `source.AttachTab(tab, ursprünglicherIndex)` (der Tab ist danach wieder aktiv), neues Fenster schließen (`Dispatcher.UIThread.Post(window.Close)`); beim ganzen Fenster die ursprüngliche Position wiederherstellen.

- [ ] **Step 1: Tests schreiben** (alle in `TabDragTests`; Zeigerpositionen auf dem Bildschirm, Fenster mit `Position` ≠ (0, 0); ohne Aktivierung wie in `WorkspaceTests`)

- `BeginningADragWithTwoTabsOpensAFloatingWindowAtThePointer`: Fenster mit [a, b], `BeginTabDrag(first, b, new PixelPoint(500, 300), new PixelPoint(60, 16))`; erwartet: 2 Fenster, `Floating.Tabs` ist [b] mit derselben Sitzung und demselben Dokument (`Ready`, kein neuer Öffnen-Aufruf: `_opens` unverändert), Größe wie das Quellfenster, `Position == (440, 284)` (im Headless ist Inhalt = Fenster-Ursprung), `!MovesWholeWindow`.
- `MovingTheDragMovesTheFloatingWindowWithThePointer`: danach `MoveTo(600, 350)` → `Position == (540, 334)`.
- `BeginningADragWithOneTabMovesTheWholeWindow`: ein Tab, Fenster bei (100, 50), `BeginTabDrag(.., pointer (160, 66), grip beliebig)`; kein neues Fenster, `MovesWholeWindow`, `MoveTo(260, 166)` → `Position == (200, 150)`.
- `CancelPutsTheTabBackAtItsPlace`: [a, b, c], Zug von b (Index 1) und `MoveTo`, `Cancel` → Quelle [a, b, c], b aktiv und `IsActive`, nach `Settle` nur noch ein Fenster, Sitzung von b `Ready` und dieselbe.
- `CancelPutsAMovedWindowBack`: ganzes Fenster nach `MoveTo`, `Cancel` → ursprüngliche `Position`.
- `CompleteWithoutATargetKeepsTheNewWindowAndActivatesIt`: `Complete(p)` → 2 Fenster, `Floating` hat den Tab, `workspace.ActiveWindow` ist `Floating` (nach `Settle`), `IsEnded`.
- `BeginningADragForAnUnknownTabDoesNothing`: Tab nicht in der Quelle, Quelle nicht im Workspace, und ein zweiter Zug, solange einer läuft → `null`.
- `ADragSurvivesAFloatingWindowThatIsClosed` (Review Focus 1): Floating-Fenster mitten im Zug schließen; `MoveTo`, `Complete`, `Cancel` werfen nie, `Windows.Count == 1`, die Sitzung des Tabs ist mit dem Fenster freigegeben (`Empty`), kein zweites Freigeben (kein Fehler beim Dispose des Workspaces).
- `AnyTabStateCanBeDragged` (Theory, Review Focus 3): Tab mit Passwortabfrage (`encrypted_hello_world_r3.pdf`), mit Fehlerkarte (`not-a-pdf.pdf`) und ein aktiver, fertig geladener; nach `BeginTabDrag` und `Complete` im neuen Fenster mit unverändertem `State` und derselben Sitzung.

- [ ] **Step 2: Fehlschlag prüfen**

Run: `dotnet test tests/Pagewing.App.Tests --filter-class "*TabDragTests"`
Expected: Build-Fehler (`BeginTabDrag` fehlt).

- [ ] **Step 3: `Workspace.Open`, `PlaceNextTo` und `MoveToNewWindow` erweitern, `BeginTabDrag` und `TabDrag` implementieren** wie unter Interfaces. `MoveToNewWindow` und `BeginTabDrag` teilen sich das Lösen des Tabs (`DetachTab`, dann `Open` mit dem Tab); kein zweiter Weg daneben. Einen laufenden Zug merkt sich der Workspace (`_activeDrag`), ein Zug endet mit `Complete` oder `Cancel` und gibt ihn frei.

- [ ] **Step 4: Tests grün, vorhandene Workspace-Tests unverändert grün**

Run: `dotnet test tests/Pagewing.App.Tests --filter-class "*TabDragTests" --filter-class "*WorkspaceTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Pagewing.App/Services/Workspace.cs src/Pagewing.App/Services/TabDrag.cs tests/Pagewing.App.Tests/TabDragTests.cs
git commit -m "feat(app): Fenster folgt beim Tab-Ziehen dem Zeiger (Workspace.BeginTabDrag, TabDrag)"
```

---

### Task 4: Ziel unter dem Zeiger – Einfügemarke, Parken, Zusammenführen

**Files:**
- Modify: `src/Pagewing.App/Services/Workspace.cs`, `src/Pagewing.App/Services/TabDrag.cs`, `src/Pagewing.App/Views/MainWindow.axaml.cs`, `src/Pagewing.App/Views/TabStrip.axaml.cs`
- Test: `tests/Pagewing.App.Tests/TabDragTests.cs`

**Interfaces:**
- Consumes: `TabDrag` (Task 3), `TabDragMath.InsertionIndex` (Task 1), `MainViewModel.TabDropIndex`, `AttachTab(tab, index)` (Task 2).
- Produces:
  - `internal int? TabStrip.DropIndexAt(PixelPoint screen)`: `null`, wenn der Punkt nicht in der Leiste liegt (ganze `TabStrip`-Fläche inklusive Zieh-Fläche und Knöpfen) oder sie keinem Fenster-VM gehört; sonst die Einfügeposition: ohne Tabs 0, bei `TabPanel.ShowOnlyActive` `Tabs.Count`, sonst `TabDragMath.InsertionIndex(TabPanel.PointToClient(screen).X, TabPanel.TabWidth, Tabs.Count)`.
  - `internal int? MainWindow.TabDropIndexAt(PixelPoint screen)`: reicht an die `TabStrip` des Fensters weiter (einmal über `GetVisualDescendants` gefunden und gemerkt); `null` bei unsichtbarem oder minimiertem Fenster.
  - In `TabDrag`: `MoveTo` sucht das Ziel (Fenster außer `Floating`, zuerst `ActiveWindow`, dann die übrigen vom jüngsten zum ältesten, erstes mit `TabDropIndexAt != null`), setzt `TabDropIndex` des Ziels und löscht es bei allen anderen; über einem Ziel parkt `Floating` bei `ParkedPosition` (`IsParked`), sonst folgt es dem Zeiger. `Complete` mit Ziel: `Floating.DetachTab(tab)`, `target.AttachTab(tab, index)`, `TabDropIndex` löschen, das leere schwebende Fenster per `Dispatcher.UIThread.Post(window.Close)` schließen, Zielfenster aktivieren, `IsEnded`. `Cancel` löscht alle Marken. Marke und Parken enden auch bei `Complete` ohne Ziel und bei `Cancel`.

- [ ] **Step 1: Tests schreiben** (in `TabDragTests`; Fenster A, B mit eigenen `Position`en, die Zeigerposition über B als `B.PointToScreen(Mitte der Leiste)` berechnet)

- `HoveringAnotherStripShowsTheMarkerAndParksTheFloatingWindow`: Zug von a2 (A hat [a1, a2], B hat [b1, b2]); `MoveTo` über der rechten Hälfte von B → `B.TabDropIndex == 2`, `A.TabDropIndex == null`, `drag.IsParked`, `Floating`-Position gleich `TabDrag.ParkedPosition`; `MoveTo` weg von jeder Leiste → `B.TabDropIndex == null`, `!IsParked`, Fenster folgt wieder.
- `DroppingOnAStripMovesTheTabWithItsSession`: `Complete` über B zwischen den Tabs → B [b1, a2, b2], a2 aktiv und `IsActive`, Sitzung und Dokument unverändert, `_opens` unverändert, nach `Settle` nur A und B offen, `B.TabDropIndex == null`.
- `DroppingBackOnTheOwnStripPutsTheTabAtTheChosenPlace`: [a1, a2, a3], a2 über den linken Rand der eigenen Leiste → [a2, a1, a3], nur ein Fenster.
- `DroppingTheOnlyTabOfAWindowMergesAndClosesTheWindow` (Review Focus 2): zweites Fenster mit einem Tab über die Leiste des ersten → erstes hat den Tab, zweites geschlossen; außerdem das **älteste** Fenster mit einem Tab über das zweite: ältestes geschlossen, das zweite ist nun das älteste (`CloseWindowWhenEmpty == false`), `workspace.Windows` hat ein Fenster, App läuft.
- `DroppingOnAWindowWithoutTabsOpensTheTabThere` (Review Focus 4): Fenster ohne Tab (`OpenWindow()` ohne Datei, Startbildschirm), Zeiger über der Titelzeile → Index 0, Tab dort, einziger und aktiv.
- `AStripWithOverflowTakesTheTabAtTheEnd`: Ziel mit Breite 640 und 5 Tabs (`ShowOnlyActive`) → `TabDropIndex == 5`, nach `Complete` ist der Tab Nummer 6.
- `OverlappingStripsPreferTheActiveWindow`: beide Fenster an derselben Position, A zuletzt aktiviert → Ziel ist A; nach `B.Activate()` ist es B.
- `DroppingATabWhoseFileIsAlreadyOpenInTheTargetKeepsBoth` (Review Focus 5): Ziel hat dieselbe Datei offen → 2 Tabs derselben Datei, zwei verschiedene Sitzungen.
- `NoTargetForAHiddenOrMinimizedWindow`: Fenster `Hide()` bzw. `WindowState = Minimized` → `TabDropIndexAt == null`.

- [ ] **Step 2: Fehlschlag prüfen**

Run: `dotnet test tests/Pagewing.App.Tests --filter-class "*TabDragTests"`
Expected: FAIL (die neuen Tests; Parken und Marken fehlen).

- [ ] **Step 3: Implementieren** wie unter Interfaces; `Workspace` stellt `TabDrag` die Fensterliste und `WindowOf` zur Verfügung (beides schon da), die Suche steht in `Workspace` (z. B. `internal (MainViewModel Target, int Index)? FindDropTarget(PixelPoint pointer, MainViewModel floating)`).

- [ ] **Step 4: Tests grün**

Run: `dotnet test tests/Pagewing.App.Tests --filter-class "*TabDragTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Pagewing.App/Services/Workspace.cs src/Pagewing.App/Services/TabDrag.cs src/Pagewing.App/Views/MainWindow.axaml.cs src/Pagewing.App/Views/TabStrip.axaml.cs tests/Pagewing.App.Tests/TabDragTests.cs
git commit -m "feat(app): Tabs über der Leiste eines Fensters ablegen (Einfügeposition, Parken, Zusammenführen)"
```

---

### Task 5: Geste in der `TabStrip` und Einfügemarke

**Files:**
- Modify: `src/Pagewing.App/Views/TabStrip.axaml`, `src/Pagewing.App/Views/TabStrip.axaml.cs`
- Test: `tests/Pagewing.App.Tests/TabDragGestureTests.cs` (neu), `tests/Pagewing.App.Tests/TabStripTests.cs` (Kommentar zu „Up and down … Milestone 4“ auf „ohne Workspace“ ändern, Verhalten bleibt), `tests/Pagewing.App.Tests/TokenTests.cs` (Paar `Accent.Default` auf `Surface.Window` für Ränder, 3:1, ergänzen, falls die Paarliste es nicht schon abdeckt)

**Interfaces:**
- Consumes: `MainViewModel.TabDragHost`, `ITabDragHost.BeginTabDrag`, `TabDrag.MoveTo/Complete/Cancel/IsEnded`, `TabDragMath.ShouldDetach` (Tasks 1 bis 4), `MainViewModel.TabDropIndex`.
- Produces: Das Verhalten der Leiste. Ein Element `DropMarker` (Name) in `TabStrip.axaml`: `Border` 2 DIP breit, Höhe der Leiste abzüglich je 4 DIP oben und unten, `Background="{DynamicResource Pw.Brush.Accent.Default}"`, `IsHitTestVisible="False"`, `IsVisible="False"`, über der `DockPanel` (Wurzel wird ein `Grid`). Sichtbar, solange `MainViewModel.TabDropIndex` gesetzt ist, `Margin.Left` = `(ShowOnlyActive ? 1 : TabDropIndex) * TabPanel.TabWidth − 1`; `TabStrip` hört dafür auf `PropertyChanged` des Fenster-VMs (in `Connect()` an- und abmelden, wie bei `Tabs.CollectionChanged`).

Verhalten der Geste (in `TabStrip.axaml.cs`):
- Der Zug beginnt, sobald der Zeiger `DragThreshold` (6 DIP) in X **oder** Y von der Zugangsposition abweicht (bisher nur X). Bei `TabPanel.ShowOnlyActive` beginnt der Zug auch, es wird aber nie umsortiert (`DragTo` entfällt), Herausziehen geht.
- Mit `_viewModel.TabDragHost != null` und `TabDragMath.ShouldDetach(Y − Zugangs-Y, Zeiger außerhalb der Fensterfläche)`: `host.BeginTabDrag(_viewModel, tab, screenPointer, grip)` mit `screenPointer = TopLevel.PointToScreen(Zeigerposition im TopLevel)` und `grip = ((Leistenanfang im TopLevel + Math.Min(Zugangs-X im Tab, TabStripLayout.MinTabWidth)) × RenderScaling, Zeiger-Y im TopLevel × RenderScaling)`. Bei Erfolg: `_dragging = false`, `SetDragItem(null)`, der Zug (`_drag`) übernimmt; **`OnTabsChanged` beendet den Druck dann nicht** (Stolperstein), und `Sync()` legt für den gelösten Tab keine Ansicht an (Schutz bei `_drag != null`). Jede weitere Bewegung ruft `_drag.MoveTo(screenPointer)`; Loslassen `_drag.Complete(screenPointer)`; Esc `_drag.Cancel()`; Verlust des Zeiger-Fangs und `Connect()` (die Leiste verlässt das Fenster) `_drag.Complete(letzte Position)`. Danach wie bisher `EndPress`, der den Fang freigibt.

- [ ] **Step 1: Tests schreiben** (`TabDragGestureTests`, mit `Workspace` und echten Mausereignissen auf das Quellfenster: `MouseDown`/`MouseMove`/`MouseUp` mit `RawInputModifiers.LeftMouseButton`, Punkte im Fenster; Hilfen wie in `TabStripTests`)

- `PullingATabDownOutOfTheStripOpensAWindowThatFollowsThePointer`: Drücken auf einen von drei Tabs; Bewegung 10 DIP nach unten → kein neues Fenster; 41 DIP → zwei Fenster, der Tab im neuen; weitere Bewegung um (+200, +60) → das neue Fenster verschiebt sich um genau dieses Delta (Vergleich der `Position` vor und nach); Loslassen → beide Fenster bleiben, Tab im neuen Fenster, aktiv, Quellfenster hat die anderen Tabs.
- `PullingBelowTheThresholdOnlyReorders`: Bewegung 39 DIP senkrecht plus 1,2 Tabbreiten seitlich → kein neues Fenster, Reihenfolge geändert (Verhalten aus M4 bleibt).
- `ReleasingOverAnotherWindowsStripInsertsTheTab`: Fenster B bei anderer `Position`, Zeiger (in Koordinaten des Quellfensters: `A.PointToClient(B.PointToScreen(…))`) über B → `B.TabDropIndex` gesetzt, `DropMarker` in B sichtbar mit `Bounds.X`-Position `Index × TabWidth − 1`, nach dem Loslassen Tab in B, Marke weg, schwebendes Fenster zu.
- `TheMarkerIsAStripOfTwoPixelsAndHiddenOtherwise`: Breite 2, ohne Hover unsichtbar; Farbe ist `Pw.Brush.Accent.Default` beider Themes (Light/Dark).
- `EscapeDuringAFloatingDragPutsTheTabBack`: nach dem Lösen `KeyPress(Key.Escape)` auf das Quellfenster → Tab am alten Platz, aktiv, ein Fenster, Rest der Geste wirkungslos.
- `DraggingTheOnlyTabMovesTheWindow`: ein Tab, 41 DIP nach unten und weiter → kein neues Fenster, `Position` wandert mit; Loslassen über einer fremden Leiste führt zusammen.
- `PullingFromAStripWithOnlyTheActiveTabShown`: Fenster 640 breit mit 5 Tabs → Herausziehen geht, Umsortieren bleibt aus.
- `ReleaseAfterTheTabWasClosedElsewhereDoesNotThrow`: Tab aus einer anderen Quelle über `CloseTabCommand` während des Zugs geschlossen (Review Focus 1) → kein Fehler, der Zug endet.

- [ ] **Step 2: Fehlschlag prüfen**

Run: `dotnet test tests/Pagewing.App.Tests --filter-class "*TabDragGestureTests"`
Expected: FAIL.

- [ ] **Step 3: Geste und Marke implementieren** wie oben; Kommentar der Klasse („Dragging out of the strip does nothing in Milestone 4“) auf das neue Verhalten ändern.

- [ ] **Step 4: Alles Betroffene grün**

Run: `dotnet test tests/Pagewing.App.Tests --filter-class "*TabDragGestureTests" --filter-class "*TabStripTests" --filter-class "*TabDragTests" --filter-class "*WorkspaceTests" --filter-class "*TokenTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Pagewing.App/Views/TabStrip.axaml src/Pagewing.App/Views/TabStrip.axaml.cs tests/Pagewing.App.Tests/TabDragGestureTests.cs tests/Pagewing.App.Tests/TabStripTests.cs tests/Pagewing.App.Tests/TokenTests.cs
git commit -m "feat(app): Tab aus der Leiste ziehen, Einfügemarke in der Zielleiste"
```

---

### Task 6: Prüfung auf Windows, Doku, Roadmap, Abschluss

**Files:**
- Modify: `Docs/DECISIONS.md`, `Docs/superpowers/specs/2026-10-17-designsystem.md` (7.2), `CLAUDE.md`, `CHANGELOG.md` (`[Unreleased]`), `Docs/roadmap.json`, `Docs/ROADMAP.md` (nur erzeugt)

- [ ] **Step 1: Release-Build und Prüfung auf Windows mit echter Maus**

Run: `dotnet build src/Pagewing.App -c Release`, dann die App mit drei PDFs starten und die Geste mit einem Skript fahren, das `SetCursorPos` und `mouse_event` benutzt (angemeldete Desktop-Sitzung, DPI-bewusst, Maustaste im `finally` immer loslassen; Muster wie im Versuch). Zu prüfen und mit Zahlen zu notieren: (1) Lösen und Mitwandern (Fenster unter dem Zeiger, Bewegungen kommen an), Zeit vom Überschreiten der Schwelle bis zum sichtbaren Fenster (Release-Build); (2) Parken und Marke über der Leiste eines zweiten Fensters, Ablegen mit Einfügeposition; (3) einziger Tab: Fenster wandert, Zusammenführen; (4) Esc; (5) Alt+Tab mitten im Zug (Verlust des Fangs endet wie Loslassen). Bei einem Fehlschlag (Fang verloren, Parken wirkt nicht): anhalten und dem Projektinhaber melden, nicht umgehen.

- [ ] **Step 2: Doku und Roadmap**

- `Docs/DECISIONS.md`: Eintrag „2026-10-18 – Tabs ziehen: Fenster hängt am Zeiger, Ablegen auf fremder Leiste (M4.1)“ mit Kontext, Entscheidung (Punkte aus „Entscheidungen dieses Plans“, Versuchsergebnis, Grenzen: Linux/X11/Wayland und gemischte Skalierung nicht geprüft), Begründung, verworfene Alternativen (Geisterbild und Fenster erst beim Loslassen; Betriebssystem-Drag-and-Drop über `DragDrop.DoDragDrop`: kein Einfluss auf das Fenster am Zeiger, Verhalten am Rand unklar; `BeginMoveDrag`: liefert während des Zugs keine Mausereignisse; `Hide()` statt Parken: beendet beim einzigen Tab den Fang).
- Designsystem 7.2: Zeile zum Ziehen (Zeile mit „Das Herausziehen … folgt in M4.1“) durch das neue Verhalten ersetzen (Schwelle 40 DIP, Fenster am Zeiger, Marke, Esc, einziger Tab).
- `CLAUDE.md`, Abschnitt App-Shell: Stichpunkt „Tab-Ziehen (seit M4.1)“ mit `TabDrag`, `ITabDragHost`, `TabDragMath`, dem Parken und dem Hinweis, dass die Tableiste den Zeiger-Fang hält.
- `CHANGELOG.md` unter `[Unreleased]`: „Tabs lassen sich aus der Leiste ziehen und in andere Fenster ablegen“.
- `Docs/roadmap.json`: Milestone 4.1 `status` `planned` → `in-progress` beim Start, Aufgaben abhaken mit den Ergebnissen; Abnahme „Headless-Tests …“ mit den Testnamen als Nachweis; Abnahme „Manuelle Prüfung auf Windows und Linux/X11“ aufteilen in „… auf Windows“ (Nachweis: Zahlen aus Step 1) und „… auf Linux/X11“ (offen, bis der Projektinhaber sie macht). Dann `dotnet run tools/roadmap.cs -- generate`.

- [ ] **Step 3: Gesamtprüfung**

Run: `dotnet build Pagewing.slnx`, `dotnet test Pagewing.slnx`, `dotnet format Pagewing.slnx --verify-no-changes`, `dotnet run tools/roadmap.cs -- check`
Expected: alles grün. Befehl und Ergebnis in der PR-Beschreibung nennen.

- [ ] **Step 4: Commit und PR nach `dev`**

```bash
git add Docs/DECISIONS.md Docs/superpowers/specs/2026-10-17-designsystem.md CLAUDE.md CHANGELOG.md Docs/roadmap.json Docs/ROADMAP.md
git commit -m "docs: Tab-Ziehen (Milestone 4.1) – Entscheidungen, Design-Spec, Roadmap"
```

Dann pushen, PR nach `dev`, auf grüne CI warten (inklusive Startzeit-Check), erst dann mergen. Offen bleibt bis zur Prüfung des Projektinhabers: Linux/X11 (und Wayland), im PR und in der Roadmap als offen vermerkt; Status `done` erst, wenn auch diese Abnahme belegt ist, sonst Entscheidung mit dem Projektinhaber.
