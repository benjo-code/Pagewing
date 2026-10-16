# Pagewing – Designsystem

**Stand:** 2026-10-17 · **Status:** abgestimmt (Milestone 2, Wahl des Projektinhabers am 2026-10-17) · **Gesamt-Spec:** [2026-10-16-pagewing-design.md](2026-10-16-pagewing-design.md) · **Roadmap:** [Milestone 2](../../ROADMAP.md) · **Plan:** [2026-10-16-milestone-02-designrichtungen.md](../plans/2026-10-16-milestone-02-designrichtungen.md)

## 1. Ziel

Diese Spec legt fest, wie Pagewing aussieht und sich verhält: Tokens (Farben hell und dunkel, Typografie, Abstände, Radien, Icons), Fenster-Layout, Verhalten der wichtigsten Bereiche, Schriftzug und Icon-Richtung. Sie ist die Vorlage für die App-Shell in M3 und für alle späteren UI-Arbeiten. Grundlage ist die vom Projektinhaber gewählte Designrichtung (Abschnitt 4).

## 2. Rahmenbedingungen

Geprüft am 2026-10-16 und 2026-10-17 gegen Avalonia 12.1.4 (Quellcode des Tags `12.1.4`, im Folgenden **SRC** = `https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/`), die Avalonia-Doku und die Repos der Icon-Sätze und Schriften. „Nicht belegt“ heißt: Es gibt keine Quelle, und es wird in M3 gemessen oder ausprobiert.

### 2.1 Vorgaben aus der Gesamt-Spec

- Prioritäten: 1. Geschwindigkeit (Start, Öffnen, Scrollen), 2. gutes, modernes Design, 3. Funktionsumfang. Das Design darf den Startpfad nicht belasten: Bis zum ersten Frame wird nur geladen, was das Fenster braucht.
- Windows 10/11 und Linux (X11/XWayland; Wayland nur experimentell). Native AOT, kompiliertes XAML.
- UI-Texte Englisch und Deutsch (resx). Komplett offline, keine Konten.
- Kontrast nach WCAG 2.2 AA in beiden Themes: Text ≥ 4,5:1, Icons, Fokusrahmen und Ränder von Bedienelementen ≥ 3:1. Zustände nie nur über Farbe.

### 2.2 Eigene Titelleiste (Tabs in der Titelleiste)

- Avalonia 12 ersetzt `ExtendClientAreaChromeHints`, `TitleBar` und `CaptionButtons` durch `WindowDecorations` (`None`/`BorderOnly`/`Full`) mit `ExtendClientAreaToDecorationsHint`; `ExtendClientAreaTitleBarHeightHint` bleibt. Quelle: https://docs.avaloniaui.net/docs/avalonia12-breaking-changes, SRC `src/Avalonia.Controls/Window.cs`.
- Bereiche der eigenen Titelleiste werden über `WindowDecorationProperties.ElementRole` markiert (`TitleBar`, `User`, `MaximizeButton`, `CloseButton` …). Mit `TitleBar` übernimmt das System Ziehen und Doppelklick. Quellen: SRC `src/Avalonia.Base/Input/WindowDecorationsElementRole.cs`, https://github.com/AvaloniaUI/Avalonia/pull/21243, https://docs.avaloniaui.net/docs/how-to/window-how-to.
- **Windows:** unterstützt. `MaximizeButton` wird zu `HTMAXBUTTON` (SRC `src/Windows/Avalonia.Win32/WindowImpl.CustomCaptionProc.cs`), das ist laut Microsoft die Voraussetzung für Snap Layouts (https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/ui/apply-snap-layout-menu). Dass Snap Layouts damit in Avalonia 12.1 funktionieren, ist nicht belegt. Empfohlen ist `WindowDecorations="Full"` mit Hint; bei `BorderOnly` fehlen unter Windows 11 die Minimieren-/Maximieren-Animationen (https://github.com/AvaloniaUI/Avalonia/issues/21212, https://github.com/AvaloniaUI/Avalonia/issues/22053).
- **Linux X11:** nur mit `X11PlatformOptions.EnableDrawnDecorations`, markiert als `[Experimental("AVALONIA_X11_CSD")]`; ohne die Option bleibt die Titelleiste des Fenstermanagers (SRC `src/Avalonia.X11/X11Window.cs`, https://docs.avaloniaui.net/docs/platform-specific-guides/linux).
- **Folge für alle Richtungen:** Unter Linux bleibt die Titelleiste des Systems, die Tabs stehen in einer eigenen Zeile darunter. Unter Windows dürfen die Tabs in der Titelleiste stehen. Jede Richtung zeigt beide Varianten bzw. beschreibt die Linux-Variante.

### 2.3 Fenster-Transparenz (Mica, Acrylic)

- Windows: `AcrylicBlur` ab Build 15063, `Mica` ab Build 22000 (nur Windows 11), beides nur mit WinUI-Komposition; `Blur` gibt es unter Windows nicht. X11: `Transparent` nur mit Compositor, `Blur` nur mit KWin, nie Acrylic oder Mica. Quellen: SRC `src/Windows/Avalonia.Win32/WinRT/Composition/WinUiCompositedWindowSurface.cs`, SRC `src/Avalonia.X11/TransparencyHelper.cs`.
- Windows schaltet die Effekte im Energiesparmodus ggf. ab (https://docs.avaloniaui.net/docs/how-to/window-how-to). Kosten für Start und Bildrate: nicht belegt.
- **Folge:** Kein Design verlässt sich auf Transparenz. Mica ist höchstens eine Option unter Windows 11 mit deckender Fläche als Fallback, und nur, wenn die Messung in M3 die Budgets einhält.

### 2.4 Anpassung des Fluent-Themes

- `FluentTheme` lässt sich je `ThemeVariant` (nur Light und Dark) über `ColorPaletteResources` anpassen: `Accent`, `RegionColor`, `AltHigh` … `AltMediumLow`, `BaseHigh` … `BaseMediumLow`, `ChromeAltLow`, `ChromeBlackHigh` … `ChromeWhite`, `ErrorText`, `ListLow`, `ListMedium`. Nur `Accent` ist zur Laufzeit änderbar; ohne eigenen `Accent` gilt die Akzentfarbe des Systems. Quellen: SRC `src/Avalonia.Themes.Fluent/ColorPaletteResources.Properties.cs`, https://docs.avaloniaui.net/docs/styling/themes.
- `DensityStyle` = `Normal` oder `Compact`. Eckenradien `ControlCornerRadius` (3) und `OverlayCornerRadius` (5) sind per `DynamicResource` gebunden und überschreibbar. Quelle: SRC `src/Avalonia.Themes.Fluent/FluentTheme.xaml.cs`, `Accents/BaseResources.xaml`.
- **Folge:** Die Tokens werden in M3 auf die Fluent-Palette und eigene Ressourcen abgebildet. Jedes Farb-Token nennt die Fluent-Rolle, soweit es eine gibt (Abschnitt 5).

### 2.5 Schriften

- Ohne Vorgabe nimmt Avalonia die Standardschrift von Skia: unter Windows die Nachrichtenschrift des Systems (in der Regel Segoe UI), unter Linux das Ergebnis von fontconfig, je nach Distribution (SRC `src/Skia/Avalonia.Skia/FontManagerImpl.cs`, https://github.com/google/skia/blob/main/src/ports/SkFontMgr_win_dw.cpp). Segoe UI Variable gibt es erst ab Windows 11 (https://learn.microsoft.com/en-us/typography/fonts/windows_11_font_list).
- Eine eigene Schrift sieht auf beiden Systemen gleich aus. `Avalonia.Fonts.Inter` 12.1.4 (Paket MIT, Schrift OFL-1.1) bettet sechs TTF mit zusammen etwa 1,88 MB ein, ohne OFL-Datei; `.WithInterFont()` registriert die Schrift nur und macht sie nicht zur Standardschrift (SRC `src/Avalonia.Fonts.Inter/AppBuilderExtension.cs`). Eine eigene Schrift kann ebenso als AvaloniaResource eingebettet werden. Startkosten eingebetteter Schriften: nicht belegt, Messung in M3.
- Geprüfte Kandidaten (alle OFL-1.1, decken äöüÄÖÜßẞ„“‚‘–—…€ ab; Größe der Variable-TTF aus google/fonts):

| Schrift | Version | Variable TTF | Reserved Font Name |
|---|---|---|---|
| Inter | 4.1 (2024-11-16) | 880 KB | keiner |
| Geist / Geist Mono | 1.8.1 (2026-10-16) | 169 KB | keiner |
| Atkinson Hyperlegible Next | 2.001 (Repo archiviert 2025-02-21) | 115 KB | keiner |
| IBM Plex Sans | 3.201 (2024-11-13) | 537 KB | „Plex“ |
| Source Sans 3 | 3.052R (2023-04-04) | 646 KB | „Source“ |
| Literata | 3.103 (2023-05-19) | 955 KB | keiner |
| Source Serif 4 | 4.005R (2023-01-20) | 1,2 MB | keiner (google/fonts-Fassung) |
| Fraunces | 1.000 (2020-10-29) | 360 KB | keiner |

- Lizenzfolge: Die FSF führt die OFL als freie Schriftlizenz (https://www.gnu.org/licenses/license-list.html#SILOFL); als eigene Datei neben dem Programm ist sie ein Aggregat im Sinne der GPL-FAQ (https://www.gnu.org/licenses/gpl-faq.html#MereAggregation). Die Schrift wird als eigene Datei mit `OFL.txt` ausgeliefert. Eine Teilmenge (Subsetting) ist eine geänderte Fassung; bei Schriften mit Reserved Font Name wäre dann eine Umbenennung nötig (OFL-FAQ 2.2, 5.9). Ein Schriftzug aus Glyphen-Pfaden ist erlaubt und wird nicht OFL-pflichtig (OFL-FAQ 1.1, 1.1.1, 1.13, https://openfontlicense.org/ofl-faq/).

### 2.6 Icons

- In M3 werden nur die genutzten Icons als Pfad-Geometrien übernommen (keine Icon-Schrift, kein Paket): AOT-freundlich, kein Ladeaufwand beim Start. Die Lizenz kommt zu den Drittlizenzen (M8).

| Satz | Lizenz | Version | Stil | Raster |
|---|---|---|---|---|
| Fluent UI System Icons | MIT | `@fluentui/svg-icons` 1.1.344 (2026-10-09) | Flächen, Regular/Filled | 16/20/24 (je Icon unterschiedlich) |
| Lucide | ISC, aus Feather übernommene Icons MIT (Cole Bemis) | 1.58.0 (2026-10-16) | Strich 2 px | 24 |
| Phosphor | MIT | `@phosphor-icons/core` 2.1.1 (2024-03-29) | 6 Gewichte (thin bis fill) | 256er-viewBox |
| Tabler | MIT | 3.49.0 (2026-10-05) | Strich 2 px, Strichstärke frei | 24 |

- Für die Szene fehlt in keinem Satz etwas Wesentliches. Lücken: Kein Satz hat ein eigenes Schwärzen-Icon (Behelf: Radierer bzw. `square-off`), Lucide und Phosphor haben kein echtes „Seitenbreite“-Icon. Lucide verlangt zwei Lizenztexte (ISC und MIT für die Feather-Icons, darunter `search`, `x`, `zoom-in`, `chevron-*`). Quellen: LICENSE-Dateien in https://github.com/microsoft/fluentui-system-icons, https://github.com/lucide-icons/lucide, https://github.com/phosphor-icons/core, https://github.com/tabler/tabler-icons.

### 2.7 Barrierefreiheit und Systemeinstellungen

- Windows liefert über `PlatformColorValues` Theme-Variante, `ContrastPreference` (Hochkontrast) und Akzentfarbe, mit Änderungsereignis (SRC `src/Windows/Avalonia.Win32/Win32PlatformSettings.cs`). Linux liefert Theme-Variante und Akzentfarbe über das XDG-Portal, keinen Kontrastwunsch (SRC `src/Avalonia.FreeDesktop/DBusPlatformSettings.cs`).
- Das Fluent-Theme wertet Hochkontrast nicht aus; Pagewing muss es selbst umsetzen. Eine API für „Animationen reduzieren“ gibt es in Avalonia 12.1 nicht.
- **Folge:** Animationen sind kurz und verzichtbar; eine Einstellung „Animationen reduzieren“ in Pagewing ersetzt die fehlende Systemabfrage (M6). Hochkontrast ist ein offener Punkt (Abschnitt 9).

## 3. Richtungen

Drei Richtungen, veröffentlicht am 2026-10-17 als Artifacts (privat, nur für den Projektinhaber sichtbar). Die HTML-Quellen liegen unter [`Docs/design/mockups/`](../../design/mockups/); sie lassen sich lokal im Browser öffnen.

Alle drei zeigen dieselbe Szene, damit sie vergleichbar sind: zwei Tabs (einer mit langem Namen, in der Mitte gekürzt), Werkzeugleiste mit Seitenfeld, Zoom und Seitenbreite, Suche mit Treffer 2 von 5, Seitenleiste mit Vorschaubildern und Reiter „Inhalt“, Seite 3 eines selbst geschriebenen Handbuchs und den Anfang von Seite 4, einen Platzhalter für eine noch nicht gerenderte Seite und einen Moduswechsel für die Werkzeuge ab v0.2 und v0.3. Umschaltbar sind hell, dunkel und System sowie die Ansichten „Dokument offen“, „Startbildschirm“ und „Linux-Titelleiste“. Jede Seite listet ihre Tokens (gleiche Namen in allen drei), Schriftgrößen, Abstände, Radien, die Icons und die Umsetzung in Avalonia.

| Richtung | Artifact | Quelle | Leitidee |
|---|---|---|---|
| A „Nativ & leise“ | https://claude.ai/artifact/He1E7CwqM3d8eQTxC2pnPs | [richtung-a-nativ.html](../../design/mockups/richtung-a-nativ.html) | Sieht aus wie ein Teil des Systems (Fluent, GNOME): neutrale Flächen, eine Akzentfarbe, klassische Werkzeugleiste, Systemschrift, Fluent UI System Icons. Geringstes Risiko, schnellster Start, wenig Wiedererkennung. |
| B „Werkbank“ | https://claude.ai/artifact/Qz1iJbLUijWbUH21WXvq6y | [richtung-b-werkbank.html](../../design/mockups/richtung-b-werkbank.html) | Erste Fassung (Artifact-Version 1): aufgebaut wie ein Code-Editor, mit Aktivitätsleiste, Panel, Modus-Reitern, Befehlspalette in der Titelleiste und Statusleiste mit Dokumentdaten. Dicht, tastaturfreundlich, dunkel zuerst, Akzent Textmarker-Gelb; Geist und Geist Mono, Tabler Icons. Am 2026-10-17 nach Rückmeldung des Projektinhabers überarbeitet (Version 2, siehe Abschnitt 4); die Quelle im Repo ist Version 2. |
| C „Papier“ | https://claude.ai/artifact/GqTxJnQPUbhrRoX9bjQ1ae | [richtung-c-papier.html](../../design/mockups/richtung-c-papier.html) | Ruhiger Leseraum: Fenster und Dokumentbereich im selben Papierton, schwebende Werkzeugleiste unten, Seitenübersicht als Panel bei Bedarf, Tintenblau als Akzent; Atkinson Hyperlegible Next, Literata für den Schriftzug, Phosphor Icons (Light). |

**Prüfung (2026-10-17):**
- Kontrast nach WCAG 2.2 AA für alle drei Token-Sätze in beiden Themes: 56 Paare je Richtung (Text auf allen Flächen ≥ 4,5:1, Akzent als Text ≥ 4,5:1, Ränder und Fokus ≥ 3:1), alle bestanden. Knappstes Paar je Richtung: `border.strong` auf `surface.window` (hell) mit 3,07 (A), 3,20 (B) und 3,34 (C).
- Sichtprüfung je Richtung in hell und dunkel bei 1440 px sowie bei 375 px Viewport-Breite: kein horizontales Scrollen (Inhaltsbreite 360 px bei 375 px), lange deutsche Beschriftungen („Ausfüllen & unterschreiben“, langer Dateiname) passen oder sind sauber gekürzt, die weiße Seite hat im dunklen Theme eine sichtbare Kante, aktive Seite, aktiver Tab und aktueller Suchtreffer sind nicht nur über Farbe erkennbar (Rahmen, Nummernmarke bzw. Unterstreichung, Form).
- Gefunden und behoben: In B passte die Befehlspalette bei 1280 px nicht mehr in die Kontextleiste; sie sitzt jetzt in der Titelleiste (unter Linux am Ende der Tab-Zeile).
- B Version 2 (2026-10-17) erneut geprüft: Kontrast der neuen Tokens bestanden (56 Paare, knappstes `border.strong` auf `surface.raised`, dunkel, 3,18), Sichtprüfung aller Ansichten (Dokument offen, Suche, Kommentieren, Startbildschirm, Linux-Titelleiste) hell und dunkel, in beiden Dichten und bei 375 px ohne horizontales Scrollen.

## 4. Entscheidung

Der Projektinhaber hat am 2026-10-17 **Richtung B „Werkbank“ in der überarbeiteten Fassung** gewählt (Artifact-Version 2, Quelle [richtung-b-werkbank.html](../../design/mockups/richtung-b-werkbank.html)). Begründung, Wortlaut der Wahl und verworfene Richtungen stehen in [`Docs/DECISIONS.md`](../../DECISIONS.md), Eintrag „Designrichtung: B „Werkbank“ (überarbeitete Fassung)“.

Änderungen gegenüber der ersten Fassung von B, alle im Mockup umgesetzt:

- Akzent gedecktes Indigo statt Textmarker-Gelb; Gelb nur für Suchtreffer und Markierungen.
- Befehlspalette als kompakter Knopf „Suchen oder Befehl · Strg K“ in der Titelleiste, kein Eingabefeld.
- Keine Modus-Reiter in v0.1; Bearbeitungsmodi ab v0.2 öffnen eine eigene Werkzeugleiste mit „Fertig“ (Ansicht „Kommentieren (ab v0.2)“ im Mockup).
- Panel nur bei Bedarf: Vorschaubilder nach dem Öffnen, Trefferliste beim Suchen.
- Statusleiste mit Seite, Zoom, Änderungsstatus (ab v0.2) und „Dokumentinfos“.
- Weniger Rahmen, mehr Luft um die Seite; Dichte „Normal“ (Standard) und „Kompakt“.
- Tabler Icons mit 2 px statt 1,75 px Strich.

Die Abschnitte 5 bis 8 beschreiben diese Fassung verbindlich. Wo Mockup und Spec abweichen, gilt die Spec.

## 5. Tokens

Die Werte stammen aus dem gewählten Mockup (B, Artifact-Version 3). Token-Namen stehen in Punkt-Notation und beschreiben die Rolle, nicht die Farbe. In M3 werden sie zu Avalonia-Ressourcen: aus `color.surface.window` werden `Pw.Color.Surface.Window` (`Color`) und `Pw.Brush.Surface.Window` (`SolidColorBrush`), hell und dunkel getrennt über `ThemeDictionaries` (`Light`/`Dark`). Eine Token-Änderung ist eine Entscheidung in `Docs/DECISIONS.md`.

### 5.1 Farben

| Token | Hell | Dunkel | Verwendung | Fluent-Rolle (Vorschlag) |
|---|---|---|---|---|
| `color.surface.window` | `#EEEFF2` | `#15171B` | Titelleiste, Aktionsleiste, Statusleiste, Startbildschirm-Rahmen | `RegionColor` |
| `color.surface.toolbar` | `#F7F8FA` | `#1B1E23` | Werkzeugleiste eines Bearbeitungsmodus | – |
| `color.surface.sidebar` | `#F7F8FA` | `#1B1E23` | Panel (Seiten, Inhalt, Suche) | – |
| `color.surface.canvas` | `#E3E5EA` | `#0F1114` | Dokumentbereich hinter den Seiten, aktiver Tab | – |
| `color.surface.raised` | `#FFFFFF` | `#24272E` | Eingabefelder, Karten, Menüs, Befehlspalette | – |
| `color.surface.hover` | `#E3E5EA` | `#2A2E36` | Hover, Befehlsknopf | – |
| `color.accent.subtle` | `#E2E5F8` | `#262B52` | Ausgewählt: aktive Seite, aktueller Treffer, aktiver Eintrag der Aktionsleiste, aktives Werkzeug | – |
| `color.border.subtle` | `#DCDFE5` | `#2A2E36` | Trennlinien, sparsam (siehe 5.3) | – |
| `color.border.strong` | `#7F848E` | `#6E7480` | Ränder von Eingabefeldern und sekundären Knöpfen | – |
| `color.text.primary` | `#16181D` | `#E6E8EB` | Text, Icons in Werkzeugleisten | `BaseHigh` |
| `color.text.secondary` | `#4F545E` | `#9BA1AC` | Beschriftungen, Statuswerte, Icons der Aktionsleiste | `BaseMedium` |
| `color.text.disabled` | `#9399A3` | `#5E636D` | Deaktiviert (vom Kontrast ausgenommen) | – |
| `color.accent.default` | `#3B48C4` | `#9DA7FF` | Indigo: aktive Elemente, Primärknopf, Links | `Accent` |
| `color.accent.on` | `#FFFFFF` | `#121539` | Text und Icons auf `accent.default` | – |
| `color.focus.ring` | `#3340B4` | `#B4BCFF` | Tastaturfokus | – |
| `color.page.border` | `#00000014` | `#FFFFFF24` | Kante der PDF-Seite | – |
| `color.page.shadow` | `#0000001A` | `#00000099` | Schatten der PDF-Seite, von Karten und Popups | – |
| `color.search.hit` | `#FBE38A` | `#FBE38A` | Suchtreffer (auf der Seite und in der Trefferliste) | – |
| `color.search.current` | `#F5A524` | `#F5A524` | Aktueller Suchtreffer, immer mit Rahmen | – |
| `color.danger.default` | `#B42318` | `#FF8A80` | Fehlertext, Fehlersymbol, Rand eines fehlerhaften Felds | `ErrorText` |

Regeln:

- Der Akzent ist nie Gelb. Gelb (`search.*`) gehört nur Suchtreffern; Farben von Markierungen sind Inhalt des PDFs und keine Tokens.
- Pagewing nutzt seine eigene Akzentfarbe, nicht die des Systems (Wiedererkennung, geprüfter Kontrast). Deshalb wird `Accent` in der Fluent-Palette immer gesetzt.
- Die Spalte „Fluent-Rolle“ ist ein Vorschlag. Die übrigen Fluent-Farben (`Chrome*`, `Alt*`, `List*`) werden in M3 so gesetzt, dass Fluent-Controls (Textfeld, Knopf, Liste, Kontrollkästchen) zu den Tokens passen (Abschnitt 9).
- Kontrast nach WCAG 2.2 AA, geprüft am 2026-10-17 in beiden Themes: `text.primary` auf allen Flächen ≥ 4,5:1, `text.secondary` auf allen Flächen außer `surface.canvas` ≥ 4,5:1, `accent.default` als Text auf `surface.window`, `surface.toolbar`, `surface.sidebar` und `surface.raised` ≥ 4,5:1, `accent.on` auf `accent.default` ≥ 4,5:1, `danger.default` auf `surface.raised` ≥ 4,5:1, `border.strong` auf `surface.window`, `surface.toolbar` und `surface.raised` ≥ 3:1, `focus.ring` auf allen Flächen ≥ 3:1, `accent.default` auf `accent.subtle` ≥ 3:1. Knappstes Paar: `border.strong` auf `surface.raised` (dunkel) 3,18:1. Neue Paare, etwa `text.secondary` auf `surface.canvas`, brauchen vorher eine Prüfung.

### 5.2 Typografie

- **Schrift:** Geist (Oberfläche, Schnitte 400, 500, 600) und Geist Mono (Tastenkürzel, Seitenmarken in der Trefferliste; 400, 500). Beide OFL-1.1 ohne Reserved Font Name, daher ist eine Teilmenge erlaubt. Eingebettet wird eine Teilmenge (Basis-Latein, Latin-1, Latin Extended-A, typografische Satzzeichen „“‚‘–—…€ und ẞ) als AvaloniaResource, mit `OFL.txt` im Paket.
- **Fallback:** Windows Segoe UI (Mono: Cascadia Mono, Consolas), Linux die Standardschrift von fontconfig (`sans-serif`, Mono: `monospace`). Liegen die Startkosten der eingebetteten Schrift in M3 über dem Budget, nutzt Pagewing von Anfang an den Fallback. Eine Schrift wird nie erst nach dem ersten Frame gewechselt.
- **Ziffern:** Seitenzahlen, Zoom und Trefferzahlen mit Tabellenziffern (`tnum`).

| Rolle | Größe / Zeilenhöhe (px) | Gewicht | Verwendung |
|---|---|---|---|
| `type.title` | 22 / 28 | 600 | Überschrift des Startbildschirms |
| `type.subtitle` | 16 / 22 | 600 | Titel von Karten (Passwort, Fehler) und Dialogen |
| `type.strong` | 13 / 18 | 600 | Panel-Überschriften, aktiver Tab, Modusname |
| `type.body` | 13 / 18 (Kompakt 12.5 / 17) | 400 | Grundschrift |
| `type.caption` | 12 / 16 | 400 | Statusleiste, Zusammenfassungen, Hinweise |
| `type.mono` | 11.5 / 16 | 400 | Tastenkürzel (`kbd`), Seitenmarken |

### 5.3 Abstände, Radien, Ränder, Schatten

- **Abstände** (4-px-Raster): `space.0` 2, `space.1` 4, `space.2` 8, `space.3` 12, `space.4` 16, `space.5` 24, `space.6` 32.
- **Radien:** `radius.control` 6 (Knöpfe, Felder, Listeneinträge), `radius.overlay` 10 (Karten, Popups, Befehlspalette, obere linke Ecke von Panel bzw. Dokumentbereich), `radius.tab` 8 (obere Ecken eines Tabs), `radius.page` 0.
- **Ränder:** Flächen trennen sich über Farbe, nicht über Linien. `border.subtle` (1 px) nur dort, wo gleichfarbige Flächen aneinanderstoßen (Unterkante der Werkzeugleiste eines Modus, Linux-Tab-Zeile). Eingabefelder und sekundäre Knöpfe haben 1 px `border.strong`.
- **Fokus:** 2 px `focus.ring`, 1 px Abstand zum Element, nur bei Tastaturbedienung.
- **Schatten** (Avalonia `BoxShadow`): Seite `0 0 0 1 page.border, 0 2 10 0 page.shadow`; Karte und Popup `0 0 0 1 border.subtle, 0 12 32 -12 page.shadow`.

### 5.4 Dichte

„Normal“ ist Standard, „Kompakt“ eine Einstellung für Vielnutzer (M6, Abschnitt 9).

| Maß (px) | Normal | Kompakt |
|---|---|---|
| Titelleiste bzw. Linux-Tab-Zeile | 40 | 34 |
| Aktionsleiste: Breite / Knopf | 52 / 40 | 44 / 34 |
| Panel (Standardbreite) | 264 | 236 |
| Statusleiste | 32 | 26 |
| Werkzeugleiste eines Modus | 44 | 38 |
| Rand um die Seiten | 32 | 20 |
| Abstand zwischen Seiten | 20 | 20 |
| Grundschrift | 13 | 12.5 |
| Vorschaubilder je Zeile (Breite) | 1 (136) | 2 (Spaltenbreite) |

### 5.5 Icons

- **Satz:** Tabler Icons 3.49.0 (MIT), Variante „outline“, 24er-Raster, Strich 2 px, runde Enden und Ecken. Übernommen werden nur die genutzten Pfade; die Lizenz kommt zu den Drittlizenzen (M8).
- **Größen:** 20 px in der Aktionsleiste, 18 px in Titel- und Werkzeugleisten, 16 px in Panel, Tabs und Statusleiste.
- **Farbe:** `text.primary` in Werkzeugleisten, `text.secondary` in Aktions- und Statusleiste, `accent.default` für aktive Einträge, `danger.default` für Fehler.
- **Umsetzung:** Tabler-Icons sind Linien, `PathIcon` füllt nur. Pagewing braucht deshalb ein eigenes Icon-Control: `Path` mit `Stroke`, `StrokeThickness` 2, `StrokeLineCap`/`StrokeJoin` rund, in einer 24×24-`Viewbox`.
- **Belegung** (Tabler-Name): Dokument `file-text`, Datei öffnen `folder-open`, Suchen `search`, Seiten `layout-grid`, Inhalt `list-tree`, Einstellungen `settings`, Panel schließen `layout-sidebar-left-collapse`, Schließen `x`, Seite zurück/vor `chevron-left`/`chevron-right`, Menü `chevron-down`, Verkleinern/Vergrößern `minus`/`plus`, Seitenbreite `arrow-autofit-width`, Dokumentinfos `info-circle`, Passwort `lock`, Fehler `alert-triangle`; ab v0.2 Kommentieren `message`, Markieren `highlight`, Unterstreichen `underline`, Zeichnen `pencil`, Notiz `note`, Rückgängig `arrow-back-up`, Löschen `trash`, Fertig `check`.

### 5.6 Bewegung

- Hover- und Druckfarben wechseln ohne Animation. Panel und Popups öffnen mit 150 ms (ease-out), schließen mit 100 ms.
- Grobe Vorschau und scharfes Seitenbild werden ohne Überblenden getauscht; Geschwindigkeit geht vor Effekt.
- Bis zum ersten Frame läuft keine Animation.
- Avalonia 12.1 kennt keine Systemeinstellung „Animationen reduzieren“ (Abschnitt 2.7). Pagewing bekommt dafür eine eigene Einstellung (M6), die alle Dauern auf 0 setzt.

## 6. Fenster-Layout

Maße für die Dichte „Normal“, Kompakt siehe 5.4. Von oben nach unten:

1. **Titelleiste (Windows):** Eigene Titelleiste (`WindowDecorations="Full"` mit `ExtendClientAreaToDecorationsHint`). Von links: App-Symbol (so breit wie die Aktionsleiste), Tabs, Knopf „Datei öffnen“, Zieh-Fläche (`ElementRole="TitleBar"`), Knopf „Suchen oder Befehl · Strg K“, Fensterknöpfe. Tabs und Knöpfe haben `ElementRole="User"`.
2. **Titelleiste (Linux):** Die Titelleiste des Fenstermanagers bleibt. Darunter folgt eine Tab-Zeile in Titelleistenhöhe mit App-Symbol, Tabs, „Datei öffnen“, Zieh-Fläche und dem Befehlsknopf am Ende.
3. **Mitte:** Aktionsleiste (52) | Panel (264, optional, ziehbar von 200 bis 400) | Dokumentbereich. Der Dokumentbereich hat `surface.canvas`; das Panel bzw. ohne Panel der Dokumentbereich hat oben links `radius.overlay` gegenüber der Aktionsleiste.
4. **Werkzeugleiste eines Bearbeitungsmodus (ab v0.2):** 44 hoch über dem Dokumentbereich, `surface.toolbar`, Unterkante `border.subtle`. Von links: Modusname mit Icon (`accent.default`), Werkzeuge mit Icon und Beschriftung, Rückgängig und Löschen; rechts der Hinweis „Esc beendet“ und der Primärknopf „Fertig“.
5. **Statusleiste:** 32 hoch, `surface.window`, links bündig mit dem Dokumentbereich (um die Aktionsleiste eingerückt): ‹ „Seite“ [Feld] „/ 24“ ›, Trenner, Zoom − [Wert] + [Seitenbreite], ab v0.2 der Änderungsstatus („Nicht gespeichert“ mit Punkt). Rechts „Dokumentinfos“.

**Dokumentbereich:** Seiten zentriert untereinander, 20 Abstand, 32 Rand ringsum (auch unter der letzten Seite). Jede Seite hat Kante und Schatten (5.3).

**Mindestgröße:** 640 × 480.

**Schmales Fenster:**
- Unter 900 px Breite liegt das Panel über dem Dokument, statt es zu verschieben, und schließt sich nach der Auswahl einer Seite oder eines Treffers. Der Befehlsknopf schrumpft auf sein Symbol.
- Ist kein Platz mehr für alle Tabs, schrumpfen sie bis auf 120 px; danach zeigt die Leiste nur noch den aktiven Tab und daneben „Alle Tabs (n)“ als Menü.
- Unter 760 px entfallen die Beschriftungen „Seite“ und „Dokumentinfos“ in der Statusleiste; die Symbole bleiben, mit Tooltip.
- Das Mockup zeigt den Zustand bei 680 px.

## 7. Verhalten

### 7.1 Start und Startbildschirm

- Der erste Frame zeigt Titelleiste und Startbildschirm, beim Start mit Dateiargument sofort die Dokumentansicht mit Platzhaltern in Seitengröße. Nichts im ersten Frame braucht PDFium.
- Der Startbildschirm zeigt die Überschrift „Dokument öffnen“, den Hinweis „Pagewing arbeitet offline. Nichts verlässt diesen Rechner.“, den Primärknopf „Datei auswählen“ (Strg+O), den Hinweis auf Drag & Drop und ab M6 „Zuletzt geöffnet“.
- „Zuletzt geöffnet“ liest nur die Einstellungsdatei. Ob die Dateien noch existieren, prüft Pagewing erst nach dem ersten Frame; fehlende Einträge werden ausgegraut und mit dem Hinweis „Nicht gefunden“ versehen.
- Eine Datei, die ins Fenster gezogen wird, öffnet als neuer Tab, egal in welchem Zustand.

### 7.2 Tabs

- Ein neuer Tab erscheint rechts vom aktiven und wird aktiv.
- Der Tab-Name ist der Dateiname, höchstens 280 px breit. Er wird in der Mitte gekürzt, sodass Anfang und Endung sichtbar bleiben. Der volle Pfad steht im Tooltip.
- Der aktive Tab hat die Farbe des Dokumentbereichs und geht in ihn über; seine Schrift ist `type.strong`.
- Schließen per ×, Mittelklick oder Strg+W. Die Reihenfolge lässt sich durch Ziehen ändern (M4).
- Symbol im Tab: Dokument, Schloss während der Passwortabfrage, Warndreieck bei einem Fehler. Ab v0.2 steht bei ungespeicherten Änderungen ein Punkt vor dem ×.

### 7.3 Aktionsleiste und Panel

- Die Einträge der Aktionsleiste sind Seiten, Inhalt und Suche, ab v0.2 zusätzlich Kommentare; unten sitzen die Einstellungen.
- Ein Klick öffnet das Panel mit diesem Inhalt; ein Klick auf den aktiven Eintrag schließt es. Der aktive Eintrag hat eine `accent.subtle`-Fläche und ein `accent.default`-Icon.
- Es ist immer höchstens ein Panel offen.
- Beim Öffnen eines Dokuments öffnet sich das Panel „Seiten“, sofern das Fenster mindestens 900 px breit ist und der Nutzer das Panel zuletzt nicht geschlossen hatte. Den zuletzt gewählten Zustand speichert Pagewing ab M6.
- **Vorschaubilder:** Die aktuelle Seite hat eine `accent.subtle`-Fläche, 2 px Rahmen in `accent.default` und eine fette Nummer. Die Vorschaubilder werden erst nach dem ersten scharfen Seitenbild gerendert (Gesamt-Spec 5.4); bis dahin stehen leere Rahmen im Seitenformat da.
- Die Panelbreite lässt sich ziehen; ein Doppelklick auf die Kante setzt die Standardbreite zurück.

### 7.4 Suche

- Strg+F öffnet das Panel „Suche“ mit dem Fokus im Suchfeld.
- Die Suche läuft im Hintergrund, und die Trefferliste füllt sich nach und nach. Die Zusammenfassung zeigt dabei „Suche läuft …“, am Ende zum Beispiel „5 Treffer, alle auf Seite 3“ oder „12 Treffer auf 4 Seiten“.
- Jeder Eintrag zeigt eine Seitenmarke in `type.mono` und einen Textausschnitt mit markiertem Treffer. Der aktuelle Treffer hat eine `accent.subtle`-Fläche und links eine 2 px breite Kante in `accent.default`.
- Enter springt zum nächsten Treffer, Umschalt+Enter zum vorherigen, ein Klick zum gewählten.
- Auf der Seite sind alle Treffer mit `search.hit` hinterlegt, der aktuelle mit `search.current` und einem 1,5 px Rahmen.
- Esc im Suchfeld beendet die Suche: Die Markierungen verschwinden, und das Panel zeigt wieder, was vorher offen war.

### 7.5 Dokumentbereich, Laden und Zoom

- **Laden:** Erst steht ein Platzhalter in Seitengröße da (`surface.raised`, Kante `border.subtle`, Seitennummer klein in der Mitte), dann die grobe Vorschau, dann das scharfe Bild. Es gibt keinen Spinner.
- **Dunkles Theme:** Die Seiten bleiben weiß (eine dunkle Seitendarstellung ist optional, M6). Die Kante `page.border` hebt die Seite vom Hintergrund ab, und der Dokumentbereich ist nie reines Schwarz.
- **Zoom:** Strg+Mausrad sowie Strg+Plus/Minus. Ein Klick auf den Zoomwert in der Statusleiste öffnet ein Menü mit Stufen, Seitenbreite, ganzer Seite und tatsächlicher Größe (Zoom-Modi M4).
- **Seitenfeld:** Eingabe und Enter springen zur Seite. Eine ungültige Eingabe setzt das Feld auf die aktuelle Seite zurück, ohne Fehlermeldung.

### 7.6 Befehlspalette und Dokumentinfos

- **Befehlspalette (M6):** „Suchen oder Befehl“ bzw. Strg+K öffnet ein Popup unter der Titelleiste (560 breit, `radius.overlay`). Oben steht ein Eingabefeld, darunter die Gruppen „Zuletzt geöffnet“, „Seiten“ (eine Zahl springt zur Seite) und „Befehle“ mit ihren Tastenkürzeln. Esc schließt das Popup. Solange die Palette nicht umgesetzt ist, entfällt der Knopf.
- **Dokumentinfos:** Ein Popup über der Statusleiste mit Dateiname und Ordner, Seitenzahl, Seitenformat der aktuellen Seite (z. B. „A4 · 210 × 297 mm“), PDF-Version, Dateigröße sowie Titel, Autor und Erstelldatum aus den Metadaten, soweit vorhanden. Der Milestone dafür steht in Abschnitt 9.

### 7.7 Bearbeitungsmodi (ab v0.2)

- Die Leseansicht zeigt nie Bearbeitungswerkzeuge.
- Ein Modus (Kommentieren, später Formulare, Seiten, Schwärzen) öffnet seine eigene Werkzeugleiste (Abschnitt 6, Punkt 4). „Fertig“ oder Esc beendet ihn.
- Ungespeicherte Änderungen zeigen der Tab (Punkt) und die Statusleiste („Nicht gespeichert“).
- Wie man einen Modus betritt und welche Werkzeuge es gibt, legt die Spec von Teilprojekt 2 fest.

### 7.8 Passwort und Fehler

- **Passwortabfrage** (`PasswordRequired`): Statt eines modalen Dialogs erscheint eine Karte im Dokumentbereich des Tabs, damit andere Tabs bedienbar bleiben. Sie enthält das Schloss-Symbol, den Titel „Dieses PDF ist geschützt“, den Text „Geben Sie das Passwort ein, um „<Dateiname>“ zu öffnen.“, ein Passwortfeld mit Fokus sowie die Knöpfe „Abbrechen“ (schließt den Tab) und „Öffnen“ (Enter).
- **Falsches Passwort** (`PasswordIncorrect`): Das Feld bekommt einen Rand in `danger.default`, sein Inhalt bleibt markiert, und der Fokus bleibt im Feld. Darunter steht „Das Passwort ist falsch. Achten Sie auf Groß- und Kleinschreibung.“
- **Fehler beim Öffnen:** Eine Karte im Dokumentbereich mit Warnsymbol (`danger.default`), Titel „Diese Datei lässt sich nicht öffnen“ und der Ursache in Alltagssprache je Fehlerart:
  - `NotAPdf`: „ist beschädigt oder keine PDF-Datei“
  - `FileNotFound`: „wurde nicht gefunden“
  - `CannotRead`: „kann nicht gelesen werden (kein Zugriff oder zu groß)“
- Darunter folgen aufklappbare „Technische Details“ (Pfad, Fehlerart) und die Knöpfe „Tab schließen“ und „Andere Datei öffnen“ (Primär).
- Alle Texte gibt es auf Englisch und Deutsch (resx).

### 7.9 Fokus und Tastatur

- Der Fokusrahmen erscheint nur bei Tastaturbedienung (5.3).
- Die Tab-Reihenfolge ist Titelleiste → Aktionsleiste → Panel → Dokument → Statusleiste. F6 springt zwischen diesen Bereichen.
- Jeder Knopf ohne Beschriftung hat einen Tooltip und einen Automation-Namen; Tooltips nennen das Tastenkürzel, z. B. „Datei öffnen (Strg+O)“.

### 7.10 Zustände nie nur über Farbe

| Zustand | Kennzeichnung neben der Farbe |
|---|---|
| Aktiver Tab | geht in den Dokumentbereich über, fette Schrift |
| Aktuelle Seite in den Vorschaubildern | Rahmen, fette Nummer |
| Aktueller Suchtreffer | Rahmen auf der Seite, Kante in der Liste |
| Aktiver Eintrag der Aktionsleiste und aktives Werkzeug | Fläche hinter dem Icon |
| Fehler | Symbol und Text |
| Ungespeichert | Punkt im Tab, Text in der Statusleiste |

## 8. Schriftzug und Icon

Drei Varianten standen am 2026-10-17 als Artifact zur Wahl: https://claude.ai/artifact/8JufHaAKLfawx5aqJHa7p3, Quelle [schriftzug-icon.html](../../design/mockups/schriftzug-icon.html). Gewählt wurde **Variante 1 „Flügelecke“, vorläufig**: Der Projektinhaber will später eventuell ein eigenes Logo entwerfen. Bis dahin gilt dieser Entwurf (Entscheidung in `Docs/DECISIONS.md`, Eintrag „App-Icon und Schriftzug: Variante 1 „Flügelecke“ (vorläufig)“).

- **Idee:** Eine Seite, deren Eselsohr sich wie eine Flügelspitze nach oben hebt. Die Seite steht für PDF, die angehobene Ecke für Leichtigkeit und Tempo.
- **Dateien:** [`Docs/design/icon-entwurf.svg`](../../design/icon-entwurf.svg) (farbig), [`Docs/design/icon-entwurf-einfarbig.svg`](../../design/icon-entwurf-einfarbig.svg) (eine Farbe, `currentColor`, Flügelspitze mit 55 % Deckkraft, ohne Kachel), [`Docs/design/schriftzug.svg`](../../design/schriftzug.svg) (Schriftzug als Pfade, `currentColor`).
- **Geometrie:** 64er-Raster. Kachel 64 × 64 mit Radius 15, Seite von x 16 bis 47 und y 13 bis 53 mit ausgeschnittener Ecke, Flügelspitze von der Ecke bis (57, 5), drei Zeilen als 3er-Striche.
- **Farben:** Kachel `#3B48C4` (= `color.accent.default` hell), Seite `#FFFFFF`, Flügelspitze `#B9C0FF`, Zeilen `#3B48C4` mit 38 % Deckkraft. Die Kachel bleibt in beiden Themes gleich.
- **Kleine Größen:** Ab 16 px ist die Form erkennbar (Pixelansicht im Artifact). Für 16 und 24 px erzeugt M8 eine vereinfachte Fassung ohne Zeilen, weil die Striche dort verschwimmen.
- **Schriftzug:** „Pagewing“ in Geist Semibold (600), Laufweite −0,025 em, mit Kerning, in Pfade umgewandelt (aus Glyphen-Pfaden erlaubt, OFL-FAQ 1.1). Farbe `text.primary` des jeweiligen Themes (hell `#16181D`, dunkel `#E6E8EB`).
- **Wortbildmarke (Icon + Schriftzug):** Icon-Höhe = 1,375 × Schriftgröße, Abstand = 0,44 × Schriftgröße, beide vertikal mittig. Ringsum ein Schutzraum von einem Viertel der Icon-Höhe.
- **Einfarbige Fassung** für symbolische Icons (z. B. GNOME) und Overlays: `icon-entwurf-einfarbig.svg`.
- **Abgrenzung:** kein Rot und kein Dreieck-Schwung wie bei Acrobat, keine Browser-Kreise. Seiten mit Eselsohr nutzen viele Dokument-Icons; dieser Entwurf hebt sich durch die Flügelspitze und die Indigo-Kachel ab.
- **Was M8 daraus erzeugt:** `.ico` für Windows (16, 20, 24, 32, 40, 48, 64, 256 px), PNG für Linux (hicolor 16, 24, 32, 48, 64, 128, 256, 512 px), SVG (`scalable`) und ein symbolisches SVG (einfarbig). Der Schriftzug wird für den Über-Dialog und die Paketbeschreibungen genutzt.

## 9. Offene Punkte

Prüfung in M3, wenn nicht anders angegeben:

- Snap Layouts unter Windows 11 mit `ElementRole="MaximizeButton"` (Hover über Maximieren zeigt das Menü).
- Minimieren-/Maximieren-Animationen bei `WindowDecorations="Full"` mit Hint unter 12.1.4.
- Startkosten einer eingebetteten Schrift gegenüber der Systemschrift (`--measure-startup`).
- Start- und Bildratenkosten von Mica gegenüber deckender Fläche, falls Mica gewünscht ist.
- X11 mit und ohne Compositor: tatsächliches `ActualTransparencyLevel`, Titelleiste.
- Tatsächliche Standardschrift auf den Ziel-Distributionen.
- Darstellung bei Windows-Hochkontrast: Fluent wertet `ContrastPreference` nicht aus; ob und wie Pagewing eine Hochkontrast-Palette bekommt, entscheidet M6 (Komfort).
- Geist und Geist Mono: Startkosten der eingebetteten Teilmenge messen. Liegen sie über dem Budget, nutzt Pagewing die Systemschrift (Fallback aus 5.2); das Ergebnis kommt als Eintrag in `Docs/DECISIONS.md`.
- Fluent-Palette: übrige Rollen (`Chrome*`, `Alt*`, `List*`) so setzen, dass Textfeld, Knopf, Liste und Kontrollkästchen zu den Tokens in 5.1 passen; Prüfung per Sichtvergleich mit dem Mockup in beiden Themes.
- Fensterknöpfe unter Windows: ob Avalonia sie bei `WindowDecorations="Full"` mit Hint selbst zeichnet oder das System; Höhe und Hover-Farben an die Titelleiste (40 bzw. 34 px) anpassen.

Spätere Milestones (Zuordnung in der Roadmap, Abschnitt „Scope-Änderungen“, Eintrag vom 2026-10-17):

- „Dokumentinfos“ (7.6) in M4 (Navigation).
- Einstellungen „Dichte“ (Normal/Kompakt) und „Animationen reduzieren“ in M6 (Komfort), zusammen mit den übrigen Einstellungen.
