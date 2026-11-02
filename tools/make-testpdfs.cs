// Generator for the self-made test PDFs in testdata/pdfs.
//   dotnet run tools/make-testpdfs.cs -- generate   writes the files
//   dotnet run tools/make-testpdfs.cs -- check      builds them in memory and compares byte by byte (exit 1 on a difference)
// The output is deterministic: no compression, no timestamps, no random ids, "\n" line endings.

using System.Globalization;
using System.Text;

return Cli.Run(args);

static class Cli
{
    public static int Run(string[] args)
    {
        var mode = args.Length == 1 ? args[0] : string.Empty;
        if (mode is not ("generate" or "check"))
        {
            Console.Error.WriteLine("Aufruf: dotnet run tools/make-testpdfs.cs -- <generate|check>");
            return 2;
        }

        var root = FindRepositoryRoot();
        if (root is null)
        {
            Console.Error.WriteLine("Pagewing.slnx nicht gefunden: bitte im Repository ausführen.");
            return 2;
        }

        var directory = Path.Combine(root, "testdata", "pdfs");
        var files = TestPdfs.Build();

        if (mode == "generate")
        {
            Directory.CreateDirectory(directory);
            foreach (var (name, bytes) in files)
            {
                File.WriteAllBytes(Path.Combine(directory, name), bytes);
            }

            Console.WriteLine($"{files.Count} Dateien nach {directory} geschrieben.");
            return 0;
        }

        var differing = new List<string>();
        foreach (var (name, bytes) in files)
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path))
            {
                differing.Add($"{name}: fehlt");
            }
            else if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            {
                differing.Add($"{name}: weicht von der Generator-Ausgabe ab");
            }
        }

        foreach (var message in differing)
        {
            Console.Error.WriteLine(message);
        }

        if (differing.Count > 0)
        {
            Console.Error.WriteLine("Test-PDFs veraltet: dotnet run tools/make-testpdfs.cs -- generate ausführen und die Dateien committen.");
            return 1;
        }

        Console.WriteLine($"{files.Count} Test-PDFs sind aktuell.");
        return 0;
    }

    private static string? FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Pagewing.slnx")))
            {
                return directory.FullName;
            }
        }

        return null;
    }
}

// All self-made files. Page sizes stay small (at most 300 x 400 pt), except text-rich (400 x 400), text-dense (612 x 792, where the text volume is the point), dark-sample (A4) and print-mixed (A4, A4 landscape, Letter, A5 with /Rotate 90).
static class TestPdfs
{
    private const string Helvetica = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";
    private const string TimesRoman = "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman /Encoding /WinAnsiEncoding >>";

    public static List<KeyValuePair<string, byte[]>> Build() =>
    [
        new("text-basic.pdf", TextBasic()),
        new("vector-shapes.pdf", VectorShapes()),
        new("image-rgb.pdf", ImageRgb()),
        new("transparency.pdf", Transparency()),
        new("rotated-page.pdf", RotatedPage()),
        new("cropbox.pdf", CropBox()),
        new("annotations.pdf", Annotations()),
        new("text-rich.pdf", TextRich()),
        new("text-dense.pdf", TextDense()),
        new("dark-sample.pdf", DarkSample()),
        new("print-mixed.pdf", PrintMixed()),
        new("wide-page.pdf", WidePage()),
        new("thousand-pages.pdf", ThousandPages()),
        new("outline.pdf", Outline()),
        new("outline-cycle.pdf", OutlineCycle()),
        new("pages-marked.pdf", PagesMarked()),
        new("signed-docmdp-none.pdf", SignedDocument(null)),
        new("signed-docmdp-1.pdf", SignedDocument(1)),
        new("signed-docmdp-2.pdf", SignedDocument(2)),
        new("signed-docmdp-3.pdf", SignedDocument(3)),
        new("linearized.pdf", Linearized()),
        new("annotations-foreign.pdf", AnnotationsForeign()),
        new("drawings-foreign.pdf", DrawingsForeign()),
        new("stamps-foreign.pdf", StampsForeign()),
        new("forms-basic.pdf", FormsBasic()),
        new("forms-calc.pdf", FormsCalc()),
        new("forms-xfa.pdf", FormsXfa()),
        new("forms-signed.pdf", FormsSigned()),
        new("forms-rotated.pdf", FormsRotated()),
        new("forms-hostile.pdf", FormsHostile()),
        new("redact-everywhere.pdf", RedactEverywhere()),
        new("redact-everywhere-nopattern.pdf", RedactEverywhere(withPattern: false)),
        new("redact-attachments.pdf", RedactAttachments()),
        new("redact-covered.pdf", RedactCovered()),
        new("redact-revisions.pdf", RedactRevisions()),
        new("redact-image.pdf", RedactImage()),
        new("redact-rotated.pdf", RedactRotated()),
        new("redact-fonts.pdf", RedactFonts()),
        new("redact-image-kinds.pdf", RedactImageKinds()),
        new("redact-control.pdf", RedactControl()),
        new("redact-ligature.pdf", RedactLigature()),
        new("redact-hidden.pdf", RedactHidden()),
        new("redact-paths.pdf", RedactPaths()),
        new("redact-structure.pdf", RedactStructure()),
        new("redact-objstm.pdf", RedactObjectStreams()),
        new("redact-patterns.pdf", RedactPatterns()),
        new("redact-image-blocks.pdf", RedactImageBlocks()),
        .. OcrScans.Files(),
        .. TextEditPdfs.Files(),
        new("not-a-pdf.pdf", Encoding.ASCII.GetBytes("This is not a PDF.")),
        new("empty.pdf", []),
    ];

    // Two pages: Helvetica "Hello, Pagewing" and Times-Roman "Größe äöü" (WinAnsi octal escapes).
    private static byte[] TextBasic()
    {
        var doc = new PdfDoc("Pagewing Textprobe: Größe");
        var fonts = $"<< /Font << /F1 {doc.Add(Helvetica)} 0 R /F2 {doc.Add(TimesRoman)} 0 R >> >>";
        doc.AddPage("/MediaBox [0 0 300 200]", fonts, """
            BT
            /F1 24 Tf
            20 120 Td
            (Hello, Pagewing) Tj
            ET
            0.2 0.4 0.8 RG 2 w
            20 105 m 280 105 l S
            """);
        doc.AddPage("/MediaBox [0 0 300 200]", fonts, """
            BT
            /F2 28 Tf
            20 120 Td
            (Gr\366\337e \344\366\374) Tj
            ET
            0.8 0.3 0.2 RG 2 w
            20 105 m 280 105 l S
            """);
        return doc.Build();
    }

    // Rectangles, curves, stroke and fill, dashes, clipping and an axial shading.
    private static byte[] VectorShapes()
    {
        var doc = new PdfDoc("Pagewing Vektorformen");
        const string resources = """
            << /Shading << /Sh1 << /ShadingType 2 /ColorSpace /DeviceRGB /Coords [160 0 280 0]
            /Function << /FunctionType 2 /Domain [0 1] /C0 [1 0.8 0] /C1 [0 0.3 1] /N 1 >> /Extend [true true] >> >> >>
            """;
        doc.AddPage("/MediaBox [0 0 300 300]", resources, """
            q
            1 0 0 rg 0 0 1 RG 4 w
            20 200 100 70 re B
            Q
            q
            0 0.6 0 rg 0.1 0.1 0.1 RG 2 w
            160 200 m 200 290 280 290 280 220 c 280 180 220 160 160 200 c h B
            Q
            q
            0 0 0 RG 3 w [10 5] 0 d
            20 170 m 280 170 l S
            [1 6] 0 d 1 J 4 w
            20 155 m 280 155 l S
            Q
            q
            125 75 m 125 102.6 102.6 125 75 125 c 47.4 125 25 102.6 25 75 c 25 47.4 47.4 25 75 25 c 102.6 25 125 47.4 125 75 c h W n
            0.9 0.5 0 rg 0 0 300 300 re f
            0 0 0 RG 6 w 0 0 m 300 300 l S
            Q
            q
            160 30 120 80 re W n
            /Sh1 sh
            Q
            """);
        return doc.Build();
    }

    // A 64 x 64 RGB image and one with a soft mask, on a grey bar.
    private static byte[] ImageRgb()
    {
        var doc = new PdfDoc("Pagewing Bilder");
        const int size = 64;
        var plain = new byte[size * size * 3];
        var masked = new byte[size * size * 3];
        var alpha = new byte[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var i = ((y * size) + x) * 3;
                plain[i] = (byte)(x * 4);
                plain[i + 1] = (byte)(y * 4);
                plain[i + 2] = (byte)((x + y) * 2);
                masked[i] = 230;
                masked[i + 1] = (byte)(80 + (x * 2));
                masked[i + 2] = 20;
                var distance = ((x - 32) * (x - 32)) + ((y - 32) * (y - 32));
                alpha[(y * size) + x] = distance >= 1024 ? (byte)0 : (byte)(255 - (distance * 255 / 1024));
            }
        }

        const string head = "/Type /XObject /Subtype /Image /Width 64 /Height 64 /BitsPerComponent 8";
        var plainId = doc.AddStream($"{head} /ColorSpace /DeviceRGB", plain);
        var maskId = doc.AddStream($"{head} /ColorSpace /DeviceGray", alpha);
        var maskedId = doc.AddStream($"{head} /ColorSpace /DeviceRGB /SMask {maskId} 0 R", masked);
        doc.AddPage("/MediaBox [0 0 300 160]", $"<< /XObject << /Im1 {plainId} 0 R /Im2 {maskedId} 0 R >> >>", """
            q 128 0 0 128 15 16 cm /Im1 Do Q
            0.7 0.7 0.7 rg 150 16 140 64 re f
            q 128 0 0 128 157 16 cm /Im2 Do Q
            """);
        return doc.Build();
    }

    // Fill and stroke opacity (ca, CA) and the Multiply blend mode.
    private static byte[] Transparency()
    {
        var doc = new PdfDoc("Pagewing Transparenz");
        const string resources = "<< /ExtGState << /GS1 << /ca 0.5 /CA 0.5 >> /GSM << /BM /Multiply >> >> >>";
        doc.AddPage("/MediaBox [0 0 300 200]", resources, """
            1 0 0 rg 20 80 100 100 re f
            q
            /GS1 gs
            0 0 1 rg 70 40 100 100 re f
            0 0.6 0 RG 12 w 30 30 m 160 170 l S
            Q
            0 1 1 rg 190 90 90 90 re f
            q
            /GSM gs
            1 1 0 rg 230 50 50 90 re f
            Q
            """);
        return doc.Build();
    }

    // MediaBox 200 x 100 with /Rotate 90; the red square marks the unrotated lower-left corner.
    private static byte[] RotatedPage()
    {
        var doc = new PdfDoc("Pagewing gedrehte Seite");
        doc.AddPage("/MediaBox [0 0 200 100] /Rotate 90", "<< /Font << /F1 " + doc.Add(Helvetica) + " 0 R >> >>", """
            1 0 0 rg 0 0 20 20 re f
            BT
            /F1 18 Tf
            30 40 Td
            (Rotated) Tj
            ET
            """);
        return doc.Build();
    }

    // Four pages of different formats for printing: A4, A4 landscape, Letter and A5 turned by /Rotate 90 (shown as 595 x 420).
    // Each page has a 1 pt frame on its edge (cutting shows), a diagonal and a text line naming it.
    private static byte[] PrintMixed()
    {
        var doc = new PdfDoc("Pagewing Druckprobe");
        var resources = "<< /Font << /F1 " + doc.Add(Helvetica) + " 0 R >> >>";
        AddFramedPage(doc, resources, "/MediaBox [0 0 595 842]", 595, 842, "Seite 1 A4 hoch");
        AddFramedPage(doc, resources, "/MediaBox [0 0 842 595]", 842, 595, "Seite 2 A4 quer");
        AddFramedPage(doc, resources, "/MediaBox [0 0 612 792]", 612, 792, "Seite 3 Letter hoch");
        AddFramedPage(doc, resources, "/MediaBox [0 0 420 595] /Rotate 90", 420, 595, "Seite 4 A5 gedreht");
        return doc.Build();
    }

    private static void AddFramedPage(PdfDoc doc, string resources, string pageEntries, int width, int height, string label)
    {
        var content = string.Create(CultureInfo.InvariantCulture, $"""
            0 0 0 RG 1 w
            0.5 0.5 {width - 1} {height - 1} re S
            0.6 0.6 0.6 RG 0 0 m {width} {height} l S
            BT
            /F1 24 Tf
            40 {height - 60} Td
            ({label}) Tj
            ET
            """);
        doc.AddPage(pageEntries, resources, content);
    }

    // MediaBox 300 x 300 with a CropBox of 200 x 150; rendering must show only the crop area.
    private static byte[] CropBox()
    {
        var doc = new PdfDoc("Pagewing Zuschnitt");
        doc.AddPage("/MediaBox [0 0 300 300] /CropBox [50 75 250 225]", "<< /Font << /F1 " + doc.Add(Helvetica) + " 0 R >> >>", """
            0.9 0.9 0.6 rg 0 0 300 300 re f
            1 0 0 rg 0 0 40 40 re f
            0 0.6 0 rg 260 0 40 40 re f
            0 0 1 rg 0 260 40 40 re f
            0 0 0 rg 260 260 40 40 re f
            0 0 0 RG 2 w 50 75 200 150 re S
            0.4 0.4 0.4 RG 0 0 m 300 300 l S 0 300 m 300 0 l S
            BT
            /F1 20 Tf
            100 145 Td
            (CropBox) Tj
            ET
            """);
        return doc.Build();
    }

    // Square and Highlight annotations with appearance streams, plus a URI link.
    private static byte[] Annotations()
    {
        var doc = new PdfDoc("Pagewing Anmerkungen");
        var square = doc.AddStream(
            "/Type /XObject /Subtype /Form /BBox [30 30 130 90]",
            Text("1 0 0 RG 0.9 0.9 1 rg 3 w 31.5 31.5 97 57 re B"));
        var highlight = doc.AddStream(
            "/Type /XObject /Subtype /Form /BBox [28 114 190 140] /Resources << /ExtGState << /GS << /BM /Multiply >> >> >>",
            Text("/GS gs 1 1 0 rg 28 114 162 26 re f"));
        var squareAnnot = doc.Add($"<< /Type /Annot /Subtype /Square /Rect [30 30 130 90] /C [1 0 0] /IC [0.9 0.9 1] /BS << /W 3 >> /F 4 /Contents (Square annotation) /AP << /N {square} 0 R >> >>");
        var highlightAnnot = doc.Add($"<< /Type /Annot /Subtype /Highlight /Rect [28 114 190 140] /QuadPoints [28 140 190 140 28 114 190 114] /C [1 1 0] /F 4 /Contents (Highlight annotation) /AP << /N {highlight} 0 R >> >>");
        var linkAnnot = doc.Add("<< /Type /Annot /Subtype /Link /Rect [160 30 270 60] /Border [0 0 1] /C [0 0 1] /F 4 /A << /S /URI /URI (https://example.com/pagewing) >> >>");
        doc.AddPage($"/MediaBox [0 0 300 200] /Annots [{squareAnnot} 0 R {highlightAnnot} 0 R {linkAnnot} 0 R]", "<< /Font << /F1 " + doc.Add(Helvetica) + " 0 R >> >>", """
            BT
            /F1 20 Tf
            30 120 Td
            (Highlighted text) Tj
            ET
            BT
            /F1 14 Tf
            170 40 Td
            (Link area) Tj
            ET
            """);
        return doc.Build();
    }

    // Two pages of 400 x 400: headings, a hyphenated paragraph, font changes inside a line, a plain URL and a "www" address,
    // a URI link and a GoTo link (/XYZ destination on page 2). Helvetica, Helvetica-Bold and Helvetica-Oblique.
    private static byte[] TextRich()
    {
        const string bold = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>";
        const string oblique = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Oblique /Encoding /WinAnsiEncoding >>";
        var doc = new PdfDoc("Pagewing Handbuch");
        var fonts = $"<< /Font << /F1 {doc.Add(Helvetica)} 0 R /F2 {doc.Add(bold)} 0 R /F3 {doc.Add(oblique)} 0 R >> >>";
        var pageTwo = doc.Reserve();
        var uriLink = doc.Add("<< /Type /Annot /Subtype /Link /Rect [40 156 140 172] /Border [0 0 0] /F 4 /A << /S /URI /URI (https://example.org/docs) >> >>");
        var gotoLink = doc.Add($"<< /Type /Annot /Subtype /Link /Rect [40 126 170 142] /Border [0 0 0] /F 4 /A << /S /GoTo /D [{pageTwo} 0 R /XYZ 0 150 null] >> >>");
        doc.AddPage($"/MediaBox [0 0 400 400] /Annots [{uriLink} 0 R {gotoLink} 0 R]", fonts, """
            BT /F2 24 Tf 40 350 Td (Pagewing Handbuch) Tj ET
            BT /F1 12 Tf 40 310 Td (Pagewing ist ein schneller PDF-Viewer für Win-) Tj ET
            BT /F1 12 Tf 40 294 Td (dows und Linux. Er startet sofort und bleibt) Tj ET
            BT /F1 12 Tf 40 278 Td (auch bei großen Dokumenten flüssig.) Tj ET
            BT 40 246 Td /F1 12 Tf (Das ist ) Tj /F2 12 Tf (fett) Tj /F1 12 Tf ( und ) Tj /F3 12 Tf (kursiv) Tj /F1 12 Tf ( im Satz.) Tj ET
            BT /F1 12 Tf 40 230 Td (Zweite Zeile des zweiten Absatzes.) Tj ET
            BT /F1 12 Tf 40 190 Td (Mehr unter https://example.org/pagewing oder www.example.org.) Tj ET
            BT /F1 12 Tf 40 160 Td (Dokumentation) Tj ET
            BT /F1 12 Tf 40 130 Td (Weiter zu Seite zwei) Tj ET
            """);
        doc.AddPage("/MediaBox [0 0 400 400]", fonts, """
            BT /F2 24 Tf 40 350 Td (Seite zwei) Tj ET
            BT /F1 12 Tf 40 150 Td (Ziel: Pagewing am Ende.) Tj ET
            """, pageTwo);
        return doc.Build();
    }

    // One A4 page for the dark page mode: black body text, a table with grey fills and thin rules, a bar chart of four
    // coloured rectangles (red, green, blue, orange) with thin axes, a 64 x 64 RGB gradient image and a 64 x 64 grey image.
    private static byte[] DarkSample()
    {
        var doc = new PdfDoc("Pagewing Testseite für die dunkle Seite");
        const int size = 64;
        var rgb = new byte[size * size * 3];
        var grey = new byte[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var i = ((y * size) + x) * 3;
                rgb[i] = (byte)(x * 4);
                rgb[i + 1] = (byte)(y * 4);
                rgb[i + 2] = (byte)(255 - ((x + y) * 2));
                grey[(y * size) + x] = (byte)(((x + y) * 255) / ((size - 1) * 2));
            }
        }

        const string head = "/Type /XObject /Subtype /Image /Width 64 /Height 64 /BitsPerComponent 8";
        var rgbId = doc.AddStream($"{head} /ColorSpace /DeviceRGB", rgb);
        var greyId = doc.AddStream($"{head} /ColorSpace /DeviceGray", grey);
        var bold = doc.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        var resources = $"<< /Font << /F1 {doc.Add(Helvetica)} 0 R /F2 {bold} 0 R >> /XObject << /ImRgb {rgbId} 0 R /ImGrey {greyId} 0 R >> >>";
        doc.AddPage("/MediaBox [0 0 595 842]", resources, """
            0 g
            BT /F2 22 Tf 50 780 Td (Dunkle Seite: Testseite) Tj ET
            BT /F1 11 Tf 14 TL 50 750 Td
            (Dieser Absatz ist schwarzer Fliesstext auf weissem Grund. In der dunklen Seite wird er hell auf dunkel) Tj T*
            (gezeichnet, waehrend Bilder und farbige Diagramme ihre Farben behalten sollen.) Tj ET
            0.85 g 50 640 160 24 re f
            0.93 g 50 616 160 24 re f
            0.85 g 50 592 160 24 re f
            0.93 g 50 568 160 24 re f
            0.4 G 0.5 w
            50 568 160 96 re S
            50 640 m 210 640 l S 50 616 m 210 616 l S 50 592 m 210 592 l S
            130 568 m 130 664 l S
            0 g
            BT /F2 11 Tf 58 647 Td (Name) Tj 80 0 Td (Wert) Tj ET
            BT /F1 11 Tf 58 623 Td (Alpha) Tj 80 0 Td (12) Tj ET
            BT /F1 11 Tf 58 599 Td (Beta) Tj 80 0 Td (34) Tj ET
            BT /F1 11 Tf 58 575 Td (Gamma) Tj 80 0 Td (56) Tj ET
            1 0 0 rg 300 520 40 90 re f
            0 0.7 0 rg 350 520 40 140 re f
            0 0 1 rg 400 520 40 60 re f
            1 0.55 0 rg 450 520 40 110 re f
            0 g 0 G 0.5 w
            290 510 m 500 510 l S
            290 510 m 290 680 l S
            BT /F1 9 Tf 295 495 Td (Rot) Tj 50 0 Td (Gruen) Tj 50 0 Td (Blau) Tj 50 0 Td (Orange) Tj ET
            q 128 0 0 128 50 330 cm /ImRgb Do Q
            q 128 0 0 128 220 330 cm /ImGrey Do Q
            BT /F1 9 Tf 50 315 Td (RGB-Verlauf) Tj 170 0 Td (Graustufen) Tj ET
            """);
        return doc.Build();
    }

    // 200 pages of 612 x 792, 45 lines of eight words each; every tenth page has "Needle in Seite <p>" as line 7 (0-based).
    private static byte[] TextDense()
    {
        string[] words =
        [
            "alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta", "iota", "kappa",
            "lambda", "mu", "nu", "xi", "omicron", "rho", "sigma", "tau", "upsilon", "phi",
        ];
        var doc = new PdfDoc("Pagewing dichter Text");
        var resources = "<< /Font << /F1 " + doc.Add(Helvetica) + " 0 R >> >>";
        for (var p = 1; p <= 200; p++)
        {
            var content = new StringBuilder("BT /F1 10 Tf 40 760 Td 14 TL\n");
            for (var r = 0; r < 45; r++)
            {
                if (r > 0)
                {
                    content.Append("T*\n");
                }

                if (r == 7 && p % 10 == 0)
                {
                    content.Append(CultureInfo.InvariantCulture, $"(Needle in Seite {p}) Tj\n");
                    continue;
                }

                var line = string.Join(' ', Enumerable.Range(0, 8).Select(k => words[((p * 7) + (r * 3) + k) % 20]));
                content.Append(CultureInfo.InvariantCulture, $"({line}) Tj\n");
            }

            content.Append("ET");
            doc.AddPage("/MediaBox [0 0 612 792]", resources, content.ToString());
        }

        return doc.Build();
    }

    // 600 x 120 pt: gradient bar, ruler lines every 20 pt and a text line; used by the tile test.
    private static byte[] WidePage()
    {
        var doc = new PdfDoc("Pagewing breite Seite");
        var resources = "<< /Font << /F1 " + doc.Add(Helvetica) + " 0 R >> /Shading << /Sh1 << /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 600 0] "
            + "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 0.8 0] /C1 [0 0.4 1] /N 1 >> /Extend [true true] >> >> >>";
        var content = new StringBuilder();
        content.Append("q 0 0 600 50 re W n /Sh1 sh Q\n");
        content.Append("0 0 0 RG 1 w\n");
        for (var i = 0; i <= 30; i++)
        {
            var x = i * 20;
            var top = i % 5 == 0 ? 120 : 105;
            content.Append(CultureInfo.InvariantCulture, $"{x} 60 m {x} {top} l S\n");
        }

        content.Append("BT /F1 20 Tf 200 70 Td (Pagewing wide page 600 x 120) Tj ET\n");
        doc.AddPage("/MediaBox [0 0 600 120]", resources, content.ToString());
        return doc.Build();
    }

    // 1000 small pages; page N contains the text "Page N".
    private static byte[] ThousandPages()
    {
        var doc = new PdfDoc("Pagewing tausend Seiten");
        var resources = "<< /Font << /F1 " + doc.Add(Helvetica) + " 0 R >> >>";
        for (var n = 1; n <= 1000; n++)
        {
            doc.AddPage("/MediaBox [0 0 200 100]", resources, string.Create(CultureInfo.InvariantCulture, $"BT /F1 24 Tf 20 40 Td (Page {n}) Tj ET"));
        }

        return doc.Build();
    }

    // Three pages of 300 x 400 without text; the bookmarks carry the meaning:
    //   Kapitel 1 (open, /Dest page 1)
    //     Abschnitt 1.1 (/Dest page 2)
    //     Abschnitt 1.2 (no destination)
    //   Kapitel 2 (closed, GoTo action to page 3)
    //     "Über uns – 日本" (/Dest page 3, UTF-16BE title)
    //   Fern (/Dest with page index 99, which does not exist)
    private static byte[] Outline()
    {
        var doc = new PdfDoc("Pagewing Lesezeichen");
        var pages = ThreeEmptyPages(doc);
        var root = doc.Reserve();
        var chapter1 = doc.Reserve();
        var section11 = doc.Reserve();
        var section12 = doc.Reserve();
        var chapter2 = doc.Reserve();
        var about = doc.Reserve();
        var far = doc.Reserve();
        doc.Set(root, $"<< /Type /Outlines /First {chapter1} 0 R /Last {far} 0 R /Count 5 >>");
        doc.Set(chapter1, $"<< /Title (Kapitel 1) /Parent {root} 0 R /Next {chapter2} 0 R /First {section11} 0 R /Last {section12} 0 R /Count 2 /Dest [{pages[0]} 0 R /Fit] >>");
        doc.Set(section11, $"<< /Title (Abschnitt 1.1) /Parent {chapter1} 0 R /Next {section12} 0 R /Dest [{pages[1]} 0 R /Fit] >>");
        doc.Set(section12, $"<< /Title (Abschnitt 1.2) /Parent {chapter1} 0 R /Prev {section11} 0 R >>");
        doc.Set(chapter2, $"<< /Title (Kapitel 2) /Parent {root} 0 R /Prev {chapter1} 0 R /Next {far} 0 R /First {about} 0 R /Last {about} 0 R /Count -1 /A << /S /GoTo /D [{pages[2]} 0 R /Fit] >> >>");
        doc.Set(about, $"<< /Title {PdfDoc.TextString("Über uns – 日本")} /Parent {chapter2} 0 R /Dest [{pages[2]} 0 R /Fit] >>");
        doc.Set(far, $"<< /Title (Fern) /Parent {root} 0 R /Prev {chapter2} 0 R /Dest [99 /Fit] >>");
        doc.CatalogEntries = $"/Outlines {root} 0 R /PageMode /UseOutlines";
        return doc.Build();
    }

    // Three sibling bookmarks "Eins", "Zwei", "Drei"; the /Next of the last one points back to the first.
    private static byte[] OutlineCycle()
    {
        var doc = new PdfDoc("Pagewing Lesezeichen-Zyklus");
        var pages = ThreeEmptyPages(doc);
        var root = doc.Reserve();
        var first = doc.Reserve();
        var second = doc.Reserve();
        var third = doc.Reserve();
        doc.Set(root, $"<< /Type /Outlines /First {first} 0 R /Last {third} 0 R /Count 3 >>");
        doc.Set(first, $"<< /Title (Eins) /Parent {root} 0 R /Next {second} 0 R /Dest [{pages[0]} 0 R /Fit] >>");
        doc.Set(second, $"<< /Title (Zwei) /Parent {root} 0 R /Prev {first} 0 R /Next {third} 0 R /Dest [{pages[1]} 0 R /Fit] >>");
        doc.Set(third, $"<< /Title (Drei) /Parent {root} 0 R /Prev {second} 0 R /Next {first} 0 R /Dest [{pages[2]} 0 R /Fit] >>");
        doc.CatalogEntries = $"/Outlines {root} 0 R /PageMode /UseOutlines";
        return doc.Build();
    }

    // Six pages that carry their number as large text "PAGE n" in the middle (order and content can be checked with the text),
    // page 3 in landscape (400 x 300, the others 300 x 400), page 5 with /Rotate 90. Outline: Kapitel 1 (page 1) with the child
    // Abschnitt 1.1 (page 2), Kapitel 2 (page 3), Kapitel 3 (page 5). /PageLabels: roman for pages 1 and 2, decimal from page 3
    // on (starting at 3). Title in the info dictionary. Subject of milestone 13 (organise pages).
    private static byte[] PagesMarked()
    {
        var doc = new PdfDoc("Pagewing Seiten markiert");
        var font = "<< /Font << /F1 " + doc.Add(Helvetica) + " 0 R >> >>";
        var ids = new int[6];
        for (var i = 0; i < ids.Length; i++)
        {
            ids[i] = doc.Reserve();
        }

        for (var i = 0; i < ids.Length; i++)
        {
            var landscape = i == 2;
            var width = landscape ? 400 : 300;
            var height = landscape ? 300 : 400;
            var entries = $"/MediaBox [0 0 {width} {height}]" + (i == 4 ? " /Rotate 90" : string.Empty);
            // Helvetica 48 pt: "PAGE n" is 6 characters, about 134 pt wide; start at x = width / 2 - 67, y = height / 2 - 17.
            var content = string.Create(CultureInfo.InvariantCulture, $"BT /F1 48 Tf {width / 2 - 67} {height / 2 - 17} Td (PAGE {i + 1}) Tj ET");
            doc.AddPage(entries, font, content, ids[i]);
        }

        var root = doc.Reserve();
        var chapter1 = doc.Reserve();
        var section11 = doc.Reserve();
        var chapter2 = doc.Reserve();
        var chapter3 = doc.Reserve();
        doc.Set(root, $"<< /Type /Outlines /First {chapter1} 0 R /Last {chapter3} 0 R /Count 4 >>");
        doc.Set(chapter1, $"<< /Title (Kapitel 1) /Parent {root} 0 R /Next {chapter2} 0 R /First {section11} 0 R /Last {section11} 0 R /Count 1 /Dest [{ids[0]} 0 R /Fit] >>");
        doc.Set(section11, $"<< /Title (Abschnitt 1.1) /Parent {chapter1} 0 R /Dest [{ids[1]} 0 R /Fit] >>");
        doc.Set(chapter2, $"<< /Title (Kapitel 2) /Parent {root} 0 R /Prev {chapter1} 0 R /Next {chapter3} 0 R /Dest [{ids[2]} 0 R /Fit] >>");
        doc.Set(chapter3, $"<< /Title (Kapitel 3) /Parent {root} 0 R /Prev {chapter2} 0 R /Dest [{ids[4]} 0 R /Fit] >>");
        doc.CatalogEntries = $"/Outlines {root} 0 R /PageMode /UseOutlines /PageLabels << /Nums [0 << /S /r >> 2 << /S /D /St 3 >>] >>";
        return doc.Build();
    }

    // One page 300 x 200 with a signature field (an invisible widget) whose value is a STRUCTURE DUMMY of a signature:
    // /ByteRange and /Contents are placeholders (no real cryptography, no certificate; Pagewing never validates one).
    // docMdpLevel 1, 2 or 3 adds a /Reference entry with the DocMDP transform (/P = level) and the catalog's /Perms;
    // null signs without any DocMDP.
    private static byte[] SignedDocument(int? docMdpLevel)
    {
        var doc = new PdfDoc(docMdpLevel is { } level ? $"Pagewing Signaturattrappe DocMDP {level}" : "Pagewing Signaturattrappe ohne DocMDP");
        var page = doc.Reserve();
        var reference = docMdpLevel is { } p
            ? string.Create(CultureInfo.InvariantCulture, $" /Reference [<< /Type /SigRef /TransformMethod /DocMDP /TransformParams << /Type /TransformParams /P {p} /V /1.2 >> >>]")
            : string.Empty;
        var signature = doc.Add(
            "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange [0 0 0 0] /Contents <" + new string('0', 512) + "> "
            + "/Name (Pagewing test dummy) /M (D:20260101000000Z) /Reason (Structure dummy, not a signature)" + reference + " >>");
        var widget = doc.Add($"<< /Type /Annot /Subtype /Widget /FT /Sig /T (Signature1) /Rect [0 0 0 0] /F 132 /V {signature} 0 R /P {page} 0 R >>");
        doc.AddPage(
            $"/MediaBox [0 0 300 200] /Annots [{widget} 0 R]",
            "<< /Font << /F1 " + doc.Add(Helvetica) + " 0 R >> >>",
            "BT /F1 18 Tf 30 110 Td (Signed document) Tj ET\nBT /F1 12 Tf 30 85 Td (Structure dummy, no real signature.) Tj ET",
            page);
        doc.CatalogEntries = $"/AcroForm << /Fields [{widget} 0 R] /SigFlags 3 >>" + (docMdpLevel is null ? string.Empty : $" /Perms << /DocMDP {signature} 0 R >>");
        return doc.Build();
    }

    // Two pages 300 x 200 with a /Linearized dictionary as the first object of the file. The file is not optimised
    // for the web (objects keep their ids, no hint stream); the dictionary only has to look like the one of a linearized file.
    // /L is the real file length (all numbers have a fixed width, so the length does not depend on them).
    private static byte[] Linearized()
    {
        var doc = new PdfDoc("Pagewing linearisierte Datei");
        var linearization = doc.Reserve();
        doc.FirstObject = linearization;
        var fonts = "<< /Font << /F1 " + doc.Add(Helvetica) + " 0 R >> >>";
        var first = doc.AddPage("/MediaBox [0 0 300 200]", fonts, "BT /F1 24 Tf 30 100 Td (Linearized, page one) Tj ET");
        doc.AddPage("/MediaBox [0 0 300 200]", fonts, "BT /F1 24 Tf 30 100 Td (Linearized, page two) Tj ET");

        string Dictionary(int length) => string.Create(
            CultureInfo.InvariantCulture,
            $"<< /Linearized 1 /L {length:D10} /H [{0:D10} {0:D10}] /O {first} /E {0:D10} /N {2} /T {0:D10} >>");

        doc.Set(linearization, Dictionary(0));
        doc.Set(linearization, Dictionary(doc.Build().Length));
        return doc.Build();
    }

    // Annotations as other programs write them. Page 1 (400 x 300): a Highlight with /Popup (an indirect object), /RC, /Contents, /NM,
    // /T, /C, /QuadPoints and an appearance stream; a reply (Text with /IRT on that Highlight); a Highlight without /NM and /AP;
    // an Ink; a FreeText; a Text note; a Link. Page 2: an Underline and a StrikeOut. Every annotation line has text to select.
    private static byte[] AnnotationsForeign()
    {
        var doc = new PdfDoc("Pagewing fremde Anmerkungen");
        var resources = "<< /Font << /F1 " + doc.Add(Helvetica) + " 0 R >> >>";
        const string date = "/M (D:20260101120000Z) /CreationDate (D:20260101120000Z)";

        var highlightAppearance = doc.AddStream(
            "/Type /XObject /Subtype /Form /BBox [38 246 250 264] /Resources << /ExtGState << /GS << /BM /Multiply >> >> >>",
            Text("/GS gs 1 0.9 0 rg 38 246 212 18 re f"));
        var inkAppearance = doc.AddStream(
            "/Type /XObject /Subtype /Form /BBox [38 160 200 185]",
            Text("1 0 0 RG 2 w 40 165 m 80 180 l 120 165 l 160 180 l 195 165 l S"));
        var freeTextAppearance = doc.AddStream(
            "/Type /XObject /Subtype /Form /BBox [38 110 200 140]",
            Text("0 0 1 RG 1 w 38.5 110.5 161 29 re S"));

        var highlight = doc.Reserve();
        var popup = doc.Add($"<< /Type /Annot /Subtype /Popup /Rect [260 200 380 260] /Parent {highlight} 0 R /Open false >>");
        doc.Set(highlight, $"<< /Type /Annot /Subtype /Highlight /Rect [38 246 250 264] /QuadPoints [38 264 250 264 38 246 250 246] /C [1 0.9 0] /F 4 "
            + $"/NM (hl-0001) /T (Alice) /Subj (Highlight) /Contents (Check this sentence) "
            + "/RC (<?xml version=\"1.0\"?><body xmlns=\"http://www.w3.org/1999/xhtml\"><p>Check <b>this</b> sentence</p></body>) "
            + $"/Popup {popup} 0 R {date} /AP << /N {highlightAppearance} 0 R >> >>");
        var reply = doc.Add(
            $"<< /Type /Annot /Subtype /Text /Rect [280 246 298 264] /Name /Comment /F 28 /NM (reply-0001) /T (Bob) /Contents (Agreed) /IRT {highlight} 0 R /RT /R /C [1 0.9 0] {date} >>");
        var bareHighlight = doc.Add("<< /Type /Annot /Subtype /Highlight /Rect [38 216 230 234] /QuadPoints [38 234 230 234 38 216 230 216] /C [0.6 1 0.6] /F 4 /T (Alice) /Contents (No name, no appearance) >>");
        var ink = doc.Add($"<< /Type /Annot /Subtype /Ink /Rect [38 160 200 185] /InkList [[40 165 80 180 120 165 160 180 195 165]] /C [1 0 0] /BS << /W 2 >> /F 4 /NM (ink-0001) /T (Alice) {date} /AP << /N {inkAppearance} 0 R >> >>");
        var freeText = doc.Add($"<< /Type /Annot /Subtype /FreeText /Rect [38 110 200 140] /DA (/Helv 12 Tf 0 0 1 rg) /Q 0 /F 4 /NM (ft-0001) /T (Alice) /Contents (Free text note) {date} /AP << /N {freeTextAppearance} 0 R >> >>");
        var note = doc.Add($"<< /Type /Annot /Subtype /Text /Rect [220 110 238 128] /Name /Note /F 28 /NM (note-0001) /T (Alice) /Contents (Sticky note) /C [1 1 0] {date} >>");
        var link = doc.Add("<< /Type /Annot /Subtype /Link /Rect [38 60 200 80] /Border [0 0 1] /C [0 0 1] /F 4 /A << /S /URI /URI (https://example.com/pagewing) >> >>");
        doc.AddPage(
            $"/MediaBox [0 0 400 300] /Annots [{highlight} 0 R {popup} 0 R {reply} 0 R {bareHighlight} 0 R {ink} 0 R {freeText} 0 R {note} 0 R {link} 0 R]",
            resources,
            """
            BT /F1 14 Tf 40 250 Td (Pagewing marks this sentence.) Tj ET
            BT /F1 14 Tf 40 220 Td (Second mark without a name.) Tj ET
            BT /F1 14 Tf 40 190 Td (Ink is drawn below.) Tj ET
            BT /F1 14 Tf 40 120 Td (Free text) Tj ET
            BT /F1 14 Tf 40 65 Td (Link to Pagewing) Tj ET
            """);

        var underlineAppearance = doc.AddStream(
            "/Type /XObject /Subtype /Form /BBox [38 246 250 264]",
            Text("0 0.6 0 RG 1 w 38 247 m 250 247 l S"));
        var strikeAppearance = doc.AddStream(
            "/Type /XObject /Subtype /Form /BBox [38 216 250 234]",
            Text("1 0 0 RG 1 w 38 225 m 250 225 l S"));
        var underline = doc.Add($"<< /Type /Annot /Subtype /Underline /Rect [38 246 250 264] /QuadPoints [38 264 250 264 38 246 250 246] /C [0 0.6 0] /F 4 /NM (ul-0001) /T (Alice) /Contents (Underlined) {date} /AP << /N {underlineAppearance} 0 R >> >>");
        var strike = doc.Add($"<< /Type /Annot /Subtype /StrikeOut /Rect [38 216 250 234] /QuadPoints [38 234 250 234 38 216 250 216] /C [1 0 0] /F 4 /NM (so-0001) /T (Alice) /Contents (Struck out) {date} /AP << /N {strikeAppearance} 0 R >> >>");
        doc.AddPage(
            $"/MediaBox [0 0 400 300] /Annots [{underline} 0 R {strike} 0 R]",
            resources,
            """
            BT /F1 14 Tf 40 250 Td (This text is underlined.) Tj ET
            BT /F1 14 Tf 40 220 Td (This text is struck out.) Tj ET
            """);
        return doc.Build();
    }

    // Drawings as other programs write them. Page 1 (400 x 300), eight listed entries in this order: an Ink with two strokes and its
    // own appearance; an Ink without /AP; a Square with a dashed /BS; a Square with a cloud /BE; a Square with its own /AP and /CA 0.5;
    // a solid Circle without /AP; a Line with /LE (arrow at the end); an Ink with a reply (a Text with /IRT on it, the ninth raw entry,
    // never listed). Page 2: /Rotate 90 with a CropBox that is offset inside the MediaBox, one Ink with an appearance. No entry carries
    // /PDFIUM_HasGeneratedAP: these are foreign files.
    private static byte[] DrawingsForeign()
    {
        var doc = new PdfDoc("Pagewing fremde Zeichnungen");
        var resources = "<< /Font << /F1 " + doc.Add(Helvetica) + " 0 R >> >>";
        const string date = "/M (D:20260101120000Z) /CreationDate (D:20260101120000Z)";

        var inkAppearance = doc.AddStream(
            "/Type /XObject /Subtype /Form /BBox [28 238 192 282]",
            Text("1 0 0 RG 2 w 1 J 1 j 30 240 m 60 280 l 90 240 l S 110 240 m 150 280 l 190 240 l S"));
        var squareAppearance = doc.AddStream(
            "/Type /XObject /Subtype /Form /BBox [38 38 118 98] /Resources << /ExtGState << /GS << /CA 0.5 /ca 0.5 >> >> >>",
            Text("/GS gs 0 0.5 0 RG 4 w 40 40 76 56 re S"));
        var replyInk = "/InkList [[250 60 270 90 290 60 310 90 330 60]] /Rect [248 58 332 92]";

        var annots = new List<int>
        {
            doc.Add($"<< /Type /Annot /Subtype /Ink /Rect [28 238 192 282] /InkList [[30 240 60 280 90 240] [110 240 150 280 190 240]] /C [1 0 0] /BS << /W 2 >> /F 4 /NM (ink-0001) /T (Alice) {date} /AP << /N {inkAppearance} 0 R >> >>"),
            doc.Add($"<< /Type /Annot /Subtype /Ink /Rect [218 238 372 282] /InkList [[220 240 250 280 280 240 310 280 370 240]] /C [0 0 1] /BS << /W 3 >> /F 4 /NM (ink-0002) /T (Alice) {date} >>"),
            doc.Add($"<< /Type /Annot /Subtype /Square /Rect [28 158 108 218] /C [0 0.5 0] /BS << /W 2 /S /D /D [4 2] >> /F 4 /NM (square-0003) /T (Alice) {date} >>"),
            doc.Add($"<< /Type /Annot /Subtype /Square /Rect [128 158 208 218] /C [0.5 0 0.5] /BS << /W 2 /S /S >> /BE << /S /C /I 1 >> /F 4 /NM (square-0004) /T (Alice) {date} >>"),
            doc.Add($"<< /Type /Annot /Subtype /Square /Rect [38 38 118 98] /C [0 0.5 0] /BS << /W 4 >> /CA 0.5 /F 4 /NM (square-0005) /T (Alice) {date} /AP << /N {squareAppearance} 0 R >> >>"),
            doc.Add($"<< /Type /Annot /Subtype /Circle /Rect [228 158 308 218] /C [1 0.5 0] /BS << /W 2 /S /S >> /F 4 /NM (circle-0006) /T (Alice) {date} >>"),
            doc.Add($"<< /Type /Annot /Subtype /Line /Rect [128 78 208 118] /L [130 80 206 116] /LE [/None /OpenArrow] /C [0 0 0] /BS << /W 2 >> /F 4 /NM (line-0007) /T (Alice) {date} >>"),
        };
        var inkWithReply = doc.Reserve();
        doc.Set(inkWithReply, $"<< /Type /Annot /Subtype /Ink {replyInk} /C [0 0.6 0.6] /BS << /W 2 >> /F 4 /NM (ink-0008) /T (Alice) {date} >>");
        annots.Add(inkWithReply);
        annots.Add(doc.Add($"<< /Type /Annot /Subtype /Text /Rect [336 58 354 76] /Name /Comment /F 28 /NM (reply-0008) /T (Bob) /Contents (Nice stroke) /IRT {inkWithReply} 0 R /RT /R /C [1 0.9 0] {date} >>"));
        doc.AddPage(
            $"/MediaBox [0 0 400 300] /Annots [{string.Join(' ', annots.Select(a => $"{a} 0 R"))}]",
            resources,
            "BT /F1 14 Tf 40 20 Td (Foreign drawings, page one) Tj ET");

        var rotatedAppearance = doc.AddStream(
            "/Type /XObject /Subtype /Form /BBox [88 118 192 182]",
            Text("0.8 0 0.8 RG 2 w 90 120 m 120 180 l 150 120 l 190 180 l S"));
        var rotatedInk = doc.Add($"<< /Type /Annot /Subtype /Ink /Rect [88 118 192 182] /InkList [[90 120 120 180 150 120 190 180]] /C [0.8 0 0.8] /BS << /W 2 >> /F 4 /NM (ink-0009) /T (Alice) {date} /AP << /N {rotatedAppearance} 0 R >> >>");
        doc.AddPage(
            $"/MediaBox [0 0 400 300] /CropBox [20 10 380 290] /Rotate 90 /Annots [{rotatedInk} 0 R]",
            resources,
            "BT /F1 14 Tf 40 250 Td (Foreign drawings, page two) Tj ET");
        return doc.Build();
    }

    // Stamps and an ink written by other programs (milestone 12): a stamp with a text appearance, a stamp with an image appearance
    // but without the Pagewing mark, a stamp without appearance, and an ink with the subject "Signature" but three strokes (not the
    // shape of an own signature). All are listed, none is an own signature.
    private static byte[] StampsForeign()
    {
        var doc = new PdfDoc("Pagewing fremde Stempel");
        var resources = "<< /Font << /F1 " + doc.Add(Helvetica) + " 0 R >> >>";
        const string date = "/M (D:20260101120000Z) /CreationDate (D:20260101120000Z)";

        var textAppearance = doc.AddStream(
            "/Type /XObject /Subtype /Form /BBox [0 0 100 30] /Resources << /Font << /F1 " + doc.Add(Helvetica) + " 0 R >> >>",
            Text("1 0 0 RG 2 w 1 1 98 28 re S BT /F1 14 Tf 1 0 0 rg 12 10 Td (APPROVED) Tj ET"));
        var pixels = new byte[4 * 4 * 3];
        for (var i = 0; i < pixels.Length; i += 3)
        {
            pixels[i] = (byte)(i * 5);
            pixels[i + 1] = 40;
            pixels[i + 2] = 160;
        }

        var image = doc.AddStream("/Type /XObject /Subtype /Image /Width 4 /Height 4 /BitsPerComponent 8 /ColorSpace /DeviceRGB", pixels);
        var imageAppearance = doc.AddStream(
            "/Type /XObject /Subtype /Form /BBox [0 0 80 40] /Resources << /XObject << /Im1 " + image + " 0 R >> >>",
            Text("q 80 0 0 40 0 0 cm /Im1 Do Q"));

        var annots = new[]
        {
            doc.Add($"<< /Type /Annot /Subtype /Stamp /Rect [20 150 120 180] /Name /Approved /F 4 /NM (stamp-0001) /T (Alice) {date} /AP << /N {textAppearance} 0 R >> >>"),
            doc.Add($"<< /Type /Annot /Subtype /Stamp /Rect [20 90 100 130] /F 4 /NM (stamp-0002) /T (Alice) {date} /AP << /N {imageAppearance} 0 R >> >>"),
            doc.Add($"<< /Type /Annot /Subtype /Stamp /Rect [140 90 220 130] /Name /Draft /F 4 /NM (stamp-0003) /T (Alice) {date} >>"),
            doc.Add($"<< /Type /Annot /Subtype /Ink /Rect [138 28 282 72] /InkList [[140 30 170 70 200 30] [210 30 230 70] [240 30 280 70]] /C [0 0 0] /BS << /W 2 >> /Subj (Signature) /F 4 /NM (ink-0004) /T (Alice) {date} >>"),
        };
        doc.AddPage(
            $"/MediaBox [0 0 300 200] /Annots [{string.Join(' ', annots.Select(a => $"{a} 0 R"))}]",
            resources,
            "BT /F1 12 Tf 20 20 Td (Foreign stamps) Tj ET");
        return doc.Build();
    }

    // ---- Forms (milestone 11). Fields omit /AP on purpose where PDFium's engine writes it on the first change; check boxes and radio
    // buttons carry /AP /N states (their value is the state name). Every field has /DA (/Helv) and the AcroForm /DR has /Helv. ----

    private const string FormLook = "/F 4 /MK << /BC [0 0 0] /BG [1 1 1] >>";
    private const string FormDa = "/DA (/Helv 10 Tf 0 g)";

    private static string AcroFormEntry(PdfDoc doc, IEnumerable<int> fields, string extra = "") =>
        $"/AcroForm << /Fields [{AnnotRefs(fields)}] /DA (/Helv 10 Tf 0 g) /DR << /Font << /Helv {doc.Add(Helvetica)} 0 R >> >> {extra} >>";

    private static string AnnotRefs(IEnumerable<int> ids) => string.Join(' ', ids.Select(a => $"{a} 0 R"));

    private static string FormRect(int left, int bottom, int right, int top) => $"[{left} {bottom} {right} {top}]";

    // A merged field/widget (text, choice, ...) on a page.
    private static int FormField(PdfDoc doc, int page, string rect, string entries) =>
        doc.Add($"<< /Type /Annot /Subtype /Widget /Rect {rect} /P {page} 0 R {FormLook} {entries} >>");

    // The shared appearance streams of 18 x 18 pt boxes: off, check mark, radio dot.
    private static (int Off, int Check, int Dot) ButtonAppearances(PdfDoc doc)
    {
        const string box = "0 G 0.5 w 0.5 0.5 17 17 re S";
        const string head = "/Type /XObject /Subtype /Form /BBox [0 0 18 18]";
        return (
            doc.AddStream(head, Text(box)),
            doc.AddStream(head, Text(box + " 1.5 w 4 4 m 14 14 l 4 14 m 14 4 l S")),
            doc.AddStream(head, Text(box + " 0 g 5 5 8 8 re f")));
    }

    // A check box or radio button widget with the states /<on> and /Off; entries carry /FT, /T or /Parent and the value.
    private static int ButtonWidget(PdfDoc doc, int page, string rect, string entries, string on, int onStream, int offStream) =>
        doc.Add($"<< /Type /Annot /Subtype /Widget /Rect {rect} /P {page} 0 R {FormLook} {entries} /AS /Off /AP << /N << /{on} {onStream} 0 R /Off {offStream} 0 R >> >> >>");

    private static void FormLabel(StringBuilder content, int x, int y, string text) =>
        content.Append(CultureInfo.InvariantCulture, $"BT /F1 9 Tf {x} {y} Td ({text}) Tj ET\n");

    // Page 1 (400 x 640), 17 widgets in this /Annots order: Name (text, value "Alt", first widget of a field with two widgets), Bemerkung (multiline),
    // Kamm (comb, /MaxLen 8), Kurz (/MaxLen 5), Gesperrt (read-only), Pflicht (required), Passwort, Agb (check box, export Yes), Zustimmung (check box,
    // export Ja), Zahlung Bar and Karte (radio group of three, no /NoToggleToOff), Stufe 1 and 2 (radio group with /NoToggleToOff), Farbe (combo with
    // /Opt pairs), Frei (editable combo), Liste (list), Mehrfach (multi-select list). Page 2 (400 x 300), 4 widgets: Zahlung Rechnung (the third
    // widget of the group), the second widget of Name, Telefon, Ort. Tab order across the pages is the page order and /Annots order.
    private static byte[] FormsBasic()
    {
        var doc = new PdfDoc("Pagewing Formularprobe");
        var resources = "<< /Font << /F1 " + doc.Add(Helvetica) + " 0 R >> >>";
        var (off, check, dot) = ButtonAppearances(doc);
        var page1 = doc.Reserve();
        var page2 = doc.Reserve();
        var content1 = new StringBuilder("BT /F1 12 Tf 20 618 Td (Form fixture, page one) Tj ET\n");
        var content2 = new StringBuilder("BT /F1 12 Tf 20 270 Td (Form fixture, page two) Tj ET\n");
        var annots1 = new List<int>();
        var fields = new List<int>();

        var y = 600;
        (int Bottom, int Top) Row(string label, int height)
        {
            var top = y;
            FormLabel(content1, 20, top - 13, label);
            y -= height + 10;
            return (top - height, top);
        }

        // Name: one field with two widgets (page 1 and page 2); value and default "Alt".
        var name = doc.Reserve();
        var (bottom, top) = Row("Name", 22);
        var nameFirst = doc.Add($"<< /Type /Annot /Subtype /Widget /Rect {FormRect(150, bottom, 380, top)} /P {page1} 0 R {FormLook} /Parent {name} 0 R >>");
        annots1.Add(nameFirst);

        void Field(string label, int height, string entries)
        {
            var (fieldBottom, fieldTop) = Row(label, height);
            var id = FormField(doc, page1, FormRect(150, fieldBottom, 380, fieldTop), entries);
            annots1.Add(id);
            fields.Add(id);
        }

        Field("Bemerkung", 52, $"/FT /Tx /Ff 4096 /T (Bemerkung) {FormDa}");
        Field("Kamm", 22, $"/FT /Tx /Ff 16777216 /MaxLen 8 /T (Kamm) {FormDa}");
        Field("Kurz", 22, $"/FT /Tx /MaxLen 5 /T (Kurz) {FormDa}");
        Field("Gesperrt", 22, $"/FT /Tx /Ff 1 /T (Gesperrt) /V (Fest) {FormDa}");
        Field("Pflicht", 22, $"/FT /Tx /Ff 2 /T (Pflicht) {FormDa}");
        Field("Passwort", 22, $"/FT /Tx /Ff 8192 /T (Passwort) {FormDa}");

        (bottom, top) = Row("Agb / Zustimmung", 18);
        var agree = ButtonWidget(doc, page1, FormRect(150, bottom, 168, top), "/FT /Btn /T (Agb) /V /Off", "Yes", check, off);
        var consent = ButtonWidget(doc, page1, FormRect(190, bottom, 208, top), "/FT /Btn /T (Zustimmung) /V /Off", "Ja", check, off);
        annots1.AddRange([agree, consent]);
        fields.AddRange([agree, consent]);

        var payment = doc.Reserve();
        (bottom, top) = Row("Zahlung", 18);
        var bar = ButtonWidget(doc, page1, FormRect(150, bottom, 168, top), $"/Parent {payment} 0 R", "Bar", dot, off);
        var card = ButtonWidget(doc, page1, FormRect(190, bottom, 208, top), $"/Parent {payment} 0 R", "Karte", dot, off);
        annots1.AddRange([bar, card]);

        var level = doc.Reserve();
        (bottom, top) = Row("Stufe", 18);
        var one = ButtonWidget(doc, page1, FormRect(150, bottom, 168, top), $"/Parent {level} 0 R", "1", dot, off);
        var two = ButtonWidget(doc, page1, FormRect(190, bottom, 208, top), $"/Parent {level} 0 R", "2", dot, off);
        annots1.AddRange([one, two]);

        Field("Farbe", 22, $"/FT /Ch /Ff 131072 /T (Farbe) /Opt [[(r) (Rot)] [(g) (Gruen)] [(b) (Blau)]] {FormDa}");
        Field("Frei", 22, $"/FT /Ch /Ff 393216 /T (Frei) /Opt [(Eins) (Zwei) (Drei)] {FormDa}");
        Field("Liste", 52, $"/FT /Ch /T (Liste) /Opt [(Alpha) (Beta) (Gamma) (Delta)] {FormDa}");
        Field("Mehrfach", 52, $"/FT /Ch /Ff 2097152 /T (Mehrfach) /Opt [(Alpha) (Beta) (Gamma) (Delta)] {FormDa}");

        // Page 2.
        FormLabel(content2, 20, 237, "Zahlung Rechnung");
        var invoice = ButtonWidget(doc, page2, FormRect(150, 234, 168, 252), $"/Parent {payment} 0 R", "Rechnung", dot, off);
        FormLabel(content2, 20, 197, "Name (second widget)");
        var nameSecond = doc.Add($"<< /Type /Annot /Subtype /Widget /Rect {FormRect(150, 190, 380, 212)} /P {page2} 0 R {FormLook} /Parent {name} 0 R >>");
        FormLabel(content2, 20, 157, "Telefon");
        var phone = FormField(doc, page2, FormRect(150, 150, 380, 172), $"/FT /Tx /T (Telefon) {FormDa}");
        FormLabel(content2, 20, 117, "Ort");
        var city = FormField(doc, page2, FormRect(150, 110, 380, 132), $"/FT /Tx /T (Ort) {FormDa}");

        doc.Set(name, $"<< /FT /Tx /T (Name) /V (Alt) /DV (Alt) {FormDa} /Kids [{nameFirst} 0 R {nameSecond} 0 R] >>");
        doc.Set(payment, $"<< /FT /Btn /Ff 32768 /T (Zahlung) /V /Off /Kids [{bar} 0 R {card} 0 R {invoice} 0 R] >>");
        doc.Set(level, $"<< /FT /Btn /Ff 49152 /T (Stufe) /V /Off /Kids [{one} 0 R {two} 0 R] >>");

        doc.AddPage($"/MediaBox [0 0 400 640] /Annots [{AnnotRefs(annots1)}]", resources, content1.ToString(), page1);
        doc.AddPage($"/MediaBox [0 0 400 300] /Annots [{AnnotRefs([invoice, nameSecond, phone, city])}]", resources, content2.ToString(), page2);

        // Top-level fields: Name and the group parents plus every merged field/widget.
        doc.CatalogEntries = AcroFormEntry(doc, [name, .. fields, payment, level, phone, city]);
        return doc.Build();
    }

    // One page 300 x 260: two numbers, Summe (calculation script AFSimple_Calculate "SUM"), Datum (format script AFDate_FormatEx and keystroke
    // script), Bereich (validation script AFRange_Validate). None of the scripts ever runs in Pagewing (no JavaScript engine).
    private static byte[] FormsCalc()
    {
        var doc = new PdfDoc("Pagewing Formularprobe mit Skripten");
        var resources = "<< /Font << /F1 " + doc.Add(Helvetica) + " 0 R >> >>";
        var page = doc.Reserve();
        var content = new StringBuilder("BT /F1 12 Tf 20 238 Td (Form fixture with scripts) Tj ET\n");
        var y = 190;
        int Field(string label, string entries)
        {
            FormLabel(content, 20, y + 7, label);
            var id = FormField(doc, page, FormRect(110, y, 280, y + 22), entries);
            y -= 40;
            return id;
        }

        var number1 = Field("Zahl1", $"/FT /Tx /T (Zahl1) /V (10) {FormDa}");
        var number2 = Field("Zahl2", $"/FT /Tx /T (Zahl2) /V (32) {FormDa}");
        var sum = Field("Summe", $"/FT /Tx /T (Summe) /V (42) {FormDa} /AA << /C << /S /JavaScript /JS (AFSimple_Calculate(\"SUM\", new Array (\"Zahl1\", \"Zahl2\"));) >> >>");
        var date = Field(
            "Datum",
            $"/FT /Tx /T (Datum) /V (26.10.2026) {FormDa} /AA << /F << /S /JavaScript /JS (AFDate_FormatEx(\"dd.mm.yyyy\");) >> /K << /S /JavaScript /JS (AFDate_KeystrokeEx(\"dd.mm.yyyy\");) >> >>");
        var range = Field("Bereich", $"/FT /Tx /T (Bereich) /V (50) {FormDa} /AA << /V << /S /JavaScript /JS (AFRange_Validate(true, 0, true, 100);) >> >>");
        doc.AddPage($"/MediaBox [0 0 300 260] /Annots [{AnnotRefs([number1, number2, sum, date, range])}]", resources, content.ToString(), page);
        doc.CatalogEntries = AcroFormEntry(doc, [number1, number2, sum, date, range], $"/CO [{sum} 0 R]");
        return doc.Build();
    }

    // One page 300 x 140 with an AcroForm that has an /XFA entry (a STUB: two XML snippets, no real XFA form) and one text field.
    private static byte[] FormsXfa()
    {
        var doc = new PdfDoc("Pagewing Formularprobe mit XFA-Attrappe");
        var resources = "<< /Font << /F1 " + doc.Add(Helvetica) + " 0 R >> >>";
        var page = doc.Reserve();
        var template = doc.AddStream(string.Empty, Text("<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\"><template xmlns=\"http://www.xfa.org/schema/xfa-template/3.3/\"/></xdp:xdp>"));
        var datasets = doc.AddStream(string.Empty, Text("<xfa:datasets xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\"><xfa:data/></xfa:datasets>"));
        var field = FormField(doc, page, FormRect(110, 60, 280, 82), $"/FT /Tx /T (Name) {FormDa}");
        doc.AddPage(
            $"/MediaBox [0 0 300 140] /Annots [{field} 0 R]",
            resources,
            "BT /F1 12 Tf 20 110 Td (Form fixture with an XFA stub) Tj ET\nBT /F1 9 Tf 20 67 Td (Name) Tj ET",
            page);
        doc.CatalogEntries = AcroFormEntry(doc, [field], $"/XFA [(template) {template} 0 R (datasets) {datasets} 0 R]");
        return doc.Build();
    }

    // One page 300 x 220: Signature1 (a signature value that is a STRUCTURE DUMMY like signed-docmdp-2.pdf: DocMDP level 2, no cryptography),
    // Signature2 (empty), Senden (push button with a SubmitForm action) and Name (text). Pagewing never validates or runs anything of it.
    private static byte[] FormsSigned()
    {
        var doc = new PdfDoc("Pagewing Formularprobe mit Signaturattrappe");
        var resources = "<< /Font << /F1 " + doc.Add(Helvetica) + " 0 R >> >>";
        var page = doc.Reserve();
        var signature = doc.Add(
            "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange [0 0 0 0] /Contents <" + new string('0', 512) + "> "
            + "/Name (Pagewing test dummy) /M (D:20260101000000Z) /Reason (Structure dummy, not a signature) "
            + "/Reference [<< /Type /SigRef /TransformMethod /DocMDP /TransformParams << /Type /TransformParams /P 2 /V /1.2 >> >>] >>");
        var content = new StringBuilder("BT /F1 12 Tf 20 198 Td (Form fixture with a signature dummy) Tj ET\n");
        FormLabel(content, 20, 168, "Signature1");
        var signed = doc.Add($"<< /Type /Annot /Subtype /Widget /FT /Sig /T (Signature1) /Rect {FormRect(110, 160, 280, 185)} /F 4 /MK << /BC [0 0 0] >> /V {signature} 0 R /P {page} 0 R >>");
        FormLabel(content, 20, 128, "Signature2");
        var unsigned = doc.Add($"<< /Type /Annot /Subtype /Widget /FT /Sig /T (Signature2) /Rect {FormRect(110, 120, 280, 145)} /F 4 /MK << /BC [0 0 0] >> /P {page} 0 R >>");
        var send = doc.Add(
            $"<< /Type /Annot /Subtype /Widget /FT /Btn /Ff 65536 /T (Senden) /Rect {FormRect(110, 80, 190, 105)} /P {page} 0 R /F 4 {FormDa} "
            + "/MK << /BC [0 0 0] /BG [0.9 0.9 0.9] /CA (Send) >> /A << /S /SubmitForm /F << /FS /URL /F (https://example.com/submit) >> /Flags 4 >> >>");
        FormLabel(content, 20, 47, "Name");
        var name = FormField(doc, page, FormRect(110, 40, 280, 62), $"/FT /Tx /T (Name) {FormDa}");
        doc.AddPage($"/MediaBox [0 0 300 220] /Annots [{AnnotRefs([signed, unsigned, send, name])}]", resources, content.ToString(), page);
        doc.CatalogEntries = AcroFormEntry(doc, [signed, unsigned, send, name], "/SigFlags 3") + $" /Perms << /DocMDP {signature} 0 R >>";
        return doc.Build();
    }

    // Page 1: /Rotate 90, MediaBox 400 x 300 with a CropBox [20 10 380 290] that is offset inside it; page 2: /Rotate 270, 300 x 400.
    // Each page has a text field, a check box and a combo (all inside the CropBox).
    private static byte[] FormsRotated()
    {
        var doc = new PdfDoc("Pagewing Formularprobe gedrehter Seiten");
        var resources = "<< /Font << /F1 " + doc.Add(Helvetica) + " 0 R >> >>";
        var (off, check, _) = ButtonAppearances(doc);
        var page1 = doc.Reserve();
        var page2 = doc.Reserve();
        var content1 = new StringBuilder("BT /F1 12 Tf 40 265 Td (Rotated form, page one) Tj ET\n");
        var content2 = new StringBuilder("BT /F1 12 Tf 20 365 Td (Rotated form, page two) Tj ET\n");
        FormLabel(content1, 30, 227, "Text1");
        var text1 = FormField(doc, page1, FormRect(80, 220, 260, 242), $"/FT /Tx /T (Text1) /V (Eins) {FormDa}");
        FormLabel(content1, 30, 177, "Check1");
        var check1 = ButtonWidget(doc, page1, FormRect(80, 170, 98, 188), "/FT /Btn /T (Check1) /V /Off", "Yes", check, off);
        FormLabel(content1, 30, 117, "Combo1");
        var combo1 = FormField(doc, page1, FormRect(80, 110, 260, 132), $"/FT /Ch /Ff 131072 /T (Combo1) /Opt [(Eins) (Zwei) (Drei)] {FormDa}");
        FormLabel(content2, 20, 327, "Text2");
        var text2 = FormField(doc, page2, FormRect(70, 320, 250, 342), $"/FT /Tx /T (Text2) /V (Zwei) {FormDa}");
        FormLabel(content2, 20, 277, "Check2");
        var check2 = ButtonWidget(doc, page2, FormRect(70, 270, 88, 288), "/FT /Btn /T (Check2) /V /Off", "Yes", check, off);
        FormLabel(content2, 20, 217, "Combo2");
        var combo2 = FormField(doc, page2, FormRect(70, 210, 250, 232), $"/FT /Ch /Ff 131072 /T (Combo2) /Opt [(Eins) (Zwei) (Drei)] {FormDa}");
        doc.AddPage($"/MediaBox [0 0 400 300] /CropBox [20 10 380 290] /Rotate 90 /Annots [{AnnotRefs([text1, check1, combo1])}]", resources, content1.ToString(), page1);
        doc.AddPage($"/MediaBox [0 0 300 400] /Rotate 270 /Annots [{AnnotRefs([text2, check2, combo2])}]", resources, content2.ToString(), page2);
        doc.CatalogEntries = AcroFormEntry(doc, [text1, check1, combo1, text2, check2, combo2]);
        return doc.Build();
    }

    // One page 300 x 400 of a hostile form: Normal (a plain first field), Loop (a field whose /Kids lead back to it through an inner node, with one
    // widget leaf), NoType (a widget without /FT), NoRect (a widget without /Rect: not listed), NoName (a field without /T), Big (a combo with 20,000
    // /Opt entries; focusing it in PDFium takes seconds, so no test ever focuses it), a name of 2,000 characters and a name with a NUL character.
    private static byte[] FormsHostile()
    {
        var doc = new PdfDoc("Pagewing feindliche Formularprobe");
        var resources = "<< /Font << /F1 " + doc.Add(Helvetica) + " 0 R >> >>";
        var page = doc.Reserve();
        var content = new StringBuilder("BT /F1 12 Tf 20 378 Td (Hostile form fixture) Tj ET\n");
        var y = 350;
        var annots = new List<int>();
        var fields = new List<int>();

        string Rect(string label)
        {
            FormLabel(content, 20, y + 7, label);
            var rect = FormRect(110, y, 280, y + 20);
            y -= 32;
            return rect;
        }

        void Plain(string label, string entries)
        {
            var id = FormField(doc, page, Rect(label), entries);
            annots.Add(id);
            fields.Add(id);
        }

        Plain("Normal", $"/FT /Tx /T (Normal) {FormDa}");

        var outer = doc.Reserve();
        var inner = doc.Reserve();
        var leaf = doc.Add($"<< /Type /Annot /Subtype /Widget /Rect {Rect("Loop")} /P {page} 0 R {FormLook} /Parent {outer} 0 R >>");
        doc.Set(outer, $"<< /FT /Tx /T (Loop) {FormDa} /Kids [{inner} 0 R {leaf} 0 R] >>");
        doc.Set(inner, $"<< /T (Inner) /Parent {outer} 0 R /Kids [{outer} 0 R] >>");
        annots.Add(leaf);
        fields.Add(outer);

        Plain("NoType", "/T (NoType)");
        var noRect = doc.Add($"<< /Type /Annot /Subtype /Widget /P {page} 0 R {FormLook} /FT /Tx /T (NoRect) {FormDa} >>");
        annots.Add(noRect);
        fields.Add(noRect);
        Plain("NoName", $"/FT /Tx {FormDa}");

        var options = new StringBuilder("[");
        for (var i = 1; i <= 20_000; i++)
        {
            options.Append(CultureInfo.InvariantCulture, $"(Option {i:D5}) ");
        }

        options.Append(']');
        Plain("Big", $"/FT /Ch /Ff 131072 /T (Big) /Opt {options} {FormDa}");
        Plain("LongName", $"/FT /Tx /T ({new string('n', 2_000)}) {FormDa}");
        Plain("NulName", $"/FT /Tx /T (Nul\\000Name) {FormDa}");

        doc.AddPage($"/MediaBox [0 0 300 400] /Annots [{AnnotRefs(annots)}]", resources, content.ToString(), page);
        doc.CatalogEntries = AcroFormEntry(doc, fields);
        return doc.Build();
    }

    // ---- Redaction (milestone 14). The term "Zephyrstraße 17" is written as a literal string with the single byte 0xDF for "ß"
    // (PDFDocEncoding and WinAnsi agree on it), so a plain byte search finds it wherever the file is not compressed; "Quellmann"
    // is the second term for metadata. ----

    private const string RedactTerm = "Zephyrstraße 17";
    private const string RedactSecondTerm = "Quellmann";

    // Where the term hides (milestone 14 relies on exactly this layout), 2 pages of 300 x 400:
    //   page 1: visible text "Adresse: Zephyrstraße 17" (Helvetica 14 at 20,340); the form XObject /Fm1 (shared with page 2) at
    //   20,300; a tiling pattern whose cell shows the term, painted into the rectangle 20,250 200 x 30; an image "scan" (200 x 80
    //   at 20,150) with INVISIBLE text over it (text render mode 3); a Text note (/Contents) and its reply (/IRT) with the term;
    //   a text form field "Adresse" whose /V is the term (widget at 20,100 200 x 25).
    //   page 2: visible text "Seite zwei" and the same form XObject /Fm1 at 20,300.
    //   Document level: bookmark title, info dictionary (/Author Quellmann, /Subject with the term), XMP metadata stream (UTF-8, both
    //   terms), an embedded file notes.txt whose content carries the term. Nothing is compressed.
    private static byte[] RedactEverywhere(bool withPattern = true)
    {
        var doc = new PdfDoc("Pagewing Schwärzen: Begriff überall");
        var helvetica = doc.Add(Helvetica);
        var fonts = $"<< /Font << /F1 {helvetica} 0 R >> >>";
        var page1 = doc.Reserve();
        var page2 = doc.Reserve();

        var form = doc.AddStream(
            $"/Type /XObject /Subtype /Form /BBox [0 0 150 30] /Resources {fonts}",
            Text($"BT /F1 12 Tf 2 10 Td ({RedactTerm}) Tj ET"));
        var pattern = withPattern
            ? doc.AddStream(
                $"/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 100 20] /XStep 100 /YStep 20 /Resources {fonts}",
                Text($"BT /F1 8 Tf 2 6 Td ({RedactTerm}) Tj ET"))
            : 0;

        const int scanSize = 32;
        var scan = new byte[scanSize * scanSize];
        for (var y = 0; y < scanSize; y++)
        {
            for (var x = 0; x < scanSize; x++)
            {
                scan[(y * scanSize) + x] = (byte)(((x / 4) + (y / 4)) % 2 == 0 ? 200 : 235);
            }
        }

        var scanId = doc.AddStream($"/Type /XObject /Subtype /Image /Width {scanSize} /Height {scanSize} /BitsPerComponent 8 /ColorSpace /DeviceGray", scan);

        var note = doc.Add($"<< /Type /Annot /Subtype /Text /Rect [250 340 268 358] /Name /Note /F 28 /NM (redact-note-0001) /T (Alice) /Contents ({RedactTerm}) /C [1 1 0] /P {page1} 0 R >>");
        var reply = doc.Add($"<< /Type /Annot /Subtype /Text /Rect [270 340 288 358] /Name /Comment /F 28 /NM (redact-reply-0001) /T (Bob) /Contents (Re: {RedactTerm}) /IRT {note} 0 R /RT /R /C [1 1 0] /P {page1} 0 R >>");
        var widget = FormField(doc, page1, FormRect(20, 100, 200, 125), $"/FT /Tx /T (Adresse) /V ({RedactTerm}) {FormDa}");

        doc.AddPage(
            $"/MediaBox [0 0 300 400] /Annots [{AnnotRefs([note, reply, widget])}]",
            $"<< /Font << /F1 {helvetica} 0 R >> /XObject << /Fm1 {form} 0 R /Scan {scanId} 0 R >>{(withPattern ? $" /Pattern << /P1 {pattern} 0 R >>" : string.Empty)} >>",
            $"""
            BT /F1 12 Tf 20 372 Td (Redaction fixture, page one) Tj ET
            BT /F1 14 Tf 20 340 Td (Adresse: {RedactTerm}) Tj ET
            q 1 0 0 1 20 300 cm /Fm1 Do Q
            {(withPattern ? "q /Pattern cs /P1 scn 20 250 200 30 re f Q" : "% (no pattern fill in this variant)")}
            q 200 0 0 80 20 150 cm /Scan Do Q
            q BT 3 Tr /F1 12 Tf 30 180 Td ({RedactTerm}) Tj ET Q
            BT /F1 10 Tf 20 135 Td (Scan above carries invisible text.) Tj ET
            """,
            page1);
        doc.AddPage(
            "/MediaBox [0 0 300 400]",
            $"<< /Font << /F1 {helvetica} 0 R >> /XObject << /Fm1 {form} 0 R >> >>",
            """
            BT /F1 12 Tf 20 372 Td (Seite zwei) Tj ET
            q 1 0 0 1 20 300 cm /Fm1 Do Q
            """,
            page2);

        var outline = doc.Reserve();
        var bookmark = doc.Reserve();
        doc.Set(outline, $"<< /Type /Outlines /First {bookmark} 0 R /Last {bookmark} 0 R /Count 1 >>");
        doc.Set(bookmark, $"<< /Title ({RedactTerm}) /Parent {outline} 0 R /Dest [{page1} 0 R /Fit] >>");

        var xmp = doc.AddStream(
            "/Type /Metadata /Subtype /XML",
            Encoding.UTF8.GetBytes(
                "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n"
                + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
                + "<rdf:Description xmlns:dc=\"http://purl.org/dc/elements/1.1/\">"
                + $"<dc:creator><rdf:Seq><rdf:li>{RedactSecondTerm}</rdf:li></rdf:Seq></dc:creator>"
                + $"<dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">{RedactTerm}</rdf:li></rdf:Alt></dc:description>"
                + "</rdf:Description></rdf:RDF></x:xmpmeta>\n<?xpacket end=\"w\"?>"));

        var attachment = doc.AddStream("/Type /EmbeddedFile /Subtype /text#2Fplain", Text($"Attachment text: {RedactTerm}\n"));
        var fileSpec = doc.Add($"<< /Type /Filespec /F (notes.txt) /UF (notes.txt) /EF << /F {attachment} 0 R >> >>");

        doc.InfoEntries = $"/Author ({RedactSecondTerm}) /Subject ({RedactTerm})";
        doc.CatalogEntries = $"/Outlines {outline} 0 R /PageMode /UseOutlines /Metadata {xmp} 0 R /Names << /EmbeddedFiles << /Names [(notes.txt) {fileSpec} 0 R] >> >> "
            + AcroFormEntry(doc, [widget]);
        return doc.Build();
    }

    // Attachments in two places. One page 300 x 200: "Public line stays visible" (Helvetica 14 at 20,160) and "Adresse: Zephyrstraße 17" (20,120),
    // a FileAttachment annotation (paperclip) at 250,20 to 270,40 whose embedded file says "ANNOTFILE-9183", and an attachment in the
    // names tree of the catalog that says "CATALOGFILE-7421". A redaction removes the names tree (document level); the paperclip is an
    // annotation that comes with the page and keeps its file. Neither file holds the term.
    private static byte[] RedactAttachments()
    {
        var doc = new PdfDoc("Pagewing Schwärzen: Anhänge");
        var catalogFile = doc.AddStream("/Type /EmbeddedFile /Subtype /text#2Fplain", Text("Catalog attachment CATALOGFILE-7421\n"));
        var catalogSpec = doc.Add($"<< /Type /Filespec /F (catalog.txt) /UF (catalog.txt) /EF << /F {catalogFile} 0 R >> >>");
        var annotFile = doc.AddStream("/Type /EmbeddedFile /Subtype /text#2Fplain", Text("Annotation attachment ANNOTFILE-9183\n"));
        var annotSpec = doc.Add($"<< /Type /Filespec /F (paperclip.txt) /UF (paperclip.txt) /EF << /F {annotFile} 0 R >> >>");
        var clip = doc.Add($"<< /Type /Annot /Subtype /FileAttachment /Rect [250 20 270 40] /Name /Paperclip /Contents (A paperclip) /FS {annotSpec} 0 R /F 4 >>");
        doc.AddPage($"/MediaBox [0 0 300 200] /Annots [{clip} 0 R]", "<< /Font << /F1 " + doc.Add(Helvetica) + " 0 R >> >>", $"""
            BT /F1 14 Tf 20 160 Td (Public line stays visible) Tj ET
            BT /F1 14 Tf 20 120 Td (Adresse: {RedactTerm}) Tj ET
            """);
        doc.CatalogEntries = $"/Names << /EmbeddedFiles << /Names [(catalog.txt) {catalogSpec} 0 R] >> >> ";
        return doc.Build();
    }

    // The LEAK fixture: live text under a black rectangle. One page 300 x 200: the line "Public line stays visible" (Helvetica 14 at
    // 20,160) and "Adresse: Zephyrstraße 17" (Helvetica 14 at 20,120) with a black rectangle 15,112 260 x 24 painted OVER it. Rendering
    // shows a black bar, text extraction and the raw bytes still find the term: a redaction that only covers must fail the check.
    private static byte[] RedactCovered()
    {
        var doc = new PdfDoc("Pagewing Schwärzen: nur verdeckt");
        doc.AddPage("/MediaBox [0 0 300 200]", "<< /Font << /F1 " + doc.Add(Helvetica) + " 0 R >> >>", $"""
            BT /F1 14 Tf 20 160 Td (Public line stays visible) Tj ET
            BT /F1 14 Tf 20 120 Td (Adresse: {RedactTerm}) Tj ET
            0 g 15 112 260 24 re f
            """);
        return doc.Build();
    }

    // The term only in an OLDER incremental revision. One page 300 x 200. Revision 1: the content stream shows "Adresse: Zephyrstraße 17"
    // (Helvetica 14 at 20,120) and "Public line stays visible". Revision 2 (appended with a new xref section and /Prev) replaces that content
    // stream by one that shows "Address removed" and the public line; the old bytes remain in the file. PDFium sees only revision 2,
    // a raw byte search finds the term in revision 1 (the first revision stays the exact beginning of the file).
    private static byte[] RedactRevisions()
    {
        var doc = new PdfDoc("Pagewing Schwärzen: ältere Revision");
        doc.AddPage("/MediaBox [0 0 300 200]", "<< /Font << /F1 " + doc.Add(Helvetica) + " 0 R >> >>", $"""
            BT /F1 14 Tf 20 160 Td (Public line stays visible) Tj ET
            BT /F1 14 Tf 20 120 Td (Adresse: {RedactTerm}) Tj ET
            """);
        var replacement = PdfDoc.StreamBody(string.Empty, Text("""
            BT /F1 14 Tf 20 160 Td (Public line stays visible) Tj ET
            BT /F1 14 Tf 20 120 Td (Address removed) Tj ET
            """));
        return doc.BuildWithRevision(doc.LastContentId, replacement);
    }

    // Three 64 x 64 images with known pixels, shown at 128 x 128 pt on one page of 420 x 170 (label at the top):
    //   /ImFlate at x=10: DeviceRGB, 8 bit, Flate (stored blocks, so the bytes do not depend on a zlib version); vertical stripes of 8 px,
    //     columns x/8 even (200,30,30), odd (30,30,200).
    //   /ImJpeg at x=150: DeviceGray, baseline JPEG that only has DC coefficients (every 8 x 8 block is flat, the decode is exact);
    //     vertical stripes of 8 px, even columns 70, odd 190.
    //   /ImMask at x=290 (on a grey 0.7 rectangle): DeviceRGB Flate with an /SMask (DeviceGray, Flate); colour stripes by column
    //     (x/8 even (30,160,30), odd (240,200,30)); alpha by row, y/8 even 255, odd 128.
    private static byte[] RedactImage()
    {
        var doc = new PdfDoc("Pagewing Schwärzen: Bilder");
        const int size = 64;
        var flate = new byte[size * size * 3];
        var jpeg = new byte[size * size];
        var colour = new byte[size * size * 3];
        var alpha = new byte[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var even = (x / 8) % 2 == 0;
                var i = ((y * size) + x) * 3;
                (flate[i], flate[i + 1], flate[i + 2]) = even ? ((byte)200, (byte)30, (byte)30) : ((byte)30, (byte)30, (byte)200);
                jpeg[(y * size) + x] = even ? (byte)70 : (byte)190;
                (colour[i], colour[i + 1], colour[i + 2]) = even ? ((byte)30, (byte)160, (byte)30) : ((byte)240, (byte)200, (byte)30);
                alpha[(y * size) + x] = (y / 8) % 2 == 0 ? (byte)255 : (byte)128;
            }
        }

        const string head = "/Type /XObject /Subtype /Image /Width 64 /Height 64 /BitsPerComponent 8";
        var flateId = doc.AddStream($"{head} /ColorSpace /DeviceRGB /Filter /FlateDecode", StoredFlate(flate));
        var jpegId = doc.AddStream($"{head} /ColorSpace /DeviceGray /Filter /DCTDecode", FlatBlockJpeg(jpeg, size));
        var maskId = doc.AddStream($"{head} /ColorSpace /DeviceGray /Filter /FlateDecode", StoredFlate(alpha));
        var maskedId = doc.AddStream($"{head} /ColorSpace /DeviceRGB /Filter /FlateDecode /SMask {maskId} 0 R", StoredFlate(colour));
        doc.AddPage(
            "/MediaBox [0 0 420 170]",
            $"<< /Font << /F1 {doc.Add(Helvetica)} 0 R >> /XObject << /ImFlate {flateId} 0 R /ImJpeg {jpegId} 0 R /ImMask {maskedId} 0 R >> >>",
            """
            BT /F1 10 Tf 10 156 Td (Image fixture: Flate, JPEG, SMask) Tj ET
            0.7 0.7 0.7 rg 290 16 128 128 re f
            q 128 0 0 128 10 16 cm /ImFlate Do Q
            q 128 0 0 128 150 16 cm /ImJpeg Do Q
            q 128 0 0 128 290 16 cm /ImMask Do Q
            """);
        return doc.Build();
    }

    // A rotated page with a CropBox inside the MediaBox. MediaBox [0 0 400 300], CropBox [20 10 380 290], /Rotate 90 (shown 280 x 360).
    // Three text lines in unrotated user space (Helvetica 14): "Top line" at 60,250; "Adresse: Zephyrstraße 17" at 60,150;
    // "Bottom line" at 60,70. A red marker square 40 x 40 sits at the CropBox corner 20,10 and shows which corner is which.
    private static byte[] RedactRotated()
    {
        var doc = new PdfDoc("Pagewing Schwärzen: gedrehte Seite");
        doc.AddPage("/MediaBox [0 0 400 300] /CropBox [20 10 380 290] /Rotate 90", "<< /Font << /F1 " + doc.Add(Helvetica) + " 0 R >> >>", $"""
            1 0 0 rg 20 10 40 40 re f
            0 g
            BT /F1 14 Tf 60 250 Td (Top line) Tj ET
            BT /F1 14 Tf 60 150 Td (Adresse: {RedactTerm}) Tj ET
            BT /F1 14 Tf 60 70 Td (Bottom line) Tj ET
            """);
        return doc.Build();
    }

    // ---- Redaction fixtures for the spike of milestone 14 (fonts, more image kinds, content no page object shows, paths). ----

    private const string FontSentence = "Name: Zephyrstraße 17 ok";

    // One page 420 x 420 whose lines all show the sentence "Name: Zephyrstraße 17 ok" (one text object per line) in other fonts. The x position
    // is 20 for every line; the baselines are 390 - 30 * line. The font program of the TrueType and CID lines is the full Geist Regular (SIL OFL 1.1,
    // src/Pagewing.Core/Assets/Fonts), embedded as it is (no subset: the generator has no subsetter); the fonts differ in how they encode:
    //   line 0  /F1  Helvetica, Type 1 standard font, not embedded, WinAnsi, Tj
    //   line 1  /F2  simple TrueType, embedded Geist, WinAnsi, no ToUnicode, Tj
    //   line 2  /F3  Type0 Identity-H, CIDFontType2, CIDToGIDMap /Identity (code = glyph id), ToUnicode, Tj with a hex string
    //   line 3  /F4  Type0 Identity-H, CIDToGIDMap stream (code = number of the character in the sentence, as a subset renumbers), ToUnicode
    //   line 4  /F5  as /F3 but without ToUnicode (the text is not extractable)
    //   line 5  /F6  simple Type 1, embedded (a self-made font whose glyphs are boxes), built-in encoding, no ToUnicode, Tj
    //   line 6  /F2  TJ array with kerning adjustments
    //   line 7  /F2  with character spacing (Tc 1.5), word spacing (Tw 4) and horizontal scaling (Tz 80)
    //   line 8  /F2  turned by 30 degrees with a text matrix (origin 60,60)
    private static byte[] RedactFonts()
    {
        var doc = new PdfDoc("Pagewing Schwärzen: Schriften");
        var ttf = new TtfInfo(File.ReadAllBytes(RepositoryFile("src", "Pagewing.Core", "Assets", "Fonts", "Geist-Regular.ttf")));
        var helvetica = doc.Add(Helvetica);

        var fontFile = doc.AddStream($"/Length1 {ttf.Data.Length}", ttf.Data);
        string Scaled(int value) => ((value * 1000) / ttf.UnitsPerEm).ToString(CultureInfo.InvariantCulture);
        var descriptor = doc.Add(
            $"<< /Type /FontDescriptor /FontName /AAAAAA+Geist /Flags 32 /FontBBox [{Scaled(ttf.XMin)} {Scaled(ttf.YMin)} {Scaled(ttf.XMax)} {Scaled(ttf.YMax)}] "
            + $"/ItalicAngle 0 /Ascent {Scaled(ttf.Ascent)} /Descent {Scaled(ttf.Descent)} /CapHeight 700 /StemV 80 /FontFile2 {fontFile} 0 R >>");

        // F2: simple TrueType, WinAnsi (the codes 32 to 126 and 160 to 255 are Unicode; the rest of the range has width 0).
        var widths = new StringBuilder();
        for (var code = 32; code <= 255; code++)
        {
            var glyph = code is (>= 32 and <= 126) or (>= 160 and <= 255) ? ttf.GlyphFor(code) : 0;
            widths.Append(CultureInfo.InvariantCulture, $"{(glyph == 0 ? 0 : Scaled(ttf.AdvanceOf(glyph)).ToString(CultureInfo.InvariantCulture))} ");
        }

        var simple = doc.Add($"<< /Type /Font /Subtype /TrueType /BaseFont /AAAAAA+Geist /FirstChar 32 /LastChar 255 /Widths [{widths}] /Encoding /WinAnsiEncoding /FontDescriptor {descriptor} 0 R >>");

        // F3 and F5: Identity-H, the code is the glyph id.
        var distinct = FontSentence.Distinct().ToArray();
        var identityWidths = new StringBuilder();
        foreach (var c in distinct)
        {
            var glyph = ttf.GlyphFor(c);
            identityWidths.Append(CultureInfo.InvariantCulture, $"{glyph} [{Scaled(ttf.AdvanceOf(glyph))}] ");
        }

        var identityCid = doc.Add(
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /AAAAAA+Geist /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
            + $"/FontDescriptor {descriptor} 0 R /CIDToGIDMap /Identity /DW 600 /W [{identityWidths}] >>");
        var identityMap = doc.AddStream(string.Empty, ToUnicodeCMap(distinct.Select(c => ((int)ttf.GlyphFor(c), c)).ToArray()));
        var identityToUnicode = doc.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /AAAAAA+Geist /Encoding /Identity-H /DescendantFonts [{identityCid} 0 R] /ToUnicode {identityMap} 0 R >>");
        var identityPlain = doc.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /AAAAAA+Geist /Encoding /Identity-H /DescendantFonts [{identityCid} 0 R] >>");

        // F4: the code is the one-based number of the character in the sentence's distinct characters, CIDToGIDMap renumbers it.
        var gidMap = new byte[(distinct.Length + 1) * 2];
        var renumberedWidths = new StringBuilder();
        for (var i = 0; i < distinct.Length; i++)
        {
            var glyph = ttf.GlyphFor(distinct[i]);
            gidMap[(i + 1) * 2] = (byte)(glyph >> 8);
            gidMap[((i + 1) * 2) + 1] = (byte)glyph;
            renumberedWidths.Append(CultureInfo.InvariantCulture, $"{i + 1} [{Scaled(ttf.AdvanceOf(glyph))}] ");
        }

        var cidToGid = doc.AddStream(string.Empty, gidMap);
        var renumberedCid = doc.Add(
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /AAAAAA+Geist /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
            + $"/FontDescriptor {descriptor} 0 R /CIDToGIDMap {cidToGid} 0 R /DW 600 /W [{renumberedWidths}] >>");
        var renumberedMap = doc.AddStream(string.Empty, ToUnicodeCMap(distinct.Select((c, i) => (i + 1, c)).ToArray()));
        var renumbered = doc.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /AAAAAA+Geist /Encoding /Identity-H /DescendantFonts [{renumberedCid} 0 R] /ToUnicode {renumberedMap} 0 R >>");

        // F6: the self-made Type 1 font.
        var type1 = Type1Font.Build(FontSentence);
        var type1File = doc.AddStream($"/Length1 {type1.Length1} /Length2 {type1.Length2} /Length3 {type1.Length3}", type1.Data);
        var type1Descriptor = doc.Add($"<< /Type /FontDescriptor /FontName /PwBox /Flags 4 /FontBBox [0 0 600 750] /ItalicAngle 0 /Ascent 750 /Descent 0 /CapHeight 700 /StemV 80 /FontFile {type1File} 0 R >>");
        var type1Widths = new StringBuilder();
        for (var code = 32; code <= 255; code++)
        {
            type1Widths.Append(CultureInfo.InvariantCulture, $"{(Type1Font.NameOf((char)code) is null || !FontSentence.Contains((char)code, StringComparison.Ordinal) ? 0 : code == 32 ? 300 : 600)} ");
        }

        var type1Font = doc.Add($"<< /Type /Font /Subtype /Type1 /BaseFont /PwBox /FirstChar 32 /LastChar 255 /Widths [{type1Widths}] /FontDescriptor {type1Descriptor} 0 R >>");

        string Hex(string text, Func<char, int> code) => "<" + string.Concat(text.Select(c => code(c).ToString("X4", CultureInfo.InvariantCulture))) + ">";
        var identityHex = Hex(FontSentence, c => ttf.GlyphFor(c));
        var renumberedHex = Hex(FontSentence, c => Array.IndexOf(distinct, c) + 1);

        doc.AddPage(
            "/MediaBox [0 0 420 420]",
            $"<< /Font << /F1 {helvetica} 0 R /F2 {simple} 0 R /F3 {identityToUnicode} 0 R /F4 {renumbered} 0 R /F5 {identityPlain} 0 R /F6 {type1Font} 0 R >> >>",
            $"""
            BT /F1 14 Tf 20 390 Td ({FontSentence}) Tj ET
            BT /F2 14 Tf 20 360 Td ({FontSentence}) Tj ET
            BT /F3 14 Tf 20 330 Td {identityHex} Tj ET
            BT /F4 14 Tf 20 300 Td {renumberedHex} Tj ET
            BT /F5 14 Tf 20 270 Td {identityHex} Tj ET
            BT /F6 14 Tf 20 240 Td ({FontSentence}) Tj ET
            BT /F2 14 Tf 20 210 Td [(Name:) -300 ( Zephyr) 120 (stra) -80 (ße 17) 50 ( ok)] TJ ET
            BT /F2 14 Tf 1.5 Tc 4 Tw 80 Tz 20 180 Td ({FontSentence}) Tj ET
            BT /F2 14 Tf 0.866 0.5 -0.5 0.866 60 60 Tm ({FontSentence}) Tj ET
            """);
        return doc.Build();
    }

    // One page 300 x 100 for glyphs whose character code is below 32. The self-made Type 1 font /F2 (no ToUnicode, built-in encoding) has
    // the glyphs A, B, and three more at the codes 5, 6 and 7 whose names no Unicode list knows (pwctl5 to pwctl7), so PDFium can only report
    // the code as the character. Helvetica /F1 line "Public line stays" at 20,70 (size 14); /F2 line (size 24) at 20,30: A, code 5, code 6, B.
    private static byte[] RedactControl()
    {
        var doc = new PdfDoc("Pagewing Schwärzen: Zeichencodes unter 32");
        var helvetica = doc.Add(Helvetica);
        const string sentence = "AB";
        var type1 = Type1Font.Build(sentence);
        var type1File = doc.AddStream($"/Length1 {type1.Length1} /Length2 {type1.Length2} /Length3 {type1.Length3}", type1.Data);
        var descriptor = doc.Add($"<< /Type /FontDescriptor /FontName /PwBoxControl /Flags 4 /FontBBox [0 0 600 750] /ItalicAngle 0 /Ascent 750 /Descent 0 /CapHeight 700 /StemV 80 /FontFile {type1File} 0 R >>");
        var widths = new StringBuilder();
        for (var code = 0; code <= 255; code++)
        {
            widths.Append(CultureInfo.InvariantCulture, $"{(sentence.Contains((char)code, StringComparison.Ordinal) ? 600 : 0)} ");
        }

        var font = doc.Add($"<< /Type /Font /Subtype /Type1 /BaseFont /PwBoxControl /FirstChar 0 /LastChar 255 /Widths [{widths}] /FontDescriptor {descriptor} 0 R >>");
        doc.AddPage(
            "/MediaBox [0 0 300 100]",
            $"<< /Font << /F1 {helvetica} 0 R /F2 {font} 0 R >> >>",
            """
            BT /F1 14 Tf 20 70 Td (Public line stays) Tj ET
            BT /F2 24 Tf 20 30 Td (AB) Tj ET
            """);
        return doc.Build();
    }

    // One page 300 x 100 with a ligature-like glyph, the way LaTeX writes one for "fi": the line "Zephyr æsthetics" (Geist Regular, Type0 Identity-H,
    // code = glyph id, size 20 at 20,50) in which the glyph of U+00E6 stands for the TWO characters "ae" (its ToUnicode entry; Geist has no
    // ligature glyphs, so a letter that exists plays the part), so PDFium reports two characters at one place. Above it the Helvetica line
    // "Ligature fixture" (size 10 at 20,80). A text that is split around "Zephyr" has to write the glyph again from two characters, which draws
    // two glyphs instead of one: the render check has to see that.
    private static byte[] RedactLigature()
    {
        var doc = new PdfDoc("Pagewing Schwärzen: Ligatur");
        var ttf = new TtfInfo(File.ReadAllBytes(RepositoryFile("src", "Pagewing.Core", "Assets", "Fonts", "Geist-Regular.ttf")));
        var helvetica = doc.Add(Helvetica);
        var fontFile = doc.AddStream($"/Length1 {ttf.Data.Length}", ttf.Data);
        string Scaled(int value) => ((value * 1000) / ttf.UnitsPerEm).ToString(CultureInfo.InvariantCulture);
        var descriptor = doc.Add(
            $"<< /Type /FontDescriptor /FontName /AAAAAA+Geist /Flags 32 /FontBBox [{Scaled(ttf.XMin)} {Scaled(ttf.YMin)} {Scaled(ttf.XMax)} {Scaled(ttf.YMax)}] "
            + $"/ItalicAngle 0 /Ascent {Scaled(ttf.Ascent)} /Descent {Scaled(ttf.Descent)} /CapHeight 700 /StemV 80 /FontFile2 {fontFile} 0 R >>");

        // The glyphs of the line: the characters of "Zephyr sthetics" and the ligature-like glyph.
        var ligature = ttf.GlyphFor(0xE6);
        if (ligature == 0)
        {
            throw new InvalidOperationException("Die Schrift hat kein Glyph fuer U+00E6.");
        }

        var glyphs = new List<int>();
        foreach (var c in "Zephyr ")
        {
            glyphs.Add(ttf.GlyphFor(c));
        }

        glyphs.Add(ligature);
        foreach (var c in "sthetics")
        {
            glyphs.Add(ttf.GlyphFor(c));
        }

        var distinct = glyphs.Distinct().ToArray();
        var widths = new StringBuilder();
        foreach (var glyph in distinct)
        {
            widths.Append(CultureInfo.InvariantCulture, $"{glyph} [{Scaled(ttf.AdvanceOf(glyph))}] ");
        }

        var cid = doc.Add(
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /AAAAAA+Geist /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
            + $"/FontDescriptor {descriptor} 0 R /CIDToGIDMap /Identity /DW 600 /W [{widths}] >>");
        var map = new StringBuilder();
        map.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        map.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        map.Append(CultureInfo.InvariantCulture, $"{distinct.Length} beginbfchar\n");
        foreach (var glyph in distinct)
        {
            var text = glyph == ligature ? "ae" : "Zephyr sthetics".First(c => ttf.GlyphFor(c) == glyph).ToString();
            map.Append(CultureInfo.InvariantCulture, $"<{glyph:X4}> <{string.Concat(text.Select(c => ((int)c).ToString("X4", CultureInfo.InvariantCulture)))}>\n");
        }

        map.Append("endbfchar\nendcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        var toUnicode = doc.AddStream(string.Empty, Text(map.ToString()));
        var font = doc.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /AAAAAA+Geist /Encoding /Identity-H /DescendantFonts [{cid} 0 R] /ToUnicode {toUnicode} 0 R >>");
        var hex = "<" + string.Concat(glyphs.Select(g => g.ToString("X4", CultureInfo.InvariantCulture))) + ">";
        doc.AddPage(
            "/MediaBox [0 0 300 100]",
            $"<< /Font << /F1 {helvetica} 0 R /F2 {font} 0 R >> >>",
            $"""
            BT /F1 10 Tf 20 80 Td (Ligature fixture) Tj ET
            BT /F2 20 Tf 20 50 Td {hex} Tj ET
            """);
        return doc.Build();
    }

    // A ToUnicode CMap for a two-byte code space: (code, character) pairs, written in blocks of at most 100 entries.
    private static byte[] ToUnicodeCMap((int Code, char Character)[] pairs)
    {
        var builder = new StringBuilder();
        builder.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        builder.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        for (var start = 0; start < pairs.Length; start += 100)
        {
            var block = pairs.Skip(start).Take(100).ToArray();
            builder.Append(CultureInfo.InvariantCulture, $"{block.Length} beginbfchar\n");
            foreach (var (code, character) in block)
            {
                builder.Append(CultureInfo.InvariantCulture, $"<{code:X4}> <{(int)character:X4}>\n");
            }

            builder.Append("endbfchar\n");
        }

        builder.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return Text(builder.ToString());
    }

    private static string RepositoryFile(params string[] parts)
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Pagewing.slnx")))
            {
                return Path.Combine([directory.FullName, .. parts]);
            }
        }

        throw new DirectoryNotFoundException("Pagewing.slnx nicht gefunden.");
    }

    // One page 440 x 330 for pixelating and blurring (milestone 14.1): images whose blocks all differ (no stripes), so that a mean of a block
    // is a number nothing else has. Every image is DeviceRGB, 8 bit, Flate (stored blocks); the pixels are a formula of the column x and row y:
    //   /ImGrad  96 x 96 px at 10,160 shown 144 x 144 pt (1.5 pt per pixel): red = x*255/95, green = y*255/95, blue = 40 + (x*y) mod 97 on the
    //            even squares of 8 x 8 pixels ((x/8 + y/8) even) and 200 - (x*y) mod 89 on the odd ones
    //   /ImText  64 x 64 px at 170,160 shown 128 x 128 pt, with the text "Secret 4711" (Helvetica 14, 180,220) drawn over it: red = 4x mod 256,
    //            green = 4y mod 256, blue = 5 (x xor y) mod 256
    //   /ImTurn  32 x 32 px turned by 30 degrees (matrix 83.14 48 -48 83.14 350 170, about 96 pt a side): red = 8x, green = 255 - 8y, blue = x*y mod 256
    //   /ImTiny  7 x 7 px at 10,90 shown 56 x 56 pt: red = 36x, green = 36y, blue = 18 (x + y)
    //   /ImClip  96 x 96 px at 320,20 shown 96 x 96 pt, clipped to the rectangle 330,30 80 x 60 (a cropped image): red = y*255/95,
    //            green = (3x + 5y) mod 256, blue = 255 - x*255/95
    // Besides a red rectangle 100,90 70 x 56 (vector) and the line "Plain 1234" (Helvetica 14, 180,110): nothing under them.
    private static byte[] RedactImageBlocks()
    {
        var doc = new PdfDoc("Pagewing Schwärzen: Bildblöcke");
        byte[] Pixels(int size, Func<int, int, (int R, int G, int B)> at)
        {
            var data = new byte[size * size * 3];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var (r, g, b) = at(x, y);
                    var i = ((y * size) + x) * 3;
                    (data[i], data[i + 1], data[i + 2]) = ((byte)r, (byte)g, (byte)b);
                }
            }

            return data;
        }

        string Head(int size) => $"/Type /XObject /Subtype /Image /Width {size} /Height {size} /BitsPerComponent 8 /ColorSpace /DeviceRGB /Filter /FlateDecode";
        var grad = Pixels(96, (x, y) => (x * 255 / 95, y * 255 / 95, ((x / 8) + (y / 8)) % 2 == 0 ? 40 + (x * y % 97) : 200 - (x * y % 89)));
        var text = Pixels(64, (x, y) => (x * 4 % 256, y * 4 % 256, (x ^ y) * 5 % 256));
        var turn = Pixels(32, (x, y) => (x * 8, 255 - (y * 8), x * y % 256));
        var tiny = Pixels(7, (x, y) => (x * 36, y * 36, (x + y) * 18));
        var clip = Pixels(96, (x, y) => (y * 255 / 95, ((3 * x) + (5 * y)) % 256, 255 - (x * 255 / 95)));
        var gradId = doc.AddStream(Head(96), StoredFlate(grad));
        var textId = doc.AddStream(Head(64), StoredFlate(text));
        var turnId = doc.AddStream(Head(32), StoredFlate(turn));
        var tinyId = doc.AddStream(Head(7), StoredFlate(tiny));
        var clipId = doc.AddStream(Head(96), StoredFlate(clip));
        doc.AddPage(
            "/MediaBox [0 0 440 330]",
            $"<< /Font << /F1 {doc.Add(Helvetica)} 0 R >> /XObject << /ImGrad {gradId} 0 R /ImText {textId} 0 R /ImTurn {turnId} 0 R /ImTiny {tinyId} 0 R /ImClip {clipId} 0 R >> >>",
            """
            BT /F1 10 Tf 10 316 Td (Image blocks: gradient, text, turned, tiny, clipped) Tj ET
            q 144 0 0 144 10 160 cm /ImGrad Do Q
            q 128 0 0 128 170 160 cm /ImText Do Q
            BT /F1 14 Tf 180 220 Td (Secret 4711) Tj ET
            q 83.14 48 -48 83.14 350 170 cm /ImTurn Do Q
            q 56 0 0 56 10 90 cm /ImTiny Do Q
            0.8 0.2 0.2 rg 100 90 70 56 re f
            0 g
            BT /F1 14 Tf 180 110 Td (Plain 1234) Tj ET
            q 330 30 80 60 re W n 96 0 0 96 320 20 cm /ImClip Do Q
            """);
        return doc.Build();
    }

    // One page 450 x 130 with four more image kinds, each 64 x 64 px shown at 100 x 100 pt (stripes of 8 px along x, 8 columns), at y=16:
    //   /ImIndexed at x=10:  Indexed (DeviceRGB palette of 4 entries), 8 bit, Flate (stored); even columns index 0 = (200,30,30), odd index 1 = (30,30,200)
    //   /ImCmyk    at x=120: DeviceCMYK, 8 bit, Flate (stored); even columns (204,0,0,0), odd (0,204,0,0)
    //   inline     at x=230: DeviceRGB 16 x 16 px written as an inline image (BI/ID/EI, ASCIIHex); columns of 2 px, even (30,160,30), odd (240,200,30)
    //   /ImStencil at x=340: 1 bit ImageMask (stencil), 64 x 64, painted in (0.2,0.4,0.8); even columns (bit 0 = paint), odd columns not painted
    private static byte[] RedactImageKinds()
    {
        var doc = new PdfDoc("Pagewing Schwärzen: weitere Bildarten");
        const int size = 64;
        var indexed = new byte[size * size];
        var cmyk = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var even = (x / 8) % 2 == 0;
                indexed[(y * size) + x] = even ? (byte)0 : (byte)1;
                var i = ((y * size) + x) * 4;
                (cmyk[i], cmyk[i + 1], cmyk[i + 2], cmyk[i + 3]) = even ? ((byte)204, (byte)0, (byte)0, (byte)0) : ((byte)0, (byte)204, (byte)0, (byte)0);
            }
        }

        var stencil = new byte[size / 8 * size];
        for (var y = 0; y < size; y++)
        {
            for (var b = 0; b < size / 8; b++)
            {
                stencil[(y * (size / 8)) + b] = b % 2 == 0 ? (byte)0x00 : (byte)0xFF;
            }
        }

        var inline = new StringBuilder();
        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                inline.Append((x / 2) % 2 == 0 ? "1EA01E" : "F0C81E");
            }
        }

        const string head = "/Type /XObject /Subtype /Image /Width 64 /Height 64";
        var indexedId = doc.AddStream($"{head} /BitsPerComponent 8 /ColorSpace [/Indexed /DeviceRGB 3 <C81E1E 1E1EC8 1EA01E F0C81E>] /Filter /FlateDecode", StoredFlate(indexed));
        var cmykId = doc.AddStream($"{head} /BitsPerComponent 8 /ColorSpace /DeviceCMYK /Filter /FlateDecode", StoredFlate(cmyk));
        var stencilId = doc.AddStream($"{head} /ImageMask true /BitsPerComponent 1 /Filter /FlateDecode", StoredFlate(stencil));
        doc.AddPage(
            "/MediaBox [0 0 450 130]",
            $"<< /Font << /F1 {doc.Add(Helvetica)} 0 R >> /XObject << /ImIndexed {indexedId} 0 R /ImCmyk {cmykId} 0 R /ImStencil {stencilId} 0 R >> >>",
            $"""
            BT /F1 8 Tf 10 120 Td (Image kinds: Indexed, CMYK, inline, stencil) Tj ET
            q 100 0 0 100 10 16 cm /ImIndexed Do Q
            q 100 0 0 100 120 16 cm /ImCmyk Do Q
            q 100 0 0 100 230 16 cm
            BI /W 16 /H 16 /CS /RGB /BPC 8 /F /AHx
            ID
            {inline}>
            EI
            Q
            0.2 0.4 0.8 rg
            q 100 0 0 100 340 16 cm /ImStencil Do Q
            """);
        return doc.Build();
    }

    // One page 300 x 300 with content that no page object shows (the term "Zephyrstraße 17" only in the content of other streams):
    //   a Type 3 font whose glyph procedure for "a" draws the term as text (Helvetica); the page shows two "a" at 20,240 (size 40);
    //   a soft mask (ExtGState /GS1, luminosity group /G) whose group draws the term in white; used for a blue rectangle 20,150 200 x 60;
    //   an axial shading (/Sh1, painted with sh in a clip of 20,80 200 x 40), which contains no text.
    private static byte[] RedactHidden()
    {
        var doc = new PdfDoc("Pagewing Schwärzen: Inhalt ohne Seitenobjekt");
        var helvetica = doc.Add(Helvetica);
        var glyph = doc.AddStream(string.Empty, Text($"1000 0 0 0 1000 1000 d1 BT /F1 400 Tf 0 200 Td ({RedactTerm}) Tj ET"));
        var empty = doc.AddStream(string.Empty, Text("1000 0 d0"));
        var type3 = doc.Add(
            "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000] /FontMatrix [0.001 0 0 0.001 0 0] /CharProcs << /a "
            + $"{glyph} 0 R /b {empty} 0 R >> /Encoding << /Type /Encoding /Differences [97 /a /b] >> /FirstChar 97 /LastChar 98 /Widths [1000 1000] /Resources << /Font << /F1 {helvetica} 0 R >> >> >>");

        var group = doc.AddStream(
            $"/Type /XObject /Subtype /Form /BBox [0 0 300 300] /Group << /S /Transparency /CS /DeviceGray >> /Resources << /Font << /F1 {helvetica} 0 R >> >>",
            Text($"0 g 0 0 300 300 re f 1 g BT /F1 12 Tf 20 170 Td ({RedactTerm}) Tj ET 1 g 20 150 200 60 re f"));
        var state = doc.Add($"<< /Type /ExtGState /SMask << /Type /Mask /S /Luminosity /G {group} 0 R >> >>");

        var function = doc.Add("<< /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >>");
        var shading = doc.Add($"<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [20 0 220 0] /Function {function} 0 R /Extend [true true] >>");

        doc.AddPage(
            "/MediaBox [0 0 300 300]",
            $"<< /Font << /T3 {type3} 0 R /F1 {helvetica} 0 R >> /ExtGState << /GS1 {state} 0 R >> /Shading << /Sh1 {shading} 0 R >> >>",
            """
            BT /F1 10 Tf 20 280 Td (Hidden content fixture) Tj ET
            BT /T3 40 Tf 20 240 Td (aa) Tj ET
            q /GS1 gs 0 0 1 rg 20 150 200 60 re f Q
            q 20 80 200 40 re W n /Sh1 sh Q
            """);
        return doc.Build();
    }

    // One page 300 x 300 for the paths: a background (the whole page, light yellow), text on it ("Zephyrstraße 17" with Helvetica 14 at 20,270),
    // an axis-parallel rectangle (blue, 20,200 200 x 40), a rectangle turned by 45 degrees (green, centre 150,150), a triangle (red) with a
    // curve (orange, a Bezier curve), a stroked rectangle (black, 4 pt, 20,60 100 x 30) and clipped text (the clip rectangle 150,60 60 x 30 cuts
    // the text "Zephyrstraße 17" written at 150,70 in 14 pt).
    private static byte[] RedactPaths()
    {
        var doc = new PdfDoc("Pagewing Schwärzen: Pfade");
        doc.AddPage("/MediaBox [0 0 300 300]", "<< /Font << /F1 " + doc.Add(Helvetica) + " 0 R >> >>", $"""
            1 1 0.8 rg 0 0 300 300 re f
            0 g BT /F1 14 Tf 20 270 Td ({RedactTerm}) Tj ET
            0 0 1 rg 20 200 200 40 re f
            q 0 0.6 0 rg 0.7071 0.7071 -0.7071 0.7071 150 110 cm -30 -30 60 60 re f Q
            1 0 0 rg 230 200 m 280 200 l 255 250 l h f
            1 0.6 0 rg 230 100 m 230 150 280 150 280 100 c h f
            0 G 4 w 20 60 100 30 re S
            q 150 60 60 30 re W n BT /F1 14 Tf 150 70 Td ({RedactTerm}) Tj ET Q
            """);
        return doc.Build();
    }

    // One page 300 x 300 whose document and page structure carries the term "Zephyrstraße 17" (a literal string with the single byte 0xDF)
    // in every place a rebuild of the document could keep it: catalog /OpenAction and /Names /JavaScript (both JavaScript), /PageLabels
    // (prefix), /Metadata (XMP), /StructTreeRoot (/Alt of a structure element), /OCProperties (layer name; the page text in that layer is
    // content), the info dictionary (/Title, /Keywords); the page /Thumb (a stream), /PieceInfo (private data), page /Metadata (XMP), /AA
    // (JavaScript), a Link annotation (/Contents, a URI action) and a text field whose /V, /DV and appearance are in the PARENT field
    // /T (Group) with a child widget that has no value of its own. The page content shows "Structure fixture" only.
    private static byte[] RedactStructure()
    {
        var doc = new PdfDoc("Pagewing Schwärzen: Struktur") { InfoEntries = $"/Keywords ({RedactTerm}) /Subject ({RedactTerm})" };
        var helvetica = doc.Add(Helvetica);
        var page = doc.Reserve();
        var thumb = doc.AddStream("/Type /XObject /Subtype /Image /Width 8 /Height 8 /BitsPerComponent 8 /ColorSpace /DeviceGray", Enumerable.Repeat((byte)0x66, 64).ToArray());
        string XmpOf(string text) => "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + $"<rdf:Description xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:description>{text}</dc:description></rdf:Description></rdf:RDF></x:xmpmeta>\n<?xpacket end=\"w\"?>";
        var catalogXmp = doc.AddStream("/Type /Metadata /Subtype /XML", Text(XmpOf(RedactTerm)));
        var pageXmp = doc.AddStream("/Type /Metadata /Subtype /XML", Text(XmpOf(RedactTerm)));
        var script = doc.Add($"<< /S /JavaScript /JS (var text = '{RedactTerm}';) >>");
        var pageScript = doc.Add($"<< /S /JavaScript /JS (var other = '{RedactTerm}';) >>");
        var layer = doc.Add($"<< /Type /OCG /Name ({RedactTerm} layer) >>");
        var element = doc.Reserve();
        var structRoot = doc.Add($"<< /Type /StructTreeRoot /K [{element} 0 R] >>");
        doc.Set(element, $"<< /Type /StructElem /S /P /P {structRoot} 0 R /Pg {page} 0 R /Alt ({RedactTerm}) /K 0 >>");

        var appearance = doc.AddStream($"/Type /XObject /Subtype /Form /BBox [0 0 200 24] /Resources << /Font << /F1 {helvetica} 0 R >> >>", Text($"BT /F1 12 Tf 2 8 Td ({RedactTerm}) Tj ET"));
        var parent = doc.Reserve();
        var child = doc.Add($"<< /Type /Annot /Subtype /Widget /Rect [20 100 220 124] /F 4 /P {page} 0 R /Parent {parent} 0 R /AP << /N {appearance} 0 R >> /MK << /CA ({RedactTerm}) >> >>");
        doc.Set(parent, $"<< /FT /Tx /T (Group) /V ({RedactTerm}) /DV ({RedactTerm}) {FormDa} /Kids [{child} 0 R] >>");
        var link = doc.Add($"<< /Type /Annot /Subtype /Link /Rect [20 150 120 170] /Border [0 0 0] /Contents ({RedactTerm}) /A << /S /URI /URI (http://example.invalid/) >> /P {page} 0 R >>");

        doc.AddPage(
            $"/MediaBox [0 0 300 300] /Thumb {thumb} 0 R /PieceInfo << /Pagewing << /Private ({RedactTerm}) >> >> /Metadata {pageXmp} 0 R /AA << /O {pageScript} 0 R >> /StructParents 0 /Annots [{link} 0 R {child} 0 R]",
            $"<< /Font << /F1 {helvetica} 0 R >> /Properties << /oc1 {layer} 0 R >> >>",
            $"""
            BT /F1 12 Tf 20 270 Td (Structure fixture) Tj ET
            /OC /oc1 BDC BT /F1 12 Tf 20 240 Td ({RedactTerm}) Tj ET EMC
            """,
            page);
        doc.CatalogEntries = $"/OpenAction {script} 0 R /Names << /JavaScript << /Names [(init) {script} 0 R] >> >> /PageLabels << /Nums [0 << /P ({RedactTerm} - ) >>] >> "
            + $"/Metadata {catalogXmp} 0 R /StructTreeRoot {structRoot} 0 R /OCProperties << /OCGs [{layer} 0 R] /D << /BaseState /ON /OFF [{layer} 0 R] >> >> "
            + AcroFormEntry(doc, [parent]);
        return doc.Build();
    }

    // Patterns for "search and redact" (milestone 14.1). One page 400 x 600, Helvetica 10, one text object per line from y = 570 down
    // in steps of 16. Page text: IBANs (valid DE, GB, FR; DE with a wrong check digit; NL ungrouped; GB grouped with no-break spaces),
    // cards (Luhn-valid 4111..., hyphenated 4111-..., 5555...; Luhn-invalid grouped 1234 ...), telephone numbers (+49, 0711/, (0711)),
    // e-mail addresses (two good, "john@localhost" and "@example.com" as non-addresses), traps (a date, an ISBN, a version, a 20-digit
    // run) and an IBAN split across two lines. A Text annotation carries an IBAN, two text fields carry an e-mail address and a
    // telephone number. Expected hits (test FixtureYieldsTheExpectedHitsPerKindAndCertainty): Iban 6 ChecksumOk + 1 ChecksumFailed,
    // Card 3 ChecksumOk + 1 ChecksumFailed, Phone 4 ShapeOnly, Email 3 ShapeOnly.
    private static byte[] RedactPatterns()
    {
        var doc = new PdfDoc("Pagewing Schwärzen: Muster");
        var helvetica = doc.Add(Helvetica);
        var page1 = doc.Reserve();
        var lines = new[]
        {
            "IBAN DE89 3704 0044 0532 0130 00",
            "IBAN GB82 WEST 1234 5698 7654 32",
            "IBAN FR14 2004 1010 0505 0001 3M02 606",
            "IBAN (typo) DE88 3704 0044 0532 0130 00",
            "IBAN NL91ABNA0417164300",
            "IBAN GB82\\240WEST\\2401234\\2405698\\2407654\\24032",
            "Card 4111 1111 1111 1111",
            "Card 4111-1111-1111-1111",
            "Card 5555 5555 5555 4444",
            "Card (typo) 1234 5678 9012 3456",
            "Tel +49 711 1234567",
            "Fax 0711/1234567",
            "Mobil (0711) 123 45 67",
            "Mail max.mustermann@example.com",
            "Mail a+b@sub.example.co.uk",
            "No mail john@localhost and @example.com",
            "Datum 01.02.2024 und ISBN 978-3-16-148410-0 Version 1.2.3",
            "Run 12345678901234567890",
            "Konto DE89 3704 0044 0",
            "532 0130 00 (split)",
        };

        var content = new StringBuilder();
        var y = 570;
        foreach (var line in lines)
        {
            content.Append(CultureInfo.InvariantCulture, $"BT /F1 10 Tf 20 {y} Td ({line}) Tj ET\n");
            y -= 16;
        }

        var note = doc.Add($"<< /Type /Annot /Subtype /Text /Rect [350 560 368 578] /Name /Note /F 28 /NM (pattern-note-0001) /Contents (Please transfer to DE89 3704 0044 0532 0130 00 today) /C [1 1 0] /P {page1} 0 R >>");
        var mail = FormField(doc, page1, FormRect(20, 100, 220, 125), $"/FT /Tx /T (Mail) /V (form@example.org) {FormDa}");
        var phone = FormField(doc, page1, FormRect(20, 60, 220, 85), $"/FT /Tx /T (Phone) /V (+49 711 7654321) {FormDa}");

        doc.AddPage(
            $"/MediaBox [0 0 400 600] /Annots [{AnnotRefs([note, mail, phone])}]",
            $"<< /Font << /F1 {helvetica} 0 R >> >>",
            content.ToString(),
            page1);
        doc.CatalogEntries = AcroFormEntry(doc, [mail, phone]);
        return doc.Build();
    }

    // The page tree in object streams, as a writer with "compressed object streams" produces it: the catalog, the page tree and both
    // page dictionaries are objects inside one /ObjStm, the file ends in a cross-reference STREAM (no classic xref section, no trailer
    // keyword). Page 2 carries /Thumb and /PieceInfo. The streams are written unfiltered (valid; no compressor whose output would
    // differ between runtimes). Milestone 14: the page-key scan of the redaction reads only what PDFium writes, where nothing is
    // compressed; this file shows it what it must refuse to read.
    private static byte[] RedactObjectStreams()
    {
        const string page = "/Type /Page /Parent 7 0 R /MediaBox [0 0 300 300] /Resources << /Font << /F1 1 0 R >> >>";
        var inner = new (int Number, string Body)[]
        {
            (6, "<< /Type /Catalog /Pages 7 0 R >>"),
            (7, "<< /Type /Pages /Count 2 /Kids [8 0 R 9 0 R] >>"),
            (8, $"<< {page} /Contents 2 0 R >>"),
            (9, $"<< {page} /Contents 3 0 R /Thumb 4 0 R /PieceInfo << /Pagewing << /Private (objstm) >> >> >>"),
        };
        var header = new StringBuilder();
        var body = new StringBuilder();
        foreach (var (number, text) in inner)
        {
            header.Append(CultureInfo.InvariantCulture, $"{number} {body.Length} ");
            body.Append(text).Append('\n');
        }

        var objectStream = PdfDoc.StreamBody($"/Type /ObjStm /N {inner.Length} /First {header.Length}", PdfDoc.Latin1(header.ToString() + body));

        var output = new MemoryStream();
        output.Write(PdfDoc.Latin1("%PDF-1.7\n"));
        output.Write([0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A]);
        var offsets = new Dictionary<int, long>();
        void Write(int number, byte[] data)
        {
            offsets[number] = output.Position;
            output.Write(PdfDoc.Latin1($"{number} 0 obj\n"));
            output.Write(data);
            output.Write(PdfDoc.Latin1("\nendobj\n"));
        }

        Write(1, PdfDoc.Latin1(Helvetica));
        Write(2, PdfDoc.StreamBody(string.Empty, PdfDoc.Latin1("BT /F1 14 Tf 20 260 Td (Object stream fixture, page one) Tj ET")));
        Write(3, PdfDoc.StreamBody(string.Empty, PdfDoc.Latin1("BT /F1 14 Tf 20 260 Td (Object stream fixture, page two) Tj ET")));
        Write(4, PdfDoc.StreamBody("/Type /XObject /Subtype /Image /Width 8 /Height 8 /BitsPerComponent 8 /ColorSpace /DeviceGray", Enumerable.Repeat((byte)0x66, 64).ToArray()));
        Write(5, objectStream);

        // The cross-reference stream: entries of /W [1 4 2] for the objects 0 to 10: free, plain objects with their offset, compressed
        // objects with their object stream and index.
        const int size = 11;
        var xrefOffset = output.Position;
        var table = new List<byte>();
        void Entry(int type, long second, int third)
        {
            table.Add((byte)type);
            table.AddRange([(byte)(second >> 24), (byte)(second >> 16), (byte)(second >> 8), (byte)second]);
            table.AddRange([(byte)(third >> 8), (byte)third]);
        }

        Entry(0, 0, 65535);
        for (var number = 1; number < size; number++)
        {
            var index = Array.FindIndex(inner, i => i.Number == number);
            if (index >= 0)
            {
                Entry(2, 5, index);
            }
            else if (offsets.TryGetValue(number, out var offset))
            {
                Entry(1, offset, 0);
            }
            else
            {
                Entry(1, xrefOffset, 0); // object 10: the cross-reference stream itself
            }
        }

        output.Write(PdfDoc.Latin1("10 0 obj\n"));
        output.Write(PdfDoc.StreamBody($"/Type /XRef /Size {size} /W [1 4 2] /Root 6 0 R", [.. table]));
        output.Write(PdfDoc.Latin1(string.Create(CultureInfo.InvariantCulture, $"\nendobj\nstartxref\n{xrefOffset}\n%%EOF\n")));
        return output.ToArray();
    }

    // Flate data made only of stored (uncompressed) deflate blocks: valid zlib, identical on every runtime.
    private static byte[] StoredFlate(byte[] data)
    {
        var output = new MemoryStream();
        output.WriteByte(0x78);
        output.WriteByte(0x01);
        var position = 0;
        do
        {
            var length = Math.Min(65535, data.Length - position);
            output.WriteByte(position + length >= data.Length ? (byte)1 : (byte)0);
            output.WriteByte((byte)(length & 0xFF));
            output.WriteByte((byte)(length >> 8));
            output.WriteByte((byte)(~length & 0xFF));
            output.WriteByte((byte)((~length >> 8) & 0xFF));
            output.Write(data, position, length);
            position += length;
        }
        while (position < data.Length);

        uint a = 1;
        uint b = 0;
        foreach (var value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }

        var adler = (b << 16) | a;
        output.WriteByte((byte)(adler >> 24));
        output.WriteByte((byte)(adler >> 16));
        output.WriteByte((byte)(adler >> 8));
        output.WriteByte((byte)adler);
        return output.ToArray();
    }

    // A baseline grey JPEG that stores only the DC coefficient of every 8 x 8 block (the mean of the block, quantiser 8, so the
    // DC value is mean - 128), with a one-symbol AC table (end of block). Blocks of equal value make the decode exact.
    private static byte[] FlatBlockJpeg(byte[] pixels, int size)
    {
        // Standard luminance DC table: lengths 0,1,5,1,1,1,1,1,1,0,... and the symbols 0 to 11; the codes follow the canonical assignment.
        int[] counts = [0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0];
        var codes = new (int Code, int Length)[12];
        var code = 0;
        var symbol = 0;
        for (var length = 1; length <= 16; length++)
        {
            for (var i = 0; i < counts[length - 1]; i++)
            {
                codes[symbol++] = (code++, length);
            }

            code <<= 1;
        }

        var output = new MemoryStream();
        void Marker(byte marker, byte[] payload)
        {
            output.WriteByte(0xFF);
            output.WriteByte(marker);
            output.WriteByte((byte)((payload.Length + 2) >> 8));
            output.WriteByte((byte)(payload.Length + 2));
            output.Write(payload);
        }

        output.WriteByte(0xFF);
        output.WriteByte(0xD8);
        Marker(0xDB, [.. new byte[] { 0x00 }, .. Enumerable.Repeat((byte)8, 64)]);
        Marker(0xC0, [8, (byte)(size >> 8), (byte)size, (byte)(size >> 8), (byte)size, 1, 1, 0x11, 0]);
        Marker(0xC4, [.. new byte[] { 0x00 }, .. counts.Select(c => (byte)c), .. Enumerable.Range(0, 12).Select(v => (byte)v)]);
        Marker(0xC4, [.. new byte[] { 0x10, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, 0x00]);
        Marker(0xDA, [1, 1, 0x00, 0, 63, 0]);

        var bits = 0;
        var bitCount = 0;
        void Write(int value, int length)
        {
            for (var i = length - 1; i >= 0; i--)
            {
                bits = (bits << 1) | ((value >> i) & 1);
                if (++bitCount == 8)
                {
                    output.WriteByte((byte)bits);
                    if (bits == 0xFF)
                    {
                        output.WriteByte(0);
                    }

                    bits = 0;
                    bitCount = 0;
                }
            }
        }

        var previous = 0;
        for (var blockY = 0; blockY < size / 8; blockY++)
        {
            for (var blockX = 0; blockX < size / 8; blockX++)
            {
                var sum = 0;
                for (var y = 0; y < 8; y++)
                {
                    for (var x = 0; x < 8; x++)
                    {
                        sum += pixels[(((blockY * 8) + y) * size) + (blockX * 8) + x];
                    }
                }

                var dc = ((sum + 32) / 64) - 128;
                var difference = dc - previous;
                previous = dc;
                var magnitude = Math.Abs(difference);
                var category = 0;
                while ((1 << category) <= magnitude)
                {
                    category++;
                }

                Write(codes[category].Code, codes[category].Length);
                if (category > 0)
                {
                    Write(difference >= 0 ? difference : difference + (1 << category) - 1, category);
                }

                Write(0, 1); // end of block: the only AC symbol has the code "0"
            }
        }

        while (bitCount != 0)
        {
            Write(1, 1);
        }

        output.WriteByte(0xFF);
        output.WriteByte(0xD9);
        return output.ToArray();
    }

    private static int[] ThreeEmptyPages(PdfDoc doc)
    {
        var pages = new int[3];
        for (var i = 0; i < pages.Length; i++)
        {
            pages[i] = doc.AddPage("/MediaBox [0 0 300 400]", "<< >>", "0.9 0.9 0.9 rg 20 20 260 360 re f");
        }

        return pages;
    }

    private static byte[] Text(string value) => PdfDoc.Latin1(value);
}

// The few facts of a TrueType file the font fixtures need: the metrics (head, hhea, hmtx) and the Unicode cmap (format 4 or 12).
sealed class TtfInfo
{
    private readonly Dictionary<string, (int Offset, int Length)> tables = [];
    private readonly int cmapOffset;
    private readonly int numberOfHMetrics;

    public TtfInfo(byte[] data)
    {
        Data = data;
        var count = U16(4);
        for (var i = 0; i < count; i++)
        {
            var entry = 12 + (i * 16);
            tables[Encoding.ASCII.GetString(data, entry, 4)] = ((int)U32(entry + 8), (int)U32(entry + 12));
        }

        var head = tables["head"].Offset;
        UnitsPerEm = U16(head + 18);
        XMin = S16(head + 36);
        YMin = S16(head + 38);
        XMax = S16(head + 40);
        YMax = S16(head + 42);
        var hhea = tables["hhea"].Offset;
        Ascent = S16(hhea + 4);
        Descent = S16(hhea + 6);
        numberOfHMetrics = U16(hhea + 34);

        var cmap = tables["cmap"].Offset;
        var subtables = U16(cmap + 2);
        for (var i = 0; i < subtables; i++)
        {
            var record = cmap + 4 + (i * 8);
            var platform = U16(record);
            var encoding = U16(record + 2);
            var offset = cmap + (int)U32(record + 4);
            var format = U16(offset);
            if ((platform == 3 && encoding == 10 && format == 12) || (cmapOffset == 0 && ((platform == 3 && encoding == 1) || platform == 0) && format is 4 or 12))
            {
                cmapOffset = offset;
            }
        }

        if (cmapOffset == 0)
        {
            throw new InvalidOperationException("Die Schrift hat keine Unicode-cmap (Format 4 oder 12).");
        }
    }

    public byte[] Data { get; }

    public int UnitsPerEm { get; }

    public int XMin { get; }

    public int YMin { get; }

    public int XMax { get; }

    public int YMax { get; }

    public int Ascent { get; }

    public int Descent { get; }

    // The glyph id of a Unicode code point, 0 if the font has none.
    public int GlyphFor(int codePoint)
    {
        var format = U16(cmapOffset);
        if (format == 12)
        {
            var groups = (int)U32(cmapOffset + 12);
            for (var i = 0; i < groups; i++)
            {
                var group = cmapOffset + 16 + (i * 12);
                var start = U32(group);
                var end = U32(group + 4);
                if (codePoint >= start && codePoint <= end)
                {
                    return (int)(U32(group + 8) + (uint)codePoint - start);
                }
            }

            return 0;
        }

        var segments = U16(cmapOffset + 6) / 2;
        var endCodes = cmapOffset + 14;
        var startCodes = endCodes + (segments * 2) + 2;
        var deltas = startCodes + (segments * 2);
        var rangeOffsets = deltas + (segments * 2);
        for (var i = 0; i < segments; i++)
        {
            if (U16(endCodes + (i * 2)) < codePoint)
            {
                continue;
            }

            if (U16(startCodes + (i * 2)) > codePoint)
            {
                return 0;
            }

            var rangeOffset = U16(rangeOffsets + (i * 2));
            if (rangeOffset == 0)
            {
                return (codePoint + U16(deltas + (i * 2))) & 0xFFFF;
            }

            var glyph = U16(rangeOffsets + (i * 2) + rangeOffset + ((codePoint - U16(startCodes + (i * 2))) * 2));
            return glyph == 0 ? 0 : (glyph + U16(deltas + (i * 2))) & 0xFFFF;
        }

        return 0;
    }

    // The advance width of a glyph in font units.
    public int AdvanceOf(int glyph)
    {
        var hmtx = tables["hmtx"].Offset;
        return U16(hmtx + (Math.Min(glyph, numberOfHMetrics - 1) * 4));
    }

    // The outline of a glyph: its contours as points in font units (y up) with the on-curve flag; a composite glyph is resolved (offsets and
    // scales of its components applied, no point matching). An empty glyph (a space) has no contours.
    public List<List<(double X, double Y, bool On)>> GlyphContours(int glyph, int depth = 0)
    {
        if (depth > 8)
        {
            throw new InvalidOperationException("Verschachtelte Teilglyphen zu tief.");
        }

        var head = tables["head"].Offset;
        var longOffsets = U16(head + 50) == 1;
        var glyf = tables["glyf"].Offset;
        var loca = tables["loca"].Offset;
        int start = longOffsets ? (int)U32(loca + (glyph * 4)) : U16(loca + (glyph * 2)) * 2;
        int end = longOffsets ? (int)U32(loca + ((glyph + 1) * 4)) : U16(loca + ((glyph + 1) * 2)) * 2;
        var contours = new List<List<(double X, double Y, bool On)>>();
        if (end <= start)
        {
            return contours;
        }

        var at = glyf + start;
        var count = S16(at);
        at += 10;
        if (count >= 0)
        {
            var ends = new int[count];
            for (var i = 0; i < count; i++)
            {
                ends[i] = U16(at + (i * 2));
            }

            at += count * 2;
            at += 2 + U16(at); // the instructions
            var pointCount = count == 0 ? 0 : ends[^1] + 1;
            var flags = new byte[pointCount];
            for (var i = 0; i < pointCount;)
            {
                var flag = Data[at++];
                flags[i++] = flag;
                if ((flag & 8) != 0)
                {
                    var repeat = Data[at++];
                    while (repeat-- > 0 && i < pointCount)
                    {
                        flags[i++] = flag;
                    }
                }
            }

            var xs = new int[pointCount];
            var ys = new int[pointCount];
            var value = 0;
            for (var i = 0; i < pointCount; i++)
            {
                if ((flags[i] & 2) != 0)
                {
                    var delta = Data[at++];
                    value += (flags[i] & 16) != 0 ? delta : -delta;
                }
                else if ((flags[i] & 16) == 0)
                {
                    value += S16(at);
                    at += 2;
                }

                xs[i] = value;
            }

            value = 0;
            for (var i = 0; i < pointCount; i++)
            {
                if ((flags[i] & 4) != 0)
                {
                    var delta = Data[at++];
                    value += (flags[i] & 32) != 0 ? delta : -delta;
                }
                else if ((flags[i] & 32) == 0)
                {
                    value += S16(at);
                    at += 2;
                }

                ys[i] = value;
            }

            var first = 0;
            foreach (var last in ends)
            {
                var contour = new List<(double X, double Y, bool On)>();
                for (var i = first; i <= last; i++)
                {
                    contour.Add((xs[i], ys[i], (flags[i] & 1) != 0));
                }

                contours.Add(contour);
                first = last + 1;
            }

            return contours;
        }

        // A composite glyph: components with an offset and an optional scale.
        while (true)
        {
            var flags = U16(at);
            var component = U16(at + 2);
            at += 4;
            if ((flags & 2) == 0)
            {
                throw new InvalidOperationException("Teilglyphen mit Punktzuordnung werden nicht unterstuetzt.");
            }

            double dx, dy;
            if ((flags & 1) != 0)
            {
                dx = S16(at);
                dy = S16(at + 2);
                at += 4;
            }
            else
            {
                dx = (sbyte)Data[at];
                dy = (sbyte)Data[at + 1];
                at += 2;
            }

            double a = 1, b = 0, c = 0, d = 1;
            if ((flags & 8) != 0)
            {
                a = d = S16(at) / 16384.0;
                at += 2;
            }
            else if ((flags & 0x40) != 0)
            {
                a = S16(at) / 16384.0;
                d = S16(at + 2) / 16384.0;
                at += 4;
            }
            else if ((flags & 0x80) != 0)
            {
                a = S16(at) / 16384.0;
                b = S16(at + 2) / 16384.0;
                c = S16(at + 4) / 16384.0;
                d = S16(at + 6) / 16384.0;
                at += 8;
            }

            foreach (var part in GlyphContours(component, depth + 1))
            {
                contours.Add([.. part.Select(p => ((a * p.X) + (c * p.Y) + dx, (b * p.X) + (d * p.Y) + dy, p.On))]);
            }

            if ((flags & 0x20) == 0)
            {
                break;
            }
        }

        return contours;
    }

    // The number of glyphs of the font.
    public int GlyphCount => U16(tables["maxp"].Offset + 4);

    // The bytes of a table, or null if the font has none.
    public byte[]? Table(string tag) => tables.TryGetValue(tag, out var entry) ? Data[entry.Offset..(entry.Offset + entry.Length)] : null;

    // The raw glyph data of a glyph (empty for a glyph without outline).
    public byte[] RawGlyph(int glyph)
    {
        var longOffsets = U16(tables["head"].Offset + 50) == 1;
        var loca = tables["loca"].Offset;
        int start = longOffsets ? (int)U32(loca + (glyph * 4)) : U16(loca + (glyph * 2)) * 2;
        int end = longOffsets ? (int)U32(loca + ((glyph + 1) * 4)) : U16(loca + ((glyph + 1) * 2)) * 2;
        var glyf = tables["glyf"].Offset;
        return end <= start ? [] : Data[(glyf + start)..(glyf + end)];
    }

    // The glyph ids a composite glyph is made of (none for a simple glyph).
    public List<int> ComponentsOf(int glyph)
    {
        var raw = RawGlyph(glyph);
        var result = new List<int>();
        if (raw.Length < 10 || (short)((raw[0] << 8) | raw[1]) >= 0)
        {
            return result;
        }

        var at = 10;
        while (true)
        {
            var flags = (raw[at] << 8) | raw[at + 1];
            result.Add((raw[at + 2] << 8) | raw[at + 3]);
            at += 4 + ((flags & 1) != 0 ? 4 : 2);
            at += (flags & 8) != 0 ? 2 : (flags & 0x40) != 0 ? 4 : (flags & 0x80) != 0 ? 8 : 0;
            if ((flags & 0x20) == 0)
            {
                return result;
            }
        }
    }

    private int U16(int offset) => (Data[offset] << 8) | Data[offset + 1];

    private int S16(int offset) => (short)U16(offset);

    private uint U32(int offset) => ((uint)Data[offset] << 24) | ((uint)Data[offset + 1] << 16) | ((uint)Data[offset + 2] << 8) | Data[offset + 3];
}

// A TrueType subset the way a PDF producer writes one: the glyph ids stay the same, only the glyphs that are used (and the components
// of the composite ones among them) keep their outline, the others are empty. With a cmap the table holds only the used code points
// (a simple font needs one); without, the font is meant for Identity-H, where the code is the glyph id.
static class TtfSubset
{
    public static byte[] Build(TtfInfo ttf, IEnumerable<int> codePoints, bool withCmap)
    {
        var points = codePoints.Distinct().Order().ToList();
        var keep = new SortedSet<int> { 0 };
        foreach (var point in points)
        {
            var glyph = ttf.GlyphFor(point);
            if (glyph != 0)
            {
                keep.Add(glyph);
            }
        }

        for (var added = true; added;)
        {
            added = false;
            foreach (var glyph in keep.ToList())
            {
                foreach (var component in ttf.ComponentsOf(glyph))
                {
                    added |= keep.Add(component);
                }
            }
        }

        var glyf = new MemoryStream();
        var loca = new MemoryStream();
        for (var glyph = 0; glyph <= ttf.GlyphCount; glyph++)
        {
            WriteU32(loca, (uint)glyf.Length);
            if (glyph < ttf.GlyphCount && keep.Contains(glyph))
            {
                var raw = ttf.RawGlyph(glyph);
                glyf.Write(raw);
                while (glyf.Length % 4 != 0)
                {
                    glyf.WriteByte(0);
                }
            }
        }

        var head = ttf.Table("head")!;
        head[8] = head[9] = head[10] = head[11] = 0;
        head[50] = 0;
        head[51] = 1;
        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["head"] = head,
            ["hhea"] = ttf.Table("hhea")!,
            ["maxp"] = ttf.Table("maxp")!,
            ["hmtx"] = ttf.Table("hmtx")!,
            ["loca"] = loca.ToArray(),
            ["glyf"] = glyf.ToArray(),
        };
        foreach (var tag in new[] { "cvt ", "fpgm", "prep" })
        {
            if (ttf.Table(tag) is { } table)
            {
                tables[tag] = table;
            }
        }

        if (withCmap)
        {
            tables["cmap"] = Cmap(points.Where(p => p <= 0xFFFF && ttf.GlyphFor(p) != 0).Select(p => (p, ttf.GlyphFor(p))).ToList());
        }

        return Assemble(tables);
    }

    private static byte[] Cmap(List<(int Code, int Glyph)> pairs)
    {
        var segments = pairs.Count + 1;
        var body = new MemoryStream();
        WriteU16(body, 4);
        WriteU16(body, 16 + (segments * 8));
        WriteU16(body, 0);
        WriteU16(body, segments * 2);
        var entrySelector = (int)Math.Floor(Math.Log2(segments));
        WriteU16(body, (1 << entrySelector) * 2);
        WriteU16(body, entrySelector);
        WriteU16(body, (segments * 2) - ((1 << entrySelector) * 2));
        foreach (var (code, _) in pairs)
        {
            WriteU16(body, code);
        }

        WriteU16(body, 0xFFFF);
        WriteU16(body, 0);
        foreach (var (code, _) in pairs)
        {
            WriteU16(body, code);
        }

        WriteU16(body, 0xFFFF);
        foreach (var (code, glyph) in pairs)
        {
            WriteU16(body, (glyph - code) & 0xFFFF);
        }

        WriteU16(body, 1);
        for (var i = 0; i < segments; i++)
        {
            WriteU16(body, 0);
        }

        var cmap = new MemoryStream();
        WriteU16(cmap, 0);
        WriteU16(cmap, 1);
        WriteU16(cmap, 3);
        WriteU16(cmap, 1);
        WriteU32(cmap, 12);
        cmap.Write(body.ToArray());
        return cmap.ToArray();
    }

    private static byte[] Assemble(SortedDictionary<string, byte[]> tables)
    {
        var count = tables.Count;
        var entrySelector = (int)Math.Floor(Math.Log2(count));
        var output = new MemoryStream();
        WriteU32(output, 0x00010000);
        WriteU16(output, count);
        WriteU16(output, (1 << entrySelector) * 16);
        WriteU16(output, entrySelector);
        WriteU16(output, (count * 16) - ((1 << entrySelector) * 16));
        var offset = 12 + (count * 16);
        foreach (var (tag, data) in tables)
        {
            output.Write(Encoding.ASCII.GetBytes(tag));
            WriteU32(output, Checksum(data));
            WriteU32(output, (uint)offset);
            WriteU32(output, (uint)data.Length);
            offset += (data.Length + 3) & ~3;
        }

        foreach (var data in tables.Values)
        {
            output.Write(data);
            for (var pad = data.Length; pad % 4 != 0; pad++)
            {
                output.WriteByte(0);
            }
        }

        return output.ToArray();
    }

    private static uint Checksum(byte[] data)
    {
        uint sum = 0;
        for (var i = 0; i < data.Length; i += 4)
        {
            uint word = 0;
            for (var j = 0; j < 4; j++)
            {
                word = (word << 8) | (i + j < data.Length ? data[i + j] : (byte)0);
            }

            sum += word;
        }

        return sum;
    }

    private static void WriteU16(Stream stream, int value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteU32(Stream stream, uint value)
    {
        WriteU16(stream, (int)(value >> 16));
        WriteU16(stream, (int)(value & 0xFFFF));
    }
}

// A tiny embedded Type 1 font (binary eexec form) whose glyphs are boxes, so a fixture can have a Type 1 font program without a font
// licence. The glyphs are the characters of the text; the glyph name of each is the PostScript name, the encoding is built in (code = ASCII / Latin-1).
static class Type1Font
{
    private static readonly Dictionary<char, string> Names = new()
    {
        [' '] = "space", [':'] = "colon", ['1'] = "one", ['7'] = "seven", ['ß'] = "germandbls",
    };

    // The codes 1 to 31 have glyphs with names no Unicode list knows ("pwctl5"): PDFium can only report the code as the character.
    public static string? NameOf(char character) =>
        Names.TryGetValue(character, out var name) ? name : char.IsAsciiLetter(character) ? character.ToString() : character is > (char)0 and < (char)32 ? "pwctl" + (int)character : null;

    public static (byte[] Data, int Length1, int Length2, int Length3) Build(string text)
    {
        var characters = text.Distinct().Order().ToArray();
        var clear = new StringBuilder();
        clear.Append("%!PS-AdobeFont-1.0: PwBox 1.0\n11 dict begin\n/FontInfo 2 dict dup begin\n/FullName (PwBox) def\n/Notice (Pagewing test font, no copyright) def\nend readonly def\n");
        clear.Append("/FontName /PwBox def\n/PaintType 0 def\n/FontType 1 def\n/FontMatrix [0.001 0 0 0.001 0 0] readonly def\n/Encoding 256 array\n0 1 255 {1 index exch /.notdef put} for\n");
        foreach (var c in characters)
        {
            clear.Append(CultureInfo.InvariantCulture, $"dup {(int)c} /{NameOf(c) ?? throw new InvalidOperationException($"Kein Glyphenname für '{c}'.")} put\n");
        }

        clear.Append("readonly def\n/FontBBox {0 0 600 750} readonly def\ncurrentdict end\ncurrentfile eexec\n");

        var privatePart = new MemoryStream();
        void Write(string value) => privatePart.Write(Encoding.Latin1.GetBytes(value));
        void Glyph(string name, byte[] charString)
        {
            var encrypted = Encrypt(charString, 4330, 4);
            Write($"/{name} {encrypted.Length} RD ");
            privatePart.Write(encrypted);
            Write(" ND\n");
        }

        Write("dup /Private 8 dict dup begin\n/RD {string currentfile exch readstring pop} executeonly def\n/ND {noaccess def} executeonly def\n/NP {noaccess put} executeonly def\n");
        Write("/BlueValues [] def\n/MinFeature {16 16} def\n/password 5839 def\n");
        Write($"2 index /CharStrings {characters.Length + 1} dict dup begin\n");
        Glyph(".notdef", CharString(300, 0));
        for (var i = 0; i < characters.Length; i++)
        {
            var c = characters[i];
            Glyph(NameOf(c)!, c == ' ' ? CharString(300, 0) : CharString(600, 300 + (((c * 37) % 12) * 40)));
        }

        Write("end\nend\nreadonly put\nnoaccess put\ndup /FontName get exch definefont pop\nmark currentfile closefile\n");

        var clearBytes = Encoding.Latin1.GetBytes(clear.ToString());
        byte[] encryptedPrivate;
        for (var attempt = 0; ; attempt++)
        {
            // Binary eexec: the first ciphertext byte must not be white space and one of the first four must not be a hex digit.
            var plain = privatePart.ToArray();
            encryptedPrivate = Encrypt(plain, 55665, 4, (byte)attempt);
            if (!IsSpace(encryptedPrivate[0]) && !encryptedPrivate.Take(4).All(IsHexOrSpace))
            {
                break;
            }
        }

        var trailer = new StringBuilder();
        for (var i = 0; i < 8; i++)
        {
            trailer.Append('0', 64).Append('\n');
        }

        trailer.Append("cleartomark\n");
        var trailerBytes = Encoding.Latin1.GetBytes(trailer.ToString());
        return ([.. clearBytes, .. encryptedPrivate, .. trailerBytes], clearBytes.Length, encryptedPrivate.Length, trailerBytes.Length);
    }

    private static bool IsSpace(byte value) => value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    private static bool IsHexOrSpace(byte value) => IsSpace(value) || (value is >= (byte)'0' and <= (byte)'9') || (value is >= (byte)'a' and <= (byte)'f') || (value is >= (byte)'A' and <= (byte)'F');

    // 0 600 hsbw, then a box of the given height: 50,0 to 550,height.
    private static byte[] CharString(int width, int height)
    {
        var bytes = new List<byte>();
        void Number(int value)
        {
            if (value is >= -107 and <= 107)
            {
                bytes.Add((byte)(value + 139));
            }
            else if (value is >= 108 and <= 1131)
            {
                value -= 108;
                bytes.Add((byte)(247 + (value >> 8)));
                bytes.Add((byte)(value & 0xFF));
            }
            else if (value is >= -1131 and <= -108)
            {
                value = -value - 108;
                bytes.Add((byte)(251 + (value >> 8)));
                bytes.Add((byte)(value & 0xFF));
            }
            else
            {
                bytes.Add(255);
                bytes.AddRange([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
            }
        }

        Number(0);
        Number(width);
        bytes.Add(13); // hsbw
        if (height > 0)
        {
            Number(50);
            Number(0);
            bytes.Add(21); // rmoveto
            Number(height);
            bytes.Add(7); // vlineto
            Number(width - 100);
            bytes.Add(6); // hlineto
            Number(-height);
            bytes.Add(7); // vlineto
            bytes.Add(9); // closepath
        }

        bytes.Add(14); // endchar
        return [.. bytes];
    }

    // Type 1 encryption: r is the key, the first lead bytes are the (fixed) random prefix.
    private static byte[] Encrypt(byte[] plain, int key, int lead, byte seed = 0)
    {
        var input = new byte[plain.Length + lead];
        for (var i = 0; i < lead; i++)
        {
            input[i] = (byte)(seed + i);
        }

        Array.Copy(plain, 0, input, lead, plain.Length);
        var output = new byte[input.Length];
        var r = key;
        for (var i = 0; i < input.Length; i++)
        {
            var cipher = (byte)(input[i] ^ (r >> 8));
            r = (((cipher + r) * 52845) + 22719) & 0xFFFF;
            output[i] = cipher;
        }

        return output;
    }
}

// Minimal writer: numbered objects, classic xref table, trailer with Info. Streams are never compressed.
sealed class PdfDoc
{
    private readonly List<byte[]?> objects = [];
    private readonly List<int> pages = [];
    private readonly int catalog;
    private readonly int pagesRoot;
    private readonly int info;
    private readonly string title;
    private long lastXref;

    public PdfDoc(string title)
    {
        this.title = title;
        catalog = Reserve();
        pagesRoot = Reserve();
        info = Add(InfoBody());
    }

    private string InfoBody() => $"<< /Title {TextString(title)} /Producer (Pagewing test generator){(InfoEntries.Length > 0 ? " " + InfoEntries : string.Empty)} >>";

    public static byte[] Latin1(string value)
    {
        var text = value.Replace("\r\n", "\n", StringComparison.Ordinal);
        var bytes = new byte[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            bytes[i] = text[i] <= 0xFF ? (byte)text[i] : throw new InvalidOperationException($"Zeichen U+{(int)text[i]:X4} ist kein Latin-1-Zeichen.");
        }

        return bytes;
    }

    // Extra entries for the catalog dictionary, for example "/Outlines 7 0 R".
    public string CatalogEntries { get; set; } = string.Empty;

    // Extra entries for the document information dictionary, for example "/Author (Quellmann)".
    public string InfoEntries { get; set; } = string.Empty;

    // Id of the content stream of the page added last by AddPage.
    public int LastContentId { get; private set; }

    // Id of an object that Build writes first in the file (the /Linearized dictionary), or null for plain id order.
    public int? FirstObject { get; set; }

    // Allocates an object id whose body is set later with Set (for forward references).
    public int Reserve()
    {
        objects.Add(null);
        return objects.Count;
    }

    public void Set(int id, string body) => objects[id - 1] = Latin1(body);

    public int Add(string body)
    {
        var id = Reserve();
        objects[id - 1] = Latin1(body);
        return id;
    }

    public int AddStream(string entries, byte[] data)
    {
        var id = Reserve();
        objects[id - 1] = StreamBody(entries, data);
        return id;
    }

    // The body of a stream object (dictionary, stream, data, endstream).
    public static byte[] StreamBody(string entries, byte[] data)
    {
        var head = Latin1($"<< {entries} /Length {data.Length} >>\nstream\n");
        var tail = Latin1("\nendstream");
        return [.. head, .. data, .. tail];
    }

    // pageEntries: MediaBox, Rotate, CropBox, Annots ...; resources: a resource dictionary; content: the page content stream.
    // reservedId: an id from Reserve, for pages that other objects (links) point to before the page is added.
    public int AddPage(string pageEntries, string resources, string content, int? reservedId = null)
    {
        var contentId = AddStream(string.Empty, Latin1(content));
        LastContentId = contentId;
        var body = $"<< /Type /Page /Parent {pagesRoot} 0 R {pageEntries} /Resources {resources} /Contents {contentId} 0 R >>";
        var id = reservedId ?? Reserve();
        Set(id, body);
        pages.Add(id);
        return id;
    }

    public byte[] Build()
    {
        var kids = string.Join(' ', pages.Select(p => $"{p} 0 R"));
        objects[catalog - 1] = Latin1($"<< /Type /Catalog /Pages {pagesRoot} 0 R{(CatalogEntries.Length > 0 ? " " + CatalogEntries : string.Empty)} >>");
        objects[pagesRoot - 1] = Latin1($"<< /Type /Pages /Count {pages.Count} /Kids [{kids}] >>");
        objects[info - 1] = Latin1(InfoBody());

        var output = new MemoryStream();
        output.Write(Latin1("%PDF-1.7\n"));
        output.Write([0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A]);

        // FirstObject (the /Linearized dictionary) is written before all others; the xref table still lists the ids in order.
        var order = Enumerable.Range(0, objects.Count).OrderBy(i => i + 1 == FirstObject ? 0 : 1).ThenBy(i => i).ToList();
        var offsets = new long[objects.Count];
        foreach (var i in order)
        {
            offsets[i] = output.Position;
            output.Write(Latin1($"{i + 1} 0 obj\n"));
            output.Write(objects[i] ?? throw new InvalidOperationException($"Objekt {i + 1} wurde nie gesetzt."));
            output.Write(Latin1("\nendobj\n"));
        }

        var xref = output.Position;
        lastXref = xref;
        var table = new StringBuilder();
        table.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            table.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        table.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root {catalog} 0 R /Info {info} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        output.Write(Latin1(table.ToString()));
        return output.ToArray();
    }

    // Builds the file and appends an incremental update that replaces the object id with newBody (a new xref section, /Prev
    // pointing to the xref of the first revision). The bytes of the first revision stay the exact beginning of the file.
    public byte[] BuildWithRevision(int id, byte[] newBody)
    {
        var first = Build();
        var output = new MemoryStream();
        output.Write(first);
        var offset = output.Position;
        output.Write(Latin1($"{id} 0 obj\n"));
        output.Write(newBody);
        output.Write(Latin1("\nendobj\n"));
        var xref = output.Position;
        output.Write(Latin1(string.Create(
            CultureInfo.InvariantCulture,
            $"xref\n{id} 1\n{offset:D10} 00000 n \ntrailer\n<< /Size {objects.Count + 1} /Root {catalog} 0 R /Info {info} 0 R /Prev {lastXref} >>\nstartxref\n{xref}\n%%EOF\n")));
        return output.ToArray();
    }

    // ASCII text becomes a literal string; everything else a UTF-16BE string with byte order mark.
    public static string TextString(string value)
    {
        if (value.All(c => c is >= ' ' and <= '~'))
        {
            return "(" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal) + ")";
        }

        var hex = new StringBuilder("<FEFF");
        foreach (var unit in value)
        {
            hex.Append(CultureInfo.InvariantCulture, $"{(int)unit:X4}");
        }

        return hex.Append('>').ToString();
    }
}

// The text edit fixtures of milestone 16. Geist Regular (SIL OFL 1.1) is the only embedded font program; the "subset" files hold a real
// subset of it (glyph ids unchanged, only the used glyphs keep an outline, see TtfSubset). text-edit-runs.tsv lists the lines the tests
// look up: file, page from 1, text, font kind (TextRunFontKind), left, baseline (both in points from the top left of the displayed page,
// of the origin of the line), size. For turned text the origin is the start of the line.
static class TextEditPdfs
{
    private const string Helvetica = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";
    private const string HelveticaBold = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>";
    private const string TimesRoman = "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman /Encoding /WinAnsiEncoding >>";

    // text-edit-hostile.pdf: the number of runs on page 1 and the length of the run on page 2.
    public const int HostileRunCount = 10000;
    public const int HostileRunLength = 2001;

    public static List<KeyValuePair<string, byte[]>> Files()
    {
        var runs = new StringBuilder();
        var ttf = new TtfInfo(File.ReadAllBytes(RepositoryFile("src", "Pagewing.Core", "Assets", "Fonts", "Geist-Regular.ttf")));
        return
        [
            new("text-edit-subset.pdf", Subset(ttf, runs)),
            new("text-edit-standard.pdf", Standard(runs)),
            new("text-edit-full.pdf", Full(ttf, runs)),
            new("text-edit-styles.pdf", Styles(runs)),
            new("text-edit-rotated.pdf", Rotated(runs)),
            new("text-edit-locked.pdf", Locked(ttf, runs)),
            new("text-edit-hostile.pdf", Hostile()),
            new("text-edit-runs.tsv", Encoding.UTF8.GetBytes(runs.ToString())),
        ];
    }

    private static void Record(StringBuilder into, string file, int page, string text, string kind, double left, double baseline, double size) =>
        into.Append(CultureInfo.InvariantCulture, $"{file}\t{page}\t{text}\t{kind}\t{left}\t{baseline}\t{size}\n");

    private static string RepositoryFile(params string[] parts)
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Pagewing.slnx")))
            {
                return Path.Combine([directory.FullName, .. parts]);
            }
        }

        throw new DirectoryNotFoundException("Pagewing.slnx nicht gefunden.");
    }

    private static string Scaled(TtfInfo ttf, int value) => ((value * 1000) / ttf.UnitsPerEm).ToString(CultureInfo.InvariantCulture);

    private static int Descriptor(PdfDoc doc, TtfInfo ttf, byte[] program, string name)
    {
        var file = doc.AddStream($"/Length1 {program.Length}", program);
        return doc.Add(
            $"<< /Type /FontDescriptor /FontName /{name} /Flags 32 /FontBBox [{Scaled(ttf, ttf.XMin)} {Scaled(ttf, ttf.YMin)} {Scaled(ttf, ttf.XMax)} {Scaled(ttf, ttf.YMax)}] "
            + $"/ItalicAngle 0 /Ascent {Scaled(ttf, ttf.Ascent)} /Descent {Scaled(ttf, ttf.Descent)} /CapHeight 700 /StemV 80 /FontFile2 {file} 0 R >>");
    }

    // A simple TrueType font (WinAnsi); the widths of the characters not in "used" are 0 (a subset has no outline for them).
    private static int SimpleTrueType(PdfDoc doc, TtfInfo ttf, byte[] program, string name, string? used)
    {
        var widths = new StringBuilder();
        for (var code = 32; code <= 255; code++)
        {
            var has = (code is (>= 32 and <= 126) or (>= 160 and <= 255)) && (used is null || used.Contains((char)code, StringComparison.Ordinal));
            var glyph = has ? ttf.GlyphFor(code) : 0;
            widths.Append(CultureInfo.InvariantCulture, $"{(glyph == 0 ? "0" : Scaled(ttf, ttf.AdvanceOf(glyph)))} ");
        }

        var descriptor = Descriptor(doc, ttf, program, name);
        return doc.Add($"<< /Type /Font /Subtype /TrueType /BaseFont /{name} /FirstChar 32 /LastChar 255 /Widths [{widths}] /Encoding /WinAnsiEncoding /FontDescriptor {descriptor} 0 R >>");
    }

    // A Type0 font with Identity-H (or -V): the code is the glyph id; /ToUnicode lists the characters of "used" only, as a producer writes it.
    private static int Type0(PdfDoc doc, TtfInfo ttf, byte[] program, string name, string used, string encoding = "Identity-H")
    {
        var distinct = used.Distinct().ToArray();
        var widths = new StringBuilder();
        foreach (var c in distinct)
        {
            var glyph = ttf.GlyphFor(c);
            widths.Append(CultureInfo.InvariantCulture, $"{glyph} [{Scaled(ttf, ttf.AdvanceOf(glyph))}] ");
        }

        var descriptor = Descriptor(doc, ttf, program, name);
        var cid = doc.Add(
            $"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{name} /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
            + $"/FontDescriptor {descriptor} 0 R /CIDToGIDMap /Identity /DW 600 /W [{widths}] >>");
        var map = new StringBuilder();
        map.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        map.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        map.Append(CultureInfo.InvariantCulture, $"{distinct.Length} beginbfchar\n");
        foreach (var c in distinct)
        {
            map.Append(CultureInfo.InvariantCulture, $"<{ttf.GlyphFor(c):X4}> <{(int)c:X4}>\n");
        }

        map.Append("endbfchar\nendcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        var toUnicode = doc.AddStream(string.Empty, PdfDoc.Latin1(map.ToString()));
        return doc.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /{name} /Encoding /{encoding} /DescendantFonts [{cid} 0 R] /ToUnicode {toUnicode} 0 R >>");
    }

    private static string Hex(TtfInfo ttf, string text) => "<" + string.Concat(text.Select(c => ttf.GlyphFor(c).ToString("X4", CultureInfo.InvariantCulture))) + ">";

    // Page 300 x 200. Line 1 (Type0 Identity-H, subset of the characters of the line, size 14 at 20,170): "Invoice total: 1,250 EUR"; the subset has no
    // z, k, 9 and no umlauts. Line 2 (simple TrueType with WinAnsi, subset with a cmap of its own, size 14 at 20,130): "Kontoauszug März" (ä is a
    // composite glyph); the subset has no b, f, ß. Both fonts are named AAAAAA+Geist and BBBBBB+Geist.
    private static byte[] Subset(TtfInfo ttf, StringBuilder runs)
    {
        const string first = "Invoice total: 1,250 EUR";
        const string second = "Kontoauszug März";
        var doc = new PdfDoc("Pagewing Textbearbeitung: Teilmenge");
        var type0 = Type0(doc, ttf, TtfSubset.Build(ttf, first.Select(static c => (int)c), withCmap: false), "AAAAAA+Geist", first);
        var simple = SimpleTrueType(doc, ttf, TtfSubset.Build(ttf, second.Select(static c => (int)c), withCmap: true), "BBBBBB+Geist", second);
        doc.AddPage(
            "/MediaBox [0 0 300 200]",
            $"<< /Font << /F1 {type0} 0 R /F2 {simple} 0 R >> >>",
            $"""
            BT /F1 14 Tf 20 170 Td {Hex(ttf, first)} Tj ET
            BT /F2 14 Tf 20 130 Td ({second}) Tj ET
            """);
        Record(runs, "text-edit-subset.pdf", 1, first, "EmbeddedSubset", 20, 30, 14);
        Record(runs, "text-edit-subset.pdf", 1, second, "EmbeddedSubset", 20, 70, 14);
        return doc.Build();
    }

    // Page 300 x 200: Helvetica "Quarterly report 2026" (size 14 at 20,170), Times-Roman "Dear customer, thank you" (size 12 at 20,130), neither embedded.
    private static byte[] Standard(StringBuilder runs)
    {
        var doc = new PdfDoc("Pagewing Textbearbeitung: Standardschriften");
        var helvetica = doc.Add(Helvetica);
        var times = doc.Add(TimesRoman);
        doc.AddPage(
            "/MediaBox [0 0 300 200]",
            $"<< /Font << /F1 {helvetica} 0 R /F2 {times} 0 R >> >>",
            """
            BT /F1 14 Tf 20 170 Td (Quarterly report 2026) Tj ET
            BT /F2 12 Tf 20 130 Td (Dear customer, thank you) Tj ET
            """);
        Record(runs, "text-edit-standard.pdf", 1, "Quarterly report 2026", "Standard14", 20, 30, 14);
        Record(runs, "text-edit-standard.pdf", 1, "Dear customer, thank you", "Standard14", 20, 70, 12);
        return doc.Build();
    }

    // Page 300 x 200: the complete Geist, as a simple TrueType font with WinAnsi (line 1, size 14 at 20,170, "Complete font line äöü ß")
    // and as Type0 Identity-H (line 2, size 14 at 20,130, "Identity full font").
    private static byte[] Full(TtfInfo ttf, StringBuilder runs)
    {
        const string first = "Complete font line äöü ß";
        const string second = "Identity full font";
        var doc = new PdfDoc("Pagewing Textbearbeitung: vollständige Schrift");
        var simple = SimpleTrueType(doc, ttf, ttf.Data, "Geist-Regular", used: null);
        var type0 = Type0(doc, ttf, ttf.Data, "Geist-Regular", second);
        doc.AddPage(
            "/MediaBox [0 0 300 200]",
            $"<< /Font << /F1 {simple} 0 R /F2 {type0} 0 R >> >>",
            $"""
            BT /F1 14 Tf 20 170 Td ({first}) Tj ET
            BT /F2 14 Tf 20 130 Td {Hex(ttf, second)} Tj ET
            """);
        Record(runs, "text-edit-full.pdf", 1, first, "EmbeddedFull", 20, 30, 14);
        Record(runs, "text-edit-full.pdf", 1, second, "EmbeddedFull", 20, 70, 14);
        return doc.Build();
    }

    // Page 1, 300 x 200. Line 1 at 20,170: "Total: " (Helvetica 14, black), "1,250.00" (Helvetica-Bold 14, red), " EUR" (Helvetica 14, black) in one text
    // object. Line 2 at 20,130: "Pagewing" as a TJ array with kerning shifts (Helvetica 14). Line 3 at 20,90: "Mixed " in Helvetica 12 followed by "fonts here"
    // in Times-Roman 12 in one object. The origins of the later parts of a line are computed from the AFM widths of Helvetica (58.9 = 20 + width of
    // "Total: "). Page 2, 300 x 200: "Tracked one" at 20,170 and "Tracked two" at 20,130 (Helvetica 12) with character spacing 1 and word spacing 4:
    // PDFium does not write Tc and Tw, so rewriting this page would change the line that is not edited.
    private static byte[] Styles(StringBuilder runs)
    {
        var doc = new PdfDoc("Pagewing Textbearbeitung: Stile");
        var helvetica = doc.Add(Helvetica);
        var bold = doc.Add(HelveticaBold);
        var times = doc.Add(TimesRoman);
        doc.AddPage(
            "/MediaBox [0 0 300 200]",
            $"<< /Font << /F1 {helvetica} 0 R /F2 {bold} 0 R /F3 {times} 0 R >> >>",
            """
            BT 20 170 Td /F1 14 Tf (Total: ) Tj /F2 14 Tf 1 0 0 rg (1,250.00) Tj /F1 14 Tf 0 g ( EUR) Tj ET
            BT /F1 14 Tf 20 130 Td [(Pa) 20 (ge) -15 (wi) 10 (ng)] TJ ET
            BT 20 90 Td /F1 12 Tf (Mixed ) Tj /F3 12 Tf (fonts here) Tj ET
            """);
        doc.AddPage(
            "/MediaBox [0 0 300 200]",
            $"<< /Font << /F1 {helvetica} 0 R >> >>",
            """
            BT /F1 12 Tf 1 Tc 4 Tw 20 170 Td (Tracked one) Tj 0 Tc 0 Tw ET
            BT /F1 12 Tf 1 Tc 4 Tw 20 130 Td (Tracked two) Tj 0 Tc 0 Tw ET
            """);
        Record(runs, "text-edit-styles.pdf", 1, "Total: ", "Standard14", 20, 30, 14);
        Record(runs, "text-edit-styles.pdf", 1, "1,250.00", "Standard14", 58.9, 30, 14);
        Record(runs, "text-edit-styles.pdf", 1, " EUR", "Standard14", 113.4, 30, 14);
        Record(runs, "text-edit-styles.pdf", 1, "Pagewing", "Standard14", 20, 70, 14);
        Record(runs, "text-edit-styles.pdf", 1, "Mixed ", "Standard14", 20, 110, 12);
        Record(runs, "text-edit-styles.pdf", 1, "fonts here", "Standard14", 55.3, 110, 12);
        Record(runs, "text-edit-styles.pdf", 2, "Tracked one", "Standard14", 20, 30, 12);
        Record(runs, "text-edit-styles.pdf", 2, "Tracked two", "Standard14", 20, 70, 12);
        return doc.Build();
    }

    // /MediaBox [0 0 400 300], /CropBox [40 30 340 270] (300 x 240), /Rotate 90: displayed 240 x 300. A point (x, y) of the page shows at
    // left = y - 30, top = x - 40. Line 1 is written turned by 90 degrees (matrix 0 1 -1 0, so it reads upright on the displayed page):
    // "Turned page line" (Helvetica 14), origin 60,60 shows at left 30, top 20. Line 2 is written without a matrix and reads downwards on the
    // displayed page: "Upright in user space" (Helvetica 12), origin 110,100 shows at left 70, top 70. Line 3 is turned by 30 degrees on top
    // of that: "Slanted text" (Helvetica 12), origin 200,150 shows at left 120, top 160.
    private static byte[] Rotated(StringBuilder runs)
    {
        var doc = new PdfDoc("Pagewing Textbearbeitung: gedrehte Seite");
        var helvetica = doc.Add(Helvetica);
        doc.AddPage(
            "/MediaBox [0 0 400 300] /CropBox [40 30 340 270] /Rotate 90",
            $"<< /Font << /F1 {helvetica} 0 R >> >>",
            """
            BT /F1 14 Tf 0 1 -1 0 60 60 Tm (Turned page line) Tj ET
            BT /F1 12 Tf 1 0 0 1 110 100 Tm (Upright in user space) Tj ET
            BT /F1 12 Tf 0.866 0.5 -0.5 0.866 200 150 Tm (Slanted text) Tj ET
            """);
        Record(runs, "text-edit-rotated.pdf", 1, "Turned page line", "Standard14", 30, 20, 14);
        Record(runs, "text-edit-rotated.pdf", 1, "Upright in user space", "Standard14", 70, 70, 12);
        Record(runs, "text-edit-rotated.pdf", 1, "Slanted text", "Standard14", 120, 160, 12);
        return doc.Build();
    }

    // Three pages 300 x 300, lines of size 14 (baselines from the top in brackets). Page 1: "Editable control line" (Helvetica, 20), "Invisible text layer"
    // (render mode 3, 45), "Shown text" (inside /Span with /ActualText, 95), "Shared header text" (Form XObject that page 2
    // uses as well, 135), "Nested form text" (Form XObject inside a Form XObject, 195), "Stroked outline text" (render mode 1, 225), "Clipping text mode"
    // (render mode 4, inside q Q, 255), "Vertical" (Type0 Identity-V, from the top down at x = 270). Page 2: "Shared header text" (135) and
    // "Editable on page two" (20). Page 3: "ABBA" (Type 3 font, 20) and "Line beside Type 3" (45): PDFium cannot write Type 3 text, so no line of this page is editable.
    private static byte[] Locked(TtfInfo ttf, StringBuilder runs)
    {
        var doc = new PdfDoc("Pagewing Textbearbeitung: gesperrte Läufe");
        var helvetica = doc.Add(Helvetica);
        var fontResources = $"<< /Font << /F1 {helvetica} 0 R >> >>";
        var boxA = doc.AddStream(string.Empty, PdfDoc.Latin1("600 0 0 0 500 700 d1 0 0 500 700 re f"));
        var boxB = doc.AddStream(string.Empty, PdfDoc.Latin1("600 0 0 0 500 700 d1 0 0 500 350 re f"));
        var type3 = doc.Add(
            $"<< /Type /Font /Subtype /Type3 /FontBBox [0 0 600 700] /FontMatrix [0.001 0 0 0.001 0 0] /CharProcs << /A {boxA} 0 R /B {boxB} 0 R >> "
            + "/Encoding << /Type /Encoding /Differences [65 /A /B] >> /FirstChar 65 /LastChar 66 /Widths [600 600] >>");
        const string vertical = "Vertical";
        var type0 = Type0(doc, ttf, ttf.Data, "Geist-Regular", vertical, "Identity-V");

        var shared = doc.AddStream(
            $"/Type /XObject /Subtype /Form /BBox [0 0 300 40] /Resources {fontResources}",
            PdfDoc.Latin1("BT /F1 14 Tf 20 15 Td (Shared header text) Tj ET"));
        var inner = doc.AddStream(
            $"/Type /XObject /Subtype /Form /BBox [0 0 300 40] /Resources {fontResources}",
            PdfDoc.Latin1("BT /F1 14 Tf 20 15 Td (Nested form text) Tj ET"));
        var outer = doc.AddStream(
            $"/Type /XObject /Subtype /Form /BBox [0 0 300 40] /Resources << /XObject << /Fm3 {inner} 0 R >> >>",
            PdfDoc.Latin1("q /Fm3 Do Q"));

        doc.AddPage(
            "/MediaBox [0 0 300 300]",
            $"<< /Font << /F1 {helvetica} 0 R /F4 {type0} 0 R >> /XObject << /Fm1 {shared} 0 R /Fm2 {outer} 0 R >> >>",
            $"""
            BT /F1 14 Tf 20 280 Td (Editable control line) Tj ET
            BT /F1 14 Tf 3 Tr 20 255 Td (Invisible text layer) Tj 0 Tr ET
            /Span << /ActualText (Replacement) >> BDC BT /F1 14 Tf 20 205 Td (Shown text) Tj ET EMC
            q 1 0 0 1 0 150 cm /Fm1 Do Q
            q 1 0 0 1 0 90 cm /Fm2 Do Q
            BT /F1 14 Tf 1 Tr 20 75 Td (Stroked outline text) Tj 0 Tr ET
            q BT /F1 14 Tf 4 Tr 20 45 Td (Clipping text mode) Tj ET Q
            BT /F4 14 Tf 0 -1 1 0 270 280 Tm {Hex(ttf, vertical)} Tj ET
            """);
        doc.AddPage(
            "/MediaBox [0 0 300 300]",
            $"<< /Font << /F1 {helvetica} 0 R >> /XObject << /Fm1 {shared} 0 R >> >>",
            """
            BT /F1 14 Tf 20 280 Td (Editable on page two) Tj ET
            q 1 0 0 1 0 150 cm /Fm1 Do Q
            """);
        doc.AddPage(
            "/MediaBox [0 0 300 300]",
            $"<< /Font << /F1 {helvetica} 0 R /F3 {type3} 0 R >> >>",
            """
            BT /F3 14 Tf 20 280 Td (ABBA) Tj ET
            BT /F1 14 Tf 20 255 Td (Line beside Type 3) Tj ET
            """);
        const string file = "text-edit-locked.pdf";
        Record(runs, file, 1, "Editable control line", "Standard14", 20, 20, 14);
        Record(runs, file, 1, "Invisible text layer", "Standard14", 20, 45, 14);
        
        Record(runs, file, 1, "Shown text", "Standard14", 20, 95, 14);
        Record(runs, file, 1, "Shared header text", "Standard14", 20, 135, 14);
        Record(runs, file, 1, "Nested form text", "Standard14", 20, 195, 14);
        Record(runs, file, 1, "Stroked outline text", "Standard14", 20, 225, 14);
        Record(runs, file, 1, "Clipping text mode", "Standard14", 20, 255, 14);
        Record(runs, file, 1, vertical, "EmbeddedFull", 270, 20, 14);
        Record(runs, file, 2, "Editable on page two", "Standard14", 20, 20, 14);
        Record(runs, file, 2, "Shared header text", "Standard14", 20, 135, 14);
        Record(runs, file, 3, "ABBA", "Type3", 20, 20, 14);
        Record(runs, file, 3, "Line beside Type 3", "Standard14", 20, 45, 14);
        return doc.Build();
    }

    // Page 1, 400 x 400: HostileRunCount runs, a grid of 100 x 100 characters "x" (size 3, one text object each, the colour alternates so that
    // no two neighbours can be one run). Page 2, 1200 x 100: one run of HostileRunLength characters "a" (Helvetica 1 at 10,50).
    private static byte[] Hostile()
    {
        var doc = new PdfDoc("Pagewing Textbearbeitung: feindliche Seiten");
        var helvetica = doc.Add(Helvetica);
        var grid = new StringBuilder();
        for (var row = 0; row < 100; row++)
        {
            for (var column = 0; column < 100; column++)
            {
                var colour = (row + column) % 2 == 0 ? "1 0 0 rg" : "0 0 1 rg";
                grid.Append(CultureInfo.InvariantCulture, $"BT {colour} /F1 3 Tf {column * 4} {(row * 4) + 1} Td (x) Tj ET\n");
            }
        }

        doc.AddPage("/MediaBox [0 0 400 400]", $"<< /Font << /F1 {helvetica} 0 R >> >>", grid.ToString());
        doc.AddPage(
            "/MediaBox [0 0 1200 100]",
            $"<< /Font << /F1 {helvetica} 0 R >> >>",
            $"BT /F1 1 Tf 10 50 Td ({new string('a', HostileRunLength)}) Tj ET");
        return doc.Build();
    }
}

// The scan fixtures of milestone 15 (OCR): pages that are one 1-bit image of text. The text is drawn with Geist Regular by a small
// scan converter of its own (outlines of the TrueType file, exact area coverage, no platform font code), so the output is the same
// bytes on every system. Resolution 200 dpi; a page of 1000 x 1400 pixels is 360 x 504 points. ocr-words.tsv lists the ink box of
// every word in points from the top left of the displayed page (the tests look the recognized words up there).
static class OcrScans
{
    private const int Dpi = 200;
    private const int WidthPx = 1000;
    private const int HeightPx = 1400;
    private const double PointsPerPixel = 72.0 / Dpi;

    private readonly record struct Line(string Text, double X, double Baseline, double Size);

    private readonly record struct Ink(string Text, int Left, int Top, int Right, int Bottom);

    // The pages of ocr-scan.pdf: English, German with umlauts, two columns.
    private static readonly Line[][] ScanPages =
    [
        [
            new("Invoice 2026-114", 80, 180, 72),
            new("Pagewing OCR test page", 80, 300, 44),
            new("The quick brown fox jumps", 80, 400, 44),
            new("over the lazy dog 0123456789", 80, 480, 44),
            new("Total due 1,284.50 EUR", 80, 600, 44),
            new("Thank you for your business", 80, 680, 44),
        ],
        [
            new("Müller Straße Größe", 80, 200, 72),
            new("Äpfel, Öl und Übung", 80, 330, 52),
            new("Zürich Köln München", 80, 430, 52),
            new("Hauptstraße 12, 80331 München", 80, 540, 44),
        ],
        [
            new("Left column", 80, 200, 52),
            new("alpha beta gamma", 80, 300, 44),
            new("delta epsilon", 80, 380, 44),
            new("zeta eta theta", 80, 460, 44),
            new("Right column", 540, 200, 52),
            new("one two three", 540, 300, 44),
            new("four five six", 540, 380, 44),
            new("seven eight nine", 540, 460, 44),
        ],
    ];

    // The page of ocr-mixed.pdf that is a scan with a real page number on it.
    private static readonly Line[] MixedScanPage =
    [
        new("Scanned page with a number", 80, 200, 52),
        new("Only the number at the bottom is text", 80, 320, 40),
        new("Everything else is a picture", 80, 400, 40),
    ];

    public static IEnumerable<KeyValuePair<string, byte[]>> Files()
    {
        var ttf = new TtfInfo(File.ReadAllBytes(RepositoryFile("src", "Pagewing.Core", "Assets", "Fonts", "Geist-Regular.ttf")));
        var words = new StringBuilder();
        var pages = ScanPages.Select(lines => Render(ttf, lines)).ToList();
        for (var i = 0; i < pages.Count; i++)
        {
            Record(words, "ocr-scan.pdf", i + 1, pages[i].Ink);
        }

        Record(words, "ocr-scan-rotated.pdf", 1, pages[0].Ink);
        var mixed = Render(ttf, MixedScanPage);
        Record(words, "ocr-mixed.pdf", 2, mixed.Ink);

        yield return new("ocr-scan.pdf", ScanPdf(pages.Select(static p => p.Bits).ToList()));
        yield return new("ocr-scan-rotated.pdf", RotatedPdf(pages[0].Bits));
        yield return new("ocr-mixed.pdf", MixedPdf(mixed.Bits));
        yield return new("ocr-hostile.pdf", HostilePdf());
        yield return new("ocr-words.tsv", Encoding.UTF8.GetBytes(words.ToString()));
    }

    private static void Record(StringBuilder into, string file, int page, List<Ink> ink)
    {
        foreach (var word in ink)
        {
            into.Append(CultureInfo.InvariantCulture, $"{file}\t{page}\t{word.Text}\t{word.Left * PointsPerPixel:0.00}\t{word.Top * PointsPerPixel:0.00}\t{word.Right * PointsPerPixel:0.00}\t{word.Bottom * PointsPerPixel:0.00}\n");
        }
    }

    // One image per page: 1000 x 1400 pixels, 1 bit, 1 = paper.
    private static byte[] ScanPdf(List<byte[]> bits)
    {
        var doc = new PdfDoc("Pagewing Scan-Probe (OCR)");
        foreach (var page in bits)
        {
            var image = doc.AddStream(ImageEntries(WidthPx, HeightPx), page);
            doc.AddPage(string.Create(CultureInfo.InvariantCulture, $"/MediaBox [0 0 {WidthPx * PointsPerPixel:0.##} {HeightPx * PointsPerPixel:0.##}]"), $"<< /XObject << /Im0 {image} 0 R >> >>", $"q {WidthPx * PointsPerPixel:0.##} 0 0 {HeightPx * PointsPerPixel:0.##} 0 0 cm /Im0 Do Q");
        }

        return doc.Build();
    }

    // The same first page, stored turned a quarter counter-clockwise and shown with /Rotate 90 inside a crop box that does not start at the origin.
    private static byte[] RotatedPdf(byte[] upright)
    {
        var doc = new PdfDoc("Pagewing Scan-Probe gedreht (OCR)");
        var turned = TurnCounterClockwise(upright, WidthPx, HeightPx);
        var image = doc.AddStream(ImageEntries(HeightPx, WidthPx), turned);
        var wide = HeightPx * PointsPerPixel;
        var high = WidthPx * PointsPerPixel;
        doc.AddPage(
            string.Create(CultureInfo.InvariantCulture, $"/MediaBox [0 0 600 500] /CropBox [50 40 {50 + wide:0.##} {40 + high:0.##}] /Rotate 90"),
            $"<< /XObject << /Im0 {image} 0 R >> >>",
            string.Create(CultureInfo.InvariantCulture, $"q {wide:0.##} 0 0 {high:0.##} 50 40 cm /Im0 Do Q"));
        return doc.Build();
    }

    // Page 1 real text (long enough to need no recognition), page 2 a scan with a real page number (5 characters), page 3 blank.
    private static byte[] MixedPdf(byte[] scan)
    {
        var doc = new PdfDoc("Pagewing Mischprobe (OCR)");
        var font = doc.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        var size = string.Create(CultureInfo.InvariantCulture, $"/MediaBox [0 0 {WidthPx * PointsPerPixel:0.##} {HeightPx * PointsPerPixel:0.##}]");
        doc.AddPage(size, $"<< /Font << /F1 {font} 0 R >> >>", """
            BT
            /F1 14 Tf
            30 440 Td
            (This page has real text and needs no recognition.) Tj
            0 -20 Td
            (It is long enough to pass the threshold of fifty characters.) Tj
            ET
            """);
        var image = doc.AddStream(ImageEntries(WidthPx, HeightPx), scan);
        doc.AddPage(size, $"<< /Font << /F1 {font} 0 R >> /XObject << /Im0 {image} 0 R >> >>", string.Create(
            CultureInfo.InvariantCulture,
            $"q {WidthPx * PointsPerPixel:0.##} 0 0 {HeightPx * PointsPerPixel:0.##} 0 0 cm /Im0 Do Q\nBT /F1 10 Tf 170 20 Td (- 2 -) Tj ET"));
        doc.AddPage(size, "<< >>", string.Empty);
        return doc.Build();
    }

    // Page 1 is 14,400 x 14,400 points (the largest page size) with a small picture stretched over it, page 2 a letter page whose picture holds
    // over 100,000 tiny marks (specks that look like words to a page analysis).
    private static byte[] HostilePdf()
    {
        var doc = new PdfDoc("Pagewing feindliche Scan-Probe (OCR)");
        var small = doc.AddStream(ImageEntries(8, 8), new byte[8]);
        doc.AddPage("/MediaBox [0 0 14400 14400]", $"<< /XObject << /Im0 {small} 0 R >> >>", "q 14400 0 0 14400 0 0 cm /Im0 Do Q");

        const int Width = 1700;
        const int Height = 2200;
        var stride = (Width + 7) / 8;
        var bits = new byte[stride * Height];
        Array.Fill(bits, (byte)0xFF);
        for (var y = 10; y + 3 <= Height - 10; y += 6)
        {
            for (var x = 10; x + 3 <= Width - 10; x += 5)
            {
                for (var dy = 0; dy < 3; dy++)
                {
                    for (var dx = 0; dx < 3; dx++)
                    {
                        bits[((y + dy) * stride) + ((x + dx) >> 3)] &= (byte)~(0x80 >> ((x + dx) & 7));
                    }
                }
            }
        }

        var image = doc.AddStream(ImageEntries(Width, Height), bits);
        doc.AddPage("/MediaBox [0 0 612 792]", $"<< /XObject << /Im0 {image} 0 R >> >>", "q 612 0 0 792 0 0 cm /Im0 Do Q");
        return doc.Build();
    }

    private static string ImageEntries(int width, int height) =>
        $"/Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceGray /BitsPerComponent 1";

    // Rotates a 1-bit image a quarter turn counter-clockwise: the top edge becomes the left edge.
    private static byte[] TurnCounterClockwise(byte[] bits, int width, int height)
    {
        var sourceStride = (width + 7) / 8;
        var targetStride = (height + 7) / 8;
        var target = new byte[targetStride * width];
        Array.Fill(target, (byte)0xFF);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if ((bits[(y * sourceStride) + (x >> 3)] & (0x80 >> (x & 7))) == 0)
                {
                    var targetX = y;
                    var targetY = width - 1 - x;
                    target[(targetY * targetStride) + (targetX >> 3)] &= (byte)~(0x80 >> (targetX & 7));
                }
            }
        }

        return target;
    }

    // Draws the lines into a coverage buffer and returns the 1-bit page and the ink box of every word.
    private static (byte[] Bits, List<Ink> Ink) Render(TtfInfo ttf, Line[] lines)
    {
        var raster = new CoverageRaster(WidthPx, HeightPx);
        var boxes = new List<(string Text, double Left, double Right, double Top, double Bottom)>();
        foreach (var line in lines)
        {
            var scale = line.Size / ttf.UnitsPerEm;
            var x = line.X;
            var word = new StringBuilder();
            double wordLeft = 0;
            foreach (var character in line.Text + " ")
            {
                var glyph = ttf.GlyphFor(character);
                if (character == ' ')
                {
                    if (word.Length > 0)
                    {
                        boxes.Add((word.ToString(), wordLeft, x, line.Baseline - (ttf.Ascent * scale), line.Baseline - (ttf.Descent * scale)));
                        word.Clear();
                    }
                }
                else
                {
                    if (word.Length == 0)
                    {
                        wordLeft = x;
                    }

                    word.Append(character);
                    raster.Fill(ttf.GlyphContours(glyph), x, line.Baseline, scale);
                }

                x += ttf.AdvanceOf(glyph) * scale;
            }
        }

        var coverage = raster.Coverage();
        var stride = (WidthPx + 7) / 8;
        var bits = new byte[stride * HeightPx];
        Array.Fill(bits, (byte)0xFF);
        for (var y = 0; y < HeightPx; y++)
        {
            for (var x = 0; x < WidthPx; x++)
            {
                if (coverage[(y * WidthPx) + x] >= 0.5f)
                {
                    bits[(y * stride) + (x >> 3)] &= (byte)~(0x80 >> (x & 7));
                }
            }
        }

        // The ink box of a word: the dark pixels inside its advance box (a little taller than the glyphs, the box of the line).
        var ink = new List<Ink>();
        foreach (var (text, left, right, top, bottom) in boxes)
        {
            int l = int.MaxValue, t = int.MaxValue, r = -1, b = -1;
            for (var y = Math.Max(0, (int)top); y <= Math.Min(HeightPx - 1, (int)Math.Ceiling(bottom)); y++)
            {
                for (var x = Math.Max(0, (int)Math.Floor(left)); x <= Math.Min(WidthPx - 1, (int)Math.Ceiling(right)); x++)
                {
                    if ((bits[(y * stride) + (x >> 3)] & (0x80 >> (x & 7))) == 0)
                    {
                        l = Math.Min(l, x);
                        t = Math.Min(t, y);
                        r = Math.Max(r, x + 1);
                        b = Math.Max(b, y + 1);
                    }
                }
            }

            if (r < 0)
            {
                throw new InvalidOperationException($"Das Wort \"{text}\" hat keine Tinte.");
            }

            ink.Add(new Ink(text, l, t, r, b));
        }

        return (bits, ink);
    }

    private static string RepositoryFile(params string[] parts)
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Pagewing.slnx")))
            {
                return Path.Combine([directory.FullName, .. parts]);
            }
        }

        throw new InvalidOperationException("Pagewing.slnx nicht gefunden.");
    }
}

// Exact area coverage of filled outlines (the signed-area accumulation of font-rs): every edge adds the area it covers to the pixels of its
// scan line; a running sum along the rows gives the coverage. Outlines must be closed; overlaps are clamped to full coverage.
sealed class CoverageRaster(int width, int height)
{
    private readonly int stride = width + 4;
    private readonly float[] area = new float[(width + 4) * (height + 1)];

    public void Fill(List<List<(double X, double Y, bool On)>> contours, double originX, double baseline, double scale)
    {
        foreach (var contour in contours)
        {
            var path = Flatten(contour);
            for (var i = 0; i < path.Count; i++)
            {
                var from = path[i];
                var to = path[(i + 1) % path.Count];
                Line(originX + (from.X * scale), baseline - (from.Y * scale), originX + (to.X * scale), baseline - (to.Y * scale));
            }
        }
    }

    public float[] Coverage()
    {
        var result = new float[width * height];
        float sum = 0;
        for (var y = 0; y < height; y++)
        {
            sum = 0;
            for (var x = 0; x < stride; x++)
            {
                sum += area[(y * stride) + x];
                if (x < width)
                {
                    result[(y * width) + x] = Math.Min(Math.Abs(sum), 1f);
                }
            }
        }

        return result;
    }

    // The outline as a polygon: on-curve points are corners, runs of off-curve points are quadratic curves (implied on-curve points between
    // two off-curve points), each curve cut into 12 straight pieces.
    private static List<(double X, double Y)> Flatten(List<(double X, double Y, bool On)> points)
    {
        var path = new List<(double X, double Y)>();
        var count = points.Count;
        if (count < 2)
        {
            return path;
        }

        var start = 0;
        (double X, double Y) first;
        if (points[0].On)
        {
            first = (points[0].X, points[0].Y);
            start = 1;
        }
        else if (points[count - 1].On)
        {
            first = (points[count - 1].X, points[count - 1].Y);
            count--;
        }
        else
        {
            first = ((points[0].X + points[count - 1].X) / 2, (points[0].Y + points[count - 1].Y) / 2);
        }

        path.Add(first);
        var current = first;
        (double X, double Y)? control = null;
        for (var i = start; i < count; i++)
        {
            var point = points[i];
            if (point.On)
            {
                if (control is { } c)
                {
                    Curve(path, current, c, (point.X, point.Y));
                    control = null;
                }
                else
                {
                    path.Add((point.X, point.Y));
                }

                current = (point.X, point.Y);
            }
            else
            {
                if (control is { } c)
                {
                    var middle = ((c.X + point.X) / 2, (c.Y + point.Y) / 2);
                    Curve(path, current, c, middle);
                    current = middle;
                }

                control = (point.X, point.Y);
            }
        }

        if (control is { } last)
        {
            Curve(path, current, last, first);
        }

        return path;
    }

    private static void Curve(List<(double X, double Y)> path, (double X, double Y) from, (double X, double Y) control, (double X, double Y) to)
    {
        const int Pieces = 12;
        for (var i = 1; i <= Pieces; i++)
        {
            var t = i / (double)Pieces;
            var u = 1 - t;
            path.Add(((u * u * from.X) + (2 * u * t * control.X) + (t * t * to.X), (u * u * from.Y) + (2 * u * t * control.Y) + (t * t * to.Y)));
        }
    }

    private void Line(double x0, double y0, double x1, double y1)
    {
        if (y0 == y1)
        {
            return;
        }

        double direction = 1;
        if (y0 > y1)
        {
            (x0, y0, x1, y1) = (x1, y1, x0, y0);
            direction = -1;
        }

        var slope = (x1 - x0) / (y1 - y0);
        var x = x0;
        var firstRow = (int)Math.Max(0, Math.Floor(y0));
        if (y0 < 0)
        {
            x -= y0 * slope;
        }

        for (var y = firstRow; y < Math.Min(height, (int)Math.Ceiling(y1)); y++)
        {
            var rowStart = y * stride;
            var dy = Math.Min(y + 1, y1) - Math.Max(y, y0);
            var next = x + (slope * dy);
            var d = dy * direction;
            var (left, right) = x < next ? (x, next) : (next, x);
            var leftFloor = Math.Floor(left);
            var leftIndex = (int)leftFloor;
            var rightCeil = Math.Ceiling(right);
            var rightIndex = (int)rightCeil;
            if (leftIndex < 0 || rightIndex >= stride - 1)
            {
                throw new InvalidOperationException("Eine Umrisslinie liegt ausserhalb der Seite.");
            }

            if (rightIndex <= leftIndex + 1)
            {
                var middle = (0.5 * (x + next)) - leftFloor;
                area[rowStart + leftIndex] += (float)(d - (d * middle));
                area[rowStart + leftIndex + 1] += (float)(d * middle);
            }
            else
            {
                var s = 1.0 / (right - left);
                var leftFraction = left - leftFloor;
                var a0 = 0.5 * s * (1 - leftFraction) * (1 - leftFraction);
                var rightFraction = right - rightCeil + 1;
                var am = 0.5 * s * rightFraction * rightFraction;
                area[rowStart + leftIndex] += (float)(d * a0);
                if (rightIndex == leftIndex + 2)
                {
                    area[rowStart + leftIndex + 1] += (float)(d * (1 - a0 - am));
                }
                else
                {
                    var a1 = s * (1.5 - leftFraction);
                    area[rowStart + leftIndex + 1] += (float)(d * (a1 - a0));
                    for (var xi = leftIndex + 2; xi < rightIndex - 1; xi++)
                    {
                        area[rowStart + xi] += (float)(d * s);
                    }

                    var a2 = a1 + ((rightIndex - leftIndex - 3) * s);
                    area[rowStart + rightIndex - 1] += (float)(d * (1 - a2 - am));
                }

                area[rowStart + rightIndex] += (float)(d * am);
            }

            x = next;
        }
    }
}
