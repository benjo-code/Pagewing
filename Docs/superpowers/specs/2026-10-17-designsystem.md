# Pagewing – Designsystem

**Stand:** 2026-10-17 · **Status:** in Arbeit (Milestone 2) · **Gesamt-Spec:** [2026-10-16-pagewing-design.md](2026-10-16-pagewing-design.md) · **Roadmap:** [Milestone 2](../../ROADMAP.md) · **Plan:** [2026-10-16-milestone-02-designrichtungen.md](../plans/2026-10-16-milestone-02-designrichtungen.md)

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
| B „Werkbank“ | https://claude.ai/artifact/Qz1iJbLUijWbUH21WXvq6y | [richtung-b-werkbank.html](../../design/mockups/richtung-b-werkbank.html) | Aufgebaut wie ein Code-Editor: Aktivitätsleiste, Panel, Modus-Reiter, Befehlspalette in der Titelleiste, Statusleiste mit Dokumentdaten. Dicht, tastaturfreundlich, dunkel zuerst, Akzent Textmarker-Gelb; Geist und Geist Mono, Tabler Icons. |
| C „Papier“ | https://claude.ai/artifact/GqTxJnQPUbhrRoX9bjQ1ae | [richtung-c-papier.html](../../design/mockups/richtung-c-papier.html) | Ruhiger Leseraum: Fenster und Dokumentbereich im selben Papierton, schwebende Werkzeugleiste unten, Seitenübersicht als Panel bei Bedarf, Tintenblau als Akzent; Atkinson Hyperlegible Next, Literata für den Schriftzug, Phosphor Icons (Light). |

**Prüfung (2026-10-17):**
- Kontrast nach WCAG 2.2 AA für alle drei Token-Sätze in beiden Themes: 56 Paare je Richtung (Text auf allen Flächen ≥ 4,5:1, Akzent als Text ≥ 4,5:1, Ränder und Fokus ≥ 3:1), alle bestanden. Knappstes Paar je Richtung: `border.strong` auf `surface.window` (hell) mit 3,07 (A), 3,20 (B) und 3,34 (C).
- Sichtprüfung je Richtung in hell und dunkel bei 1440 px sowie bei 375 px Viewport-Breite: kein horizontales Scrollen (Inhaltsbreite 360 px bei 375 px), lange deutsche Beschriftungen („Ausfüllen & unterschreiben“, langer Dateiname) passen oder sind sauber gekürzt, die weiße Seite hat im dunklen Theme eine sichtbare Kante, aktive Seite, aktiver Tab und aktueller Suchtreffer sind nicht nur über Farbe erkennbar (Rahmen, Nummernmarke bzw. Unterstreichung, Form).
- Gefunden und behoben: In B passte die Befehlspalette bei 1280 px nicht mehr in die Kontextleiste; sie sitzt jetzt in der Titelleiste (unter Linux am Ende der Tab-Zeile).

## 4. Entscheidung

*Wird in Task 3 ausgefüllt.*

## 5. Tokens

*Wird in Task 4 ausgefüllt.*

## 6. Fenster-Layout

*Wird in Task 4 ausgefüllt.*

## 7. Verhalten

*Wird in Task 4 ausgefüllt.*

## 8. Schriftzug und Icon

*Wird in Task 5 ausgefüllt.*

## 9. Offene Punkte

Prüfung in M3, wenn nicht anders angegeben:

- Snap Layouts unter Windows 11 mit `ElementRole="MaximizeButton"` (Hover über Maximieren zeigt das Menü).
- Minimieren-/Maximieren-Animationen bei `WindowDecorations="Full"` mit Hint unter 12.1.4.
- Startkosten einer eingebetteten Schrift gegenüber der Systemschrift (`--measure-startup`).
- Start- und Bildratenkosten von Mica gegenüber deckender Fläche, falls Mica gewünscht ist.
- X11 mit und ohne Compositor: tatsächliches `ActualTransparencyLevel`, Titelleiste.
- Tatsächliche Standardschrift auf den Ziel-Distributionen.
- Darstellung bei Windows-Hochkontrast.
