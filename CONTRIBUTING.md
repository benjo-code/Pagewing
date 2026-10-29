# Mitwirken / Contributing

## Deutsch

Danke für dein Interesse an Pagewing. Issues und Pull Requests sind auf Deutsch oder Englisch willkommen. Ein Contributor License Agreement (CLA) gibt es nicht.

**Lizenz:** Pagewing steht unter der GPL-3.0-or-later ([LICENSE](LICENSE)). Mit einem Pull Request stellst du deinen Beitrag unter dieselbe Lizenz.

**Sicherheitslücken** nie als öffentliches Issue melden, sondern wie in [SECURITY.md](SECURITY.md) beschrieben.

**Bauen und prüfen** (Voraussetzungen und Hinweise: [README.md](README.md), Abschnitt „Setup“, und [CLAUDE.md](CLAUDE.md)):

```
dotnet build Pagewing.slnx
dotnet test Pagewing.slnx
dotnet format Pagewing.slnx --verify-no-changes
```

**Konventionen:** Doku und Commit-Nachrichten Deutsch, Code und Kommentare Englisch, Commits nach Conventional Commits (`feat:`, `fix:`, `docs:` …). Arbeite auf einem Branch (bzw. Fork) und öffne den Pull Request nach `dev`. Neue NuGet-Pakete brauchen einen Lizenzeintrag (siehe [CLAUDE.md](CLAUDE.md), „Verteilung“).

**CI für Pull Requests aus Forks:** Die CI läuft erst, nachdem der Projektinhaber den Lauf freigegeben hat, und nur auf von GitHub gehosteten Runnern. Eigene Runner des Projekts führen nie Code aus Forks aus. Pull Requests aus Forks werden nie automatisch gemergt.

## English

Thanks for your interest in Pagewing. Issues and pull requests are welcome in German or English. There is no Contributor License Agreement (CLA).

**Licence:** Pagewing is licensed under GPL-3.0-or-later ([LICENSE](LICENSE)). By opening a pull request you license your contribution under the same terms.

**Security vulnerabilities** must never be reported as public issues; see [SECURITY.md](SECURITY.md).

**Build and check** (requirements: [README.md](README.md), section "Setup", and [CLAUDE.md](CLAUDE.md)):

```
dotnet build Pagewing.slnx
dotnet test Pagewing.slnx
dotnet format Pagewing.slnx --verify-no-changes
```

**Conventions:** Documentation and commit messages are in German, code and comments in English, commits follow Conventional Commits (`feat:`, `fix:`, `docs:` ...). Work on a branch (or fork) and open the pull request against `dev`. New NuGet packages need a licence entry (see [CLAUDE.md](CLAUDE.md), "Verteilung").

**CI for pull requests from forks:** CI only runs after the project owner has approved the run, and only on GitHub-hosted runners. The project's own runners never execute code from forks. Pull requests from forks are never merged automatically.
