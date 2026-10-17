# Geist und Geist Mono – Drittlizenz

Pagewing bettet die Schriften **Geist** (Schnitte 400, 500, 600) und **Geist Mono** (400, 500) als Teilmenge ein (`src/Pagewing.App/Assets/Fonts/`). Entscheidung und Vorbehalt (Startkosten) stehen in `Docs/DECISIONS.md`, Eintrag „Schrift: Geist und Geist Mono, als Teilmenge eingebettet“.

## Quelle und Lizenz

- Projekt: https://github.com/vercel/geist-font, Tag `v1.8.1` (Commit `e1f671c727f6de7e97c1e1d26dbb44f349c19459`)
- Lizenz: SIL Open Font License 1.1, Text in [OFL.txt](OFL.txt) (Copyright 2024 The Geist Project Authors). Die Schrift hat keinen Reserved Font Name, eine Teilmenge darf deshalb den Namen behalten.
- Ausgangsdateien (statische TTF): `fonts/Geist/ttf/Geist-{Regular,Medium,SemiBold}.ttf` und `fonts/GeistMono/ttf/GeistMono-{Regular,Medium}.ttf`
- Die Glyphen sind nicht verändert; die Teilmenge entfernt nur Zeichen und Layout-Merkmale, die Pagewing nicht braucht.

## Teilmenge

Zeichen: Basis-Latein (U+0020–007E), Latin-1 (U+00A0–00FF), Latin Extended-A (U+0100–017F), die typografischen Satzzeichen – — ‘ ’ ‚ “ ” „ … € (U+2013, 2014, 2018, 2019, 201A, 201C, 201D, 201E, 2026, 20AC) und ẞ (U+1E9E). Der Apostroph ’ (U+2019) ist gegenüber der Liste der Design-Spec ergänzt, weil er im Deutschen und Englischen häufig vorkommt.

Layout-Merkmale: `kern,liga,tnum,ccmp,locl,mark,mkmk` (vorhandene davon; Geist Mono hat kein `kern`, `tnum`, `ccmp`).

Befehl (fonttools 4.67.0 und brotli 1.2.0 in einer virtuellen Python-Umgebung; `<in>` ist die Ausgangsdatei, `<out>` die Zieldatei):

```
pyftsubset <in>.ttf \
  --unicodes=U+0020-007E,U+00A0-00FF,U+0100-017F,U+2013,U+2014,U+2018,U+2019,U+201A,U+201C,U+201D,U+201E,U+2026,U+20AC,U+1E9E \
  --layout-features=kern,liga,tnum,ccmp,locl,mark,mkmk \
  --name-IDs='*' --notdef-outline --glyph-names \
  --output-file=<out>.ttf
```

`--name-IDs='*'` behält alle Namenseinträge, darunter Copyright und Lizenzhinweis der Schrift. Die Tabelle `meta` (nur Metadaten zu Skripten) entfällt, `pyftsubset` meldet das als Warnung.

## Größen

| Datei | Original | Teilmenge |
|---|---|---|
| Geist-Regular.ttf | 126048 | 52336 |
| Geist-Medium.ttf | 127660 | 53352 |
| Geist-SemiBold.ttf | 127872 | 53356 |
| GeistMono-Regular.ttf | 149284 | 53712 |
| GeistMono-Medium.ttf | 150096 | 54180 |
