# Paketierung

Alles Gemeinsame für die Verteilung von Pagewing (Milestone 8). Das Werkzeug ist `tools/package.cs`; es läuft aus dem Repo-Root und braucht kein Netzwerk, außer `inno-install` und `runtime-fetch` (je ein Download mit fester SHA-256).

## Paketlayout

`package.cs layout` legt aus den Publish-Ordnern (`dotnet publish -c Release -r <rid>`) einen Ordner an:

```
Pagewing(.exe)            die App samt Bibliotheken (ohne *.pdb, *.dbg)
pdfium.dll / libpdfium.so
LICENSE
THIRD-PARTY-NOTICES.txt   Kopie von src/Pagewing.App/Resources/ThirdPartyNotices.txt
cli/
  pagewing(.exe)          die CLI samt eigener PDFium-Bibliothek
  pdfium.dll / libpdfium.so
```

Fehlt die PDFium-Bibliothek in App oder CLI, bricht der Befehl ab. Unter Linux sind Programme 0755, alles andere 0644.

## Dateinamen der Veröffentlichung

| Datei | Inhalt |
| --- | --- |
| `Pagewing-<Version>-win-x64-portable.zip` | portable ZIP für Windows |
| `Pagewing-<Version>-win-x64-setup.exe` | Installer für Windows |
| `SHA256SUMS-windows.txt`, `SHA256SUMS-linux.txt` | Zeilen `<sha256>  <Dateiname>`, je eine Datei für die Dateien des Windows- und des Linux-Jobs (jeder Job hängt seine Dateien selbst an das Release) |
| `fuse-3.15.0.tar.xz`, `type2-runtime-20251108.tar.gz` | Quelltext von libfuse (LGPL-2.1) und der AppImage-Runtime samt Bauskripten, unverändert von den Upstream-Adressen geladen und gegen feste SHA-256 geprüft (`package.cs runtime-sources`); stehen in `SHA256SUMS-linux.txt` |
| `pagewing_<Version>_amd64.deb` | Paket für Debian, Ubuntu, Linux Mint (Vorabversion `x.y.z-rc1` heißt im Paket `x.y.z~rc1`) |
| `Pagewing-<Version>-x86_64.AppImage` | AppImage für alle Linux-Distributionen ab der glibc des Build-Rechners, ohne `libfuse2` |
| `update-manifest.json`, `update-manifest.json.sig` | Update-Manifest (Version, je Paketform Dateiname, Größe und SHA-256) und seine Signatur (`<Kennung>:<Base64>`, ECDSA P-256); nur stabile Versionen `X.Y.Z`, siehe „Update-Manifest und Signatur“ |

## Befehle

```sh
dotnet run tools/package.cs -- layout --rid linux-x64 --app <publish-app> --cli <publish-cli> --out <layout>
dotnet run tools/package.cs -- zip --layout <layout> --version 1.0.0 --out Pagewing-1.0.0-win-x64-portable.zip   # nur Windows wird als ZIP ausgeliefert
dotnet run tools/package.cs -- sums <Datei>... --out SHA256SUMS-windows.txt   # je Betriebssystem eine Datei: SHA256SUMS-windows.txt, SHA256SUMS-linux.txt
dotnet run tools/package.cs -- check-binary <Datei>... [--max-glibc 2.35] [--max-glibcxx 3.4.30]   # GLIBC-, GLIBCXX- und NEEDED-Prüfung
dotnet run tools/package.cs -- deb --layout <layout> --version 1.0.0 --out pagewing_1.0.0_amd64.deb   # Linux, dpkg-deb
dotnet run tools/package.cs -- deb-smoke pagewing_1.0.0_amd64.deb
dotnet run tools/package.cs -- runtime-sources --out <Ordner>   # Quellen von libfuse und der AppImage-Runtime (LGPL-2.1), SHA-256 geprüft
dotnet run tools/package.cs -- update-manifest --version 1.0.0 --sums SHA256SUMS-windows.txt SHA256SUMS-linux.txt --sizes assets.json --out update-manifest.json
dotnet run tools/package.cs -- update-sign --manifest update-manifest.json --key-env PAGEWING_UPDATE_SIGNING_KEY --id <Kennung> --out update-manifest.json.sig
dotnet run tools/package.cs -- update-verify --manifest update-manifest.json --sig update-manifest.json.sig --keys-from-source
dotnet run tools/package.cs -- selftest
```

Die ZIP-Datei ist reproduzierbar: sortierte Einträge, Zeitstempel 2000-01-01, feste Rechte (0755 für Programme, sonst 0644), oberster Ordner `Pagewing-<Version>/`. Zweimal aus demselben Layout erzeugt ergibt sie dieselbe SHA-256-Summe.

## Verzeichnisse

- `linux/pagewing.desktop`: Vorlage der `.desktop`-Datei. Sie darf sich von `DesktopEntry.Render` (`Pagewing.Core.Distribution`) nur in der Zeile `Exec` unterscheiden (`DesktopEntryTests.RenderIsTheSameForTheTemplateInPackaging`). `Exec` ist der Pfad des `.deb` (`/usr/lib/pagewing/Pagewing %F`). `StartupWMClass=Pagewing` ist die Fensterklasse, die Avalonia unter X11 setzt (belegt mit `xprop WM_CLASS` an der Native-AOT-App unter Xvfb: `"Pagewing", "Pagewing"`).
- `linux/AppRun`: Einstieg des AppImage (POSIX-`sh`), startet `$APPDIR/usr/lib/pagewing/Pagewing` mit allen Argumenten.
- `linux/control.template`, `linux/copyright.template`: Vorlage der `.deb`-Steuerdatei (Platzhalter `@VERSION@`, `@INSTALLED_SIZE@`, `@MAINTAINER@`) und die Copyright-Datei im Debian-Format 1.0.
- `windows/pagewing.iss`: Skript des Windows-Installers (Inno Setup 6), siehe unten.

## Windows-Installer

Werkzeug ist Inno Setup 6.7.3, nur zum Bauen (Version, Quelle und Prüfsumme: `Docs/DECISIONS.md`, Eintrag „Windows-Installer mit Inno Setup 6.7.3 (M8)“). Alle drei Befehle laufen nur unter Windows, ohne Administratorrechte:

```powershell
dotnet run tools/package.cs -- inno-install        # lädt Inno Setup 6.7.3, prüft die SHA-256, installiert nach %LOCALAPPDATA%\PagewingBuildTools\InnoSetup-6.7.3
dotnet publish src/Pagewing.App -c Release -r win-x64 -o out/app
dotnet publish src/Pagewing.Cli -c Release -r win-x64 -o out/cli
dotnet run tools/package.cs -- layout --rid win-x64 --app out/app --cli out/cli --out out/layout
dotnet run tools/package.cs -- installer --layout out/layout --version 1.0.0 --out out/Pagewing-1.0.0-win-x64-setup.exe
dotnet run tools/package.cs -- installer-smoke out/Pagewing-1.0.0-win-x64-setup.exe
```

- `installer` übergibt `ISCC.exe` die Version, das Layout und das Icon (`/D…`) sowie Ausgabeordner und -name (`/O`, `/F`); jede Warnung von `ISCC.exe` gilt als Fehler. `--iscc <Pfad>` nimmt ein anderes `ISCC.exe`. Die Ausgabedatei muss `Pagewing-<Version>-win-x64-setup.exe` heißen.
- Der Installer installiert standardmäßig nur für den aktuellen Benutzer (`%LOCALAPPDATA%\Programs\Pagewing`, keine Administratorrechte); ein Dialog oder `/ALLUSERS` wählt die Installation für alle Benutzer (`Program Files\Pagewing`). Danach registriert er Pagewing für PDF-Dateien (`Pagewing.exe --register-file-types=user|machine`), die Frage nach dem Standardprogramm stellt erst die App. Die Deinstallation meldet die Registrierung zuerst ab und lässt die Einstellungen in `%APPDATA%\Pagewing` stehen.
- Still installieren: `Pagewing-<Version>-win-x64-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR="<Ordner>"` (dazu optional `/ALLUSERS` oder `/CURRENTUSER`, `/LOG="<Datei>"`); still deinstallieren: `"<Ordner>\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART`.
- `installer-smoke` installiert still nach `%TEMP%\Pagewing Test ä&`, prüft `Pagewing.exe`, `cli\pagewing.exe info testdata\pdfs\text-basic.pdf` und `HKCU\Software\Classes\Pagewing.PDF`, installiert ein zweites Mal darüber, deinstalliert mit `unins000.exe /VERYSILENT` und prüft, dass Ordner und Schlüssel weg sind und `%APPDATA%\Pagewing` unverändert ist. Er bricht ab, wenn Pagewing auf dem Rechner schon installiert oder registriert ist (er würde es sonst entfernen). Protokolle: `%TEMP%\pagewing-installer-smoke\`.
- Die Regeln des Skripts (AppId, Rechte, Sprachen, `[Run]`/`[UninstallRun]`, keine Benutzerdaten) prüft `package.cs selftest` auch unter Linux.

## Linux-Paket (.deb)

Gebaut wird aus demselben Layout wie die ZIP-Datei (`package.cs layout --rid linux-x64 …`), nur unter Linux mit `dpkg-deb`:

```sh
dotnet publish src/Pagewing.App -c Release -r linux-x64 -o out/app
dotnet publish src/Pagewing.Cli -c Release -r linux-x64 -o out/cli
dotnet run tools/package.cs -- layout --rid linux-x64 --app out/app --cli out/cli --out out/layout
dotnet run tools/package.cs -- deb --layout out/layout --version 1.0.0 --out out/pagewing_1.0.0_amd64.deb
dotnet run tools/package.cs -- deb-smoke out/pagewing_1.0.0_amd64.deb
```

- Inhalt: Programm und `cli/` in `/usr/lib/pagewing/`, `/usr/bin/pagewing` als Symlink auf die CLI, `.desktop`-Datei, Symbole `hicolor/<N>x<N>`, `copyright`. Alles `root:root`, 0755/0644; zweimal gebaut ergibt dieselbe SHA-256-Summe. Begründung der Felder und Lintian-Grenzen: `Docs/DECISIONS.md`, Eintrag „Linux-Paket .deb …“. Der Maintainer ist vorläufig (`Deb.Maintainer` in `tools/package.cs`).
- `deb-smoke` prüft Felder, Besitzer, `Exec` der `.desktop`-Datei und den Symlink im entpackten Baum, ruft `pagewing info testdata/pdfs/text-basic.pdf` aus dem entpackten Paket auf und führt `desktop-file-validate` und `lintian` aus, wenn sie installiert sind (`sudo apt-get install -y lintian desktop-file-utils`; fehlen sie, gibt es nur einen Hinweis). Lintian-Fehler außer `embedded-library`, `unstripped-binary-or-object` und `no-changelog` lassen den Test scheitern.
- Das GLIBC-Limit prüft `check-binary` auf den Dateien des Layouts; es gilt erst auf dem Build-Rechner mit der ältesten unterstützten glibc.

Manuelle Abnahme (nicht automatisierbar):

1. `sudo apt install ./pagewing_<Version>_amd64.deb` auf Ubuntu 22.04, Debian 12 und Linux Mint: Abhängigkeiten werden aufgelöst, `pagewing info <datei.pdf>` läuft.
2. Pagewing erscheint im Anwendungsmenü mit Symbol; ein Doppelklick auf eine PDF im Dateimanager öffnet sie in Pagewing (nach „Öffnen mit“ bzw. als Standardprogramm).
3. `sudo apt remove pagewing` entfernt alles; `~/.config/pagewing` bleibt.

## AppImage

Gebaut wird aus demselben Layout wie ZIP und `.deb`, nur unter Linux mit `mksquashfs` (`sudo apt-get install -y squashfs-tools`; geprüft mit 4.6.1):

```sh
dotnet run tools/package.cs -- runtime-fetch     # lädt die type2-Runtime 20251108, prüft Größe und SHA-256, legt sie nach ~/.cache/PagewingBuildTools
dotnet run tools/package.cs -- layout --rid linux-x64 --app out/app --cli out/cli --out out/layout
dotnet run tools/package.cs -- appimage --layout out/layout --version 1.0.0 --out out/Pagewing-1.0.0-x86_64.AppImage
dotnet run tools/package.cs -- appimage-smoke out/Pagewing-1.0.0-x86_64.AppImage
```

- Format: AppImage Typ 2, also die **statisch gelinkte** type2-Runtime des AppImage-Projekts (feste Version, Adresse, Größe und SHA-256 in `tools/package.cs`, `AppImageRuntime`; Herkunft der Prüfsumme: `Docs/DECISIONS.md`, Eintrag „AppImage: type2-Runtime 20251108 …“) und direkt dahinter ein SquashFS-Abbild (`mksquashfs … -root-owned -noappend -comp zstd -no-xattrs`, `SOURCE_DATE_EPOCH=946684800`). Die Runtime findet das Abbild am Ende ihrer eigenen ELF-Datei; sie liest zstd und gzip, nicht xz. Weil sie statisch gelinkt ist, braucht der Rechner kein `libfuse2`; fehlt FUSE ganz, entpackt sie sich selbst in einen Temp-Ordner und startet von dort.
- `appimage` prüft die Runtime (Größe und SHA-256) vor dem Bauen, sonst entsteht nichts; `--runtime <Datei>` nimmt eine andere Ablage als `~/.cache/PagewingBuildTools/type2-runtime-20251108-x86_64`. Die Ausgabedatei muss `Pagewing-<Version>-x86_64.AppImage` heißen. Zweimal gebaut ergibt sie dieselbe SHA-256 (sortiert, alles `root`, Zeit 2000-01-01, keine erweiterten Attribute).
- Inhalt (AppDir): `AppRun`, `pagewing.desktop` (die Vorlage `linux/pagewing.desktop` mit `Exec=Pagewing %F`, sonst gleich), `pagewing.png` (256 px) und `.DirIcon` (Symlink darauf) in der Wurzel; das Layout unter `usr/lib/pagewing/` (App, `cli/`, `LICENSE`, `THIRD-PARTY-NOTICES.txt`); die Symbole unter `usr/share/icons/hicolor/<N>x<N>/apps/pagewing.png`. Von dort nimmt sie `LinuxDefaultAppService`, wenn Pagewing aus dem AppImage heraus Standardprogramm wird (er schreibt dann `.desktop` und Symbole nach `~/.local/share`).
- `appimage-smoke` prüft, dass das Image mit genau der festgehaltenen Runtime beginnt (sonst führt es nichts aus) und sie keine `NEEDED`-Bibliothek hat, kopiert es in einen Ordner `Pagewing Test` (mit Leerzeichen), prüft `--appimage-offset`, entpackt mit `--appimage-extract`, prüft `AppRun`, `.desktop`, `.DirIcon` und Symbole, ruft `usr/lib/pagewing/cli/pagewing info testdata/pdfs/text-basic.pdf` auf und startet die App zweimal mit `--measure-startup testdata/pdfs/text-basic.pdf` (erwartet `first-sharp`): mit `APPIMAGE_EXTRACT_AND_RUN=1` (ohne FUSE) und ohne Umgebungsvariable (über FUSE, wo vorhanden; ohne FUSE gilt die klare Meldung der Runtime als erwartetes Ergebnis). Ohne Anzeige nimmt es `xvfb-run`; fehlt beides, wird der Start mit Hinweis übersprungen.
- Die AppImage-Runtime enthält Code unter eigenen Lizenzen (Runtime MIT, musl, libfuse LGPL-2.1, squashfuse, zstd, zlib); `Pagewing-<Version>-x86_64.AppImage --appimage-help` nennt sie.

Manuelle Abnahme (nicht automatisierbar):

1. CachyOS (Arch-basiert) und Ubuntu 24.04, jeweils **ohne** `libfuse2` (`pacman -Q fuse2` bzw. `dpkg -l libfuse2t64` meldet nichts): `chmod +x Pagewing-<Version>-x86_64.AppImage`, Doppelklick im Dateimanager und `./Pagewing-<Version>-x86_64.AppImage <datei.pdf>` im Terminal starten die App und öffnen die PDF.
2. Linux Mint: dasselbe, außerdem aus einem Ordner mit Leerzeichen (`./Pagewing\ Test/Pagewing-<Version>-x86_64.AppImage testdata/pdfs/text-rich.pdf`).
3. In der App „Als Standardprogramm festlegen“ wählen: Pagewing erscheint im Anwendungsmenü mit Symbol (`~/.local/share/applications/pagewing.desktop` zeigt auf die AppImage-Datei), ein Doppelklick auf eine PDF öffnet sie in Pagewing.
4. Auf einem Rechner ohne FUSE-Modul und ohne `fusermount` (etwa in einem Container) startet das AppImage **nicht** von selbst: die Runtime meldet „No suitable fusermount binary found“ und endet mit Code 127 (gemessen im Trockenlauf 39914298456). Mit `APPIMAGE_EXTRACT_AND_RUN=1 ./Pagewing-<Version>-x86_64.AppImage` (oder `--appimage-extract-and-run`) entpackt es sich in einen Temp-Ordner und startet.

## Release bauen (`.github/workflows/release.yml`)

Das Release selbst legt der Projektinhaber an (`gh release create vX.Y.Z --target main …`); der Workflow hängt nur Dateien an. Auslöser sind `release: published` und `workflow_dispatch` (Eingaben `tag`, `upload`). Je Betriebssystem läuft ein Job auf dem eigenen Runner (`dotnet-windows`, `dotnet-linux`), jeder lädt seine Dateien selbst mit `gh release upload "$TAG" … --clobber` hoch (kein `upload-artifact`).

0. Vorab (Job `preflight`, ohne Secret, nur Leserechte): Beim Hochladen prüft er mit `update-verify --keys-from-source`, dass der Tag in `UpdateTrust.cs` einen echten öffentlichen Schlüssel hat. Steht dort nur der Platzhalter, laufen die Bau-Jobs nicht, und nichts wird hochgeladen (ein Release, das nur dem Platzhalter vertraut, könnte sich nie aktualisieren, und ein Tag lässt sich nicht nachbessern). Ein Trockenlauf (`upload=false`) überspringt die Prüfung.
1. Der Tag muss `vX.Y.Z` oder `vX.Y.Z-<Vorabversion>` heißen, sonst bricht der Job ab. Die Version (Tag ohne `v`) geht als `-p:Version=<Version>` an `dotnet publish` für App und CLI; ein `-rc1` heißt im `.deb` `~rc1`.
2. Windows: Publish (Native AOT), `licenses check`, `layout`, `zip`, `inno-install`, `installer`, `installer-smoke`, `sums` nach `SHA256SUMS-windows.txt`, Hochladen. Dateien: `Pagewing-<Version>-win-x64-portable.zip`, `Pagewing-<Version>-win-x64-setup.exe`.
3. Linux: Der Job baut in **einem** `docker run` von `ubuntu:22.04` (`packaging/linux/container-build.sh`, weil die glibc des Runners neuer als 2.35 ist; Eintrag „Mindest-glibc 2.35 und Bauweise der Linux-Pakete: Container …“): Publish, `licenses check`, `check-binary --max-glibc 2.35` (**blockierend**: ein Binary mit neuerer glibc darf nicht ausgeliefert werden), `layout`, `deb`, `deb-smoke`, `runtime-fetch`, `appimage`, `appimage-smoke`, `runtime-sources`, `sums` nach `SHA256SUMS-linux.txt`. Hochgeladen wird danach auf dem Host (der Container sieht `GH_TOKEN` nie). Dateien: `pagewing_<Version>_amd64.deb`, `Pagewing-<Version>-x86_64.AppImage`, die zwei Quellarchive. Das Skript lässt sich auf einem Linux-Rechner mit Docker auch von Hand starten (Befehl im Kopf der Datei, dazu `VERSION` und `DEBVERSION`).
4. Hochgeladen wird nur bei `release` oder `workflow_dispatch` mit `upload=true` (das Release muss dann existieren). Sonst baut der Lauf und führt alle Smoke-Tests aus.
5. Manifest (Job `manifest`, Milestone 8.1): läuft erst, wenn beide Jobs erfolgreich hochgeladen haben, und nur beim Hochladen (`needs: [windows, linux]`, auf `dotnet-linux`). Er prüft mit `PAGEWING_REQUIRE_RELEASE_KEY=1` (`UpdateTrustTests.ReleaseKeysAreNotThePlaceholderWhenReleasing`), dass `UpdateTrust.cs` nicht mehr den Platzhalter enthält, baut das Werkzeug ohne Secret aus dem Checkout des Tags in einen frisch geleerten Ordner des Jobs (`dotnet project convert` und `dotnet build` nach `$RUNNER_TEMP/package-tool`, ohne Buildserver und ohne den gemeinsamen Skript-Cache, den auch die Pull-Request-CI auf diesem Runner nutzt), lädt `SHA256SUMS-windows.txt`, `SHA256SUMS-linux.txt` und die Dateigrößen (`gh release view <Tag> --json assets`), baut `update-manifest.json` (`update-manifest`), signiert es (`update-sign`, Kennung aus `UPDATE_KEY_ID` im Job; der Schritt mit dem Secret startet nur das gebaute Werkzeug, kein `dotnet run` oder `dotnet build`), prüft die Signatur mit den Schlüsseln des Programms (`update-verify --keys-from-source`) und lädt Manifest und Signatur mit `--clobber` hoch. Eine Vorabversion (`vX.Y.Z-…`) bekommt kein Manifest: Der Job überspringt dann alle Schritte, denn die App liest nur `X.Y.Z` und `releases/latest` nennt nie eine Vorabversion.

Trockenlauf (Bau und Smoke-Tests ohne Hochladen), vom Projektinhaber auf Wunsch zu starten; seine Runner müssen laufen. Vor `gh release create` gibt es den Tag noch nicht; `ref` baut deshalb einen Branch (die Version kommt aus `tag`), und mit `ref` wird nie hochgeladen (der Lauf bricht ab, wenn zusätzlich `upload=true` gesetzt ist):

```sh
git push -u origin release/v0.1.0
gh workflow run release.yml --ref release/v0.1.0 -f tag=v0.1.0 -f ref=release/v0.1.0 -f upload=false
```

`dotnet run tools/package.cs -- selftest` (`TagRuleParts`) prüft, dass die Tag-Regel in beiden Jobs gleich ist und genau SemVer-Tags annimmt (`v0.1.0`, `v0.1.0-rc.1`; nicht `V0.1.0`, `v0.1`, `0.1.0`).

Voraussetzungen der Runner (zusätzlich zu denen für Native AOT in `CLAUDE.md`):

- Linux: `docker` (der Runner-Benutzer muss es nutzen dürfen, `docker info` ohne `sudo`) und `gh`. Alle Bauwerkzeuge (`clang`, `zlib1g-dev`, `dpkg`, `squashfs-tools`, `binutils`, `xvfb`, `lintian`, `desktop-file-utils` …) installiert `container-build.sh` im Container, das Image `ubuntu:22.04` und die Pakete kommen beim Lauf aus dem Netz.
- Windows: `gh` im PATH (`winget install --id GitHub.cli`); Inno Setup holt `inno-install` selbst ins Benutzerprofil (kein Administrator).
- `check-binary` erlaubt neben den glibc-Kernbibliotheken nur `libfontconfig.so.1` (NEEDED von `libSkiaSharp.so`). Sie ist im `.deb` über `Depends: libfontconfig1` abgedeckt; das AppImage setzt sie, wie die X11-Bibliotheken, auf dem Rechner voraus (es bringt keine Systembibliotheken mit).
- Der Lauf braucht die Rechte `contents: write` nur zum Hochladen; er läuft nie für Forks (Repo privat, siehe `CLAUDE.md`, Abschnitt „CI“).
- Der erste echte Lauf gehört zum Release `v0.1.0`; bis dahin ist das Hochladen ungeprüft.
- Für den Job `manifest`: das Secret `PAGEWING_UPDATE_SIGNING_KEY` im Repo (siehe „Signaturschlüssel für Updates“) und auf dem Linux-Runner wie beim Linux-Job `gh` und .NET. Ohne echten öffentlichen Schlüssel in `UpdateTrust.cs` scheitert schon der Job `preflight`, bevor etwas gebaut oder hochgeladen wird; der Platzhalter-Test im Job `manifest` ist die zweite Sicherung.

## Update-Manifest und Signatur

`update-manifest.json` ist kompaktes JSON ohne Zeilenumbruch (`{"format":1,"version":"X.Y.Z","files":[{"kind":…,"name":…,"size":…,"sha256":…}]}`), so wie `Pagewing.Core.Updates.UpdateManifest` es liest. `update-manifest` nimmt aus den Prüfsummen nur die vier bekannten Arten (`win-x64-setup`, `win-x64-portable`, `linux-appimage`, `linux-deb`) mit genau den Dateinamen dieser Version; die Quellarchive und Prüfsummendateien gehören nicht hinein. Fehlt eine Art, hat eine Datei mit bekannter Endung eine andere Version, fehlt eine Größe, ist eine Größe 0 oder über 1 GiB oder ist eine Zeile der Prüfsummen kaputt, entsteht nichts. Neue Arten kommen hinzu, ohne `format` zu erhöhen (ältere Programme überspringen sie).

`update-manifest.json.sig` ist `<Kennung>:<Base64 der 64 Byte langen ECDSA-P256-Signatur (IEEE P1363, SHA-256) über die rohen Bytes des Manifests>`. `update-sign` liest den privaten Schlüssel **nur** aus der Umgebungsvariablen, die `--key-env` nennt (nie als Argument, nie in einer Datei, nie in der Ausgabe), nimmt nur eine Kennung, die in `src/Pagewing.Core/Updates/UpdateTrust.cs` steht (nie den Platzhalter), prüft, dass der Schlüssel zu ihr gehört, und schreibt die Signatur erst, nachdem sie mit dem öffentlichen Schlüssel aus `UpdateTrust.cs` geprüft ist. `update-verify --keys-from-source` prüft Signatur und Inhalt wie die App und scheitert, solange `UpdateTrust.cs` nur den Platzhalter enthält.

Testvektor: `testdata/updates/update-manifest.json` und `.sig` sind die Ausgabe von `update-manifest` und `update-sign` für feste Eingaben (`SelfTest.VectorFiles` in `tools/package.cs`), einmal mit einem Wegwerfschlüssel signiert, dessen privater Teil gelöscht ist; sein öffentlicher Teil steht in `tests/Pagewing.Core.Tests/Updates/ReleaseToolTests.cs`. `ReleaseToolTests` prüft den Vektor mit dem `ManifestVerifier` der App, `package.cs selftest` prüft, dass `update-manifest` noch genau diese Bytes schreibt und `update-verify` sie annimmt. Ändert sich das Format bewusst: Manifest mit `update-manifest` aus denselben Eingaben neu schreiben, mit `update-keygen` (Ausgabe in einen Temp-Ordner) einen neuen Wegwerfschlüssel erzeugen, mit `update-sign --source <Datei mit der ausgegebenen Zeile>` signieren, den ausgegebenen Eintrag in `ReleaseToolTests.cs` einsetzen und die private Datei löschen.

## Signaturschlüssel für Updates

Den Schlüssel erzeugt nur der Projektinhaber, einmal, auf dem eigenen Rechner. Der private Schlüssel erscheint nie im Chat, in einem Issue, PR, Log oder Repository, und kein Agent liest ihn.

1. Erzeugen, **außerhalb** jedes Git-Arbeitsverzeichnisses (`update-keygen` verweigert Pfade in einem Repository und überschreibt nie eine vorhandene Datei; die Datei ist nur für den eigenen Benutzer lesbar, unter Windows per ACL, unter Linux 0600):

   ```powershell
   dotnet run tools/package.cs -- update-keygen --id release-1 --private-out "$env:USERPROFILE\pagewing-update-signing-key.txt"
   ```

   Auf stdout steht danach der öffentliche Schlüssel als Eintrag für `UpdateTrust.Keys`:

   ```csharp
           new UpdateKey(
               "release-1",
               Convert.FromBase64String("MFkw…")),
   ```

2. Secret anlegen, ohne den Wert auf die Kommandozeile zu schreiben (nie `--body`):

   ```powershell
   Get-Content -Raw "$env:USERPROFILE\pagewing-update-signing-key.txt" | gh secret set PAGEWING_UPDATE_SIGNING_KEY --repo benjo-code/Pagewing
   ```

   (bash: `gh secret set PAGEWING_UPDATE_SIGNING_KEY --repo benjo-code/Pagewing < ~/pagewing-update-signing-key.txt`)

3. Öffentlichen Schlüssel eintragen (auf einem Branch, per PR nach `dev`): in `src/Pagewing.Core/Updates/UpdateTrust.cs` den Eintrag `new UpdateKey(PlaceholderKeyId, …)` in `Keys` durch die ausgegebene Zeile ersetzen (die Konstante `PlaceholderKeyId` bleibt, die Tests nennen sie). Heißt die Kennung nicht `release-1`, auch `UPDATE_KEY_ID` im Job `manifest` von `.github/workflows/release.yml` ändern; `package.cs selftest` prüft, dass sie in `UpdateTrust.cs` steht.

4. Prüfen, ohne etwas zu veröffentlichen:

   ```powershell
   $env:PAGEWING_UPDATE_SIGNING_KEY = Get-Content -Raw "$env:USERPROFILE\pagewing-update-signing-key.txt"
   dotnet run tools/package.cs -- update-sign --manifest testdata/updates/update-manifest.json --key-env PAGEWING_UPDATE_SIGNING_KEY --id release-1 --out "$env:TEMP\check.sig"
   Remove-Item Env:PAGEWING_UPDATE_SIGNING_KEY
   dotnet run tools/package.cs -- update-verify --manifest testdata/updates/update-manifest.json --sig "$env:TEMP\check.sig" --keys-from-source
   Remove-Item "$env:TEMP\check.sig"
   $env:PAGEWING_REQUIRE_RELEASE_KEY = '1'; dotnet test --project tests/Pagewing.Core.Tests -- --filter-method "*ReleaseKeysAreNotThePlaceholderWhenReleasing"; Remove-Item Env:PAGEWING_REQUIRE_RELEASE_KEY
   ```

5. Die Datei mit dem privaten Schlüssel offline sichern (Passwortmanager oder verschlüsselter Datenträger) und dann vom Rechner löschen.

Schlüsselwechsel (Verdacht auf Diebstahl, etwa ein gekaperter Runner): mit `update-keygen` einen neuen Schlüssel unter neuer Kennung (`release-2`) erzeugen und **zusätzlich** in `UpdateTrust.Keys` eintragen; dieses Release wird noch mit dem alten Schlüssel signiert, denn nur ihm vertrauen die installierten Programme. Danach Secret und `UPDATE_KEY_ID` auf den neuen Schlüssel umstellen und im folgenden Release den alten Eintrag entfernen. Geht der private Schlüssel verloren, gibt es diesen Weg nicht: Die Nutzer müssen einmal von Hand auf eine Version mit neuem Schlüssel umsteigen.

## Update prüfen

Ende-zu-Ende-Prüfung des Updaters (Milestone 8.1) mit zwei lokal gebauten Versionen gegen einen Testserver auf dem eigenen Rechner. Sie ist **manuell** (Installer, UAC-Abfrage, Neustart der App) und läuft nie in der CI. Der echte Signaturschlüssel kommt darin nicht vor, und kein Release ist beteiligt.

### Testaufbau

Das ausgelieferte Programm vertraut nur den Schlüsseln und Adressen in `src/Pagewing.Core/Updates/UpdateTrust.cs` (GitHub; kein Schalter, keine Umgebungsvariable). Nur die Bauzeit-Eigenschaft `-p:PagewingUpdateTestBuild=true` ersetzt diese Datei durch `UpdateTrust.TestBuild.cs` (`Pagewing.Core.csproj`); der Release-Workflow setzt sie nie (`package.cs selftest` prüft `release.yml` und `container-build.sh`). Ein Testaufbau

- braucht `-p:PagewingUpdateTestPort=<Port>` und `-p:PagewingUpdateTestKey=<öffentlicher Testschlüssel, Base64>`, sonst bricht der Bau mit einer Meldung ab;
- vertraut genau diesem einen Schlüssel mit der Kennung `test-1` (nicht dem Platzhalter, nicht dem echten Schlüssel) und fragt nur `http://127.0.0.1:<Port>/` (`releases/latest`, `download/v<Version>/<Datei>`, als „Download-Seite“ `/`); einer Umleitung folgt er außer zu GitHub nur auf genau diese Adresse;
- zeigt in „Über Pagewing“ hinter der Version „(Testaufbau)“ bzw. „(test build)“ (der Text steht nur in `UpdateTrust.TestBuild.cs`, ein Release enthält ihn nicht);
- baut nach `bin\update-test-build\` und `obj\update-test-build\` jedes Projekts (`Directory.Build.props`), mischt sich also nie mit einem normalen Bau; ein normaler `dotnet build` oder `dotnet publish` danach braucht nichts zu löschen.

Die Tests des Testaufbaus laufen einmal mit der Eigenschaft (`$Key` ist ein beliebiger öffentlicher P-256-Schlüssel, etwa der aus Schritt 1 unten; der normale `dotnet test` prüft den Release-Aufbau):

```powershell
dotnet test --project tests/Pagewing.Core.Tests -p:PagewingUpdateTestBuild=true -p:PagewingUpdateTestPort=8765 "-p:PagewingUpdateTestKey=$Key" -- --filter-class "*UpdateTrustTests"
dotnet test --project tests/Pagewing.App.Tests -p:PagewingUpdateTestBuild=true -p:PagewingUpdateTestPort=8765 "-p:PagewingUpdateTestKey=$Key" -- --filter-class "*GitHubUpdateSourceTests" --filter-class "*AboutCardTests"
```

### Testserver

`tools/update-testserver.cs` (Einzeldatei, `dotnet run`) legt den Ordner eines Test-Releases an und beantwortet Anfragen **nur auf 127.0.0.1**:

```powershell
dotnet run tools/update-testserver.cs -- prepare --version 0.0.2 --key <privater Testschlüssel> --out <neuer Ordner> <Release-Datei>... [--tamper signature|checksum]
dotnet run tools/update-testserver.cs -- serve --root <Ordner> --port 8765     # Ende mit Strg+C
dotnet run tools/update-testserver.cs -- selftest
```

- `prepare` kopiert die angegebenen Dateien (Namen wie im Release: `Pagewing-<Version>-win-x64-setup.exe`, `…-win-x64-portable.zip`, `…-x86_64.AppImage`, `pagewing_<Version>_amd64.deb`), schreibt `update-manifest.json` im Format des Release-Jobs, signiert es mit dem privaten Testschlüssel (die Datei von `update-keygen`, Kennung `test-1`) und schreibt `latest.json` (die Antwort von `releases/latest`). Es legt immer einen neuen Ordner an und gibt den öffentlichen Schlüssel aus, der zum Testaufbau passen muss. `--tamper signature` ändert das Manifest nach dem Signieren (die Signatur passt nicht mehr), `--tamper checksum` signiert ein Manifest mit falscher SHA-256.
- `serve` liefert nur `/releases/latest`, `/download/v<x.y.z>/<Datei>` (302 auf `/assets/<Datei>`, wie GitHub auf seinen Speicher umleitet), `/assets/<Datei>` (nur Dateien direkt im Ordner) und `/`; alles andere ist 404 (kein `..`, keine `%`-Kodierung, kein Backslash, keine absoluten Pfade, keine Unterordner). Jede Anfrage steht mit Status im Terminal, so ist zu sehen, was die App fragt.
- `selftest` (auch in der CI) prüft die Routen, Pfadtraversal gegen den laufenden Server und `prepare` mit einem Schlüssel im Speicher; der Server lauscht dabei auf einem freien Port von 127.0.0.1 und wird am Ende beendet.

### Windows (Projektinhaber, manuell)

Wo: ein Windows-Rechner **ohne installiertes Pagewing**, am besten eine VM. Der Installer hat dieselbe feste `AppId` wie die echten Releases und würde eine vorhandene Installation ersetzen; die Prüfung schreibt außerdem in `%APPDATA%\Pagewing` und registriert Dateitypen. Bauen geht auch auf dem Haupt-PC (.NET-10-SDK und Native-AOT-Voraussetzungen, siehe `CLAUDE.md`); dann die Setup-Dateien, den Ordner des Testservers und den Testserver als eigenständiges Programm in die VM kopieren (`dotnet publish tools/update-testserver.cs -r win-x64 --self-contained -o "$T\server-tool"`, Aufruf `& "$T\server-tool\update-testserver.exe" serve …`). Testserver und App müssen auf demselben Rechner laufen (127.0.0.1); die Windows Sandbox geht nur mit beiden darin.

1. Testschlüssel und Port (Wegwerfschlüssel nur für diese Prüfung, **außerhalb** des Repos; `update-keygen` verweigert Pfade in einem Git-Arbeitsverzeichnis):

   ```powershell
   $T = "$env:TEMP\pagewing-update-test"; New-Item -ItemType Directory $T | Out-Null
   dotnet run tools/package.cs -- update-keygen --id test-1 --private-out "$T\test-key.txt" | Out-File -Encoding utf8 "$T\test-key.cs"
   $Key = (Select-String -Path "$T\test-key.cs" -Pattern 'FromBase64String\("([^"]+)"\)').Matches[0].Groups[1].Value
   $Port = 8765
   ```

2. Alte (`0.0.1`) und neue (`0.0.2`) Version als Testaufbau bauen, je mit Installer (Inno Setup vorher mit `package.cs inno-install`):

   ```powershell
   foreach ($v in '0.0.1', '0.0.2') {
     dotnet publish src/Pagewing.App -c Release -r win-x64 -o "$T\$v\app" "-p:Version=$v" -p:PagewingUpdateTestBuild=true "-p:PagewingUpdateTestPort=$Port" "-p:PagewingUpdateTestKey=$Key"
     dotnet publish src/Pagewing.Cli -c Release -r win-x64 -o "$T\$v\cli" "-p:Version=$v"
     dotnet run tools/package.cs -- layout --rid win-x64 --app "$T\$v\app" --cli "$T\$v\cli" --out "$T\$v\layout"
     dotnet run tools/package.cs -- installer --layout "$T\$v\layout" --version $v --out "$T\Pagewing-$v-win-x64-setup.exe"
   }
   ```

3. Test-Release `0.0.2` anlegen und den Testserver in einem **zweiten** Terminal starten (bleibt offen, zeigt jede Anfrage). Die von `prepare` ausgegebene Zeile „oeffentlicher Testschluessel“ muss `$Key` sein:

   ```powershell
   dotnet run tools/update-testserver.cs -- prepare --version 0.0.2 --key "$T\test-key.txt" --out "$T\server" "$T\Pagewing-0.0.2-win-x64-setup.exe"
   dotnet run tools/update-testserver.cs -- serve --root "$T\server" --port 8765
   ```

4. Durchlauf A, **„nur für mich“**: `Pagewing-0.0.1-win-x64-setup.exe` starten und die Installation nur für den eigenen Benutzer wählen (Ordner `%LOCALAPPDATA%\Programs\Pagewing`). Pagewing 0.0.1 starten:
   - „Über Pagewing“ zeigt „Programmversion 0.0.1 (Testaufbau)“.
   - Zwei PDFs öffnen (etwa `testdata\pdfs\text-rich.pdf` und `text-dense.pdf`), im zweiten bis Seite 50 blättern und den Zoom ändern, ein zweites Fenster mit einem Tab öffnen. Die Karte „Pagewing als Standard für PDFs festlegen?“ mit „Als Standard festlegen“ beantworten und in der Liste, die Windows zeigt, Pagewing wählen.
   - Einstellungen und Verlauf sichern (gibt es `settings.json` noch nicht, vorher eine Einstellung ändern): `New-Item -ItemType Directory "$T\before" -Force | Out-Null; Copy-Item "$env:APPDATA\Pagewing\settings.json", "$env:APPDATA\Pagewing\recent.json" "$T\before\"`
   - Nach dem ersten scharfen Seitenbild erscheint in der Titelleiste „Aktualisieren“ (der Server zeigt `GET /releases/latest`, `…/update-manifest.json`, `…/update-manifest.json.sig`). Erscheint nichts: Befehlspalette (Strg+K) → „Nach Updates suchen“.
   - „Aktualisieren“ → „Installieren“: Das Fenster „Pagewing wird aktualisiert“ zeigt Herunterladen, Prüfen, Installieren, Neustart (der Server zeigt die erneute Abfrage des Manifests, `GET /download/v0.0.2/… -> 302` und `GET /assets/… -> 200`).

   Erwartet nach dem Neustart:
   - „Über Pagewing“ zeigt „Programmversion 0.0.2 (Testaufbau)“; `(Get-Item "$env:LOCALAPPDATA\Programs\Pagewing\Pagewing.exe").VersionInfo.ProductVersion` beginnt mit `0.0.2`.
   - Beide Fenster mit allen Tabs sind zurück, der aktive Tab ist derselbe, Seite 50 und der Zoom stimmen.
   - `fc.exe /b "$T\before\settings.json" "$env:APPDATA\Pagewing\settings.json"` meldet keine Unterschiede. `fc.exe "$T\before\recent.json" "$env:APPDATA\Pagewing\recent.json"`: dieselben Dateien; geändert haben dürfen sich nur Seite und Zoom der beim Beenden offenen Tabs, die das Schließen wie immer schreibt.
   - Das Standardprogramm bleibt: Doppelklick auf eine PDF im Explorer öffnet Pagewing 0.0.2; `reg query HKCU\Software\Classes\Pagewing.PDF\shell\open\command` zeigt auf `%LOCALAPPDATA%\Programs\Pagewing\Pagewing.exe`.
   - **Keine** UAC-Abfrage erschien. Unter „Apps“ steht Pagewing genau einmal, mit Version 0.0.2. `%APPDATA%\Pagewing\updates` ist leer oder fehlt.

   Danach Pagewing über „Apps“ deinstallieren (`%APPDATA%\Pagewing` bleibt stehen).

5. Durchlauf B, **„alle Benutzer“**: wie Durchlauf A, aber im Installer die Installation für alle Benutzer wählen (UAC bestätigen; Ordner `C:\Program Files\Pagewing`). Vorher `Remove-Item "$env:APPDATA\Pagewing\update.json"` (sonst gilt die Prüfung aus Durchlauf A noch 24 Stunden; „Nach Updates suchen“ geht auch). Erwartet wie in A, außerdem: Beim Installieren des Updates **erscheint die UAC-Abfrage** des Installers; das Programm liegt danach in `C:\Program Files\Pagewing` (nicht zusätzlich unter `%LOCALAPPDATA%\Programs`), und `reg query HKLM\Software\Classes\Pagewing.PDF\shell\open\command` zeigt dorthin. Danach deinstallieren.

6. Fehlerpfade (mit installiertem 0.0.1, „nur für mich“ genügt außer beim letzten Punkt; vor jedem Punkt `Remove-Item "$env:APPDATA\Pagewing\update.json" -ErrorAction SilentlyContinue`):
   - **Server aus** (Strg+C im Server-Terminal): Pagewing startet und arbeitet normal, ohne Karte und ohne Meldung, auch nach „Nach Updates suchen“. Wird der Server erst beendet, nachdem „Aktualisieren“ erschienen ist, zeigt „Installieren“ „Pagewing konnte das Update nicht laden …“ mit „Erneut versuchen“; nach erneutem Start des Servers installiert „Erneut versuchen“.
   - **Manifest manipuliert**: `prepare … --out "$T\server-bad-signature" --tamper signature`, dann `serve --root "$T\server-bad-signature" …`: kein „Aktualisieren“, keine Meldung, und der Server zeigt keine Anfrage nach der Setup-Datei. War „Aktualisieren“ von einem guten Server schon da, zeigt „Installieren“ „Das Update hat die Prüfung der Signatur nicht bestanden …“; nichts wird installiert.
   - **Falsche Prüfsumme**: `prepare … --out "$T\server-bad-checksum" --tamper checksum`, dann `serve` darauf: „Aktualisieren“ erscheint (die Signatur stimmt); „Installieren“ lädt die Datei und zeigt „Die geladene Datei passt nicht zum signierten Manifest und wurde gelöscht …“. Die Version bleibt 0.0.1, `%APPDATA%\Pagewing\updates` enthält keine Setup-Datei.
   - **UAC abgelehnt** (Installation „alle Benutzer“): bei der UAC-Abfrage „Nein“ wählen: 0.0.1 startet wieder, und das Fenster zeigt „Die Installation ist fehlgeschlagen. Ihre Version bleibt, wie sie ist.“ mit „Erneut versuchen“ (fragt erneut) und „Schließen“.

7. Aufräumen: Pagewing deinstallieren, Server beenden, `Remove-Item -Recurse $T` (löscht auch den privaten Testschlüssel), bei Bedarf `%APPDATA%\Pagewing` und `%LOCALAPPDATA%\Pagewing` der Testmaschine löschen.

Was der Projektinhaber beurteilt und als Nachweis in die Roadmap einträgt: Beide Durchläufe zeigen die erwarteten Ergebnisse (vor allem Tabs und Positionen, `settings.json` bytegleich, Standardprogramm, UAC nur bei „alle Benutzer“); Inno Setup behält beim stillen Update Installationsart und Ordner (keine zweite Installation, kein zweiter Eintrag unter „Apps“); die Fehlerpfade enden still bzw. mit der genannten Meldung und installieren nie etwas.

Ohne Oberfläche prüft `dotnet run tools/package.cs -- update-smoke "$T\Pagewing-0.0.1-win-x64-setup.exe" "$T\Pagewing-0.0.2-win-x64-setup.exe" --key "$T\test-key.txt"` den Helfermodus mit genau diesen beiden Installern des Testaufbaus (`test-1` ist die Vorgabe von `--key-id`). Er installiert wie `installer-smoke` nach `%TEMP%` und schreibt die Registrierung unter `HKCU`, also ebenfalls nur auf einem Rechner ohne installiertes Pagewing.

### Linux, AppImage (Projektinhaber, manuell)

Auf einem Linux-Rechner oder einer VM mit .NET-10-SDK, `clang`, `zlib1g-dev` und `squashfs-tools`. Der Testaufbau ist an die glibc dieses Rechners gebunden; für die Prüfung genügt das.

```sh
T=$(mktemp -d /tmp/pagewing-update-test.XXXXXX); PORT=8765
dotnet run tools/package.cs -- update-keygen --id test-1 --private-out "$T/test-key.txt" > "$T/test-key.cs"
KEY=$(sed -n 's/.*FromBase64String("\([^"]*\)").*/\1/p' "$T/test-key.cs")
dotnet run tools/package.cs -- runtime-fetch
for v in 0.0.1 0.0.2; do
  dotnet publish src/Pagewing.App -c Release -r linux-x64 -o "$T/$v/app" -p:Version=$v -p:PagewingUpdateTestBuild=true -p:PagewingUpdateTestPort=$PORT -p:PagewingUpdateTestKey=$KEY
  dotnet publish src/Pagewing.Cli -c Release -r linux-x64 -o "$T/$v/cli" -p:Version=$v
  dotnet run tools/package.cs -- layout --rid linux-x64 --app "$T/$v/app" --cli "$T/$v/cli" --out "$T/$v/layout"
  dotnet run tools/package.cs -- appimage --layout "$T/$v/layout" --version $v --out "$T/Pagewing-$v-x86_64.AppImage"
done
dotnet run tools/update-testserver.cs -- prepare --version 0.0.2 --key "$T/test-key.txt" --out "$T/server" "$T/Pagewing-0.0.2-x86_64.AppImage"
dotnet run tools/update-testserver.cs -- serve --root "$T/server" --port $PORT     # in einem zweiten Terminal
```

1. `mkdir -p ~/"Pagewing Test" && cp "$T/Pagewing-0.0.1-x86_64.AppImage" ~/"Pagewing Test"/ && chmod +x ~/"Pagewing Test"/Pagewing-0.0.1-x86_64.AppImage`, starten (Doppelklick oder Terminal), PDFs öffnen und Positionen ändern wie unter Windows, auf der Karte „Pagewing als Standard für PDFs festlegen?“ „Als Standard festlegen“ wählen, dann `cp ~/.config/pagewing/settings.json ~/.config/pagewing/recent.json "$T/"`.
2. „Aktualisieren“ → „Installieren“. Erwartet: Die Datei `~/Pagewing Test/Pagewing-0.0.1-x86_64.AppImage` enthält jetzt die neue Version (der Name bleibt, „Über Pagewing“ zeigt „Programmversion 0.0.2 (Testaufbau)“); die App startet von selbst mit allen Fenstern, Tabs und Positionen; `cmp "$T/settings.json" ~/.config/pagewing/settings.json` gibt nichts aus; der Menüeintrag (`~/.local/share/applications/pagewing.desktop`) zeigt weiter auf die Datei; im Ordner bleibt keine `*.new`; `~/.config/pagewing/updates` ist leer oder fehlt.
3. Fehlerpfade wie unter Windows (Server aus, `--tamper signature`, `--tamper checksum`; vorher jeweils `rm -f ~/.config/pagewing/update.json`), dazu der **schreibgeschützte Ordner**: `chmod 555 ~/"Pagewing Test"`, Update auslösen: Meldung „Der Ordner ist schreibgeschützt: … Laden Sie das neue AppImage von Hand herunter.“, die alte Datei bleibt unverändert; danach `chmod 755 ~/"Pagewing Test"`.
4. `.deb` (optional): Ein Testaufbau als `.deb` (`package.cs deb` aus dem Layout von 0.0.1) zeigt statt „Installieren“ den Hinweis auf die Download-Seite; „Download-Seite öffnen“ fragt über die Link-Karte nach `http://127.0.0.1:8765/`.
5. Aufräumen: `rm -rf "$T" ~/"Pagewing Test"` und `~/.local/share/applications/pagewing.desktop` löschen.
