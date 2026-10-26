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
        new("signed-docmdp-none.pdf", SignedDocument(null)),
        new("signed-docmdp-1.pdf", SignedDocument(1)),
        new("signed-docmdp-2.pdf", SignedDocument(2)),
        new("signed-docmdp-3.pdf", SignedDocument(3)),
        new("linearized.pdf", Linearized()),
        new("annotations-foreign.pdf", AnnotationsForeign()),
        new("drawings-foreign.pdf", DrawingsForeign()),
        new("forms-basic.pdf", FormsBasic()),
        new("forms-calc.pdf", FormsCalc()),
        new("forms-xfa.pdf", FormsXfa()),
        new("forms-signed.pdf", FormsSigned()),
        new("forms-rotated.pdf", FormsRotated()),
        new("forms-hostile.pdf", FormsHostile()),
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
