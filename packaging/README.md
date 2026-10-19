# Paketierung

Alles Gemeinsame für die Verteilung von Pagewing (Milestone 8). Das Werkzeug ist `tools/package.cs`; es läuft aus dem Repo-Root und braucht kein Netzwerk, außer `inno-install` (ein Download mit fester SHA-256).

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
| `Pagewing-<Version>-linux-x64-portable.zip` | portable ZIP für Linux |
| `Pagewing-<Version>-win-x64-setup.exe` | Installer für Windows |
| `SHA256SUMS-<Version>.txt` | Zeilen `<sha256>  <Dateiname>` |

`.deb` und AppImage kommen mit den folgenden Aufgaben des Milestones hinzu.

## Befehle

```sh
dotnet run tools/package.cs -- layout --rid linux-x64 --app <publish-app> --cli <publish-cli> --out <layout>
dotnet run tools/package.cs -- zip --layout <layout> --version 1.0.0 --out Pagewing-1.0.0-linux-x64-portable.zip
dotnet run tools/package.cs -- sums Pagewing-1.0.0-linux-x64-portable.zip --out SHA256SUMS-1.0.0.txt
dotnet run tools/package.cs -- check-binary <Datei>...   # GLIBC- und NEEDED-Prüfung
dotnet run tools/package.cs -- selftest
```

Die ZIP-Datei ist reproduzierbar: sortierte Einträge, Zeitstempel 2000-01-01, feste Rechte (0755 für Programme, sonst 0644), oberster Ordner `Pagewing-<Version>/`. Zweimal aus demselben Layout erzeugt ergibt sie dieselbe SHA-256-Summe.

## Verzeichnisse

- `linux/pagewing.desktop`: Vorlage der `.desktop`-Datei. Sie darf sich von `DesktopEntry.Render` (`Pagewing.Core.Distribution`) nur in der Zeile `Exec` unterscheiden (`DesktopEntryTests.RenderIsTheSameForTheTemplateInPackaging`). `Exec` ist der Pfad des `.deb` (`/usr/lib/pagewing/Pagewing %F`). `StartupWMClass=Pagewing` ist die Fensterklasse, die Avalonia unter X11 setzt (belegt mit `xprop WM_CLASS` an der Native-AOT-App unter Xvfb: `"Pagewing", "Pagewing"`).
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
