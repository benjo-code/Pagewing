# Milestone 8.1: Automatische Updates – Implementierungsplan

> **Für ausführende Agenten:** PFLICHT-SKILL: `superpowers:subagent-driven-development` (empfohlen) oder `superpowers:executing-plans`, Aufgabe für Aufgabe. Schritte sind Checkboxen (`- [ ]`).

**Ziel:** Pagewing prüft nach dem Start im Hintergrund auf eine neue Version, zeigt in der Titelleiste die Schaltfläche „Update“ und installiert auf Klick mit einem Fortschrittsfenster im Pagewing-Design; danach öffnen sich Tabs und Positionen wieder.

**Architektur:** Rechnerisches (Version, Manifest, Signatur, Prüfsumme, Zeitplan, Zustände, Paketform, Sitzungsdatei) liegt UI- und netzfrei in `Pagewing.Core.Updates`. Das Netz (`HttpClient`, GitHub Releases) und die Anwendung des Updates liegen in der App hinter Schnittstellen und entstehen erst nach dem ersten scharfen Seitenbild. Unter Windows übernimmt eine Kopie der App im Modus `--finish-update` das Installieren, weil die laufende Exe nicht überschrieben werden kann; unter Linux ersetzt die App das AppImage selbst.

**Tech Stack:** .NET 10 Native AOT, Avalonia 12.1, `System.Security.Cryptography.ECDsa` (P-256), `System.Net.Http`, Inno Setup 6.7.3 (stiller Modus), `tools/package.cs`, GitHub Actions.

**Spec:** [2026-10-23-m81-automatische-updates-design.md](../specs/2026-10-23-m81-automatische-updates-design.md) (maßgeblich); Milestone und Abnahme: [Docs/ROADMAP.md](../../ROADMAP.md), „Milestone 8.1“.

## Globale Randbedingungen

Gelten für jede Aufgabe (Wortlaut aus Spec, `CLAUDE.md` und Roadmap):

- Prüfung **von selbst, Standard an**, in den Einstellungen abschaltbar; abgeschaltet **kein einziger Netzwerkzugriff**.
- Nichts Neues im Startpfad: Netzwerk erst nach dem ersten scharfen Seitenbild bzw. dem ersten Frame der Startseite, auf einem Hintergrundthread, mit Zeitlimit und Abbruch; Startzeit-Budgets und `bench/startup` nie lockern.
- Höchstens eine Prüfung in 24 Stunden, einmal je Start (nach dem ersten Frame) und auf Befehl „Nach Updates suchen“; während einer Sitzung keine erneute Prüfung.
- Signatur: **ECDSA P-256** aus der .NET-Standardbibliothek, Schlüssel**liste** mit Kennung im Programm; privater Schlüssel nur als Secret des Repos, nur im Signier-Job, der nur bei einem Tag läuft.
- Quelle und Schlüssel **fest einkompiliert, kein Override** im ausgelieferten Programm (keine Umgebungsvariable, kein Schalter); Testaufbau nur über eine Bauzeit-Eigenschaft, die der Release-Aufbau nie setzt.
- Nur stabile Versionen über `releases/latest`. Jede Abweichung (falsche Signatur, falsche Prüfsumme, ältere oder gleiche Version, unbekannte Schlüsselkennung) lehnt ab; es wird nichts gestartet.
- Anfrage ohne Kennung und ohne Angaben zum Rechner: feste Kennung `Pagewing`, ohne Version, ohne Zufallswert.
- Ohne Netz, bei Zeitüberschreitung oder Serverfehler: still (keine Fehlerkarte beim Start, keine Schleife). Fehler **nach** „Installieren“ dagegen zeigt eine Meldung mit „Erneut versuchen“.
- Native AOT: JSON nur mit Source-Generator, keine Reflection, AOT-/Trim-Warnungen sind Fehler; `IsAotCompatible` je Projekt.
- Tests und Messläufe greifen nie ins Netz und nie in den echten Benutzerordner (`UserSettings.Detached()`, `RecentFilesService.Detached()`, Temp-Ordner). Messläufe bekommen **keinen** Updater (wie `PlatformDefaultAppServices.FactoryFor(isMeasurement)`).
- Neue Abhängigkeiten: keine geplant; kommt eine hinzu, Eintrag in `third-party/licenses.json` samt `ThirdPartyNotices.txt` und `Docs/DECISIONS.md`.
- Design: neue Maße und Farben nur als Tokens der Design-Spec (alle drei Sätze: hell, dunkel, Hochkontrast); Zustände nie nur über Farbe; WCAG 2.2 AA; Tastatur und Screenreader (`AutomationProperties.Name`, `AccessibilityView="Content"` an `Grid`/`Border` mit Namen); keine Farb- oder Maßliterale im XAML.
- UI-Texte Englisch **und** Deutsch in `Strings.en.resx`/`Strings.de.resx` plus `Strings.cs`. Code und Kommentare Englisch, Doku und Commit-Nachrichten Deutsch (Conventional Commits, gezielt stagen, nie `git add .`).
- Testnamen ohne Unterstriche; Befehle: Build `dotnet build Pagewing.slnx`, Test `dotnet test Pagewing.slnx`, Format `dotnet format Pagewing.slnx --verify-no-changes`.

## Review-Fokus

Eingaben und Zustände, die der Entwurf nicht ausspricht und an denen echte Nutzer scheitern könnten, wahrscheinlichste zuerst; jeder Punkt hat seinen Test in der Aufgabe, die den Code besitzt:

1. **Dateiname im Manifest mit Pfadanteil** (`..\..\evil.exe`, absolute Pfade, Laufwerksbuchstaben) oder absurde Größe: wird abgelehnt (Aufgabe 1).
2. **Uhr des Rechners falsch** (`lastChecked` in der Zukunft): zählt als fällig, die Prüfung blockiert nie dauerhaft (Aufgabe 2).
3. **Captive Portal, HTML statt JSON, riesige Antwort, Rate-Limit (403/429), Umleitung auf Fremdhost**: still, kein Zustand ändert sich, Antwort über 1 MiB wird verworfen (Aufgabe 4).
4. **Zwei Fenster oder doppelter Klick auf „Installieren“**: genau ein Download, genau eine Installation (Aufgabe 5 und 9).
5. **Download bricht ab, Platte voll, Abbruch durch den Nutzer**: Teil-Datei wird gelöscht, alte Version bleibt unberührt, „Erneut versuchen“ geht (Aufgabe 9). Dazu: Tab mit passwortgeschützter PDF oder noch ladender Datei beim Update kommt mit Passwortabfrage zurück (Aufgabe 8).

---

## Dateistruktur

| Datei | Verantwortung |
|---|---|
| `src/Pagewing.Core/Updates/UpdateVersion.cs` | SemVer lesen und vergleichen |
| `src/Pagewing.Core/Updates/UpdateManifest.cs` | Manifest-Typen, JSON-Kontext (Source-Generator), Namensregeln |
| `src/Pagewing.Core/Updates/UpdateKey.cs`, `UpdateTrust.cs` | Schlüsselliste, fest einkompilierte Quelle, Testaufbau-Umschaltung |
| `src/Pagewing.Core/Updates/ManifestVerifier.cs`, `ChecksumVerifier.cs` | Signatur und SHA-256 prüfen |
| `src/Pagewing.Core/Updates/UpdateStatusStore.cs` | `update.json` (letzte Prüfung, gefundene Version) |
| `src/Pagewing.Core/Updates/UpdateChecker.cs`, `IUpdateSource.cs`, `UpdateState.cs` | Zeitplan und Zustände, Quelle als Schnittstelle |
| `src/Pagewing.Core/Updates/PackageForm.cs` | Paketform erkennen |
| `src/Pagewing.Core/Updates/SessionSnapshot.cs`, `SessionStore.cs` | Sitzung für den Neustart (`session.json`) |
| `src/Pagewing.App/Services/GitHubUpdateSource.cs` | `HttpClient`, `releases/latest`, Download mit Fortschritt |
| `src/Pagewing.App/Services/UpdateCoordinator.cs` | ein Updater je Prozess: Prüfung, Zustand, Stage-Ablauf |
| `src/Pagewing.App/ViewModels/MainViewModel.Update.cs` | Haken im Fenster (Zeitpunkt, Befehl, Karte) |
| `src/Pagewing.App/ViewModels/UpdateViewModel.cs` | Zustand für Karte und Fortschrittsfenster |
| `src/Pagewing.App/Views/UpdateCard.axaml(.cs)`, `Views/UpdateWindow.axaml(.cs)`, `Views/MainWindow.Update.cs` | Karte, Fortschrittsfenster |
| `src/Pagewing.App/Platform/Windows/WindowsUpdateInstaller.cs`, `Platform/Linux/AppImageUpdater.cs` | Anwenden je System |
| `tools/package.cs` (erweitert) | `update-keygen`, `update-manifest`, `update-sign`, `update-verify` |
| `.github/workflows/release.yml` (erweitert) | dritter Job `manifest` |

---

### Aufgabe 1: Version, Manifest, Signatur und Prüfsumme (Core)

**Dateien:**
- Neu: `src/Pagewing.Core/Updates/{UpdateVersion,UpdateManifest,UpdateKey,UpdateTrust,ManifestVerifier,ChecksumVerifier}.cs`
- Test: `tests/Pagewing.Core.Tests/Updates/{UpdateVersionTests,ManifestVerifierTests,ChecksumVerifierTests}.cs`

**Schnittstellen:**
- Erzeugt:
  - `readonly record struct UpdateVersion(int Major, int Minor, int Patch)` mit `static bool TryParse(string? text, out UpdateVersion v)` (nur `X.Y.Z`, Präfix `v` erlaubt, Vorabversion und Metadaten **nicht**), `IComparable<UpdateVersion>`, Vergleichsoperatoren.
  - `sealed record UpdateManifest(int Format, string Version, IReadOnlyList<UpdateFile> Files)`; `sealed record UpdateFile(string Kind, string Name, long Size, string Sha256)`; `Kind` ist `win-x64-setup`, `win-x64-portable`, `linux-appimage` oder `linux-deb`. JSON-Namen klein (`format`, `version`, `files`, `kind`, `name`, `size`, `sha256`).
  - `sealed record UpdateKey(string Id, byte[] PublicKey)` (SubjectPublicKeyInfo, P-256).
  - `static class UpdateTrust { IReadOnlyList<UpdateKey> Keys; string ReleasesLatestUrl; string DownloadBaseUrl; }` (Release-Werte fest; unter der Compile-Konstante `PAGEWING_UPDATE_TESTBUILD` Testschlüssel und eine Test-Adresse, siehe Aufgabe 13). Der echte öffentliche Schlüssel kommt erst mit Aufgabe 12; bis dahin steht ein erkennbarer Platzhalter-Schlüssel samt Test, der meldet, solange er Platzhalter ist (`UpdateTrustTests.ReleaseKeysAreNotThePlaceholderWhenReleasing` ist nur im Release-Lauf aktiv, siehe Aufgabe 12).
  - `enum ManifestRejection { Malformed, UnknownKey, BadSignature, UnsupportedFormat, NotNewer, UnsafeFileName, UnsafeSize }`
  - `static class ManifestVerifier { static ManifestResult Verify(ReadOnlySpan<byte> manifestJson, string signatureText, IReadOnlyList<UpdateKey> keys, UpdateVersion current); }` mit `readonly record struct ManifestResult(UpdateManifest? Manifest, ManifestRejection? Rejection)`.
  - Signaturtext: `<keyId>:<base64 der 64-Byte-IEEE-P1363-Signatur>` über **die rohen Bytes** der Manifest-Datei, SHA-256. Geparst wird erst nach geprüfter Signatur.
  - `static class ChecksumVerifier { static Task<bool> MatchesAsync(string path, string sha256Hex, long size, CancellationToken ct); }` (Größe zuerst, dann Hash, ohne ganze Datei im Speicher).
- Nutzt: nichts.

- [ ] **Schritt 1: Tests schreiben** (rot): `UpdateVersionTests` (`ParsesPlainAndPrefixed`, `RejectsPrereleaseAndGarbage`, `ComparesNumericallyNotAsText` mit `0.10.0 > 0.9.0`); `ManifestVerifierTests` mit Testschlüsseln, die der Test selbst erzeugt (`ECDsa.Create(ECCurve.NamedCurves.nistP256)`): `AcceptsAValidNewerManifest`, `RejectsATamperedManifest` (ein Byte geändert → `BadSignature`), `RejectsAWrongSignature`, `RejectsAnUnknownKeyId`, `RejectsTheSameVersion`, `RejectsAnOlderVersion`, `RejectsAnUnsupportedFormat`, `RejectsAMalformedManifest`, `ChecksTheSignatureBeforeParsing` (kaputtes JSON mit gültiger Signatur → `Malformed`, mit ungültiger → `BadSignature`), `AcceptsAnyKeyOfTheList` (zweiter Schlüssel der Liste signiert); **Review-Fokus 1:** `RejectsFileNamesWithPathParts` (Theorie: `..\a.exe`, `a/b.exe`, `C:\a.exe`, `/etc/x`, leerer Name, Name mit Steuerzeichen → `UnsafeFileName`), `RejectsAbsurdSizes` (≤ 0 oder > 1 GiB → `UnsafeSize`); `ChecksumVerifierTests`: `MatchesTheFile`, `RejectsOneChangedByte`, `RejectsAWrongSize`, `ReadsInChunks`.
- [ ] **Schritt 2:** `dotnet test tests/Pagewing.Core.Tests --filter-class "*Updates*"` → FAIL (Typen fehlen). (Bei xunit.v3 auf Microsoft.Testing.Platform filtert `--filter-class`; sonst `dotnet test tests/Pagewing.Core.Tests` ganz laufen lassen.)
- [ ] **Schritt 3:** Typen oben in `src/Pagewing.Core/Updates/` implementieren. Dateiname gilt als sicher, wenn er `^[A-Za-z0-9][A-Za-z0-9._+~-]{0,127}$` erfüllt. JSON über einen `JsonSerializerContext` mit denselben Optionen wie `SettingsJsonContext` (Source-Generator, `UseStringEnumConverter`). Signatur mit `ECDsa.ImportSubjectPublicKeyInfo` und `VerifyData(…, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)`.
- [ ] **Schritt 4:** Tests laufen → PASS. Dann **AOT-Beleg:** `dotnet publish src/Pagewing.Cli -c Release -r win-x64 -o out/aot-probe` ohne Warnung (Windows) und `dotnet build Pagewing.slnx` ohne Warnung. Unter Linux hängt `ECDsa` an `libssl`/OpenSSL: Notiere in `Docs/DECISIONS.md` (Aufgabe 14), dass `.deb` `libssl3`/`libssl3t64` verlangen muss und das AppImage sie vom System nimmt; prüfen auf Ubuntu 22.04 (WSL) mit `ldd`/Smoke (offen markieren, falls nicht möglich).
- [ ] **Schritt 5:** Commit: `feat(updates): Version, Manifest, Signatur- und Prüfsummenprüfung im Core`.

---

### Aufgabe 2: Einstellung „Nach Updates suchen“ und Statusdatei

**Dateien:**
- Ändern: `src/Pagewing.Core/Settings/AppSettings.cs` (Eigenschaft + `Normalized`)
- Neu: `src/Pagewing.Core/Updates/UpdateStatusStore.cs`
- Test: `tests/Pagewing.Core.Tests/Settings/…` (bestehende Einstellungs-Tests erweitern), `tests/Pagewing.Core.Tests/Updates/UpdateStatusStoreTests.cs`

**Schnittstellen:**
- Erzeugt: `AppSettings.UpdateCheck { get; set; } = true` (`bool`, `{ get; set; }` wie alle Felder dort, Grund steht im Klassenkommentar). `sealed record UpdateStatus(DateTimeOffset? LastChecked, string? AvailableVersion)`. `sealed class UpdateStatusStore(string path)` mit `FilePath`, `UpdateStatus Load()`, `bool TrySave(UpdateStatus)`; Datei `update.json` im Ordner von `UserDataPaths.ConfigDirectory()`, Regeln wie `RecentFilesStore` (atomar über `JsonFile`, nichts wirft, kaputte Datei → `.bad` und leerer Status; `AvailableVersion` muss `UpdateVersion.TryParse` bestehen, sonst `null`). Interner Dokument-/Kontexttyp wie in `RecentFilesStore.cs`.
- Nutzt: `JsonFile` (`internal`, gleiche Assembly), `UpdateVersion` aus Aufgabe 1.

- [ ] **Schritt 1: Tests** (rot): `AppSettingsTests` ergänzen: `UpdateCheckIsOnByDefault`, `UpdateCheckSurvivesTheRoundTrip`, `AMissingUpdateCheckFieldKeepsTheDefault` (Datei ohne das Feld → `true`). `UpdateStatusStoreTests`: `MissingFileGivesAnEmptyStatus`, `RoundTrips`, `CorruptFileIsKeptAsBadAndGivesEmpty`, `DropsAnUnparsableVersion`, `NeverThrowsForABadPath`; **Review-Fokus 2:** ein reiner Funktionstest in Aufgabe 3 (`FutureLastCheckedCountsAsDue`).
- [ ] **Schritt 2:** Tests laufen → FAIL.
- [ ] **Schritt 3:** Eigenschaft und Store implementieren (`Normalized` ändert für `UpdateCheck` nichts).
- [ ] **Schritt 4:** Tests laufen → PASS.
- [ ] **Schritt 5:** Commit: `feat(updates): Einstellung UpdateCheck und Statusdatei update.json`.

---

### Aufgabe 3: Zeitplan, Zustände, Quelle als Schnittstelle, Paketform

**Dateien:**
- Neu: `src/Pagewing.Core/Updates/{IUpdateSource,UpdateState,UpdateChecker,PackageForm}.cs`
- Test: `tests/Pagewing.Core.Tests/Updates/{UpdateCheckerTests,PackageFormTests}.cs`, Hilfsklasse `FakeUpdateSource` in `tests/Pagewing.Core.Tests/Updates/`

**Schnittstellen:**
- Erzeugt:
  - `sealed record UpdateDocuments(byte[] Manifest, string Signature)`.
  - `interface IUpdateSource { Task<UpdateDocuments?> FetchManifestAsync(CancellationToken ct); Task<bool> DownloadAsync(UpdateFile file, string destinationPath, IProgress<long> bytesReceived, CancellationToken ct); }` (`null`/`false` heißt „nichts bekommen“, nie eine Exception für Netzfehler; Abbruch wirft `OperationCanceledException`).
  - `enum UpdateStage { None, Available, Downloading, Verifying, Ready, Installing, Failed }`; `sealed record UpdateState(UpdateStage Stage, UpdateVersion? Version, UpdateFile? File, long BytesReceived, string? Error)`; `UpdateState.None`.
  - `sealed class UpdateChecker(IUpdateSource source, UpdateStatusStore store, TimeProvider clock, UpdateVersion current, IReadOnlyList<UpdateKey> keys)` mit `static readonly TimeSpan Interval = 24 h`, `TimeSpan CheckTimeout = 10 s`; `UpdateState Known()` (liest nur `update.json`: `AvailableVersion` > `current` → `Available`, sonst `None`; keine Datei außer `update.json`, kein Netz); `Task<UpdateState> CheckAsync(bool force, CancellationToken ct)` (nicht fällig und nicht `force` → `Known()` ohne Netz; sonst Manifest holen, mit `ManifestVerifier` prüfen, `update.json` schreiben mit `LastChecked = clock.GetUtcNow()` und gefundener Version; jede Ablehnung, jeder Netzfehler, jedes Zeitlimit → `Known()` unverändert und **`LastChecked` bleibt**, damit der nächste Start es wieder versucht; nie eine Exception außer Abbruch); `UpdateFile? Pick(UpdateManifest m, PackageForm form)` wählt `win-x64-setup` für `WindowsInstaller`, `linux-appimage` für `AppImage`, sonst `null`.
  - `enum PackageForm { WindowsInstaller, WindowsPortable, AppImage, Deb, Unknown }`; `static PackageForm PackageForm.Detect(bool isWindows, string? processPath, Func<string, bool> fileExists, Func<string, string?> env)`: Windows: `unins000.exe` im Ordner von `processPath` → `WindowsInstaller`, sonst `WindowsPortable`; Linux: `env("APPIMAGE")` nicht leer → `AppImage`; `processPath` beginnt mit `/usr/lib/pagewing/` → `Deb`; sonst `Unknown`.
- Nutzt: Aufgaben 1 und 2.

- [ ] **Schritt 1: Tests** (rot), alle mit `FakeUpdateSource` (zählt Aufrufe, liefert vorbereitete Dokumente, signiert mit einem Testschlüssel) und einer Test-`TimeProvider`-Uhr: `CheckerTests`: `NoCallWhenNotDueAndNotForced`, `ChecksWhenNoStatusExists`, `ChecksAgainAfter24Hours`, `ForceChecksEvenWhenNotDue`, `StoresTheFoundVersionAndTheTime`, `KnownKeepsTheOfferAcrossRestarts` (zweiter Checker mit derselben Datei, ohne Netz → `Available`), `NewerManifestGivesAvailable`, `SameOrOlderGivesNone`, `FailureKeepsTheLastCheckedUntouched`, `RejectedManifestChangesNothing`, `TimeoutIsSilent` (Quelle hängt, `CheckTimeout` kürzer per Uhr/Token → `Known()`), `CancellationThrows`; **Review-Fokus 2:** `FutureLastCheckedCountsAsDue`. `PackageFormTests` (Theorie): `unins000.exe` da/nicht da, `APPIMAGE` gesetzt/leer, Pfad `/usr/lib/pagewing/Pagewing`, anderer Pfad, `Pick` je Form (`WindowsPortable`, `Deb`, `Unknown` → `null`).
- [ ] **Schritt 2:** Tests laufen → FAIL.
- [ ] **Schritt 3:** Typen implementieren. Das Zeitlimit der Prüfung über `CancellationTokenSource.CreateLinkedTokenSource(ct)` mit `CancelAfter(CheckTimeout, clock)`-Entsprechung (`TimeProvider`-fähiger Timer), damit der Test es ohne Warten treiben kann.
- [ ] **Schritt 4:** Tests laufen → PASS.
- [ ] **Schritt 5:** Commit: `feat(updates): UpdateChecker mit Zeitplan, Zuständen und Paketform`.

---

### Aufgabe 4: Netzquelle `GitHubUpdateSource`

**Dateien:**
- Neu: `src/Pagewing.App/Services/GitHubUpdateSource.cs`
- Test: `tests/Pagewing.App.Tests/GitHubUpdateSourceTests.cs`

**Schnittstellen:**
- Erzeugt: `sealed class GitHubUpdateSource : IUpdateSource, IDisposable` mit Konstruktor `internal GitHubUpdateSource(HttpMessageHandler handler)` und `static GitHubUpdateSource Create()` (eigener `SocketsHttpHandler`, keine Cookies, keine Anmeldedaten, `AllowAutoRedirect = true` nur innerhalb von `github.com`/`githubusercontent.com`-Hosts, Zertifikatsprüfung Standard). Fester Header `User-Agent: Pagewing`, kein `Authorization`, sonst nichts. `FetchManifestAsync` ruft `UpdateTrust.ReleasesLatestUrl` (GitHub-API `releases/latest`), liest daraus die Tag-Version nur um die Asset-Adressen `update-manifest.json` und `update-manifest.json.sig` aus `UpdateTrust.DownloadBaseUrl + "/v<Version>/…"` zu bilden (Tag muss `UpdateVersion.TryParse` bestehen, Adressen nie aus der Antwort übernommen), lädt beide mit Obergrenze 1 MiB. `DownloadAsync` lädt `UpdateTrust.DownloadBaseUrl + "/v<Version>/" + file.Name` (Name ist durch Aufgabe 1 sicher) in `destinationPath + ".part"`, meldet Bytes, prüft nach dem Laden Länge == `file.Size`, benennt erst dann um; Abbruch, Fehler oder zu viele Bytes löschen die Teil-Datei.
- Nutzt: `IUpdateSource`, `UpdateTrust`, `UpdateFile`.

- [ ] **Schritt 1: Tests** (rot) mit einem Test-`HttpMessageHandler` (Warteschlange von Antworten, zählt Aufrufe, hält die Anfrage fest): `SendsOnlyTheFixedUserAgentAndNoCredentials`, `BuildsAssetAddressesFromTheTagNotFromTheResponse`, `ReturnsNullForNetworkErrors`, `ReturnsNullForServerErrors`; **Review-Fokus 3:** `ReturnsNullForRateLimit` (403/429), `ReturnsNullForHtmlInsteadOfJson`, `RejectsAResponseOverOneMiB`, `RefusesARedirectToAnotherHost`, `RejectsATagThatIsNoVersion`; Download: `ReportsProgress`, `RenamesOnlyAfterTheFullLength`, `DeletesThePartFileOnCancel`, `DeletesThePartFileWhenTooManyBytesArrive`, `DeletesThePartFileWhenTheDiskIsFull` (Ziel-Stream, der `IOException` wirft; die Quelle öffnet das Ziel über eine überschreibbare interne Fabrik).
- [ ] **Schritt 2:** Tests laufen → FAIL.
- [ ] **Schritt 3:** Klasse implementieren. `HttpClient` entsteht erst in `Create()` bzw. beim ersten Aufruf, nie in einem statischen Konstruktor (Startpfad). `Timeout` des Clients unendlich, Zeitlimits über Tokens.
- [ ] **Schritt 4:** Tests laufen → PASS. `dotnet build Pagewing.slnx` ohne Warnung (AOT/Trim: kein Reflection-JSON; die GitHub-Antwort mit `JsonDocument`/`Utf8JsonReader` oder Source-Generator lesen).
- [ ] **Schritt 5:** Commit: `feat(updates): GitHub-Releases als Update-Quelle mit Größenlimit und Teil-Datei`.

---

### Aufgabe 5: Koordinator, Startzeitpunkt, Abschalten und Befehl

**Dateien:**
- Neu: `src/Pagewing.App/Services/UpdateCoordinator.cs`, `src/Pagewing.App/ViewModels/MainViewModel.Update.cs`
- Ändern: `src/Pagewing.App/Services/Workspace.cs` (Konstruktor-Parameter `Func<UpdateCoordinator?>? updateFactory`, ein Koordinator je Prozess), `src/Pagewing.App/App.axaml.cs` (Fabrik übergeben), `src/Pagewing.App/Commands/CommandCatalog.cs` (Eintrag), `src/Pagewing.App/ViewModels/MainViewModel.cs` (`OnStartScreenShown` und `OnFirstSharpPageDrawn`/`HasShownSharpPage`-Zweig rufen `PostUpdateCheck()` neben `PostDefaultAppCheck()`)
- Neu: `src/Pagewing.App/Services/PlatformUpdate.cs` (`FactoryFor(bool isMeasurement)` wie `PlatformDefaultAppServices`; Messlauf → `null`)
- Test: `tests/Pagewing.App.Tests/{UpdateStartupTests,UpdateDisabledTests,CommandCatalogTests}.cs` (Katalog-Test erweitern), Hilfsklasse `FakeUpdateSourceApp` oder Wiederverwendung der Core-Fake über `InternalsVisibleTo`

**Schnittstellen:**
- Erzeugt:
  - `sealed class UpdateCoordinator(UpdateChecker checker, UserSettings settings, PackageForm form)` (UI-Thread); `UpdateState State`; `event Action? Changed`; `bool CheckStarted` (Gate: **einmal je Prozess** nach dem Start); `Task StartAsync()` (nur wenn `settings.Current.UpdateCheck`; sonst sofort fertig und **ohne** Netzobjekt); `Task CheckNowAsync()` (Befehl; prüft auch bei abgeschaltetem Hintergrund-Check **nur auf ausdrücklichen Befehl**); `UpdateState Known` vor dem ersten Check aus `update.json` (gelesen erst im Hintergrund-Job).
  - `MainViewModel.UpdateCoordinator { internal get; internal set; }`, `public UpdateViewModel? Update` (Aufgabe 6), `internal void PostUpdateCheck()` (Hintergrundpriorität `DispatcherPriority.Background`, nur wenn Koordinator gesetzt, `!_disposed`, Gate noch nicht gestartet).
  - Befehl `app.checkForUpdates` im Katalog (`WindowGroup`, ohne Taste, `Offered` nur wenn Koordinator gesetzt; Titel „Check for updates“/„Nach Updates suchen“), ruft `CheckNowAsync()`.
- Nutzt: `UpdateChecker`, `GitHubUpdateSource.Create()` (nur in der Fabrik, die erst beim ersten Bedarf läuft), `PackageForm.Detect(...)`.

- [ ] **Schritt 1: Tests** (rot), nach dem Muster von `DefaultAppPromptTests` und `StartupTests`: `UpdateStartupTests.NoNetworkAccessBeforeTheFirstSharpPage` (Fake-Quelle mit Zähler, Fenster mit Datei geöffnet **vor** `Show`: Zähler ist 0 nach dem ersten Frame, 1 nach `OnFirstSharpPageDrawn` und einem Dispatcher-Lauf), `NoNetworkAccessBeforeTheFirstFrameOfTheStartScreen`, `CheckRunsOnceForTwoWindows` (**Review-Fokus 4**), `RecentFilesAndUpdateStatusAreNotReadInTheStartupPath` (`update.json` wird erst nach dem Gate gelesen); `UpdateDisabledTests.NoCallWhenTheSettingIsOff` (echte `GitHubUpdateSource` mit einem Handler, der **jeden Aufruf als Testfehler meldet**, Einstellung aus, Fenster mit Datei, 2 s Dispatcher-Lauf: Handler nie aufgerufen, auch nicht für DNS), `NoCoordinatorInAMeasurementRun` (`PlatformUpdate.FactoryFor(true)` ist `null`), `TheCommandWorksEvenWhenTheSettingIsOff` (Befehl prüft einmal), `NoErrorCardWhenTheCheckFails` (Quelle liefert `null`: kein Overlay, keine Karte, `Update` bleibt `None`). `CommandCatalogTests`: neuer Befehl ist gelistet, in beiden Sprachen betitelt.
- [ ] **Schritt 2:** `dotnet test tests/Pagewing.App.Tests --filter-class "*Update*"` → FAIL.
- [ ] **Schritt 3:** Implementieren. `UpdateCoordinator` fragt das Gate (`CheckStarted`) wie `DefaultAppPromptGate`; die Prüfung läuft auf dem Thread-Pool (`Task.Run`), das Ergebnis geht über den Dispatcher zurück. `Program.cs` bleibt unverändert (kein Netzcode vor dem ersten Frame; `Workspace` bekommt nur die Fabrik, ein `UpdateCoordinator` entsteht in der Fabrik erst beim ersten Hintergrund-Job).
- [ ] **Schritt 4:** Tests laufen → PASS; dazu `dotnet test tests/Pagewing.App.Tests` komplett (`MeasurementTests`, `StartupTests` bleiben grün).
- [ ] **Schritt 5:** Commit: `feat(updates): Koordinator, Start nach dem ersten scharfen Bild, Befehl Nach Updates suchen`.

---

### Aufgabe 6: Schaltfläche „Update“ in der Titelleiste und Karte

**Dateien:**
- Ändern: `src/Pagewing.App/Views/TitleBar.axaml` (+ `.axaml.cs`: Breitenbudget der Tableiste), `src/Pagewing.App/Styles/Tokens.axaml` und `Styles/HighContrast.axaml` (nur falls neue Tokens nötig; bevorzugt vorhandene `Pw.Brush.Accent.*`, `Accent.Subtle`), `Docs/superpowers/specs/2026-10-17-designsystem.md` (Eintrag), `src/Pagewing.App/Resources/Strings.{cs,en.resx,de.resx}`
- Neu: `src/Pagewing.App/ViewModels/UpdateViewModel.cs`, `src/Pagewing.App/Views/UpdateCard.axaml(.cs)`, `src/Pagewing.App/Views/MainWindow.Update.cs`
- Test: `tests/Pagewing.App.Tests/{UpdateButtonTests,UpdateCardTests}.cs`; Kontrast in `TokenTests`/eigenem Test über `Wcag`

**Schnittstellen:**
- Erzeugt: `sealed class UpdateViewModel : ViewModelBase` mit `UpdateStage Stage`, `string VersionText`, `bool ShowButton` (`Stage != None`), `bool IsCardOpen`, `ICommand InstallCommand` (startet Aufgabe 9; hier nur `UpdateCoordinator.BeginInstall()`-Haken, noch nicht verdrahtet), `ICommand LaterCommand` (schließt die Karte, Schaltfläche bleibt), `ICommand ToggleCardCommand` (Schaltfläche). Schaltfläche `Button#UpdateButton` in `TitleBar.axaml` (neue Spalte zwischen Palette und `CaptionButtonsSpace`, `Classes="update"`, Icon `Pw.Icon.Download` im Tabler-Stil (Strich 2 px, nur genutzter Pfad kopieren), Text „Update“; unter 900 px nur Icon wie `Button.palette.narrow`; `chrome:WindowDecorationProperties.ElementRole="User"`; `AutomationProperties.Name` „Update available“/„Update verfügbar“). Karte `UpdateCard` als Element im `RootGrid` (kein Overlay, kein `Popup`, `ZIndex` unter den Overlays, rechts oben unter der Titelleiste verankert), entsteht erst beim ersten Zeigen (Muster `MainWindow.DefaultApp.cs`). Karte: Titel „Pagewing 0.2.0 ist verfügbar“, Satz „Du hast 0.1.0. Pagewing lädt das Update, installiert es und öffnet danach deine Tabs wieder.“ (Englisch sinngemäß), „Installieren“ (Primär), „Später“; für `Deb`/`WindowsPortable`/`Unknown` statt „Installieren“ der Hinweis mit Link „Download-Seite öffnen“ (läuft über `MainViewModel.ConfirmLink`/`ExternalLink` wie jeder externe Link, **mit Bestätigung**, nie am Launcher vorbei); Esc und Fokus-Rückkehr wie bei `DefaultAppCard`.
- Nutzt: `UpdateCoordinator.State`/`Changed` aus Aufgabe 5.

- [ ] **Schritt 1: Tests** (rot): `UpdateButtonTests`: `HiddenWithoutAnUpdate`, `VisibleWithAnOfferAndShowsIconAndText` (Text sichtbar, nicht nur Farbe), `OnlyTheIconBelow900px`, `AutomationNameIsSet`, `ReachableByKeyboardAndOpensTheCardWithEnter`, `DoesNotCoverTheCaptionButtons` (Breitenbudget: `TabStrip` bekommt weniger Platz, keine Überlappung bei 1280 und 800 px), `SitsLeftOfTheCaptionButtonsOnWindowsAndAtTheEndOfTheTabRowOnLinux`; `UpdateCardTests`: `OpensOnClickAndLaterHidesTheCardButKeepsTheButton`, `EscClosesTheCardAndReturnsTheFocus`, `PortableAndDebShowTheHintWithALinkAndNoInstall` (Link geht durch `ExternalLink.TryParse` und die Bestätigung), `TheCardColoursMeetAA` (Text 4,5:1, Rahmen/Icon/Fokus 3:1 in hell, dunkel, Hochkontrast), `TextsExistInBothLanguages` (`LocalizationTests`-Muster), `AutomationNamesOfCardAndButtons`.
- [ ] **Schritt 2:** Tests laufen → FAIL.
- [ ] **Schritt 3:** Umsetzen. Erst das Icon `Pw.Icon.Download` in die Icon-Ressourcen (nur der genutzte Pfad), dann Tokens falls nötig (alle drei Sätze, sonst schlägt `TheVariantHasEveryTokenOfTheOtherThemes` an), dann Schaltfläche, dann Karte. Die Design-Spec bekommt einen Abschnitt (Position, Maße, Zustände, Beschreibung aus den Mockups `.superpowers/…/update-ui.html`) und `Docs/DECISIONS.md` einen Eintrag in Aufgabe 14.
- [ ] **Schritt 4:** Tests laufen → PASS; Sichtprüfung mit `run` (Skill `run`) hell, dunkel, Hochkontrast, Kompakt, 800 px; Abweichung zu den Mockups nennen.
- [ ] **Schritt 5:** Commit: `feat(ui): Schaltfläche Update in der Titelleiste und Karte Update verfügbar`.

---

### Aufgabe 7: Einstellung und Über-Karte

**Dateien:**
- Ändern: `src/Pagewing.App/ViewModels/SettingsViewModel.cs`, `src/Pagewing.App/Views/SettingsCard.axaml(.cs)`, `src/Pagewing.App/Views/AboutCard.axaml`, `Strings.{cs,en.resx,de.resx}`
- Test: `tests/Pagewing.App.Tests/{SettingsCardTests,AboutCardTests}.cs`

**Schnittstellen:**
- Erzeugt: `SettingsViewModel.UpdateCheck` (bool, schreibt `AppSettings.UpdateCheck` über `UserSettings.Update` wie die anderen Schalter, z. B. `ReduceMotion`); Zeile „Automatically check for updates“/„Automatisch nach Updates suchen“ mit einem Satz darunter: was abgefragt wird (die öffentliche Release-Seite von Pagewing auf GitHub) und was nicht (keine Kennung, keine Version, keine Daten über dich oder deinen Rechner). Über-Karte: derselbe Satz statt „Pagewing arbeitet offline. Nichts verlässt diesen Rechner.“ (wörtlich ersetzen, dort und auf der Startseite, siehe Aufgabe 14).
- Nutzt: Aufgabe 2.

- [ ] **Schritt 1: Tests** (rot): `SettingsCardTests`: `UpdateSwitchIsOnByDefault`, `TurningItOffWritesTheSettingAndStopsTheCheck` (Koordinator-Fake meldet danach keinen Hintergrund-Check mehr), `KeyboardOnlyUseWorks` bleibt grün mit der neuen Zeile, `TheCardColoursMeetAA` bleibt grün; `AboutCardTests`: `NamesWhatTheCheckAsksAndWhatItDoesNot`, beide Sprachen.
- [ ] **Schritt 2:** FAIL → **Schritt 3:** umsetzen → **Schritt 4:** PASS.
- [ ] **Schritt 5:** Commit: `feat(ui): Einstellung Nach Updates suchen und Hinweis in der Über-Karte`.

---

### Aufgabe 8: Sitzung für den Neustart nach dem Update

**Dateien:**
- Neu: `src/Pagewing.Core/Updates/{SessionSnapshot,SessionStore}.cs`
- Ändern: `src/Pagewing.App/StartupOptions.cs` (`RestoreSession`), `src/Pagewing.App/Program.cs` (Sitzung lesen **nur** mit `--restore-session`), `src/Pagewing.App/Services/Workspace.cs` (`CaptureSession()`, `RestoreSession(SessionSnapshot)`), `src/Pagewing.App/App.axaml.cs` (Weitergabe an das erste Fenster), `MainViewModel` (nur falls ein Haken fehlt)
- Test: `tests/Pagewing.Core.Tests/Updates/SessionStoreTests.cs`, `tests/Pagewing.App.Tests/{StartupOptionsTests,SessionRestoreTests,WorkspaceTests}.cs`

**Schnittstellen:**
- Erzeugt:
  - `sealed record SessionTab(string Path, RememberedView? View)`; `sealed record SessionWindow(IReadOnlyList<SessionTab> Tabs, int ActiveIndex)`; `sealed record SessionSnapshot(IReadOnlyList<SessionWindow> Windows)`; `sealed class SessionStore(string path)` mit `Load()` (leer bei fehlend/kaputt, `.bad` wie die anderen Stores, **Pfade und Ansichten bereinigt**: leere Pfade, Duplikate je Fenster, ungültige `RememberedView` → `null`, höchstens 20 Tabs je Fenster, höchstens 10 Fenster), `bool TrySave(SessionSnapshot)`, `void Delete()`; Datei `session.json` neben den anderen.
  - `StartupOptions.RestoreSession` (`bool`, Schalter `--restore-session`, versteckt, nicht in der Hilfe, wird von keiner Messung beachtet).
  - `Workspace.CaptureSession() → SessionSnapshot` (je Fenster die Tabs mit Datei und `RememberedView` des Tabs, aktiver Tab; Tabs ohne geöffnetes Dokument werden mit Pfad, aber ohne Ansicht gespeichert, solange sie einen Pfad haben) und `Workspace.RestoreSession(SessionSnapshot)`: Fenster 1 bekommt seine Tabs über den bestehenden frühen Weg der Kommandozeile (erste Datei früh geöffnet, weitere nach dem ersten scharfen Kachelbild wie `OpenMoreFilesAfterFirstSharpTile`), weitere Fenster werden nach dem ersten scharfen Bild von Fenster 1 geöffnet; die gemerkte Ansicht kommt über `TabViewModel.InitialView`.
- Regeln (alle als Tests): Datei fehlt oder verschoben → still übersprungen; passwortgeschützte PDF → öffnet und fragt wie sonst (Zustand `PasswordRequired`); wiederhergestellte Dateien lösen **keine** Erst-Öffnen-Karte aus (`DefaultAppPromptGate.Started = true` beim Wiederherstellen); `session.json` wird beim Lesen einmal verbraucht (`Delete()` direkt nach dem Laden), Start ohne den Schalter liest sie nie.
- Nutzt: `JsonFile`, `RememberedView`.

- [ ] **Schritt 1: Tests** (rot): `SessionStoreTests`: `RoundTrips`, `MissingFileGivesEmpty`, `CorruptFileIsKeptAsBad`, `CleansPathsAndViews`, `LimitsTabsAndWindows`; `StartupOptionsTests`: `ParsesRestoreSession`, `IgnoresItInAMeasurement`; `SessionRestoreTests`: `RestoresTabsAndPositionsOfOneWindow`, `RestoresSeveralWindowsAsBefore`, `SkipsMissingFilesSilently`, `AskForThePasswordOfAProtectedPdf` (**Review-Fokus 5**, Datei `testdata/pdfs` mit Passwort wie in den bestehenden Passwort-Tests), `ATabThatWasStillLoadingComesBack`, `NoFirstOpenCardAfterARestore`, `StartWithoutTheSwitchNeverReadsTheSessionFile` (Zähler/Datei unberührt), `TheFileIsConsumedOnce`; `WorkspaceTests.CaptureSessionListsEveryWindowWithItsActiveTab`.
- [ ] **Schritt 2:** FAIL → **Schritt 3:** umsetzen (nur lesen, wenn `RestoreSession` gesetzt ist; ein Start ohne Schalter liest `session.json` nie) → **Schritt 4:** PASS, dazu `StartupTests` grün.
- [ ] **Schritt 5:** Commit: `feat(updates): Sitzung merken und nach dem Update wiederherstellen`.

---

### Aufgabe 9: Fortschrittsfenster, Download und Prüfung (alle Systeme)

**Dateien:**
- Neu: `src/Pagewing.App/Views/UpdateWindow.axaml(.cs)`; Erweiterung `UpdateViewModel`/`UpdateCoordinator` um den Ablauf
- Ändern: `Strings.{cs,en.resx,de.resx}`, `Styles/*` nur falls ein Token fehlt (alle drei Sätze)
- Test: `tests/Pagewing.App.Tests/{UpdateFlowTests,UpdateWindowTests}.cs`

**Schnittstellen:**
- Erzeugt: `UpdateCoordinator.BeginInstallAsync()`: (1) holt das Manifest erneut und prüft es (`UpdateChecker`-Verfahren, **gleiche Version wie angeboten**, sonst Fehler „Update wurde zurückgezogen“), (2) wählt `Pick(manifest, form)`, (3) legt `staging = <ConfigDirectory>/updates/<version>/` an, lädt die Datei dorthin (Stage `Downloading` mit Bytes), (4) prüft Größe und SHA-256 mit `ChecksumVerifier` (Stage `Verifying`), (5) schreibt `update-manifest.json`, `update-manifest.json.sig` und `request.json` (Version, Dateiname, Paketform, Pfad der laufenden Exe, Zielordner, gemeinsame Argumente für den Neustart) in `staging`, (6) setzt Stage `Ready`. Danach ruft der Koordinator `IUpdateApplier.ApplyAsync(StagedUpdate)` (Aufgaben 10 und 11). Erst **nach** (4) wird die App beendet. `interface IUpdateApplier { Task<ApplyResult> ApplyAsync(StagedUpdate staged, CancellationToken ct); }`; `sealed record StagedUpdate(string Directory, UpdateVersion Version, UpdateFile File, PackageForm Form)`. `UpdateWindow` ist ein eigenes Fenster (Pagewing-Stil, wie der Entwurf: vier Schritte „Herunterladen“, „Prüfen“, „Installieren“, „Neustart“, Balken, `31 von 68 MB`, Satz „Bitte das Fenster offen lassen. Deine Tabs kommen danach zurück.“, „Abbrechen“ bis zum Schritt „Installieren“, bei Fehler die Meldung mit „Erneut versuchen“ und „Schließen“); es hängt am `UpdateViewModel` und öffnet beim Klick auf „Installieren“ (Position und Größe so, dass die Übergabe an das Fenster der Kopie unter Windows nahtlos wirkt: das Helferfenster nimmt `request.json`-Werte `left/top/width/height`).
- Nutzt: `IUpdateSource.DownloadAsync`, `ChecksumVerifier`, `UpdateChecker`, Aufgabe 8 (`SessionStore`).

- [ ] **Schritt 1: Tests** (rot): `UpdateFlowTests` mit Fake-Quelle und Fake-`IUpdateApplier`: `DownloadsVerifiesThenAppliesInThatOrder`, `TheAppIsNotClosedBeforeTheChecksumMatched` (Fake-Applier wird bei falscher Prüfsumme nie aufgerufen), `RejectsAWithdrawnVersion`, `RejectsAManifestOfAnotherVersionThanOffered`, `WrongChecksumLeavesNothingBehindAndShowsAnError`, `RetryAfterAnErrorWorks`, **Review-Fokus 4:** `DoubleClickStartsOneInstall` und `TwoWindowsStartOneInstall`; **Review-Fokus 5:** `CancelDuringDownloadDeletesThePartFileAndKeepsTheOldVersion`, `DiskFullShowsAnErrorAndRetryWorks`, `NoSessionIsWrittenBeforeTheApplyStarts`; `UpdateWindowTests`: `ShowsTheFourStepsAndTheProgress`, `StepStateIsNotOnlyColour` (Text/Form je Schritt: ✓, laufend, offen), `AutomationNamesAndProgressValue` (Fortschrittswert für Screenreader), `EscDoesNotCloseWhileInstalling`, `ColoursMeetAA`, `TextsExistInBothLanguages`.
- [ ] **Schritt 2:** FAIL → **Schritt 3:** umsetzen → **Schritt 4:** PASS; Sichtprüfung mit `run`.
- [ ] **Schritt 5:** Commit: `feat(updates): Fortschrittsfenster mit Download und Prüfung vor dem Beenden`.

---

### Aufgabe 10: Windows installieren (Modus `--finish-update`)

**Dateien:**
- Neu: `src/Pagewing.App/Platform/Windows/WindowsUpdateInstaller.cs`
- Ändern: `src/Pagewing.App/StartupOptions.cs` (`FinishUpdate`: Pfad des `staging`-Ordners), `src/Pagewing.App/Program.cs` (Zweig direkt nach `--register-file-types`, vor der Single-Instance-Wahl; ruft eine eigene Start-Methode, die Avalonia mit dem `UpdateWindow` im Helfermodus startet), `src/Pagewing.App/Views/UpdateWindow.axaml.cs`
- Test: `tests/Pagewing.App.Tests/{WindowsUpdateInstallerTests,StartupOptionsTests}.cs`; Smoke in `tools/package.cs` (Befehl `update-smoke`, Windows)

**Schnittstellen:**
- Erzeugt: `WindowsUpdateInstaller : IUpdateApplier`: kopiert den Programmordner nach `%LOCALAPPDATA%\Pagewing\updater\<guid>\` (alte Kopien > 1 Tag werden vorher gelöscht), schreibt `session.json` (Aufgabe 8) und startet **die Kopie** mit `--finish-update <staging>`, dann beendet die App sich regulär (Workspace schließen). Helfermodus: liest `request.json`, **prüft erneut** Signatur des gespeicherten Manifests (`ManifestVerifier`, Schlüsselliste) und SHA-256 der Installer-Datei (gegen TOCTOU), dann `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=<staging>\setup.log` über `InnoArguments`-gleiche Quotierung (nicht neu erfinden: `tools/package.cs` hat `InnoArguments`; die App braucht nur Leerzeichen-/Anführungszeichen-Regeln für **einen** Pfad) und wartet auf den Prozess; Exit 0 → Schritt „Neustart“: startet `<Zielordner>\Pagewing.exe --restore-session` und beendet sich; Exit ≠ 0 (UAC abgelehnt, Inno-Fehler) → startet die vorhandene Version mit `--restore-session` neu, zeigt im Fenster die Meldung mit „Erneut versuchen“ (Installer erneut starten) und „Schließen“, löscht `staging` erst bei Erfolg oder „Schließen“. Der Modus `--finish-update` nimmt **keine** andere Quelle als `request.json` im angegebenen Ordner und nur einen Ordner unter `…\Pagewing\updates\`.
- Nutzt: Aufgaben 8 und 9, `ManifestVerifier`, `ChecksumVerifier`.

- [ ] **Schritt 1: Tests** (rot): `StartupOptionsTests.ParsesFinishUpdateWithAFolder`, `IgnoresFinishUpdateWithoutAValue`; `WindowsUpdateInstallerTests` (Prozessstart über eine Schnittstelle `IProcessLauncher` gefälscht): `CopiesTheProgramFolderAndStartsTheCopyWithTheSwitch`, `WritesTheSessionBeforeStarting`, `HelperRefusesAFolderOutsideTheUpdatesFolder`, `HelperRefusesAChangedInstaller` (ein Byte → kein Inno-Start, Fehler), `HelperRefusesAChangedManifest`, `HelperRunsInnoSilentWithTheLogOption` (genau die Argumente), `SuccessStartsTheNewVersionWithRestoreSession`, `FailureStartsTheOldVersionWithRestoreSessionAndShowsRetry`, `UacDeclinedCountsAsAFailure` (Exit 1223 wie von Inno gemeldet oder ≠ 0), `OldCopiesAreCleanedUp`. Smoke-Befehl `package.cs update-smoke <setup-alt.exe> <setup-neu.exe>`: installiert die alte Version still nach einem Testordner, legt ein Test-Manifest mit Testschlüssel ab, ruft den Helfermodus gegen den Testserver (Aufgabe 13) und prüft, dass `Pagewing.exe` danach die neue Version meldet und `settings.json` unverändert ist.
- [ ] **Schritt 2:** FAIL → **Schritt 3:** umsetzen. Prüfen, dass Inno den Modus der vorhandenen Installation übernimmt (`UsePreviousPrivileges`, für „alle Benutzer“ UAC-Abfrage); wenn nicht, die Eigenschaft im `.iss` setzen und `package.cs selftest` (`IssRules`) anpassen, mit `installer-smoke` belegen.
- [ ] **Schritt 4:** Tests PASS; `dotnet run tools/package.cs -- selftest` grün; `update-smoke` grün (Windows); `dotnet publish src/Pagewing.App -c Release -r win-x64` ohne Warnung.
- [ ] **Schritt 5:** Commit: `feat(updates): Windows-Installation im Helfermodus mit erneuter Prüfung und Neustart`.

---

### Aufgabe 11: Linux (AppImage ersetzen, `.deb` und portable ZIP nur Hinweis)

**Dateien:**
- Neu: `src/Pagewing.App/Platform/Linux/AppImageUpdater.cs`
- Test: `tests/Pagewing.App.Tests/AppImageUpdaterTests.cs`

**Schnittstellen:**
- Erzeugt: `AppImageUpdater : IUpdateApplier`: Ziel ist der Pfad aus `APPIMAGE`; die geprüfte Datei aus `staging` wird in den Ordner des Ziels kopiert (`<Ziel>.new`, gleiches Dateisystem), `chmod +x` (`File.SetUnixFileMode`), dann **atomar** per `File.Move(..., overwrite: true)`; die laufende Datei bleibt über ihre geöffnete Inode gültig. Danach `session.json` schreiben und das neue AppImage mit `--restore-session` starten, dann beenden. Kein Schreibrecht im Ordner des Ziels (Schreibschutz, Nur-Lese-Mount) → Fehler im Fenster („Der Ordner ist schreibgeschützt: <Pfad>. Lade das neue AppImage von Hand.“) mit Link zur Download-Seite; die alte Datei bleibt. `Deb`, `WindowsPortable`, `Unknown`: kein Applier, nur der Hinweis der Karte (Aufgabe 6).
- Nutzt: Aufgaben 8 und 9.

- [ ] **Schritt 1: Tests** (rot, laufen unter Windows und Linux, Dateisystem im Temp-Ordner, Prozessstart gefälscht): `ReplacesTheFileAtomicallyAndKeepsItExecutable` (nur Linux-Modusbits; unter Windows übersprungen mit Meldung), `LeavesTheOldFileOnAWriteFailure`, `StartsTheNewFileWithRestoreSession`, `WritesTheSessionBeforeStarting`, `NoApplierForDebPortableUnknown`, `ReadOnlyFolderShowsTheManualHint`.
- [ ] **Schritt 2:** FAIL → **Schritt 3:** umsetzen → **Schritt 4:** PASS. Ende zu Ende mit einem echten AppImage (Ubuntu 24.04 ohne `libfuse2`) bleibt **offen und manuell** (Befehle in `packaging/README.md`, Aufgabe 13).
- [ ] **Schritt 5:** Commit: `feat(updates): AppImage ersetzt sich selbst, deb und portable zeigen einen Hinweis`.

---

### Aufgabe 12: Release-Werkzeug und dritter Job

**Dateien:**
- Ändern: `tools/package.cs` (Befehle `update-keygen`, `update-manifest`, `update-sign`, `update-verify`; `selftest` erweitern), `.github/workflows/release.yml` (Job `manifest`), `packaging/README.md`, `src/Pagewing.Core/Updates/UpdateTrust.cs` (echter öffentlicher Schlüssel)

**Schnittstellen:**
- Erzeugt:
  - `package.cs update-keygen --id <kennung> --private-out <datei>`: erzeugt P-256, schreibt den privaten Schlüssel (PKCS#8, Base64) **nur** in die angegebene Datei (Rechte nur für den Benutzer), gibt den öffentlichen Schlüssel als C#-Literal für `UpdateTrust.Keys` auf stdout aus; der private Schlüssel erscheint nie in der Ausgabe.
  - `package.cs update-manifest --version <x.y.z> --sums <SHA256SUMS-windows.txt> <SHA256SUMS-linux.txt> --sizes <json von gh release view> --out update-manifest.json`: nimmt nur Dateien mit bekannter `Kind`-Zuordnung (`*-win-x64-setup.exe`, `*-win-x64-portable.zip`, `*-x86_64.AppImage`, `*_amd64.deb`).
  - `package.cs update-sign --manifest <datei> --key-env PAGEWING_UPDATE_SIGNING_KEY --id <kennung> --out <datei.sig>` (Schlüssel **nur** aus der Umgebungsvariablen, nie als Argument, nie ausgegeben; nach dem Signieren sofort mit dem eingebetteten öffentlichen Schlüssel prüfen) und `update-verify --manifest <datei> --sig <datei> [--keys-from-source]`.
  - Job `manifest` in `release.yml`: `needs: [windows, linux]`, `if: needs.*.result == 'success' && (github.event_name == 'release' || inputs.upload)`, `runs-on: [self-hosted, dotnet-linux]`, `permissions: contents: write`; lädt die beiden `SHA256SUMS-*.txt` und die Dateigrößen per `gh release download`/`gh release view --json assets` (Token wie in den anderen Jobs, `GH_REPO` gesetzt, `persist-credentials: false`), baut, signiert, **prüft** (`update-verify`) und lädt `update-manifest.json` und `update-manifest.json.sig` mit `--clobber` hoch. Das Secret `PAGEWING_UPDATE_SIGNING_KEY` steht **nur** im `env` des Schritts „Sign“ (nie im Job-`env`, nie in einem Schritt, der Pull-Request-Code ausführt; der Workflow läuft nie für Forks). Tag-Regex und Dateinamen bleiben stabil.
- Nutzt: Aufgabe 1 (Format, Signaturtext).

- [ ] **Schritt 1: Tests** (rot) im `selftest` von `tools/package.cs` (bestehendes `Expect`-Muster): `update-manifest: nur bekannte Dateien`, `update-manifest: Größe und SHA-256 stimmen`, `update-sign: Signatur prüft mit dem Schlüssel`, `update-sign: Schlüssel nie in der Ausgabe`, `update-sign: ohne Umgebungsvariable Fehler`, `update-verify: manipuliert → Fehler`, `release.yml: Secret nur im Signier-Schritt`, `release.yml: Job manifest wartet auf beide Jobs`, `release.yml: setzt PagewingUpdateTestBuild nie` (liest die YAML-Datei als Text), `update-keygen: private Datei nur für den Benutzer lesbar` (Linux; Windows ACL übersprungen mit Meldung). Core-Test `UpdateTrustTests.ReleaseKeysAreNotThePlaceholderWhenReleasing` wird scharf, sobald der echte Schlüssel eingetragen ist.
- [ ] **Schritt 2:** `dotnet run tools/package.cs -- selftest` → FAIL.
- [ ] **Schritt 3:** Befehle und Job umsetzen; der Projektinhaber erzeugt den echten Schlüssel selbst (`update-keygen`), legt das Secret im Repo an und trägt den öffentlichen Schlüssel ein (Anweisung in `packaging/README.md`; der Agent liest oder zitiert den privaten Schlüssel nie).
- [ ] **Schritt 4:** `selftest` PASS; Trockenlauf des Workflows nach der bestehenden Anleitung (`gh workflow run release.yml --ref <Branch> -f tag=v0.2.0-rc.1 -f ref=<Branch> -f upload=false`: der Job `manifest` läuft dabei **nicht**, weil nichts hochgeladen wird; der echte Lauf gehört zum Release und erfolgt nur auf Anweisung).
- [ ] **Schritt 5:** Commit: `ci(updates): Manifest, Signatur und dritter Release-Job`.

---

### Aufgabe 13: Testaufbau, Testserver, Ende-zu-Ende-Anleitung

**Dateien:**
- Neu: `tools/update-testserver.cs` (Einzeldatei, `dotnet run`; serviert einen Ordner mit Manifest, Signatur und Dateien auf `http://127.0.0.1:<Port>/`)
- Ändern: `src/Pagewing.Core/Pagewing.Core.csproj` und `src/Pagewing.App/Pagewing.App.csproj` (Bauzeit-Eigenschaft `PagewingUpdateTestBuild` → Compile-Konstante `PAGEWING_UPDATE_TESTBUILD`), `src/Pagewing.Core/Updates/UpdateTrust.cs`, `packaging/README.md` (Abschnitt „Update prüfen“), `bench/startup/README.md` nur falls die Messung sich ändert

**Schnittstellen:**
- Erzeugt: Mit `-p:PagewingUpdateTestBuild=true` zeigt `UpdateTrust` auf `http://127.0.0.1:<Port>/` (Port und Testschlüssel als MSBuild-Eigenschaften `PagewingUpdateTestPort`, `PagewingUpdateTestKey`, nur im Testaufbau gelesen) und nimmt nur den Testschlüssel; der Release-Aufbau setzt die Eigenschaft nie (Test in Aufgabe 12). Ein Testaufbau trägt im Über-Dialog den Zusatz „(Testaufbau)“.
- Ablauf der Handprüfung (in `packaging/README.md`): Testschlüssel erzeugen, alte und neue Version mit `-p:Version=0.0.1`/`0.0.2` und Testaufbau bauen, Installer beider bauen (`package.cs layout`/`installer`), Manifest für `0.0.2` mit dem Testschlüssel signieren, Testserver starten, alte Version installieren (für „nur für mich“ **und** „alle Benutzer“), Update auslösen, danach prüfen: neue Version läuft, Tabs und Positionen sind zurück, `settings.json` und `recent.json` unverändert, Standardprogramm-Zuordnung bleibt, UAC bei „alle Benutzer“ erschien, bei „nur für mich“ nicht; Fehlerpfade: Server aus, Manifest manipuliert, falsche Prüfsumme, UAC abgelehnt.

- [ ] **Schritt 1: Tests** (rot): `UpdateTrustTests.TestBuildPointsAtLoopbackOnly` (nur unter der Konstante, Test-Projekt wird dafür einmal mit der Eigenschaft gebaut: `dotnet test tests/Pagewing.Core.Tests -p:PagewingUpdateTestBuild=true --filter-class "*UpdateTrust*"`), `UpdateTrustTests.ReleaseBuildHasNoOverride` (der normale Lauf prüft per Reflection **im Test**, dass `UpdateTrust` keine `Environment`-Abfrage enthält, über einen Textvergleich des Quelltexts auf `GetEnvironmentVariable`), `TestserverServesOnlyTheGivenFolder` (Pfadtraversal `..` → 404).
- [ ] **Schritt 2:** FAIL → **Schritt 3:** umsetzen → **Schritt 4:** PASS; Handprüfung Windows nach der Anleitung (Ergebnis in die Roadmap-Nachweise, falls nicht möglich: als offen mit dem Befehl markieren).
- [ ] **Schritt 5:** Commit: `test(updates): Testaufbau, Testserver und Anleitung zur Handprüfung`.

---

### Aufgabe 14: Offline-Zusage, Entscheidungen, Roadmap, Abnahme

**Dateien:**
- Ändern: `Docs/DECISIONS.md` (Einträge unten), `CLAUDE.md` (Kopfzeile „Komplett offline, keine Telemetrie“, Abschnitt „Offline und Sicherheit“, „Projektspezifisches“ um einen Abschnitt „Updates (seit M8.1)“ mit den Stolperfallen), `README.md`, `Docs/superpowers/specs/2026-10-16-pagewing-design.md` (Grundsätze und Abgrenzung), `Docs/superpowers/specs/2026-10-17-designsystem.md`, `src/Pagewing.App/Resources/Strings.{en,de}.resx` (Startseite und Über-Karte: „Pagewing arbeitet offline. Nichts verlässt diesen Rechner.“ ersetzen), `CHANGELOG.md` (`[Unreleased]`), `Docs/roadmap.json` + erzeugte `Docs/ROADMAP.md`, `third-party/licenses.json` nur falls ein Paket hinzukam

**DECISIONS-Einträge (Überschriften):** „Automatische Updates: Offline-Zusage mit Ausnahme der Update-Prüfung (M8.1)“ (löst „Keine Auto-Update-Funktion“ ab), „Update-Signatur: ECDSA P-256, Schlüsselliste, fest einkompiliert (M8.1)“ (Restrisiko gekaperter Runner, Schlüsselwechsel, Verlust = einmal von Hand umsteigen), „Update-Quelle: GitHub `releases/latest`, nur stabile Versionen, Manifest im Release (M8.1)“, „Update-Ablauf: Prüfen vor dem Beenden, Helfermodus unter Windows, Grenze beim Abbruch mitten im Kopieren (M8.1)“, „Sitzung nur für den Neustart nach dem Update (M8.1)“, „Linux: AppImage ersetzt sich, `.deb` nur Hinweis, `libssl` als Abhängigkeit (M8.1)“.

- [ ] **Schritt 1:** Texte nachziehen (siehe Dateien). Die Aussagen „komplett offline“/„keine Update-Prüfung“ mit `rg -n "offline|Update-Pr|Auto-Update|Nichts verlässt"` in `README.md`, `CLAUDE.md`, `Docs/`, `src/Pagewing.App/Resources/` suchen und jede Stelle bewusst anpassen; Test: `LocalizationTests`/`StartScreenTests`-Texttests für die neuen Sätze beider Sprachen.
- [ ] **Schritt 2:** `CHANGELOG.md` unter `[Unreleased]`: „Automatische Updates: Pagewing prüft im Hintergrund auf neue Versionen (abschaltbar in den Einstellungen) und installiert sie auf Klick; Tabs kommen danach zurück.“
- [ ] **Schritt 3:** Roadmap pflegen (`Docs/roadmap.json` bearbeiten, nie `ROADMAP.md`): beim Start `Plan` verlinken und Status `planned` setzen; bei der Umsetzung Aufgaben abhaken, Abnahmekriterien erst nach tatsächlicher Prüfung mit Nachweis; am Ende `done` mit `completedOn`. Danach `dotnet run tools/roadmap.cs -- generate` und `-- check`.
- [ ] **Schritt 4: Abnahme mit Nachweis** (Befehl und Ergebnis nennen, Offenes als offen markieren):
  - `dotnet build Pagewing.slnx`, `dotnet test Pagewing.slnx`, `dotnet format Pagewing.slnx --verify-no-changes`, `dotnet run tools/roadmap.cs -- check`, `dotnet run tools/package.cs -- selftest`, `dotnet run tools/licenses.cs -- check --publish-dir <publish>` (nach `dotnet publish src/Pagewing.App -c Release -r win-x64`).
  - Startzeit: `dotnet run bench/startup/startup.cs -- --case cold|warm|thousand|second` mit Vergleich zu `v0.1.0`; **auf dem Haupt-PC mit GPU** (Workflow `hardware-measure.yml`, der Projektinhaber startet den Runner von Hand: vorher bitten); CI-Startzeit-Check auf beiden Runnern.
  - Ablehnungstests (Aufgaben 1, 3, 4, 9, 10), Abschalt-Test (Aufgabe 5), Ende zu Ende Windows („nur für mich“ und „alle Benutzer“) und Linux-AppImage nach Aufgabe 13 (manuell; offen, solange das Repo privat ist bzw. die Handprüfung fehlt).
- [ ] **Schritt 5:** PR nach `dev` (siehe `CLAUDE.md`, „Git & Branches“), CI grün abwarten, selbst mergen.

---

## Selbstprüfung gegen den Entwurf

- **Prüfung von selbst, abschaltbar, null Netz wenn aus:** Aufgaben 2, 5, 7. **Nicht im Startpfad:** Aufgabe 5, `UpdateStartupTests`. **Zeitplan, Merken über Neustarts, Befehl:** Aufgaben 3, 5.
- **Schaltfläche B, Karte, „Später“:** Aufgabe 6. **Installer im Pagewing-Stil:** Aufgaben 9, 10. **Sitzung nach dem Update:** Aufgabe 8, 10, 11.
- **Signatur, Prüfsumme, Ablehnungen, Schlüsselliste, fest einkompiliert, kein Override:** Aufgaben 1, 12, 13. **Release-Workflow, dritter Job:** Aufgabe 12.
- **Windows (alle Benutzer/UAC, portable Hinweis), Linux (AppImage, `.deb` Hinweis), Paketform:** Aufgaben 3, 10, 11. **Fehlgeschlagene Installation:** Aufgaben 9, 10.
- **Offline-Zusage nachziehen, Entscheidungen, Roadmap:** Aufgabe 14. **Lizenzen:** keine neue Abhängigkeit; Prüfung in Aufgabe 14.
- **Voraussetzung Repo öffentlich:** Umsetzung und Ende-zu-Ende erst danach; Aufgaben 1 bis 11 sind davor testbar (Fake-Netz). **Offen vor dem Release `v0.2.0`:** Schlüssel erzeugt und als Secret angelegt (Projektinhaber), echter öffentlicher Schlüssel in `UpdateTrust`, Repo öffentlich.
