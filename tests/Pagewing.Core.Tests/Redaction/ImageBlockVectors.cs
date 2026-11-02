namespace Pagewing.Core.Tests.Redaction;

/// <summary>
/// The test vectors of the block calculation of pixelating and blurring (milestone 14.1, task 12). Two implementations are held against them:
/// <c>Pagewing.Core.Redaction.ImageBlocks</c> (what the check of the result expects) and <c>Pagewing.Pdfium.PdfiumImageBlocks</c> (what the
/// redaction writes); they share no code, so a mistake in one of them fails here. The literal numbers were worked out by hand from the
/// formula in the remarks of <c>ImageBlocks</c>, not copied from either implementation.
/// </summary>
internal static class ImageBlockVectors
{
    /// <summary>The side of the image and the box of the blur vectors.</summary>
    public const int BlurSide = 16;

    /// <summary>The block means (blue = green = red) of the 2 x 2 blocks of the blur vectors, row by row: the corners of the interpolation.</summary>
    public static readonly int[][] BlurMeans = [[40, 120], [200, 240]];

    /// <summary>A grid the planner must give for a box, or <see langword="null"/> columns for "no grid, solid fill".</summary>
    internal sealed record GridCase(int Width, int Height, bool VeryCoarse, int Columns, int Rows, int[] ColumnEdges, int[] RowEdges);

    /// <summary>A pixel of the blur vector with the value it must get (blue = green = red).</summary>
    internal sealed record BlurSample(int X, int Y, byte Value);

    /// <summary>Boxes of sizes for which the planner must say "no grid" (smaller than 2 x 2 blocks of 8 pixels).</summary>
    public static readonly (int Width, int Height)[] TooSmall = [(7, 7), (15, 16), (16, 15), (8, 100), (100, 8), (15, 100), (100, 15), (0, 20), (20, 0), (1, 1)];

    public static readonly GridCase[] Grids =
    [
        new(100, 50, false, 6, 6, [0, 16, 33, 50, 66, 83, 100], [0, 8, 16, 25, 33, 41, 50]),
        new(100, 50, true, 3, 3, [0, 33, 66, 100], [0, 16, 33, 50]),
        new(23, 40, false, 2, 5, [0, 11, 23], [0, 8, 16, 24, 32, 40]),
        new(16, 16, false, 2, 2, [0, 8, 16], [0, 8, 16]),
        new(16, 16, true, 2, 2, [0, 8, 16], [0, 8, 16]),
        new(24, 24, true, 3, 3, [0, 8, 16, 24], [0, 8, 16, 24]),
        new(200, 200, true, 3, 3, [0, 66, 133, 200], [0, 66, 133, 200]),
        new(47, 47, false, 5, 5, [0, 9, 18, 28, 37, 47], [0, 9, 18, 28, 37, 47]),
        new(26, 17, false, 3, 2, [0, 8, 17, 26], [0, 8, 17]),
    ];

    /// <summary>The size of the image and the box of the unequal-grid vectors: 3 columns of 8, 9 and 9 pixels and 2 rows of 8 and 9 pixels.</summary>
    public const int UnequalWidth = 26;

    public const int UnequalHeight = 17;

    /// <summary>The block means (blue = green = red) of the unequal grid, row by row.</summary>
    public static readonly int[][] UnequalMeans = [[40, 120, 200], [200, 240, 80]];

    private static readonly int[] UnequalColumnEdges = [0, 8, 17, 26];

    private static readonly int[] UnequalRowEdges = [0, 8, 17];

    /// <summary>
    /// Pixels of the blur of the unequal grid, worked out by hand with the formula of <c>ImageBlocks</c> (doubled coordinates, centres at
    /// 8, 25 and 43 across and 8 and 25 down). For example (11, 2): across it lies between the centres 8 and 25 with tx = 15 and dx = 17,
    /// down it is before the first centre, so the value is (40 * 2 + 120 * 15 + 8) / 17 = 111.
    /// </summary>
    public static readonly BlurSample[] UnequalSamples =
    [
        new(0, 0, 40), new(3, 3, 40), new(8, 4, 91), new(11, 2, 111), new(12, 8, 184), new(16, 8, 163), new(17, 12, 151), new(20, 3, 191),
        new(25, 16, 80), new(25, 0, 200), new(10, 10, 200), new(14, 12, 204), new(21, 6, 165), new(8, 8, 156), new(4, 12, 202), new(0, 16, 200),
    ];

    /// <summary>
    /// A 26 x 17 image whose blocks have the means <see cref="UnequalMeans"/> in blue, green and red, but where two pixels of the first row of
    /// every block differ from the mean by 10 (one up, one down, so the mean stays) and the alpha is a noise.
    /// </summary>
    public static byte[] UnequalImage()
    {
        var pixels = new byte[UnequalWidth * UnequalHeight * 4];
        for (var row = 0; row < UnequalMeans.Length; row++)
        {
            for (var column = 0; column < UnequalMeans[row].Length; column++)
            {
                for (var y = UnequalRowEdges[row]; y < UnequalRowEdges[row + 1]; y++)
                {
                    for (var x = UnequalColumnEdges[column]; x < UnequalColumnEdges[column + 1]; x++)
                    {
                        var value = UnequalMeans[row][column];
                        if (y == UnequalRowEdges[row] && x == UnequalColumnEdges[column])
                        {
                            value += 10;
                        }
                        else if (y == UnequalRowEdges[row] && x == UnequalColumnEdges[column] + 1)
                        {
                            value -= 10;
                        }

                        var at = ((y * UnequalWidth) + x) * 4;
                        pixels[at] = (byte)value;
                        pixels[at + 1] = (byte)value;
                        pixels[at + 2] = (byte)value;
                        pixels[at + 3] = (byte)(((x * 53) + (y * 29) + 5) % 256);
                    }
                }
            }
        }

        return pixels;
    }

    /// <summary>
    /// Pixels of the blur vectors that were worked out by hand: the bilinear value between the block centres (the formula of
    /// <c>ImageBlocks</c>), clamped beyond the first and the last centre. Every one is the value of blue, green and red.
    /// </summary>
    public static readonly BlurSample[] BlurSamples =
    [
        new(0, 0, 40), new(3, 3, 40), new(15, 0, 120), new(12, 0, 120), new(0, 15, 200), new(0, 12, 200), new(15, 15, 240), new(12, 12, 240),
        new(4, 4, 55), new(7, 7, 137), new(8, 8, 162), new(11, 11, 230), new(4, 0, 45), new(4, 7, 114),
    ];

    /// <summary>
    /// A 16 x 16 image whose blocks of 8 x 8 pixels have the means <see cref="BlurMeans"/> in blue, green and red, but whose pixels differ
    /// from them by 10 (alternating by row), and whose alpha is a noise: nothing in it but the means may reach the result.
    /// </summary>
    public static byte[] BlurImage() => MeanImage(rowWise: true, deviation: 10, alphaSeed: 37);

    /// <summary>The same block means with other pixels: the deviation of 15 alternates by column, the alpha is another noise.</summary>
    public static byte[] BlurImageOtherPixels() => MeanImage(rowWise: false, deviation: 15, alphaSeed: 101);

    private static byte[] MeanImage(bool rowWise, int deviation, int alphaSeed)
    {
        var pixels = new byte[BlurSide * BlurSide * 4];
        for (var y = 0; y < BlurSide; y++)
        {
            for (var x = 0; x < BlurSide; x++)
            {
                var mean = BlurMeans[y / 8][x / 8];
                var odd = (rowWise ? y : x) % 2 == 1;
                var value = (byte)(mean + (odd ? deviation : -deviation));
                var at = ((y * BlurSide) + x) * 4;
                pixels[at] = value;
                pixels[at + 1] = value;
                pixels[at + 2] = value;
                pixels[at + 3] = (byte)(((x * alphaSeed) + (y * 91) + 3) % 256);
            }
        }

        return pixels;
    }

    /// <summary>A reproducible pseudo random image of the given size (blue, green, red, alpha).</summary>
    public static byte[] RandomImage(int width, int height, int seed)
    {
        var random = new Random(seed);
        var pixels = new byte[width * height * 4];
        random.NextBytes(pixels);
        return pixels;
    }

    /// <summary>The boxes (x, y, width, height) inside an image of 130 x 90 pixels that the cross checks use, each with a different grid.</summary>
    public static readonly (int X, int Y, int Width, int Height)[] CrossBoxes =
    [
        (0, 0, 130, 90), (3, 5, 48, 48), (10, 10, 16, 16), (7, 2, 100, 40), (60, 30, 69, 59), (1, 1, 23, 40), (40, 0, 47, 90), (0, 40, 130, 50),
    ];
}
