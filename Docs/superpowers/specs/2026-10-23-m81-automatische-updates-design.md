# Milestone 8.1: Automatische Updates (Entwurf)

Stand 2026-10-23, erarbeitet im Brainstorming mit dem Projektinhaber. Grundlage ist der Milestone 8.1 in [Docs/ROADMAP.md](../../ROADMAP.md) samt Scope-Änderung vom 2026-10-22. Dieser Entwurf legt fest, **was** gebaut wird und **warum**; das **wie** im Einzelnen klärt der Plan (`Docs/superpowers/plans/`).

## Absicht

Wer Pagewing hat, soll nie mehr einen Installer von Hand laden. Pagewing merkt im Hintergrund, dass es eine neue Version gibt, und installiert sie auf Knopfdruck. Danach läuft es mit denselben Tabs weiter.

Das wichtigste Merkmal von Pagewing bleibt die Geschwindigkeit. Die Prüfung darf Start und Öffnen einer PDF nicht messbar verlangsamen.

## Ziele

- Prüfung auf neue Versionen **von selbst** (Standard: an), im Hintergrund, erst nach dem Start.
- In den Einstellungen **abschaltbar**. Abgeschaltet gibt es keinen einzigen Netzwerkzugriff.
- Ein Klick auf „Installieren“ lädt, prüft und installiert das Update und startet Pagewing neu. Die Anzeige dazu ist ein **eigener Installer im Pagewing-Design** mit sichtbarem Fortschritt, nicht das rohe Inno-Setup-Fenster.
- Nach dem Update öffnen sich die **Tabs und Positionen von vorher** wieder.
- Jede Abweichung (falsche Signatur, falsche Prüfsumme, ältere Version) lehnt das Update ab, ohne dass etwas installiert oder gestartet wird.

## Nicht-Ziele und Gegenziele

- Keine Telemetrie, keine Konten, keine Online-Dienste. Netzwerk gibt es **allein** für die Update-Prüfung. Die Anfrage enthält keine Kennung und keine Angaben zum Rechner (feste Kennung des Programms, ohne Version und ohne Zufallswert).
- Kein stilles Installieren ohne Zutun: Installiert wird nur nach „Installieren“. Kein Update mitten in der Arbeit ohne Wunsch des Nutzers.
- Keine Code-Signierung des Programms (Backlog „Code-Signierung über SignPath Foundation“). Die Signatur dieses Milestones schützt nur den Update-Weg.
- Keine allgemeine „Sitzung wiederherstellen“-Funktion bei jedem Start. Die Sitzung wird nur für den Neustart nach dem Update gemerkt (siehe unten).
- Kein Hinweis-Spam: Ohne Netz oder bei Fehlern der Prüfung bleibt Pagewing still. Es gibt keine Fehlerkarte beim Start und kein Wiederholen in einer Schleife.
- Gegenziel (wäre ein Fehlschlag, auch wenn es technisch läuft): eine spürbar langsamere Startzeit, ein verlorener Tab oder ein halb installiertes Pagewing.

## Festgelegt (vom Projektinhaber)

1. **Anzeige:** Eine kleine Schaltfläche **„Update“ mit Icon** (Variante B der Mockups) sitzt in der Titelleiste rechts, links neben den Fensterknöpfen. Ein Klick öffnet eine nicht blockierende Karte „Pagewing 0.2.0 ist verfügbar“ mit „Installieren“ und „Später“. „Später“ blendet die Karte aus, die Schaltfläche bleibt.
2. **Installer-Fenster:** eigenes Fenster im Pagewing-Stil mit vier Schritten (Herunterladen, Prüfen, Installieren, Neustart), Fortschrittsbalken und Größenangabe; Schritte stehen im Text, der Fortschritt ist nie nur Farbe. Abbrechen ist bis zum Installieren möglich. Mockups: `.superpowers/brainstorm/…/content/update-ui.html` (lokal, nicht im Repo).
3. **Standard der Prüfung:** an, ohne Nachfrage beim ersten Start; Schalter in den Einstellungen.
4. **Linux AppImage:** Pagewing lädt die neue Datei, prüft sie, ersetzt die alte und startet neu, mit demselben Fortschrittsfenster. **`.deb`:** kein Austausch durch die App (das wäre Sache von `apt` mit Root-Rechten); die Karte zeigt nur „verfügbar“ mit Link zur Download-Seite.
5. **Sitzung:** Offene Tabs und Positionen bleiben beim Update erhalten. Dafür kommt der Backlog-Eintrag „Sitzung wiederherstellen“ in diesen Milestone, eingeschränkt auf den Neustart nach dem Update.

## Annahmen, vom Projektinhaber bestätigt

- Windows, Installation für alle Benutzer: UAC-Abfrage bleibt (nicht zu umgehen). Installation nur für den Benutzer: keine Abfrage.
- Die portable ZIP aktualisiert sich nicht selbst und zeigt nur einen Hinweis.
- Der Installer ist ein kleines eigenes Programm bzw. ein eigener Modus, weil Windows die laufende Datei nicht überschreiben kann: Er beendet die App, installiert mit Inno Setup im stillen Modus (feste `AppId`), zeigt den Fortschritt und startet Pagewing wieder.

## Meine Entscheidungen (Vorschläge, im Plan zu bestätigen)

- **Quelle:** GitHub Releases des öffentlichen Repos, ohne Token. Das Release trägt zusätzlich ein Manifest (Version, Dateinamen, Größe, SHA-256) und dessen Signatur.
- **Signaturverfahren:** ECDSA P-256 aus der .NET-Standardbibliothek (AOT-fähig, keine neue Abhängigkeit). Der private Schlüssel liegt nur als Secret des Repos, der öffentliche steckt im Programm. Der Plan prüft, ob Ed25519 in .NET 10 ohne Zusatzpaket geht; sonst bleibt es bei ECDSA.
- **Häufigkeit:** höchstens eine Prüfung in 24 Stunden, Zeitpunkt der letzten Prüfung in `settings.json`-Nachbarschaft gemerkt (nicht im Startpfad gelesen). Zusätzlich der Befehl „Nach Updates suchen“ im `CommandCatalog`, der sofort prüft.
- **Zeitlimit und Abbruch:** Die Prüfung ist abbrechbar und hat ein Zeitlimit (Größenordnung 10 s); der Download hat ein eigenes, großzügigeres.
- **Installer als Modus derselben Exe:** Pagewing kopiert sich samt Bibliotheken in einen temporären Ordner und startet dort mit einem Installer-Schalter (nutzt Theme und Avalonia der App, kein zweites Projekt mit eigener Skia-Kopie). Alternative im Plan abwägen, wenn das AOT-Binary oder die Größe es verlangen.
- **Zeitpunkt der Installation:** nur nach „Installieren“. Weil es bis M9 keine ungespeicherten Änderungen gibt, geht kein Inhalt verloren; M9 muss den Installationsweg um „ungespeicherte Änderungen“ ergänzen und trägt das als Voraussetzung in seinem Plan ein.

## Weitere Entscheidungen nach der Prüfung des Entwurfs (meine, im Plan zu bestätigen)

- **Update-Quelle und Schlüssel sind fest einkompiliert, kein Schalter im ausgelieferten Programm.** Kein Umgebungsvariablen- oder Kommandozeilen-Override (der vergrößerte die Angriffsfläche eines Programms, das mit Administratorrechten installieren kann). Für Tests gibt es einen **Testaufbau** mit eigenem Testschlüssel und einer Test-Quelle (lokaler Server mit Test-Manifest); der Schalter dafür ist eine Bauzeit-Eigenschaft, die der Release-Aufbau nie setzt. Folge: Es gibt kein öffentliches Testrelease, die Tag-Regex bleibt unverändert, und der echte Schlüssel kommt in keinem Testlauf vor. Der Ende-zu-Ende-Test läuft mit zwei lokal gebauten Versionen gegen den Testserver; die echte Kette von `v0.1.0` aus gibt es nicht (`v0.1.0` enthält noch keinen Updater), sie wird erst mit `v0.2.0` → `v0.3.0` belegt.
- **Schlüsselwechsel:** Das Programm kennt eine **Liste** erlaubter öffentlicher Schlüssel, das Manifest nennt die Kennung des verwendeten Schlüssels. Heute steht ein Schlüssel drin; ein Wechsel ist dann ein Release, das den neuen Schlüssel zusätzlich einbettet. Geht der Schlüssel verloren, müssen Nutzer einmal von Hand umsteigen; das steht in `Docs/DECISIONS.md`.
- **Merken über Neustarts:** Neben dem Zeitpunkt der letzten Prüfung wird die gefundene Version gemerkt (eine kleine Datei im Benutzerordner, erst nach dem ersten Frame gelesen). Ein Start innerhalb der 24 Stunden zeigt „Update“ also weiter an, auch nach „Später“. Eine fällige Prüfung läuft einmal je Start (nach dem ersten Frame) und auf Befehl; während einer langen Sitzung prüft Pagewing nicht erneut.
- **Fehlgeschlagene Installation:** Kein Rollback über Inno hinaus. Der Installer-Modus lädt und prüft **vor** dem Beenden der App; erst danach wird beendet. Lehnt der Nutzer UAC ab oder scheitert Inno, startet der Installer-Modus die vorhandene Version neu und zeigt eine Fehlermeldung mit „Erneut versuchen“ (keine stille Rückkehr). Ein Abbruch mitten im Kopieren (Rechnerabsturz) ist nicht abgedeckt; Inno ersetzt Dateien einzeln, und die nächste Installation repariert es. Der Plan beschreibt die Grenze in `Docs/DECISIONS.md`.
- **Paketform erkennen:** Windows-Installer: `unins000.exe` im Programmordner (Inno) → Installer; sonst portable ZIP (nur Hinweis). Linux: Umgebungsvariable `APPIMAGE` gesetzt → AppImage; Programmpfad unter `/usr/lib/pagewing` → `.deb` (nur Hinweis). Alles andere: nur Hinweis. Für „nur für mich“ oder „alle Benutzer“ übernimmt das Update den Modus der vorhandenen Installation (Inno tut das über die feste `AppId`); der Plan prüft das mit `installer-smoke`.
- **Sitzung:** Mehrere Fenster kommen wie vorher zurück (je Fenster seine Tabs). Passwortgeschützte PDFs öffnen und fragen wie sonst nach dem Passwort. Verschobene oder gelöschte Dateien werden still übersprungen. Wiederhergestellt wird **nur** nach einem Neustart durch den Updater (Schalter oder Datei, die der Updater schreibt und die Pagewing beim Start einmal verbraucht). Wiederhergestellte Dateien lösen keine Erst-Öffnen-Karten aus (Standardprogramm-Karte).
- **Welche Releases zählen:** Nur stabile Versionen über `releases/latest` (Vorabversionen und Entwürfe zählt GitHub dort nicht). Kein Kanal, keine „nicht über X hinaus“-Regel.
- **Reihenfolge im Release-Workflow:** Ein dritter Job nach den beiden Betriebssystem-Jobs erstellt und signiert das Manifest und lädt es hoch. Bis dahin gibt es keinen Hinweis, denn die Prüfung nimmt `releases/latest` erst dann ernst, wenn das Manifest da ist; fehlt es, bleibt Pagewing still. **Signiert wird automatisch in der CI** (Entscheidung des Projektinhabers, 2026-10-23: Empfehlung übernommen): Der private Schlüssel liegt als Secret des Repos und ist nur im Signier-Job lesbar, der nur bei einem Tag läuft, nie bei Pull Requests. Restrisiko für `DECISIONS`: Ein gekaperter eigener Runner könnte das Secret lesen; Gegenmittel ist der Schlüsselwechsel (siehe oben).

## Randbedingungen

- **Startpfad:** Nichts Neues vor dem ersten scharfen Seitenbild bzw. dem ersten Frame der Startseite. Kein Netzwerkcode im Startpfad geladen. Prüfung läuft auf einem Hintergrundthread. Budgets und `bench/startup` bleiben unverändert streng (nie lockern, siehe `CLAUDE.md`).
- **Tests und Messläufe:** greifen nie ins Netz (abgekoppelt wie `UserSettings.Detached()`). Tests mit Fake-Netzwerk; bei abgeschalteter Prüfung ein Handler, der jeden Aufruf als Fehler meldet.
- **Native AOT:** JSON nur mit Source-Generator, keine Reflection, AOT- und Trim-Warnungen sind Fehler.
- **Abhängigkeiten:** möglichst keine neuen. Kommt eine hinzu: Lizenzmanifest (`tools/licenses.cs`), Eintrag in `Docs/DECISIONS.md`.
- **Release-Workflow:** Manifest und Signatur als zusätzliche Release-Dateien; Tag-Regex und Dateinamen bleiben stabil (`packaging/README.md` anpassen).
- **Offline-Zusage nachziehen:** `Docs/DECISIONS.md` (löst „Keine Auto-Update-Funktion“ ab), `CLAUDE.md` („Offline und Sicherheit“ und Kopfzeile), `README.md`, Gesamt-Spec, Design-Spec, UI-Texte der Startseite und der Über-Karte (Englisch und Deutsch) mit Hinweis, was die Prüfung abfragt und was nicht.
- **Design:** neue Farben und Maße nur als Tokens der Design-Spec; Zustände nie nur über Farbe; Kontrast nach WCAG 2.2 AA; Tastatur und Screenreader (Automation-Namen, siehe `CLAUDE.md`, „Automation“).

## Voraussetzungen vor Umsetzung und Ende-zu-Ende-Test

- Das Repo `benjo-code/Pagewing` ist **öffentlich** (Stand heute: privat). Ohne das gibt es keine Prüfung ohne Token und keinen Ende-zu-Ende-Test. Planen und die meisten Tests (Fake-Netzwerk) gehen davor.
- Der Signaturschlüssel ist erzeugt und als Secret im Repo angelegt (durch den Projektinhaber, nie im Chat oder in Dateien).

## Dem Builder überlassen

Zuschnitt der Typen in `Pagewing.Core`, Dateiformat des Manifests, genaue Zustände und Texte, Aufbau des Installer-Modus, Gestaltung der Karte im Detail innerhalb der Design-Spec, Reihenfolge der Aufgaben, Form der Sitzungsdatei.

## Abnahme

Es gelten die Abnahmekriterien des Milestones in `Docs/ROADMAP.md` (Startzeit unverändert, Ablehnung manipulierter Updates, Verhalten ohne Netz, Abschalten, Ende-zu-Ende unter Windows und Linux, keine unterbrochene Arbeit, Lizenzen, Build/Test/Format).
