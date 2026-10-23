# Milestone 2 – Designrichtungen – Implementierungsplan

> **Für agentische Ausführer:** Pflicht-Skill: `superpowers:subagent-driven-development` (empfohlen) oder `superpowers:executing-plans`. Die Schritte nutzen Checkbox-Syntax (`- [ ]`).

**Ziel:** Der Projektinhaber hat aus mindestens drei Designrichtungen eine gewählt, und sie ist als Design-Spec mit Tokens, Fenster-Layout, Verhalten der wichtigsten Bereiche, Schriftzug und Icon-Richtung festgehalten.

**Architektur:** Kein Produktcode. Jede Richtung ist eine eigenständige HTML-Seite, die dieselbe Szene zeigt (Hauptfenster mit geöffnetem PDF, Seitenleiste, Werkzeugleiste, Startbildschirm), hell und dunkel, und darunter ihre Tokens. Die Seiten werden als Artifacts veröffentlicht und als Quelle unter `Docs/design/` eingecheckt, damit sie ohne claude.ai erhalten bleiben. Die Design-Spec ist die Vorlage für M3: Tokens so benannt und gegliedert, dass sie sich auf Avalonia-Ressourcen (`ThemeVariant` hell/dunkel) abbilden lassen.

**Tech-Stack:** HTML/CSS (inline, nur erlaubte CDNs), Artifact-Tool, Avalonia 12.1.4 mit `FluentTheme` als Zielplattform (nur geprüft, nicht angefasst).

**Spec:** [2026-10-16-pagewing-design.md](../specs/2026-10-16-pagewing-design.md), Abschnitte 1, 4, 5.4, 5.8, 6 (Teilprojekt 1, Zeile M2). Roadmap: [Milestone 2](../../ROADMAP.md).

## Global Constraints

- Prioritäten laut Spec: „1. Geschwindigkeit: Start, Öffnen, Scrollen 2. Gutes, modernes Design 3. Funktionsumfang“. Keine Richtung darf etwas verlangen, das den Startpfad belastet (Unschärfe-/Acrylic-Effekte als Pflicht, Animationen vor dem ersten Frame, große Schriftdateien ohne Messplan).
- Jede Richtung muss in Avalonia 12.1 unter Windows **und** Linux (X11/XWayland) umsetzbar sein, mit Native AOT, ohne neue Laufzeit-Abhängigkeit außer Schrift und Icons. Was nur unter Windows geht (z. B. Mica), ist optional und hat einen gleichwertigen Fallback.
- Schriften und Icons nur mit GPL-3.0-verträglicher Lizenz (MIT, BSD, Apache-2.0, ISC, OFL-1.1 als separat ausgelieferte Schriftdatei); jede gewählte Abhängigkeit bekommt einen Eintrag in `Docs/DECISIONS.md`. Eingebunden werden sie erst in M3.
- UI-Texte gibt es auf Englisch und Deutsch (Spec 5.8). Die Mockups zeigen deutsche Beschriftungen, weil sie meist länger sind. Kein Text verspricht etwas, das gegen die fachlichen Regeln in `CLAUDE.md` verstößt (nicht „rechtsgültig“, keine Absatz-Bearbeitung).
- Kontrast nach WCAG 2.2 AA in beiden Themes: Text ≥ 4,5:1, großer Text, Icons, Fokusrahmen und Bedienelement-Ränder ≥ 3:1.
- Mockup-Inhalte sind selbst geschrieben (neutraler Fließtext, eine einfache Vektorgrafik). Keine echten Dokumente, Marken oder Personen; Dateinamen neutral (`Benutzerhandbuch.pdf`, `Vortrag.pdf`).
- Artifact-Regeln: vor dem ersten Artifact `artifact-design` laden und je neuem Artifact zuerst `Artifact` mit `action: "quickstart"`, `intent: "design"` aufrufen. Das Fenster-Mockup (Entwurfsbreite 1280 × 800) skaliert per CSS auf die Viewport-Breite, ohne horizontales Scrollen. Farben als Tokens auf `:root`, dunkel über `prefers-color-scheme` **und** einen Umschalter (`data-theme`).
- Nur M2-Umfang: kein Code unter `src/`, keine Pakete, kein Theme in der App (M3), kein Dark-Mode-Umschalter in der App (M6), keine Icon-Dateien für Installer (M8).
- Doku und Commit-Messages Deutsch, Conventional Commits (`docs:`), gezielt stagen, nach jedem Commit `git show --stat HEAD` prüfen, Trailer laut Session-Vorgabe.
- Zu Beginn den Status von M2 in `Docs/roadmap.json` auf `in-progress` setzen. Nach jedem Task die unter **Roadmap** genannten Einträge abhaken (`"done": true`, Nachweis in `"evidence"`), `dotnet run tools/roadmap.cs -- generate` ausführen und beide Dateien mit committen.
- „Fertig“ heißt: `dotnet build Pagewing.slnx`, `dotnet test Pagewing.slnx`, `dotnet format Pagewing.slnx --verify-no-changes` und `dotnet run tools/roadmap.cs -- check` nachweislich grün (auch wenn nur Doku geändert wurde).

## Review Focus

Situationen, die die Spec nicht nennt, die aber im Alltag auftreten. Die Prüfung steht jeweils im genannten Task.

1. **Schmales Fenster** (Halbbild auf 1366er-Bildschirm, ca. 680 px Breite): Werkzeugleiste bricht nicht um und schneidet nichts ab, Seitenleiste klappt ein, Suchfeld bleibt erreichbar. Mindestfenstergröße ist festgelegt (Task 4).
2. **Deutsche Beschriftungen** sind bis zu 40 % länger als englische: Knöpfe, Tabs und Menüs vertragen „Ausfüllen & unterschreiben“ und lange Dateinamen (Kürzung mit „…“ in der Mitte, voller Name im Tooltip) (Task 2, Task 4).
3. **Dunkles Theme mit weißer PDF-Seite:** Die Seite blendet nicht; Seitenrand bzw. Schatten bleibt auf dunklem Grund sichtbar, Hintergrund des Dokumentbereichs ist nicht reines Schwarz. Dunkle Seitendarstellung selbst ist M6 (Task 2, Task 4).
4. **Barrierefreiheit:** Kontrast laut Global Constraints in beiden Themes für alle Text-/Flächenpaare, sichtbarer Tastaturfokus, kein Zustand nur über Farbe (aktive Seite, aktiver Tab, Suchtreffer) (Task 2, Task 4).
5. **Plattformgrenzen und Startpfad:** Eigene Titelleiste mit Tabs unter Linux (X11, Fenstermanager ohne Unterstützung), Fenster ohne Mica unter Windows 10 bzw. ohne Compositor, Schriftladen beim Start: jede Richtung nennt ihren Fallback und was sie bis zum ersten Frame braucht (Task 1, Task 4).

## Entscheidungen dieses Plans (Annahmen sind markiert)

- **Drei Richtungen als Vorschlag** (der Projektinhaber kann sie bei der Planprüfung ändern):
  - **A „Nativ & leise“:** nah an Windows 11 (Fluent) und GNOME/libadwaita. Neutrale Grautöne, eine Akzentfarbe, klassische Werkzeugleiste oben, Seitenleiste links mit Vorschaubildern, Tabs in der Titelleiste. Wirkt auf beiden Systemen zu Hause, geringstes Umsetzungsrisiko.
  - **B „Werkbank“:** dichtes Profi-Werkzeug im Stil von Code-Editoren. Schmale Icon-Leiste links (Vorschau, Inhalt, Suche, später Kommentare), kontextabhängige Werkzeugleiste, Befehlspalette (Strg+K) prominent, kompakte Abstände, dunkel zuerst gedacht.
  - **C „Papier“:** auf das Lesen ausgerichtet. Warme, gebrochene Weißtöne, großzügige Ränder, schwebende Werkzeugleiste (Pille) unten über dem Dokument, Seitenleiste als einblendbares Panel, ruhiger Schriftzug mit Charakter.
  - *Eigene Entscheidung.* Weitere Richtungen sind erlaubt, drei sind Pflicht.
- **Gemeinsame Szene in allen Richtungen**, damit sie vergleichbar sind: Titelleiste bzw. Tab-Leiste mit zwei offenen Dokumenten; Werkzeugleiste mit Seitenleiste ein/aus, Öffnen, Seitenanzeige „3 / 24“, Zoom (−, „100 %“, +, Seitenbreite), Suche; ein Moduswechsel für spätere Werkzeuge (Ansehen, Kommentieren, Ausfüllen & unterschreiben, Seiten, Schwärzen), als „später“ gekennzeichnet, damit sichtbar ist, wo Teilprojekte 2–4 Platz finden; Seitenleiste mit Vorschaubildern (aktuelle Seite markiert) und Reiter „Inhalt“; Dokumentbereich mit Seite 3 und Anfang von Seite 4, ein Suchtreffer markiert; ein Knopf im Hover- und einer im Fokus-Zustand. Zweite Ansicht: Startbildschirm ohne Dokument (das ist der erste Frame beim Kaltstart). *Eigene Entscheidung.*
- **Tokens:** Namen in Punkt-Notation nach Rolle, nicht nach Farbe (`color.surface.window`, `color.text.secondary`, `color.accent.default`, `space.2`, `radius.control`), je Token ein Wert hell und dunkel. Die Spec nennt zu jedem Farb-Token die Fluent-Palettenrolle, auf die M3 es abbilden soll, soweit es eine gibt. *Eigene Entscheidung.*
- **Icons** werden in M3 als Pfad-Geometrien nur für die genutzten Symbole übernommen (keine Icon-Schrift, kein Paket): AOT-freundlich, kein Ladeaufwand beim Start. Ihre Lizenz kommt zu den Drittlizenzen (M8).
- **Mockup-Quellen** liegen unter `Docs/design/mockups/`, Schriftzug und Icon-Skizze unter `Docs/design/`. *Eigene Entscheidung,* weil Artifact-Links privat sind und ein öffentliches Repo sie nicht auflösen kann.

## Dateistruktur

```
Docs/
  design/
    mockups/
      richtung-a-nativ.html        # Task 2
      richtung-b-werkbank.html     # Task 2
      richtung-c-papier.html       # Task 2
      schriftzug-icon.html         # Task 5
    schriftzug.svg                 # Task 5
    icon-entwurf.svg               # Task 5
  superpowers/specs/
    YYYY-MM-DD-designsystem.md     # Task 1 angelegt, Task 3–5 ausgebaut (Datum = Tag des Anlegens)
  DECISIONS.md                     # Task 3, Task 4, Task 5
  roadmap.json, ROADMAP.md         # jeder Task
CLAUDE.md                          # Task 6 (Verweis auf die Design-Spec)
```

---

### Task 1: Rahmenbedingungen prüfen und Design-Spec anlegen

**Files:**
- Create: `Docs/superpowers/specs/YYYY-MM-DD-designsystem.md`
- Modify: `Docs/roadmap.json` (Status `in-progress`), `Docs/ROADMAP.md` (generiert)

**Interfaces:**
- Produces: Abschnitt „Rahmenbedingungen“ der Design-Spec, gegen den Task 2 jede Richtung prüft; Kandidatenliste Schrift und Icons mit geprüfter Lizenz.

- [ ] **Step 1:** M2 in `Docs/roadmap.json` auf `"status": "in-progress"` setzen, `generate` ausführen.
- [ ] **Step 2: Machbarkeit in Avalonia 12.1 klären, jede Aussage mit Quelle** (Avalonia-Doku, Quellcode oder Issue, Abrufdatum). Fragen:
  - Eigene Titelleiste mit Tabs (`ExtendClientAreaToDecorationsHint` o. Ä.): Windows 10/11 ja/nein, Linux X11 ja/nein; Verhalten von Fenster-Knöpfen, Andocken (Snap Layouts), Ziehen; Fallback, wenn es nicht geht.
  - Fenster-Transparenz (Mica, Acrylic) über `TransparencyLevelHint`: welche Plattformen, und gibt es eine messbare Startkosten-Angabe? Wenn nicht: als „in M3 messen“ markieren, nicht schätzen.
  - Anpassung von `FluentTheme` über Paletten je `ThemeVariant` (`ColorPaletteResources`) gegenüber eigenem Theme: was ist überschreibbar (Akzent, Flächen, Radien, Dichte)?
  - Schrift: Avalonia-Standard, `Avalonia.Fonts.Inter` (Version, Lizenz der Schrift und des Pakets), Systemschrift je OS (Segoe UI Variable, Cantarell/Noto/Ubuntu). Startkosten eingebetteter Schrift als offener Messpunkt für M3.
- [ ] **Step 3: Icon-Sätze und Schriften prüfen:** mindestens Fluent UI System Icons, Lucide, Phosphor, Tabler. Je Satz: Lizenz (Datei im Repo des Satzes gelesen), Stil (Strich/Fläche), Rastergröße (16/20/24), Vollständigkeit für die Szene (Seitenleiste, Öffnen, Zoom, Suche, Seitenbreite, Hervorheben, Stift, Unterschrift, Schwärzen, Drehen). Schriften: jede in den Mockups geplante Schrift mit Lizenz.
- [ ] **Step 4: Design-Spec anlegen** mit Kopf (Stand, Status „in Arbeit“, Verweis auf Gesamt-Spec und Roadmap) und den Abschnitten: 1 Ziel, 2 Rahmenbedingungen (Ergebnisse aus Step 2–3 mit Quellen, Global Constraints dieses Plans), 3 Richtungen (leer, Task 2), 4 Entscheidung (leer, Task 3), 5 Tokens, 6 Fenster-Layout, 7 Verhalten, 8 Schriftzug und Icon (leer, Task 4–5), 9 Offene Punkte.
- [ ] **Step 5: Prüfen:** `dotnet run tools/roadmap.cs -- check` → Exit 0.
- [ ] **Step 6: Commit** `docs: Design-Spec mit Rahmenbedingungen anlegen (Milestone 2)` (Spec, `roadmap.json`, `ROADMAP.md`).

**Roadmap:** nichts abzuhaken (nur Status).

---

### Task 2: Drei Designrichtungen als Artifacts

**Files:**
- Create: `Docs/design/mockups/richtung-a-nativ.html`, `richtung-b-werkbank.html`, `richtung-c-papier.html`
- Modify: Design-Spec Abschnitt 3, `Docs/roadmap.json`, `Docs/ROADMAP.md`

**Interfaces:**
- Consumes: Rahmenbedingungen und Kandidaten aus Task 1.
- Produces: drei Artifact-URLs; je Richtung ein Token-Satz (gleiche Token-Namen in allen drei Seiten, Schema siehe „Entscheidungen dieses Plans“), den Task 4 übernimmt.

- [ ] **Step 1:** Skill `artifact-design` laden; je Richtung `Artifact` `action: "quickstart"`, `intent: "design"` aufrufen.
- [ ] **Step 2: Richtung A bauen** nach der gemeinsamen Szene. Aufbau jeder Seite: Kopf mit Name und Leitidee der Richtung (2–3 Sätze), Umschalter hell/dunkel und „Dokument / Startbildschirm“; das skalierte Fenster-Mockup; darunter „Tokens dieser Richtung“ (Farbfelder hell/dunkel mit Namen und Hex, Schriftgrößen-Skala, Abstands-Skala, Radien, Icon-Satz) und „Umsetzung in Avalonia“ (was nötig ist, Fallback unter Linux/ohne Effekt, was bis zum ersten Frame geladen wird).
- [ ] **Step 3: Richtung B und C bauen**, gleiche Szene, gleiche Token-Namen, deutlich unterschiedliche Haltung (Layout, Dichte, Farbe, Form). Icons aus dem in Task 1 geprüften Satz der jeweiligen Richtung, inline als SVG.
- [ ] **Step 4: Kontrast prüfen.** Kleines Skript im Scratchpad (nicht eingecheckt), das für jede Richtung und jedes Theme die WCAG-Kontrastverhältnisse der Paare Text/Fläche, Icon/Fläche, Rand/Fläche, Fokus/Fläche, Akzent-Text/Akzent ausgibt. Erwartet: alle Werte laut Global Constraints; sonst Tokens nachziehen.
- [ ] **Step 5: Sichtprüfung je Seite** in beiden Themes und bei 375 px und 1440 px Viewport-Breite: kein horizontales Scrollen; deutsche Beschriftungen ungekürzt oder sauber mit „…“ gekürzt (Review Focus 2); weiße Seite im dunklen Theme mit sichtbarer Kante (Review Focus 3); aktive Seite, aktiver Tab und Suchtreffer nicht nur über Farbe erkennbar (Review Focus 4).
- [ ] **Step 6: Veröffentlichen** (je Seite ein Artifact, `icon: "design"`), URLs und je zwei Sätze Leitidee in Abschnitt 3 der Design-Spec eintragen; HTML-Quellen unter `Docs/design/mockups/` ablegen.
- [ ] **Step 7: Roadmap abhaken** (siehe unten), `generate`, `check` → Exit 0.
- [ ] **Step 8: Commit** `docs: drei Designrichtungen als Mockups (Milestone 2)`.

**Roadmap:** Aufgabe „Mindestens 3 unterschiedliche … Designrichtungen als HTML-Mockups“ und Kriterium „Mindestens 3 Richtungen wurden als Artifacts bereitgestellt (Links in der Design-Spec)“ (Nachweis: die drei URLs, Abschnitt 3 der Design-Spec, Kontrast-Ergebnis).

---

### Task 3: Entscheidung des Projektinhabers

**Files:**
- Modify: `Docs/DECISIONS.md`, Design-Spec Abschnitt 4, `Docs/roadmap.json`, `Docs/ROADMAP.md`

**Interfaces:**
- Consumes: Artifact-URLs aus Task 2.
- Produces: gewählte Richtung (Buchstabe und Name) samt gewünschter Änderungen, auf die Task 4 und 5 aufbauen.

- [ ] **Step 1: Fragen** mit `AskUserQuestion`: eine Frage „Welche Designrichtung soll Pagewing bekommen?“ mit den Optionen A, B, C (je ein Satz Beschreibung, Link steht im Text davor); „Other“ deckt Mischungen ab. Eine zweite Frage nach Änderungswünschen an der gewählten Richtung (Akzentfarbe, Dichte, Schrift).
- [ ] **Step 2: Bei Mischung oder Änderungswünschen** das betreffende Artifact überarbeiten (gleicher `file_path`, gleiche URL), Kontrast erneut prüfen und erneut fragen, bis eine Richtung **ausdrücklich** gewählt ist. Keine Wahl unterstellen.
- [ ] **Step 3: Eintrag in `Docs/DECISIONS.md`** im Format der vorhandenen Einträge: „Designrichtung: <Name>“, Kontext, Entscheidung (mit Datum und dem Wortlaut der Wahl), Begründung, verworfene Richtungen je mit Grund (soweit der Projektinhaber einen nennt; sonst „ohne Angabe von Gründen verworfen“).
- [ ] **Step 4:** Abschnitt 4 der Design-Spec ausfüllen (Wahl, Änderungen gegenüber dem Mockup, Verweis auf `DECISIONS.md`).
- [ ] **Step 5: Roadmap abhaken**, `generate`, `check` → Exit 0.
- [ ] **Step 6: Commit** `docs: Designrichtung festgelegt (Milestone 2)`.

**Roadmap:** Aufgabe „Entscheidung des Projektinhabers einholen …“ und Kriterium „Projektinhaber hat eine Richtung ausdrücklich gewählt“ (Nachweis: Datum, gewählte Option, Eintrag in `DECISIONS.md`).

---

### Task 4: Design-Spec ausarbeiten

**Files:**
- Modify: Design-Spec Abschnitte 5–7 und 9, gewähltes Mockup unter `Docs/design/mockups/` (gleiche Artifact-URL), `Docs/DECISIONS.md`, `Docs/roadmap.json`, `Docs/ROADMAP.md`

**Interfaces:**
- Consumes: Token-Satz der gewählten Richtung (Task 2) mit den Änderungen aus Task 3.
- Produces: die verbindliche Token-Tabelle und Layout-Maße, die M3 in Avalonia-Ressourcen übersetzt.

- [ ] **Step 1: Tokens** (Abschnitt 5) als Tabellen: Farben (Name, hell, dunkel, Verwendung, Fluent-Palettenrolle), Typografie (Schrift, Fallback je OS, Größen/Gewichte/Zeilenhöhen je Rolle: Titel, Text, Beschriftung, klein), Abstände (4-px-Raster, Stufen), Radien, Ränder und Schatten (Seite, Popup), Bewegung (Dauer, Kurve, Verhalten bei „Animationen reduzieren“, keine Animation vor dem ersten Frame), Icons (Satz, Größen, Strichstärke, Zustände).
- [ ] **Step 2: Fenster-Layout** (Abschnitt 6) mit Maßen: Titel-/Tab-Leiste, Werkzeugleiste, Seitenleiste (Standard-, Mindest-, Höchstbreite), Dokumentbereich (Hintergrund, Seitenabstand, Seitenschatten), optionale Statusleiste, Mindestfenstergröße; Linux- und Windows-Variante, falls die Titelleiste abweicht (Review Focus 5).
- [ ] **Step 3: Verhalten** (Abschnitt 7) je Bereich: Seitenleiste (ein-/ausklappen, Breite ziehen, Reiter), Werkzeugleiste bei schmalem Fenster (welche Elemente in welcher Reihenfolge ins Überlaufmenü wandern; Review Focus 1), Tabs (Überlauf, lange Namen, Schließen), Startbildschirm (was er zeigt; nur, was ohne PDFium und ohne Dateizugriff geht), Laden einer Seite (Platzhalter in Seitengröße, dann grobe Vorschau, dann scharf; keine Spinner), Fehler und Passwortabfrage, Fokus und Tastaturbedienung, Kontrastanforderungen (Review Focus 4), dunkles Theme mit heller Seite (Review Focus 3).
- [ ] **Step 4: Gewähltes Mockup ergänzen** um drei Zustände: schmales Fenster (ca. 680 px Fensterbreite), Passwortabfrage, Fehlermeldung „Datei beschädigt“. Erneut veröffentlichen (gleiche URL), Sichtprüfung wie Task 2 Step 5, Kontrast wie Task 2 Step 4.
- [ ] **Step 5: Abhängigkeiten festhalten:** je ein Eintrag in `Docs/DECISIONS.md` für Schrift und Icon-Satz (Lizenz, Version bzw. Commit, Herkunft, AOT-Tauglichkeit, Einbindung erst in M3, Startkosten der Schrift als Messpunkt M3). Verworfene Kandidaten mit Grund.
- [ ] **Step 6: Offene Punkte** (Abschnitt 9): nur Dinge, die erst M3 klären kann (Messungen, Plattformverhalten), je mit dem Prüfweg.
- [ ] **Step 7: Roadmap abhaken**, `generate`, `check` → Exit 0.
- [ ] **Step 8: Commit** `docs: Design-Spec mit Tokens, Layout und Verhalten (Milestone 2)`.

**Roadmap:** Aufgabe „Design-Spec unter `Docs/superpowers/specs/` mit Tokens …“ (Nachweis: Pfad und Abschnitte der Spec).

---

### Task 5: Schriftzug und Icon-Richtung

**Files:**
- Create: `Docs/design/mockups/schriftzug-icon.html`, `Docs/design/schriftzug.svg`, `Docs/design/icon-entwurf.svg`
- Modify: Design-Spec Abschnitt 8, `Docs/DECISIONS.md`, `Docs/roadmap.json`, `Docs/ROADMAP.md`

**Interfaces:**
- Consumes: Tokens aus Task 4 (Farben, Schrift).
- Produces: gewählter Schriftzug und Icon-Entwurf als SVG; Grundlage für die Icon-Dateien in M8.

- [ ] **Step 1: Artifact mit drei Varianten** von Schriftzug und App-Icon in der gewählten Richtung (Idee jeweils in einem Satz, z. B. Seite mit umgeknickter Ecke als Flügel). Je Variante: Icon bei 16, 24, 32, 48 und 256 px auf hellem und dunklem Grund, in einer angedeuteten Windows-Taskleiste und einem Linux-Dock, Schriftzug neben dem Icon. Schrift des Schriftzugs nur mit geprüfter Lizenz (OFL erlaubt Logos aus Glyphen-Pfaden).
- [ ] **Step 2: Prüfen:** Icon bei 16 px noch als Form erkennbar, unterscheidbar von Acrobat-, Chrome- und Edge-PDF-Icons (keine Ähnlichkeit zu fremden Marken), funktioniert ohne Farbe (einfarbige Variante für Taskleisten-Overlays).
- [ ] **Step 3: Wahl** per `AskUserQuestion` (Varianten 1–3, „Other“ für Änderungen); bei Änderungen überarbeiten und erneut fragen.
- [ ] **Step 4:** Gewählte Variante als `Docs/design/schriftzug.svg` und `Docs/design/icon-entwurf.svg` ablegen (Schriftzug in Pfade umgewandelt, keine Schriftabhängigkeit); Abschnitt 8 der Design-Spec (Idee, Farben, Mindestgröße, Schutzraum, was M8 daraus erzeugen muss: `.ico`, PNG-Größen, SVG für Linux); kurzer Eintrag in `Docs/DECISIONS.md`.
- [ ] **Step 5: Roadmap abhaken**, `generate`, `check` → Exit 0.
- [ ] **Step 6: Commit** `docs: Schriftzug und Icon-Richtung (Milestone 2)`.

**Roadmap:** Aufgabe „App-Name-Schriftzug und Icon-Richtung festlegen (Grundlage für M8)“ (Nachweis: Artifact-URL, SVG-Pfade, Datum der Wahl).

---

### Task 6: Abschluss

**Files:**
- Modify: Design-Spec (Status „abgestimmt“), `CLAUDE.md` (Abschnitt „Projektspezifisches“), `Docs/roadmap.json`, `Docs/ROADMAP.md`

- [ ] **Step 1:** Design-Spec beim Milestone verlinken (`"spec": "superpowers/specs/YYYY-MM-DD-designsystem.md"` im M2-Eintrag) und Status der Spec auf „abgestimmt“.
- [ ] **Step 2:** In `CLAUDE.md` unter „Projektspezifisches“ einen kurzen Abschnitt „Design“: Design-Spec ist verbindlich für alle UI-Arbeit, Tokens nur dort ändern (Änderung = Eintrag in `DECISIONS.md`), Mockup-Quellen unter `Docs/design/`.
- [ ] **Step 3:** Kriterium „Design-Spec ist committet und in der Roadmap verlinkt“ abhaken (Nachweis: Commit-Hash, `spec`-Feld), Status `done`, `completedOn`.
- [ ] **Step 4: Verifizieren:** `dotnet build Pagewing.slnx` (0 Warnungen, 0 Fehler), `dotnet test Pagewing.slnx` (alle grün), `dotnet format Pagewing.slnx --verify-no-changes` (Exit 0), `dotnet run tools/roadmap.cs -- check` (Exit 0). Ergebnisse notieren.
- [ ] **Step 5: Commit** `docs: Milestone 2 abgeschlossen`; Kein `CHANGELOG.md`-Eintrag (keine für Nutzer sichtbare Änderung).
- [ ] **Step 6: PR** nach `dev` mit Zusammenfassung (gewählte Richtung, Links, Tokens), nach grüner CI per Squash-Merge mergen.
