# Startzeit-Check

`bench/startup/startup.cs` prüft die Geschwindigkeitsbudgets am veröffentlichten (Native-AOT-)Binary der App. Die CI ruft es im Job `build` nach „Publish the app (Native AOT)“ für alle vier Fälle auf (Schritt „Startup check“) und lädt die CSV-Dateien als Artefakt `startup-<linux|windows>` hoch. Definition und Grenzen der Messung: `Docs/DECISIONS.md`, Eintrag „Startzeitmessung: Definition und Grenzen (M3)“.

## Aufruf

Im Repository-Stammordner, nach `dotnet publish src/Pagewing.App -c Release -r <win-x64|linux-x64> -o out/app`:

```powershell
dotnet run bench/startup/startup.cs -- --exe out/app/Pagewing.exe --pdf testdata/pdfs/text-basic.pdf --case cold --out out/startup/cold.csv
dotnet run bench/startup/startup.cs -- --exe out/app/Pagewing.exe --pdf testdata/pdfs/text-basic.pdf --case warm --out out/startup/warm.csv
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

## Selbsttest

`selftest` prüft Median (ungerade und gerade Anzahl), Grenze je Fall, die Grenzentscheidung (ein Median genau auf der Grenze besteht, knapp darüber nicht), das Lesen von `second-sharp <ms>` und die Parameter der Fälle mit festen Werten. Er läuft in der CI vor dem Check.
