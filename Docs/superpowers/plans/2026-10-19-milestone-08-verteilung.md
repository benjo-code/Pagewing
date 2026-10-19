# Milestone 8 – Verteilung: Implementierungsplan

> **Für agentische Ausführung:** Erforderlicher Unter-Skill: `superpowers:subagent-driven-development` (empfohlen) oder `superpowers:executing-plans`. Die Schritte nutzen Checkbox-Syntax (`- [ ]`).

**Ziel:** Pagewing lässt sich unter Windows und Linux installieren (grafisch oder still, mit wählbarem Zielordner), als Standardprogramm für PDFs festlegen, und die Pakete samt Lizenzhinweisen entstehen in der CI und hängen am GitHub-Release.

**Architektur:** Alles Rechnerische (Registrierungseinträge, Desktop-Eintrag, Standardprogramm-Logik) liegt UI-frei in `Pagewing.Core.Distribution` und ist ohne Betriebssystem testbar; die Systemaufrufe liegen dünn in `Pagewing.App/Platform/{Windows,Linux}`. Pakete baut ein Einzeldatei-Werkzeug `tools/package.cs` (wie `tools/roadmap.cs`, mit `selftest`), Lizenzen prüft `tools/licenses.cs`. Der Release-Workflow baut je Betriebssystem auf dessen Runner und lädt die Dateien **direkt** ans Release, ohne GitHub-Actions-Artifacts.

**Tech Stack:** .NET 10 Native AOT, Avalonia 12.1, Inno Setup 6 (nur Build-Werkzeug), `dpkg-deb`, `mksquashfs` mit dem statischen AppImage-type2-Runtime, GitHub Actions auf den Self-hosted-Runnern.

**Spec:** [Gesamt-Spec](../specs/2026-10-16-pagewing-design.md) (Abschnitt 6, Zeile „8 Verteilung“; Abschnitt „Offline“), [Design-Spec](../specs/2026-10-17-designsystem.md) für die neuen Karten, Roadmap-Eintrag „Milestone 8“. Es gibt keine eigene Design-Datei; die Entscheidungen aus dem Gespräch stehen im Abschnitt „Festgelegt“ und kommen in Task 12 nach `Docs/DECISIONS.md`.

## Festgelegt (Gespräch mit dem Projektinhaber, 2026-10-19)

- „Release-Artefakte“ sind die fertigen Dateien am **GitHub-Release**. **Keine** Actions-Artifacts (`upload-artifact`/`download-artifact`) im Release-Workflow; jeder Betriebssystem-Job lädt seine Dateien selbst ans Release.
- Windows: grafischer Installer **und** stiller Modus (Zielordner wählbar, auch still per Parameter), Frage „Pagewing jetzt starten?“ am Ende, dazu portable ZIP.
- Standardprogramm: **nicht im Installer**, sondern eine Karte **beim ersten Start** der App. Sie führt unter Windows direkt zur Seite der Windows-Einstellungen, unter Linux setzt sie den Standard selbst (`xdg-mime`). Sie bietet „Später“ und „Nicht mehr fragen“.
- Linux: AppImage und .deb für **Ubuntu, Debian, Linux Mint und CachyOS**.

## Meine Entscheidungen (zur Prüfung markiert)

- **Mindest-glibc 2.35** (Ubuntu 22.04, Linux Mint 21, Debian 12). Ubuntu 20.04 und Debian 11 sind ausgelaufen und nicht unterstützt; CachyOS (Rolling) ist neuer. Die Entscheidung gilt nur, wenn der Linux-Runner dafür bauen kann (Task 1).
- **Inno Setup** statt WiX/MSI: kann grafisch und still, Zielordner, Start nach der Installation, Deutsch und Englisch. Es ist reines Build-Werkzeug (die Lizenz betrifft das Ergebnis nicht), Eintrag in `DECISIONS`.
- Die **Registrierung der Dateitypen übernimmt die App selbst** (`Pagewing.exe --register-file-types`), der Installer ruft sie auf. So gibt es genau eine Liste der Registry-Einträge, und die portable ZIP nutzt dieselbe für „Als Standard festlegen“.
- Das Paket enthält **App und CLI** (`pagewing`). Die CLI liegt im Unterordner `cli/` (eigene `pdfium`-Bibliothek daneben), weil unter Windows `Pagewing.exe` und `pagewing.exe` denselben Namen hätten. Unter Linux zeigt `/usr/bin/pagewing` auf die CLI; der Starter der App ruft den Pfad `/usr/lib/pagewing/Pagewing` direkt.
- Die **portable ZIP** schreibt Einstellungen wie die installierte App nach `%APPDATA%\Pagewing`, nicht neben die Exe.
- Die Dateien heißen `Pagewing-<Version>-win-x64-setup.exe`, `Pagewing-<Version>-win-x64-portable.zip`, `pagewing_<Version>_amd64.deb`, `Pagewing-<Version>-x86_64.AppImage`, dazu je Betriebssystem eine `SHA256SUMS-<os>.txt`.
- **Nicht im Umfang:** Code-Signierung (SignPath steht im Backlog; der Installer löst bis dahin SmartScreen-Warnungen aus), winget, Flatpak, MSIX, PATH-Eintrag der CLI, Auto-Update (Pagewing greift nie ins Netz).

## Globale Randbedingungen

- Native AOT, `IsAotCompatible`, Warnungen sind Fehler; keine Reflection-Serialisierung. Kein Netzwerkzugriff durch Pagewing.
- Nichts Neues im Startpfad: Die Standardprogramm-Prüfung läuft erst nach dem ersten scharfen Seitenbild (`DocumentView.FirstSharpPageDrawn` bzw. `MainViewModel.PostHistoryLoad`-Muster) und nie in Messläufen (`StartupOptions.IsMeasurement`). Die neuen Schalter in `StartupOptions` und die Karten dürfen die Startzeit-Budgets nicht berühren; Task 6 misst das.
- UI-Texte in `Strings.en.resx` und `Strings.de.resx`; Tests ohne Unterstriche; neue Tastenkürzel nur im `CommandCatalog`.
- Tests schreiben nie in den echten Benutzerordner und nie in die echte Registry (`UserSettings.Detached()`, Testwurzel-Schlüssel).
- Neue Dateien in `third-party/` und jede neue Abhängigkeit nur nach der Regel „Abhängigkeiten“ aus `CLAUDE.md`.
- „Fertig“ heißt: `dotnet build Pagewing.slnx`, `dotnet test Pagewing.slnx`, `dotnet format Pagewing.slnx --verify-no-changes` und `dotnet run tools/roadmap.cs -- check` grün, Befehl und Ergebnis genannt.

## Review-Schwerpunkte

Eingaben, die der Plan nicht aus der Spec ableitet und die Nutzer am ehesten treffen; jede hat in der genannten Task einen Test.

1. Installationspfad oder Dateiname mit Leerzeichen, Umlauten, `&` (Task 4, Task 8): `FileTypeRegistration` setzt den Befehl `"<exe>" "%1"`; der Installer-Smoke-Test installiert nach `…\Pagewing Test ä&`.
2. `$APPIMAGE` mit Leerzeichen oder `%` im Pfad (Task 5): Der Desktop-Eintrag maskiert `Exec` nach der Desktop-Entry-Spezifikation.
3. `xdg-mime` fehlt, hängt oder `settings.json` ist nicht beschreibbar (Task 6): keine Karte bzw. keine Ausnahme, die App läuft weiter.
4. Zweite Installation über eine laufende oder ältere Version (Task 8): `CloseApplications`, Smoke-Test installiert zweimal hintereinander.
5. Karte erscheint nicht, wenn eine weitere Datei an die laufende Instanz übergeben wird oder wenn Pagewing schon Standard ist (Task 6).

---

### Task 1: Linux-Build-Umgebung und glibc-Wächter

Klärt zuerst, ob der Runner für glibc 2.35 bauen kann; alles Weitere hängt daran. Spike plus ein Werkzeug, das die Grenze dauerhaft prüft.

**Files:**
- Create: `tools/package.cs` (Unterbefehl `check-binary`, `selftest`)
- Create: `tools/package-fixtures/` (kleine ELF-Dateien für den Selbsttest, mit `gcc`/`clang` im Test erzeugt oder eingecheckt, Herkunft in einer README)
- Modify: `.github/workflows/ci.yml` (Schritt „Check the packaging prerequisites (Linux)“ in `build`)

**Interfaces:**
- Produces: `dotnet run tools/package.cs -- check-binary <datei>... --max-glibc 2.35` → Exit 0, wenn jede Datei höchstens `GLIBC_2.35` verlangt und nur erlaubte `NEEDED`-Bibliotheken hat; sonst 1 mit Liste. Erlaubt: `libc.so.6`, `libm.so.6`, `libdl.so.2`, `libpthread.so.0`, `librt.so.1`, `libz.so.1`, `ld-linux-x86-64.so.2`, `libstdc++.so.6`, `libgcc_s.so.1`.

- [ ] **Schritt 1: Selbsttest schreiben** – `selftest` prüft den Parser mit festen Eingaben von `objdump -T`/`objdump -p`: `GLIBC_2.34` besteht, `GLIBC_2.38` fällt durch, ein unbekanntes `NEEDED` (`libfoo.so.1`) fällt durch. Erwartung vor der Umsetzung: `selftest` meldet „unbekannter Befehl“.
- [ ] **Schritt 2: `check-binary` umsetzen** (Aufruf von `objdump`, Versionsvergleich als Zahlentupel, nicht als Text).
- [ ] **Schritt 3:** `dotnet run tools/package.cs -- selftest` → alle Fälle bestehen.
- [ ] **Schritt 4: CI-Schritt ergänzen**, der auf dem Linux-Runner ausgibt: `cat /etc/os-release`, `ldd --version | head -1`, ob `dpkg-deb`, `mksquashfs`, `objdump`, `docker` vorhanden sind (fehlendes `dpkg-deb`/`mksquashfs`/`objdump` ist ein Fehler mit Installationshinweis, `docker` nur eine Information), und nach dem AOT-Publish `check-binary out/app/Pagewing out/app/libpdfium.so out/app/libSkiaSharp.so … --max-glibc 2.35` als **zunächst nicht blockierenden** Schritt (`continue-on-error: true`, nur in diesem Schritt).
- [ ] **Schritt 5: PR-Lauf auswerten** und die Lage dokumentieren:
  - Runner hat glibc ≤ 2.35 und alle Dateien bestehen → fertig; `continue-on-error` entfernen.
  - Sonst: Frage an den Projektinhaber (Betriebssystem des Runners, ist Docker erlaubt?). Zwei Wege, der Projektinhaber entscheidet: (a) Pakete im Container `ubuntu:22.04` bauen (SDK dort installiert), (b) die Mindest-glibc auf die des Runners anheben und Ubuntu 22.04, Linux Mint 21 und Debian 12 streichen. Erst danach geht Task 9 weiter.
- [ ] **Schritt 6: Commit** `ci: glibc-Wächter und Voraussetzungen für Linux-Pakete`

### Task 2: Lizenzinventar und Hinweisdatei

**Files:**
- Create: `tools/licenses.cs` (Befehle `generate`, `check`, `selftest`)
- Create: `third-party/licenses.json` (Manifest), ggf. weitere Lizenztexte unter `third-party/<name>/`
- Create: `src/Pagewing.App/Resources/ThirdPartyNotices.txt` (erzeugt, eingecheckt)
- Modify: `src/Pagewing.App/Pagewing.App.csproj` (`EmbeddedResource` mit `LogicalName="Pagewing.App.Resources.ThirdPartyNotices.txt"`), `.github/workflows/ci.yml` (Schritt `licenses check` im Job `build`)

**Interfaces:**
- Manifest-Eintrag: `{ "id": string, "kind": "nuget"|"native"|"asset"|"runtime", "version": string, "license": "<SPDX>", "licenseFile": "third-party/…", "files": [string] }`; `files` nennt die Dateien im Publish-Ordner, die zu diesem Eintrag gehören (z. B. `libpdfium.so`).
- `licenses check [--publish-dir <ordner>]...` prüft:
  1. Jedes Paket aus `dotnet list <projekt> package --include-transitive --format json` (App und CLI) steht mit gleicher Version im Manifest, und seine SPDX-Angabe ist die der `.nuspec` im NuGet-Cache.
  2. Jede `licenseFile` existiert und ist nicht leer.
  3. Jede native Datei in den Publish-Ordnern (alles außer der eigenen Exe) gehört über `files` zu genau einem Eintrag.
  4. Die PDFium-Version im Manifest gleicht `Directory.Packages.props` (`bblanchon.PDFium.*`), beide Pakete haben dieselbe Version.
  5. `ThirdPartyNotices.txt` ist aktuell (wie `roadmap check`).

- [ ] **Schritt 1: Fixtures und Selbsttest** in `tools/licenses.cs selftest`: fehlender Manifest-Eintrag, falsche Version, SPDX-Abweichung, native Datei ohne Besitzer, veraltete Hinweisdatei – jeder Fall liefert Exit 1 mit dem Pfad im Fehler; ein vollständiger Fixture-Satz Exit 0. Läuft rot, weil der Befehl fehlt.
- [ ] **Schritt 2: `check` und `generate` umsetzen.** `generate` schreibt je Eintrag Name, Version, SPDX und den vollen Lizenztext, getrennt durch eine Zeile `====`; vorweg Pagewing selbst (GPL-3.0-or-later).
- [ ] **Schritt 3: Manifest füllen** aus einem echten Publish (`dotnet publish src/Pagewing.App -c Release -r <rid> -o out/app`, ebenso CLI): jedes Paket und jede native Datei eintragen. Bereits vorhanden sind `third-party/pdfium` (mit den rund 20 Lizenzen), `geist`, `tabler-icons`. Für SkiaSharp, HarfBuzzSharp und deren mitgelinkte Bibliotheken, Avalonia, MicroCom, Tmds.DBus.Protocol, die .NET-Laufzeit (MIT, im AOT-Binary) und den Native-AOT-Runtime-Anteil die Lizenzdateien aus den Paketen bzw. den Upstream-Repos beschaffen und ablegen; was ein Paket nicht mitbringt, wie bei PDFium in `third-party/pdfium/README.md` mit Quelle und Prüfsumme festhalten.
- [ ] **Schritt 4:** `dotnet run tools/licenses.cs -- selftest` → grün; `generate`, dann `check --publish-dir out/app --publish-dir out/cli` → Exit 0.
- [ ] **Schritt 5: CI-Schritt** `licenses check` nach den Publish-Schritten auf beiden Betriebssystemen; ein Dependabot-Update eines Pakets schlägt damit fehl, bis das Manifest passt.
- [ ] **Schritt 6: Commit** `feat: Lizenzinventar der Drittbibliotheken mit Prüfung in der CI`

### Task 3: Versionsangabe und Über-Karte

**Files:**
- Create: `src/Pagewing.Core/AppInfo.cs`, `src/Pagewing.App/ViewModels/AboutViewModel.cs`, `src/Pagewing.App/Views/AboutCard.axaml(.cs)`
- Modify: `ViewModels/OverlayKind.cs` (`About`), `ViewModels/MainViewModel.cs`, `Views/MainWindow.Overlays.cs`, `Commands/CommandCatalog.cs` (`app.about`, ohne Kürzel), beide `Strings.*.resx`
- Test: `tests/Pagewing.Core.Tests/AppInfoTests.cs`, `tests/Pagewing.App.Tests/AboutCardTests.cs`

**Interfaces:**
- Produces: `public static class AppInfo { public static string Version { get; } }` – `AssemblyInformationalVersion` ohne den Teil ab `+`; `"0.0.0"` ohne gesetzte Version.
- Produces: `AboutViewModel(string version, Func<string> loadNotices)` mit `Name`, `Version`, `LicenseLine` („GPL-3.0-or-later“), `OfflineLine` (keine Telemetrie, keine Netzwerkzugriffe), `NoticesText` (lädt beim ersten Lesen).

- [ ] **Schritt 1: Tests** – `AppInfoTests.VersionDropsTheBuildMetadata` (`1.2.3+abc` → `1.2.3`); `AboutCardTests.ShowsNameVersionAndLicense`, `NoticesAreLoadedOnlyWhenTheListIsOpened`, `NoticesContainPdfium` (Text enthält „PDFium“ und „FreeType“), `EscClosesTheCardAndFocusReturns`, `TheCardColoursMeetAA`, `PaletteListsTheAboutCommand`, `TheCardIsNotBuiltAtStartup`.
- [ ] **Schritt 2:** Tests laufen rot (`dotnet test --project tests/Pagewing.App.Tests --filter-class "*AboutCardTests"`).
- [ ] **Schritt 3: Umsetzen** nach dem Muster von `HelpCard` (Overlay entsteht erst beim ersten Zeigen; Datenvorlage samt Styles in der eigenen `.axaml`). Hinweise lädt `AboutViewModel` aus der eingebetteten Ressource, nicht beim Erstellen der Karte. Gestaltung und Maße aus der Design-Spec lesen und Tokens verwenden, keine neuen Farben. Kein klickbarer Link (jeder externe Link läuft sonst durch `ExternalLink`).
- [ ] **Schritt 4:** `dotnet test --project tests/Pagewing.App.Tests --filter-class "*AboutCardTests"` und `…Core.Tests --filter-class "*AppInfoTests"` → grün; `dotnet format … --verify-no-changes` grün.
- [ ] **Schritt 5: Commit** `feat: Über-Karte mit Version und Drittlizenzen`

### Task 4: Dateityp-Registrierung für Windows

**Files:**
- Create: `src/Pagewing.Core/Distribution/FileTypeRegistration.cs`, `src/Pagewing.App/Platform/Windows/WindowsFileTypeRegistry.cs`
- Modify: `src/Pagewing.App/StartupOptions.cs`, `src/Pagewing.App/Program.cs`
- Test: `tests/Pagewing.Core.Tests/Distribution/FileTypeRegistrationTests.cs`, `tests/Pagewing.App.Tests/StartupOptionsTests.cs`, `tests/Pagewing.App.Tests/WindowsFileTypeRegistryTests.cs`

**Interfaces:**
- Produces (Core, `Pagewing.Core.Distribution`):
  ```csharp
  public enum RegistrationScope { User, Machine }
  public readonly record struct RegistryEntry(string KeyPath, string? ValueName, string Value);
  public readonly record struct RegistryRemoval(string KeyPath, string? ValueName); // ValueName null = ganzer Schlüssel
  public static class FileTypeRegistration {
      public const string ProgId = "Pagewing.PDF";
      public static IReadOnlyList<RegistryEntry> Entries(string exePath);
      public static IReadOnlyList<RegistryRemoval> Removals();
  }
  ```
  Schlüsselpfade relativ zur Wurzel (`HKCU` bzw. `HKLM`), Eintragsliste: `Software\Classes\Pagewing.PDF` (Name, `shell\open\command` = `"<exe>" "%1"`, `DefaultIcon` = `"<exe>",0`), `Software\Classes\.pdf\OpenWithProgids` (Wert `Pagewing.PDF`, leer), `Software\Classes\Applications\Pagewing.exe\shell\open\command`, `Software\Pagewing\Capabilities` (`ApplicationName`, `ApplicationDescription`) samt `FileAssociations` (`.pdf` = `Pagewing.PDF`), `Software\RegisteredApplications` (`Pagewing` = `Software\Pagewing\Capabilities`).
- Produces (App): `WindowsFileTypeRegistry(RegistryKey root)` mit `Register(string exePath)`, `Unregister()`, `bool IsRegistered`; `StartupOptions.RegisterFileTypes` und `UnregisterFileTypes` vom Typ `RegistrationScope?` (Schalter `--register-file-types[=user|machine]`, `--unregister-file-types[=user|machine]`, Standard `user`).

- [ ] **Schritt 1: Kerntests** – `EntriesHaveTheOpenCommandForTheExe`, `CommandQuotesAPathWithSpacesAndUmlauts` (`C:\Program Files\Pagewing Test ä&\Pagewing.exe` ergibt `"C:\Program Files\Pagewing Test ä&\Pagewing.exe" "%1"`), `EveryEntryHasARemoval` (jeder Schlüssel bzw. Wert aus `Entries` wird von `Removals` erfasst), `NothingTouchesTheUserChoice` (kein Pfad enthält `UserChoice` oder `FileExts`).
- [ ] **Schritt 2:** rot; `FileTypeRegistration` umsetzen; grün.
- [ ] **Schritt 3: Schalter-Tests** – `StartupOptionsTests.RegisterSwitchDefaultsToUser`, `MachineScopeIsParsed`, `UnknownScopeIsIgnored`, `RegisterSwitchIsNotAMeasurement`. Umsetzen in `StartupOptions.Parse`. In `Program.Main` behandelt Windows die Schalter **direkt nach der `AppContext`-Initialisierung** und vor Avalonia (Exit 0, bei Fehler 1 und `error: …` auf stderr); unter Linux werden sie ignoriert.
- [ ] **Schritt 4: Registry-Tests, nur Windows, sonst übersprungen** (Muster `WindowsPrintToPdfTests`): `RegisterWritesEveryEntryUnderTheTestRoot`, `UnregisterRemovesThemAgain`, `RegisterTwiceIsHarmless`. Die Wurzel ist ein frischer Unterschlüssel `Software\PagewingTests\<Guid>` in `HKCU`, nach dem Test gelöscht. `Registry.CurrentUser` selbst fassen Tests nie an.
- [ ] **Schritt 5:** `dotnet test --project tests/Pagewing.Core.Tests --filter-class "*FileTypeRegistrationTests"`, `…App.Tests --filter-class "*StartupOptionsTests"` grün; Windows-Läufe laufen in der CI, in der Linux-Umgebung als übersprungen gemeldet (offen: Lauf unter Windows, Befehl `dotnet test --project tests/Pagewing.App.Tests --filter-class "*WindowsFileTypeRegistryTests"`).
- [ ] **Schritt 6: Commit** `feat: Dateityp-Registrierung für Windows (--register-file-types)`

### Task 5: Standardprogramm-Dienst (Windows und Linux)

**Files:**
- Create: `src/Pagewing.Core/Distribution/IDefaultAppService.cs`, `src/Pagewing.Core/Distribution/DesktopEntry.cs`, `src/Pagewing.App/Platform/Windows/WindowsDefaultAppService.cs`, `src/Pagewing.App/Platform/Linux/LinuxDefaultAppService.cs`, `src/Pagewing.App/Services/PlatformDefaultAppServices.cs` (Wahl nach Betriebssystem, erst beim ersten Gebrauch, wie `PlatformPrintServices`)
- Test: `tests/Pagewing.Core.Tests/Distribution/DesktopEntryTests.cs`, `tests/Pagewing.App.Tests/LinuxDefaultAppServiceTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public enum DefaultAppStatus { Unknown, IsDefault, NotDefault }
  public enum DefaultAppResult { Done, OpenedSystemSettings, NotAvailable, Failed }
  public interface IDefaultAppService {
      bool IsSupported { get; }
      Task<DefaultAppStatus> GetStatusAsync(CancellationToken cancellationToken);
      Task<DefaultAppResult> MakeDefaultAsync(CancellationToken cancellationToken);
  }
  public static class DesktopEntry {
      public static string Render(string execPath, string iconName);   // Inhalt der .desktop-Datei
      public static string EscapeExec(string path);                    // nach der Desktop-Entry-Spezifikation
  }
  ```
- Windows: Status aus `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\UserChoice` (nur lesen; `ProgId` gleich `Pagewing.PDF`). `MakeDefaultAsync` registriert per `WindowsFileTypeRegistry` unter `HKCU`, wenn noch nicht registriert, und öffnet dann `ms-settings:defaultapps?registeredAppUser=Pagewing` → `OpenedSystemSettings`. Pagewing schreibt `UserChoice` nie.
- Linux: Status aus `xdg-mime query default application/pdf` (gleich `pagewing.desktop`). `MakeDefaultAsync`: Ist die Umgebungsvariable `APPIMAGE` gesetzt, schreibt der Dienst zuerst `~/.local/share/applications/pagewing.desktop` (aus `DesktopEntry.Render`, `Exec=` auf den AppImage-Pfad) und die Symbole aus `$APPDIR` nach `~/.local/share/icons/hicolor/<N>x<N>/apps/pagewing.png`; dann `xdg-mime default pagewing.desktop application/pdf` und, falls vorhanden, `update-desktop-database`. Prozesse starten mit `ProcessStartInfo.ArgumentList`, nie über eine Shell, mit 5-Sekunden-Zeitlimit; fehlt `xdg-mime` → `NotAvailable`.

- [ ] **Schritt 1: Kerntests** – `DesktopEntryTests.ExecEscapesSpacesAndPercent` (`/home/a b/Pagewing%1.AppImage` → in der Zeile `Exec="/home/a b/Pagewing%%1.AppImage" %F`), `RenderHasMimeTypeIconAndWmClass` (`MimeType=application/pdf;`, `StartupWMClass=Pagewing`, `Icon=pagewing`), `RenderIsTheSameForTheTemplateInPackaging` (Vorlage in `packaging/linux/pagewing.desktop` und `Render` unterscheiden sich nur im `Exec`).
- [ ] **Schritt 2:** rot, `DesktopEntry` umsetzen, grün.
- [ ] **Schritt 3: Linux-Dienst-Tests** gegen ein Stub-Verzeichnis im `PATH` mit Skript-Attrappen für `xdg-mime` (schreiben ihre Argumente in eine Datei): `StatusIsDefaultWhenXdgMimeAnswersPagewing`, `StatusIsUnknownWithoutXdgMime`, `MakeDefaultCallsXdgMimeWithoutAShell`, `MakeDefaultFromAnAppImageWritesDesktopFileAndIcons` (mit `HOME` und `XDG_DATA_HOME` in einem Temp-Ordner), `ATimeoutEndsAsFailed`. Läuft nur unter Linux.
- [ ] **Schritt 4:** beide Dienste umsetzen; Windows-Dienst ohne eigenen Test außer dem Aufbau des Aufrufs hinter einer kleinen Naht (`Func<string, Task> openUri`), geprüft mit `MakeDefaultOpensTheDefaultAppsPageOfPagewing` unter Windows.
- [ ] **Schritt 5:** `dotnet test --project tests/Pagewing.Core.Tests --filter-class "*DesktopEntryTests"` und `…App.Tests --filter-class "*LinuxDefaultAppServiceTests"` grün.
- [ ] **Schritt 6: Commit** `feat: Dienst „Standardprogramm festlegen“ für Windows und Linux`

### Task 6: Karte „Standardprogramm“ beim ersten Start

**Files:**
- Create: `src/Pagewing.App/ViewModels/DefaultAppViewModel.cs`, `src/Pagewing.App/Views/DefaultAppCard.axaml(.cs)`
- Modify: `src/Pagewing.Core/Settings/AppSettings.cs` (+ Source-Generator-Kontext, falls nötig), `ViewModels/MainViewModel.cs` (`DefaultAppServiceFactory`, `PostDefaultAppCheck`), `Views/MainWindow.axaml`, `Commands/CommandCatalog.cs` (`app.setDefault`), beide `Strings.*.resx`, Messläufe bleiben unberührt
- Test: `tests/Pagewing.App.Tests/DefaultAppPromptTests.cs`, `tests/Pagewing.Core.Tests/Settings/SettingsStoreTests.cs` (Rundreise-Test erweitern)

**Interfaces:**
- Consumes: `IDefaultAppService`, `DefaultAppStatus`, `DefaultAppResult` (Task 5), `UserSettings`.
- Produces: `public enum DefaultAppPromptSetting { Ask, Never }`; `AppSettings.DefaultAppPrompt` (`{ get; set; }`, Standard `Ask`); `MainViewModel.DefaultAppPrompt` (`DefaultAppViewModel?`, `null` = keine Karte); `MainViewModel.DefaultAppServiceFactory` (`Func<IDefaultAppService>`, Tests ersetzen sie wie `PrintServiceFactory`).
- `DefaultAppViewModel`: `SetDefaultCommand`, `LaterCommand` (nur diese Sitzung), `NeverCommand` (schreibt `Never`), `Message` (Ergebnis nach `OpenedSystemSettings`/`Failed`).

- [ ] **Schritt 1: Tests** (Fake-Dienst `FakeDefaultAppService`, `UserSettings.Detached()`):
  - `TheCardShowsAfterTheFirstSharpPageWhenNotDefault`
  - `NothingShowsWhenPagewingIsAlreadyDefault`, `NothingShowsAfterNever`, `NothingShowsInAMeasurementRun`
  - `NothingShowsForAnUnsupportedPlatform` (`IsSupported == false`)
  - `AFailingStatusCheckShowsNothing` (Dienst wirft oder liefert `Unknown`)
  - `TheCardShowsOnlyOncePerProcess` (zweites Fenster und `OnExternalOpen` lösen sie nicht erneut aus)
  - `LaterClosesWithoutSaving`, `NeverSavesAndSurvivesARestart` (neu geladene Einstellungen aus dem Temp-Ordner), `UnwritableSettingsStillCloseTheCard`
  - `SetDefaultClosesOnDoneAndShowsAHintOnOpenedSettings`, `FailedKeepsTheCardWithAMessage`
  - `StatusCheckNeverRunsBeforeTheFirstSharpPage` (der Dienst wird erst danach aufgerufen)
  - `TheCommandSetsTheDefaultEvenAfterNever` (Palette `app.setDefault`)
  - `TheCardColoursMeetAA`, `KeyboardOnlyUseWorks` (Tab, Enter, Esc = „Später“)
  - Einstellungs-Rundreise: `RoundTripKeepsEveryField` kennt das neue Feld.
- [ ] **Schritt 2:** rot (`dotnet test --project tests/Pagewing.App.Tests --filter-class "*DefaultAppPromptTests"`).
- [ ] **Schritt 3: Umsetzen.** Die Prüfung startet aus dem Ereignis des ersten scharfen Bildes bzw. dem ersten Frame der Startseite und läuft auf dem Pool, das Ergebnis geht über den Dispatcher zurück; der UI-Thread wartet nie auf `xdg-mime` oder die Registry. Die Karte ist **nicht modal** (kein `OverlayKind`, sie nimmt dem Fenster keinen Fokus), erscheint nur wenn `Overlay == None`, und folgt in Form und Maßen den vorhandenen Karten der Design-Spec (nachlesen, vor dem Bauen den Abschnitt zu Karten und Hinweisen im Dokument nennen). Texte: Titel „Pagewing als Standard für PDFs festlegen?“, Knöpfe „Als Standard festlegen“, „Später“, „Nicht mehr fragen“ (englisch sinngemäß).
- [ ] **Schritt 4: Startzeit prüfen.** Mit AOT `win-x64` bzw. `linux-x64` den Startzeit-Check wie in M6 im Wechsel gegen `dev` messen (`bench/startup/startup.cs --case cold|warm`, Protokoll `bench/startup/README.md`); die Karte darf die Mediane nicht über das Rauschen heben. Ohne GPU/Desktop-Takt nur relative Werte, als solche festhalten.
- [ ] **Schritt 5:** `dotnet test --project tests/Pagewing.App.Tests`, `…Core.Tests`, `dotnet format … --verify-no-changes` grün.
- [ ] **Schritt 6: Commit** `feat: Karte „Als Standardprogramm festlegen“ beim ersten Start`

### Task 7: Paketlayout und portable ZIP

**Files:**
- Create: `packaging/README.md`, `packaging/linux/pagewing.desktop` (aus `Docs/design/icons/linux/pagewing.desktop` übernommen und korrigiert), `packaging/windows/` (für Task 8)
- Modify: `tools/package.cs` (Befehle `layout`, `zip`, `sums`, `selftest`), `Docs/design/icons/README.md` (Abschnitt „Linux (M8)“ verweist auf `packaging/`)

**Interfaces:**
- Produces:
  - `package.cs layout --rid <win-x64|linux-x64> --app <ordner> --cli <ordner> --out <ordner>`: kopiert die App (ohne `*.pdb`, `*.dbg`) in die Wurzel, die CLI nach `cli/`, dazu `LICENSE` und `THIRD-PARTY-NOTICES.txt` (aus `ThirdPartyNotices.txt`). Bricht ab, wenn die Native-Bibliothek von PDFium (`pdfium.dll` bzw. `libpdfium.so`) fehlt.
  - `package.cs zip --layout <ordner> --version <x.y.z> --out <datei>`: ZIP mit stabiler Reihenfolge und festen Zeitstempeln; oberster Ordner `Pagewing-<Version>/`.
  - `package.cs sums <datei>... --out <SHA256SUMS-…txt>`: Zeilen `<sha256>  <dateiname>`.
- Korrektur der Vorlage: `Exec` (für das .deb `/usr/lib/pagewing/Pagewing %F`), `StartupWMClass` gegen den echten Fensterklassennamen der App prüfen (unter X11 Klasse des Fensters; mit `xprop WM_CLASS` am laufenden Fenster belegen und den Wert im Test festhalten), `Comment[en]`, `Name[de]`.

- [ ] **Schritt 1: Selbsttest-Fälle** – `layout` lässt `.pdb` weg, legt `cli/` an, bricht ohne PDFium-Bibliothek mit Fehler ab; `zip` ist reproduzierbar (zweimal erzeugt gleiche SHA-256); `sums` schreibt das Format oben. Rot, weil die Befehle fehlen.
- [ ] **Schritt 2:** Befehle umsetzen; `dotnet run tools/package.cs -- selftest` grün.
- [ ] **Schritt 3: Von Hand prüfen** mit echten Publish-Ordnern auf dem eigenen Betriebssystem: `layout`, `zip`, ZIP entpacken und `cli/pagewing info testdata/pdfs/text-basic.pdf` laufen lassen (Befehl und Ausgabe in den Commit-Text).
- [ ] **Schritt 4: Commit** `feat: Paketlayout und portable ZIP (tools/package.cs)`

### Task 8: Windows-Installer

**Files:**
- Create: `packaging/windows/pagewing.iss`
- Modify: `tools/package.cs` (Befehle `inno-install`, `installer`, `installer-smoke`, Erweiterung des `selftest`), `packaging/README.md`

**Interfaces:**
- Consumes: Layout aus Task 7, `Pagewing.exe --register-file-types[=user|machine]` / `--unregister-file-types` (Task 4).
- Produces: `package.cs installer --layout <ordner> --version <x.y.z> --out <datei>` ruft `iscc.exe` und liefert `Pagewing-<Version>-win-x64-setup.exe`; `package.cs installer-smoke <setup.exe>`.
- Verhalten des Installers: `PrivilegesRequired=lowest` mit `PrivilegesRequiredOverridesAllowed=commandline dialog` (Benutzer- oder Computerinstallation, Standard `{autopf}\Pagewing` bzw. benutzerbezogen), Zielordner-Seite, `CloseApplications=yes`, `ChangesAssociations=yes`, Lizenzseite mit der GPL, Sprachen Englisch und Deutsch. `[Run]`: erst `Pagewing.exe --register-file-types=<user|machine>` (versteckt), dann – nur im grafischen Modus – „Pagewing jetzt starten“ (`postinstall skipifsilent`). `[UninstallRun]`: `--unregister-file-types=<scope>`. Still: `Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR="<ordner>"`. Einstellungen in `%APPDATA%\Pagewing` bleiben beim Deinstallieren erhalten.

- [ ] **Schritt 1: Werkzeug bereitstellen.** `inno-install` lädt die feste Version von Inno Setup 6 (die zum Umsetzungszeitpunkt neueste 6.x-Version), prüft die **SHA-256** gegen den im Skript festgehaltenen Wert und installiert sie mit `/VERYSILENT /CURRENTUSER` in einen Ordner im Benutzerprofil des Runners (kein Administrator nötig); ist `iscc.exe` schon da, passiert nichts. Version, Quelle und Prüfsumme in `DECISIONS`.
- [ ] **Schritt 2: Smoke-Test schreiben** (`installer-smoke`, Windows): (a) still installieren nach `%TEMP%\Pagewing Test ä&`; (b) prüfen: `Pagewing.exe` und `cli\pagewing.exe` da, `cli\pagewing.exe info testdata/pdfs/text-basic.pdf` läuft, `HKCU\Software\Classes\Pagewing.PDF` vorhanden; (c) **zweite** Installation über die erste, Exit 0; (d) Deinstaller `unins000.exe /VERYSILENT` – Ordner und Schlüssel weg, `%APPDATA%\Pagewing` unberührt; (e) Exit 1 mit Beschreibung bei jedem Fehlschlag. Rot, weil es noch kein Setup gibt.
- [ ] **Schritt 3: `.iss` und Befehle umsetzen.**
- [ ] **Schritt 4:** auf einem Windows-Rechner `dotnet run tools/package.cs -- installer …` und `installer-smoke …` → Exit 0 (in der Linux-Umgebung nicht prüfbar, als offen markieren).
- [ ] **Schritt 5: Commit** `feat: Windows-Installer (Inno Setup) mit stillem Modus und Dateityp-Registrierung`

### Task 9: Linux-.deb

**Files:**
- Create: `packaging/linux/control.template`, `packaging/linux/copyright.template`
- Modify: `tools/package.cs` (Befehle `deb`, `deb-smoke`, Erweiterung des `selftest`)

**Interfaces:**
- Consumes: Layout, `packaging/linux/pagewing.desktop`, Symbole aus `Docs/design/icons/linux/hicolor`.
- Produces: `package.cs deb --layout <ordner> --version <x.y.z> --out <datei>` → `pagewing_<Version>_amd64.deb`.
- Inhalt: `/usr/lib/pagewing/` (App, `cli/`), `/usr/bin/pagewing` → `/usr/lib/pagewing/cli/pagewing`, `/usr/share/applications/pagewing.desktop`, Symbole `/usr/share/icons/hicolor/<N>x<N>/apps/pagewing.png`, `/usr/share/doc/pagewing/copyright` (Debian-Copyright-Format: Pagewing GPL-3.0-or-later plus Verweis auf `THIRD-PARTY-NOTICES.txt` im selben Ordner). Felder: `Package: pagewing`, `Architecture: amd64`, `Section: graphics`, `Depends: libc6 (>= 2.35), libfontconfig1, libx11-6, libice6, libsm6`, `Recommends: libcups2, xdg-utils`, `Maintainer:` siehe offene Frage. Besitzer `root:root`, Rechte 0755/0644.

- [ ] **Schritt 1: Selbsttest-Fälle** – das erzeugte `control` hat die Pflichtfelder und die Versionsform `0.1.0`; Dateiliste und Rechte stimmen; `deb-smoke` entpackt mit `dpkg-deb -x` in einen Temp-Ordner, prüft `Exec` der `.desktop` gegen eine vorhandene Datei, ruft `cli/pagewing info` auf und führt `desktop-file-validate` und `lintian --no-tag-display-limit` aus, **wenn sie vorhanden sind** (sonst Hinweis, kein Fehler). Rot.
- [ ] **Schritt 2: Umsetzen** (Staging-Ordner mit Rechten, `dpkg-deb --root-owner-group --build`).
- [ ] **Schritt 3:** `dotnet run tools/package.cs -- selftest`, dann `deb` und `deb-smoke` mit einem echten Linux-Publish → Exit 0; `dpkg-deb -I` und `-c` in den Commit-Text.
- [ ] **Schritt 4: Installation prüfen**, soweit die Umgebung es erlaubt (`dpkg -i` in einem Container oder einer VM mit Ubuntu 22.04, Debian 12, Linux Mint; Doppelklick auf ein PDF im Dateimanager); was hier nicht geht, bleibt als manuelle Abnahme offen.
- [ ] **Schritt 5: Commit** `feat: Linux-Paket .deb mit Starter und MIME-Zuordnung`

### Task 10: AppImage

**Files:**
- Create: `packaging/linux/AppRun`
- Modify: `tools/package.cs` (Befehle `appimage`, `appimage-smoke`, Erweiterung des `selftest`)

**Interfaces:**
- Produces: `package.cs appimage --layout <ordner> --version <x.y.z> --runtime <datei> --out <datei>` → `Pagewing-<Version>-x86_64.AppImage`, gebaut als **type2-Runtime + SquashFS** (`mksquashfs … -root-owned -noappend -comp zstd`, Runtime davor gesetzt, ausführbar). Die Runtime ist der **statisch gelinkte** type2-Runtime des AppImage-Projekts in einer festen Version mit SHA-256 im Skript (so braucht der Nutzer kein `libfuse2`); `package.cs runtime-fetch` lädt und prüft sie.
- AppDir: `AppRun` (`exec "$APPDIR/usr/lib/pagewing/Pagewing" "$@"`), `pagewing.desktop` und `pagewing.png` im Wurzelordner, `.DirIcon`, `usr/lib/pagewing/…`. Die `.desktop` im AppImage ist die Vorlage mit `Exec=Pagewing %F`; die systemweite Integration schreibt `LinuxDefaultAppService` (Task 5).

- [ ] **Schritt 1: Selbsttest-Fälle** – `AppRun` zeigt auf eine vorhandene Datei, die SHA-256 der Runtime stimmt oder der Befehl bricht ab; `appimage-smoke` führt das fertige Image mit `--appimage-extract` in einem Temp-Ordner aus und startet `squashfs-root/AppRun`-nahes `cli`-Programm (`usr/lib/pagewing/cli/pagewing info testdata/pdfs/text-basic.pdf`), prüft außerdem, dass das Image **ohne** FUSE startet (`APPIMAGE_EXTRACT_AND_RUN=1` als Ersatz, wenn `/dev/fuse` fehlt). Rot.
- [ ] **Schritt 2: Umsetzen.**
- [ ] **Schritt 3:** `selftest`, `appimage`, `appimage-smoke` grün; Image von Hand in einem Pfad mit Leerzeichen starten und eine PDF öffnen (`./Pagewing\ Test/Pagewing-….AppImage testdata/pdfs/text-rich.pdf`).
- [ ] **Schritt 4: Commit** `feat: AppImage (type2-Runtime, ohne FUSE-Pflicht)`

### Task 11: Release-Workflow

**Files:**
- Create: `.github/workflows/release.yml`
- Modify: `.github/workflows/ci.yml` (Voraussetzungs-Schritte um `mksquashfs` und `dpkg-deb` ergänzen, falls in Task 1 nicht schon geschehen)

**Interfaces:**
- Consumes: `tools/package.cs`, `tools/licenses.cs`, `tools/smoke-cli.cs`.
- Verhalten: Auslöser `release: types: [published]` und `workflow_dispatch` mit Eingabe `tag`. Rechte nur `contents: write`. Ein Job je Betriebssystem (`[self-hosted, dotnet-windows]` bzw. `dotnet-linux`), gleiche gepinnte Actions wie `ci.yml`. Ablauf je Job: Tag auf SemVer `vX.Y.Z` prüfen (sonst Fehler), Checkout des Tags, SDK, Voraussetzungen prüfen, `dotnet publish … -p:Version=<ohne v>` für App und CLI, `licenses check`, `check-binary` (Linux), Paketbau, Smoke-Tests (`installer-smoke`, `deb-smoke`, `appimage-smoke`), `SHA256SUMS-<os>.txt`, dann `gh release upload "$TAG" <dateien> --clobber`. **Kein** `upload-artifact`. Beim `workflow_dispatch` mit dem Tag eines Entwurfs oder ohne Release laufen Bau und Smoke-Tests, das Hochladen entfällt.
- Hinweis: Die vorhandene Regel „Repo privat → Forks freigeben“ aus `CLAUDE.md` gilt unverändert; der Workflow läuft nie für Forks.

- [ ] **Schritt 1: Workflow schreiben** und mit `actionlint` (falls vorhanden) bzw. `dotnet run tools/roadmap.cs -- check` unabhängig davon auf YAML-Fehler prüfen; Voraussetzung `gh` auf den Runnern als eigener Schritt mit Installationshinweis prüfen.
- [ ] **Schritt 2: Trockenlauf** per `workflow_dispatch` auf dem Branch (Bau und Smoke-Tests, ohne Hochladen) auf beiden Runnern; Logs prüfen, Fehler beheben, bis beide grün sind. Dem Projektinhaber melden, dass dafür seine Runner laufen müssen.
- [ ] **Schritt 3: Erster echter Lauf** gehört zum Release `v0.1.0`, nicht zu M8 (Release nur auf Anweisung). Bis dahin ist das Hochladen ungeprüft und als offen zu markieren: `gh release create v0.1.0-rc1 --prerelease --target dev …` als Probe mit anschließendem Löschen **nur** auf ausdrückliche Anweisung.
- [ ] **Schritt 4: Commit** `ci: Release-Workflow baut Installer, ZIP, .deb und AppImage`

### Task 12: Abschluss – Doku, Roadmap, Entscheidungen

**Files:**
- Modify: `Docs/DECISIONS.md`, `CLAUDE.md` (Abschnitt „Projektspezifisches“: neue Unterpunkte „Verteilung (seit M8)“ und die Runner-Voraussetzungen unter „CI“), `README.md` (Abschnitt „Installation“), `CHANGELOG.md` (`[Unreleased]`, Added), `Docs/roadmap.json` und `Docs/ROADMAP.md`

- [ ] **Schritt 1: `Docs/DECISIONS.md`**, je ein Eintrag: Mindest-glibc und Bauweise (Ergebnis aus Task 1); Installer-Technik Inno Setup samt Version und Prüfsumme; Registrierung durch die App selbst; Paketinhalt mit `cli/` und Dateinamen; Standardprogramm-Karte beim ersten Start (Gründe: Windows lässt keine stille Umstellung zu, AppImage und .deb haben keinen Installer); Lizenzinventar und Prüfungen; Release ohne Actions-Artifacts; Nicht-Ziele (Signierung, winget, Flatpak, MSIX, Auto-Update).
- [ ] **Schritt 2: `Docs/roadmap.json`:** Plan verlinken, Status `in-progress` bzw. am Ende `done` mit `completedOn`, jede Aufgabe mit Nachweis abhaken. Abnahmekriterien erst abhaken, wenn sie wirklich geprüft sind: „Lizenzhinweise vollständig“ mit dem Ergebnis von `dotnet run tools/licenses.cs -- check`; „Installation, Doppelklick auf PDF und Deinstallation auf frischem Windows und frischer Linux-Distribution“ **bleibt offen**, bis der Projektinhaber es manuell geprüft hat. In der `v0.1.0`-Abnahme ergänzen: Installation auf Ubuntu, Debian, Linux Mint und CachyOS (.deb bzw. AppImage), Windows-Installer grafisch und still, Standardprogramm-Karte unter beiden, Start nach der Installation, SmartScreen-Hinweis zur Kenntnis genommen.
- [ ] **Schritt 3:** `dotnet run tools/roadmap.cs -- generate`, danach `-- check`.
- [ ] **Schritt 4: Gesamtlauf:** `dotnet build Pagewing.slnx`, `dotnet test Pagewing.slnx`, `dotnet format Pagewing.slnx --verify-no-changes`, `dotnet run tools/roadmap.cs -- check`, `dotnet run tools/package.cs -- selftest`, `dotnet run tools/licenses.cs -- selftest`; Ergebnisse mit Zahlen in den Roadmap-Nachweisen.
- [ ] **Schritt 5: Commit** `docs: Milestone 8 – Entscheidungen, Roadmap, Changelog`

---

## Selbstprüfung

- **Abdeckung der Roadmap-Aufgaben:** Windows Installer und ZIP, Registrierung (Tasks 4, 7, 8) · Linux AppImage und .deb, `.desktop`, MIME, glibc (Tasks 1, 7, 9, 10) · Lizenzhinweise im Paket und im Über-Dialog (Tasks 2, 3, 7) · Release-Artefakte per CI (Task 11) · Wünsche aus dem Gespräch: Zielordner, still, Start nach Installation, Standardprogramm-Karte (Tasks 5, 6, 8).
- **Abnahmekriterien:** „Lizenzhinweise vollständig“ ist automatisch prüfbar (Task 2); „Installation, Doppelklick, Deinstallation auf frischem System“ ist manuell und bleibt offen; Teile davon deckt der Installer-Smoke-Test auf dem Runner ab.
- **Typenabgleich:** `RegistrationScope`, `RegistryEntry`, `RegistryRemoval`, `FileTypeRegistration` (Task 4) werden in Task 5 und Task 8 benutzt; `IDefaultAppService`, `DefaultAppStatus`, `DefaultAppResult` (Task 5) in Task 6; `DefaultAppPromptSetting` nur in Task 6.

## Offene Fragen an den Projektinhaber

1. **Linux-Runner:** Welches Betriebssystem und welche glibc hat `dotnet-linux`, und darf dort Docker laufen? (Entscheidet, ob Ubuntu 22.04, Mint 21 und Debian 12 unterstützt werden, siehe Task 1.)
2. **`Maintainer` im .deb** braucht einen Namen und eine Kontaktadresse; welche soll hinein (Debian verlangt das Format `Name <adresse>`)?
3. **CLI im Paket:** Soll die CLI schon in v0.1.0 mit ausgeliefert werden (meine Annahme: ja, im Unterordner `cli/`), oder nur die App?
4. **Portable ZIP:** Einstellungen im Benutzerordner (meine Annahme) oder neben der Exe?
