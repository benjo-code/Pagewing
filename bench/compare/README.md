# Vergleichsmessung: Öffnen von PDFs in Acrobat, Chrome und SumatraPDF

Dieses Protokoll gehört zu Milestone 1 (Roadmap-Aufgabe „Vergleichsmessung Acrobat, Chrome, SumatraPDF …; Budgets festlegen“). Es legt fest, wie die Öffnungszeiten der Vergleichs-Viewer gemessen werden, damit daraus die endgültigen Geschwindigkeitsbudgets von Pagewing abgeleitet werden können. Dasselbe Werkzeug misst ab Milestone 3 Pagewing selbst.

Die Messung führt der Projektinhaber auf seinem Rechner durch (Acrobat, SumatraPDF und eine angemeldete Desktop-Sitzung sind nötig). Die Ergebnisse kommen als CSV nach `bench/compare/results/` und werden eingecheckt.

## Was gemessen wird

Das Werkzeug `tools/measure-open.cs` startet das Programm, beobachtet etwa alle 10 ms den Fensterinhalt und liefert je Lauf zwei Zeiten in Millisekunden, jeweils ab unmittelbar vor dem Prozessstart:

| Messgröße | Bedeutung |
|---|---|
| `t_first_content` („erstes Bild“) | Erster Frame, in dem der Seitenbereich nicht mehr dem leeren Anfangszustand entspricht und Seiteninhalt zeigt: ein Mindestanteil weißer Seitenfläche **und** mehrere dunkle Textpixel, die links und rechts (oder oben und unten) von Weiß umgeben sind. Damit zählen dunkle Fensterrahmen, Werkzeugleisten oder ein grauer Hintergrund nicht als Inhalt. |
| `t_visually_complete` („visuell fertig“) | Zeitpunkt der letzten sichtbaren Änderung im Seitenbereich vor 500 ms Stillstand (z. B. eine grobe Vorschau, die scharf wird). |

Beide Größen werden auf den **Seitenbereich** bezogen (mittlerer Teil des Fensters: 20–80 % der Breite, 12–92 % der Höhe). Tab-Leisten, Avatare und Ladeanimationen des Fensterrahmens ändern sich besonders bei Browsern noch lange und gehören nicht zum Öffnen der Seite.

Das Werkzeug kopiert den Fensterbereich **vom Bildschirm** (GDI `BitBlt` des Desktops in einen DIB-Abschnitt, im Speicher ausgewertet). `PrintWindow` liefert bei GPU-Fenstern schwarze Bilder, deshalb die Bildschirmkopie (dasselbe wie `Graphics.CopyFromScreen`, aber ohne `System.Drawing`). Es werden **keine Bilder gespeichert**, nur die Zeiten. Das Werkzeug läuft nur unter Windows und braucht keine Pakete.

Pro Lauf beendet es den gestarteten Prozessbaum (und alle neu entstandenen Prozesse gleichen Namens) und wartet `--pause-ms` (Standard 2000) bis zum nächsten Lauf.

## Fälle

Je Fall 10 Läufe. Jeder Lauf beginnt mit **nicht laufendem Programm**, außer in Fall (d).

| Fall | Datei | Zustand | Werkzeugoptionen |
|---|---|---|---|
| (a) Start | `testdata/pdfs/text-basic.pdf` (1 Seite Text) | Programm lief lange nicht (möglichst direkt nach Neustart und Anmeldung) | `--runs 10 --pause-ms 5000` |
| (b) Warmstart | `testdata/pdfs/text-basic.pdf` | unmittelbar nach Fall (a), Programm und Datei sind im Cache | `--runs 10 --warmup 3 --pause-ms 500` |
| (c) 1000 Seiten | `testdata/pdfs/thousand-pages.pdf` (1000 Seiten, 271 KB) | wie (b) | `--runs 10 --warmup 3 --pause-ms 500` |
| (d) Zweite Datei | `text-basic.pdf` zuerst, dann `hello_world.pdf` (`--second-pdf`) | Programm läuft schon, zweite Datei wird an die laufende Instanz übergeben | `--runs 10 --second-pdf testdata/pdfs/hello_world.pdf` |

Hinweise:

- Fall (a) ist nur dann ein echter Kaltstart, wenn das Programm zuvor lange nicht lief. Windows lässt sich ohne Zusatzwerkzeuge nicht dazu bringen, den Datei-Cache zu leeren. Am besten (a) als allererste Messung nach einem Neustart und der Anmeldung ausführen, danach Minuten Ruhe vor jedem weiteren Programm. Die CSV enthält alle Läufe; die Ausgabe zeigt Median und Statistik **auch ohne Lauf 1**, weil dieser der echte Kaltstart ist.
- Fall (d) gilt nur für Acrobat und SumatraPDF. Chrome hat keine „zweite Datei in der laufenden Instanz“, sondern öffnet einen **Tab** im selben Browser-Fenster. Das lässt sich mit demselben Schalter messen: Chrome ohne `--new-window` starten (siehe unten), dann nimmt das zweite Programm den Tab im Fenster der ersten Instanz. Die Auswertung führt Chrome in Fall (d) als „Tab öffnen“ getrennt von der „zweiten Datei in der laufenden Instanz“ von Acrobat und SumatraPDF.
- Bei (d) beziehen sich `t_first_content` und `t_visually_complete` auf den Start des **zweiten** Prozesses. Die Zeiten für die erste Datei stehen in den Spalten `first_file_*`. Als „Inhalt der zweiten Datei“ zählt erst ein Frame, in dem sich der Seitenbereich gegenüber dem fertigen Bild der ersten Datei geändert hat und Text zeigt; ein neuer Tab allein genügt nicht.
- Bei `--warmup N` laufen N Starts davor, die nicht in die CSV eingehen.

## Bedingungen am Rechner

- Der Bildschirm bleibt während der Läufe **unberührt**: keine Maus- oder Tastaturbedienung, kein Verschieben anderer Fenster. Das Terminal mit dem Messbefehl gehört auf einen zweiten Monitor oder wird nach dem Start nicht mehr berührt.
- Das Anwendungsfenster darf **nicht verdeckt** werden. Weil vom Bildschirm kopiert wird, würde ein darüberliegendes Fenster (zum Beispiel das Terminal selbst) mit gemessen. Ob das Fenster oben liegt, vorher mit `--trace` prüfen (siehe unten).
- Immer **derselbe Monitor und dieselbe Skalierung** (zum Beispiel 100 % oder 150 %, nicht zwischen Läufen wechseln). Die Fenstergröße richtet sich nach den Programmeinstellungen; sie sollte zwischen den Programmen ähnlich sein (Zielgröße: Fenster nicht maximiert, etwa 1280 × 800 Pixel).
- Energiesparplan **Höchstleistung** (Einstellungen → System → Netzbetrieb; bei Notebooks am Netzteil).
- Keine anderen aktiven Programme, keine Updates, kein Backup, keine Indexierung. Lang laufende Hintergrundprozesse vorher beenden.
- Virenscanner: Ein Echtzeitscanner (auch Windows Defender) verlängert Programmstarts, besonders den ersten. Nicht abschalten, aber **in der CSV-Begleitnotiz vermerken**, welcher Scanner aktiv war, und für alle Programme dieselbe Konfiguration lassen. Wer Ausschlüsse setzt, tut das für alle Programme gleich.
- Browser **ohne Erweiterungen und ohne Sitzungswiederherstellung**, mit eigenem Profilordner (siehe Befehle).
- Acrobat: Der „Geschützte Modus beim Start“ (Voreinstellungen → Sicherheit (erweitert)) verlangsamt den Start deutlich. Messung mit der **Standardeinstellung** des Nutzers; die Einstellung in der Begleitnotiz festhalten. Acrobat startet Hintergrunddienste (zum Beispiel Updater, Cloud-Sync), die nicht beendet werden. Läuft Acrobat beim Start der Messung schon, weigert sich das Werkzeug (Exit 2).
- Laufen Programme gleichen Namens bereits (zum Beispiel das eigene Chrome), bricht das Werkzeug mit Exit 2 ab, weil die Datei sonst an die laufende Instanz übergeben würde. Ausnahme: Browser mit eigenem `--user-data-dir` laufen als eigene Instanz; dann `--allow-running` angeben.

## Befehle

Alle Befehle im Repository-Stammordner ausführen. Der Rechnername gehört in den Dateinamen. Alle Messungen eines Tages kommen mit `--out` in **dieselbe** Datei: das Werkzeug hängt an eine vorhandene CSV mit gleichem Spaltenformat an; `--label` benennt das Programm, `--case` den Fall. Vor der ersten Messung des Tages die Datei nicht anlegen oder leeren, falls sie schon existiert.

Pfade können je nach Installation abweichen (die gezeigten sind typisch). Sie sind in PowerShell anzupassen:

```powershell
$out  = "bench/compare/results/$(Get-Date -Format yyyy-MM-dd)-$env:COMPUTERNAME.csv"
$tmp  = Join-Path $env:TEMP "pagewing-compare"   # muss ohne Leerzeichen sein, sonst anderen Ordner wählen
$one  = "testdata/pdfs/text-basic.pdf"
$many = "testdata/pdfs/thousand-pages.pdf"
$two  = "testdata/pdfs/hello_world.pdf"
$measure = "dotnet run tools/measure-open.cs --"
```

### Chrome

```powershell
$chrome = "C:\Program Files\Google\Chrome\Application\chrome.exe"
$chromeArgs = "--new-window --user-data-dir=$tmp\chrome --no-first-run --no-default-browser-check --disable-extensions --disable-session-crashed-bubble {pdfurl}"
# (a) Start; --warmup 1 legt das Profil an, das erste Profil-Erzeugen soll nicht mitzählen
Invoke-Expression "$measure --exe '$chrome' --args '$chromeArgs' --pdf $one --runs 10 --warmup 1 --pause-ms 5000 --allow-running --label chrome --case a-start --out $out"
# (b) Warmstart
Invoke-Expression "$measure --exe '$chrome' --args '$chromeArgs' --pdf $one --runs 10 --warmup 3 --pause-ms 500 --allow-running --label chrome --case b-warm --out $out"
# (c) 1000 Seiten
Invoke-Expression "$measure --exe '$chrome' --args '$chromeArgs' --pdf $many --runs 10 --warmup 3 --pause-ms 500 --allow-running --label chrome --case c-1000 --out $out"
# (d) zweite Datei als Tab: ohne --new-window, damit die zweite Datei im ersten Fenster landet
$chromeTab = "--user-data-dir=$tmp\chrome --no-first-run --no-default-browser-check --disable-extensions --disable-session-crashed-bubble {pdfurl}"
Invoke-Expression "$measure --exe '$chrome' --args '$chromeTab' --pdf $one --second-pdf $two --runs 10 --warmup 1 --allow-running --label chrome --case d-second-tab --out $out"
```

Begründung für einen **dauerhaften eigenen Profilordner** statt des normalen Profils oder eines bei jedem Lauf neu angelegten Ordners:

- Ein eigener `--user-data-dir` macht den Browser zur eigenen Instanz. Mit dem normalen Profil würde `chrome.exe` die Datei an ein bereits laufendes Chrome übergeben und sofort beenden; gemessen würde ein Tab, nicht der Start.
- Ein neuer Ordner je Lauf würde jedes Mal die einmalige Profilanlage mitmessen. Ein Nutzer startet Chrome fast immer mit vorhandenem Profil. Der erste Lauf (`--warmup 1`) legt das Profil an und zählt nicht.
- `--no-first-run --no-default-browser-check` verhindern Willkommensseiten und Dialoge, `--disable-extensions` Erweiterungen, `--disable-session-crashed-bubble` den Hinweis „Chrome wurde nicht ordnungsgemäß beendet“, der nach dem gewaltsamen Beenden erscheint. Eine Sitzungswiederherstellung gibt es in einem neuen Profil nicht (Startseite bleibt die übergebene Datei).
- Der PDF-Viewer von Chrome ist PDFium, dieselbe Bibliothek wie in Pagewing. Chrome ist deshalb die engste Vergleichsgröße.

`{pdfurl}` setzt das Werkzeug als `file:///C:/…`-URL ein (Leerzeichen als `%20`), `{pdf}` als Dateipfad (mit Anführungszeichen, wenn er Leerzeichen enthält). Optional lässt sich Edge genauso messen: `C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe` mit denselben Argumenten (eigener `--user-data-dir`).

### Adobe Acrobat

```powershell
$acrobat = "C:\Program Files\Adobe\Acrobat DC\Acrobat\Acrobat.exe"
# Reader: "C:\Program Files\Adobe\Acrobat DC\Reader\AcroRd32.exe" oder "C:\Program Files (x86)\Adobe\Acrobat Reader DC\Reader\AcroRd32.exe"
Invoke-Expression "$measure --exe '$acrobat' --args '{pdf}' --pdf $one --window-title-contains text-basic --runs 10 --pause-ms 5000 --label acrobat --case a-start --out $out"
Invoke-Expression "$measure --exe '$acrobat' --args '{pdf}' --pdf $one --window-title-contains text-basic --runs 10 --warmup 3 --pause-ms 500 --label acrobat --case b-warm --out $out"
Invoke-Expression "$measure --exe '$acrobat' --args '{pdf}' --pdf $many --window-title-contains thousand-pages --runs 10 --warmup 3 --pause-ms 500 --label acrobat --case c-1000 --out $out"
Invoke-Expression "$measure --exe '$acrobat' --args '{pdf}' --pdf $one --second-pdf $two --runs 10 --warmup 1 --label acrobat --case d-second --out $out"
```

`--window-title-contains` wählt das Fenster mit dem Dateinamen im Titel. Acrobat zeigt zuerst die Startseite (weiß mit Text), die sonst als Seiteninhalt gelten würde. Der Titel enthält den Dateinamen, sobald das Dokument geöffnet ist. Bei den Läufen mit zweiter Datei entfällt der Filter; das Werkzeug bleibt beim Fenster der ersten Datei. Ob der Titel bei der verwendeten Version wirklich den Dateinamen enthält, zuerst mit `--runs 1 --trace` prüfen.

### SumatraPDF

```powershell
$sumatra = "C:\Program Files\SumatraPDF\SumatraPDF.exe"
# portable Variante: Pfad zur entpackten SumatraPDF.exe
Invoke-Expression "$measure --exe '$sumatra' --args '{pdf}' --pdf $one --runs 10 --pause-ms 5000 --label sumatra --case a-start --out $out"
Invoke-Expression "$measure --exe '$sumatra' --args '{pdf}' --pdf $one --runs 10 --warmup 3 --pause-ms 500 --label sumatra --case b-warm --out $out"
Invoke-Expression "$measure --exe '$sumatra' --args '{pdf}' --pdf $many --runs 10 --warmup 3 --pause-ms 500 --label sumatra --case c-1000 --out $out"
Invoke-Expression "$measure --exe '$sumatra' --args '-reuse-instance {pdf}' --pdf $one --second-pdf $two --runs 10 --warmup 1 --label sumatra --case d-second --out $out"
```

SumatraPDF öffnet bei einem zweiten Aufruf standardmäßig ein neues Fenster oder Tab; `-reuse-instance` nutzt die laufende Instanz (Kommandozeilenoption von SumatraPDF, in der installierten Version prüfen). Einstellungen und Startseite von SumatraPDF unverändert lassen; es ist wichtig, dass **kein** „Sitzung wiederherstellen“ aktiv ist, sonst öffnet es zusätzlich alte Dateien.

### Vor der ersten Messung (je Programm)

1. `dotnet run tools/measure-open.cs -- --selftest` (Logik ohne Fenster).
2. Ein einzelner Probelauf mit Tracing, ohne Ergebnis zu behalten: `… --runs 1 --trace --out $env:TEMP\probe.csv`. Das Tracing schreibt je Frame eine Zeile (Zeit, Weißanteil, Textpixel, Inhalt erkannt, Dauer der Bildschirmkopie). Es muss gelten: Fenster liegt oben (Weißanteil und Text erscheinen), `content yes` kommt kurz vor oder zusammen mit dem sichtbaren Seitenbild.
3. Danach die Fälle der Reihe nach, Bildschirm unberührt lassen.

Exit-Code 0: Messung vollständig. 1: Fenster oder Seiteninhalt erschien nicht innerhalb von `--timeout-ms` (Standard 30 s); die bisherigen Läufe stehen in der CSV. 2: Aufruf falsch oder Messung ungeeignet (laufende Instanz).

## Auswertung und Budgetvorschlag

Die Auswertung macht Claude (Task 12) aus den CSV-Dateien; die endgültige Festlegung trifft der Projektinhaber.

Vorläufige Budgets (Roadmap „Geschwindigkeitsziele“):

| Messgröße | Vorläufiges Budget | Vergleichsfall |
|---|---|---|
| Kaltstart bis erste Seite scharf (1-seitig, AOT) | ≤ 400 ms | (a) |
| Warmstart bis erste Seite scharf | ≤ 200 ms | (b) |
| Weitere Datei in laufender Instanz bis erste Seite | ≤ 100 ms | (d) |
| 1000-seitige PDF öffnen bis erste Seite | ≤ 300 ms | (c) |

Regel für den Vorschlag je Messgröße:

1. Vorschlag = **Median des schnellsten Browsers (Chrome)**, nach oben auf 50 ms gerundet.
2. Der Vorschlag ist **höchstens das vorläufige Budget**. Budgets werden nie gelockert; ist der Vorschlag größer, bleibt das vorläufige Budget bestehen.
3. Acrobat und SumatraPDF dienen der Einordnung (wo steht Pagewing im Vergleich), nicht der Festlegung.
4. Gegenprobe: Der Vorschlag muss mindestens die in Task 10 gemessene Kernzeit (Öffnen + erste Seite rendern, `bench/Pagewing.Benchmarks`) **plus 50 ms** für den Fensterstart betragen. Liegt er darunter, ist er unrealistisch; dann legt der Ausführer dem Projektinhaber beide Zahlen vor (Kernzeit + 50 ms und den Browserwert), statt selbst zu entscheiden.
5. Die endgültige Festlegung trifft der Projektinhaber auf Rückfrage mit einem konkreten Vorschlag. Das Ergebnis kommt als Eintrag in `Docs/DECISIONS.md` und in die Roadmap „Geschwindigkeitsziele“.

Als Messgröße „bis erste Seite scharf“ gilt `t_visually_complete`; `t_first_content` zeigt, wann überhaupt etwas Lesbares erscheint (grobe Vorschau). Welche der beiden Größen ein Budget bindet, steht im Vorschlag; die Auswertung nennt beide Zahlen je Programm.

## Grenzen des Verfahrens

Das Verfahren ist grob und für den Vergleich mehrerer Programme unter gleichen Bedingungen gedacht, nicht für Millisekunden-genaue Absolutwerte.

- **Zeitauflösung.** Eine Bildschirmkopie kostet je nach Grafikstapel 10–80 ms. Das Werkzeug gibt den erreichten Bildabstand an (`frame_interval_ms` in der CSV und als „Zeitauflösung“ in der Ausgabe). Zielwert sind etwa 10 ms. Auf einem Testrechner mit virtualisiertem oder langsamem Grafiktreiber lag der Abstand bei etwa 70 ms. Werte unter dieser Auflösung sind nicht belastbar; der Median aus 10 Läufen glättet nur den Zufall, nicht die Granularität. Der Zeitstempel eines Frames ist die Mitte der Bildschirmkopie.
- **Splashscreens und Animationen.** Ein eigener Splash (Acrobat) oder ein Einblendeffekt des Fensters verzögert das erste Bild, gehört aber zum Nutzererlebnis. Das Werkzeug wählt das größte sichtbare Fenster des Prozesses (mindestens 400 × 300 Pixel); kleinere Splash-Fenster zählen nicht. Dauernde Animationen im Seitenbereich verhindern 500 ms Stillstand; nach 10 s ohne Stillstand endet der Lauf, `settled` ist `false`, und `t_visually_complete` ist die letzte Änderung.
- **Verdeckung.** Weil der Bildschirm kopiert wird, verfälscht jedes darüberliegende Fenster (Terminal, Benachrichtigungen, Mauszeiger-Tooltips) die Messung. Der Mauszeiger selbst wird nicht kopiert.
- **DPI.** Das Werkzeug arbeitet DPI-bewusst (Pixel des Bildschirms). Verschiedene Skalierungen verändern Fenstergröße und damit Renderzeiten; deshalb gleiche Skalierung für alle Programme.
- **Seitenerkennung ist heuristisch.** „Seitenfläche weiß“ und „dunkle Textpixel“ setzt hellen Papierhintergrund und dunklen Text voraus. Ein dunkles Farbschema der Anwendung mit weißer Seite funktioniert; eine Seite, die selbst dunkel dargestellt wird (Nachtmodus), nicht. Startseiten mit weißem Hintergrund und Text (Acrobat Home) lassen sich nur über `--window-title-contains` ausschließen.
- **Fenstererkennung.** Das Fenster wird über die Prozess-ID gesucht, bei Programmen mit mehreren Prozessen (Browser) zusätzlich über alle während des Laufs neu entstandenen Prozesse gleichen Namens. Der Fenstertitelfilter wählt unter mehreren Fenstern aus.
- **Kaltstart.** Der Datei-Cache lässt sich nicht zuverlässig leeren (siehe Fälle). Windows-Prefetch und SuperFetch machen den echten Kaltstart nur direkt nach dem Neustart nachvollziehbar.
- **Netzwerk.** Das Messwerkzeug selbst nutzt kein Netzwerk. Die Vergleichsprogramme dürfen eigene Hintergrundzugriffe haben (Updates, Telemetrie); sie beeinflussen die Messung und sind Teil des Vergleichs.

## Dateien

- `tools/measure-open.cs`: das Messwerkzeug (`--selftest` prüft die Auswertelogik ohne Fenster).
- `bench/compare/results/<JJJJ-MM-TT>-<rechner>.csv`: Ergebnisse der Messung, eingecheckt. Spalten: `label,case,run,t_first_content_ms,t_visually_complete_ms,window_found_ms,settled,frame_interval_ms,first_file_t_first_content_ms,first_file_t_visually_complete_ms`. Eine Begleitnotiz (Rechner, Windows-Version, Monitor und Skalierung, Programmversionen, Virenscanner, Geschützter Modus von Acrobat) gehört als Kommentar in den Commit oder in `bench/compare/results/<JJJJ-MM-TT>-<rechner>.md`.

## Später: dasselbe Werkzeug für Pagewing (M3)

Ab Milestone 3 gibt es in Pagewing den Schalter `--measure-startup` und das Skript `bench/startup` für den Startzeit-Check in der CI. Für Vergleiche mit den hier gemessenen Programmen auf demselben Rechner läuft dasselbe Werkzeug mit Pagewing als Ziel:

```powershell
Invoke-Expression "$measure --exe 'src\Pagewing.App\bin\Release\net10.0\win-x64\publish\Pagewing.App.exe' --args '{pdf}' --pdf $one --runs 10 --label pagewing --case a-start --out $out"
```

Dabei gelten dieselben Bedingungen und Fälle (a) bis (d). Die Werte aus diesem Werkzeug sind nicht dasselbe wie der CI-Startzeit-Check (der nach `--measure-startup` des Programms selbst misst), sondern die Außensicht eines Nutzers mit Bildschirm.
