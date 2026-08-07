using System.Text;

namespace PinqOps.TwoFactor;

/// <summary>A square of light and dark modules. <c>this[x, y]</c> is true where it is dark.</summary>
public sealed class QrMatrix
{
    private readonly bool[] _modules;

    internal QrMatrix(int size)
    {
        Size = size;
        _modules = new bool[size * size];
    }

    public int Size { get; }

    public bool this[int x, int y]
    {
        get => _modules[(y * Size) + x];
        internal set => _modules[(y * Size) + x] = value;
    }
}

/// <summary>
/// A QR encoder, just big enough for an <c>otpauth://</c> URI.
///
/// <para><b>Why not a library.</b> The dashboard is one HTML file with one inline
/// script and a content-security policy that hashes it; a script tag pointing at a
/// CDN is the one thing that policy exists to refuse, and bundling a JavaScript
/// encoder would add a few hundred kilobytes to a file that is already large. This
/// produces the matrix on the server and the page draws it as an inline SVG, which
/// needs no script at all.</para>
///
/// <para><b>Deliberately narrow.</b> Byte mode, error-correction level M, versions
/// 1 to 10. An otpauth URI is a hundred-odd bytes of ASCII, so the modes this does
/// not implement would never be chosen and the versions above 10 would never be
/// reached — and every one of them is more table to get subtly wrong.</para>
///
/// <para><b>Getting it wrong cannot lock anyone out.</b> Two-factor is switched on
/// only after the operator has typed a code their app produced, so a QR that
/// encoded the wrong thing fails at setup, in front of the person setting it
/// up.</para>
/// </summary>
public static class QrCode
{
    public const int MinVersion = 1;

    public const int MaxVersion = 10;

    /// <summary>
    /// Total codewords — data plus error correction — for versions 1 to 10. Not a
    /// number to take on trust: it equals the free modules divided by eight, and
    /// the tests check exactly that against the placement code.
    /// </summary>
    private static readonly int[] TotalCodewords =
        [0, 26, 44, 70, 100, 134, 172, 196, 242, 292, 346];

    /// <summary>Error-correction codewords per block at level M.</summary>
    private static readonly int[] EcCodewordsPerBlock =
        [0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26];

    /// <summary>Blocks the data is split into at level M.</summary>
    private static readonly int[] BlockCount =
        [0, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5];

    /// <summary>Alignment-pattern centre coordinates per version.</summary>
    private static readonly int[][] AlignmentCentres =
    [
        [], [], [6, 18], [6, 22], [6, 26], [6, 30], [6, 34],
        [6, 22, 38], [6, 24, 42], [6, 26, 46], [6, 28, 50],
    ];

    /// <summary>Level M's two-bit code, as it appears in the format information.</summary>
    private const int ErrorCorrectionLevelM = 0b00;

    /// <summary>The matrix for <paramref name="text"/>, at the smallest version that holds it.</summary>
    public static QrMatrix Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var data = Encoding.UTF8.GetBytes(text);
        var version = SmallestVersion(data.Length);
        var codewords = BuildCodewords(data, version);
        return Draw(codewords, version);
    }

    /// <summary>Total codewords at a version — what <see cref="DataModuleCount"/> has to agree with.</summary>
    public static int TotalCodewordsAt(int version) => TotalCodewords[version];

    /// <summary>Error-correction codewords per block, and how many blocks there are, at level M.</summary>
    public static (int PerBlock, int Blocks) ErrorCorrectionAt(int version) =>
        (EcCodewordsPerBlock[version], BlockCount[version]);

    /// <summary>How many bytes fit at a version, in byte mode at level M.</summary>
    public static int Capacity(int version)
    {
        var dataCodewords = TotalCodewords[version] - (EcCodewordsPerBlock[version] * BlockCount[version]);

        // Four bits of mode indicator plus the character count, which widens at
        // version 10 — so the header costs one byte more from there on.
        var headerBits = 4 + (version < 10 ? 8 : 16);
        return dataCodewords - ((headerBits + 7) / 8);
    }

    private static int SmallestVersion(int byteCount)
    {
        for (var version = MinVersion; version <= MaxVersion; version++)
        {
            if (byteCount <= Capacity(version))
            {
                return version;
            }
        }

        throw new ArgumentException(
            $"That is {byteCount} bytes, and this encoder stops at {Capacity(MaxVersion)}.");
    }

    // ---- bits, blocks and error correction ------------------------------------

    private static byte[] BuildCodewords(byte[] data, int version)
    {
        var bits = new BitBuffer();
        bits.Append(0b0100, 4); // byte mode
        bits.Append(data.Length, version < 10 ? 8 : 16);
        foreach (var value in data)
        {
            bits.Append(value, 8);
        }

        var totalCodewords = TotalCodewords[version];
        var ecPerBlock = EcCodewordsPerBlock[version];
        var blocks = BlockCount[version];
        var dataCodewords = totalCodewords - (ecPerBlock * blocks);
        var capacityBits = dataCodewords * 8;

        // Terminator, then to a byte boundary, then the two pad bytes the standard
        // names — alternating, which is what a decoder expects to find and skip.
        bits.Append(0, Math.Min(4, capacityBits - bits.Length));
        bits.Append(0, (8 - (bits.Length % 8)) % 8);
        for (var pad = 0xEC; bits.Length < capacityBits; pad ^= 0xEC ^ 0x11)
        {
            bits.Append(pad, 8);
        }

        var payload = bits.ToBytes();

        // Short blocks first, then the long ones: the standard splits the data so
        // that the remainder goes into the last blocks, one extra codeword each.
        var shortLength = dataCodewords / blocks;
        var longBlocks = dataCodewords % blocks;

        var dataBlocks = new List<byte[]>(blocks);
        var ecBlocks = new List<byte[]>(blocks);
        var offset = 0;
        for (var index = 0; index < blocks; index++)
        {
            var length = shortLength + (index >= blocks - longBlocks ? 1 : 0);
            var block = payload[offset..(offset + length)];
            offset += length;
            dataBlocks.Add(block);
            ecBlocks.Add(ReedSolomon.Remainder(block, ecPerBlock));
        }

        // Interleaved, because that is what makes a scratch across the symbol
        // damage a few codewords of every block rather than destroying one block
        // entirely — which is the difference between recoverable and not.
        var result = new List<byte>(totalCodewords);
        for (var position = 0; position < shortLength + 1; position++)
        {
            foreach (var block in dataBlocks.Where(block => position < block.Length))
            {
                result.Add(block[position]);
            }
        }

        for (var position = 0; position < ecPerBlock; position++)
        {
            foreach (var block in ecBlocks)
            {
                result.Add(block[position]);
            }
        }

        return [.. result];
    }

    // ---- drawing ---------------------------------------------------------------

    /// <summary>The width of a version's symbol, in modules.</summary>
    public static int SizeOf(int version) => (version * 4) + 17;

    /// <summary>
    /// The function patterns for a version: the matrix with them drawn, and which
    /// modules they occupy.
    ///
    /// <para>Public because the tests decode what this encoder produced, and doing
    /// that needs to know which modules are not data. It is also what makes the
    /// codeword table checkable rather than trusted — the free modules divided by
    /// eight have to equal it.</para>
    /// </summary>
    public static (QrMatrix Matrix, bool[] Reserved) FunctionModules(int version)
    {
        if (version is < MinVersion or > MaxVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        var size = SizeOf(version);
        var matrix = new QrMatrix(size);
        var reserved = new bool[size * size];

        void Reserve(int x, int y, bool dark)
        {
            matrix[x, y] = dark;
            reserved[(y * size) + x] = true;
        }

        DrawFinders(size, Reserve);
        DrawTiming(size, Reserve);
        DrawAlignment(version, size, Reserve);

        // The one module that is always dark, and the format areas around the
        // finders, which are filled in once the mask is chosen.
        Reserve(8, size - 8, true);
        ReserveFormatAreas(size, Reserve);
        if (version >= 7)
        {
            DrawVersionInformation(version, size, Reserve);
        }

        return (matrix, reserved);
    }

    /// <summary>How many modules a version leaves for data and error correction.</summary>
    public static int DataModuleCount(int version) =>
        FunctionModules(version).Reserved.Count(taken => !taken);

    private static QrMatrix Draw(byte[] codewords, int version)
    {
        var size = SizeOf(version);
        var (matrix, reserved) = FunctionModules(version);

        PlaceData(matrix, reserved, codewords, size);

        var mask = BestMask(matrix, reserved, size);
        ApplyMask(matrix, reserved, size, mask);
        DrawFormatInformation(matrix, size, mask);
        return matrix;
    }

    private static void DrawFinders(int size, Action<int, int, bool> reserve)
    {
        foreach (var (centreX, centreY) in new[] { (3, 3), (size - 4, 3), (3, size - 4) })
        {
            for (var dy = -4; dy <= 4; dy++)
            {
                for (var dx = -4; dx <= 4; dx++)
                {
                    var x = centreX + dx;
                    var y = centreY + dy;
                    if (x < 0 || y < 0 || x >= size || y >= size)
                    {
                        continue;
                    }

                    // Rings at Chebyshev distance 0-1 and 3 are dark; 2 is the light
                    // ring inside and 4 is the separator outside.
                    var distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    reserve(x, y, distance is not (2 or 4));
                }
            }
        }
    }

    private static void DrawTiming(int size, Action<int, int, bool> reserve)
    {
        for (var index = 8; index < size - 8; index++)
        {
            reserve(index, 6, index % 2 == 0);
            reserve(6, index, index % 2 == 0);
        }
    }

    private static void DrawAlignment(int version, int size, Action<int, int, bool> reserve)
    {
        var centres = AlignmentCentres[version];
        if (centres.Length == 0)
        {
            return;
        }

        var first = centres[0];
        var last = centres[^1];

        foreach (var centreY in centres)
        {
            foreach (var centreX in centres)
            {
                // Exactly three are left out — the ones the finder patterns already
                // occupy. Not "anything already reserved": from version 7 there are
                // alignment patterns centred on the timing row and column, and those
                // are meant to be drawn straight over it. Skipping them costs five
                // codewords a version and produces a symbol nothing can read.
                var isFinderCorner =
                    (centreX == first && centreY == first)
                    || (centreX == last && centreY == first)
                    || (centreX == first && centreY == last);

                if (isFinderCorner)
                {
                    continue;
                }

                for (var dy = -2; dy <= 2; dy++)
                {
                    for (var dx = -2; dx <= 2; dx++)
                    {
                        reserve(centreX + dx, centreY + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
                    }
                }
            }
        }
    }

    private static void ReserveFormatAreas(int size, Action<int, int, bool> reserve)
    {
        for (var index = 0; index <= 8; index++)
        {
            if (index != 6)
            {
                reserve(index, 8, false);
                reserve(8, index, false);
            }
        }

        for (var index = 0; index < 8; index++)
        {
            reserve(size - 1 - index, 8, false);
            if (size - 1 - index != size - 8)
            {
                reserve(8, size - 1 - index, false);
            }
        }
    }

    private static void DrawVersionInformation(int version, int size, Action<int, int, bool> reserve)
    {
        var bits = (version << 12) | Bch.Remainder(version, degree: 12, generator: 0x1F25);
        for (var index = 0; index < 18; index++)
        {
            var dark = ((bits >> index) & 1) != 0;
            reserve(index / 3, size - 11 + (index % 3), dark);
            reserve(size - 11 + (index % 3), index / 3, dark);
        }
    }

    /// <summary>
    /// Walks the symbol in two-module-wide columns, right to left, alternating
    /// upward and downward, skipping everything already reserved.
    /// </summary>
    private static void PlaceData(QrMatrix matrix, bool[] reserved, byte[] codewords, int size)
    {
        var bit = 0;
        var upward = true;

        for (var right = size - 1; right >= 1; right -= 2)
        {
            // Column 6 is the vertical timing pattern, and the pairs step over it
            // rather than through it.
            if (right == 6)
            {
                right = 5;
            }

            for (var step = 0; step < size; step++)
            {
                var y = upward ? size - 1 - step : step;
                for (var column = 0; column < 2; column++)
                {
                    var x = right - column;
                    if (reserved[(y * size) + x])
                    {
                        continue;
                    }

                    // Past the end of the data are the remainder bits, which stay
                    // light.
                    if (bit < codewords.Length * 8)
                    {
                        matrix[x, y] = ((codewords[bit / 8] >> (7 - (bit % 8))) & 1) != 0;
                    }

                    bit++;
                }
            }

            upward = !upward;
        }
    }

    /// <summary>
    /// Whether a mask flips the module at a position. Public so the tests can check
    /// the eight against the standard's own definitions — a scanner unmasks with
    /// those, so one wrong here is a symbol nothing can read.
    /// </summary>
    public static bool Masked(int mask, int x, int y) => mask switch
    {
        0 => (x + y) % 2 == 0,
        1 => y % 2 == 0,
        2 => x % 3 == 0,
        3 => (x + y) % 3 == 0,
        4 => ((y / 2) + (x / 3)) % 2 == 0,
        5 => (x * y % 2) + (x * y % 3) == 0,
        6 => (((x * y) % 2) + ((x * y) % 3)) % 2 == 0,
        _ => (((x + y) % 2) + ((x * y) % 3)) % 2 == 0,
    };

    private static void ApplyMask(QrMatrix matrix, bool[] reserved, int size, int mask)
    {
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (!reserved[(y * size) + x] && Masked(mask, x, y))
                {
                    matrix[x, y] = !matrix[x, y];
                }
            }
        }
    }

    /// <summary>
    /// The mask with the lowest penalty. Any of the eight is readable in principle
    /// — the format information says which was used — but the penalty rules are
    /// what keep a symbol from growing large blank areas or shapes that look like a
    /// finder pattern, and a scanner in poor light notices the difference.
    /// </summary>
    private static int BestMask(QrMatrix matrix, bool[] reserved, int size)
    {
        var best = 0;
        var lowest = int.MaxValue;

        for (var mask = 0; mask < 8; mask++)
        {
            ApplyMask(matrix, reserved, size, mask);
            DrawFormatInformation(matrix, size, mask);
            var penalty = QrPenalty.Score(matrix);
            ApplyMask(matrix, reserved, size, mask);

            if (penalty < lowest)
            {
                lowest = penalty;
                best = mask;
            }
        }

        return best;
    }

    private static void DrawFormatInformation(QrMatrix matrix, int size, int mask)
    {
        var data = (ErrorCorrectionLevelM << 3) | mask;
        var bits = ((data << 10) | Bch.Remainder(data, degree: 10, generator: 0x537)) ^ 0x5412;

        // Two copies, so losing one corner does not lose the mask.
        for (var index = 0; index <= 5; index++)
        {
            matrix[8, index] = Bit(bits, index);
        }

        matrix[8, 7] = Bit(bits, 6);
        matrix[8, 8] = Bit(bits, 7);
        matrix[7, 8] = Bit(bits, 8);
        for (var index = 9; index < 15; index++)
        {
            matrix[14 - index, 8] = Bit(bits, index);
        }

        for (var index = 0; index < 8; index++)
        {
            matrix[size - 1 - index, 8] = Bit(bits, index);
        }

        for (var index = 8; index < 15; index++)
        {
            matrix[8, size - 15 + index] = Bit(bits, index);
        }

        matrix[8, size - 8] = true;
    }

    private static bool Bit(int value, int index) => ((value >> index) & 1) != 0;

    private sealed class BitBuffer
    {
        private readonly List<bool> _bits = [];

        internal int Length => _bits.Count;

        internal void Append(int value, int count)
        {
            for (var index = count - 1; index >= 0; index--)
            {
                _bits.Add(((value >> index) & 1) != 0);
            }
        }

        internal byte[] ToBytes()
        {
            var bytes = new byte[_bits.Count / 8];
            for (var index = 0; index < _bits.Count; index++)
            {
                if (_bits[index])
                {
                    bytes[index / 8] |= (byte)(1 << (7 - (index % 8)));
                }
            }

            return bytes;
        }
    }
}

/// <summary>
/// Reed-Solomon over GF(256) with the QR standard's primitive polynomial. The
/// error-correction codewords are the remainder of the data divided by the
/// generator polynomial — which is also how they can be checked: a correct
/// codeword evaluates to zero at every root of that generator.
/// </summary>
public static class ReedSolomon
{
    /// <summary>x^8 + x^4 + x^3 + x^2 + 1, which is the field QR uses.</summary>
    public const int PrimitivePolynomial = 0x11D;

    /// <summary>Multiplication in GF(256): shift and reduce, one bit at a time.</summary>
    public static byte Multiply(byte left, byte right)
    {
        var result = 0;
        var a = left;
        var b = right;

        while (b != 0)
        {
            if ((b & 1) != 0)
            {
                result ^= a;
            }

            var high = (a & 0x80) != 0;
            a = (byte)(a << 1);
            if (high)
            {
                a ^= PrimitivePolynomial & 0xFF;
            }

            b >>= 1;
        }

        return (byte)result;
    }

    /// <summary>The generator polynomial of the given degree, lowest term last.</summary>
    public static byte[] Generator(int degree)
    {
        var polynomial = new byte[degree];
        polynomial[^1] = 1;

        // (x - α^0)(x - α^1)…; root is the running power of the primitive element.
        byte root = 1;
        for (var index = 0; index < degree; index++)
        {
            for (var position = 0; position < degree; position++)
            {
                polynomial[position] = Multiply(polynomial[position], root);
                if (position + 1 < degree)
                {
                    polynomial[position] ^= polynomial[position + 1];
                }
            }

            root = Multiply(root, 2);
        }

        return polynomial;
    }

    /// <summary>The error-correction codewords for one block.</summary>
    public static byte[] Remainder(ReadOnlySpan<byte> data, int degree)
    {
        var generator = Generator(degree);
        var result = new byte[degree];

        foreach (var value in data)
        {
            var factor = (byte)(value ^ result[0]);
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (var index = 0; index < result.Length; index++)
            {
                result[index] ^= Multiply(generator[index], factor);
            }
        }

        return result;
    }
}

/// <summary>
/// The BCH remainder the format and version information carry. Both are tiny
/// codes over GF(2) — long division by a fixed generator, nothing more.
/// </summary>
public static class Bch
{
    public static int Remainder(int data, int degree, int generator)
    {
        var remainder = data << degree;
        var generatorBits = BitWidth(generator);

        while (BitWidth(remainder) >= generatorBits)
        {
            remainder ^= generator << (BitWidth(remainder) - generatorBits);
        }

        return remainder;
    }

    private static int BitWidth(int value)
    {
        var width = 0;
        while (value != 0)
        {
            width++;
            value >>= 1;
        }

        return width;
    }
}

/// <summary>
/// The standard's four penalty rules, which is how one of the eight masks is
/// chosen. They exist so a symbol does not end up with long same-coloured runs,
/// large blocks, shapes a scanner mistakes for a finder pattern, or a wildly
/// uneven balance of dark to light.
/// </summary>
public static class QrPenalty
{
    public static int Score(QrMatrix matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        return Runs(matrix) + Blocks(matrix) + FinderLookalikes(matrix) + Balance(matrix);
    }

    /// <summary>Rule 1: five or more of the same colour in a row or column.</summary>
    private static int Runs(QrMatrix matrix)
    {
        var penalty = 0;
        for (var line = 0; line < matrix.Size; line++)
        {
            penalty += RunPenalty(matrix, line, horizontal: true) + RunPenalty(matrix, line, horizontal: false);
        }

        return penalty;
    }

    private static int RunPenalty(QrMatrix matrix, int line, bool horizontal)
    {
        var penalty = 0;
        var run = 1;

        for (var index = 1; index < matrix.Size; index++)
        {
            var current = horizontal ? matrix[index, line] : matrix[line, index];
            var previous = horizontal ? matrix[index - 1, line] : matrix[line, index - 1];

            if (current == previous)
            {
                run++;
                if (run == 5)
                {
                    penalty += 3;
                }
                else if (run > 5)
                {
                    penalty++;
                }
            }
            else
            {
                run = 1;
            }
        }

        return penalty;
    }

    /// <summary>Rule 2: every 2×2 block of one colour.</summary>
    private static int Blocks(QrMatrix matrix)
    {
        var penalty = 0;
        for (var y = 0; y < matrix.Size - 1; y++)
        {
            for (var x = 0; x < matrix.Size - 1; x++)
            {
                var corner = matrix[x, y];
                if (corner == matrix[x + 1, y] && corner == matrix[x, y + 1] && corner == matrix[x + 1, y + 1])
                {
                    penalty += 3;
                }
            }
        }

        return penalty;
    }

    /// <summary>Rule 3: the 1:1:3:1:1 pattern with four light modules beside it.</summary>
    private static int FinderLookalikes(QrMatrix matrix)
    {
        bool[] pattern = [true, false, true, true, true, false, true];
        bool[] light = [false, false, false, false];
        var penalty = 0;

        for (var line = 0; line < matrix.Size; line++)
        {
            for (var start = 0; start + 11 <= matrix.Size; start++)
            {
                penalty += 40 * Matches(matrix, line, start, [.. pattern, .. light]);
                penalty += 40 * Matches(matrix, line, start, [.. light, .. pattern]);
            }
        }

        return penalty;
    }

    private static int Matches(QrMatrix matrix, int line, int start, bool[] wanted)
    {
        var found = 0;

        var horizontal = true;
        for (var pass = 0; pass < 2; pass++, horizontal = false)
        {
            var hit = true;
            for (var index = 0; index < wanted.Length && hit; index++)
            {
                var module = horizontal ? matrix[start + index, line] : matrix[line, start + index];
                hit = module == wanted[index];
            }

            if (hit)
            {
                found++;
            }
        }

        return found;
    }

    /// <summary>Rule 4: how far the proportion of dark modules is from half.</summary>
    private static int Balance(QrMatrix matrix)
    {
        var dark = 0;
        for (var y = 0; y < matrix.Size; y++)
        {
            for (var x = 0; x < matrix.Size; x++)
            {
                if (matrix[x, y])
                {
                    dark++;
                }
            }
        }

        var total = matrix.Size * matrix.Size;
        var percent = dark * 100 / total;
        return Math.Abs(percent - 50) / 5 * 10;
    }
}
