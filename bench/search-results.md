# Messung der Volltextsuche (Milestone 5)

Gemessen am 2026-10-18 mit BenchmarkDotNet 0.15.8 (`DefaultJob`, Release), Klasse `TextBenchmarks`. Jeder Lauf durchsucht das Dokument vollständig über `DocumentSearch.SearchAsync` (Dokument bereits geöffnet).

| Benchmark | Dokument, Suchbegriff | Treffer | Mittelwert | Fehler (99,9 %) | Zugeteilt |
|---|---|---|---|---|---|
| `SearchThousandPages` | `thousand-pages.pdf` (1000 Seiten), `Page 99` | 11 | 190 ms | 3,8 ms | 1,9 MB |
| `SearchDenseDocument` | `text-dense.pdf` (200 Seiten), `Needle` | 20 | 280 ms | 5,3 ms | 41 MB |
| `SearchManyHitsDenseDocument` | `text-dense.pdf`, `a` (ohne Groß-/Kleinschreibung) | 10 000 (Grenze) | 56 ms | 1,1 ms | 16 MB |

Budgets aus dem Plan: 1000 Seiten unter 2 s, dichtes Dokument unter 1 s; beide mit großem Abstand eingehalten.

Rechner: AMD Ryzen 7 3800X (6 Kerne), Windows 11 25H2 (10.0.26200), .NET SDK 10.0.302, Laufzeit .NET 10.0.10 (X64 RyuJIT x86-64-v3).

Hinweise: Die Läufe schwanken auf diesem Rechner um 20 bis 30 % (ein erster Lauf vor der Optimierung "nur Geometrie je Treffer" ergab 150 / 223 / 52 ms, dazu 205 µs statt 264 µs für den unveränderten `GetTextOfFirstPage`). Die Zahlen gelten als Größenordnung, nicht als Vergleich zwischen Läufen. Die Zuteilung ist stabil.

Erneut messen:

```
dotnet run -c Release --project bench/Pagewing.Benchmarks -- --filter "*TextBenchmarks*"
```

Die Trefferzahlen sichert `SearchBenchmarkDataTests` in `tests/Pagewing.Core.Tests/Text`.
