# PDFium – Drittlizenzen

Pagewing nutzt die vorkompilierten PDFium-Binaries von [bblanchon/pdfium-binaries](https://github.com/bblanchon/pdfium-binaries) über die NuGet-Pakete `bblanchon.PDFium.Win32` und `bblanchon.PDFium.Linux` (ohne V8, ohne XFA).

## Version

- NuGet-Pakete: `157.0.8100` (siehe `Directory.Packages.props`)
- `VERSION`-Datei der Release-Archive: `MAJOR=157`, `MINOR=0`, `BUILD=8100`, `PATCH=0`
- Release: `chromium/8100`

## Quelle

Die NuGet-Pakete enthalten selbst **keine** Lizenzdateien (nur Binaries, Import-Bibliotheken, Header und eine README). Die Lizenzen in diesem Ordner stammen deshalb aus den Release-Archiven derselben Version:

| Archiv | SHA-256 |
|---|---|
| `https://github.com/bblanchon/pdfium-binaries/releases/download/chromium%2F8100/pdfium-win-x64.tgz` | `51621bcc1d9be9ea448f4b84b0516862b928e3968d3fca899af1a6d6e2848d6d` |
| `https://github.com/bblanchon/pdfium-binaries/releases/download/chromium%2F8100/pdfium-linux-x64.tgz` | `1c1894a12d4d96ee01400c3874444126254180c5c7d2ab628576b67f8120320a` |

Übernommen sind die Datei `LICENSE` (Lizenz der Build-Skripte von Benoit Blanchon, MIT) und der Ordner `licenses/` (PDFium selbst und die mitgelinkten Bibliotheken wie FreeType, ICU, libjpeg-turbo, OpenJPEG, libpng, zlib, Little CMS, HarfBuzz, Abseil).

## Unterschiede zwischen den Archiven

Beide Archive enthalten dieselben Dateien mit demselben Inhalt. Sie unterscheiden sich nur in den Zeilenenden: Im Windows-Archiv haben einige Dateien CRLF (die `LICENSE` gemischt CRLF/LF), im Linux-Archiv durchgehend LF. Übernommen sind die Dateien aus dem Linux-Archiv, passend zur LF-Vorgabe des Repos (`.gitattributes`). `licenses/freetype.txt` ist in beiden Archiven ISO-8859-1-kodiert und bleibt unverändert.

Die nuspec der NuGet-Pakete gibt als Lizenz `Apache-2.0` an; die `LICENSE` der Archive ist MIT. Beide sind GPL-3.0-kompatibel.

## Build-Argumente

Aus `args.gn` beider Archive (relevant für Pagewing):

```
pdf_enable_v8 = false
pdf_enable_xfa = false
```

Damit läuft in PDFs kein JavaScript, und XFA-Formulare werden nicht unterstützt.

## Auslieferung

Die Auslieferung der Lizenzen im Installationspaket und ihre Anzeige im Über-Dialog folgen in Milestone 8.
