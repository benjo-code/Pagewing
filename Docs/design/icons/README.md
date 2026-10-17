# Pagewing Icon-Paket

Geliefert am 2026-10-17 (Entscheidung: `Docs/DECISIONS.md`, Eintrag „App-Icon: neues Icon „Seite mit Welle“ löst den Entwurf „Flügelecke“ ab“). Hier liegt, was die App selbst nicht braucht; die App nutzt zwei Dateien aus ihrem Ordner `src/Pagewing.App/Assets/`.

## Inhalt

- `source/pagewing-1024.png` – normierter Master (Motiv zentriert, ca. 5 % Rand). Aus ihm sind alle anderen Größen entstanden.
- `linux/hicolor/<N>x<N>/apps/pagewing.png` – 16, 22, 24, 32, 48, 64, 96, 128, 256, 512 px, für die Linux-Pakete (M8).
- `linux/pagewing.desktop` – Vorlage für den Starter (M8, siehe „Linux“).
- Windows: `src/Pagewing.App/Assets/pagewing.ico` (16, 20, 24, 32, 40, 48, 64, 96, 128, 256 px, 32 bit; bis 128 px als BMP, 256 px als PNG).
- Fenster-Icon unter Linux: `src/Pagewing.App/Assets/pagewing-128.png` (Kopie von `linux/hicolor/128x128`).

## Eingebunden

- Windows: `ApplicationIcon` in `src/Pagewing.App/Pagewing.App.csproj`. Avalonia nimmt es für Fenster und Taskleiste aus der exe.
- Linux: `Window.Icon` aus `pagewing-128.png`, gesetzt nach dem ersten Frame (`App.LoadWindowIcon`).

## Linux (M8)

Systemweit:

```sh
cp -r linux/hicolor/* /usr/share/icons/hicolor/
install -Dm644 linux/pagewing.desktop /usr/share/applications/pagewing.desktop
gtk-update-icon-cache -f /usr/share/icons/hicolor
```

Pro Benutzer: dieselben Pfade unter `~/.local/share/`.

Vor dem Einsatz in der `.desktop`-Vorlage prüfen: `Exec` muss zum Dateinamen des Binaries passen (die App heißt `Pagewing`), `StartupWMClass` zur Fensterklasse der App (sonst zeigt das Dock ein generisches Icon), und `Comment[en]` fehlt. Für Flatpak müssen Icon- und Dateiname der App-ID entsprechen (z. B. `io.github.benjo_code.Pagewing.png`).
