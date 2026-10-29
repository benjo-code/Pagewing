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

Das Release selbst legt der Projektinhaber an (`gh release create vX.Y.Z --target main …`); der Workflow hängt nur Dateien an. Der Workflow startet **nur von Hand** (`workflow_dispatch`, Eingaben `tag`, `ref`, `upload`; seit M12.1 kein Auslöser `release: published` mehr, weil die Workflow-Referenz eines Release-Ereignisses ein Tag ist und die Workflow-Einschränkung der Runner-Gruppe nur Branch-Referenzen nennen kann). Ablauf: Release anlegen (`gh release create vX.Y.Z --target main …`), dann den Workflow vom Branch `main` starten:

```sh
gh workflow run release.yml --ref main -f tag=vX.Y.Z -f upload=true
```

Die Bau-Jobs laufen je Betriebssystem auf dem eigenen Runner (`dotnet-windows`, `dotnet-linux`); nur der Job `manifest` läuft auf einem gehosteten, kurzlebigen Runner (`ubuntu-24.04`) hinter dem Environment `release-signing` (siehe Punkt 5). Jeder Bau-Job lädt seine Dateien selbst mit `gh release upload "$TAG" … --clobber` hoch (kein `upload-artifact`).

0. Vorab (Job `preflight`, ohne Secret, nur Leserechte): Beim Hochladen prüft er mit `update-verify --keys-from-source`, dass der Tag in `UpdateTrust.cs` einen echten öffentlichen Schlüssel hat. Steht dort nur der Platzhalter, laufen die Bau-Jobs nicht, und nichts wird hochgeladen (ein Release, das nur dem Platzhalter vertraut, könnte sich nie aktualisieren, und ein Tag lässt sich nicht nachbessern). Ein Trockenlauf (`upload=false`) überspringt die Prüfung.
1. Der Tag muss `vX.Y.Z` oder `vX.Y.Z-<Vorabversion>` heißen, sonst bricht der Job ab. Die Version (Tag ohne `v`) geht als `-p:Version=<Version>` an `dotnet publish` für App und CLI; ein `-rc1` heißt im `.deb` `~rc1`.
2. Windows: Publish (Native AOT), `licenses check`, `layout`, `zip`, `inno-install`, `installer`, `installer-smoke`, `sums` nach `SHA256SUMS-windows.txt`, Hochladen. Dateien: `Pagewing-<Version>-win-x64-portable.zip`, `Pagewing-<Version>-win-x64-setup.exe`.
3. Linux: Der Job baut in **einem** `docker run` von `ubuntu:22.04` (`packaging/linux/container-build.sh`, weil die glibc des Runners neuer als 2.35 ist; Eintrag „Mindest-glibc 2.35 und Bauweise der Linux-Pakete: Container …“): Publish, `licenses check`, `check-binary --max-glibc 2.35` (**blockierend**: ein Binary mit neuerer glibc darf nicht ausgeliefert werden), `layout`, `deb`, `deb-smoke`, `runtime-fetch`, `appimage`, `appimage-smoke`, `runtime-sources`, `sums` nach `SHA256SUMS-linux.txt`. Hochgeladen wird danach auf dem Host (der Container sieht `GH_TOKEN` nie). Dateien: `pagewing_<Version>_amd64.deb`, `Pagewing-<Version>-x86_64.AppImage`, die zwei Quellarchive. Das Skript lässt sich auf einem Linux-Rechner mit Docker auch von Hand starten (Befehl im Kopf der Datei, dazu `VERSION` und `DEBVERSION`).
4. Hochgeladen wird nur mit `upload=true` (das Release muss dann existieren). Sonst baut der Lauf und führt alle Smoke-Tests aus. Nach dem Hochladen berechnet jeder Bau-Job die SHA-256 seiner `SHA256SUMS-*.txt` und gibt sie als Job-Ausgabe `sums_sha256` aus (Kleinbuchstaben-Hex).
5. Manifest (Job `manifest`, Milestone 8.1): läuft erst, wenn beide Jobs erfolgreich hochgeladen haben, und nur beim Hochladen (`needs: [windows, linux]`, auf einem gehosteten `ubuntu-24.04`-Runner mit `environment: release-signing`: der Job wartet auf die Freigabe des Projektinhabers, das Environment erlaubt nur die Branches `main` und `dev`). Er prüft mit `PAGEWING_REQUIRE_RELEASE_KEY=1` (`UpdateTrustTests.ReleaseKeysAreNotThePlaceholderWhenReleasing`), dass `UpdateTrust.cs` nicht mehr den Platzhalter enthält, baut das Werkzeug ohne Secret aus dem Checkout des Tags in einen frisch geleerten Ordner des Jobs (`dotnet project convert` und `dotnet build` nach `$RUNNER_TEMP/package-tool`, ohne Buildserver und ohne Verzeichnis-Importe), lädt `SHA256SUMS-windows.txt`, `SHA256SUMS-linux.txt` und die Dateigrößen (`gh release view <Tag> --json assets`), vergleicht die SHA-256 beider Dateien mit den Job-Ausgaben der Bau-Jobs (`sums-verify`, Schritt „Verify the checksum files against the build jobs“; ein Unterschied, eine leere oder kaputte Ausgabe bricht ab, bevor etwas signiert wird; das schützt vor einer zwischen Hochladen und Signieren ausgetauschten Prüfsummendatei, nicht vor einem Bau-Runner, der falsche Dateien meldet), baut `update-manifest.json` (`update-manifest`), signiert es (`update-sign`, Kennung aus `UPDATE_KEY_ID` im Job; der Schritt mit dem Secret startet nur das gebaute Werkzeug, kein `dotnet run` oder `dotnet build`), prüft die Signatur mit den Schlüsseln des Programms (`update-verify --keys-from-source`) und lädt Manifest und Signatur mit `--clobber` hoch. Eine Vorabversion (`vX.Y.Z-…`) bekommt kein Manifest: Der Job überspringt dann alle Schritte, denn die App liest nur `X.Y.Z` und `releases/latest` nennt nie eine Vorabversion.

Trockenlauf (Bau und Smoke-Tests ohne Hochladen), vom Projektinhaber auf Wunsch zu starten; seine Runner müssen laufen. Der Workflow muss auf `main` oder `dev` liegen (`--ref`). Vor `gh release create` gibt es den Tag noch nicht; `ref` baut deshalb einen Branch (die Version kommt aus `tag`), und mit `ref` wird nie hochgeladen (der Lauf bricht ab, wenn zusätzlich `upload=true` gesetzt ist):

```sh
gh workflow run release.yml --ref dev -f tag=v0.1.0 -f upload=false
gh workflow run release.yml --ref dev -f tag=v0.1.0 -f ref=<Branch> -f upload=false
```

`dotnet run tools/package.cs -- selftest` (`TagRuleParts`) prüft, dass die Tag-Regel in beiden Jobs gleich ist und genau SemVer-Tags annimmt (`v0.1.0`, `v0.1.0-rc.1`; nicht `V0.1.0`, `v0.1`, `0.1.0`).

Voraussetzungen der Runner (zusätzlich zu denen für Native AOT in `CLAUDE.md`):

- Linux: `docker` (der Runner-Benutzer muss es nutzen dürfen, `docker info` ohne `sudo`) und `gh`. Alle Bauwerkzeuge (`clang`, `zlib1g-dev`, `dpkg`, `squashfs-tools`, `binutils`, `xvfb`, `lintian`, `desktop-file-utils` …) installiert `container-build.sh` im Container, das Image `ubuntu:22.04` und die Pakete kommen beim Lauf aus dem Netz.
- Windows: `gh` im PATH (`winget install --id GitHub.cli`); Inno Setup holt `inno-install` selbst ins Benutzerprofil (kein Administrator).
- `check-binary` erlaubt neben den glibc-Kernbibliotheken nur `libfontconfig.so.1` (NEEDED von `libSkiaSharp.so`). Sie ist im `.deb` über `Depends: libfontconfig1` abgedeckt; das AppImage setzt sie, wie die X11-Bibliotheken, auf dem Rechner voraus (es bringt keine Systembibliotheken mit). OpenSSL lädt .NET erst zur Laufzeit (`dlopen`, kein NEEDED, `check-binary` sieht es nicht); die Update-Prüfung braucht es für HTTPS und die Signatur, deshalb verlangt das `.deb` seit Milestone 8.1 `libssl3 | libssl3t64` (Eintrag „Linux: AppImage ersetzt sich, `.deb` nur Hinweis, `libssl` als Abhängigkeit (M8.1)“).
- Der Lauf braucht die Rechte `contents: write` nur zum Hochladen; er startet nie für Pull Requests oder Forks (nur `workflow_dispatch`, siehe `CLAUDE.md`, Abschnitt „CI“).
- Der erste echte Lauf gehört zum Release `v0.1.0`; bis dahin ist das Hochladen ungeprüft.
- Für den Job `manifest`: kein eigener Runner mehr nötig (gehosteter `ubuntu-24.04`-Runner mit `gh` im Image, .NET kommt von `setup-dotnet`). Voraussetzung ist das Environment `release-signing` (Einstellungen des Repos → Environments: Bereitstellungszweige nur `main` und `dev`, Pflichtprüfer der Projektinhaber) mit dem Environment-Secret `PAGEWING_UPDATE_SIGNING_KEY` (siehe „Signaturschlüssel für Updates“); ohne das Environment wartet der Job nicht auf eine Freigabe, ohne das Secret scheitert die Signatur. Es darf **kein** Repository-Secret gleichen Namens geben (ein Job mit Environment liest auch Repository-Secrets). Mit `upload=true` muss der Commit des Tags auf `main` oder `dev` liegen (Schritt „Check that the tag is on main or dev“ in `preflight` und `manifest`, vor jedem Checkout): Die Zweig-Regel des Environments gilt nur für die Workflow-Referenz, der Code im Job kommt aus dem Tag. Der Prüfer der Freigabe sieht trotzdem nur den Tag-Namen und sollte Tag und Commit vor dem Klick ansehen; ein Tag-Ruleset (`v*`: nur der Projektinhaber) ergänzt das. Ohne echten öffentlichen Schlüssel in `UpdateTrust.cs` scheitert schon der Job `preflight`, bevor etwas gebaut oder hochgeladen wird; der Platzhalter-Test im Job `manifest` ist die zweite Sicherung.

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

2. Secret als **Environment-Secret** von `release-signing` anlegen (nie zusätzlich als Repository-Secret; das Environment vorher im Repo anlegen: Bereitstellungszweige `main` und `dev`, Pflichtprüfer der Projektinhaber), ohne den Wert auf die Kommandozeile zu schreiben (nie `--body`):

   ```powershell
   Get-Content -Raw "$env:USERPROFILE\pagewing-update-signing-key.txt" | gh secret set PAGEWING_UPDATE_SIGNING_KEY --env release-signing --repo benjo-code/Pagewing
   ```

   (bash: `gh secret set PAGEWING_UPDATE_SIGNING_KEY --env release-signing --repo benjo-code/Pagewing < ~/pagewing-update-signing-key.txt`)

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
- gilt **nur** mit `-p:`/`/p:` auf der Kommandozeile: Steht einer der drei Namen als Umgebungsvariable da (MSBuild machte daraus sonst eine Eigenschaft, und ein Release-Job erbt die Umgebung seines Runners), bricht jeder Bau ab (`CheckPagewingUpdateTestBuildSource` in `Directory.Build.props`, geprüft von `package.cs selftest`);
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

## Annotationen im Fremdviewer prüfen

Handprüfung des Projektinhabers zu Milestone 9 und 10: Was Pagewing speichert (Hervorhebung, Unterstreichung, Durchstreichung mit Kommentar, seit M10 auch Freihand, Rechteck, Ellipse, Linie und Pfeil; Erscheinungsbild als eigenes Objekt von PDFiums Generator, Druck-Flag, inkrementelles Speichern), muss in anderen Programmen an derselben Stelle und in derselben Farbe erscheinen. Der Round-Trip in PDFium ist automatisch getestet (`MarkupRoundTripTests`, `InteropSampleTests`); wie Chrome und Acrobat die Dateien zeigen, prüft nur ein Mensch. Die Prüfung läuft nie in der CI.

### Muster erzeugen

`tests/Pagewing.Core.Tests/Annotations/InteropSampleTests.cs` annotiert die Testdateien über die echte Pipeline der App (`AnnotationEditor` über eine `ViewerSession`, Speichern über `DocumentSaver` mit Prüfung vor dem Ersetzen) und legt je Quelle das unveränderte Original `<name>.pdf` und die annotierte Fassung `<name>__annotated.pdf` in einen Ordner, dazu `README-interop.txt`: welches Wort welche Markierung in welcher Farbe trägt, die Kommentartexte und die Hinweise zu signierten und verschlüsselten Dateien. Der Test prüft dabei selbst, dass die ersten Bytes jeder Datei das Original sind, dass PDFium sie öffnet und Art, Farbe, Vierecke und Kommentar wieder liest und dass jedes Erscheinungsbild ein eigenes Objekt ist. Ohne die Umgebungsvariable meldet er sich als übersprungen; er schreibt nur in den angegebenen Ordner (gearbeitet wird in einem Temp-Ordner), nie in `testdata/`.

```powershell
$env:PAGEWING_INTEROP_OUT = "$env:TEMP\pagewing-interop"
dotnet test tests/Pagewing.Core.Tests --filter-class "*InteropSampleTests"
Remove-Item Env:PAGEWING_INTEROP_OUT
```

```sh
PAGEWING_INTEROP_OUT=/tmp/pagewing-interop dotnet test tests/Pagewing.Core.Tests --filter-class "*InteropSampleTests"
```

Quellen: `text-basic.pdf`, `text-rich.pdf` (eine Hervorhebung über zwei Zeilen, eine Unterstreichung über einer Webadresse), `rotated-page.pdf` (`/Rotate 90`), `cropbox.pdf` (versetzte CropBox), `annotations.pdf` (die fremde Hervorhebung wird blau umgefärbt), `annotations-foreign.pdf` (die fremde grüne Hervorhebung ohne Namen wird gelöscht), `signed-docmdp-3.pdf`, `signed-docmdp-1.pdf`, `encrypted_hello_world_r6.pdf` (Passwort `hôtel`) und `linearized.pdf`. Der Inhalt ist bei jedem Lauf gleich (Texte, Farben, Autor „Pagewing Interop“, Datum 2026-10-24 12:00 UTC); nur die `/NM`-Namen sind zufällig.

**Zeichnungen (M10):** Dieselbe Datei erzeugt sieben weitere Muster `drawings-*__annotated.pdf` (Quelle daneben als `<quelle>.pdf`): `drawings-text-basic` (alle fünf Arten, auf Seite 1 zwei Pfeile, auf Seite 2 ein dickes Freihand und ein dünnes Rechteck mit Kommentar), `drawings-annotations` (vier Zeichnungen neben den fremden Annotationen von `annotations.pdf`), `drawings-rotated-page` (`/Rotate 90`), `drawings-cropbox`, `drawings-drawings-foreign` (eigene Zeichnungen neben den fremden aus `drawings-foreign.pdf`, auf Seite 1 das Freihand nach dem Anlegen verschoben, Seite 2 gedreht mit CropBox), `drawings-moved` (alle fünf Arten nach dem Anlegen verschoben) und `drawings-recolored` (alle fünf nach dem Anlegen umgefärbt). Alles geht durch dieselbe Pipeline wie in der App (`AnnotationEditor.ApplyDrawingAsync`, `ReplaceGeometryAsync`, `UpdateAsync`, Speichern über `DocumentSaver`); ein Freihand-Strich wird wie beim Loslassen mit der Maus vereinfacht (Toleranz 0,5 pt) und geglättet. `README-interop.txt` nennt je Zeichnung Art, Farbe, Stärke, Rahmen in Punkten von oben links und bei verschobenen Zeichnungen die alte Lage. Der Test prüft selbst: Originalbytes unverändert am Anfang, jede Zeichnung nach dem Neuöffnen genau einmal mit Art, Farbe, Kommentar, Autor und Form, jede Zeichnung ein eigenes Objekt mit Erscheinungsbild-Formular in der angehängten Revision (Linie und Pfeil mit `/Subj`), fremde Annotationen gleich in Art, Name, Kommentar, Autor, Farbe (± 1/255) und Form.

### Prüfliste je Viewer

Pflicht unter Windows: **Chrome** (eingebauter PDF-Viewer) und **Adobe Acrobat Reader**. Optional: Edge, Firefox (pdf.js), unter Linux Okular und Evince. Original und annotierte Fassung nebeneinander öffnen, `README-interop.txt` daneben:

1. **Lage und Farbe:** Jede Markierung liegt genau auf den Wörtern aus `README-interop.txt`, in der dort genannten Farbe der Palette (Gelb `#FFEB3B`, Grün `#8BE08B`, Blau `#7CC4FA`, Rosa `#FF8FC7`, Orange `#FFB454`); die Hervorhebung über zwei Zeilen in `text-rich` deckt beide Zeilen und nicht den Zwischenraum.
2. **Text unter der Hervorhebung lesbar:** Die Farbe liegt hinter dem Text (multipliziert), die Buchstaben bleiben dunkel und scharf.
3. **Unterstreichung und Durchstreichung:** Die Unterstreichung sitzt knapp unter den Wörtern, die Durchstreichung läuft durch ihre Mitte, beide in der Farbe der Markierung und nicht länger als die Wörter.
4. **Kommentar:** Acrobat: Der Kommentarbereich („Kommentare“) listet jede Markierung mit Kommentartext und Autor „Pagewing Interop“, beim Überfahren mit der Maus erscheint der Kommentar. Chrome und die übrigen: notieren, ob und wie sie den Kommentar zeigen (Chrome zeigt Kommentare von Markierungen nicht immer; das ist dann kein Fehler von Pagewing, solange das Erscheinungsbild stimmt).
5. **Gedrehte Seite und CropBox:** In `rotated-page__annotated.pdf` liegt die Hervorhebung auf „Rotated“ in der gedrehten Ansicht, in `cropbox__annotated.pdf` auf „CropBox“ innerhalb des sichtbaren Ausschnitts.
6. **Fremde Annotationen:** `annotations__annotated.pdf`: Die vorhandene Hervorhebung ist blau statt gelb und trägt den neuen Kommentar, Link und übrige Annotationen sind unverändert. `annotations-foreign__annotated.pdf`: Die grüne Hervorhebung über „Second mark without a name.“ fehlt; Antwort („Agreed“), Popup, Ink, Freitext, Notiz und Link sind wie im Original.
7. **Signierte Dateien** (Acrobat, Unterschriftenbereich): Die beiden Testdateien sind Strukturattrappen ohne gültige Signatur; Acrobat meldet sie im Original und in der Kopie als ungültig bzw. nicht prüfbar, und das ist erwartet. Aussagekräftig ist nur eine **echt signierte** Datei, die der Projektinhaber selbst signiert und nie eincheckt: in Acrobat eine beliebige eigene PDF signieren (eigene digitale ID; „Zertifizieren“ mit „Kommentieren erlaubt“ ergibt DocMDP-Stufe 3, „Zertifizieren“ ohne Änderungen Stufe 1, eine einfache Signatur ist ohne DocMDP), außerhalb des Repos speichern, in Pagewing markieren und mit Strg+S speichern (bei Stufe 1 und 2 warnt Pagewing vorher; trotzdem speichern), dann in Acrobat öffnen. Erwartet bei Stufe 3 und ohne DocMDP: Die Signatur gilt weiter, Acrobat nennt die Änderungen nach dem Signieren als erlaubt („Kommentare hinzugefügt“). Erwartet bei Stufe 1: Acrobat meldet die Zertifizierung als durch nicht erlaubte Änderungen ungültig. Pagewing selbst behauptet nie, eine Signatur sei gültig; festgehalten wird nur, was Acrobat anzeigt.
8. **Verschlüsselt:** `encrypted_hello_world_r6__annotated.pdf` fragt nach dem Passwort `hôtel` und zeigt danach die Hervorhebung samt Kommentar; ohne Passwort öffnet sie nicht.
9. **Linearisiert:** `linearized__annotated.pdf` öffnet ohne Fehlermeldung mit beiden Markierungen (Seite 1 und 2). Dass Acrobat unter „Dokumenteigenschaften“ für die Kopie „Schnelle Webansicht: Nein“ zeigt, ist erwartet (ein inkrementelles Update hebt die Linearisierung auf).
10. **Ohne Reparatur:** Keine Datei löst eine Meldung „beschädigt“ oder „repariert“ aus, und Acrobat fragt beim Schließen einer unveränderten Datei nicht nach dem Speichern (diese Frage heißt, dass Acrobat die Datei beim Öffnen repariert hat).
11. **Druck:** In der Druckvorschau (Acrobat „Drucken“, Chrome Strg+P) erscheinen die Markierungen (Druck-Flag gesetzt).

### Zeichnungen (M10)

Zusätzlich zu den Punkten 1 bis 11 für `drawings-*__annotated.pdf` (Original daneben; Pagewing selbst zeigt dieselben Dateien als Vergleich). `README-interop.txt` nennt die Lage jeder Zeichnung. Acrobat und Chrome vergleichen, ob Strich und Lage mit Pagewing übereinstimmen:

12. **Strich:** Farbe (Rot `#DC1E1E`, Schwarz `#000000`, Blau `#1E50DC`, Grün `#148C3C`, Orange `#F08200`) und Stärke (1, 2, 4 pt) sind in jedem Viewer dieselben wie in Pagewing; das Freihand ist eine glatte Welle ohne Ecken und Lücken, die Enden sind rund.
13. **Rechteck und Ellipse:** Der Strich liegt **innerhalb** des genannten Rahmens (der Rahmen ist die Außenkante des Strichs), nicht außerhalb, und die Form ist nicht gefüllt, auch nicht schwach getönt.
14. **Linie und Pfeil:** Eine gerade Linie; der Pfeil hat Schaft und offene Spitze, die Spitze endet am Ende des Rahmens und ist nicht abgeschnitten (besonders der dicke orange Pfeil in `drawings-text-basic` und der dünne am rechten oberen Rand).
15. **Gedrehte Seite und CropBox:** In `drawings-rotated-page` und `drawings-cropbox` sitzen alle fünf Zeichnungen an den Stellen aus `README-interop.txt` (gemessen auf der Seite, wie sie am Bildschirm aussieht), nicht gespiegelt oder gedreht; in `drawings-drawings-foreign` Seite 2 ebenso.
16. **Fremde Zeichnungen:** In `drawings-drawings-foreign` und `drawings-annotations` sehen die Annotationen anderer Programme wie in der Quelldatei aus (Position, Farbe, Strichstärke, gestrichelt, Antwort „Nice stroke“ am Ink in Teal). **Bekannte Abweichung zum Notieren, kein Fehler der Liste:** PDFium gibt beim Speichern jeder fremden Annotation ohne eigenes Erscheinungsbild eines (`/PDFIUM_HasGeneratedAP`; `ink-0002`, `ink-0008`, `square-0003`, `square-0004`, `circle-0006` in `drawings-foreign.pdf`). Der Wolkenrahmen von `square-0004` (`/BE /S /C`) wird dabei vermutlich ein glatter Rahmen, das Rechteck von `square-0003` (gestrichelt) und `ink-0002` ändern ihr `/Rect` um die halbe Strichstärke. Festhalten, wie diese fünf Annotationen in Acrobat und Chrome gegenüber der Quelle aussehen und ob Acrobat die Datei danach als „geändert“ meldet.
17. **Verschobene Zeichnung genau einmal:** In `drawings-moved` ist jede der fünf Zeichnungen **einmal** da, an der neuen Stelle (alte Lage in `README-interop.txt`), und an der alten Stelle ist nichts (kein Geist, kein zweiter Strich in Acrobats Kommentarliste, die genau fünf Zeichnungen zählt). In der angehängten Revision liegt zu jeder verschobenen Zeichnung noch ihre erste, von keiner Seite aufgeführte Fassung (`README-interop.txt` nennt die Zahl); sie darf in keinem Viewer erscheinen. Dasselbe in `drawings-drawings-foreign` Seite 1 für das Freihand.
18. **Umgefärbte Zeichnung nur in der neuen Farbe:** In `drawings-recolored` hat jede Zeichnung ausschließlich die neue Farbe (kein Rand oder Schatten in der alten Farbe dahinter). Im Kommentarbereich von Acrobat steht die neue Farbe.
19. **Linie und Pfeil in Acrobat (offene Frage aus Spike A):** Pagewing legt beide als Freihand (`/Ink`) mit `/Subj (Line)` bzw. `/Subj (Arrow)` an. Erwartet: Acrobat führt sie im Kommentarbereich als „Freihand“. Festhalten, ob das Feld „Betreff“ (Subject) der Kommentareigenschaften „Arrow“ bzw. „Line“ zeigt und ob es nach einem Speichern der Datei **durch Acrobat** (Datei → Speichern unter, andere Datei) in der Kopie erhalten bleibt (neu in Pagewing öffnen: Pagewing erkennt den Pfeil nur mit `/Subj` und passender Form; sonst zeigt es ihn als Freihand, ohne Fehlermeldung). Bleibt das Feld nicht erhalten, ist das ein Befund für den Backlog, kein Fehler der Datei.
20. **Auswählen in Acrobat:** Eine Zeichnung lässt sich mit dem Kommentar-Werkzeug anklicken, verschieben und löschen; Acrobat zeigt Rechteck und Ellipse als „Rechteck“ bzw. „Oval“.

### Textfelder (M10)

`InteropSampleTests.WritesTextBoxSamplesForOtherViewers` (dieselbe Variable `PAGEWING_INTEROP_OUT`) schreibt `textbox-plain.pdf`, `textbox-rotated.pdf`, `textbox-cropbox.pdf`, `textbox-rotated-cropbox.pdf`, `textbox-moved.pdf` und `textbox-edited.pdf` samt `README-textboxes.txt` (Text, Größe und Lage jedes Textfelds). In Chrome und Acrobat Reader:

21. **Text und Zeichen:** Jede Zeile und jedes Zeichen steht da (ü ö ß – € © ½ « » µ), aufrecht (auch in `textbox-rotated*` und `textbox-cropbox`), innerhalb des in `README-textboxes.txt` genannten Rahmens, in der genannten Farbe; keine Meldung über fehlende Schriften.
22. **Kommentarliste:** Acrobat führt jedes Textfeld mit seinem Text (und Autor „Pagewing Interop“).
23. **Verschoben und geändert:** `textbox-moved` zeigt das Textfeld **einmal**, an der neuen Stelle; `textbox-edited` nur den neuen Text in 20 pt, zwei Zeilen.
24. **Bearbeiten in Acrobat:** Ein Doppelklick bearbeitet den Text auf der Seite; danach zeichnet Acrobat ihn in Helvetica (erwartet), auf gedrehten Seiten mitgedreht (bekannte, hingenommene Grenze). Öffnet man die in Acrobat gespeicherte Datei in Pagewing, ist das Textfeld dort gesperrt (löschbar, nicht änderbar).

### Unterschriften (M12)

`InteropSampleTests.WritesSignatureSamplesForOtherViewers` (dieselbe Variable `PAGEWING_INTEROP_OUT`) schreibt `signature-drawn.pdf`, `signature-image.pdf`, `signature-rotated.pdf`, `signature-cropbox.pdf`, `signature-moved.pdf`, `signature-recoloured.pdf` und `signature-on-signed-document.pdf` samt `README-signatures.txt` (Art, Lage und Größe jeder Unterschrift). Eine gezeichnete Unterschrift ist ein Freihand (`/Ink`) mit `/Subj (Signature)`, eine importierte ein Stempel (`/Stamp`) mit einem Bild und dem privaten Schlüssel `/PagewingSignature`. Keine davon ist eine digitale Signatur. In Chrome und Acrobat Reader:

25. **Keine weiße Box:** In `signature-image`, `-rotated`, `-cropbox`, `-moved` und `-on-signed-document` zeigt die importierte Unterschrift nur die Tinte (dunkelblau, weicher Rand) auf dem Seitentext darunter, ohne weißes oder graues Rechteck. Das Bild ist ein RGB-Bild mit `/SMask`; ein Viewer, der die Maske ignoriert, zeigte hier eine weiße Fläche (Befund festhalten, in Acrobat und Chrome nicht erwartet).
26. **Aufrecht und am richtigen Ort:** In `signature-rotated` (Seite mit `/Rotate 90`) und `signature-cropbox` (versetzte CropBox) stehen beide Unterschriften aufrecht, nicht gespiegelt, an der in `README-signatures.txt` genannten Stelle (Punkte von links oben der angezeigten Seite) und in der genannten Größe.
27. **Verschoben genau einmal:** `signature-moved` zeigt die Unterschrift **einmal**, an der neuen Stelle (150 pt tiefer, 40 pt weiter rechts), und an der alten Stelle ist nichts; Acrobats Kommentarliste zählt genau eine.
28. **Umgefärbt nur in der neuen Farbe:** `signature-recoloured` zeigt die gezeichnete Unterschrift ausschließlich in Rot, ohne Rand oder Schatten in Dunkelblau.
29. **Kommentarliste und Auswählen:** Acrobat führt jede Unterschrift mit Autor „Pagewing Interop“ (die gezeichnete als „Freihand“, die importierte als „Stempel“); sie lässt sich mit dem Kommentar-Werkzeug anklicken, verschieben und löschen.
30. **Betreff nach Acrobats Speichern (offene Frage):** Eine Datei in Acrobat öffnen, unter anderem Namen speichern und die Kopie in Pagewing öffnen. Festhalten, ob die gezeichnete Unterschrift ihren `/Subj (Signature)` behält (sonst zeigt Pagewing sie als Freihand, ohne Fehlermeldung) und ob die importierte ihren Schlüssel `/PagewingSignature` behält (sonst ist sie dort gesperrt: weder änderbar noch löschbar). Bleibt etwas nicht erhalten, ist das ein Befund für den Backlog, kein Fehler der Datei.
31. **Signierte Attrappe:** `signature-on-signed-document` ist eine Strukturattrappe (DocMDP-Stufe 3, keine gültige Signatur); Acrobat meldet sie als ungültig bzw. nicht prüfbar, und das ist erwartet. Festhalten, was Acrobat zur Datei mit beiden Unterschriften sagt. Pagewing selbst nennt keine Signatur gültig und keine Unterschrift rechtsgültig.
32. **Druck und dunkle Seite in Pagewing:** Dieselben Dateien in Pagewing zeigen die Unterschriften auf der hellen und der dunklen Seite lesbar (auf der dunklen aufgehellt, ohne helles Rechteck); Strg+P → „Microsoft Print to PDF“ druckt sie in der eigenen Farbe, auch **ungespeichert**.

### Ergebnis festhalten

Das Ergebnis ist der Nachweis der Abnahmekriterien von Milestone 9 „Gespeicherte Annotationen erscheinen in einem anderen Viewer …“ und „Digital signierte Test-PDF …“ und, für die Zeichnungen, des Milestones 10 (Aufgabe 14 des Plans) und, für die Unterschriften (Punkte 25 bis 32), des Milestones 12 in `Docs/roadmap.json` (danach `dotnet run tools/roadmap.cs -- generate`). Als `evidence` je Viewer: Datum, Programm und Version, Betriebssystem, für jede Datei „wie erwartet“ oder die Abweichung (welche Datei, welcher Punkt der Prüfliste, was zu sehen war). Ein Kriterium wird erst abgehakt, wenn Chrome und Acrobat Reader geprüft sind; die Acrobat-Aussage zur echt signierten Datei gehört nur dann dazu, wenn sie geprüft wurde, sonst bleibt dieser Teil ausdrücklich offen. Abweichungen sind Befunde für einen Fix, kein Anlass, die Erwartungen dieser Liste zu ändern.

## Formulare im Fremdviewer prüfen

Handprüfung des Projektinhabers zu Milestone 11: Was Pagewing in Formularfelder schreibt (`/V` und ein von PDFiums Formular-Engine geschriebenes Erscheinungsbild `/AP`, inkrementell gespeichert), muss in anderen Programmen in jedem Feld stehen und das Feld weiter ausfüllbar lassen. Der Round-Trip in PDFium ist automatisch getestet (`FormRoundTripTests`, `InteropSampleTests`); wie Chrome und Acrobat Reader die Dateien zeigen, prüft nur ein Mensch. Die Prüfung läuft nie in der CI. Die Wegwerf-Muster des Spikes (Aufgabe 1 des Plans) waren nicht eingecheckt und sind durch die folgenden ersetzt.

### Muster erzeugen

`tests/Pagewing.Core.Tests/Annotations/InteropSampleTests.Forms.cs` füllt Formulare aus `testdata/pdfs` über die echte Pipeline der App (`AnnotationEditor.SetFormValueAsync` über eine `ViewerSession`, Speichern über `DocumentSaver` mit Prüfung vor dem Ersetzen) und legt je Quelle das unveränderte Original `<quelle>.pdf` und die gefüllte Fassung `form-*.pdf` in einen Ordner, dazu `README-forms.txt` (welches Feld welchen Wert trägt). Der Test prüft dabei selbst: Originalbytes unverändert am Anfang, jeder Wert nach dem Neuöffnen wieder gelesen und das Feld nicht gesperrt; bei `form-umlauts.pdf`, dass ein Text mit deutschen Anführungen und Gedankenstrich als Ganzes abgelehnt wird. Ohne die Umgebungsvariable meldet er sich als übersprungen (`FormSamplesKeepTheOriginalAsPrefix` läuft dagegen immer und prüft dieselbe Präfix-Eigenschaft in einem Temp-Ordner); er schreibt nur in den angegebenen Ordner, nie in `testdata/`.

```powershell
$env:PAGEWING_INTEROP_OUT = "$env:TEMP\pagewing-interop"
dotnet test tests/Pagewing.Core.Tests --filter-class "*InteropSampleTests"
Remove-Item Env:PAGEWING_INTEROP_OUT
```

```sh
PAGEWING_INTEROP_OUT=/tmp/pagewing-interop dotnet test tests/Pagewing.Core.Tests --filter-class "*InteropSampleTests"
```

Der Befehl schreibt auch die Muster der Annotationen (oben); nur die der Formulare: `--filter-method "*WritesFormSamplesForOtherViewers"`.

Die Muster: `form-text.pdf` (aus `forms-basic.pdf`: einzeilig, mehrzeilig, Kammfeld, Höchstlänge, Felder auf Seite 2, ein Feld mit zwei Widgets), `form-choices.pdf` (Checkbox, zwei Radiogruppen, Combo, editierbare Combo, Liste, Liste mit Mehrfachauswahl), `form-rotated.pdf` (aus `forms-rotated.pdf`: `/Rotate 90` mit versetzter CropBox und `/Rotate 270`), `form-umlauts.pdf` („Grüße aus Köln, 5 €“ und weitere Zeichen), `form-with-annotations.pdf` (Hervorhebung und Formularwerte in **einem** Speichervorgang) und `form-signed.pdf` (aus `forms-signed.pdf`, einer Strukturattrappe ohne Kryptografie; nur das Textfeld `Name` wird gefüllt). Die Werte sind bei jedem Lauf gleich; die Bytes der Dateien sind es nicht (`/ID` und `/NM` sind je Lauf zufällig).

### Prüfliste je Viewer

Pflicht unter Windows: **Chrome** (eingebauter PDF-Viewer) und **Adobe Acrobat Reader**; dazu für jede Datei **Acrobats eigenes Speichern** (Datei → Speichern unter, andere Datei) und die danach in Pagewing geöffnete Kopie. Optional: Edge, Firefox (pdf.js), unter Linux Okular und Evince. Original und gefüllte Fassung nebeneinander öffnen, `README-forms.txt` daneben:

1. **Werte sichtbar** (`form-text`): Jedes Feld zeigt den Wert aus `README-forms.txt`; `Name` steht auf Seite 1 **und** im zweiten Widget auf Seite 2; das mehrzeilige Feld hat zwei Zeilen; das Kammfeld hat in jeder der 8 Zellen ein Zeichen; nichts ist leer, abgeschnitten oder verrutscht.
2. **Felder weiter ausfüllbar:** In jedem Viewer lässt sich in ein gefülltes Textfeld klicken und tippen (der Wert ändert sich), eine Checkbox umschalten, eine Radiogruppe und eine Auswahl ändern. Kein Feld ist plötzlich schreibgeschützt oder leer.
3. **Auswahlfelder und Radiogruppen über zwei Seiten** (`form-choices`): `Agb` ist angehakt, `Zustimmung` nicht; in der Gruppe `Zahlung` ist **nur** der Knopf „Rechnung“ auf Seite 2 gewählt (die Knöpfe auf Seite 1 sind leer); `Stufe` zeigt „2“; die Combo `Farbe` zeigt „Gruen“, die editierbare Combo `Frei` den Text „Eigener Wert“; die Liste `Liste` hat „Gamma“ gewählt, die Mehrfachliste `Mehrfach` **beide** Einträge „Alpha“ und „Gamma“.
4. **Gedrehte Seite und CropBox** (`form-rotated`): Auf Seite 1 (`/Rotate 90`, CropBox versetzt) und Seite 2 (`/Rotate 270`) sitzen Text, Häkchen und Combo-Wert (`Seite eins`/`Seite zwei`, `Check1`/`Check2` angehakt, `Combo1` zweiter Eintrag, `Combo2` dritter Eintrag) in ihren Feldern, nicht gespiegelt und nicht am Rand der Seite. Notieren, in welche Richtung der Text liegt. Pagewing selbst legt sein Eingabeelement mit aufrechtem Text auf das Feld einer gedrehten Seite (bekannte, hingenommene Grenze, `Docs/DECISIONS.md`, Eintrag „Formulare: Bedienung auf der Seite (M11)“).
5. **Umlaute und „€“** (`form-umlauts`): „Grüße aus Köln, 5 €“, die zweite Zeile „Straße 5 € ½ « » µ“ (nach „Äpfel, Öl und Übermut“) und „München, 5 €“ stehen vollständig da, ohne „?“, Kästchen oder fehlende Zeichen, in Chrome und Acrobat. Ändert man den Text in Acrobat, ist das Erscheinungsbild danach Acrobats eigenes (erwartet).
6. **Annotation und Formularwerte in einer Datei** (`form-with-annotations`): Die gelbe Hervorhebung über „Name“ (Seite 1) liegt hinter dem Text und trägt den Kommentar „Interop: highlight next to form values“ (Acrobat: Kommentarbereich); `Name`, `Agb` und `Ort` tragen ihre Werte.
7. **Signierte Attrappe** (`form-signed`, Acrobat, Unterschriftenbereich): Die Datei ist eine Strukturattrappe ohne gültige Signatur (DocMDP-Stufe 2); Acrobat meldet sie im Original und in der gefüllten Kopie als ungültig bzw. nicht prüfbar, und das ist erwartet. Festhalten, was Acrobat zu beiden sagt und ob es sich unterscheidet; das Feld `Name` zeigt seinen Wert, die Signaturfelder und die Schaltfläche „Senden“ sind unverändert. Pagewing selbst behauptet nie, eine Signatur sei gültig. Aussagekräftig ist nur eine **echt signierte** Datei, die der Projektinhaber selbst signiert und nie eincheckt (Ablauf wie in „Annotationen im Fremdviewer prüfen“, Punkt 7; hier „Zertifizieren“ mit erlaubtem Ausfüllen von Formularfeldern für Stufe 2): in Pagewing ein Feld füllen, mit Strg+S speichern (die Warnkarte sagt bei Stufe 2 und nur Formularänderungen, dass Formularfelder nach dieser Signatur erlaubt sind und Pagewing das nicht prüfen kann; trotzdem speichern) und in Acrobat öffnen. **Offener Befund zum Notieren:** Pagewing hängt beim Speichern alle geladenen Widgets des Dokuments an, auch die nicht angefassten (`Docs/DECISIONS.md`, Eintrag „Formulare: Speichern, Pflichtfelder und was PDFium beim Speichern anhängt (M11)“); festhalten, ob Acrobat die Signatur danach dennoch als erlaubt geändert oder als ungültig meldet.
8. **Acrobats eigenes Speichern:** Jede gefüllte Datei in Acrobat öffnen, unter anderem Namen speichern und die Kopie in Pagewing öffnen: Alle Werte stehen noch da und die Felder lassen sich in Pagewing weiter ausfüllen (Acrobat zeichnet Erscheinungsbilder teils selbst, die Schrift darf abweichen).
9. **Ohne Reparatur:** Keine Datei löst eine Meldung „beschädigt“ oder „repariert“ aus, und Acrobat fragt beim Schließen einer unveränderten Datei nicht nach dem Speichern.
10. **Druck:** In der Druckvorschau (Acrobat „Drucken“, Chrome Strg+P) erscheinen die Werte. Optional in Pagewing selbst: Strg+P → „Microsoft Print to PDF“ (Windows) druckt die Werte auch **ungespeichert**, ohne Hervorhebungsfarbe der Felder; ein echter Drucker ist nicht geprüft.
11. **Pagewing selbst:** Dieselben Dateien zeigen in Pagewing ihre Werte (auf der hellen und der dunklen Seite; die Felder bleiben hell und lesbar), „Formular ausfüllen“ (Strg+Umschalt+F) lässt sich bedienen.

### Ergebnis festhalten

Das Ergebnis ist der Nachweis des Abnahmekriteriums von Milestone 11 „Ausgefüllte Test-Formulare zeigen die Werte in einem anderen Viewer (Test + manuelle Prüfung)“ und der Handprüfung im Release `v0.2.0` in `Docs/roadmap.json` (danach `dotnet run tools/roadmap.cs -- generate`). Als `evidence` je Viewer: Datum, Programm und Version, Betriebssystem, für jede Datei „wie erwartet“ oder die Abweichung (welche Datei, welcher Punkt der Prüfliste, was zu sehen war). Ein Kriterium wird erst abgehakt, wenn Chrome und Acrobat Reader geprüft sind; die Acrobat-Aussage zur echt signierten Datei gehört nur dann dazu, wenn sie geprüft wurde, sonst bleibt dieser Teil ausdrücklich offen. Abweichungen sind Befunde für einen Fix, kein Anlass, die Erwartungen dieser Liste zu ändern.
