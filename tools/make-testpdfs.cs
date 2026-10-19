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

// Minimal writer: numbered objects, classic xref table, trailer with Info. Streams are never compressed.
sealed class PdfDoc
{
    private readonly List<byte[]?> objects = [];
    private readonly List<int> pages = [];
    private readonly int catalog;
    private readonly int pagesRoot;
    private readonly int info;

    public PdfDoc(string title)
    {
        catalog = Reserve();
        pagesRoot = Reserve();
        info = Add($"<< /Title {TextString(title)} /Producer (Pagewing test generator) >>");
    }

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
        var head = Latin1($"<< {entries} /Length {data.Length} >>\nstream\n");
        var tail = Latin1("\nendstream");
        objects[id - 1] = [.. head, .. data, .. tail];
        return id;
    }

    // pageEntries: MediaBox, Rotate, CropBox, Annots ...; resources: a resource dictionary; content: the page content stream.
    // reservedId: an id from Reserve, for pages that other objects (links) point to before the page is added.
    public int AddPage(string pageEntries, string resources, string content, int? reservedId = null)
    {
        var contentId = AddStream(string.Empty, Latin1(content));
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

        var output = new MemoryStream();
        output.Write(Latin1("%PDF-1.7\n"));
        output.Write([0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A]);

        var offsets = new long[objects.Count];
        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i] = output.Position;
            output.Write(Latin1($"{i + 1} 0 obj\n"));
            output.Write(objects[i] ?? throw new InvalidOperationException($"Objekt {i + 1} wurde nie gesetzt."));
            output.Write(Latin1("\nendobj\n"));
        }

        var xref = output.Position;
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
