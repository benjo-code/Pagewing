# Milestone 0 – Repo-Grundgerüst & CI – Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ein lauffähiges, leeres Repo-Gerüst: Solution mit Projekten, zwei Native-AOT-Binaries (CLI und Avalonia-App) und eine CI, die auf Linux und Windows baut, testet, formatiert und AOT-publisht.

**Architecture:** Eine Solution `Pagewing.slnx` mit zentraler Paketverwaltung und gemeinsamen Build-Einstellungen im Repo-Root. Die Einzeldatei-Werkzeuge in `tools/` werden von diesen Einstellungen isoliert. Die CI ist ein Workflow mit zwei Jobs (`build-linux`, `build-windows`) auf den selbst gehosteten Runnern der Orga, daneben bleibt der Job `roadmap`.

**Tech Stack:** .NET 10 SDK 10.0.x, Native AOT, Avalonia 12.1.4, xunit.v3 4.0.2 (Microsoft.Testing.Platform), GitHub Actions, Dependabot.

**Spec:** [2026-10-16-pagewing-design.md](../specs/2026-10-16-pagewing-design.md), insbesondere Abschnitte 5.1 (Struktur) und 8 (CI). Aufgaben und Abnahmekriterien stehen in [ROADMAP.md](../../ROADMAP.md), Milestone 0.

## Global Constraints

- .NET 10 (LTS), App und CLI mit Native AOT; `IsAotCompatible` in allen `src/`-Projekten; Warnungen sind Fehler, AOT- und Trim-Warnungen eingeschlossen.
- Avalonia 12.1.x (hier 12.1.4), kompilierte Bindings, keine XAML-Ladung zur Laufzeit.
- Lizenz GPL-3.0-or-later; Code und Kommentare Englisch, Doku und Commits Deutsch, Conventional Commits; den Commit-Trailer nach Session-Vorgabe anhängen.
- Arbeit nur auf `feature/m00-projektgeruest`, nie auf `dev` oder `main`. Gezielt stagen (`git add <datei>`), nach jedem Commit `git show --stat HEAD` gegen die erwartete Dateiliste prüfen.
- Runner-Labels `dotnet-linux` und `dotnet-windows`; Native AOT kompiliert nicht über Betriebssystemgrenzen, jedes OS baut auf seinem Runner.
- GitHub Actions per Commit-SHA mit Versionskommentar pinnen (SHAs aus `.github/workflows/roadmap.yml` übernehmen).
- Keine Secrets in Dateien. Nur Umfang von M0: keine PDFium-Pakete, keine Render-Tests, keine Benchmarks, kein Single-Instance, kein echtes UI.
- Keine Warnungen unterdrücken, Tests überspringen oder Checks abschwächen, nur damit etwas grün wird.

## Review Focus

Ausfälle, die die Spec nahelegt, die aber keine Aufgabe von selbst abdeckt. Jeder hat einen Test in der genannten Task:

1. **Repo-weite Props brechen `tools/roadmap.cs`** (NU1008 durch zentrale Paketverwaltung) und damit den Pflicht-Check `roadmap`: Task 2 führt `check` und `selftest` nach dem Anlegen der Props aus, die CI tut es bei jedem PR.
2. **CLI ohne oder mit falschen Argumenten** soll Exit 2 und „Usage“ auf stderr liefern, nicht abstürzen: Task 3 prüft das im Smoke-Skript.
3. **AOT-/Trim-Verstöße rutschen durch**, weil Warnungen nicht zu Fehlern werden: Task 2 baut absichtlich einen Verstoß ein und erwartet IL2026/IL3050 als Fehler.
4. **Windows-Runner ohne C++-Werkzeuge oder `vswhere` im PATH** bricht den Linker mit einer schwer lesbaren Meldung ab (im Spike so passiert): Task 5 prüft die Voraussetzungen in einem eigenen Schritt mit klarer Fehlermeldung.
5. **Zeilenenden und Stil auf Windows** (Checkout, `.editorconfig` fordert LF): Task 2 baut einen Stilverstoß ein und erwartet, dass `dotnet format --verify-no-changes` fehlschlägt, die CI lässt das Format auch auf dem Windows-Runner laufen.

---

### Task 1: Repo-Hygiene (.gitignore, NuGet.config, .editorconfig)

**Files:**
- Create: `.gitignore`, `NuGet.config`
- Modify: `.editorconfig`

**Interfaces:**
- Produces: ignorierte Build-Ausgaben (`bin/`, `obj/`, `out/`), NuGet nur aus nuget.org, C#-Stilregeln, die `dotnet format` und der Build durchsetzen.

- [ ] **Step 1: Status in der Roadmap setzen.** In `Docs/roadmap.json` Milestone 0 auf `"status": "in-progress"` setzen, `dotnet run tools/roadmap.cs -- generate`, committen als `docs: Milestone 0 in Arbeit`.

- [ ] **Step 2: `.gitignore` anlegen.** Einträge: `bin/`, `obj/`, `out/`, `publish/`, `.vs/`, `.idea/`, `*.user`, `TestResults/`, `**/render-diff/`, `.env`, `.env.*` mit Ausnahme `!.env.example`.

- [ ] **Step 3: Verifizieren.** Run: `git check-ignore -v src/x/bin/a src/x/obj/a out/cli/a .env tests/x/render-diff/a` → jede Zeile wird gelistet. Run: `git check-ignore .env.example; echo $?` → Ausgabe `1` (nicht ignoriert).

- [ ] **Step 4: `NuGet.config` anlegen:** `<clear />`, einzige Quelle `nuget.org` (`https://api.nuget.org/v3/index.json`), `packageSourceMapping` mit Muster `*` für nuget.org.

- [ ] **Step 5: `.editorconfig` erweitern.** Die Abschnitte `[*.{json,jsonc,yml,yaml,md}]` um `csproj,props,targets,axaml,slnx` erweitern (Einrückung 2). Neuen Abschnitt `[*.cs]` ergänzen:
  `csharp_style_namespace_declarations = file_scoped:warning`, `csharp_using_directive_placement = outside_namespace:warning`, `dotnet_sort_system_directives_first = true`, `dotnet_style_qualification_for_field|property|method = false:warning`, `csharp_style_var_for_built_in_types = true:suggestion`.

- [ ] **Step 6: Commit.**

```bash
git add .gitignore NuGet.config .editorconfig
git commit -m "chore: .gitignore, NuGet.config und C#-Stilregeln"
git show --stat HEAD   # erwartet: genau diese drei Dateien
```

---

### Task 2: Solution-Fundament (Pdfium, Core, Core.Tests)

**Files:**
- Create: `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `tools/Directory.Build.props`, `tools/Directory.Packages.props`, `Pagewing.slnx`
- Create: `src/Pagewing.Pdfium/Pagewing.Pdfium.csproj`, `src/Pagewing.Core/Pagewing.Core.csproj`, `src/Pagewing.Core/AppInfo.cs`
- Create: `tests/Pagewing.Core.Tests/Pagewing.Core.Tests.csproj`, `tests/Pagewing.Core.Tests/AppInfoTests.cs`

**Interfaces:**
- Produces: `public static class AppInfo { public static string Version { get; } }` in `Pagewing.Core` (Namespace `Pagewing.Core`), Wert z. B. `0.0.0`, ohne `+Build`-Anteil. Wird von Task 3 gelesen.
- Produces: Projekt `Pagewing.Pdfium` (in M0 ohne Code), Referenzkette Core → Pdfium.

Alle Einstellungen unten sind im Spike (siehe Abschnitt „Spike-Erkenntnisse“) auf Windows gebaut und getestet worden.

- [ ] **Step 1: Konfigurationsdateien anlegen.**
  - `global.json`: SDK `10.0.100` mit `rollForward` `latestFeature`, und `"test": { "runner": "Microsoft.Testing.Platform" }` (ohne diesen Eintrag verweigert `dotnet test` mit xunit.v3 auf dem .NET-10-SDK die Arbeit).
  - `Directory.Build.props`: `TargetFramework` net10.0, `LangVersion` latest, `Nullable` enable, `ImplicitUsings` enable, `TreatWarningsAsErrors` true, `EnforceCodeStyleInBuild` true, `AnalysisLevel` `latest-recommended`, `Deterministic` true, `Version` `0.0.0`.
  - `Directory.Packages.props`: `ManagePackageVersionsCentrally` true; `PackageVersion` für `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent` (alle 12.1.4) und `xunit.v3` 4.0.2. **Nicht** eintragen: `Microsoft.NET.Test.Sdk` und `xunit.runner.visualstudio` (gehören zum alten VSTest-Weg und brechen den neuen Runner).
  - `tools/Directory.Build.props`: leeres `<Project>` mit Kommentar, dass es die Einzeldatei-Werkzeuge von den Einstellungen im Root isoliert.
  - `tools/Directory.Packages.props`: `ManagePackageVersionsCentrally` false, Kommentar: die Werkzeuge pinnen ihre Pakete per `#:package`.

- [ ] **Step 2: Isolation prüfen (Review Focus 1).** Run: `dotnet run tools/roadmap.cs -- check` → `Roadmap ist gültig und ROADMAP.md ist aktuell.` Run: `dotnet run tools/roadmap.cs -- selftest` → `selftest: 23 Fixtures bestanden.` Zum Gegenbeweis `tools/Directory.Packages.props` kurz umbenennen: `check` muss mit `NU1008` scheitern; Datei zurück umbenennen.

- [ ] **Step 3: Projekte und Solution anlegen.**
  - `Pagewing.Pdfium.csproj` und `Pagewing.Core.csproj` (Sdk `Microsoft.NET.Sdk`, `IsAotCompatible` true; Core referenziert Pdfium).
  - `Pagewing.Core.Tests.csproj`: `OutputType` Exe, `IsPackable` false, `PackageReference` `xunit.v3`, `ProjectReference` auf Core.
  - `dotnet new sln -n Pagewing --format slnx`, danach `dotnet sln Pagewing.slnx add` für alle drei Projekte.

- [ ] **Step 4: Failing Test schreiben** in `AppInfoTests.cs` (`namespace Pagewing.Core.Tests`, Klasse `AppInfoTests`, Attribut `[Fact]`). Der Name enthält keinen Unterstrich, sonst bricht CA1707 den Build:

```csharp
[Fact]
public void VersionIsSemanticVersionWithoutBuildMetadata()
{
    Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$", AppInfo.Version);
}
```

- [ ] **Step 5: Run to verify it fails.** Run: `dotnet test Pagewing.slnx` → Fehler `CS0103` bzw. „The name 'AppInfo' does not exist“.

- [ ] **Step 6: `AppInfo` implementieren** (`src/Pagewing.Core/AppInfo.cs`). `Version` liest `AssemblyInformationalVersionAttribute` der Core-Assembly, schneidet ab `+` ab und fällt auf `"0.0.0"` zurück, wenn das Attribut fehlt.

- [ ] **Step 7: Run to verify it passes.** Run: `dotnet build Pagewing.slnx` → `0 Warnung(en)`, `0 Fehler`. Run: `dotnet test Pagewing.slnx` → `erfolgreich: 1`. Run: `dotnet format Pagewing.slnx --verify-no-changes` → Exit 0.

- [ ] **Step 8: AOT-Gate beweisen (Review Focus 3).** Temporär `src/Pagewing.Core/Gate.cs` mit `internal static class Gate { public static string Dump() => System.Text.Json.JsonSerializer.Serialize(new object()); }` anlegen. Run: `dotnet build src/Pagewing.Core` → Ausgabe enthält `error IL2026` und `error IL3050`. Datei danach löschen.

- [ ] **Step 9: Stil-Gate beweisen (Review Focus 5).** Temporär `src/Pagewing.Core/Style.cs` mit Namespace im Blockstil (`namespace Pagewing.Core { internal static class Style { } }`) anlegen. Run: `dotnet format Pagewing.slnx --verify-no-changes` → Exit ≠ 0 mit `IDE0161`. Datei danach löschen.

- [ ] **Step 10: Commit.**

```bash
git add global.json Directory.Build.props Directory.Packages.props tools/Directory.Build.props tools/Directory.Packages.props Pagewing.slnx src/Pagewing.Pdfium src/Pagewing.Core tests/Pagewing.Core.Tests
git commit -m "feat: Solution-Fundament mit Pdfium, Core und Core.Tests"
git show --stat HEAD   # keine bin/ oder obj/ im Commit
```

---

### Task 3: CLI mit Native AOT und Smoke-Skript

**Files:**
- Create: `tools/smoke-cli.cs`, `src/Pagewing.Cli/Pagewing.Cli.csproj`, `src/Pagewing.Cli/Program.cs`
- Modify: `Pagewing.slnx` (Projekt hinzufügen)

**Interfaces:**
- Consumes: `AppInfo.Version` aus Task 2.
- Produces: Binary `pagewing` bzw. `pagewing.exe` (`AssemblyName` `pagewing`). Vertrag: `--version` schreibt die Version auf stdout, Exit 0. Jeder andere Aufruf schreibt eine Zeile mit „Usage“ auf stderr, Exit 2.
- Produces: `dotnet run tools/smoke-cli.cs -- <binary>`: Exit 0 bei bestandenen Prüfungen, 1 bei Fehlern, 2 bei falschem Aufruf. Wird von Task 5 in der CI genutzt.

- [ ] **Step 1: Smoke-Skript schreiben** (`tools/smoke-cli.cs`, Einzeldatei wie `tools/roadmap.cs`, ohne Pakete). Es löst den übergebenen Pfad mit `Path.GetFullPath` auf (ein relativer Pfad scheitert sonst in `Process.Start`) und prüft zwei Aufrufe: (a) `--version` → Exit 0 und stdout passt auf `^\d+\.\d+\.\d+`; (b) ohne Argumente → Exit 2 und stderr enthält `Usage`. Jeder Fehler wird auf stderr gemeldet, am Ende `Smoke-Test bestanden (<Version>).` bzw. `Smoke-Test fehlgeschlagen.`.

- [ ] **Step 2: Negativtest des Skripts.** Run: `dotnet run tools/smoke-cli.cs -- /c/Windows/System32/whoami.exe` (unter Linux `/bin/true`) → beide Prüfungen werden gemeldet, Exit 1. Run ohne Argument → Exit 2.

- [ ] **Step 3: CLI implementieren.**
  - `Pagewing.Cli.csproj`: `OutputType` Exe, `AssemblyName` `pagewing`, `IsAotCompatible` true, `PublishAot` true, Referenz auf Core.
  - `Program.cs` (Top-Level): bei `args is ["--version"]` `AppInfo.Version` ausgeben und `return 0`; sonst `Usage: pagewing --version` auf stderr und `return 2`.
  - Zur Solution hinzufügen.

- [ ] **Step 4: AOT-Publish.** Run (Linux): `dotnet publish src/Pagewing.Cli -c Release -r linux-x64 -o out/cli`. Run (Windows, PowerShell): zuerst `$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;" + $env:PATH` (ohne `vswhere` im PATH scheitert der Linker mit „Der Befehl vswhere.exe …“), dann `dotnet publish src/Pagewing.Cli -c Release -r win-x64 -o out/cli`. Erwartet: keine Warnungen, Binary unter `out/cli/` (Spike: 1,8 MB).

- [ ] **Step 5: Smoke-Test ausführen.** Run: `dotnet run tools/smoke-cli.cs -- out/cli/pagewing.exe` (Linux: `out/cli/pagewing`) → `Smoke-Test bestanden (0.0.0).`, Exit 0.

- [ ] **Step 6: Commit.**

```bash
git add tools/smoke-cli.cs src/Pagewing.Cli Pagewing.slnx
git commit -m "feat: CLI mit --version als Native-AOT-Binary und Smoke-Skript"
git show --stat HEAD   # kein out/ im Commit
```

---

### Task 4: Avalonia-App mit Native AOT

**Files:**
- Create: `src/Pagewing.App/Pagewing.App.csproj`, `app.manifest`, `Program.cs`, `App.axaml`, `App.axaml.cs`, `MainWindow.axaml`, `MainWindow.axaml.cs`
- Modify: `Pagewing.slnx`

**Interfaces:**
- Consumes: Projekt `Pagewing.Core` (nur als Referenz; in M0 keine Aufrufe).
- Produces: Binary `Pagewing` (`Pagewing.exe` bzw. `Pagewing`), öffnet ein leeres Fenster mit dem Titel „Pagewing“. Die UI kommt erst nach M2.

- [ ] **Step 1: Projekt anlegen.** `Pagewing.App.csproj`: `OutputType` WinExe, `AssemblyName` `Pagewing`, `IsAotCompatible` true, `PublishAot` true, `ApplicationManifest` `app.manifest`, `AvaloniaUseCompiledBindingsByDefault` true. Pakete `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent` (Versionen kommen aus `Directory.Packages.props`), Referenz auf Core, und `<TrimmerRootAssembly Include="Avalonia.Themes.Fluent" />`. Das Manifest enthält nur die Windows-10/11-Kompatibilität (`supportedOS` `{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}`).

- [ ] **Step 2: Code.**
  - `Program.cs`: `internal static class Program` mit `[STAThread] Main(string[] args)` und `BuildAvaloniaApp()` (`AppBuilder.Configure<App>().UsePlatformDetect()`), Start mit `StartWithClassicDesktopLifetime`.
  - `App.axaml` mit `FluentTheme`, `App.axaml.cs`: `public sealed class App : Application`, `Initialize()` ruft `AvaloniaXamlLoader.Load(this)`, `OnFrameworkInitializationCompleted()` setzt bei `IClassicDesktopStyleApplicationLifetime` das `MainWindow`.
  - `MainWindow.axaml`: `Title="Pagewing"`, 900×600, ein zentrierter `TextBlock` „Pagewing“.

- [ ] **Step 3: Build, Test und Format.** Run: `dotnet build Pagewing.slnx`, `dotnet test Pagewing.slnx`, `dotnet format Pagewing.slnx --verify-no-changes` → alles grün, keine Warnungen.

- [ ] **Step 4: AOT-Publish.** Run (Windows mit `vswhere` im PATH wie in Task 3): `dotnet publish src/Pagewing.App -c Release -r win-x64 -o out/app`; Linux: `-r linux-x64`. Erwartet: keine Warnungen, Binary vorhanden (Spike: `Pagewing.exe` 17 MB plus `libSkiaSharp.dll`, `libHarfBuzzSharp.dll`, `av_libglesv2.dll`).

- [ ] **Step 5: Start-Smoke von Hand (nur lokal, nicht in der CI).** Auf Windows: `Start-Process out/app/Pagewing.exe -PassThru`, 4 Sekunden warten, dann muss der Prozess laufen und `MainWindowTitle` gleich `Pagewing` sein; danach `Stop-Process`. Trimming-Fehler zeigen sich erst zur Laufzeit, deshalb gehört dieser Schritt dazu. (Die CI hat auf dem Runner keinen Desktop, das Starten dort kommt mit M3.)

- [ ] **Step 6: Commit.**

```bash
git add src/Pagewing.App Pagewing.slnx
git commit -m "feat: leere Avalonia-App mit Native AOT"
git show --stat HEAD
```

---

### Task 5: CI-Workflow, Dependabot und PR

**Files:**
- Create: `.github/workflows/ci.yml`, `.github/dependabot.yml`

**Interfaces:**
- Consumes: Befehle und Skripte aus Task 2 bis 4.
- Produces: Check-Namen `build-linux` und `build-windows` (Job-Namen, relevant für die Branch Protection in Task 6) sowie ein offener PR nach `dev`.

- [ ] **Step 1: `ci.yml` schreiben.** Aufbau:
  - `name: CI`; Trigger `pull_request` und `push` auf `dev` und `main`; `permissions: contents: read`; `concurrency` Gruppe `ci-${{ github.ref }}` mit `cancel-in-progress: true`.
  - Ein Job `build` mit `name: build-${{ matrix.name }}`, `runs-on: [self-hosted, '${{ matrix.label }}']`, `timeout-minutes: 30`, `fail-fast: false`. Matrix `include`: `{ name: linux, label: dotnet-linux, rid: linux-x64, binary: pagewing }` und `{ name: windows, label: dotnet-windows, rid: win-x64, binary: pagewing.exe }`.
  - Steps in dieser Reihenfolge:
    1. `actions/checkout` mit der SHA aus `roadmap.yml`.
    2. `DOTNET_INSTALL_DIR` setzen (kein Root auf den Runnern): Linux `echo "DOTNET_INSTALL_DIR=$HOME/.dotnet" >> "$GITHUB_ENV"`, Windows `Add-Content -Path $env:GITHUB_ENV -Value "DOTNET_INSTALL_DIR=$env:USERPROFILE\.dotnet"`, jeweils mit `if: runner.os == …`.
    3. `actions/setup-dotnet` mit der SHA aus `roadmap.yml`, `dotnet-version: '10.0.x'`.
    4. Windows: `vswhere` in den PATH (`Add-Content -Path $env:GITHUB_PATH -Value "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"`).
    5. Voraussetzungen für Native AOT prüfen (Review Focus 4), Linux: `clang --version` und `test -f /usr/include/zlib.h`; Windows: `$p = vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`, bei leerem Ergebnis `throw 'Visual Studio C++ build tools not found'`.
    6. `dotnet restore Pagewing.slnx`; `dotnet build Pagewing.slnx -c Release --no-restore`; `dotnet test Pagewing.slnx -c Release --no-build`; `dotnet format Pagewing.slnx --verify-no-changes --no-restore`.
    7. `dotnet publish src/Pagewing.Cli -c Release -r ${{ matrix.rid }} -o out/cli`; `dotnet run tools/smoke-cli.cs -- out/cli/${{ matrix.binary }}`; `dotnet publish src/Pagewing.App -c Release -r ${{ matrix.rid }} -o out/app`.

- [ ] **Step 2: `dependabot.yml` schreiben** (`version: 2`), zwei Einträge, beide `directory: "/"` mit `schedule: interval: weekly, day: monday`:
  - `nuget` mit Gruppen `pdfium` (Muster `bblanchon.PDFium*`), `avalonia` (Muster `Avalonia*`) und `testing` (Muster `xunit*`); **kein** `update-types`-Filter, weil jede PDFium-Version formal ein Major-Update ist; `commit-message: prefix: "chore(deps)"`.
  - `github-actions` mit `commit-message: prefix: "ci(deps)"`.

- [ ] **Step 3: Lokal validieren.** Run: `python -c "import yaml,sys; [yaml.safe_load(open(f)) for f in ('.github/workflows/ci.yml','.github/dependabot.yml')]; print('yaml ok')"` → `yaml ok`. Run: `dotnet run tools/roadmap.cs -- check` → gültig.

- [ ] **Step 4: Commit, pushen, PR anlegen.**

```bash
git add .github/workflows/ci.yml .github/dependabot.yml
git commit -m "ci: Build, Test, Format und AOT-Publish auf Linux und Windows, Dependabot"
git show --stat HEAD
git push -u origin feature/m00-projektgeruest
gh pr create --base dev --head feature/m00-projektgeruest --title "feat: Projektgerüst und CI (Milestone 0)" --body-file <Beschreibung nach .github/pull_request_template.md>
```

- [ ] **Step 5: Auf die Checks warten.** Run: `gh pr checks --watch` → `roadmap`, `build-linux`, `build-windows` alle `pass`. Bei Rot zuerst das Log lesen (`gh run view <id> --log-failed`). Die Voraussetzungs-Schritte nennen die Ursache.
  - Fehlen `clang` oder `zlib.h` auf den Linux-Runnern: **nicht umgehen**. Dem Projektinhaber melden; er installiert auf den Runnern `clang` und `zlib1g-dev`, danach den Job neu starten (`gh run rerun <id> --failed`).
  - Fehlen die C++-Werkzeuge auf dem Windows-Runner: ebenso melden (Visual Studio Build Tools, Workload „Desktopentwicklung mit C++“).

- [ ] **Step 6: Check-Namen sichern.** Run: `gh api repos/benjo-code/Pagewing/commits/$(git rev-parse HEAD)/check-runs --jq '.check_runs[].name'` → enthält exakt `roadmap`, `build-linux`, `build-windows` (diese Namen gehen in Task 6 in die Branch Protection).

---

### Task 6: Abschluss – Branch Protection, Dokumentation, Merge

**Files:**
- Modify: `Docs/roadmap.json`, `Docs/ROADMAP.md` (generiert), `CLAUDE.md`, `README.md`, `Docs/DECISIONS.md`

**Interfaces:**
- Consumes: grüne Checks aus Task 5.

- [ ] **Step 1: Branch Protection anpassen (braucht ausdrückliche Zustimmung des Projektinhabers, weil es eine GitHub-Einstellung ist).** Vor dem Ausführen nachfragen. Dann für `main` und `dev`:

```bash
for b in main dev; do
gh api -X PATCH "repos/benjo-code/Pagewing/branches/$b/protection/required_status_checks" --input - <<'EOF'
{ "strict": true, "contexts": ["roadmap", "build-linux", "build-windows"] }
EOF
done
```

  Verifizieren: `gh api repos/benjo-code/Pagewing/branches/dev/protection --jq '{checks: .required_status_checks.contexts, pr: (.required_pull_request_reviews != null), force: .allow_force_pushes.enabled, del: .allow_deletions.enabled}'` (analog `main`) → `checks` mit allen drei Namen, `pr` true, `force` false, `del` false.

- [ ] **Step 2: Dokumentation nachtragen.**
  - `CLAUDE.md`, Abschnitt „Projektspezifisches“, neuer Unterabschnitt „Build-Stolperfallen“:
    - Zentrale Paketverwaltung im Root gilt auch für Einzeldatei-Werkzeuge; `tools/Directory.Build.props` und `tools/Directory.Packages.props` isolieren sie, sonst bricht `tools/roadmap.cs` mit NU1008.
    - xunit.v3 läuft auf Microsoft.Testing.Platform: `global.json` enthält den Runner, `Microsoft.NET.Test.Sdk` und `xunit.runner.visualstudio` nicht referenzieren.
    - Testnamen ohne Unterstriche (CA1707 unter `latest-recommended`).
    - Native AOT unter Windows braucht `vswhere.exe` im PATH (Ordner `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer`) und die VS-Build-Tools mit C++-Workload; unter Linux `clang` und `zlib1g-dev`.
  - Im Kopf von `CLAUDE.md` den Zusatz „(Solution entsteht in M0)“ entfernen.
  - `README.md`, Abschnitt „Setup“: den Satz „Das Code-Grundgerüst entsteht in Milestone 0. Bis dahin gibt es nur Planungsdokumente.“ durch den tatsächlichen Stand ersetzen und die Voraussetzungen (Build-Tools) wie oben nennen.
  - `Docs/DECISIONS.md`, neuer Eintrag oben (Datum des Tages): **Testframework xunit.v3 4.0.2 mit Microsoft.Testing.Platform**. Begründung: `Avalonia.Headless.XUnit` 12.1.4 hängt von `xunit.v3.extensibility.core` 3.2.2 ab, xunit v2 würde dazu nicht passen. Verworfen: xunit v2 mit VSTest. Offener Punkt: Die Verträglichkeit von `Avalonia.Headless.XUnit` mit xunit.v3 4.0.x wird in M3 geprüft. Zweiter Eintrag: **Zentrale Paketverwaltung und Build-Einstellungen im Root, `tools/` isoliert**, mit Verweis auf den NU1008-Fund.

- [ ] **Step 3: Roadmap fortschreiben** in `Docs/roadmap.json` (Milestone 0):
  - Alle erledigten Aufgaben abhaken, auch „Self-hosted Windows-Runner“ (Nachweis: der Windows-Runner ist in der Orga online, `gh api orgs/benjo-code/actions/runners`).
  - Abnahmekriterien mit Nachweis abhaken: (1) lokale Befehle grün; (2) AOT-Publish ohne Warnungen auf beiden OS plus `pagewing --version` (Smoke-Skript, CI-Lauf); (3) CI grün auf dem PR (PR-Nummer); (4) Branch Protection (Ausgabe aus Step 1).
  - Status `done` mit `completedOn` (Datum des Tages).
  - Run: `dotnet run tools/roadmap.cs -- generate`, dann `dotnet run tools/roadmap.cs -- check` → gültig.

- [ ] **Step 4: Abschlussprüfung (verification-before-completion).** Frisch ausführen und Ausgabe prüfen: `dotnet build Pagewing.slnx`, `dotnet test Pagewing.slnx`, `dotnet format Pagewing.slnx --verify-no-changes`, `dotnet run tools/roadmap.cs -- check`. Erst danach „fertig“ behaupten.

- [ ] **Step 5: Commit, pushen, Merge.**

```bash
git add CLAUDE.md README.md Docs/DECISIONS.md Docs/roadmap.json Docs/ROADMAP.md
git commit -m "docs: Milestone 0 abgeschlossen, Stolperfallen und Entscheidungen"
git show --stat HEAD
git push
gh pr checks --watch   # alle drei Checks grün
gh pr merge --squash   # nur wenn alles grün ist
```

  Nach dem Merge: `git switch dev && git pull`; prüfen, dass der Workflow auf `dev` grün durchläuft (`gh run list --branch dev --limit 3`).

## Spike-Erkenntnisse (Grundlage der Konfiguration oben)

Die Konfiguration wurde vorab in einem Wegwerf-Verzeichnis auf Windows (SDK 10.0.302, Visual Studio 18 Community) gebaut und getestet: Build, Test, Format, AOT-Publish von CLI und App, Start der App, Smoke-Skript, `tools/roadmap.cs` mit Isolation, beide Negativ-Gates. **Nicht** geprüft werden konnte Linux (kein Linux-Rechner verfügbar): Der Linux-AOT-Build, `clang`/`zlib` auf den Runnern und das Verhalten von `dotnet test` unter Linux zeigen sich erst in Task 5. Ebenfalls ungeprüft ist die Variante `dotnet test Pagewing.slnx -c Release --no-build` aus der CI; im Spike lief `dotnet test Pagewing.slnx` ohne diese Optionen. Fällt sie aus, ist der Fehler in Task 5 zu beheben, ohne den Test zu überspringen.

## Offene Punkte

- **Linux-Runner:** `clang` und `zlib1g-dev` müssen installiert sein (Task 5, Step 5). Ob sie es sind, ist nicht geprüft.
- **Windows-Runner:** Ob die VS-Build-Tools mit C++-Workload installiert sind, ist nicht geprüft. Der Voraussetzungs-Schritt der CI zeigt es.
- **Dependabot:** Die Konfiguration lässt sich nur lokal als YAML validieren. Ob GitHub sie akzeptiert, zeigt die Seite „Insights → Dependency graph → Dependabot“ nach dem Merge nach `dev` und ist von Hand zu prüfen.
- **Runner-Betrieb:** Der Windows-Runner läuft vorerst als Dienst. Für GUI-Tests und den Startzeit-Check (M3) braucht er eine angemeldete Desktop-Sitzung. Das wird in M3 geplant.
