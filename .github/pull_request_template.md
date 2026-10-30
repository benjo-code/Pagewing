## Was

<!-- Was ändert dieser PR und warum? -->

## Milestone

<!-- z. B. „M3 – App-Shell & schnelles Öffnen“, sonst „–“ -->

## Prüfung

<!-- Welche Befehle/Tests wurden ausgeführt, mit welchem Ergebnis? Was ist noch offen? -->

## Checkliste

- [ ] Ziel-Branch ist `dev` (Ausnahme: `release/*` und `hotfix/*` → `main`)
- [ ] Erledigte Aufgaben in `Docs/roadmap.json` abgehakt, Status aktuell, `Docs/ROADMAP.md` neu erzeugt
- [ ] Build, Test und Format/Lint lokal grün
- [ ] Startzeit-Budgets eingehalten (der Startzeit-Check läuft ab M3 erst nach dem Merge auf `dev`/`main`, Budgets unverändert), nichts Neues ungemessen im Startpfad
- [ ] Neue Abhängigkeiten: Lizenz GPL-3.0-kompatibel und Native-AOT-fähig geprüft, Eintrag in `Docs/DECISIONS.md`
- [ ] Geänderte Referenzbilder der Render-Regression: Abweichungen gesichtet und begründet
- [ ] `CHANGELOG.md` → `[Unreleased]` ergänzt (bei nennenswerter Änderung für Nutzer)
- [ ] Neue Entscheidungen in `Docs/DECISIONS.md` festgehalten
- [ ] Keine Secrets im Diff
