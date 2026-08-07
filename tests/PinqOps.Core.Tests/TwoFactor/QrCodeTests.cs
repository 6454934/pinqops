using System.Text;
using PinqOps.TwoFactor;
using Xunit;

namespace PinqOps.Tests.TwoFactor;

/// <summary>
/// Reed-Solomon, checked by the property that defines it rather than against a
/// table copied from somewhere.
///
/// <para>The error-correction codewords are the remainder of the data divided by a
/// generator polynomial whose roots are the first powers of the field's primitive
/// element. So the whole codeword — data followed by its remainder — evaluates to
/// zero at every one of those roots. That is what a decoder computes to decide
/// whether a symbol is intact, and an encoder that got any step wrong fails it.
/// The arithmetic below is written out from the definition, independently of the
/// encoder it is checking.</para>
/// </summary>
public class ReedSolomonTests
{
    /// <summary>a·b in GF(256), by the definition rather than by the implementation under test.</summary>
    private static byte Multiply(byte a, byte b)
    {
        var product = 0;
        for (var bit = 0; bit < 8; bit++)
        {
            product <<= 1;
            if ((product & 0x100) != 0)
            {
                product ^= 0x11D;
            }

            if ((b & (0x80 >> bit)) != 0)
            {
                product ^= a;
            }
        }

        return (byte)product;
    }

    /// <summary>α^exponent, by repeated multiplication.</summary>
    private static byte Power(int exponent)
    {
        byte value = 1;
        for (var index = 0; index < exponent; index++)
        {
            value = Multiply(value, 2);
        }

        return value;
    }

    /// <summary>The polynomial at x, coefficients highest-order first.</summary>
    private static byte Evaluate(IReadOnlyList<byte> coefficients, byte x)
    {
        byte result = 0;
        foreach (var coefficient in coefficients)
        {
            result = (byte)(Multiply(result, x) ^ coefficient);
        }

        return result;
    }

    [Fact]
    public void MultiplicationAgreesWithTheDefinition()
    {
        for (var left = 0; left < 256; left += 7)
        {
            for (var right = 0; right < 256; right += 5)
            {
                Assert.Equal(Multiply((byte)left, (byte)right), ReedSolomon.Multiply((byte)left, (byte)right));
            }
        }
    }

    /// <summary>
    /// The generator of degree d is the product of (x − α^k) for k below d, so it
    /// is zero at each of those and nowhere else nearby.
    /// </summary>
    [Theory]
    [InlineData(10)]
    [InlineData(16)]
    [InlineData(18)]
    [InlineData(22)]
    [InlineData(24)]
    [InlineData(26)]
    public void TheGeneratorHasExactlyTheRootsItShould(int degree)
    {
        byte[] polynomial = [1, .. ReedSolomon.Generator(degree)];

        for (var root = 0; root < degree; root++)
        {
            Assert.Equal(0, Evaluate(polynomial, Power(root)));
        }

        Assert.NotEqual(0, Evaluate(polynomial, Power(degree)));
    }

    /// <summary>
    /// The check a decoder performs. If every syndrome is zero the codeword is one
    /// the standard's own arithmetic accepts — which is the whole claim being made
    /// about this encoder.
    /// </summary>
    [Theory]
    [InlineData(10)]
    [InlineData(18)]
    [InlineData(26)]
    public void EverySyndromeOfACompleteCodewordIsZero(int degree)
    {
        var data = Enumerable.Range(0, 40).Select(index => (byte)(index * 7)).ToArray();
        byte[] codeword = [.. data, .. ReedSolomon.Remainder(data, degree)];

        for (var root = 0; root < degree; root++)
        {
            Assert.Equal(0, Evaluate(codeword, Power(root)));
        }
    }

    /// <summary>And a corrupted one does not, so the check above is not vacuous.</summary>
    [Fact]
    public void ACorruptedCodewordFailsTheSameCheck()
    {
        var data = Enumerable.Range(0, 40).Select(index => (byte)(index * 7)).ToArray();
        byte[] codeword = [.. data, .. ReedSolomon.Remainder(data, 18)];
        codeword[3] ^= 0xFF;

        Assert.Contains(
            Enumerable.Range(0, 18),
            root => Evaluate(codeword, Power(root)) != 0);
    }
}

public class QrCodeTests
{
    /// <summary>
    /// The codeword table is not taken on trust: a version's free modules are
    /// exactly what the data and error-correction codewords have to fit into, so
    /// dividing them by eight has to give the number the encoder was told.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void TheCodewordCountMatchesTheModulesThereActuallyAre(int version) =>
        Assert.Equal(QrCode.TotalCodewordsAt(version), QrCode.DataModuleCount(version) / 8);

    /// <summary>Data plus error correction is the whole of it, with nothing left over.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(10)]
    public void TheBlocksAccountForEveryCodeword(int version)
    {
        var (perBlock, blocks) = QrCode.ErrorCorrectionAt(version);
        var data = QrCode.TotalCodewordsAt(version) - (perBlock * blocks);

        Assert.True(data > 0);
        Assert.Equal(QrCode.TotalCodewordsAt(version), data + (perBlock * blocks));
        // Every block is one of two lengths, differing by one.
        Assert.InRange(data % blocks, 0, blocks - 1);
    }

    /// <summary>
    /// The eight masks, written out from the standard rather than read off the
    /// implementation. A scanner unmasks with these, so one wrong here is a symbol
    /// nothing can read.
    /// </summary>
    [Fact]
    public void TheMasksAreTheStandardsOwn()
    {
        Func<int, int, bool>[] expected =
        [
            (x, y) => (x + y) % 2 == 0,
            (x, y) => y % 2 == 0,
            (x, y) => x % 3 == 0,
            (x, y) => (x + y) % 3 == 0,
            (x, y) => ((y / 2) + (x / 3)) % 2 == 0,
            (x, y) => ((x * y) % 2) + ((x * y) % 3) == 0,
            (x, y) => (((x * y) % 2) + ((x * y) % 3)) % 2 == 0,
            (x, y) => (((x + y) % 2) + ((x * y) % 3)) % 2 == 0,
        ];

        for (var mask = 0; mask < 8; mask++)
        {
            for (var y = 0; y < 21; y++)
            {
                for (var x = 0; x < 21; x++)
                {
                    Assert.Equal(expected[mask](x, y), QrCode.Masked(mask, x, y));
                }
            }
        }
    }

    // ---- what the symbol looks like -------------------------------------------

    [Fact]
    public void TheSymbolGrowsWithTheContent()
    {
        Assert.Equal(21, QrCode.Encode("hello").Size);
        Assert.True(QrCode.Encode(new string('x', 100)).Size > 21);
    }

    /// <summary>
    /// The three squares a scanner finds first. Without them in all three corners
    /// it never gets as far as the data.
    /// </summary>
    [Fact]
    public void ThereAreFinderPatternsInThreeCorners()
    {
        var matrix = QrCode.Encode("https://example.com");
        var size = matrix.Size;

        foreach (var (originX, originY) in new[] { (0, 0), (size - 7, 0), (0, size - 7) })
        {
            for (var dy = 0; dy < 7; dy++)
            {
                for (var dx = 0; dx < 7; dx++)
                {
                    var distance = Math.Max(Math.Abs(dx - 3), Math.Abs(dy - 3));
                    Assert.Equal(distance != 2, matrix[originX + dx, originY + dy]);
                }
            }
        }
    }

    [Fact]
    public void TheTimingPatternsAlternate()
    {
        var matrix = QrCode.Encode("https://example.com");

        for (var index = 8; index < matrix.Size - 8; index++)
        {
            Assert.Equal(index % 2 == 0, matrix[index, 6]);
            Assert.Equal(index % 2 == 0, matrix[6, index]);
        }
    }

    [Fact]
    public void TheAlwaysDarkModuleIsDark()
    {
        var matrix = QrCode.Encode("https://example.com");

        Assert.True(matrix[8, matrix.Size - 8]);
    }

    [Fact]
    public void SomethingTooLongIsRefusedRatherThanTruncated() =>
        Assert.Throws<ArgumentException>(() => QrCode.Encode(new string('x', 400)));

    // ---- reading it back ------------------------------------------------------

    /// <summary>
    /// Everything between the text and the modules, checked the way a scanner does
    /// it: read the format information, undo the mask, walk the placement, take the
    /// codewords apart and see the original bytes come out.
    /// </summary>
    [Theory]
    [InlineData("hello")]
    [InlineData("otpauth://totp/pinqops:ada?secret=JBSWY3DPEHPK3PXP&issuer=pinqops&algorithm=SHA1&digits=6&period=30")]
    [InlineData("otpauth://totp/a-rather-long-issuer-name:someone@example.com?secret=JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP&issuer=a-rather-long-issuer-name&algorithm=SHA1&digits=6&period=30")]
    [InlineData("çöğüşi — non-ascii travels as UTF-8")]
    public void WhatWentInComesBackOut(string text) => Assert.Equal(text, Decode(QrCode.Encode(text)));

    [Fact]
    public void EveryVersionThisEncoderHandlesRoundTrips()
    {
        // One string per version, sized so the encoder has to step up each time.
        for (var version = 1; version <= QrCode.MaxVersion; version++)
        {
            var text = new string('x', QrCode.Capacity(version));
            var matrix = QrCode.Encode(text);

            Assert.Equal(QrCode.SizeOf(version), matrix.Size);
            Assert.Equal(text, Decode(matrix));
        }
    }

    /// <summary>
    /// A decoder, written to the standard rather than to the encoder: read the
    /// format information out of the symbol, unmask, walk the same zigzag, undo the
    /// interleaving and read the byte-mode header.
    /// </summary>
    private static string Decode(QrMatrix matrix)
    {
        var version = (matrix.Size - 17) / 4;
        var reserved = QrCode.FunctionModules(version).Reserved;
        var size = matrix.Size;

        // Format information, top-left copy, then the mask out of it.
        var format = 0;
        for (var index = 0; index <= 5; index++)
        {
            format |= (matrix[8, index] ? 1 : 0) << index;
        }

        format |= (matrix[8, 7] ? 1 : 0) << 6;
        format |= (matrix[8, 8] ? 1 : 0) << 7;
        format |= (matrix[7, 8] ? 1 : 0) << 8;
        for (var index = 9; index < 15; index++)
        {
            format |= (matrix[14 - index, 8] ? 1 : 0) << index;
        }

        var mask = ((format ^ 0x5412) >> 10) & 0b111;
        Assert.Equal(0, ((format ^ 0x5412) >> 13) & 0b11); // level M

        // Unmask, then read the data modules in placement order.
        var bits = new List<bool>();
        var upward = true;
        for (var right = size - 1; right >= 1; right -= 2)
        {
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

                    bits.Add(matrix[x, y] ^ QrCode.Masked(mask, x, y));
                }
            }

            upward = !upward;
        }

        var interleaved = new byte[bits.Count / 8];
        for (var index = 0; index < interleaved.Length * 8; index++)
        {
            if (bits[index])
            {
                interleaved[index / 8] |= (byte)(1 << (7 - (index % 8)));
            }
        }

        // De-interleave the data half back into blocks, then concatenate them.
        var (ecPerBlock, blocks) = QrCode.ErrorCorrectionAt(version);
        var dataCodewords = QrCode.TotalCodewordsAt(version) - (ecPerBlock * blocks);
        var shortLength = dataCodewords / blocks;
        var longBlocks = dataCodewords % blocks;

        var lengths = Enumerable.Range(0, blocks)
            .Select(index => shortLength + (index >= blocks - longBlocks ? 1 : 0))
            .ToArray();
        var recovered = lengths.Select(length => new byte[length]).ToArray();

        var source = 0;
        for (var position = 0; position < shortLength + 1; position++)
        {
            for (var block = 0; block < blocks; block++)
            {
                if (position < lengths[block])
                {
                    recovered[block][position] = interleaved[source++];
                }
            }
        }

        var payload = recovered.SelectMany(block => block).ToArray();

        // Byte mode: four bits of indicator, then the length, then the bytes.
        Assert.Equal(0b0100, payload[0] >> 4);
        var countBits = version < 10 ? 8 : 16;
        var length = countBits == 8
            ? ((payload[0] & 0x0F) << 4) | (payload[1] >> 4)
            : ((payload[0] & 0x0F) << 12) | (payload[1] << 4) | (payload[2] >> 4);

        var start = countBits == 8 ? 1 : 2;
        var bytes = new byte[length];
        for (var index = 0; index < length; index++)
        {
            bytes[index] = (byte)(((payload[start + index] & 0x0F) << 4) | (payload[start + index + 1] >> 4));
        }

        return Encoding.UTF8.GetString(bytes);
    }
}

public class QrSvgTests
{
    /// <summary>
    /// Without the border a scanner fails on the symbol's outer edge, which looks
    /// like a broken camera rather than a missing margin.
    /// </summary>
    [Fact]
    public void ThereIsAQuietZoneAroundIt()
    {
        var matrix = QrCode.Encode("hello");
        var span = matrix.Size + (QrSvg.QuietZone * 2);

        Assert.Contains($"viewBox=\"0 0 {span} {span}\"", QrSvg.Render(matrix), StringComparison.Ordinal);
    }

    /// <summary>
    /// Explicit colours rather than currentColor: on a dark background an inherited
    /// foreground draws the symbol inverted, and an inverted symbol scans as
    /// nothing.
    /// </summary>
    [Fact]
    public void TheColoursAreFixedRatherThanInherited()
    {
        var svg = QrSvg.Render(QrCode.Encode("hello"));

        Assert.Contains("fill=\"#ffffff\"", svg, StringComparison.Ordinal);
        Assert.Contains("fill=\"#000000\"", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("currentColor", svg, StringComparison.Ordinal);
    }

    /// <summary>It goes straight into the page, so it must not be able to carry a script.</summary>
    [Fact]
    public void ItIsShapesAndNothingElse()
    {
        var svg = QrSvg.Render(QrCode.Encode("otpauth://totp/pinqops:ada?secret=JBSWY3DPEHPK3PXP"));

        Assert.DoesNotContain("<script", svg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href", svg, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("<svg xmlns=\"http://www.w3.org/2000/svg\"", svg, StringComparison.Ordinal);
        Assert.EndsWith("</svg>", svg, StringComparison.Ordinal);
    }
}
