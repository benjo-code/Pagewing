# <Projektname>

<1–2 Sätze: Was ist das Projekt, für wen, mit welchem Ziel?>

- **Stack:** <z. B. .NET 10 / Unity 6 / Next.js 16>
- **Sprache:** Doku und Commits <Deutsch> · Code und Kommentare <Englisch/Deutsch> · UI-Texte <…>
- **Build:** `<befehl>` · **Test:** `<befehl>` · **Format/Lint:** `<befehl>`
- **Fahrplan:** [Docs/ROADMAP.md](Docs/ROADMAP.md) (generiert, Quelle [Docs/roadmap.json](Docs/roadmap.json), siehe „Roadmap pflegen“)
- **Entscheidungen:** [Docs/DECISIONS.md](Docs/DECISIONS.md)
- **Specs:** `Docs/superpowers/specs/` · **Pläne:** `Docs/superpowers/plans/`

## Projektspezifisches

<Architektur, Konventionen, Besonderheiten, Stolperfallen – wächst mit dem Projekt.>

---

<!-- Ab hier: allgemeine Regeln aus benjo-code/project-template. Projektübergreifend gleich halten; Verbesserungen im Template pflegen und in die Projekte nachziehen. -->

## Roadmap pflegen

Die Quelle der Wahrheit ist `Docs/roadmap.json` (Schema: `Docs/roadmap.schema.json`). `Docs/ROADMAP.md` wird daraus erzeugt und nie von Hand geändert. Voraussetzung ist das .NET-10-SDK, auch wenn das Projekt sonst einen anderen Stack nutzt.

1. Nur `Docs/roadmap.json` bearbeiten: Status, Plan-Link, Aufgaben und Kriterien abhaken (`"done": true`, Nachweis in `"evidence"`), Scope-Änderungen, Backlog. Optionale Felder weglassen, nie auf `null` setzen.
2. Danach `dotnet run tools/roadmap.cs -- generate` ausführen und beide Dateien committen. Fehler nennen den Pfad im JSON (zum Beispiel `subprojects[0].milestones[2].completedOn`).
3. `dotnet run tools/roadmap.cs -- check` prüft Schema, Verweise und ob `ROADMAP.md` aktuell ist; die CI (Job `roadmap`) tut dasselbe.
4. Regeln: `status` ist `open`, `planned`, `in-progress` oder `done`; `done` verlangt `completedOn` (JJJJ-MM-TT), ab `planned` ist `plan` Pflicht (Pfad relativ zu `Docs/`); Milestone-Ids sind eindeutige Strings (`"5.1"` ist erlaubt); Voraussetzungen und `afterMilestone` müssen existieren; Titel, Ziel und Name stehen in einer Zeile; Texte sind Markdown; was nirgends passt, kommt als Eintrag in `blocks` (`title`, `text`).
5. Ein neuer Milestone ist ein Eintrag in `subprojects[].milestones`:

```json
{
  "id": "1", "title": "<Titel>", "status": "open",
  "prerequisites": ["0"],
  "goal": "<Ein Satz: Was ist nach diesem Milestone möglich?>",
  "tasks": [ { "text": "…", "done": false } ],
  "acceptance": [ { "text": "… (prüfbar: Test, Befehl oder konkrete manuelle Prüfung)", "done": false } ]
}
```

   Ein neues Teilprojekt ist ein Eintrag in `subprojects` (`number`, `name`, `spec` mit `text` und/oder `link`, `milestones`), ein Release ein Eintrag in `releases` (`version`, `title`, `status`, `afterMilestone`, optional `covers`, `date`, `intro`, `acceptance`, `blocks`).
6. Ideen gehören in `backlog` (`title`, `text`, optional `idea`). Der Skill `/ideen` (aus `benjo-code/Labjo`) trägt übernommene Ideen dort ein und erzeugt `Docs/ROADMAP.md` neu.

## Milestone-Workflow

Jede Änderung „in der Roadmap“ (Plan verlinken, Status setzen, Aufgaben abhaken, Scope-Änderungen) ist eine Änderung an `Docs/roadmap.json` samt neu erzeugter `Docs/ROADMAP.md`, siehe „Roadmap pflegen“.

**„Plane Milestone N“:**
1. Branch `feature/mNN-<thema>` vom aktuellen `dev` anlegen. Plan, Roadmap-Pflege und Umsetzung des Milestones laufen alle über diesen Branch.
2. `Docs/ROADMAP.md`, die Spec des Teilprojekts und den aktuellen Code-Stand lesen.
3. Offene Ideen des Projekts mit `/ideen` sichten (Skill aus `benjo-code/Labjo`, Installation: `bash tools/install-ideen-skill.sh` dort): übernehmen, umsetzen oder verwerfen. Was in den Milestone gehört, fließt in den Plan ein. Die Roadmap-Änderung liegt auf dem Milestone-Branch (beim Nachfragen „aktueller Branch“ wählen).
4. Beginnt der Milestone mit „Spec erstellen“ oder existiert für das Teilprojekt noch keine Spec: zuerst die Spec per Brainstorming mit dem Nutzer erarbeiten und unter `Docs/superpowers/specs/` ablegen.
5. Plan nach `Docs/superpowers/plans/YYYY-MM-DD-milestone-NN-<thema>.md` schreiben. Der Plan deckt genau die Aufgaben und Abnahmekriterien des Milestones ab, nicht mehr.
6. In der Roadmap den Plan verlinken und den Status auf `🟡 geplant` setzen (Übersichtstabelle und Milestone).

**Umsetzung:**
- Status `🔵 in Arbeit`. Jede erledigte Aufgabe sofort in der Roadmap abhaken.
- Abnahmekriterien erst abhaken, wenn sie tatsächlich geprüft wurden, und den Nachweis dahinter notieren (Test-Name, Befehl + Ergebnis oder manuelle Prüfung).
- Am Ende Status `✅ fertig` mit Datum in Tabelle und Milestone, dann PR nach `dev` (siehe „Git & Branches“).
- Scope-Änderungen in der Roadmap unter „Scope-Änderungen“ festhalten, nicht stillschweigend im Code.
- Nur im Umfang des aktiven Milestones arbeiten. Keine Funktionen, Pakete oder Architekturentscheidungen späterer Milestones vorwegnehmen.

## Git & Branches

- `main` enthält nur veröffentlichte Stände (Releases). `dev` ist der Integrationszweig.
- Arbeit immer auf `feature/<thema>` oder `fix/<thema>`, abgezweigt vom aktuellen `dev` (`git switch dev && git pull && git switch -c feature/<thema>`).
- Vor jeder Arbeit `git status` und den aktuellen Branch prüfen. Steht man auf `main` oder `dev`, zuerst einen Feature-Branch anlegen. Nie direkt auf `main` oder `dev` committen.
- Feature- und Fix-Branches per PR nach `dev`. Claude merged diese PRs selbst, sobald die Arbeit fertig ist und alle CI-Checks grün sind (Squash-Merge erlaubt).
- PRs nach `main` gibt es nur für Releases und Hotfixes und nur auf ausdrückliche Anweisung des Nutzers.
- PRs zwischen `main` und `dev` (Release, Hotfix, Rückführung `main` → `dev`) immer mit **„Create a merge commit“** mergen, nie per Squash oder Rebase. Sonst verlieren `main` und `dev` ihre gemeinsame History, und jedes Release erzeugt Konflikte.
- Hotfix: `hotfix/<thema>` von `main` abzweigen → PR nach `main` → Release (siehe unten) → danach `main` per PR nach `dev` mergen.
- Gezielt stagen (`git add <datei>`). Kein `git add .` oder `git add -A`, wenn fremde, nicht zur Aufgabe gehörende Änderungen im Working Tree liegen. Bei Dateien, die schon vorher geändert waren, erst `git diff <datei>` lesen und ggf. `git add -p` nutzen.
- Nach jedem Commit `git show --stat HEAD` gegen die erwartete Dateiliste prüfen.
- Commit-Messages nach Conventional Commits: `feat:`, `fix:`, `docs:`, `refactor:`, `test:`, `chore:`, `ci:`, optional mit Scope (`feat(api): …`).
- Ohne ausdrückliche Anweisung verboten: Force-Push, `git reset --hard`, Branches löschen, History umschreiben, Hooks mit `--no-verify` umgehen.
- Neue Repos (auch begleitende wie `-Releases` oder `-Privacy`) immer in der Orga `benjo-code` anlegen, immer mit ausgeschriebenem Owner (`gh repo create benjo-code/<name> …`), nie unter dem persönlichen Account. Danach mit `gh repo view benjo-code/<name>` prüfen.

## Releases

- Versionierung nach SemVer (`vMAJOR.MINOR.PATCH`).
- `CHANGELOG.md` → `[Unreleased]` wird in jedem PR mit nennenswerter Änderung für Nutzer ergänzt (nicht bei reinen Planungs-, Roadmap- oder internen Aufräum-PRs).
- Ablauf (nur auf Anweisung):
  1. `release/vX.Y.Z` von `dev` abzweigen, in `CHANGELOG.md` `[Unreleased]` zu `[X.Y.Z] – YYYY-MM-DD` machen (enthaltene Milestones nennen) und ein neues, leeres `[Unreleased]` darüber anlegen.
  2. PR `release/vX.Y.Z` → `main` (Merge-Commit).
  3. Tag und GitHub-Release in einem Schritt: `gh release create vX.Y.Z --target main --title "vX.Y.Z" --notes "<CHANGELOG-Abschnitt>"`.
  4. `main` per PR nach `dev` zurückführen (Merge-Commit), damit beide nicht auseinanderlaufen.

## Projektgedächtnis

- Zukunftswichtiges ohne Aufforderung im Repo dokumentieren:
  - Architektur- und Technikentscheidungen, festgelegte Versionen, verworfene Ansätze → `Docs/DECISIONS.md`
  - Scope-Änderungen → `Docs/ROADMAP.md`, Abschnitt „Scope-Änderungen“
  - Dauerhaftes Projektwissen (Architektur, Konventionen, Workarounds, Stolperfallen) → diese Datei, Abschnitt „Projektspezifisches“
- Keine Vermutungen, Zwischenstände oder temporären Diagnosen als Fakten festhalten.
- Verweise nennen Datei und Typ-, Member- oder Abschnittsnamen, nie Zeilennummern (die veralten).

## Qualität & Nachweis

- „Fertig“ heißt: Build, Test und Format/Lint (Befehle siehe oben) sind nachweislich grün. Befehl und Ergebnis nennen.
- `dotnet run tools/roadmap.cs -- check` gehört zu „fertig“: die Roadmap ist gültig und `Docs/ROADMAP.md` ist aktuell.
- Was nicht geprüft werden konnte (z. B. weil echte Hardware oder Zugangsdaten fehlen), explizit als offen markieren, zusammen mit dem Befehl, der es prüft.
- Keine Warnungen unterdrücken, Tests überspringen oder Checks abschwächen, nur damit etwas grün wird.

## Sicherheit

- Keine Secrets (Tokens, Passwörter, Keys, echte Zugangsdaten) in Dateien, Commits, Logs, PRs oder Antworten.
- Werte aus `.env` o. ä. nie zitieren oder ausgeben, nur Variablennamen oder Platzhalter nennen.
- Secrets kommen aus Env-Vars oder einem Secret-Store. Für jede benötigte Variable einen Platzhalter in `.env.example` pflegen. `.env` steht in `.gitignore`.

## Zusammenarbeit

- Bei unklaren Anforderungen nachfragen statt raten.
- Destruktive oder nach außen wirkende Aktionen (Merge nach `main`, Release, Löschen von Branches, Tags, Releases oder Repos, Änderungen an GitHub-Einstellungen) nur auf ausdrückliche Anweisung. Automatisch gelöschte Feature-Branches nach dem Merge sind davon ausgenommen.
