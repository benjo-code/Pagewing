# Test-PDFs: Herkunft und Lizenzen

Jede Datei in diesem Ordner steht genau einmal in einer der Tabellen unten (der Test `TestDataTests.AllListedPdfsExist` prüft das). Nur Dateien mit klarer Lizenz gehören hierher, nie private oder vertrauliche PDFs.

## Selbst erzeugt

Erzeugt von `tools/make-testpdfs.cs` (`dotnet run tools/make-testpdfs.cs -- generate`), Lizenz GPL-3.0-or-later wie das Repository. Die Dateien sind unkomprimiert, ohne Zeitstempel und byte-genau reproduzierbar; `dotnet run tools/make-testpdfs.cs -- check` (CI-Schritt „Check the test PDFs“) vergleicht sie mit der Generator-Ausgabe. Alle haben ein Info-Dictionary mit `Title` und `Producer` = `Pagewing test generator`. Seitengrößen in pt.

| Datei | Zweck | Erwartete Fakten |
| --- | --- | --- |
| `text-basic.pdf` | Text, Schriften, Umlaute, Metadaten | 2 Seiten je 300 × 200. Seite 1: Helvetica, Text `Hello, Pagewing`. Seite 2: Times-Roman, WinAnsiEncoding, Text `Größe äöü`. Title `Pagewing Textprobe: Größe` (UTF-16BE-String mit BOM). Textextraktion von PDFium liefert genau diese Texte |
| `vector-shapes.pdf` | Vektorgrafik | 1 Seite 300 × 300, kein Text. Rotes Rechteck mit blauem Rand, grüne Kurvenform, gestrichelte und gepunktete Linie, Kreis-Clipping mit diagonaler Linie, axiales Shading (gelb nach blau) |
| `image-rgb.pdf` | Bilder | 1 Seite 300 × 160, kein Text. RGB-Bild 64 × 64 (8 Bit, unkomprimiert) und ein Bild mit weicher Maske (`SMask`, radialer Alpha-Verlauf) über grauem Balken |
| `transparency.pdf` | Transparenz | 1 Seite 300 × 200, kein Text. Füll- und Strichdeckkraft (`ca`/`CA` 0.5) und Blend-Mode Multiply (gelb über cyan ergibt grün) |
| `rotated-page.pdf` | Seitendrehung | 1 Seite, MediaBox 200 × 100 mit `/Rotate 90`, PDFium meldet 100 × 200. Text `Rotated`, rotes Quadrat in der unrotierten Ecke unten links |
| `cropbox.pdf` | Zuschnitt | 1 Seite, MediaBox 300 × 300, CropBox `[50 75 250 225]`, PDFium meldet 200 × 150. Text `CropBox`. Die farbigen Eckquadrate liegen außerhalb des Zuschnitts |
| `annotations.pdf` | Anmerkungen | 1 Seite 300 × 200. Text `Highlighted text` und `Link area` (Extraktion: beide Zeilen). Annotationen: Square (mit Appearance-Stream), Highlight (mit Appearance-Stream, Multiply) und Link (URI `https://example.com/pagewing`, ohne Appearance-Stream); Render-Flag `FPDF_ANNOT` nötig |
| `wide-page.pdf` | Kacheltest | 1 Seite 600 × 120. Lineal-Striche alle 20 pt, Text `Pagewing wide page 600 x 120`, Farbverlauf (gelb nach blau) in der unteren Hälfte |
| `thousand-pages.pdf` | Geschwindigkeit, viele Seiten | 1000 Seiten je 200 × 100. Seite N enthält den Text `Page N` (zum Beispiel `Page 1`, `Page 1000`) |
| `not-a-pdf.pdf` | Fehlerfall: kein PDF | 18 Byte Text `This is not a PDF.`; PDFium meldet `FPDF_ERR_FORMAT` |
| `empty.pdf` | Fehlerfall: leere Datei | 0 Byte |

## Aus PDFium

Aus `testing/resources` des PDFium-Repositorys (<https://pdfium.googlesource.com/pdfium>), Lizenz BSD-3-Clause, Copyright 2014 The PDFium Authors, Lizenztext in [LICENSE-pdfium.txt](LICENSE-pdfium.txt). Unverändert übernommen am 2026-10-16 vom Stand `main` = Commit `bb72e525b77a2e9dbc3eff126490b969e79808b7`, abgerufen über `https://pdfium.googlesource.com/pdfium/+/refs/heads/main/testing/resources/<Datei>?format=TEXT`.

| Datei | Zweck | Erwartete Fakten |
| --- | --- | --- |
| `hello_world.pdf` | einfaches Referenzdokument | Pfad `testing/resources/hello_world.pdf`. 1 Seite 200 × 200, ohne Info-Dictionary. Text `Hello, world!` und `Goodbye, world!` (PDFium trennt die Zeilen mit `\r\n`) |
| `encrypted_hello_world_r3.pdf` | Verschlüsselung RC4 128 Bit (Revision 3) | Pfad `testing/resources/encrypted_hello_world_r3.pdf`. 1 Seite 200 × 200, Inhalt wie `hello_world.pdf`. Benutzer-Passwort `hôtel`, Eigentümer-Passwort `âge` (siehe unten) |
| `encrypted_hello_world_r6.pdf` | Verschlüsselung AES-256 (Revision 6) | Pfad `testing/resources/encrypted_hello_world_r6.pdf`. 1 Seite 200 × 200, Inhalt wie `hello_world.pdf`. Benutzer-Passwort `hôtel`, Eigentümer-Passwort `âge` (siehe unten) |

### Passwörter der verschlüsselten Dateien

Ermittelt mit einem Wegwerf-Test gegen die gebundene PDFium-Version (nicht eingecheckt) und gegen die Tests des PDFium-Repositorys (`core/fpdfapi/parser/cpdf_security_handler_embeddertest.cpp`, dort `kHotelUTF8` und `kAgeUTF8`). Die naheliegenden Kandidaten `1234`, `5678` und das leere Passwort (gelten für `encrypted.pdf` aus PDFium, nicht für diese Dateien) öffnen keine der beiden Dateien: `FPDF_GetLastError` liefert `FPDF_ERR_PASSWORD` (4).

| Datei | Benutzer-Passwort | Eigentümer-Passwort |
| --- | --- | --- |
| encrypted_hello_world_r3.pdf | `hôtel` | `âge` |
| encrypted_hello_world_r6.pdf | `hôtel` | `âge` |

- Beide Passwörter enthalten ein Nicht-ASCII-Zeichen (`ô` = U+00F4, `â` = U+00E2). Tests übergeben sie als UTF-8-Bytes (`68 C3 B4 74 65 6C` bzw. `C3 A2 67 65`); PDFium akzeptiert für Revision 3 auch Latin-1 (`68 F4 74 65 6C`, `E2 67 65`).
- Mit dem Benutzer-Passwort öffnen sich beide Dateien mit den Benutzer-Berechtigungen (Revision 3: `FPDF_GetDocPermissions` = `0xFFFFF0C0`), mit dem Eigentümer-Passwort mit allen Berechtigungen (`0xFFFFFFFC`). Bei Revision 6 meldet PDFium für beide `0xFFFFFFFC`, weil die Datei dort volle Berechtigungen setzt.
- Ohne Passwort (oder mit falschem) lässt sich keine der Dateien öffnen.
