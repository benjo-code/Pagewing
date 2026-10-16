<!-- Generiert aus Docs/roadmap.json mit `dotnet run tools/roadmap.cs -- generate` – nicht von Hand ändern. -->
# Vollständig – Roadmap

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
| 0 | Grundgerüst & CI | 1 – Fundament \| Kern | ✅ fertig | [Plan](superpowers/plans/2026-10-02-milestone-00-grundgeruest.md) | 2026-10-02 |
| 5.1 | Lesen ohne Anmeldung | 1 – Fundament \| Kern | 🔵 in Arbeit | [Plan](superpowers/plans/2026-10-02-milestone-05-1-lesen.md) | – |
| – | **Release `v0.1.0`** – ersetzt das Alte (M0 bis M5.1) | – | ✅ fertig | – | 2026-10-02 |
| 6 | Agent | 2 – Hosts | 🟡 geplant | [Plan](superpowers/plans/2026-10-03-milestone-06-agent.md) | – |
| – | **Release `v0.2.0`** – bringt Hosts (M6) | – | ⬜ offen | – | – |

## Scope-Änderungen

- **2026-10-02 (Planung M1):** Die PWA kommt in M1.
  Zweite Zeile des Eintrags.

---

# Teilprojekt 1 – Fundament | Kern

**Spec:** [2026-10-02-design.md](superpowers/specs/2026-10-02-design.md) (Abschnitte 1 bis 6)

## Milestone 0 – Grundgerüst & CI
**Status:** ✅ fertig (2026-10-02) · **Plan:** [2026-10-02-milestone-00-grundgeruest.md](superpowers/plans/2026-10-02-milestone-00-grundgeruest.md) · **Voraussetzungen:** keine

Ziel: Ein aufgesetztes Repo.

**Aufgaben**
- [x] Projektgerüst anlegen
- [x] CI einrichten
  mit zweiter Zeile

**Abnahmekriterien**
- [x] Build läuft grün – `dotnet build` Exit 0
- [x] Kein Nachweis nötig

## Milestone 5.1 – Lesen ohne Anmeldung
**Status:** 🔵 in Arbeit · **Plan:** [2026-10-02-milestone-05-1-lesen.md](superpowers/plans/2026-10-02-milestone-05-1-lesen.md) · **Spec:** [2026-10-02-lesen-design.md](superpowers/specs/2026-10-02-lesen-design.md) · **Voraussetzungen:** M0 (Release `v0.1.0`)

Ziel: Lesen geht ohne Anmeldung.

**Aufgaben**
- [ ] Richtlinie anlegen

**Abnahmekriterien**
- [ ] Tests grün

**Offene Punkte**
Noch zu klären: Seitenschutz.

## Release `v0.1.0` – Abnahme

Manuelle Prüfung durch den Nutzer.

- [ ] Läuft im Betrieb
- [x] Daten übernommen – Bericht vom 2026-10-03

**Bekannte Grenzen**
Nur ein Host getestet.

---

# Teilprojekt 2 – Hosts

## Milestone 6 – Agent
**Status:** 🟡 geplant · **Plan:** [2026-10-03-milestone-06-agent.md](superpowers/plans/2026-10-03-milestone-06-agent.md) · **Voraussetzungen:** M0, M5.1

Ziel: Ein Agent schickt Metriken.

**Aufgaben**
- [ ] Agent bauen

## Backlog

Bewusst nicht eingeplant. Ideen, die `/ideen` hier ablegt, stehen als Liste; bei Bedarf wird daraus ein Milestone.

- **Dunkles Thema:** Folgt der Systemeinstellung. (Idee #12)
- **Export:** Als CSV.
