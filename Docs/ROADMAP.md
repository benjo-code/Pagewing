<!-- Generiert aus Docs/roadmap.json mit `dotnet run tools/roadmap.cs -- generate` – nicht von Hand ändern. -->
# <Projektname> – Roadmap

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
| 0 | Repo-Grundgerüst & CI | 1 – <Name> | ⬜ offen | – | – |

## Scope-Änderungen

_Noch keine._

---

# Teilprojekt 1 – <Name>

**Spec:** noch nicht erstellt (wird in M0 erstellt)

## Milestone 0 – Repo-Grundgerüst & CI
**Status:** ⬜ offen · **Plan:** – · **Voraussetzungen:** keine

Ziel: Leeres, aber vollständig aufgesetztes Repo, auf dem alle weiteren Milestones aufbauen.

**Aufgaben**
- [ ] Spec erstellen (Brainstorming): Ziel, Umfang, Stack, Teilprojekte; hier verlinken und die ersten Milestones eintragen
- [ ] Repo-Einrichtung laut README erledigt: `dev` als Default-Branch, Merge-Optionen, Branch Protection für `main` und `dev`
- [ ] Projekt-Kopf in `CLAUDE.md` und `README.md` ausfüllen, Template-Abschnitt aus der README entfernen
- [ ] Stackspezifische `.gitignore` (inkl. `.env`) und `.gitattributes`-Ergänzungen (z. B. Unity: Git LFS für große Assets)
- [ ] Projektgerüst inkl. Testprojekt
- [ ] `.env.example` mit Platzhaltern, falls Secrets benötigt werden
- [ ] CI (GitHub Actions): Build, Test und Format/Lint auf PRs nach `dev` und `main`; der Job `roadmap` aus `.github/workflows/roadmap.yml` bleibt
- [ ] CI-Jobs (`roadmap` und der Job für Build, Test, Lint) als Pflicht-Status-Checks in der Branch Protection von `main` und `dev` eintragen
- [ ] Dependabot für das Paket-Ökosystem und GitHub Actions

**Abnahmekriterien**
- [ ] Build, Test und Format/Lint laufen lokal grün (Befehle in `CLAUDE.md`)
- [ ] CI ist auf einem PR nach `dev` grün
- [ ] Branch Protection für `main` und `dev` ist aktiv: PR Pflicht, CI-Check Pflicht, kein Force-Push, kein Löschen – `gh api repos/benjo-code/<name>/branches/<branch>/protection`

## Backlog

Bewusst nicht eingeplant. Ideen, die `/ideen` hier ablegt, stehen als Liste; bei Bedarf wird daraus ein Milestone.

_Noch nichts._
