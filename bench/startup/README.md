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

Ein dritter Schalter misst das Scrollen und gehört nicht zum Check: `Pagewing --measure-scroll <pdf>` scrollt nach dem ersten scharfen Bild in 600 gleichen Schritten, einem pro Frame, von oben nach unten und schreibt `scroll frames=<n> p99-ms=<x> max-ms=<y>` (Abstände zwischen den Frames, 99. Perzentil nach dem Nearest-Rank-Verfahren).

Für alle drei Schalter gilt: Die App nimmt nicht an der Single-Instance-Wahl teil (sie übergibt nichts an ein laufendes Pagewing und belegt weder Sperre noch Pipe), eine Datei, die sich nicht öffnen lässt, endet mit Exit-Code 1 und `error: …` auf stderr, und nach 30 s ohne Ergebnis endet die App mit Exit-Code 3. Das Skript bricht einen Lauf nach 45 s ab und beendet den Prozess.

## Frame-Takt der Messumgebung

Der Messpunkt ist der Frame nach dem Zeichnen, und Avalonia richtet seine Frames unter Windows nach der Desktop-Komposition (DWM). Zeigt DWM nur wenige Bilder pro Sekunde, enthält jeder Lauf bis zu zwei solcher Wartezeiten, und die Zeiten sagen nichts über die App. Das Skript misst deshalb unter Windows vor den Läufen fünf Abstände von `DwmFlush` und schreibt eine `Warnung: …`, wenn der Median über 40 ms liegt (unter 25 Hz) oder es keine Komposition gibt (Dienstsitzung ohne Desktop). Scheitert die Messung selbst (Ausnahme oder kein Ergebnis nach 2 s), gilt das als „keine Komposition“; der Check läuft trotzdem weiter. Die Warnung steht vor den Läufen und noch einmal neben dem Ergebnis („Ergebnis unter Vorbehalt: …“), dazu als GitHub-Annotation (`::warning::`) in der Zusammenfassung des CI-Laufs. Sie ändert das Ergebnis des Checks nicht.

**Fehlt die Warnung, sind die Zahlen damit nicht belegt gültig.** Die Schwelle liegt bei 25 Hz, ein Takt von 30 Hz bleibt still; unter Linux wird gar nicht gemessen. Beobachtet in derselben VM: Als `DwmFlush` dort zeitweise wieder alle ~16 ms zurückkam (keine Warnung), lag der Warmstart trotzdem bei 393 ms, weil der erste präsentierte Frame des Fensters weiter rund 170 ms nach dem Zeichnen kam. Auf dem CI-Runner (ebenfalls ohne GPU) ist dieser Abstand mit den Phasen gemessen: rund 200 ms im Standardmodus (ANGLE über WARP), rund 23 ms mit `Win32RenderingMode.Software` (`Docs/DECISIONS.md`, Eintrag „Windows-Warmstart: Phasenmessung auf dem Runner (M3)“). Gültig sind nur Zahlen von einem Rechner mit GPU und aktiver Anzeige.

Beobachtet in einer QEMU-VM, deren einzige aktive Anzeige der „Red Hat VirtIO GPU DOD controller“ ist (ohne GPU): DWM zeigt dort nur alle 253 ms ein Bild, auch bei einem leeren Avalonia-Fenster. Gültige Zahlen gibt es nur auf einem Rechner mit GPU und aktiver Anzeige; Befund und verworfene Umgehungen: `Docs/DECISIONS.md`, Eintrag „Frame-Takt der Desktop-Komposition: Messumgebung statt Kompositionsmodus (M3)“.

## Phasen

In jedem Messlauf schreibt die App nach `first-sharp` (also nach dem Messpunkt, den das Skript stoppt) je erreichter Phase eine Zeile `phase <Name> <ms>`, die Millisekunden seit ihrem eigenen Prozessstart (`src/Pagewing.App/Measurement/StartupPhases.cs`). Mit `--phases` gibt das Skript nach den Läufen den Median jeder Phase über die gezählten Läufe aus („Phasen (Median …)“), dazu den Median von „Skript − `frame-after-tile`“ je Lauf (Zeit vor dem Prozessstart plus Weg der Zeile). Das ist nur eine Information; Ergebnis und Exit-Code des Checks ändern sich nicht. Die CI nutzt `--phases` im Fall `warm`.

| Phase | Zeitpunkt |
|---|---|
| `main` | Eintritt in `Program.Main` (nach der `AppContext`-Initialisierung) |
| `session-open-started` | das frühe Öffnen der Datei ist gestartet |
| `session-ready` | die Session meldet `Ready` (PDFium-Thread) |
| `app-initialize` | `App.Initialize`: Avalonia-Plattform (Fenstersystem, Grafik) ist initialisiert |
| `app-initialized` | `OnFrameworkInitializationCompleted`: XAML der App geladen |
| `window-created` | `MainWindow` erzeugt |
| `window-opened` | `Window.Opened` (nach Layout und Anzeigen des Fensters) |
| `first-frame` | erster `RequestAnimationFrame`-Rückruf nach `Opened` |
| `preview-applied`, `tile-applied` | Vorschau bzw. erste scharfe Kachel von Seite 1 als Bitmap übernommen |
| `tile-drawn` | `DocumentCanvas.Render` hat die erste scharfe Kachel gezeichnet |
| `frame-after-tile` | der Frame danach; derselbe Zeitpunkt, zu dem `first-sharp` geschrieben wird |

Genauigkeit: Der Ursprung ist die Erzeugungszeit des Prozesses laut Betriebssystem, gegen die Systemuhr neben der `Stopwatch` gelesen. Abstände zwischen Phasen sind genau. Der Ursprung selbst ist unter Windows auf etwa 1 ms genau (CI: „Skript − `frame-after-tile`“ 2 bis 6 ms), unter Linux nur auf einen Takt (10 ms): Dort liegen die Phasen um bis zu 10 ms zu spät, „Skript − `frame-after-tile`“ wird negativ (CI: −5 bis −8 ms).

## Selbsttest

`selftest` prüft Median (ungerade und gerade Anzahl), Grenze je Fall, die Grenzentscheidung (ein Median genau auf der Grenze besteht, knapp darüber nicht), das Lesen von `second-sharp <ms>` und `phase <Name> <ms>`, Median und Ausgabe der Phasen, dass `--phases` keinen Wert nimmt, die Parameter der Fälle mit festen Werten, die Schwelle der Frame-Takt-Warnung, die Warnung bei gescheiterter Messung, deren Zeitlimit und das Maskieren der GitHub-Annotation. Er läuft in der CI vor dem Check.
