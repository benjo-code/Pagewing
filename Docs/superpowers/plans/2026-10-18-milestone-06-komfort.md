# Milestone 6 – Komfort – Implementierungsplan

> **Für agentische Ausführer:** Pflicht-Skill: `superpowers:subagent-driven-development` (empfohlen) oder `superpowers:executing-plans`. Die Schritte nutzen Checkbox-Syntax (`- [ ]`).

**Ziel:** Pagewing lässt sich weitgehend per Tastatur bedienen (zentraler Befehlskatalog, Tastenkürzel, Hilfe, Befehlspalette Strg+K) und merkt sich Einstellungen, zuletzt geöffnete Dateien und die Position je Datei; dazu ein dunkler Modus mit **dunkler Seitendarstellung, die Bilder nicht invertiert**, die Dichte „Kompakt“, „Animationen reduzieren“ und eine Hochkontrast-Palette unter Windows.

**Architektur:** Zustand, der einen Neustart überleben muss, liegt UI-frei in `Pagewing.Core.Settings` (`AppSettings`, `RecentFiles`, je ein `…Store` mit atomarem Schreiben, JSON nur über Source-Generator). Die App hält ihn in einem `UserSettings`-Objekt, das beim Start vor dem ersten Frame gelesen wird (klein, ohne Datei gar nichts) und die Darstellung setzt (`Appearance`: Theme, Dichte, Bewegung, Hochkontrast). Alle Befehle, Tastenkürzel, Palette und Hilfe speisen sich aus **einem** `CommandCatalog`. Die dunkle Seite ist ein `PageTheme` in den `RenderOptions`; wie PDFium sie malt (Farbschema oder Helligkeitsumkehr), entscheidet ein Versuch in Task 1 mit dem Projektinhaber.

**Tech-Stack:** .NET 10 mit Native AOT, Avalonia 12.1.4, PDFium 157.0.8100 über eigene `[LibraryImport]`-Bindings, `System.Text.Json` mit Source-Generator, xunit.v3, Avalonia.Headless. Keine neuen Pakete.

**Spec:** [Designsystem](../specs/2026-10-17-designsystem.md) Abschnitte 2.7, 5.1, 5.4, 5.6, 6, 7.1, 7.3, 7.5, 7.6, 7.9, 9 (werden in Task 14 nachgezogen); [Gesamt-Spec](../specs/2026-10-16-pagewing-design.md). Roadmap: [Milestone 6](../../ROADMAP.md). Vorgänger: [Plan M5](2026-10-18-milestone-05-text-suche.md).

## Scope dieses Plans (am 2026-10-18 mit dem Projektinhaber abgestimmt)

- **Enthalten:** alle sechs Aufgaben der Roadmap (dunkler Modus mit dunkler Seite, Tastenkürzel + Hilfe, Befehlspalette, zuletzt geöffnet, Einstellungen, Hochkontrast) und aus dem Backlog: Tastenkürzel für die Panels, Automation-Zustände für Tabs und Aktionsleiste, Panel-Zustand speichern (Spec 7.3, „ab M6“). **Dazu, weil es die Wettbewerber nicht oder nur lückenhaft haben:** Pagewing merkt sich Seite, Zoom und Layout **je Datei**; die Palette findet auch Inhaltsverzeichnis-Einträge und offene Tabs.
- **Bewusst nicht enthalten (Backlog bleibt):** Sitzung wiederherstellen, Einstellung „Hardwarebeschleunigung“, frei belegbare Tastenkürzel (der Katalog macht es später leicht), F6-Bereichswechsel (Spec 7.9; eigene Fokus-Arbeit, kein Viewer-Befehl), Sprachwechsel zur Laufzeit (die UI-Texte sind `{x:Static}`; ein Wechsel gilt ab dem nächsten Start).
- Der Milestone ist groß. Teil A (Tasks 1 bis 8: Grundlagen, dunkle Seite, Darstellung) und Teil B (Tasks 9 bis 13: Bedienung) sind unabhängig voneinander lieferbar; reicht die Zeit nicht, wird B als M6.1 abgespalten (Roadmap-Scope-Änderung).

## Global Constraints

- .NET 10 (`net10.0`), Native AOT Pflicht; `TreatWarningsAsErrors`; AOT- und Trim-Warnungen sind Fehler; keine Reflection, JSON nur mit Source-Generator; `IsAotCompatible` je Projekt; Avalonia 12.1.4. Testnamen ohne Unterstriche (CA1707). Code und Kommentare Englisch; Doku, UI-Texte (de/en per resx) und Commits Deutsch bzw. beide Sprachen.
- PDFium nur in `Pagewing.Pdfium` und nur auf dem `PdfiumThread`; der UI-Thread wartet nie auf PDFium. Konstanten und Signaturen gegen die Header prüfen (`~/.nuget/packages/bblanchon.pdfium.win32/157.0.8100/build/native/include/pdfium/fpdfview.h`, `fpdf_progressive.h`); `NativeBindingsTests` ergänzen, wo Konstanten dazukommen.
- **Nichts Neues im Startpfad ohne Messung.** Budgets laut Roadmap „Geschwindigkeitsziele“ bleiben, Fehlschlag ab Budget + 25 %, nie lockern. Einstellungen werden vor dem ersten Frame gelesen: erlaubt ist eine kleine Datei und nur wenn sie existiert; Verlauf (`recent.json`), Palette, Einstellungs- und Hilfekarte entstehen erst bei Bedarf (nach dem ersten scharfen Seitenbild bzw. beim Öffnen). Task 5 und Task 14 messen mit `bench/startup`; der Warm-Median liegt auf dem Entwicklungsrechner schon an der Grenze, jede Zunahme über 3 ms wird untersucht, nicht hingenommen.
- Design: Tokens und Maße nur aus dem Designsystem; jede Änderung daran steht in Task 14 in der Spec und in `Docs/DECISIONS.md`. Neue Farbpaare vor Verwendung auf WCAG 2.2 AA prüfen (Text 4,5:1, Ränder/Icons/Fokus 3:1; Hochkontrast 7:1) mit dem Helfer `tests/Pagewing.App.Tests/Wcag.cs`. Gelb nur für Suchtreffer. Zustände nie nur über Farbe. Panels, Karten und Popups bauen ihren Inhalt erst beim Öffnen und bringen ihre Styles in der eigenen `.axaml` mit. `KeyBindings` des Fensters greifen vor jedem Key-Event; ein Feld, das eine Taste braucht (Suchfeld, Palette), beansprucht sie mit eigenen `KeyBindings`.
- Datenschutz und Sicherheit: nur lokale Dateien im Benutzerordner (Windows `%APPDATA%\Pagewing`, Linux `$XDG_CONFIG_HOME/pagewing` bzw. `~/.config/pagewing`), keine Netzwerkzugriffe, keine Telemetrie. Schreiben immer atomar (temporäre Datei im Zielordner, dann ersetzen); ein Fehler beim Lesen oder Schreiben darf nie die App beenden oder blockieren. Tests der App und des Kerns schreiben nie in den echten Benutzerordner (immer ein temporärer Ordner oder `UserSettings.Detached`).
- Git: Branch `feature/m06-komfort` (liegt an). Gezielt stagen (`git add <datei>`), nach jedem Commit `git show --stat HEAD` gegen die erwartete Dateiliste prüfen, Conventional Commits auf Deutsch, Trailer `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`. Kein Force-Push, kein `--no-verify`. Ein PR nach `dev` am Ende; vor dem Merge alle CI-Checks grün. Die unversionierte Datei `Docs/design/pagewing-icons.zip` gehört nicht zur Aufgabe und wird nie gestaged.
- Roadmap: Status von M6 beim Start der Umsetzung `in-progress`; nach jedem Task die genannten Aufgaben abhaken (`Docs/roadmap.json`), `dotnet run tools/roadmap.cs -- generate`, beide Dateien mit committen. Abnahmekriterien erst abhaken, wenn geprüft, mit Nachweis.
- „Fertig“ heißt: `dotnet build Pagewing.slnx`, `dotnet test Pagewing.slnx`, `dotnet format Pagewing.slnx --verify-no-changes`, `dotnet run tools/roadmap.cs -- check` nachweislich grün, dazu der Startzeit-Check in der CI. Einzelne Tests: `dotnet test tests/Pagewing.App.Tests --filter-class "*CommandCatalogTests"` (Microsoft.Testing.Platform), analog `--filter-method` und für `tests/Pagewing.Core.Tests`. Wo eine Avalonia- oder PDFium-API anders heißt als hier, gilt der Compiler bzw. der Header; die Absicht des Schritts bleibt.

## Review Focus

Eingaben und Zustände, die weder Spec noch Roadmap nennen, die aber vorkommen. Die Prüfung steht jeweils im genannten Task.

1. **Kaputte, leere, zu neue oder nicht schreibbare Dateien** (`settings.json`, `recent.json`: abgeschnittenes JSON, falscher Typ, unbekannte Enum-Werte, `Version` 99, Ordner schreibgeschützt, Datei von zwei Instanzen gleichzeitig): die App startet mit Standardwerten, wirft nie, überschreibt eine unlesbare Datei erst nach einer Sicherung (`<name>.bad`), und Werte außerhalb des Bereichs (Zoom 0, Panelbreite 5000) werden begrenzt (Tasks 2, 3, 5, 12).
2. **Verlaufseinträge, deren Datei fehlt, umbenannt wurde oder auf einem nicht erreichbaren Netzlaufwerk liegt:** Start, Startseite und Palette prüfen **nie** synchron, ob die Datei existiert (ein Netzpfad kann Sekunden blockieren); der Eintrag erscheint und führt beim Öffnen zur bekannten Fehlerkarte; „Aus dem Verlauf entfernen“ und „Verlauf löschen“ gibt es (Tasks 12, 10).
3. **Dunkle Seite und alles, was auf der Seite liegt:** Suchtreffer (heute mit der Seite multipliziert, auf dunkel unsichtbar), Textauswahl, Platzhalter, Seitenschatten, Miniaturen und Vorschau müssen lesbar bleiben; ein Wechsel hell/dunkel mitten im Rendern lässt nie eine Kachel des alten Themes im Cache oder auf dem Bildschirm zurück (Tasks 4, 6).
4. **Befehlspalette per Tastatur und in Randfällen:** Strg+K ohne offenen Tab, leere Eingabe, keine Treffer, eine Zahl größer als die Seitenzahl, ein Inhaltsverzeichnis mit 10 000 Einträgen (höchstens 50 Zeilen, Oberfläche bleibt flüssig), Esc/Fokus kehrt dorthin zurück, wo er war, Strg+K im Suchfeld, ein Tab, der geschlossen wird, während die Palette offen ist (Task 10).
5. **Tastenkürzel gegen Tastaturbelegungen und Textfelder:** `Strg+Plus` auf deutscher Belegung, Kürzel dürfen in Such-, Seiten- und Palettenfeld nicht deren Eingabe oder Kopieren verhindern; ein Kürzel gehört genau einem Befehl (keine doppelte Geste im Katalog); alle bisherigen Kürzel wirken unverändert (Tasks 9, 10).

## Entscheidungen dieses Plans (eigene sind markiert; sie bekommen einen Eintrag in `Docs/DECISIONS.md`, Task 14)

- **Dunkle Seite = `PageTheme.Dark` im Rendern, Bilder bleiben, wie sie sind.** *Entscheidung des Projektinhabers (2026-10-18): dunkle Seite als Schwerpunkt, Versuch zuerst.* Wie gemalt wird, wählt der Projektinhaber in Task 1 nach Sichtprüfung: **A** PDFium-Farbschema (`FPDF_RenderPageBitmapWithColorScheme_Start`, Text und Vektoren umgefärbt, Bilder unberührt; bekannte Schwäche laut Header: *ein* Füllfarbe für alle Pfade, farbige Diagramm-Flächen verlieren ihre Farbe, `FPDF_CONVERT_FILL_TO_STROKE` mildert das) oder **B** Helligkeitsumkehr des gerenderten Bilds nach der Formel `c' = (max + min) − c` je Kanal (erhält Farbton und Sättigung, kehrt aber auch Fotos in der Helligkeit um). Farben der dunklen Seite (Vorschlag, in Task 1 zu prüfen): Grund `#1E2127`, Vordergrund `#E3E5E8` (rund 12:1).
- **Wirkung der dunklen Seite:** Einstellung `PageAppearance` = `Light` (Standard, wie bisher) oder `FollowTheme` (dunkle Seiten, wenn das wirksame Theme dunkel ist); je Tab überschreibt der Befehl „Seite hell/dunkel“ (`Strg+Umschalt+D`) die Einstellung, bis der Tab schließt. Treffer, Auswahl und Platzhalter haben dafür eigene Tokens (Task 6). *Eigene Entscheidung.*
- **Settings-Datei:** `settings.json`, Enums als Text, `Version` = 1, unbekannte Felder werden ignoriert, fehlende bekommen den Standard. **Verlauf getrennt** in `recent.json` (Zustand ≠ Einstellung, wird häufig geschrieben). Höchstens 30 Einträge; die Startseite zeigt 5, die Palette 10. *Eigene Entscheidung.*
- **Gespeichert je Datei:** Seite + Anteil (`ViewState`), Zoom, Zoom-Modus, Layout. Geschrieben beim Schließen des Tabs und des Fensters; der Eintrag selbst (Pfad, Zeit) schon beim erfolgreichen Öffnen. Mehrere Instanzen: der letzte Schreiber gewinnt. *Eigene Entscheidung.*
- **Sprache** gilt ab dem nächsten Start (Hinweis in der Karte), weil die Texte beim Laden des XAML gelesen werden. *Eigene Entscheidung.*
- **Standard-Zoom** in den Einstellungen ist ein Zoom-Modus (Seitenbreite, Ganze Seite, Tatsächliche Größe), kein freier Prozentwert. *Eigene Entscheidung.*
- **Tastenkürzel neu:** Strg+K Palette, Strg+Komma Einstellungen, F1 Hilfe, Strg+Umschalt+1 Seiten, Strg+Umschalt+2 Inhalt, Strg+Umschalt+D Seite hell/dunkel. Alles andere bleibt (Strg+O/W/F/G/Tab/Plus/Minus/0/1/2, Bild auf/ab, F3, Alt+Links/Rechts, Esc). Strg+C bleibt in `DocumentView`. *Eigene Entscheidung; der Katalog macht Änderungen einzeilig.*
- **Hochkontrast:** ja, **eigene feste Palette** (schwarzer Grund, weißer Text, Gelb als Akzent, Cyan für Links und Fokus), gewählt, wenn Windows `ContrastPreference` auf „hoch“ steht und das Theme auf „System“; Linux liefert kein Signal und bleibt bei hell/dunkel. Die Farben der Windows-Kontrastthemen (Aquatic, Desert, …) werden **nicht** übernommen: Fluent erlaubt zur Laufzeit nur den Akzent zu ändern (Spec 2.4). Seiten bleiben schwarz auf weiß. *Eigene Entscheidung.*
- **Dichte** setzt `Pw.Size.*`-Ressourcen (Spec 5.4); „Vorschaubilder je Zeile“ im Kompaktmodus heißt zwei Spalten. *Eigene Entscheidung (Umsetzung der Spec-Tabelle).*

## Dateistruktur

**Neu**
- `src/Pagewing.Core/Settings/`: `AppSettings.cs` (inkl. Enums), `SettingsJsonContext.cs`, `SettingsStore.cs`, `UserDataPaths.cs`, `RecentFiles.cs`, `RecentFilesStore.cs`.
- `src/Pagewing.Core/Pdf/PageTheme.cs`, `src/Pagewing.Core/Imaging/DarkPageFilter.cs` (nur bei Entscheidung B), `src/Pagewing.Core/Palette/PaletteMatcher.cs`.
- `src/Pagewing.App/Services/UserSettings.cs`, `Appearance.cs`, `HighContrast.cs`, `RecentFilesService.cs`; `Commands/AppCommand.cs`, `CommandCatalog.cs`, `GestureText.cs`; `ViewModels/PaletteViewModel.cs`, `PaletteItem.cs`, `SettingsViewModel.cs`, `OverlayKind.cs`; `Views/CommandPalette.axaml(.cs)`, `SettingsCard.axaml(.cs)`, `HelpCard.axaml(.cs)`, `MainWindow.Overlays.cs`; `Styles/Density.axaml`, `Styles/HighContrast.axaml`.
- `testdata/pdfs/dark-sample.pdf` (erzeugt).
- Tests (Core): `Settings/AppSettingsTests.cs`, `SettingsStoreTests.cs`, `RecentFilesTests.cs`, `Palette/PaletteMatcherTests.cs`, `Pdfium/DarkPageRenderTests.cs`, `Viewing/PageThemeSchedulerTests.cs`. (App): `UserSettingsTests.cs`, `ThemeSwitchTests.cs`, `DarkPageViewTests.cs`, `DensityTests.cs`, `HighContrastTests.cs`, `CommandCatalogTests.cs`, `CommandPaletteTests.cs`, `SettingsCardTests.cs`, `RecentFilesViewTests.cs`, `AutomationStateTests.cs`.

**Geändert**
- `src/Pagewing.Pdfium/Native/Fpdf.cs`, `FpdfConstants.cs`, `PdfiumRenderWork.cs`; `src/Pagewing.Core/Pdf/RenderOptions.cs`, `PdfDocument.cs`; `Viewing/RenderScheduler.cs`, `ViewerSession.cs`, `ThumbnailSource.cs`, `DocumentLayout.cs` (Rand je Dichte).
- `src/Pagewing.App`: `Program.cs`, `App.axaml(.cs)`, `StartupOptions.cs` (unverändert, `--lang` gewinnt), `ViewModels/MainViewModel.cs`, `TabViewModel.cs`, `Views/MainWindow.axaml(.cs)`, `TitleBar.axaml`, `ActionBar.axaml`, `StatusBar.axaml`, `StartScreen.axaml`, `TabView.axaml`, `DocumentView.axaml(.cs)`, `PagesPanel.axaml`, `Controls/DocumentCanvas.cs`, `ThumbnailList.cs`, `Styles/Tokens.axaml`, `Metrics.axaml`, `Controls.axaml`, `Icons.axaml`, `Resources/Strings.cs`, `Strings.en.resx`, `Strings.de.resx`.
- `tools/make-testpdfs.cs`, `testdata/pdfs/LICENSES.md`, `tests/Pagewing.Render.Tests` (`RenderCorpus`), `tests/Pagewing.Core.Tests/Pdfium/NativeBindingsTests.cs`.
- Doku: Designsystem-Spec, `Docs/DECISIONS.md`, `CLAUDE.md`, `CHANGELOG.md`, `Docs/roadmap.json`, `Docs/ROADMAP.md`, `bench/startup/README.md` (Messwerte).

---

## Teil A – Grundlagen, dunkle Seite, Darstellung

### Task 1: Versuch dunkle Seite und Entscheidung (Tor)

**Files:**
- Create: `testdata/pdfs/dark-sample.pdf` (über `tools/make-testpdfs.cs`), Eintrag in `testdata/pdfs/LICENSES.md` und `RenderCorpus.Cases`
- Modify: `src/Pagewing.Pdfium/Native/Fpdf.cs`, `FpdfConstants.cs`, `tests/Pagewing.Core.Tests/Pdfium/NativeBindingsTests.cs`
- Wegwerf (nie committen): `tests/Pagewing.Core.Tests/Pdfium/DarkSpikeTests.cs`

**Interfaces:**
- Produces: `Fpdf.FPDF_RenderPageBitmapWithColorScheme_Start(nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags, FpdfColorScheme* colorScheme, IfsdkPause* pause) : int`; `struct FpdfColorScheme { uint PathFillColor, PathStrokeColor, TextFillColor, TextStrokeColor }` (je ARGB 8888, Layout wie `FPDF_COLORSCHEME`); `FpdfConstants.RenderConvertFillToStroke = 0x20`.

- [ ] **Step 1: Testdatei `dark-sample.pdf`** in `tools/make-testpdfs.cs` ergänzen (deterministisch, unkomprimiert, wie die anderen): eine A4-Seite mit schwarzem Fließtext, einer Tabelle mit grauen Füllflächen, einem Balkendiagramm aus vier farbigen Rechtecken (rot, grün, blau, orange) mit dünnen Achsenlinien, einem 64 × 64 großen RGB-Verlaufsbild (eingebettetes Bild-XObject) und einem 64 × 64 Graustufenbild. Herkunft „selbst erzeugt“ in `LICENSES.md`, Fall in `RenderCorpus.Cases` (`Tolerant`; die Referenzbilder entstehen erst in Task 4). `dotnet run tools/make-testpdfs.cs -- generate` und `-- check` grün.
- [ ] **Step 2: Bindung + Konstante + Test.** Header prüfen (`fpdf_progressive.h`: `FPDF_RenderPageBitmapWithColorScheme_Start`; `fpdfview.h`: `FPDF_COLORSCHEME`, `FPDF_CONVERT_FILL_TO_STROKE` = 0x20), Bindung und Struct ergänzen, `NativeBindingsTests.ConstantsMatchThePackageHeaders` um die Konstante erweitern. Test laufen lassen: grün.
- [ ] **Step 3: Wegwerf-Test `DarkSpikeTests`** (nicht committen): rendert Seite 1 von `dark-sample.pdf` bei Maßstab 1,5 dreimal in PNGs nach `$env:TEMP\pagewing-dark-spike\` (mit `PngEncoder`): `light.png`; `a-scheme.png` (Bitmap mit Grund `#1E2127` gefüllt, Farbschema: Pfad-Füllung `#1E2127`, Pfad-Strich `#E3E5E8`, Text-Füllung und Text-Strich `#E3E5E8`, einmal mit und einmal ohne `RenderConvertFillToStroke`, also `a-scheme.png` und `a-scheme-stroke.png`); `b-lightness.png` (helles Bild, danach je Pixel `c' = (max + min) − c` auf B, G, R). Dazu `contact.png`: die vier nebeneinander.
- [ ] **Step 4: Ergebnis dem Projektinhaber zeigen** (Pfad der Bilder nennen, `contact.png` zuerst) und nach der Wahl fragen: A mit oder ohne Fill-to-Stroke, oder B. Die Farben `#1E2127`/`#E3E5E8` (Kontrast rund 12:1) bestätigen oder ändern lassen. **Hier anhalten, bis die Entscheidung da ist.**
- [ ] **Step 5: Aufräumen und Eintrag.** `DarkSpikeTests.cs` löschen. Bei B die Bindung und Konstante wieder entfernen (nur Bindungen für gebrauchte Funktionen). Eintrag in `Docs/DECISIONS.md`: „Dunkle Seite: <A|B> (M6)“ mit Kontext, Messbildern (Dateinamen, Beobachtung), Entscheidung, verworfenen Alternativen. Commit: `feat(pdfium): Versuch dunkle Seite, Testseite dark-sample.pdf und Entscheidung`.

### Task 2: Einstellungen im Kern (`AppSettings`, `SettingsStore`)

**Files:**
- Create: `src/Pagewing.Core/Settings/AppSettings.cs`, `SettingsJsonContext.cs`, `SettingsStore.cs`, `UserDataPaths.cs`
- Test: `tests/Pagewing.Core.Tests/Settings/AppSettingsTests.cs`, `SettingsStoreTests.cs`

**Interfaces:**
- Produces:
  - `enum ThemeSetting { System, Light, Dark }`, `enum LanguageSetting { System, German, English }`, `enum DensitySetting { Normal, Compact }`, `enum PageAppearanceSetting { Light, FollowTheme }`, `enum StartPanelSetting { None, Pages, Contents }`.
  - `sealed record AppSettings` mit `int Version = 1`, `ThemeSetting Theme`, `LanguageSetting Language`, `DensitySetting Density`, `bool ReduceMotion`, `PageAppearanceSetting PageAppearance`, `PageLayoutMode DefaultLayout = Continuous`, `ZoomMode DefaultZoomMode = ActualSize`, `StartPanelSetting StartPanel = Pages`, `double PanelWidth = 264` (alles `init`); `static AppSettings Default`; `AppSettings Normalized()` (nicht definierte Enum-Werte → Standard, `DefaultZoomMode` nur `ActualSize`/`FitWidth`/`FitPage` (`Free` → `ActualSize`), `PanelWidth` auf 200 bis 400, nicht endlich → 264).
  - `static class UserDataPaths { static string ConfigDirectory(); static string ConfigDirectory(Func<string, string?> getEnvironmentVariable, bool isWindows); }` (Windows `%APPDATA%\Pagewing`; sonst `$XDG_CONFIG_HOME/pagewing`, Rückfall `$HOME/.config/pagewing`; leere Variable zählt als nicht gesetzt).
  - `sealed class SettingsStore(string path)`: `AppSettings Load()` (wirft nie; fehlende Datei, leere, kaputte, unlesbare Datei → `Default`; eine kaputte Datei wird vorher einmal nach `<path>.bad` kopiert; Ergebnis immer `Normalized()`), `bool TrySave(AppSettings settings)` (legt den Ordner an, schreibt in `<path>.tmp` im Zielordner, ersetzt atomar; `false` bei jeder `IOException`/`UnauthorizedAccessException`, nie eine Ausnahme).

- [ ] **Step 1: Tests zuerst** – `AppSettingsTests`: `DefaultsMatchTheSpec`, `NormalizedClampsPanelWidthAndFixesUndefinedValues` (Breite 5000 → 400, `NaN` → 264, `(ThemeSetting)99` → `System`, `DefaultZoomMode = Free` → `ActualSize`). `SettingsStoreTests` (temporärer Ordner je Test): `MissingFileGivesDefaults`, `RoundTripKeepsEveryField`, `CorruptJsonGivesDefaultsAndKeepsABackup` (Datei `{"Theme":` → Default, `.bad` enthält den alten Text), `UnknownFieldsAndFutureVersionAreTolerated`, `EmptyFileGivesDefaults`, `SaveIsAtomicAndLeavesNoTempFile`, `SaveToAReadOnlyDirectoryReturnsFalse` (Datei als Ordnername benutzen, damit es auf beiden OS scheitert), `EnumsAreWrittenAsText`; `UserDataPathsTests`: `WindowsUsesAppData`, `LinuxPrefersXdgConfigHomeThenHome`, `EmptyVariableCountsAsUnset`.
- [ ] **Step 2: Rot sehen**, `dotnet test tests/Pagewing.Core.Tests --filter-class "*AppSettingsTests"` schlägt fehl (Typen fehlen).
- [ ] **Step 3: Implementieren** wie oben. JSON: `JsonSerializerContext` mit `[JsonSerializable(typeof(AppSettings))]`, `UseStringEnumConverter = true`, `WriteIndented = true`, `ReadCommentHandling = Skip`, `AllowTrailingCommas = true`. `Load` liest die Datei als Text (Obergrenze 1 MiB, größere Datei zählt als kaputt).
- [ ] **Step 4: Grün sehen** (beide Klassen), `dotnet build Pagewing.slnx` ohne AOT-/Trim-Warnung.
- [ ] **Step 5: Roadmap** (Aufgabe „Einstellungen …“ bleibt offen, Teilaufgabe im Plan), Commit `feat(core): Einstellungen als JSON mit Source-Generator und atomarem Speichern`.

### Task 3: Verlauf im Kern (`RecentFiles`)

**Files:**
- Create: `src/Pagewing.Core/Settings/RecentFiles.cs`, `RecentFilesStore.cs`
- Test: `tests/Pagewing.Core.Tests/Settings/RecentFilesTests.cs`

**Interfaces:**
- Consumes: `ViewState`, `PageLayoutMode`, `ZoomMode` aus `Pagewing.Core.Viewing`; `UserDataPaths` (Task 2).
- Produces:
  - `sealed record RememberedView(ViewState View, double Zoom, ZoomMode ZoomMode, PageLayoutMode Layout)`.
  - `sealed record RecentFile(string Path, DateTimeOffset LastOpened, RememberedView? View)`.
  - `sealed class RecentFiles(IEnumerable<RecentFile>? items = null)`: `const int MaxEntries = 30`; `IReadOnlyList<RecentFile> Items` (jüngste zuerst); `void Touch(string path, DateTimeOffset now)` (nach oben, behält `View`); `void Remember(string path, RememberedView view, DateTimeOffset now)` (legt an oder aktualisiert, nach oben); `RecentFile? TryGet(string path)`; `bool Remove(string path)`; `void Clear()`; Pfade werden unter Windows ohne Beachtung der Schreibung verglichen, sonst mit; mehr als 30 Einträge → die ältesten fallen weg; leere Pfade werden ignoriert. Nur auf dem UI-Thread benutzt (kein Sperren).
  - `sealed class RecentFilesStore(string path)`: `RecentFiles Load()` und `bool TrySave(RecentFiles files)` mit denselben Regeln wie `SettingsStore` (`Version` 1, `.bad`, atomar, nie Ausnahme); geladene Einträge sind bereinigt (leerer Pfad, doppelter Pfad, mehr als 30, `Zoom` nicht endlich oder außerhalb 0,1 bis 8 → Eintrag ohne `View`).

- [ ] **Step 1: Tests zuerst** – `TouchMovesToTheTopAndKeepsTheView`, `RememberUpdatesAnExistingEntry`, `OnlyThirtyEntriesAreKept`, `WindowsComparesPathsIgnoringCase` (nur unter Windows, `OperatingSystem.IsWindows()`-Bedingung wie in `MainViewModel`), `RemoveAndClear`, `EmptyPathIsIgnored`, `StoreRoundTripKeepsViewAndOrder`, `StoreDropsEntriesWithInvalidZoomView`, `CorruptFileGivesAnEmptyList`.
- [ ] **Step 2: Rot**, **Step 3: Implementieren**, **Step 4: Grün** (`--filter-class "*RecentFilesTests"`).
- [ ] **Step 5: Roadmap** (Teil von „Zuletzt geöffnete Dateien“), Commit `feat(core): Verlauf der zuletzt geöffneten Dateien mit Position je Datei`.

### Task 4: Dunkle Seite im Rendern (`PageTheme`)

**Files:**
- Create: `src/Pagewing.Core/Pdf/PageTheme.cs`; bei Entscheidung B `src/Pagewing.Core/Imaging/DarkPageFilter.cs`
- Modify: `src/Pagewing.Core/Pdf/RenderOptions.cs`, `PdfDocument.cs`, `src/Pagewing.Pdfium/PdfiumRenderWork.cs`, `src/Pagewing.Core/Viewing/RenderScheduler.cs`, `ViewerSession.cs`, `ThumbnailSource.cs` (zuerst lesen)
- Test: `tests/Pagewing.Core.Tests/Pdfium/DarkPageRenderTests.cs`, `tests/Pagewing.Core.Tests/Viewing/PageThemeSchedulerTests.cs`, `tests/Pagewing.Render.Tests` (Referenzen)

**Interfaces:**
- Produces:
  - `enum PageTheme { Light, Dark }`; `static class PageThemeColors { const uint DarkBackground = 0xFF1E2127; const uint DarkForeground = 0xFFE3E5E8; }` (Werte aus Task 1 bestätigt).
  - `RenderOptions.PageTheme { get; init; } = PageTheme.Light`.
  - `RenderScheduler.PageTheme { get; }` und `void SetPageTheme(PageTheme theme)`: setzt unter der Sperre das Theme, bricht den laufenden Auftrag ab, leert die Warteschlange, den Cache (`RenderCache.Clear()`) und die Fehlermenge; ein Auftrag merkt sich das Theme beim Start, und ein Ergebnis mit anderem Theme als dem aktuellen wird wie ein abgebrochenes verworfen (kein Cache-Eintrag, kein Ereignis, kein Fehler). Der Aufrufer ruft danach `Update` neu auf. `OptionsFor(RenderRequest request, PageTheme theme = PageTheme.Light)` bleibt `internal static`.
  - `ViewerSession.SetPageTheme(PageTheme theme)`: gilt für das offene Dokument (`scheduler?.SetPageTheme`) und für jedes später geöffnete; tut nichts nach `Dispose`. `ViewerSession.PageTheme { get; }`.
  - `ThumbnailSource.SetPageTheme(PageTheme theme)`: leert seinen Cache und rendert fortan im Theme (Aufbau nach Lesen der Datei).

- [ ] **Step 1: Tests zuerst** – `DarkPageRenderTests` (`dark-sample.pdf`, Maßstab 1): `DarkThemeFillsThePageWithTheDarkBackground` (Pixel in einer leeren Ecke = `#1E2127`), `DarkThemeDrawsTextLight` (in der Textzeile existiert ein Pixel mit Helligkeit ≥ 0xC0 und keines schwarzes mit < 0x40 im Textfeld), `ImagesKeepTheirColoursInTheDarkTheme` (**nur bei A**: der Mittelpunkt des RGB-Verlaufsbilds liegt im Dunkel-Rendering innerhalb ±2 je Kanal beim Hell-Rendering; bei B stattdessen `LightnessInversionKeepsTheHue`), `LightThemeIsUnchanged` (Dunkel-Option nicht gesetzt = byte-gleich zum bisherigen Rendern), `TilesOfTheDarkThemeMatchTheWholePageRender` (Kachel gegen Ganzseite, Toleranz wie in den Kacheltests). `PageThemeSchedulerTests` (Fake-Renderfunktion wie in den vorhandenen Scheduler-Tests): `SettingTheThemeClearsTheCacheAndTheQueue`, `AResultRenderedInTheOldThemeIsDiscarded` (Renderer hält an, Theme wechselt, Renderer liefert: Cache bleibt leer, `TileReady` kommt nicht), `RequestsAfterTheSwitchRenderInTheNewTheme` (Optionen am Renderer = `Dark`), `SameThemeDoesNothing`, `ANewDocumentOpensInTheSessionsTheme`.
- [ ] **Step 2: Rot.**
- [ ] **Step 3: Rendern.** `PdfDocument.RenderAsync` reicht `options.PageTheme` an `PdfiumRenderWork` (neuer Parameter `PdfiumPageColors? colors`, `readonly record struct PdfiumPageColors(uint Background, uint Foreground)`; `null` = hell, bisheriges Verhalten bit-genau). **A:** Bitmap mit `Background` statt Weiß füllen und `FPDF_RenderPageBitmapWithColorScheme_Start` mit dem in Task 1 gewählten Schema (Pfad-Füllung `Background`, übrige drei `Foreground`, ggf. `RenderConvertFillToStroke`) aufrufen; **B:** hell rendern, in `Step()` nach `RenderDone` (vor `DropOverlap`) `DarkPageFilter.Apply(Span<byte> bgra)` mit `c' = (max + min) − c` anwenden (Alpha-Byte unberührt, in der Schleife über `Vector128`/`Vector256` nur wenn ohne Mehraufwand lesbar, sonst skalar). Der Überlapp der Kacheln und das Abbrechen bleiben unberührt.
- [ ] **Step 4: Scheduler, Session, Miniaturen** wie in den Interfaces.
- [ ] **Step 5: Grün.** Render-Regression für `dark-sample.pdf` hell: `PAGEWING_UPDATE_REFERENCES=1 dotnet test tests/Pagewing.Render.Tests`, die Abweichung **ansehen** (nur die neue Datei darf entstehen), Referenzen committen; ein zusätzlicher Fall rendert dieselbe Datei dunkel (`RenderCorpus` bekommt ein Feld `Theme` mit Standard `Light`, Fall `dark-sample-dark`, `Tolerant`). Danach `dotnet test Pagewing.slnx` ohne die Updatevariable.
- [ ] **Step 6: Roadmap** (Teil „dunkle Seitendarstellung“), Commit `feat(core): dunkle Seite als PageTheme im Rendern (<A|B>)`.

### Task 5: `UserSettings`, Darstellung und Start (Theme, Sprache, Bewegung, Panel)

**Files:**
- Create: `src/Pagewing.App/Services/UserSettings.cs`, `Appearance.cs`
- Modify: `src/Pagewing.App/Program.cs`, `App.axaml.cs`, `ViewModels/MainViewModel.cs`, `Styles/Metrics.axaml`, die Stellen mit Dauern/Animationen (`grep -rn "Transition\|Duration\|Animation\|DispatcherTimer" src/Pagewing.App`)
- Test: `tests/Pagewing.App.Tests/UserSettingsTests.cs`, `ThemeSwitchTests.cs`

**Interfaces:**
- Consumes: `AppSettings`, `SettingsStore`, `UserDataPaths` (Task 2).
- Produces:
  - `sealed class UserSettings`: `static UserSettings Load()` (aus `UserDataPaths.ConfigDirectory()`/`settings.json`; ohne Datei sofort `Default`, ohne JSON-Aufruf), `static UserSettings Detached()` (nur im Speicher, schreibt nie; für Tests und Designer), `AppSettings Current { get; }`, `void Update(Func<AppSettings, AppSettings> change)` (normalisiert, benachrichtigt, schreibt über `TrySave` **nach** der Benachrichtigung; schlägt das Schreiben fehl, bleibt der Wert im Speicher), `event Action<AppSettings>? Changed`, `bool LastSaveFailed { get; }`.
  - `sealed class Appearance`: `Appearance(Application app, UserSettings settings)` setzt `RequestedThemeVariant` (`System` → `ThemeVariant.Default`), Dichte (Task 7) und Bewegung (`Pw.Duration.Open` = 150 ms und `Pw.Duration.Close` = 100 ms als `x:TimeSpan` in `Metrics.axaml`; mit `ReduceMotion` beide `TimeSpan.Zero`; jede Animation und jedes Timeout aus Spec 5.6 liest sie), reagiert auf `Changed` und auf das Systemtheme (`ActualThemeVariantChanged`); `ThemeVariant EffectiveTheme { get; }`, `event Action? EffectiveThemeChanged`.
  - `MainViewModel.Settings { get; }` (`UserSettings`), `MainViewModel.PanelWidth` startet mit `Settings.Current.PanelWidth` und schreibt das Loslassen des Griffs zurück; das erste scharfe Bild öffnet `StartPanel` (Pages/Contents) statt immer „Seiten“, sofern die bisherigen Bedingungen gelten (`OpenPagesPanelAfterFirstSharpTile` behält seinen Namen); Öffnen/Schließen von „Seiten“ oder „Inhalt“ durch den Nutzer setzt `StartPanel` auf diesen Wert bzw. `None`, „Suche“ ändert es nicht.

- [ ] **Step 1: Tests zuerst** – `UserSettingsTests`: `DetachedNeverWritesAFile`, `UpdateRaisesChangedAndWrites` (temporärer Ordner), `FailingSaveKeepsTheValueAndFlagsIt`, `NoFileMeansNoJsonIsRead` (Ordner ohne Datei → `Current == Default`, danach existiert keine Datei), `UpdateNormalizes`, `SettingsSurviveARestart` (zwei `UserSettings.Load`-Instanzen auf demselben Ordner; **Abnahmekriterium „Einstellungen überleben einen Neustart“**). `ThemeSwitchTests` (Headless): `SystemFollowsTheVariantOfThePlatform`, `ChoosingDarkSwitchesTheVariantAndTheTokens` (`Pw.Brush.Surface.Window` ändert sich auf `#15171B`), `ChoosingLightSwitchesBack`, `TheSwitchNeedsNoRestart`, `ReduceMotionSetsBothDurationsToZero`, `ReduceMotionOpensThePanelWithoutTransition`, `PanelWidthIsRestoredAndSaved`, `TheLastChosenPanelOpensAfterTheFirstSharpPage` (Inhalt gewählt → nächster Start öffnet „Inhalt“), `TheSearchPanelIsNeverTheStartPanel`. **Das sind die „Headless-Tests für Theme-Wechsel“ der Abnahme.**
- [ ] **Step 2: Rot**, **Step 3: Implementieren.** `Program.Main`: nach `StartupOptions.Parse` `UserSettings.Load()`; Sprache `--lang` vor Einstellung vor Systemsprache; `AppStartup` bekommt `UserSettings` (Pflicht), der parameterlose `App()` nimmt `UserSettings.Detached()`. `Appearance` wird in `OnFrameworkInitializationCompleted` vor dem ersten Fenster angelegt.
- [ ] **Step 4: Grün**; **Startzeit messen** wie in `bench/startup/README.md` (zuerst Stand vor dem Task auf demselben Rechner, dann danach, Fälle `cold`, `warm`, `thousand`, `second`, Median aus 10), Werte in den Commit-Text und später in die Roadmap; über +3 ms im `warm`-Median: Ursache suchen (JSON-Initialisierung, Dateizugriff), nicht lockern.
- [ ] **Step 5: Roadmap** („Dunkler Modus … umschaltbar“ Teil, „Einstellungen“ Teil), Commit `feat(app): Einstellungen laden, Theme, Bewegung und Panel-Zustand`.

### Task 6: Dunkle Seite in der Oberfläche

**Files:**
- Modify: `src/Pagewing.App/Controls/DocumentCanvas.cs`, `Views/DocumentView.axaml(.cs)`, `Views/DocumentView.Highlights.cs`, `ThumbnailList.cs`/`PagesPanel.axaml`, `ViewModels/TabViewModel.cs`, `MainViewModel.cs`, `Styles/Tokens.axaml`, `Controls.axaml` (Befehl kommt in Task 9)
- Test: `tests/Pagewing.App.Tests/DarkPageViewTests.cs`, `TokenTests.cs` (erweitern)

**Interfaces:**
- Consumes: `PageTheme`, `ViewerSession.SetPageTheme`, `ThumbnailSource.SetPageTheme` (Task 4); `Appearance.EffectiveTheme`, `AppSettings.PageAppearance` (Task 5).
- Produces:
  - `TabViewModel.PageThemeOverride { get; set; }` (`PageTheme?`, `null` = Einstellung), `TabViewModel.EffectivePageTheme { get; }` (`PageTheme`; = Override, sonst `FollowTheme` und wirksames Theme dunkel → `Dark`, sonst `Light`), `ICommand TogglePageThemeCommand` (wechselt zum jeweils anderen Wert und setzt den Override), `event`/`PropertyChanged` bei Änderung. Die `MainViewModel` hält `tab.AppPageTheme` aktuell (bei Einstellungs- und Themewechsel und beim Hinzufügen eines Tabs); ein Tab, der in ein anderes Fenster zieht, behält seinen Override.
  - `DocumentCanvas.PageTheme` (StyledProperty): bestimmt den Seitengrund statt `Brushes.White` und den Platzhalter; ein Wechsel leert die Bitmap-Kacheln (`TileBitmaps`) und löst `Update` des Planers aus.
  - Neue Tokens (je Theme gleich, Spec 5.1): `Pw.Color/Brush.Page.DarkBackground` `#1E2127`, `Page.DarkForeground` `#E3E5E8`, `Search.HitOnDark` (`#FBE38A` mit Alpha 0,30), `Search.CurrentOnDark` (`#F5A524` mit Alpha 0,45), `Selection.PageOnDark` (`#9DA7FF` mit Alpha 0,35); die Werte sind Vorschläge und werden vom Kontrasttest bestätigt oder angepasst (Seitentext `#E3E5E8` über dem gemischten Grund ≥ 4,5:1).

- [ ] **Step 1: Tests zuerst** – `DarkPageViewTests` (Headless, Fake-Renderer wie die vorhandenen Ansichtstests): `LightIsTheDefaultAndPagesStayWhite`, `FollowThemeInTheDarkThemeRendersDark` (Session bekommt `Dark`, Canvas-Grund = `#1E2127`), `TheOverrideBeatsTheSetting`, `TogglingKeepsPageAndZoom`, `ASwitchDropsTilesOfTheOldTheme` (nach dem Wechsel zeigt das Canvas keine Kachel des alten Themes, auch nicht eine, die gerade fertig wurde), `SearchMatchesAreVisibleOnADarkPage` (Treffer sind nicht multipliziert, sondern mit `Search.HitOnDark` gefüllt; Pixelprobe im Treffer ≠ Seitengrund und Kontrast Seitentext/gemischter Grund ≥ 4,5:1 über `Wcag`), `SelectionIsVisibleOnADarkPage`, `PlaceholderFollowsTheTheme`, `ThumbnailsFollowThePageTheme`, `ThePageShadowAndBorderStayVisible`. `TokenTests`: neue Tokens gegen die Spec-Tabelle (Task 14 schreibt sie dort ein; bis dahin Test mit den Plan-Werten).
- [ ] **Step 2: Rot**, **Step 3: Implementieren.** Treffer auf dunkler Seite **nicht** multiplizieren: `DrawHighlights` wählt je `PageTheme` zwischen dem Multiply-Weg (hell, unverändert) und gewöhnlichem Füllen mit den `…OnDark`-Pinseln. Die Regel aus CLAUDE.md („Treffer werden multipliziert“) gilt weiter für helle Seiten; die Ausnahme steht in Task 14.
- [ ] **Step 4: Grün**, `dotnet test tests/Pagewing.App.Tests` komplett (bestehende Highlight-Tests bleiben unverändert grün).
- [ ] **Step 5: Roadmap** („Dunkler Modus … optional dunkle Seitendarstellung“ abhaken), Commit `feat(app): dunkle Seitendarstellung mit eigenen Markierungsfarben`.

### Task 7: Dichte „Kompakt“

**Files:**
- Create: `src/Pagewing.App/Styles/Density.axaml`
- Modify: `Views/MainWindow.axaml` (`ExtendClientAreaTitleBarHeightHint`, Statusleistenzeile), `TitleBar.axaml`, `ActionBar.axaml`, `StatusBar.axaml`, `Controls/ThumbnailList.cs`, `Views/PagesPanel.axaml`, `Styles/Controls.axaml` (Grundschrift), `src/Pagewing.Core/Viewing/DocumentLayout.cs`, `Views/DocumentView.axaml.cs`, `Services/Appearance.cs`, `MainViewModel.cs` (Panelbreite)
- Test: `tests/Pagewing.App.Tests/DensityTests.cs`

**Interfaces:**
- Produces: Ressourcen `Pw.Size.TitleBarHeight` (40 | 34), `Pw.Size.ActionBarWidth` (52 | 44), `Pw.Size.ActionButton` (40 | 34), `Pw.Size.StatusBarHeight` (32 | 26), `Pw.Size.ToolbarHeight` (44 | 38), `Pw.Font.Body` (13 | 12.5; Zeilenhöhe 18 | 17); `DocumentLayout.PageMargin { get; set; }` (Standard `Margin` = 32, Kompakt 20; `Margin`-Konstante bleibt als Standardwert, alle internen Rechnungen nutzen die Eigenschaft); `MainViewModel.DefaultPanelWidth` ist 264 (Normal) bzw. 236 (Kompakt) – gespeicherte Breite gewinnt; Vorschaubilder: Kompakt zwei Spalten in der Spaltenbreite (Spec 5.4).
- Alle Werte aus der Tabelle in Spec 5.4; „Seitenabstand“ 20 bleibt in beiden.

- [ ] **Step 1: Tests zuerst** – `DensityTests`: `NormalKeepsEveryDimensionOfTheSpec` (die sieben Maße im Normalfall gleich den bisherigen Literalen: Titelleiste 40, Aktionsleiste 52/40, Statusleiste 32 …), `CompactSetsEveryDimensionOfTheSpec` (34, 44/34, 26, Seitenrand 20, Panel 236, Schrift 12,5), `SwitchingNeedsNoRestartAndKeepsThePage` (Dokument offen, Dichte wechseln: Seite/Zoom bleiben, Layout rechnet mit neuem Rand), `CompactThumbnailsUseTwoColumns`, `TheWindowsTitleBarHintFollowsTheDensity` (`ExtendClientAreaTitleBarHeightHint` = 34 im Kompaktmodus; die 140 px für die Fensterknöpfe bleiben), `TextStaysReadable` (kleinste Schrift ≥ 12 px).
- [ ] **Step 2: Rot**, **Step 3: Umsetzen.** Die Literale in den genannten Views durch `{DynamicResource Pw.Size.*}` ersetzen (nur diese Maße, nichts anderes anfassen), `Density.axaml` hält die Kompakt-Werte, `Appearance` tauscht die Ressourcen zur Laufzeit.
- [ ] **Step 4: Grün** (`DensityTests`, danach alle App-Tests: bestehende Tests, die Maße prüfen, bleiben für „Normal“ unverändert). Startzeit wie in Task 5 erneut messen (Standard Normal darf nichts kosten).
- [ ] **Step 5: Roadmap** (Teil „Einstellungen: Dichte“), Commit `feat(app): Dichte Kompakt nach Designsystem 5.4`.

### Task 8: Hochkontrast (Windows)

**Files:**
- Create: `src/Pagewing.App/Services/HighContrast.cs`, `Styles/HighContrast.axaml`
- Modify: `Services/Appearance.cs`, `App.axaml` (Variante und Fluent-Palette), `Styles/Tokens.axaml`
- Test: `tests/Pagewing.App.Tests/HighContrastTests.cs`

**Interfaces:**
- Consumes: `Appearance`, `ThemeSetting.System` (Task 5); `Wcag`.
- Produces: `static class HighContrast { static readonly ThemeVariant Variant = new("HighContrast", ThemeVariant.Dark); static bool IsRequested(PlatformColorValues values) }` (`ContrastPreference == High`; ohne Signal `false`); eine Tokens-Gruppe `HighContrast` mit **allen** Tokens der Dark-/Light-Gruppen: Grund `#000000`, Text `#FFFFFF`, sekundärer Text `#FFFFFF`, Akzent und Links `#FFFF00`, Fokus `#00FFFF`, Ränder `#FFFFFF`, deaktiviert `#A0A0A0`, Gefahr `#FF8080`, Suchtreffer-Fläche mit schwarzem Text; Fluent-Palette `HighContrast` entsprechend. `Appearance` wählt die Variante, wenn Theme = System und Hochkontrast angefordert ist, und folgt `PlatformSettings.ColorValuesChanged`; bei Theme = Hell/Dunkel gilt die Wahl des Nutzers.

- [ ] **Step 1: Tests zuerst** – `HighContrastTests`: `TheVariantHasEveryTokenOfTheOtherThemes` (Schlüsselmengen gleich), `EveryTextPairReachesSevenToOne` (Text/Grund aller Flächen, Akzent/Grund, Fokus/Grund; ≥ 7:1 mit `Wcag`), `ABorderIsNeverOnlyColour` (Fokusring 2 px bleibt, Ränder 1 px sichtbar), `RequestedContrastSelectsTheVariantForSystemTheme`, `AnExplicitThemeBeatsTheContrastRequest`, `NoSignalMeansNoHighContrast` (Linux/Headless: `ContrastPreference` nicht gesetzt), `TheSwitchWorksAtRuntime`. Die Platform-Werte kommen über eine kleine Schnittstelle (`IContrastSource`), die der Test ersetzt.
- [ ] **Step 2: Rot**, **Step 3: Umsetzen.** Zeigt Avalonia 12.1.4 eine eigene `ThemeVariant` in `FluentTheme.Palettes` nicht an oder lässt sie sich nicht als `RequestedThemeVariant` setzen, ist der Rückfall die `Dark`-Variante mit überschriebenen `Pw.*`-Tokens und einer festen `ColorPaletteResources`; die Absicht (feste Palette, ≥ 7:1) bleibt. Das Ergebnis in `Docs/DECISIONS.md` (Task 14) festhalten.
- [ ] **Step 4: Grün.** Nicht automatisierbar, offen für die manuelle Prüfung: echter Windows-Kontrastmodus (Einstellungen → Barrierefreiheit → Kontrastdesigns), Sichtprüfung von Tabs, Aktionsleiste, Karten und Palette; Befehl `Pagewing.exe testdata/pdfs/text-rich.pdf` und Kontrastdesign „Nachthimmel“ einschalten.
- [ ] **Step 5: Roadmap** („Hochkontrast“ abhaken mit Hinweis auf die offene Sichtprüfung), Commit `feat(app): feste Hochkontrast-Palette unter Windows`.

---

## Teil B – Bedienung

### Task 9: Befehlskatalog, Tastenkürzel und Hilfe

**Files:**
- Create: `src/Pagewing.App/Commands/AppCommand.cs`, `CommandCatalog.cs`, `GestureText.cs`; `ViewModels/OverlayKind.cs`; `Views/HelpCard.axaml(.cs)`; `Views/MainWindow.Overlays.cs`
- Modify: `Views/MainWindow.axaml` (alle `Window.KeyBindings` entfernen), `MainWindow.axaml.cs`, `ViewModels/MainViewModel.cs`, `TabViewModel.cs`, `Resources/Strings*`, `Controls/…` für Tooltips (Tooltips nennen das Kürzel, Spec 7.9)
- Test: `tests/Pagewing.App.Tests/CommandCatalogTests.cs`

**Interfaces:**
- Produces:
  - `enum OverlayKind { None, Palette, Settings, Help }`; `MainViewModel.Overlay { get; }`, `void ShowOverlay(OverlayKind kind)` / `HideOverlay()` (eines zugleich, Esc schließt; das Overlay wird erst beim ersten Zeigen gebaut, `MainWindow.Overlays.cs` wie `MainWindow.LinkCard.cs`); Commands `ShowPaletteCommand`, `ShowSettingsCommand`, `ShowHelpCommand`.
  - `sealed record AppCommand(string Id, Func<string> Title, string Group, KeyGesture? Gesture, Func<bool> CanExecute, Action Execute)`; `Func<string> Title` liest `Strings` lazy (Sprachwechsel, Tests).
  - `sealed class CommandCatalog(MainViewModel window)`: `IReadOnlyList<AppCommand> All`, `AppCommand? ById(string id)`, `AppCommand? ForGesture(KeyGesture gesture)`; Gruppen: „Datei“, „Ansicht“, „Navigation“, „Suche“, „Fenster“. Mindestens diese Ids: `file.open`, `file.closeTab`, `tab.next`, `tab.previous`, `view.zoomIn`, `view.zoomOut`, `view.fitPage`, `view.actualSize`, `view.fitWidth`, `view.layout.single|continuous|spread|spreadCover`, `view.pageTheme.toggle`, `nav.goToPage`, `nav.previousPage`, `nav.nextPage`, `nav.back`, `nav.forward`, `panel.pages`, `panel.contents`, `search.open`, `search.next`, `search.previous`, `palette.show`, `settings.show`, `help.show`, `recent.clear`. Jeder Befehl wirkt auf `window.ActiveTab` zur Ausführungszeit.
  - `GestureText.Of(KeyGesture gesture)`: lokalisierte Anzeige („Strg+Umschalt+1“ / „Ctrl+Shift+1“; Ziffern, `+`, `-` lesbar).
  - `MainWindow` baut seine `KeyBindings` einmal aus `CommandCatalog.All` (Gesten sind dort die einzige Quelle); zusätzliche Gesten (`Ctrl+Add`, `Ctrl+Subtract`, `Ctrl+NumPad0/1/2`, `Ctrl+Shift+OemPlus`) stehen als `AlternativeGestures` am Befehl.

- [ ] **Step 1: Tests zuerst** – `CommandCatalogTests`: `EveryCommandHasATitleInBothLanguages`, `NoGestureBelongsToTwoCommands` (inklusive Alternativen), `EveryFormerWindowKeyBindingStillWorks` (Tabelle der bisherigen 24 Kürzel aus `MainWindow.axaml`: Strg+O/W/Tab/Umschalt+Tab, Strg+Plus/Minus (Haupt- und Ziffernblock, Umschalt+Plus), Strg+0/1/2 (beide Blöcke), Strg+G, Bild auf/ab, Strg+F, F3, Umschalt+F3, Alt+Links/Rechts, Esc; jeweils Taste an das Fenster senden und die Wirkung prüfen), `AGestureDoesNotFireWhileTheCommandCannotRun` (Alt+Links ohne Verlauf lässt die Taste weiterlaufen), `ShortcutsDoNotBreakTextFields` (Strg+C im Suchfeld kopiert dort; Strg+K im Suchfeld öffnet die Palette), `TooltipsNameTheShortcut` („Datei öffnen (Strg+O)“ für die Knöpfe mit Kürzel, de/en), `GestureTextIsLocalized`, `TheNewShortcutsAreBound` (Strg+K, Strg+Komma, F1, Strg+Umschalt+1/2/D), `HelpListsEveryCommandWithAGestureGroupedAndSearchable`, `HelpOpensWithF1AndClosesWithEscAndReturnsTheFocus`, `OnlyOneOverlayAtATime`.
- [ ] **Step 2: Rot**, **Step 3: Implementieren.** Die bisherigen `RelayCommand`s bleiben (Katalog ruft sie auf); Hilfekarte ist eine Karte im Fenster (Stil der `LinkConfirmCard`), nicht modal, Tab-Reihenfolge und Fokusrückgabe wie bei dieser.
- [ ] **Step 4: Grün** (`--filter-class "*CommandCatalogTests"`, dann alle App-Tests: `ZoomModeTests`, `TabsTests`, `LinkTests` u. a. drücken Tasten und bleiben unverändert grün).
- [ ] **Step 5: Roadmap** („Tastenkürzel … Hilfe-Dialog“; Panelkürzel aus dem Backlog streichen), Commit `feat(app): zentraler Befehlskatalog, Tastenkürzel und Hilfekarte`.

### Task 10: Befehlspalette

**Files:**
- Create: `src/Pagewing.Core/Palette/PaletteMatcher.cs`; `src/Pagewing.App/ViewModels/PaletteViewModel.cs`, `PaletteItem.cs`; `Views/CommandPalette.axaml(.cs)`
- Modify: `Views/TitleBar.axaml` (Knopf „Suchen oder Befehl · Strg K“ nach Spec 6, `ElementRole="User"`; fehlt die Palette, entfällt der Knopf), `MainWindow.Overlays.cs`, `Resources/Strings*`, `Styles/Icons.axaml` (falls `search` reicht, nichts)
- Test: `tests/Pagewing.Core.Tests/Palette/PaletteMatcherTests.cs`, `tests/Pagewing.App.Tests/CommandPaletteTests.cs`

**Interfaces:**
- Consumes: `CommandCatalog`, `GestureText`, `OverlayKind` (Task 9); `RecentFiles` (Task 3, über `RecentFilesService` erst in Task 12 – bis dahin eine leere Liste); `TabViewModel.Outline`/`EnsureOutlineAsync`; `MainViewModel.Tabs`.
- Produces:
  - `static class PaletteMatcher { static int Score(string query, string text) }`: `-1` kein Treffer; sonst höher = besser; jedes durch Leerraum getrennte Wort der Eingabe muss in `text` vorkommen (ohne Beachtung der Schreibung, Umlaute unverändert); Wortanfang schlägt Wortmitte, Textanfang schlägt Wortanfang, kürzerer Text schlägt längeren; leere Eingabe → 0.
  - `enum PaletteKind { Recent, Page, Tab, Outline, Command }`; `sealed record PaletteItem(PaletteKind Kind, string Title, string? Detail, string? Gesture, Action Run)`.
  - `PaletteViewModel(MainViewModel window)`: `string Query`, `IReadOnlyList<PaletteGroup> Groups` (Reihenfolge „Zuletzt geöffnet“ (leere Eingabe: die letzten 10; sonst gefiltert), „Seiten“ (nur bei einer Zahl 1 … Seitenzahl: ein Eintrag „Gehe zu Seite N“), „Tabs“, „Inhalt“ (nur bei nicht leerer Eingabe und gelesenem Inhaltsverzeichnis, höchstens 50 Zeilen insgesamt über alle Gruppen), „Befehle“ (ausführbare zuerst, mit Kürzel)), `int SelectedIndex`, Befehle `MoveNext`, `MovePrevious`, `PageDown`, `PageUp`, `Run`, `Close`. Beim ersten Öffnen mit einem Dokument stößt die Palette `EnsureOutlineAsync` an (nie beim Start).

- [ ] **Step 1: Tests zuerst** – `PaletteMatcherTests`: `EmptyQueryMatchesEverything`, `AllWordsMustMatch`, `PrefixBeatsInnerMatch`, `CaseIsIgnored`, `UmlautsAreKept`, `ShorterTextWins`, `NoMatchIsMinusOne`. `CommandPaletteTests` (Headless): `CtrlKOpensWithTheFocusInTheField`, `TypingFiltersCommandsAndShowsTheirShortcuts`, `ArrowsEnterAndEscWork` (Enter führt den gewählten Eintrag aus und schließt; Esc schließt; der Fokus kehrt zum vorherigen Element zurück, auch aus dem Suchfeld), `ANumberJumpsToThePage` und `ANumberBeyondTheLastPageOffersNothing`, `WithoutATabOnlyFileCommandsAreOffered` (Strg+K auf der Startseite wirft nicht, „Datei öffnen“ und „Einstellungen“ sind da), `OpenTabsAreListedAndActivated`, `OutlineEntriesAreFoundAndJump`, `AHugeOutlineIsLimitedToFiftyRows` (10 000 Einträge, Antwort unter 100 ms, `Groups` ≤ 50 Zeilen), `ClosingTheActiveTabWhileOpenRefreshesTheList`, `PaletteDoesNotBuildAnythingAtStartup` (vor dem ersten Öffnen kein `CommandPalette`-Objekt im Fensterbaum), `EscapeInsideThePaletteDoesNotEndTheSearch` (die Esc-Kette der Dokumentansicht greift nicht), `EveryRowHasAnAutomationName`. **Das sind die „Headless-Tests für Befehlspalette“ der Abnahme.**
- [ ] **Step 2: Rot**, **Step 3: Implementieren** (Popup 560 DIP breit unter der Titelleiste, `radius.overlay`, Karten-Schatten über das Token; Pfeile/Bild/Enter/Esc selbst über eigene `KeyBindings` des Feldes).
- [ ] **Step 4: Grün**, `dotnet format` (nach XAML-Änderungen oft nötig).
- [ ] **Step 5: Roadmap** („Befehlspalette“ abhaken, Nachweis mit Klassennamen), Commit `feat(app): Befehlspalette mit Befehlen, Seiten, Tabs und Inhaltsverzeichnis`.

### Task 11: Einstellungskarte

**Files:**
- Create: `src/Pagewing.App/ViewModels/SettingsViewModel.cs`; `Views/SettingsCard.axaml(.cs)`
- Modify: `Views/ActionBar.axaml` (Knopf „Einstellungen“ unten, Icon `settings` aus Tabler in `Icons.axaml`), `MainWindow.Overlays.cs`, `Resources/Strings*`
- Test: `tests/Pagewing.App.Tests/SettingsCardTests.cs`

**Interfaces:**
- Consumes: `UserSettings`, `Appearance` (Task 5), `OverlayKind` (Task 9), Dichte (Task 7), `RecentFilesService.Clear` (Task 12; bis dahin ein nicht verdrahteter Knopf ist verboten – der Knopf „Verlauf löschen“ wird in Task 12 ergänzt).
- Produces: `SettingsViewModel(UserSettings settings)` mit zweiseitig gebundenen Eigenschaften `Theme`, `Language`, `Density`, `ReduceMotion`, `PageAppearance`, `DefaultLayout`, `DefaultZoomMode` (jede Änderung ruft `settings.Update` sofort auf, es gibt kein „Übernehmen“); `bool LanguageNeedsRestart` (Hinweiszeile „Gilt ab dem nächsten Start.“, solange die Auswahl vom laufenden Zustand abweicht); `bool SaveFailed` (zeigt „Einstellungen konnten nicht gespeichert werden.“ mit Symbol). Die Karte öffnet über den Knopf unten in der Aktionsleiste und Strg+Komma, schließt mit Esc oder „Fertig“; Aufbau erst beim ersten Öffnen. Neue Tabs übernehmen `DefaultLayout`/`DefaultZoomMode`, wenn für die Datei kein gemerkter Zustand vorliegt (`MainViewModel.AddTab`); bestehende Tabs bleiben unberührt.

- [ ] **Step 1: Tests zuerst** – `SettingsCardTests`: `OpensFromTheActionBarAndWithCtrlComma`, `EveryOptionWritesAtOnce` (jede Option einzeln: Wert in `UserSettings.Current` und Wirkung sichtbar: Theme → Variante, Dichte → `Pw.Size.TitleBarHeight`, Bewegung → Dauern 0, Seite → `EffectivePageTheme`), `LanguageShowsTheRestartHint`, `SavingFailureIsShownWithATextNotOnlyAColour`, `NewTabsUseTheDefaultLayoutAndZoomMode` und `ExistingTabsAreNotChanged`, `KeyboardOnlyUseWorks` (Tab-Reihenfolge, Pfeile in Auswahllisten, Esc), `NothingIsBuiltBeforeTheFirstOpen`, `LabelsAreLocalizedAndEveryControlHasAnAutomationName`, `SettingsSurviveARestartThroughTheCard` (Wert in der Karte ändern, `UserSettings.Load` auf demselben Ordner liefert ihn).
- [ ] **Step 2: Rot**, **Step 3: Implementieren**, **Step 4: Grün.**
- [ ] **Step 5: Roadmap** („Einstellungen …“ abhaken: Sprache, Theme, Dichte, Animationen, Standard-Layout/-Zoom), Commit `feat(app): Einstellungskarte`.

### Task 12: Zuletzt geöffnet und Position je Datei

**Files:**
- Create: `src/Pagewing.App/Services/RecentFilesService.cs`
- Modify: `ViewModels/MainViewModel.cs`, `TabViewModel.cs`, `Views/StartScreen.axaml`, `Views/MainWindow.axaml.cs` (Schließen), `Views/DocumentView.axaml.cs` (Ansicht sichern), `PaletteViewModel.cs`, `SettingsCard.axaml`, `Resources/Strings*`, `App.axaml.cs`
- Test: `tests/Pagewing.App.Tests/RecentFilesViewTests.cs`, `UserSettingsTests.cs` (erweitern)

**Interfaces:**
- Consumes: `RecentFiles`, `RecentFilesStore`, `RememberedView` (Task 3); `TabViewModel.SavedView`, `Zoom`, `ZoomMode`, `LayoutMode`.
- Produces:
  - `sealed class RecentFilesService`: `static RecentFilesService Load()` (liest `recent.json` **nicht** im Startpfad: erst beim ersten Zugriff auf `Files`, spätestens nach dem ersten scharfen Bild per `Dispatcher.Post(Background)`), `static RecentFilesService Detached()` (Tests), `RecentFiles Files { get; }`, `void Opened(string path)` (`Touch`, schreibt), `void Closed(string path, RememberedView view)` (`Remember`, schreibt), `void Remove(string path)`, `void Clear()`, `event Action? Changed`. `AppStartup`/`MainViewModel` bekommen ihn wie `UserSettings`; der parameterlose Pfad nutzt `Detached()`.
  - `TabViewModel.InitialView { set; }`: setzt nach dem `SavedView = null` in `OpenAsync` den gemerkten Zustand (`SavedView`, `LayoutMode`, `ZoomMode`, `Zoom` in dieser Reihenfolge, damit der freie Zoom den Modus nicht überschreibt); ein Dokument mit weniger Seiten als gemerkt (Datei geändert) öffnet auf der letzten Seite (die Ansicht begrenzt bereits, Test sichert es).
  - Aufrufe: `MainViewModel` meldet `Opened` beim ersten `Ready` einer Datei, `Closed` in `CloseTab` nach `Tabs.Close(tab)` (die Ansicht hat da `SavedView` gesichert), und `MainWindow.OnClosing` sichert zuerst die Ansicht des aktiven Tabs (`DocumentView.SaveView()` wird `internal`) und meldet danach alle Tabs des Fensters; Messläufe (`MeasurementRun`) schreiben nie.
  - Startseite: Abschnitt „Zuletzt geöffnet“ mit bis zu 5 Einträgen (Dateiname fett, Ordner in `text.secondary`, mittig gekürzt mit `MiddleEllipsisTextBlock`, Enter/Klick öffnet, Entf entfernt den Eintrag); leer = Abschnitt entfällt. Palette: Gruppe „Zuletzt geöffnet“ (10). Befehl `recent.clear` und Knopf „Verlauf löschen“ in der Einstellungskarte (löscht Liste und Positionen, ohne Rückfrage, mit Bestätigungstext „Verlauf gelöscht.“ im Knopf selbst).

- [ ] **Step 1: Tests zuerst** – `RecentFilesViewTests`: `OpeningAFileAddsItToTheHistory`, `ClosingATabStoresPageZoomAndLayout`, `ReopeningRestoresTheRememberedView` (Seite 3, Zoom 150 %, doppelseitig → nach `OpenAsync` stehen genau diese Werte, Nachweis über `TabViewModel` und die Ansicht), `ClosingTheWindowStoresTheActiveTabsPosition` (Position wird aus der Ansicht gesichert, nicht die vom letzten Tab-Wechsel), `AFileWithFewerPagesOpensOnTheLastPage`, `ARememberedViewDoesNotApplyToAMovedTab`, `TheStartScreenListsFiveEntriesNewestFirst`, `EntriesNeverTouchTheDiskWhenShown` (kein `File.Exists`: die Liste zeigt einen Pfad zu einem nicht vorhandenen Ordner ohne Verzögerung und ohne Fehler), `OpeningAMissingEntryShowsTheErrorCard`, `DeleteRemovesAnEntry`, `ClearEmptiesTheListAndThePositions`, `MeasurementRunsNeverWriteTheHistory`, `TheHistoryIsNotReadInTheStartPath` (vor dem ersten scharfen Bild kein Zugriff auf `recent.json`), `RecentFilesSurviveARestart` (zwei `RecentFilesService.Load` auf demselben temporären Ordner; **Abnahmekriterium „zuletzt geöffnete Dateien überleben einen Neustart“**), `ACorruptHistoryFileDoesNotStopTheApp`.
- [ ] **Step 2: Rot**, **Step 3: Implementieren**, **Step 4: Grün**; **Startzeit-Check** (Task 5 Verfahren), insbesondere `cold` und `warm` mit vorhandener 30-Einträge-Datei.
- [ ] **Step 5: Roadmap** („Zuletzt geöffnete Dateien“ abhaken), Commit `feat(app): zuletzt geöffnete Dateien mit Position je Datei`.

### Task 13: Automation-Zustände und Panelkürzel

**Files:**
- Modify: `Views/ActionBar.axaml`, `Views/TabView.axaml(.cs)` (und ein kleiner Automation-Peer bei Bedarf in `Controls/`), `Styles/Controls.axaml`
- Test: `tests/Pagewing.App.Tests/AutomationStateTests.cs`

**Interfaces:**
- Produces: Einträge der Aktionsleiste sind umschaltbar für Screenreader (Zustand „eingeschaltet“ entspricht dem aktiven Panel; Aussehen unverändert, `Classes.active` bleibt); der aktive Tab meldet „ausgewählt“, die anderen „nicht ausgewählt“; die Tooltips der drei Einträge nennen ihre Kürzel („Seiten (Strg+Umschalt+1)“, „Inhalt (Strg+Umschalt+2)“, „Suche (Strg+F)“).

- [ ] **Step 1: Tests zuerst** – `AutomationStateTests`: `TheOpenPanelEntryReportsToggledOn`, `ClosingThePanelReportsToggledOff`, `TheActiveTabReportsSelected`, `SwitchingTabsMovesTheSelection`, `TooltipsNameThePanelShortcuts`, `LooksAreUnchanged` (die vorhandenen Stil-Tests der Aktionsleiste bleiben grün). Der Zustand wird über die Automation-Peers der Steuerelemente gelesen (`ControlAutomationPeer`/`ToggleButtonAutomationPeer`).
- [ ] **Step 2: Rot**, **Step 3: Umsetzen** (Einträge als `ToggleButton` mit `IsChecked` gebunden an `IsPagesPanelOpen` usw., Befehl bleibt; Tab: Peer mit Auswahlzustand). **Step 4: Grün.**
- [ ] **Step 5: Roadmap**/Backlog: die zwei Backlog-Einträge („Zustände für Tabs und Aktionsleiste“, „Tastenkürzel für die Panels“) streichen; Commit `feat(app): Automation-Zustände für Tabs und Aktionsleiste`.

### Task 14: Abschluss – Spec, Entscheidungen, Doku, Messung, PR

**Files:**
- Modify: `Docs/superpowers/specs/2026-10-17-designsystem.md`, `Docs/DECISIONS.md`, `CLAUDE.md`, `CHANGELOG.md`, `Docs/roadmap.json`, `Docs/ROADMAP.md`, `bench/startup/README.md`

- [ ] **Step 1: Spec nachziehen.** 5.1 (neue Tokens der dunklen Seite und Hochkontrast), 5.4 (Umsetzung, „Vorschaubilder“), 5.6 (Dauern als Ressourcen, „Animationen reduzieren“ umgesetzt), 7.1 (Startseite „Zuletzt geöffnet“), 7.3 (Panelzustand gespeichert, Kürzel), 7.4/7.5 (dunkle Seite, Treffer auf dunkler Seite nicht multipliziert), 7.6 (Palette: Gruppen „Tabs“ und „Inhalt“ ergänzt, 50 Zeilen), 7.9 (Kürzel-Liste, F6 weiterhin offen), Abschnitt 9 (Hochkontrast entschieden; offene Sichtprüfungen: Hochkontrast am echten Gerät, Dichte-Kompakt-Titelleiste 34 px, dunkle Seite in beiden Themes).
- [ ] **Step 2: `Docs/DECISIONS.md`** neue Einträge (neueste oben): dunkle Seite (aus Task 1 fortgeschrieben mit Umsetzung und Treffern), Einstellungen/Verlauf (Dateien, Format, Pfade, atomar, Normalisierung), Befehlskatalog als einzige Quelle, Hochkontrast (feste Palette, Begründung, Verworfenes), Dichte/Bewegung als Ressourcen, Startzeit-Messung (Werte aus Tasks 5/7/12), Sprache ab nächstem Start.
- [ ] **Step 3: `CLAUDE.md`** Projektwissen (kurz, ohne Zeilennummern): Einstellungen/Verlauf in `Pagewing.Core.Settings` und `UserSettings`/`RecentFilesService` (nie im Test auf den echten Benutzerordner), `CommandCatalog` ist die Quelle aller Kürzel (keine `KeyBindings` in XAML), Overlays (`OverlayKind`) entstehen erst beim Öffnen, `PageTheme` im Rendern und die Ausnahme zur Multiplikationsregel der Treffer, Dichte-Ressourcen `Pw.Size.*`, `Pw.Duration.*`.
- [ ] **Step 4: `CHANGELOG.md`** unter `[Unreleased]` → Added/Changed: dunkler Modus mit dunkler Seite, Einstellungen, Befehlspalette (Strg+K), Tastenkürzel und Hilfe (F1), zuletzt geöffnet und Position je Datei, Dichte „Kompakt“, „Animationen reduzieren“, Hochkontrast unter Windows.
- [ ] **Step 5: Vollständige Prüfung.** `dotnet build Pagewing.slnx`, `dotnet test Pagewing.slnx`, `dotnet format Pagewing.slnx --verify-no-changes`, `dotnet run tools/roadmap.cs -- check`, AOT-Publish `dotnet publish src/Pagewing.App -c Release -r win-x64` und App/CLI starten (`tools/smoke-cli.cs`), Startzeit-Check (`bench/startup/startup.cs --case cold|warm|thousand|second`, Median aus 10, ohne `Warnung:`), Werte vor/nach in `bench/startup/README.md` und Roadmap. Nicht prüfbar hier und als offen markiert: Hochkontrast am echten Windows-Kontrastdesign, Dichte und dunkle Seite auf echtem Bildschirm, Linux (Palette, Kürzel, `XDG_CONFIG_HOME`), Hardware-Messung auf dem Haupt-PC (Runner muss der Projektinhaber starten; vor dem Release) – jeweils mit Befehl.
- [ ] **Step 6: Roadmap abschließen.** Abnahmekriterien mit Nachweis abhaken („Headless-Tests für Befehlspalette und Theme-Wechsel“: `CommandPaletteTests`, `ThemeSwitchTests`; „Einstellungen und zuletzt geöffnete Dateien überleben einen Neustart“: `UserSettingsTests.SettingsSurviveARestart`, `RecentFilesViewTests.RecentFilesSurviveARestart`), Status `done` mit Datum, `generate`, `check`.
- [ ] **Step 7: PR** nach `dev` (Squash), Beschreibung mit Zusammenfassung, Testnachweis, offenen Prüfungen; CI-Checks abwarten, bei grün mergen (CLAUDE.md, „Git & Branches“).
