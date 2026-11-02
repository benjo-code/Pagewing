# Tesseract, Leptonica und tessdata_fast – Drittlizenzen

Pagewing liefert die OCR-Bibliothek (`tesseract.dll` bzw. `libtesseract.so`, Tesseract 5.5.4 mit statisch eingebundenem Leptonica 1.87.0) und die Sprachdaten `eng` und `deu` neben der Anwendung aus (Milestone 15; `packaging/native/`, `tools/package.cs`, Konstante `NativeTesseract`; Einträge „OCR: …“ in `Docs/DECISIONS.md`). Die Bibliothek entsteht im Workflow `.github/workflows/native-tesseract.yml` aus den unveränderten Quellen der unten genannten Tags (ohne OpenMP, ohne libcurl, ohne libarchive, ohne Bilddecoder).

| Datei | Komponente (Version) | Quelle | SHA-256 der Datei |
| --- | --- | --- | --- |
| `third-party/tesseract/LICENSE` | Tesseract OCR 5.5.4 (Apache-2.0) | `https://raw.githubusercontent.com/tesseract-ocr/tesseract/5.5.4/LICENSE` | `cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30` |
| `third-party/leptonica/LICENSE` | Leptonica 1.87.0 (BSD-2-Clause) | `https://raw.githubusercontent.com/DanBloomberg/leptonica/1.87.0/leptonica-license.txt` | `87829abb5bbb00b55a107365da89e9a33f86c4250169e5a1e5588505be7d5806` |
| `third-party/tessdata-fast/LICENSE` | tessdata_fast 4.1.0 (Apache-2.0) | `https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/4.1.0/LICENSE` | `cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30` |

Die Dateien sind unveränderte Kopien, am 2026-11-02 aus den Quellen geholt. Die Sprachdaten `eng.traineddata` und `deu.traineddata` stammen aus `https://github.com/tesseract-ocr/tessdata_fast` (Tag 4.1.0); ihre Prüfsummen stehen in `tools/package.cs` (`NativeTesseract`).
