# Startzeit-Check

`bench/startup/startup.cs` prüft die Geschwindigkeitsbudgets am veröffentlichten (Native-AOT-)Binary der App. Die CI ruft es im Job `build` nach „Publish the app (Native AOT)“ für alle vier Fälle auf (Schritt „Startup check“) und lädt die CSV-Dateien als Artefakt `startup-<linux|windows>` hoch. Definition und Grenzen der Messung: `Docs/DECISIONS.md`, Eintrag „Startzeitmessung: Definition und Grenzen (M3)“.

## Aufruf

Im Repository-Stammordner, nach `dotnet publish src/Pagewing.App -c Release -r <win-x64|linux-x64> -o out/app`:

```powershell
dotnet run bench/startup/startup.cs -- --exe out/app/Pagewing.exe --pdf testdata/pdfs/text-basic.pdf --case cold --out out/startup/cold.csv
dotnet run bench/startup/startup.cs -- --exe out/app/Pagewing.exe --pdf testdata/pdfs/text-basic.pdf --case warm --out out/startup/warm.csv --phases
dotnet run bench/startup/startup.cs -- --exe out/app/Pagewing.exe --pdf testdata/pdfs/thousand-pages.pdf --case thousand --out out/startup/thousand.csv
dotnet run bench/startup/startup.cs -- --exe out/app/Pagewing.exe --pdf testdata/pdfs/text-basic.pdf --second-pdf testdata/pdfs/hello_world.pdf --case second --out out/startup/second.csv
dotnet run bench/startup/startup.cs -- selftest
```

Unter Linux heißt das Binary `out/app/Pagewing`; ohne X-Server läuft jeder Aufruf unter `xvfb-run -a` (so in der CI).

Exit-Codes: `0` Median innerhalb der Grenze, `1` Median über der Grenze oder ein Lauf ist fehlgeschlagen, `2` falscher Aufruf.

## Fälle und Budgets

| Fall | Datei | Läufe | Aufwärmläufe | Pause | Budget | Grenze (Fehlschlag darüber) |
|---|---|---|---|---|---|---|
| `cold` | `text-basic.pdf` | 10 | 0 | 5 s | 400 ms | 500 ms |
| `warm` | `text-basic.pdf` | 10 | 3 | 500 ms | 200 ms | 250 ms |
| `thousand` | `thousand-pages.pdf` | 10 | 3 | 500 ms | 300 ms | 375 ms |
| `second` | `text-basic.pdf`, dann `hello_world.pdf` | 10 | 1 | 500 ms | 100 ms | 125 ms |

Entscheidend ist der **Median** der 10 Läufe; die Grenze ist Budget × 1,25 (Budget + 25 %). Ausgegeben werden jeder Lauf, Median, Min und Max; die CSV (`--out`) hat die Spalten `case,run,ms` (Aufwärmläufe stehen nicht darin). Budgets und Grenze werden nie gelockert, nur damit der Check grün wird; eine Änderung ist ein Eintrag in `Docs/DECISIONS.md`.

**`cold` ist kein garantierter Kaltstart.** Das Skript kann den Datei-Cache des Betriebssystems nicht leeren; es startet nur ohne Aufwärmlauf und mit 5 s Pause. Ein echter Kaltstart ist eine manuelle Messung des Projektinhabers direkt nach einem Neustart (Protokoll: `bench/compare/README.md`, Fall (a)).

## Was gemessen wird

Das Skript startet die App mit einem versteckten Messschalter und liest ihre Standardausgabe (die App ist ein GUI-Programm; ihre Standardausgabe kommt nur bei einem Elternprozess an, der sie umleitet):

- `cold`, `warm`, `thousand`: `Pagewing --measure-startup <pdf>`. Die App schreibt `first-sharp`, sobald das erste scharfe Bild von Seite 1 gezeichnet ist (der Frame nach dem Zeichnen, `TopLevel.RequestAnimationFrame`), und beendet sich mit Exit-Code 0. Gemessen wird die Zeit von unmittelbar vor dem Prozessstart bis zum Lesen dieser Zeile.
- `second`: `Pagewing --measure-second <a> <b>`. Die App öffnet `a`, schreibt `first-sharp`, wartet 500 ms und übergibt dann `b` auf demselben Weg wie ein späterer Aufruf (`MainViewModel.OnExternalOpen`). Sie schreibt `second-sharp <ms>`: die Zeit von dieser Übergabe bis zum gezeichneten scharfen Bild von `b`, gemessen in der App, ohne Prozessstart und Pipe (die Pipe hat einen eigenen Test). Dieser Wert ist das Ergebnis des Laufs.

Ein dritter Schalter misst das Scrollen und gehört nicht zum Check: `Pagewing --measure-scroll <pdf> [--zoom <Prozent>]` stellt nach dem ersten scharfen Bild den Zoom ein (Standard 100; ganze Zahl von 10 bis 800, sonst endet die App mit Exit-Code 1 und `error: --zoom …`), springt an den Anfang, wartet einen Frame (der Frame mit dem neuen Zoom zählt nicht mit) und scrollt dann in 600 gleichen Schritten, einem pro Frame, von oben nach unten. Er schreibt zwei Zeilen über die Abstände zwischen den Frames:

- `scroll frames=<n> p99-ms=<x> max-ms=<y>`: 99. Perzentil nach dem Nearest-Rank-Verfahren und Maximum (Format unverändert, Auswertungen lesen diese Zeile).
- `scroll-detail zoom-pct=<z> mean-ms=<m> over-20ms-pct=<a> over-30ms-pct=<b> range-dips=<r>`: Zoom, mittlerer Abstand, Anteil der Abstände über 20 bzw. 30 ms in Prozent und der Scrollweg (Höhe des Inhalts minus Höhe des Viewports, in ganzen DIP aufgerundet). Ein Takt von 30 Bildern pro Sekunde zeigt sich als Mittel um 33 ms und ein hoher Anteil über 30 ms; einzelne Ausreißer bei 60 Hz als kleiner Anteil über 20 ms.

**Scrollweg 0:** Passt der Inhalt in den Viewport (etwa `vector-shapes.pdf`, eine Seite von 300 × 300 pt, bei 100 %), scrollt nichts und es ändert sich nichts. Avalonia ruft die Frame-Rückrufe dann über einen 16-ms-Timer auf, unter Windows rund alle 31 ms; die Abstände zeigen diesen Takt, nicht das Zeichnen. Die App schreibt dann `warning: nothing to scroll at <Zoom> %: …` auf stderr, Ergebnis und Exit-Code bleiben (`Docs/DECISIONS.md`, Eintrag „Scrollmessung: Takt von rund 31 ms ohne Scrollweg ist ein Messartefakt (M3)“).

`--zoom` gilt nur zusammen mit `--measure-scroll`; bei einem normalen Start und den anderen Messschaltern wird es ignoriert.

Für alle drei Schalter gilt: Die App nimmt nicht an der Single-Instance-Wahl teil (sie übergibt nichts an ein laufendes Pagewing und belegt weder Sperre noch Pipe), eine Datei, die sich nicht öffnen lässt, endet mit Exit-Code 1 und `error: …` auf stderr, und nach 30 s ohne Ergebnis endet die App mit Exit-Code 3. Das Skript bricht einen Lauf nach 45 s ab und beendet den Prozess.

## Frame-Takt der Messumgebung

Der Messpunkt ist der Frame nach dem Zeichnen, und Avalonia richtet seine Frames unter Windows nach der Desktop-Komposition (DWM). Zeigt DWM nur wenige Bilder pro Sekunde, enthält jeder Lauf bis zu zwei solcher Wartezeiten, und die Zeiten sagen nichts über die App. Das Skript misst deshalb unter Windows vor den Läufen fünf Abstände von `DwmFlush` und schreibt eine `Warnung: …`, wenn der Median über 40 ms liegt (unter 25 Hz) oder es keine Komposition gibt (Dienstsitzung ohne Desktop). Scheitert die Messung selbst (Ausnahme oder kein Ergebnis nach 2 s), gilt das als „keine Komposition“; der Check läuft trotzdem weiter. Die Warnung steht vor den Läufen und noch einmal neben dem Ergebnis („Ergebnis unter Vorbehalt: …“), dazu als GitHub-Annotation (`::warning::`) in der Zusammenfassung des CI-Laufs. Sie ändert das Ergebnis des Checks nicht.

**Fehlt die Warnung, sind die Zahlen damit nicht belegt gültig.** Die Schwelle liegt bei 25 Hz, ein Takt von 30 Hz bleibt still; unter Linux wird gar nicht gemessen. Beobachtet in derselben VM: Als `DwmFlush` dort zeitweise wieder alle ~16 ms zurückkam (keine Warnung), lag der Warmstart trotzdem bei 393 ms, weil der erste präsentierte Frame des Fensters weiter rund 170 ms nach dem Zeichnen kam. Auf dem CI-Runner (ebenfalls ohne GPU) ist dieser Abstand mit den Phasen gemessen: rund 200 ms im Standardmodus (ANGLE über WARP), 22 bis 32 ms mit `Win32RenderingMode.Software` (`Docs/DECISIONS.md`, Eintrag „Windows-Warmstart: Phasenmessung auf dem Runner (M3)“). Gültig sind nur Zahlen von einem Rechner mit GPU und aktiver Anzeige.

Beobachtet in einer QEMU-VM, deren einzige aktive Anzeige der „Red Hat VirtIO GPU DOD controller“ ist (ohne GPU): DWM zeigt dort nur alle 253 ms ein Bild, auch bei einem leeren Avalonia-Fenster. Gültige Zahlen gibt es nur auf einem Rechner mit GPU und aktiver Anzeige; Befund und verworfene Umgehungen: `Docs/DECISIONS.md`, Eintrag „Frame-Takt der Desktop-Komposition: Messumgebung statt Kompositionsmodus (M3)“.

## Phasen

In den Start- und Zweitdatei-Messläufen (`--measure-startup`, `--measure-second`; nicht `--measure-scroll`) schreibt die App nach `first-sharp` (also nach dem Messpunkt, den das Skript stoppt) je erreichter Phase eine Zeile `phase <Name> <ms>`, die Millisekunden seit ihrem eigenen Prozessstart (`src/Pagewing.App/Measurement/StartupPhases.cs`). Mit `--phases` gibt das Skript nach den Läufen den Median jeder Phase über die gezählten Läufe aus („Phasen (Median …)“), dazu den Median von „Skript − `frame-after-tile`“ je Lauf (Zeit vor dem Prozessstart plus Weg der Zeile). Das ist nur eine Information; Ergebnis und Exit-Code des Checks ändern sich nicht. Die CI nutzt `--phases` im Fall `warm`.

| Phase | Zeitpunkt |
|---|---|
| `main` | Eintritt in `Program.Main` (nach der `AppContext`-Initialisierung) |
| `session-open-started` | das frühe Öffnen der Datei ist gestartet |
| `rendering-software`, `rendering-default` | nur Windows: Rendering-Modus gewählt (`rendering-default` nur mit `PAGEWING_RENDERING=default`; `Docs/DECISIONS.md`, Eintrag „Software-Rendering als Windows-Standard (M3)“) |
| `session-ready` | die Session meldet `Ready` (PDFium-Thread) |
| `avalonia-configure` | vor `AppBuilder.Configure` (in `Program.Run`) |
| `platform-detected` | nach `UsePlatformDetect()`: Subsysteme gewählt, noch nichts initialisiert |
| `desktop-lifetime-start` | unmittelbar vor `StartWithClassicDesktopLifetime` |
| `runtime-init`, `runtime-ready` | Avalonias Laufzeit-Subsystem (Assets, Plattformdienste) |
| `text-shaping-init`, `text-shaping-ready` | Textformung (HarfBuzz) |
| `render-init`, `render-ready` | Render-Subsystem (Skia) |
| `windowing-init`, `windowing-ready` | Fenstersystem (`Win32Platform.Initialize` bzw. X11); unter Windows auf dem GPU-Pfad (`PAGEWING_RENDERING=default`) samt Grafik (ANGLE mit Direct3D-11-Gerät) und Komposition (WinUI, DirectComposition …), bei Software-Rendering ohne beides |
| `platform-services-setup` | `AfterPlatformServicesSetup`: alle Subsysteme initialisiert |
| `app-created` | Konstruktor von `App` (Avalonia erzeugt die App nach den Subsystemen) |
| `app-register-services` | `App.RegisterServices` |
| `app-initialize` | `App.Initialize`: Avalonia-Plattform (Fenstersystem, Grafik) ist initialisiert |
| `app-setup-done` | `AfterSetup`: nach `App.Initialize` (XAML der App geladen), vor `OnFrameworkInitializationCompleted` |
| `app-initialized` | `OnFrameworkInitializationCompleted`: XAML der App geladen |
| `window-created` | `MainWindow` erzeugt |
| `window-opened` | `Window.Opened` (nach Layout und Anzeigen des Fensters) |
| `first-frame` | erster `RequestAnimationFrame`-Rückruf nach `Opened` |
| `preview-applied`, `tile-applied` | Vorschau bzw. erste scharfe Kachel von Seite 1 als Bitmap übernommen |
| `tile-drawn` | `DocumentCanvas.Render` hat die erste scharfe Kachel gezeichnet |
| `frame-after-tile` | der Frame danach; derselbe Zeitpunkt, zu dem `first-sharp` geschrieben wird |

Die Phasen zwischen `avalonia-configure` und `app-initialize` zerlegen Avalonias Start (`AppBuilder.Setup`: Laufzeit, Textformung, Rendering, Fenstersystem, dann die App). Avalonia ruft die Subsysteme in dieser Reihenfolge auf; die Hüllen um die Initialisierer (`Program.WithSetupPhases`) gibt es nur im Messmodus, sie ändern weder Auswahl noch Reihenfolge. Ein normaler Start liest an jeder Marke nur ein Feld. Nach `StartWithClassicDesktopLifetime` gibt es keine Phase: Die Methode kehrt erst beim Beenden zurück, nach den Phasenzeilen.

Genauigkeit: Der Ursprung ist die Erzeugungszeit des Prozesses laut Betriebssystem, gegen die Systemuhr neben der `Stopwatch` gelesen. Abstände zwischen Phasen sind genau. Der Ursprung selbst ist unter Windows auf etwa 1 ms genau (CI: „Skript − `frame-after-tile`“ 2 bis 6 ms), unter Linux nur auf einen Takt (10 ms): Dort liegen die Phasen um bis zu 10 ms zu spät, „Skript − `frame-after-tile`“ wird negativ (CI: −5 bis −8 ms).

## Selbsttest

`selftest` prüft Median (ungerade und gerade Anzahl), Grenze je Fall, die Grenzentscheidung (ein Median genau auf der Grenze besteht, knapp darüber nicht), das Lesen von `second-sharp <ms>` und `phase <Name> <ms>`, Median und Ausgabe der Phasen, dass `--phases` keinen Wert nimmt, die Parameter der Fälle mit festen Werten, die Schwelle der Frame-Takt-Warnung, die Warnung bei gescheiterter Messung, deren Zeitlimit und das Maskieren der GitHub-Annotation. Er läuft in der CI vor dem Check.

## Messung auf echter Hardware (Haupt-PC)

Unter Windows ist Software-Rendering der Standard (`Docs/DECISIONS.md`, Eintrag „Software-Rendering als Windows-Standard (M3)“): Der Startzeit-Check der CI (Runner ohne GPU) und die strikten Schritte auf dem Haupt-PC messen beide diesen Pfad. Den GPU-Pfad (`PAGEWING_RENDERING=default`) misst nur ein informativer Schritt auf dem Haupt-PC. Zahlen für Rechner mit GPU liefert der Workflow `.github/workflows/hardware-measure.yml` („Hardware measurement“) auf dem Haupt-PC des Projektinhabers. Begründung und Grenzen: `Docs/DECISIONS.md`, Eintrag „Messung auf echter Hardware: manueller Runner des Haupt-PCs (M3)“.

**Was der Workflow tut** (Job `measure-windows-gpu`, höchstens 30 Minuten, Läufe nie parallel):

1. Auschecken, .NET 10 einrichten, Voraussetzungen prüfen (`dotnet` 10, `vswhere`, C++-Build-Tools), die App als Native AOT für `win-x64` veröffentlichen.
2. „Hardware report“: Grafikkarte(n) mit Treiber, Windows-Build, CPU, Energiesparplan, Akkustatus nach `out/startup/hardware.txt`.
3. „Startup check (default rendering, software)“: die vier Fälle aus „Fälle und Budgets“ wie in der CI, mit `--phases` im Fall `warm`, mit unveränderten Budgets und Grenzen. `PAGEWING_RENDERING` ist dabei nicht gesetzt (Standard-Renderpfad, unter Windows Software). Ein roter Fall ist ein echter Befund und färbt den Schritt rot.
4. „Scroll measurement“: `Pagewing.exe --measure-scroll` auf `thousand-pages.pdf` und `vector-shapes.pdf` (Standardpfad, 100 %), je 3 Läufe, die Zeilen `scroll frames=…` und `scroll-detail …` samt einer Warnung bei Scrollweg 0 nach `out/startup/scroll.txt`. Für das Scrollen gibt es kein Budget; nur ein Exit-Code der App ungleich 0 oder eine fehlende Ergebniszeile färbt den Schritt rot.
5. Zur Information (`continue-on-error`): der Fall `warm` auf dem GPU-Pfad (`PAGEWING_RENDERING=default`) mit `--phases` (`warm-gpu.log`, `warm-gpu.csv`), damit die Kosten des GPU-Hochfahrens über die Zeit sichtbar bleiben. Ein Median über der Grenze ist dort ein Ergebnis; nur ein fehlendes Ergebnis färbt den Schritt rot.
6. Zur Information (`continue-on-error`, höchstens 8 Minuten): `--measure-scroll` mit `--zoom` 100 und 400 auf `thousand-pages.pdf` und `vector-shapes.pdf`, je 2 Läufe auf dem Standardpfad (Software) und auf dem GPU-Pfad, abwechselnd; Ergebnis `out/startup/scroll-compare.csv` (Datei, Zoom, Pfad, Lauf, Exit-Code, Frames, p99, Max, Mittel, Anteil über 20 und 30 ms, Scrollweg).
7. Zusammenfassung des Laufs (Hardware, Median je Fall gegen Budget und Grenze, Phasen des Falls `warm`, Scrollwerte, Warmstart mit „windowing“ und „erster Frame“ für Software und GPU-Pfad, Scrollvergleich) und Artefakt `hardware-measure` mit allem aus `out/startup/` (CSV, Logs, `hardware.txt`, `scroll.txt`).

**Auslösen:** Der Runner hat das Label `dotnet-windows-gpu` (nicht `dotnet-windows` oder `dotnet-linux`, die CI-Jobs landen deshalb nie dort) und liegt in der Runner-Gruppe `main-pc`, deren Repository-Zugriff der Projektinhaber auf Pagewing beschränkt. Der Workflow startet nur auf ausdrücklichen Befehl:

- Im Actions-Tab über „Run workflow“ (`workflow_dispatch`), oder
  `gh workflow run hardware-measure.yml --repo benjo-code/Pagewing --ref <branch>`; das geht erst, wenn die Datei auf dem Standardzweig (`dev`) liegt.
- Vorher, und für Stände eines PR-Zweigs: das Label `hardware-measure` an einen PR nach `dev` oder `main` hängen (`pull_request` vom Typ `labeled`). PRs aus Forks starten den Workflow nie.

Ohne online laufenden Runner bleibt der Job in der Warteschlange; auf diesen Runner ist nie Verlass.

**Einrichtung durch den Projektinhaber** (einmalig, keine Zugangsdaten im Repository):

1. Voraussetzungen auf dem Haupt-PC: .NET-SDK 10, Visual Studio Build Tools mit der Workload „Desktopentwicklung mit C++“, Windows-PowerShell-Ausführungsrichtlinie `RemoteSigned` für LocalMachine (`Set-ExecutionPolicy -Scope LocalMachine -ExecutionPolicy RemoteSigned -Force` in einer PowerShell mit Administratorrechten).
2. Auf GitHub: Settings → Actions → Runners → „New self-hosted runner“ (Windows x64), Anleitung und Token der Seite folgen; das Archiv in einen eigenen Ordner entpacken, z. B. `C:\actions-runner-hw`.
3. Registrieren, mit dem Token der Seite (`<TOKEN>`; ein Registrierungstoken ist kurz gültig und gehört nirgends ins Repository):

   ```powershell
   .\config.cmd --url https://github.com/benjo-code --token <TOKEN> --runnergroup main-pc --labels dotnet-windows-gpu --name hauptpc --work _work
   ```

   Die Frage „run as service“ mit **N** beantworten. Die Runner-Gruppe `main-pc` vorher in den Einstellungen der Organisation anlegen und ihren Repository-Zugriff auf Pagewing beschränken.
4. Gemessen wird nur, wenn der Projektinhaber den Runner auf Zuruf startet: `run.cmd` im Runner-Ordner, in der angemeldeten Desktop-Sitzung. Nach dem Lauf mit Strg+C beenden.

**Bedingungen während der Messung** (sonst sind die Zahlen nicht gültig): Bildschirm an und entsperrt, Netzbetrieb, Energiesparplan „Höchstleistung“ oder „Ausbalanciert“ ohne Sparmodus, nichts Schweres im Hintergrund (Spiele, Builds, Videos), kein anderes Pagewing offen. Läuft der Runner als Dienst, gibt der Workflow eine Warnung aus: Die App bekäme keinen Desktop. Die Warnung des Skripts zum Frame-Takt (siehe oben) gilt auch hier.
