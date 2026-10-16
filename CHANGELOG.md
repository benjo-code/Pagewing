# Changelog

Alle nennenswerten Änderungen an diesem Projekt werden hier dokumentiert.

Format nach [Keep a Changelog](https://keepachangelog.com/de/1.1.0/), Versionierung nach [Semantic Versioning](https://semver.org/lang/de/). Jeder Release-Eintrag nennt die enthaltenen Milestones.

## [Unreleased]

### Added
- Abschnitt `## Backlog` in `Docs/ROADMAP.md`, Schritt „offene Ideen mit `/ideen` sichten“ im Milestone-Workflow der `CLAUDE.md` und Issue-Vorlage `idee.md` (Label `idee`)
- Roadmap als maschinenlesbare `Docs/roadmap.json` mit JSON-Schema (`Docs/roadmap.schema.json`); `tools/roadmap.cs` (.NET-10-Einzeldatei) erzeugt daraus `Docs/ROADMAP.md` (Befehle `generate`, `check`, `selftest`), der Job `roadmap` in `.github/workflows/roadmap.yml` prüft sie, Abschnitt „Roadmap pflegen“ in der `CLAUDE.md`

### Changed
- `Docs/ROADMAP.md` ist generiert und wird nicht mehr von Hand geändert; bestehende Repos stellt Labjo (M12) um

### Fixed

### Removed
