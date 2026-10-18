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
- Eine eigene Variante in `FluentTheme.Palettes` ist nicht möglich: Avalonia 12.1.4 wirft für jede Variante außer Light und Dark eine `InvalidOperationException` („FluentTheme.Palettes only supports Light and Dark variants.“, SRC `src/Avalonia.Themes.Fluent/ColorPaletteResourcesCollection.cs`), und die Brushes der Fluent-Controls lesen die Palettenfarben einmal per `StaticResource`. Eine eigene `ThemeVariant` (mit `InheritVariant`) funktioniert dagegen als `RequestedThemeVariant` und in `ThemeDictionaries`. Der Hochkontrast (M6) nutzt deshalb eine eigene Variante für die Tokens und gibt ihr ein zweites `FluentTheme` mit der Hochkontrast-Palette als Dark-Palette (5.1, Regel „Hochkontrast“; geprüft am 2026-10-18 im Code von 12.1.4 und mit Headless-Tests).

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
- Das Fluent-Theme wertet Hochkontrast nicht aus (keine Fundstelle für `ContrastPreference` in SRC `src/Avalonia.Themes.Fluent/`); Pagewing muss es selbst umsetzen. Eine API für „Animationen reduzieren“ gibt es in Avalonia 12.1 nicht (keine Fundstelle für `ReducedMotion` oder `ClientAreaAnimation` in SRC `src/`). Beides wurde am 2026-10-16 im Quellcode von Tag 12.1.4 gesucht; ein Fehlen ist nur so sicher wie diese Suche.
- **Folge:** Animationen sind kurz und verzichtbar; eine Einstellung „Animationen reduzieren“ in Pagewing ersetzt die fehlende Systemabfrage (M6). Hochkontrast setzt Pagewing seit M6 unter Windows selbst um: eine feste Palette (5.1, Regel „Hochkontrast“), die der Laufzeitänderung von `ContrastPreference` folgt (`PlatformSettings.ColorValuesChanged`).

## 3. Richtungen

Drei Richtungen, veröffentlicht am 2026-10-17 als Artifacts (privat, nur für den Projektinhaber sichtbar). Die HTML-Quellen liegen unter [`Docs/design/mockups/`](../../design/mockups/). Es sind Artifact-Quellen ohne Dokument-Rahmen (`<!doctype>`, Zeichensatz-Angabe): Ein Browser zeigt sie lokal im Quirks-Modus und lädt die Schriften von Google Fonts. Maßgeblich für Layout und Schrift ist die Artifact-Ansicht.

Alle drei zeigen dieselbe Szene, damit sie vergleichbar sind: zwei Tabs (einer mit langem Namen, in der Mitte gekürzt), Werkzeugleiste mit Seitenfeld, Zoom und Seitenbreite, Suche mit Treffer 2 von 5, Seitenleiste mit Vorschaubildern und Reiter „Inhalt“, Seite 3 eines selbst geschriebenen Handbuchs und den Anfang von Seite 4, einen Platzhalter für eine noch nicht gerenderte Seite und einen Moduswechsel für die Werkzeuge ab v0.2 und v0.3. Umschaltbar sind hell, dunkel und System sowie die Ansichten „Dokument offen“, „Startbildschirm“ und „Linux-Titelleiste“. Jede Seite listet ihre Tokens (gleiche Namen in allen drei), Schriftgrößen, Abstände, Radien, die Icons und die Umsetzung in Avalonia.

| Richtung | Artifact | Quelle | Leitidee |
|---|---|---|---|
| A „Nativ & leise“ | https://claude.ai/artifact/He1E7CwqM3d8eQTxC2pnPs | [richtung-a-nativ.html](../../design/mockups/richtung-a-nativ.html) | Sieht aus wie ein Teil des Systems (Fluent, GNOME): neutrale Flächen, eine Akzentfarbe, klassische Werkzeugleiste, Systemschrift, Fluent UI System Icons. Geringstes Risiko, schnellster Start, wenig Wiedererkennung. |
| B „Werkbank“ | https://claude.ai/artifact/Qz1iJbLUijWbUH21WXvq6y | [richtung-b-werkbank.html](../../design/mockups/richtung-b-werkbank.html) | Erste Fassung (Artifact-Version 1): aufgebaut wie ein Code-Editor, mit Aktivitätsleiste, Panel, Modus-Reitern, Befehlspalette in der Titelleiste und Statusleiste mit Dokumentdaten. Dicht, tastaturfreundlich, dunkel zuerst, Akzent Textmarker-Gelb; Geist und Geist Mono, Tabler Icons. Am 2026-10-17 nach Rückmeldung des Projektinhabers überarbeitet (Version 2, siehe Abschnitt 4). Version 3 ergänzt die Zustände „Schmales Fenster“, „Passwort“ und „Fehler“, Version 4 das Token `search.on`; die übrigen Tokens sind seit Version 2 unverändert. Die Quelle im Repo ist Version 4. |
| C „Papier“ | https://claude.ai/artifact/GqTxJnQPUbhrRoX9bjQ1ae | [richtung-c-papier.html](../../design/mockups/richtung-c-papier.html) | Ruhiger Leseraum: Fenster und Dokumentbereich im selben Papierton, schwebende Werkzeugleiste unten, Seitenübersicht als Panel bei Bedarf, Tintenblau als Akzent; Atkinson Hyperlegible Next, Literata für den Schriftzug, Phosphor Icons (Light). |

**Prüfung (2026-10-17):**
- Kontrast nach WCAG 2.2 AA für alle drei Token-Sätze in beiden Themes: 28 Paare je Theme, also 56 Prüfungen je Richtung (Text auf allen Flächen ≥ 4,5:1, Akzent als Text ≥ 4,5:1, Ränder und Fokus ≥ 3:1), alle bestanden. Knappstes Paar je Richtung: `border.strong` auf `surface.window` (hell) mit 3,07 (A), 3,20 (B) und 3,34 (C).
- Sichtprüfung je Richtung in hell und dunkel bei 1440 px sowie bei 375 px Viewport-Breite: kein horizontales Scrollen (Inhaltsbreite 360 px bei 375 px), lange deutsche Beschriftungen („Ausfüllen & unterschreiben“, langer Dateiname) passen oder sind sauber gekürzt, die weiße Seite hat im dunklen Theme eine sichtbare Kante, aktive Seite, aktiver Tab und aktueller Suchtreffer sind nicht nur über Farbe erkennbar (Rahmen, Nummernmarke bzw. Unterstreichung, Form).
- Gefunden und behoben: In B passte die Befehlspalette bei 1280 px nicht mehr in die Kontextleiste; sie sitzt jetzt in der Titelleiste (unter Linux am Ende der Tab-Zeile).
- B Version 2 (2026-10-17) erneut geprüft: Kontrast der neuen Tokens bestanden (56 Prüfungen, knappstes `border.strong` auf `surface.raised`, dunkel, 3,18), Sichtprüfung aller Ansichten (Dokument offen, Suche, Kommentieren, Startbildschirm, Linux-Titelleiste) hell und dunkel, in beiden Dichten und bei 375 px ohne horizontales Scrollen.

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

Die Werte stammen aus dem gewählten Mockup (B, Artifact-Version 4). Token-Namen stehen in Punkt-Notation und beschreiben die Rolle, nicht die Farbe. In M3 werden sie zu Avalonia-Ressourcen: aus `color.surface.window` werden `Pw.Color.Surface.Window` (`Color`) und `Pw.Brush.Surface.Window` (`SolidColorBrush`), hell und dunkel getrennt über `ThemeDictionaries` (`Light`/`Dark`). Eine Token-Änderung ist eine Entscheidung in `Docs/DECISIONS.md`.

### 5.1 Farben

| Token | Hell | Dunkel | Hochkontrast | Verwendung | Fluent-Rolle (Vorschlag) |
|---|---|---|---|---|---|
| `color.surface.window` | `#EEEFF2` | `#15171B` | `#000000` | Titelleiste, Aktionsleiste, Statusleiste, Startbildschirm-Rahmen | `RegionColor` |
| `color.surface.toolbar` | `#F7F8FA` | `#1B1E23` | `#000000` | Werkzeugleiste eines Bearbeitungsmodus | – |
| `color.surface.sidebar` | `#F7F8FA` | `#1B1E23` | `#000000` | Panel (Seiten, Inhalt, Suche) | – |
| `color.surface.canvas` | `#E3E5EA` | `#0F1114` | `#000000` | Dokumentbereich hinter den Seiten, aktiver Tab | – |
| `color.surface.raised` | `#FFFFFF` | `#24272E` | `#000000` | Eingabefelder, Karten, Menüs, Befehlspalette | – |
| `color.surface.hover` | `#E3E5EA` | `#2A2E36` | `#333333` | Hover, Befehlsknopf | – |
| `color.accent.subtle` | `#E2E5F8` | `#262B52` | `#3D3D00` | Ausgewählt: aktive Seite, aktueller Treffer, aktiver Eintrag der Aktionsleiste, aktives Werkzeug | – |
| `color.border.subtle` | `#DCDFE5` | `#2A2E36` | `#FFFFFF` | Trennlinien, sparsam (siehe 5.3) | – |
| `color.border.strong` | `#7F848E` | `#6E7480` | `#FFFFFF` | Ränder von Eingabefeldern und sekundären Knöpfen | – |
| `color.text.primary` | `#16181D` | `#E6E8EB` | `#FFFFFF` | Text, Icons in Werkzeugleisten | `BaseHigh` |
| `color.text.secondary` | `#4F545E` | `#9BA1AC` | `#FFFFFF` | Beschriftungen, Statuswerte, Icons der Aktionsleiste | `BaseMedium` |
| `color.text.disabled` | `#9399A3` | `#5E636D` | `#A0A0A0` | Deaktiviert (vom Kontrast ausgenommen) | – |
| `color.accent.default` | `#3B48C4` | `#9DA7FF` | `#FFFF00` | Indigo: aktive Elemente, Primärknopf, Links | `Accent` |
| `color.accent.on` | `#FFFFFF` | `#121539` | `#000000` | Text und Icons auf `accent.default` | – |
| `color.focus.ring` | `#3340B4` | `#B4BCFF` | `#00FFFF` | Tastaturfokus | – |
| `color.page.border` | `#00000014` | `#FFFFFF24` | `#FFFFFF` | Kante der PDF-Seite | – |
| `color.page.shadow` | `#0000001A` | `#00000099` | `#00000099` | Schatten der PDF-Seite, von Karten und Popups | – |
| `color.search.hit` | `#FBE38A` | `#FBE38A` | `#FBE38A` | Suchtreffer (auf der Seite und in der Trefferliste) | – |
| `color.search.current` | `#F5A524` | `#F5A524` | `#F5A524` | Aktueller Suchtreffer, immer mit Rahmen | – |
| `color.search.on` | `#16181D` | `#16181D` | `#000000` | Text auf `search.hit` und `search.current` (in der Trefferliste); nie `text.primary`, das im dunklen Theme auf Gelb nur 1,04:1 erreicht | – |
| `color.selection.page` | `#3B48C44D` | `#3B48C44D` | `#3B48C44D` | Ausgewählter Text auf der Seite (30 % Indigo über dem Seitenbild, in beiden Themes gleich, weil Seiten weiß bleiben); schwarzer Text darauf 12,8:1 | – |
| `color.danger.default` | `#B42318` | `#FF8A80` | `#FF8080` | Fehlertext, Fehlersymbol, Rand eines fehlerhaften Felds | `ErrorText` |
| `color.page.darkBackground` | `#1E2127` | `#1E2127` | `#1E2127` | Grund einer dunklen Seite (M6) und ihres Platzhalters, auch des leeren Vorschaurahmens; so malt PDFium die dunkle Seite | – |
| `color.page.darkForeground` | `#E3E5E8` | `#E3E5E8` | `#E3E5E8` | Text und Linien einer dunklen Seite (PDFium), Seitennummer auf ihrem Platzhalter, 1,5 px Rahmen des aktuellen Treffers auf ihr; 12,8:1 auf `page.darkBackground` | – |
| `color.search.hitOnDark` | `#FBE38A4D` | `#FBE38A4D` | `#FBE38A4D` | Suchtreffer auf einer dunklen Seite: über die Seite gelegt (30 %), nicht multipliziert; Seitentext darauf 5,3:1 | – |
| `color.search.currentOnDark` | `#F5A52473` | `#F5A52473` | `#F5A52473` | Aktueller Suchtreffer auf einer dunklen Seite (45 %), immer mit Rahmen in `page.darkForeground`; Seitentext darauf 4,8:1 | – |
| `color.selection.pageOnDark` | `#9DA7FF59` | `#9DA7FF59` | `#9DA7FF59` | Ausgewählter Text auf einer dunklen Seite (35 % Indigo); Seitentext darauf 6,2:1 | – |

Regeln:

- Der Akzent ist nie Gelb. Gelb (`search.*`) gehört nur Suchtreffern; Farben von Markierungen sind Inhalt des PDFs und keine Tokens.
- Pagewing nutzt seine eigene Akzentfarbe, nicht die des Systems (Wiedererkennung, geprüfter Kontrast). Deshalb wird `Accent` in der Fluent-Palette immer gesetzt.
- Die Spalte „Fluent-Rolle“ ist ein Vorschlag. Die übrigen Fluent-Farben (`Chrome*`, `Alt*`, `List*`) werden in M3 so gesetzt, dass Fluent-Controls (Textfeld, Knopf, Liste, Kontrollkästchen) zu den Tokens passen (Abschnitt 9).
- Kontrast nach WCAG 2.2 AA, geprüft am 2026-10-17 in beiden Themes: `text.primary` auf allen Flächen ≥ 4,5:1, `text.secondary` auf allen Flächen außer `surface.canvas` ≥ 4,5:1, `accent.default` als Text auf `surface.window`, `surface.toolbar`, `surface.sidebar` und `surface.raised` ≥ 4,5:1, `accent.on` auf `accent.default` ≥ 4,5:1, `danger.default` auf `surface.raised` ≥ 4,5:1, `border.strong` auf `surface.window`, `surface.toolbar` und `surface.raised` ≥ 3:1, `focus.ring` auf allen Flächen ≥ 3:1, `accent.default` auf `accent.subtle` ≥ 3:1, `search.on` auf `search.hit` (13,9:1) und `search.current` (8,7:1) ≥ 4,5:1, `page.darkForeground` auf `page.darkBackground` mit darübergelegtem `search.hitOnDark`, `search.currentOnDark` und `selection.pageOnDark` ≥ 4,5:1 (geprüft am 2026-10-18). Knappstes Paar: `border.strong` auf `surface.raised` (dunkel) 3,18:1. Neue Paare, etwa `text.secondary` auf `surface.canvas`, brauchen vorher eine Prüfung.
- **Hochkontrast (M6, nur Windows):** Fordert Windows Hochkontrast an (`ContrastPreference` = `High`, Einstellungen → Barrierefreiheit → Kontrastdesigns) und steht das Theme auf „System“, gilt die feste Palette der Spalte „Hochkontrast“, unabhängig vom gewählten Kontrastdesign; ein ausdrücklich gewähltes Hell oder Dunkel geht vor. Linux liefert keinen Kontrastwunsch (2.7). Grund schwarz, Text und Ränder weiß, Akzent und Links Gelb `#FFFF00` (wie in den Kontrastdesigns von Windows; die Regel „Akzent nie Gelb“ gilt hier nicht), Fokus Cyan, Gefahr `#FF8080`, deaktiviert `#A0A0A0`. Geprüft am 2026-10-18 mit 7:1 statt 4,5:1: `text.primary`, `text.secondary`, `accent.default`, `focus.ring`, `border.strong` und `border.subtle` auf allen Flächen einschließlich `surface.hover` und `accent.subtle`, `danger.default` und `text.disabled` auf den schwarzen Flächen, `accent.on` auf `accent.default`, `search.on` auf `search.hit` und `search.current`; knappste Paare `text.disabled` auf Schwarz 8,0:1 (deaktiviert, vom Kontrast ausgenommen) und `danger.default` auf Schwarz 8,6:1. Nicht geprüft und nicht vorgesehen: `danger.default` und `text.disabled` auf `surface.hover` (5,2:1 bzw. 4,8:1). Die Seiten bleiben PDF-Inhalt (schwarz auf weiß, auch wenn die Seitendarstellung dem Theme folgt): Treffer- und Auswahlfarben auf der Seite und die dunkle Seite behalten ihre Werte (schwarzer Seitentext auf ihnen ≥ 7:1, auf der dunklen Seite wie oben ≥ 4,5:1); `page.border` ist weiß, damit auch eine dunkle Seite sich von der schwarzen Dokumentfläche abhebt. Umsetzung: `Styles/HighContrast.axaml` und `Services/HighContrast` (Variante `HighContrast`, erbt Dark; wird erst bei Bedarf geladen).

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
- **Schatten** (Avalonia `BoxShadow`): Seite `0 0 0 1 page.border, 0 2 10 0 page.shadow`; Karte und Popup `0 0 0 1 border.subtle, 0 12 32 -12 page.shadow`. Im Hochkontrast sind `page.border` und `border.subtle` weiß, Seiten und Karten bekommen also eine weiße 1-px-Kante. Der Seitenschatten wird aus diesem Token als vorberechnetes Bild gezeichnet, nicht als `BoxShadow` (Token unverändert; `Docs/DECISIONS.md`, Eintrag „Seitenschatten als vorberechnetes Bild (M3)“).

### 5.4 Dichte

„Normal“ ist Standard, „Kompakt“ eine Einstellung für Vielnutzer (M6, Abschnitt 9).

| Maß (px) | Normal | Kompakt | Ressource (M6) |
|---|---|---|---|
| Titelleiste bzw. Linux-Tab-Zeile | 40 | 34 | `Pw.Size.TitleBarHeight` (auch `ExtendClientAreaTitleBarHeightHint`) |
| Aktionsleiste: Breite / Knopf | 52 / 40 | 44 / 34 | `Pw.Size.ActionBarWidth` (auch App-Symbol der Titelleiste) / `Pw.Size.ActionButton` |
| Panel (Standardbreite) | 264 | 236 | `MainViewModel.DefaultPanelWidth` / `CompactDefaultPanelWidth` |
| Statusleiste | 32 | 26 | `Pw.Size.StatusBarHeight`; Einzug um die Aktionsleiste + 4: `Pw.Size.StatusBarIndent` (56 / 48) |
| Werkzeugleiste eines Modus | 44 | 38 | `Pw.Size.ToolbarHeight` |
| Rand um die Seiten | 32 | 20 | `Pw.Size.PageMargin` (`DocumentLayout.PageMargin`) |
| Abstand zwischen Seiten | 20 | 20 | `DocumentLayout.PageGap` |
| Grundschrift | 13 | 12.5 | `Pw.Font.Body`, Zeilenhöhe `Pw.Font.BodyLineHeight` (18 / 17) |
| Vorschaubilder je Zeile (Breite) | 1 (136) | 2 (Spaltenbreite: 56 bis 160, bei 236 Panelbreite 72) | `Pw.Size.ThumbnailColumns`, `Pw.Size.ThumbnailWidth` |

Umsetzung (M6): Die Werte für „Normal“ stehen in `Styles/Metrics.axaml`, die für „Kompakt“ mit denselben Schlüsseln in `Styles/Density.axaml`; `Services/Appearance` kopiert sie nur bei „Kompakt“ über die normalen (zur Laufzeit, ohne Neustart). Die Vorschaubilder in „Kompakt“ folgen der Panelbreite: (Panelbreite − 2 × 14 Listenrand − 16 Abstand) / 2, abzüglich 2 × (6 Innenabstand + 2 Ring + 1 Kante) des Eintrags, abgerundet auf 8 (bei 200 also 56, bei 236 72, bei 400 160; keine Obergrenze). Zwischen den Einträgen einer Zeile liegen wie zwischen den Zeilen 16, der Rest der Breite verteilt sich auf beide Außenränder; die Stufen von 8 sorgen dafür, dass das Ziehen der Panelkante die Vorschaubilder nicht bei jedem Pixel neu rendert. Eine gespeicherte Panelbreite gilt in beiden Dichten; gespeichert als 264 steht sie für „Standardbreite der Dichte“.

### 5.5 Icons

- **Satz:** Tabler Icons 3.49.0 (MIT), Variante „outline“, 24er-Raster, Strich 2 px, runde Enden und Ecken. Übernommen werden nur die genutzten Pfade; die Lizenz kommt zu den Drittlizenzen (M8).
- **Größen:** 20 px in der Aktionsleiste, 18 px in Titel- und Werkzeugleisten, 16 px in Panel, Tabs und Statusleiste.
- **Farbe:** `text.primary` in Werkzeugleisten, `text.secondary` in Aktions- und Statusleiste, `accent.default` für aktive Einträge, `danger.default` für Fehler.
- **Umsetzung:** Tabler-Icons sind Linien, `PathIcon` füllt nur. Pagewing braucht deshalb ein eigenes Icon-Control: `Path` mit `Stroke`, `StrokeThickness` 2, `StrokeLineCap`/`StrokeJoin` rund, in einer 24×24-`Viewbox`.
- **Belegung** (Tabler-Name): Dokument `file-text`, Datei öffnen `folder-open`, Suchen `search`, Seiten `layout-grid`, Inhalt `list-tree`, Einstellungen `settings`, Panel schließen `layout-sidebar-left-collapse`, Schließen `x`, Seite zurück/vor `chevron-left`/`chevron-right`, Menü sowie Aufklappen im Inhaltsverzeichnis `chevron-down`, Verkleinern/Vergrößern `minus`/`plus`, aktuelle Wahl in Menüs (Häkchen) `check`, Layouts Einzelseite `file`, Fortlaufend `layout-rows`, Doppelseitig `layout-columns`, Doppelseitig mit Deckblatt `book`, Dokumentinfos `info-circle`, Passwort `lock`, Fehler `alert-triangle`; ab v0.2 Kommentieren `message`, Markieren `highlight`, Unterstreichen `underline`, Zeichnen `pencil`, Notiz `note`, Rückgängig `arrow-back-up`, Löschen `trash`, Fertig `check`. Treffer vor/zurück in der Suche `chevron-down`/`chevron-up`. Bis M5 eingebunden (`src/Pagewing.App/Styles/Icons.axaml`) sind alle Einträge vor dem Semikolon außer `settings`; `search` und `chevron-up` kamen mit M5. Seitenbreite hat seit M4 keinen eigenen Knopf mehr (7.5), `arrow-autofit-width` entfällt.

### 5.6 Bewegung

- Hover- und Druckfarben wechseln ohne Animation. Panel und Popups öffnen mit 150 ms (ease-out), schließen mit 100 ms. Ressourcen: `Pw.Duration.Open` (150 ms) und `Pw.Duration.Close` (100 ms) in `Styles/Metrics.axaml`; jede Animation liest sie beim Start.
- Grobe Vorschau und scharfes Seitenbild werden ohne Überblenden getauscht; Geschwindigkeit geht vor Effekt.
- Bis zum ersten Frame läuft keine Animation.
- Avalonia 12.1 kennt keine Systemeinstellung „Animationen reduzieren“ (Abschnitt 2.7). Pagewing bekommt dafür eine eigene Einstellung (M6), die alle Dauern auf 0 setzt.

## 6. Fenster-Layout

Maße für die Dichte „Normal“, Kompakt siehe 5.4. Von oben nach unten:

1. **Titelleiste (Windows):** Eigene Titelleiste (`WindowDecorations="Full"` mit `ExtendClientAreaToDecorationsHint`). Von links: App-Symbol (so breit wie die Aktionsleiste), Tabs, Knopf „Datei öffnen“, Zieh-Fläche (`ElementRole="TitleBar"`), Knopf „Suchen oder Befehl · Strg K“, Fensterknöpfe. Tabs und Knöpfe haben `ElementRole="User"`.
2. **Titelleiste (Linux):** Die Titelleiste des Fenstermanagers bleibt. Darunter folgt eine Tab-Zeile in Titelleistenhöhe mit App-Symbol, Tabs, „Datei öffnen“, Zieh-Fläche und dem Befehlsknopf am Ende.
3. **Mitte:** Aktionsleiste (52) | Panel (264, optional, ziehbar von 200 bis 400) | Dokumentbereich. Der Dokumentbereich hat `surface.canvas`; das Panel bzw. ohne Panel der Dokumentbereich hat oben links `radius.overlay` gegenüber der Aktionsleiste.
4. **Werkzeugleiste eines Bearbeitungsmodus (ab v0.2):** 44 hoch über dem Dokumentbereich, `surface.toolbar`, Unterkante `border.subtle`. Von links: Modusname mit Icon (`accent.default`), Werkzeuge mit Icon und Beschriftung, Rückgängig und Löschen; rechts der Hinweis „Esc beendet“ und der Primärknopf „Fertig“.
5. **Statusleiste:** 32 hoch, `surface.window`, links bündig mit dem Dokumentbereich (um die Aktionsleiste eingerückt): ‹ „Seite“ [Feld] „/ 24“ ›, Trenner, Zoom − [Wert] +, Trenner, Layout-Knopf (Icon und Name des Layouts), ab v0.2 der Änderungsstatus („Nicht gespeichert“ mit Punkt). Rechts „Dokumentinfos“. Der Zoomwert öffnet das Zoom-Menü (7.5); einen eigenen Knopf „Seitenbreite“ gibt es seit M4 nicht mehr.

**Dokumentbereich:** Seiten zentriert untereinander, 20 Abstand, 32 Rand ringsum (auch unter der letzten Seite). Jede Seite hat Kante und Schatten (5.3).

**Mindestgröße:** 640 × 480.

**Schmales Fenster:**
- Unter 900 px Breite liegt das Panel über dem Dokument, statt es zu verschieben, und schließt sich nach der Auswahl einer Seite oder eines Treffers. Der Befehlsknopf schrumpft auf sein Symbol.
- Die Zieh-Fläche der Titelleiste ist immer mindestens 48 px breit, damit sich das Fenster verschieben lässt. Dafür schrumpfen zuerst die Tabs bis auf 120 px; reicht das nicht, zeigt die Leiste nur noch den aktiven Tab (ebenfalls bis 120 px) und daneben „Alle Tabs (n)“ als Menü. Bei 640 px Mindestbreite bleiben damit für die Zieh-Fläche rund 216 px (Aktionsleiste 52, Tab 120, „Alle Tabs“ 50, „Datei öffnen“ 32, Befehlsknopf 32, Fensterknöpfe 138).
- Werkzeugleiste eines Bearbeitungsmodus (ab v0.2), wenn sie nicht passt, in dieser Reihenfolge: zuerst entfällt „Esc beendet“, dann die Beschriftungen der Werkzeuge (Icons mit Tooltip bleiben), dann wandern Rückgängig und Löschen und danach die hinteren Werkzeuge ins Menü „⋯“. Modusname und „Fertig“ bleiben immer sichtbar. Die Spec von Teilprojekt 2 kann das für ihre Werkzeuge verfeinern.
- Unter 760 px entfallen die Beschriftungen „Seite“, der Name des Layouts und „Dokumentinfos“ in der Statusleiste; die Symbole bleiben, mit Tooltip.
- Das Mockup zeigt den Zustand bei 680 px.

## 7. Verhalten

### 7.1 Start und Startbildschirm

- Der erste Frame zeigt Titelleiste und Startbildschirm. Beim Start mit Dateiargument zeigt er stattdessen den Tab mit dem Dateinamen und einen leeren Dokumentbereich; die Platzhalter in Seitengröße folgen, sobald der Kern die Seitengrößen liefert. Nichts im ersten Frame braucht PDFium.
- Der Startbildschirm zeigt die Überschrift „Dokument öffnen“, den Hinweis „Pagewing arbeitet offline. Nichts verlässt diesen Rechner.“, den Primärknopf „Datei auswählen“ (Strg+O), den Hinweis auf Drag & Drop und ab M6 „Zuletzt geöffnet“.
- „Zuletzt geöffnet“ liest nur die Einstellungsdatei. Ob die Dateien noch existieren, prüft Pagewing erst nach dem ersten Frame; fehlende Einträge werden ausgegraut und mit dem Hinweis „Nicht gefunden“ versehen.
- Eine Datei, die ins Fenster gezogen wird, öffnet als neuer Tab, egal in welchem Zustand.

### 7.2 Tabs

- Ein neuer Tab erscheint rechts vom aktiven und wird aktiv.
- Der Tab-Name ist der Dateiname, höchstens 280 px breit. Er wird in der Mitte gekürzt, sodass Anfang und Endung sichtbar bleiben. Der volle Pfad steht im Tooltip.
- Der aktive Tab hat die Farbe des Dokumentbereichs und geht in ihn über; seine Schrift ist `type.strong`.
- Schließen per ×, Mittelklick oder Strg+W (Strg+W schließt den aktiven Tab).
- Ein Klick (Drücken) aktiviert den Tab. Ziehen ab 6 px ändert die Reihenfolge innerhalb der Leiste, die Nachbarn weichen sofort aus; Esc während des Ziehens stellt die alte Position wieder her (seit M4).
- Herausziehen (seit M4.1): Zieht der Zeiger mehr als 40 px senkrecht von der Leiste weg (oder aus dem Fenster hinaus), löst sich der Tab. Ein neues Fenster in der Größe des Quellfensters hängt dann am Zeiger, der es dort hält, wo er den Tab gefasst hat, und folgt ihm. Mit nur einem Tab im Fenster wandert das Fenster selbst. Über der Tableiste eines Fensters (auch des eigenen) tritt das schwebende Fenster zur Seite, und die Leiste zeigt einen 2 px breiten Strich in `accent.default` an der Stelle, an der der Tab landet; beim Loslassen wandert der Tab dorthin, mit Dokument, Seite und Zoom, und das leere Fenster schließt sich. Loslassen an anderer Stelle lässt das Fenster liegen. Esc stellt Platz und aktiven Tab wieder her. Das Kontextmenü „In neuem Fenster öffnen“ bleibt der Weg, wo das Fenster nicht gesetzt werden kann (Wayland, ungeprüft).
- Rechtsklick öffnet ein Kontextmenü mit „In neuem Fenster öffnen“ (nur ab zwei Tabs im Fenster; verschiebt den Tab samt Dokument, Seite und Zoom in ein neues Fenster gleicher Größe, 32 px versetzt) und „Schließen“.
- Strg+Tab wechselt zum nächsten, Strg+Umschalt+Tab zum vorigen Tab, jeweils mit Umlauf.
- Überlauf: Passen die Tabs auch mit 120 px nicht, zeigt die Leiste nur den aktiven Tab und „Alle Tabs (n)“ (Anzahl und `chevron-down`) als Menü mit allen Dateinamen in Tab-Reihenfolge, der aktive fett (Abschnitt 6, „Schmales Fenster“).
- Dieselbe Datei ein zweites Mal öffnen aktiviert ihren Tab; mehrere Dateien auf einmal öffnen höchstens 20 Tabs, die letzte wird aktiv. Nach dem letzten Tab zeigt das erste Fenster den Startbildschirm, jedes weitere Fenster schließt sich.
- Symbol im Tab: Dokument, Schloss während der Passwortabfrage, Warndreieck bei einem Fehler. Ab v0.2 steht bei ungespeicherten Änderungen ein Punkt vor dem ×.
- Im hellen Theme ist `surface.hover` gleich `surface.canvas`: Ein inaktiver Tab unter dem Zeiger hat die Farbe des aktiven. Den aktiven kennzeichnen weiter fette Schrift und der Übergang in den Dokumentbereich (7.10); hingenommen (`Docs/DECISIONS.md`, Eintrag „Tableiste ohne `ItemsControl` (M4)“).

### 7.3 Aktionsleiste und Panel

- Die Einträge der Aktionsleiste sind Seiten, Inhalt und Suche, ab v0.2 zusätzlich Kommentare; unten sitzen die Einstellungen.
- Ein Klick öffnet das Panel mit diesem Inhalt; ein Klick auf den aktiven Eintrag schließt es. Der aktive Eintrag hat eine `accent.subtle`-Fläche und ein `accent.default`-Icon.
- Es ist immer höchstens ein Panel offen. Welches Panel offen ist, gehört zum Tab (ein Tab-Wechsel zeigt das Panel des anderen Tabs); die Breite gehört zum Fenster.
- Umgesetzt sind Seiten und Inhalt (M4) und Suche (M5); Kommentare (ab v0.2) und Einstellungen (M6) erscheinen erst mit ihrem Milestone. Die Aktionsleiste ist sichtbar, sobald das Fenster einen Tab hat. Für Seiten und Inhalt gibt es noch kein Tastenkürzel, die Suche öffnet Strg+F (7.4).
- Beim ersten scharfen Seitenbild eines Fensters öffnet sich das Panel „Seiten“ im aktiven Tab, einmal je Fenster und nur, wenn das Fenster mindestens 900 px breit ist und der Nutzer in diesem Fenster noch kein Panel geöffnet oder geschlossen hat; auch Strg+F zählt als Wahl des Nutzers. Ein weiterer Tab beginnt ohne Panel. Den zuletzt gewählten Zustand speichert Pagewing ab M6. Messläufe öffnen nie ein Panel.
- Das Panel blendet beim Öffnen mit 150 ms ein und beim Schließen mit 100 ms aus (nur die Deckkraft, 5.6), nicht beim Tab-Wechsel.
- Liegt das Panel neben dem Dokument, trägt es oben links `radius.overlay`, der Dokumentbereich keine Ecke; im schmalen Fenster (unter 900 px) liegt es über dem Dokument, mit 1 px `border.subtle` an der Kante zum Dokument und ohne Schatten, und schließt sich nach der Wahl einer Seite, eines Eintrags oder eines Suchtreffers.
- **Vorschaubilder:** Die aktuelle Seite hat eine `accent.subtle`-Fläche, 2 px Rahmen in `accent.default` und eine fette Nummer. Die Vorschaubilder werden erst nach dem ersten scharfen Seitenbild gerendert (Gesamt-Spec 5.4); bis dahin stehen leere Rahmen im Seitenformat da. Klick, Enter oder Leertaste zeigen die Seite; Pfeiltasten, Bild auf/ab, Pos1 und Ende bewegen die Auswahl in der Liste, ohne das Dokument umzublättern.
- **Inhalt:** die Lesezeichen des PDFs als Baum (gelesen beim ersten Öffnen), Einträge mit Ziel springen zur Seite, Einträge ohne Ziel klappen nur auf und zu; ohne Lesezeichen ein Hinweis im Panel.
- Die Panelbreite lässt sich neben dem Dokument von 200 bis 400 px ziehen; ein Doppelklick auf die Kante setzt die Standardbreite zurück.

### 7.4 Suche

- Strg+F öffnet das Panel „Suche“ (auch über einem anderen Panel) mit dem Fokus im Suchfeld; ein vorhandener Suchbegriff ist markiert. Strg+F zählt in 7.3 als Wahl des Nutzers. Der Knopf „Suche“ der Aktionsleiste (Tooltip „Suchen (Strg+F)“) öffnet dasselbe Panel.
- Unter dem Suchfeld stehen zwei Optionen: „Groß-/Kleinschreibung beachten“ und „Ganze Wörter“. Gesucht wird nach Text, nicht nach Mustern (keine regulären Ausdrücke, kein Ersetzen). Ohne die erste Option gilt die Groß-/Kleinschreibung nicht; Diakritika zählen immer mit („uber“ findet „über“ nicht). Ligaturen („ﬁ“), Zeilenumbrüche und ein Trennstrich am Zeilenende innerhalb eines Worts werden überbrückt („Win-“ am Zeilenende und „dows“ in der nächsten Zeile ergeben „Windows“).
- Die Suche beginnt 200 ms nach der letzten Eingabe oder Änderung einer Option und läuft im Hintergrund; die Trefferliste füllt sich nach und nach. Sie beginnt auf der aktuellen Seite, läuft bis zum Ende des Dokuments und dann von Seite 1 bis vor die aktuelle Seite; die Liste ist nach Seite und Position im Text sortiert. Die Zusammenfassung (eine Zeile unter den Optionen, `type.caption`) zeigt dabei „Suche läuft …“, am Ende „1 Treffer auf Seite 3“, „5 Treffer, alle auf Seite 3“, „12 Treffer auf 4 Seiten“ oder „Keine Treffer“.
- Die Zahl der Treffer ist auf 10.000 begrenzt; die Suche endet dort, und die Zusammenfassung sagt „Mehr als 10.000 Treffer“ (die Zahl gruppiert, wie die Sprache der Oberfläche es schreibt, englisch „More than 10,000 matches“). Das Panel bleibt auch dann bedienbar, die Liste ist virtualisiert.
- Hat die Suche keinen Treffer und enthalten die ersten Seiten des Dokuments keinen Text, steht an der Stelle der Zusammenfassung stattdessen: „Dieses Dokument enthält keinen durchsuchbaren Text. Gescannte Seiten brauchen eine Texterkennung (OCR), die Pagewing noch nicht hat.“
- Jeder Eintrag zeigt eine Seitenmarke in `type.mono` und einen Textausschnitt mit markiertem Treffer (der Treffer auf `search.hit`, Text `search.on`). Der aktuelle Treffer hat eine `accent.subtle`-Fläche und links eine 2 px breite Kante in `accent.default`.
- Enter springt zum nächsten Treffer, Umschalt+Enter zum vorherigen (jeweils mit Umlauf), ein Klick oder Enter in der Liste zum gewählten; dieselben Wege bieten die zwei Pfeilknöpfe neben der Zusammenfassung. F3 und Umschalt+F3 tun dasselbe, auch wenn das Panel geschlossen ist, solange die Suche besteht. In der Liste bewegen Pfeile, Bild auf/ab, Pos1 und Ende nur die Auswahl; Enter und Leertaste wählen den Eintrag (wie in der Seitenvorschau). Der erste Treffer ab der aktuellen Seite wird zum aktuellen Treffer, sobald er da ist; hat der Nutzer vorher selbst gewählt, bleibt dessen Wahl.
- Auf der Seite sind alle Treffer mit `search.hit` hinterlegt, der aktuelle mit `search.current` und einem 1,5 px Rahmen in `search.on`. „Hinterlegt“ heißt: Die Farbe wird mit dem Seitenbild multipliziert (wie ein Textmarker), der Seitentext bleibt also schwarz lesbar.
- Ein Sprung zu einem Treffer scrollt nur, wenn dessen Rechteck nicht ganz im sichtbaren Bereich liegt: Dann steht die Mitte des Rechtecks in der Mitte des Bereichs (soweit der Inhalt es zulässt), seitwärts wird nur so weit gescrollt wie nötig.
- Esc im Suchfeld beendet die Suche: Die Markierungen verschwinden, und das Panel zeigt wieder, was vorher offen war; der Fokus geht zurück in die Dokumentansicht. Dasselbe tun ein leeres Suchfeld, Esc in der Dokumentansicht (dort löscht Esc zuerst eine Textauswahl, 7.5a, erst ein zweites Esc beendet die Suche) und ein anderes Dokument im Tab. Das Schließen des Panels (Klick auf den aktiven Eintrag der Aktionsleiste) versteckt nur das Panel: Markierungen, aktueller Treffer sowie F3 und Umschalt+F3 bleiben, bis die Suche beendet wird.
### 7.5 Dokumentbereich, Laden und Zoom

- **Laden:** Erst steht ein Platzhalter in Seitengröße da (`surface.raised`, Kante `border.subtle`, Seitennummer klein in der Mitte), dann die grobe Vorschau, dann das scharfe Bild. Es gibt keinen Spinner.
- **Dunkles Theme:** Die Seiten bleiben weiß (eine dunkle Seitendarstellung ist optional, M6). Die Kante `page.border` hebt die Seite vom Hintergrund ab, und der Dokumentbereich ist nie reines Schwarz.
- **Zoom:** Strg+Mausrad sowie Strg+Plus/Minus. Ein Klick auf den Zoomwert in der Statusleiste öffnet ein Menü nach oben: die Stufen 25 bis 400 %, ein Trenner, dann „Seitenbreite“, „Ganze Seite“ und „Tatsächliche Größe“ mit ihrem Tastenkürzel rechts in `text.secondary`. Ein Häkchen (`check`) markiert den aktiven Modus, im freien Zoom die passende Stufe.
- **Zoom-Modi (M4):** Seitenbreite (Strg+2) und ganze Seite (Strg+0) passen die Zeile der aktuellen Seite ein, tatsächliche Größe (Strg+1) ist 100 %. Die Modi passen neu an, wenn der Modus gewählt wird, das Layout oder die Fenstergröße sich ändert oder zu einer Seite gesprungen wird, nicht beim Scrollen. Stufen, Strg+Mausrad und Strg+Plus/Minus beenden den Modus. Modus, Zoom und Layout gehören zum Tab.
- **Layout (M4):** Der Layout-Knopf der Statusleiste zeigt Icon und Name des aktuellen Layouts (unter 760 px nur das Icon) und öffnet ein Menü mit Einzelseite (`file`), Fortlaufend (`layout-rows`), Doppelseitig (`layout-columns`) und Doppelseitig mit Deckblatt (`book`), das aktuelle mit Häkchen. Doppelseitig scrollt fortlaufend, die Seiten einer Zeile stehen oben bündig mit 8 px Abstand an der Mittelachse; mit Deckblatt steht die erste Seite allein rechts. In der Einzelseite blättern Bild auf/ab, die Pfeile der Statusleiste, die Leertaste am Seitenende und ein Mausradschritt über den Seitenrand hinaus.
- **Seitenfeld:** Eingabe und Enter springen zur Seite. Eine ungültige Eingabe setzt das Feld auf die aktuelle Seite zurück, ohne Fehlermeldung. Strg+G setzt den Fokus mit markiertem Inhalt in das Feld (Gehe zu Seite); der Tooltip nennt das Kürzel.

### 7.5a Text markieren und kopieren (M5)

- **Markieren mit der Maus:** Ziehen mit der linken Taste markiert den Text von der Stelle des Drückens bis zur Stelle des Loslassens, auch über mehrere Seiten. Die Auswahl folgt der Lesereihenfolge des PDFs (die Reihenfolge, in der das PDF seinen Text schreibt), nicht einem Rechteck; eine Spaltenerkennung gibt es nicht. Doppelklick markiert ein Wort, Dreifachklick eine Zeile. Ein Klick ohne Weg (unter 4 DIP) oder ein Klick neben die Seite löscht die Auswahl; Esc in der Dokumentansicht löscht sie ebenfalls. Strg+Klick und Klicks auf die Bildlaufleisten markieren nichts. Am Rand des Fensters scrollt die Ansicht beim Ziehen nicht von selbst (Backlog).
- **Darstellung:** Der markierte Text liegt auf `selection.page` (5.1), einer halbtransparenten Fläche über dem Seitenbild, in beiden Themes gleich. Je Zeile ist es ein Rechteck; die Rechtecke folgen gedrehten Seiten und der CropBox.
- **Auswahl und Tab:** Die Auswahl gehört zum Tab. Sie bleibt bei Zoom, Layoutwechsel und Tab-Wechsel erhalten und endet mit einem anderen Dokument im Tab.
- **Cursor:** Über Text steht ein I-Balken, über einem Link (7.5b) die Hand (der Link gewinnt), sonst der Pfeil; der Pfeil bleibt auch, solange der Text der Seite noch gelesen wird. Der Text wird erst nach dem ersten scharfen Seitenbild gelesen, die sichtbaren Seiten und je eine davor und dahinter im Hintergrund.
- **Seite ohne Text:** Ein Druck auf eine Seite ohne Text (Scan, Vektorgrafik) markiert nichts, sondern zeigt unten mittig im Dokumentbereich drei Sekunden lang eine Karte (`surface.raised`, 1 px `border.subtle`, `radius.overlay`): „Auf dieser Seite gibt es keinen auswählbaren Text. Sie ist vielleicht ein Scan.“ Die Karte nimmt keine Eingaben an.
- **Kopieren:** Strg+C legt den markierten Text als **Fließtext** in die Zwischenablage, in zwei Formaten: Klartext und HTML. Strg+Umschalt+C legt **Rohtext** ab (Klartext mit den Zeilenumbrüchen des PDFs, ohne HTML). Beide wirken, wenn die Dokumentansicht den Fokus hat; ein Suchfeld oder das Seitenfeld behält sein eigenes Kopieren. Ohne Auswahl bleibt die Zwischenablage unverändert und die Taste geht weiter.
- **Was der Fließtext macht:** Die Zeilen eines Absatzes werden zu einer Zeile verbunden, ein Trennstrich am Zeilenende innerhalb eines Worts entfällt, Ligaturen stehen als Buchstaben da (aus „ﬁ“ wird „fi“), mehrere Leerzeichen werden eins. Ein neuer Absatz beginnt, wo der senkrechte Abstand zweier Zeilen größer ist als die halbe Schriftgröße; an einer Seitengrenze beginnt er nur nach einem Satzende (`.`, `!`, `?`, `:`). Fett und Kursiv übernimmt das HTML nur, wo das PDF es anzeigt (Schriftname, Schrift-Flags, Gewicht). Eine Zeile mit deutlich größerer Schrift als der Fließtext der Seite wird zur Überschrift (`h1` bis `h3`, ab dem 1,6-, 1,3- und 1,15-Fachen, höchstens 100 Zeichen). **Nicht** kopiert werden Schriftart, Schriftgröße im Fließtext und Farbe: Der eingefügte Text soll nicht wie ein zusammengewürfeltes PDF aussehen.
- **Grenzen:** Eingerückte Absatzanfänge ohne Mehrabstand bleiben ein Absatz; Kopf- und Fußzeilen werden mitkopiert; bei mehreren Spalten stimmt der Text nur, wenn das PDF spaltenweise schreibt; bei gedrehtem oder senkrechtem Text sind die Absätze unsicher (der Text geht nicht verloren). Pagewing verspricht keinen Absatzsatz wie in einem Textverarbeitungsprogramm.

### 7.5b Links (M5)

- **Erkennen:** Klickbar sind die Link-Flächen des PDFs und Adressen, die im Text stehen (`https://…`, `www.…`); überschneidet sich beides, gilt die Link-Fläche. Über einem klickbaren Link steht die Hand. Ein Link folgt erst beim **Loslassen** der linken Taste, ohne Umschalt, Strg oder Alt, wenn der Zeiger nicht gezogen wurde (unter 4 DIP) und noch über demselben Link steht; ein Doppelklick folgt nichts.
- **Intern:** Ein Link auf eine Seite (mit oder ohne Position) springt sofort, ohne Rückfrage: Die Oberkante des Ziels steht oben im Dokumentbereich (mit dem Seitenrand davor), bei „Einzelseite“ wird die Seite gezeigt und ein Zoom-Modus passt neu an, wie bei jedem Sprung zu einer Seite (7.5). Alt+Links springt zur Ansicht vor dem Sprung zurück, Alt+Rechts wieder vor; der Verlauf gehört zum Tab, hält je Richtung 50 Sprünge, und ein neuer Sprung leert den Vorwärts-Verlauf. Bei gedrehten oder zugeschnittenen Zielseiten ist die Position nur ungefähr.
- **Extern:** Erlaubt sind nur die Schemata `http`, `https` und `mailto`. Ein Klick darauf öffnet nie sofort etwas, sondern eine **Bestätigungskarte** im Dokumentbereich (Stil wie die Fehlerkarte, 7.8: `surface.raised`, `radius.overlay`, Kartenschatten und 1 px `border.subtle`): Titel „Diese Webadresse öffnen?“, der Text „Dieses PDF möchte einen Link in Ihrem Browser öffnen. Fahren Sie nur fort, wenn Sie der Adresse vertrauen.“, darunter die **vollständige Adresse** in `type.mono` (umbrechend, bei sehr langen Adressen scrollbar), der Host **fett**, dann die Knöpfe „Abbrechen“ und „Öffnen“ (beide sekundär, damit die riskante Aktion nicht hervorgehoben ist). Geöffnet wird genau die angezeigte Zeichenkette.
- **Warnungen:** Enthält die Adresse vor dem „@“ einen Benutzernamen (`https://bank.example@evil.example/`), zeigt die Karte eine Warnzeile mit Warndreieck und Text in `danger.default`: „Die Adresse enthält vor dem „@“ einen Benutzernamen. Der eigentliche Host ist …“. Nutzt der Host Sonderzeichen (`xn--` oder ein Unicode-Host), lautet sie „Der Host nutzt Sonderzeichen und wird als … dargestellt.“ Zustand und Warnung stehen als Symbol und Text da, nicht nur in Farbe (7.10). Die Warnung nennt die Unicode-Form des Hosts, wenn sie nur aus Buchstaben, Ziffern, „-“ und „.“ besteht, sonst den Host, wie er geschrieben steht; ein Unicode-Host, der einem bekannten Namen zum Verwechseln ähnlich sieht, erscheint damit nur in der geschriebenen Form (Backlog).
- **Standard ist Abbrechen:** Der Fokus liegt auf „Abbrechen“, Enter bricht also ab; Esc bricht ab. „Öffnen“ ist die ersten 400 ms, nachdem die Karte gezeichnet ist, inaktiv und sieht deaktiviert aus (damit ein Doppelklick auf den Link nie auf „Öffnen“ landet); ein Druck auf „Öffnen“ in dieser Zeit wird ignoriert, auch wenn er erst danach losgelassen wird, ebenso Enter und Leertaste. Ein Doppelklick auf „Öffnen“ löst es nie aus. Für Screenreader trägt die Karte den Titel als Namen, und die Adresse wird beim Erscheinen angesagt. Die Karte deckt den ganzen Dokumentbereich ab: Klicks und Dokumenttasten (Bild auf/ab, Alt+Links/Rechts, F3, Strg+F, Strg+G) wirken dahinter nicht; Aktionsleiste, Panel, Statusleiste und Tableiste bleiben bedienbar. Ein Wechsel des Tabs oder des Dokuments verwirft die Karte. Wartet eine Adresse, wird ein weiterer Linkklick ignoriert. Es gibt kein „Immer erlauben“, und Pagewing merkt sich keine Entscheidung.
- **Nicht klickbar:** Alle anderen Ziele (`javascript:`, `file:`, `data:`, Programmaufrufe, Laufwerks- und Netzpfade, Sprünge in andere Dateien, JavaScript-Aktionen) haben keine Hand und tun beim Klick nichts; sie verhalten sich wie Text. Dasselbe gilt für Adressen mit Leerzeichen, Anführungszeichen, Steuer- oder unsichtbaren Zeichen und für überlange Adressen.

### 7.6 Befehlspalette und Dokumentinfos

- **Befehlspalette (M6):** „Suchen oder Befehl“ bzw. Strg+K öffnet ein Popup unter der Titelleiste (560 breit, `radius.overlay`). Oben steht ein Eingabefeld, darunter die Gruppen „Zuletzt geöffnet“, „Seiten“ (eine Zahl springt zur Seite) und „Befehle“ mit ihren Tastenkürzeln. Esc schließt das Popup. Solange die Palette nicht umgesetzt ist, entfällt der Knopf.
- **Dokumentinfos (M4):** Der Knopf „Dokumentinfos“ rechts in der Statusleiste (nur bei geöffnetem Dokument) öffnet ein Popup über der Statusleiste mit Dateiname und Ordner, Seitenzahl, Seitenformat der aktuellen Seite (z. B. „A4 · 210 × 297 mm“; A3 bis A6, Letter und Legal mit Namen, sonst „B × H mm“), PDF-Version, Dateigröße (Einheiten zu 1024, z. B. „1,5 MB“) sowie Titel, Autor und Erstelldatum aus den Metadaten, soweit vorhanden. Je Angabe eine Zeile: Bezeichnung in `type.caption` und `text.secondary`, Wert in `type.body` und `text.primary`. Karte mit `radius.overlay`, `surface.raised` und 1 px `border.subtle`; den Schatten zeichnet das System, weil das Popup ein eigenes Fenster ist. Die Metadaten werden erst beim Öffnen gelesen. Esc oder ein Klick daneben schließt das Popup (der Fokus geht zurück an den Knopf), ebenso ein anderer Tab oder ein anderes Dokument.

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
  - `UnsupportedEncryption`: „ist mit einem Verfahren verschlüsselt, das Pagewing nicht unterstützt“
  - `Unknown`: „konnte aus einem unbekannten Grund nicht geöffnet werden“
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
| Link mit Warnung (Bestätigungskarte) | Warndreieck und Text |
| Aktiver Eintrag der Aktionsleiste und aktives Werkzeug | Fläche hinter dem Icon |
| Fehler | Symbol und Text |
| Ungespeichert | Punkt im Tab, Text in der Statusleiste |

## 8. Schriftzug und Icon

Der Schriftzug stammt aus dem Artifact vom 2026-10-17 (https://claude.ai/artifact/8JufHaAKLfawx5aqJHa7p3, Quelle [schriftzug-icon.html](../../design/mockups/schriftzug-icon.html)). Das Icon war dort der Entwurf „Flügelecke“ und ist seit 2026-10-17 durch ein vom Projektinhaber geliefertes Icon ersetzt (Entscheidung in `Docs/DECISIONS.md`, Eintrag „App-Icon: neues Icon „Seite mit Welle“ löst den Entwurf „Flügelecke“ ab“).

- **Icon:** Weiße Seite mit Eselsohr auf dunkelblauer Kachel mit leichtem Lichtschein, der untere Seitenrand läuft in eine blauviolette Welle aus. Rastermotiv, keine SVG-Quelle.
- **Dateien:** [`Docs/design/icons/`](../../design/icons/README.md) (Master 1024 px, hicolor-PNGs 16 bis 512 px, `.desktop`-Vorlage; die `.ico` liegt als `src/Pagewing.App/Assets/pagewing.ico` bei der App) und [`Docs/design/schriftzug.svg`](../../design/schriftzug.svg) (Schriftzug als Pfade, `currentColor`).
- **Farben:** Die Kachel ist bewusst dunkel und in beiden Themes gleich; sie ist keine Token-Farbe. Die Welle liegt im Blauviolett der Akzentfarbe, `color.accent.default` bleibt `#3B48C4` (hell) bzw. `#9DA7FF` (dunkel).
- **Kleine Größen:** Bei 16 und 24 px bleibt die Seite mit Eselsohr erkennbar, die Welle verschwimmt zu einem Farbfleck. Eine vereinfachte Fassung für diese Größen ist nicht vorgesehen, solange das Motiv ein Raster ist.
- **Schriftzug:** „Pagewing“ in Geist Semibold (600), Laufweite −0,025 em, mit Kerning, in Pfade umgewandelt (aus Glyphen-Pfaden erlaubt, OFL-FAQ 1.1). Farbe `text.primary` des jeweiligen Themes (hell `#16181D`, dunkel `#E6E8EB`).
- **Wortbildmarke (Icon + Schriftzug):** Icon-Höhe = 1,375 × Schriftgröße, Abstand = 0,44 × Schriftgröße, beide vertikal mittig. Ringsum ein Schutzraum von einem Viertel der Icon-Höhe. Ob das neue Icon mit dem Schriftzug zusammen genutzt wird, ist nicht entschieden.
- **Einbindung:** Windows nimmt `ApplicationIcon` (`.ico`), Linux setzt das Fenster-Icon aus `Assets/pagewing-128.png` nach dem ersten Frame. M8 baut daraus die Pakete mit den hicolor-Größen und dem Starter (`.desktop`); ein `scalable`-SVG und ein symbolisches Icon gibt es erst mit einem Vektorentwurf.

## 9. Offene Punkte

Stand nach M5 (2026-10-18); „Erledigt“ nennt M3 und M4, „Offen“ am Ende auch M5.

Erledigt:

- „Dokumentinfos“ (7.6) in M4: Popup über der Statusleiste (`Docs/DECISIONS.md`, Einträge „Dokumentinfos: beim Öffnen lesen …“ und „Dokumentinfos: Seitenformat-Namen …“).
- Tabs mit Umsortieren, Kontextmenü und Überlauf (7.2), Aktionsleiste mit „Seiten“ und „Inhalt“ (7.3), Zoom-Menü, Zoom-Modi und Layout-Knopf (7.5) in M4; die Belegung der Icons (5.5) ist nachgezogen.
- Menüs (Zoom, Layout, Tab-Kontextmenü, „Alle Tabs“) in M4 mit `surface.raised`, 1 px `border.subtle` und `radius.overlay` (5.1, 5.3), Einträge mit `surface.hover` und 2 px `focus.ring` bei Tastaturbedienung (`Docs/DECISIONS.md`, Eintrag „Menüs: Stile über dem Fluent-Flyout …“). Sichtprüfung der Headless-Aufnahmen hell und dunkel, Deutsch und Englisch: Im dunklen Theme ist der Rand `border.subtle` schwach, die Fläche `surface.raised` hebt das Menü aber vom Hintergrund ab.

- Startkosten der eingebetteten Schrift gegenüber der Systemschrift (Geist und Geist Mono): gemessen, Geist bleibt (Warmstart +4 ms, Regel höchstens +15 ms). `Docs/DECISIONS.md`, Eintrag „Schrift-Entscheidung nach Messung (M3) …“.
- Fluent-Palette: übrige Rollen sind auf das nächste Token gesetzt (`Docs/DECISIONS.md`, Eintrag „Design-Tokens, Schrift und Icons in Avalonia umgesetzt (M3)“). Sichtvergleich der Headless-Aufnahmen (Startbildschirm, Passwort- und Fehlerkarte, Dokumentansicht; hell und dunkel; Deutsch und Englisch) gegen Tokens und Maße dieser Spec nach Augenmaß: keine erkennbare Abweichung. Offen bleibt der Vergleich mit dem Mockup im Artifact und von Hover-, Druck- und Fokuszuständen am echten Bildschirm.
- Fensterknöpfe unter Windows: Avalonia 12.1.4 zeichnet sie bei `WindowDecorations="Full"` mit Hint selbst (Fluent `WindowDrawnDecorations`, 3 × 45 px); `MainWindow.axaml` blendet Titeltext und Vollbildknopf aus, `TitleBar.axaml` reserviert 140 px. Im AOT-Binary sichtbar und auf die 40-px-Titelleiste ausgerichtet (Fensteraufnahme). Offen: Aussehen und Hover-Farben auf einem echten 60-Hz-Bildschirm, Kompakt-Höhe 34 px (Dichte kommt in M6).

Offen (wie zu prüfen):

- Snap Layouts unter Windows 11 mit `ElementRole="MaximizeButton"`: Zeiger auf Maximieren halten, das Layout-Menü muss erscheinen (manuell auf Windows 11 mit echter Anzeige; in der Entwicklungs-VM nicht geprüft).
- Minimieren-/Maximieren-Animationen bei `WindowDecorations="Full"` mit Hint unter 12.1.4: Fenster minimieren, wiederherstellen und maximieren und auf die Systemanimation achten (manuell, echte Anzeige).
- Start- und Bildratenkosten von Mica gegenüber deckender Fläche, falls Mica gewünscht ist (`bench/startup` mit beiden Varianten auf einem Rechner mit GPU).
- X11 mit und ohne Compositor: tatsächliches `ActualTransparencyLevel`, Titelleiste (manuell auf einem Linux-Desktop, z. B. Xfce mit ein- und ausgeschaltetem Compositor).
- Tatsächliche Standardschrift auf den Ziel-Distributionen (`fc-match sans-serif` und `fc-match monospace` je Distribution; die Distributionen legt M8 fest).
- Hochkontrast (M6) am echten Windows-Kontrastdesign: Tabs, Aktionsleiste, Panel, Karten, Menüs und Befehlspalette ansehen, auch das Umschalten bei laufender App (`Pagewing.exe testdata/pdfs/text-rich.pdf`, dann Einstellungen → Barrierefreiheit → Kontrastdesigns → „Nachthimmel“ und zurück; ebenso „Wüste“, das hell ist und trotzdem die schwarze Palette bekommt). Die Headless-Aufnahme zeigt keine Trennlinie zwischen Panel und Dokumentfläche und zwischen Titelleiste und Dokumentfläche, weil im Hochkontrast alle Flächen schwarz sind; ob das am Bildschirm stört, entscheidet die Sichtprüfung.
- Linux-Titelleiste mit Tab-Zeile darunter (2.2): manuell auf einem Linux-Desktop prüfen; die Headless-Tests erzwingen die Windows-Titelleiste.
- Hover-, Druck- und Fokuszustände der Elemente aus M4 (Tabs, Aktionsleiste, Panel, Statusleiste, Menüs) auf einem echten Bildschirm in beiden Themes ansehen; die Headless-Aufnahmen zeigen nur Ruhezustände.
- „In neuem Fenster öffnen“ unter Windows und Linux (X11): Lage und Größe des neuen Fensters (32 px versetzt, ganz auf dem Bildschirm, auch am rechten oder unteren Rand und bei 125 %/150 % Skalierung; Quellfenster maximiert), welches Fenster nach vorn kommt, und Dateien eines späteren Aufrufs öffnen im zuletzt aktivierten Fenster (manuell, `pagewing <datei.pdf>` aus einem Terminal).
- Linux: Das zweite Fenster zeigt das App-Icon (manuell auf einem Linux-Desktop).
- Schatten des Popups „Dokumentinfos“ (`Popup.WindowManagerAddShadowHint`) auf echter Hardware unter Windows und Linux ansehen.
- Einzelseite: Eine Touchpad-Geste mit Trägheit kann mehrere Seiten hintereinander blättern, wenn die Seite ganz in den Viewport passt; auf echter Hardware prüfen, ob eine Sperrzeit nötig ist.
- Das Popup „Dokumentinfos“ öffnet ohne die 150-ms-Einblendung aus 5.6.
- M5 (Suche, Auswahl, Links; Befehle in `Docs/ROADMAP.md`, Milestone 5, „Offen“): Einfügen von Strg+C in Word und LibreOffice (Absätze, fett, kursiv, Überschriften); Linux: `text/html` in der Zwischenablage, I-Balken- und Hand-Cursor, Multiplikation der Treffer (7.4) auf dem GPU-Pfad; das Öffnen des Browsers nach „Öffnen“ (7.5b) unter Windows und Linux; Sichtprüfung der Kantenrundung des aktuellen Treffers und der Link-Karte am echten Bildschirm in beiden Themes.

Spätere Milestones (Zuordnung in der Roadmap, Abschnitt „Scope-Änderungen“, Eintrag vom 2026-10-17):

- Einstellungen „Dichte“ (Normal/Kompakt) und „Animationen reduzieren“ in M6 (Komfort), zusammen mit den übrigen Einstellungen.
- Hochkontrast (Windows `ContrastPreference`) in M6: entschieden und umgesetzt (feste Palette, 5.1); offen ist die Sichtprüfung oben.
