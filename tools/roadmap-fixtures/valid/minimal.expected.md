<!-- Generiert aus Docs/roadmap.json mit `dotnet run tools/roadmap.cs -- generate` – nicht von Hand ändern. -->
# Beispiel – Roadmap

Gesamtfahrplan für das Projekt. Jeder Milestone ist so geschnitten, dass er in **einem** Plan und einer Umsetzungsrunde abgearbeitet werden kann.

> **Diese Datei ist generiert.** Quelle ist [roadmap.json](roadmap.json) (Schema: [roadmap.schema.json](roadmap.schema.json)). Änderungen nur dort, danach `dotnet run tools/roadmap.cs -- generate`.

## Arbeitsweise

1. **„Plane Milestone N“:** Claude Code liest diese Roadmap, die verlinkte Spec und den aktuellen Code-Stand und schreibt einen Plan nach `Docs/superpowers/plans/YYYY-MM-DD-milestone-NN-<thema>.md`. Der Plan wird beim Milestone verlinkt, Status → `🟡 geplant`.
2. **Umsetzung:** Plan abarbeiten. Jede erledigte Aufgabe wird sofort abgehakt (`done: true` in `Docs/roadmap.json`).
3. **Abschluss:** Alle Abnahmekriterien geprüft und abgehakt → Status `✅ fertig`, Datum eintragen.
4. Scope-Änderungen während der Umsetzung werden unter „Scope-Änderungen“ festgehalten, nicht stillschweigend im Code.

**Status-Legende:** `⬜ offen` · `🟡 geplant` · `🔵 in Arbeit` · `✅ fertig`

**Regeln:**
- Milestones grundsätzlich in Reihenfolge abarbeiten. Maßgeblich sind die Voraussetzungen beim jeweiligen Milestone.
- Ein Milestone beginnt mit `Spec erstellen`, wenn für sein Teilprojekt noch keine Spec existiert. Die Spec entsteht per Brainstorming und wird unter `Docs/superpowers/specs/` abgelegt.
- Aufgaben späterer Teilprojekte sind absichtlich grob gehalten und werden beim Schreiben der jeweiligen Spec präzisiert.

## Übersicht

| # | Milestone | Teilprojekt | Status | Plan | Fertig am |
|---|---|---|---|---|---|
| 0 | Repo-Grundgerüst | 1 – Fundament | ⬜ offen | – | – |

## Scope-Änderungen

_Noch keine._

---

# Teilprojekt 1 – Fundament

## Milestone 0 – Repo-Grundgerüst
**Status:** ⬜ offen · **Plan:** – · **Voraussetzungen:** keine

Ziel: Ein leeres, aufgesetztes Repo.

## Backlog

Bewusst nicht eingeplant. Ideen, die `/ideen` hier ablegt, stehen als Liste; bei Bedarf wird daraus ein Milestone.

_Noch nichts._
