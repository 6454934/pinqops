using System.Globalization;
using System.Text;

namespace PinqOps.TwoFactor;

/// <summary>
/// Draws a QR matrix as an SVG the dashboard can put straight into the page.
///
/// <para><b>Markup, not an image.</b> A PNG would mean a data URI, which the
/// content-security policy would have to allow; this is elements, it scales to
/// whatever the phone is held at, and it needs no script.</para>
/// </summary>
public static class QrSvg
{
    /// <summary>
    /// The light border. Four modules is what the standard asks for, and a scanner
    /// without it fails on the symbol's outer edge — which looks like a broken
    /// camera rather than a missing margin.
    /// </summary>
    public const int QuietZone = 4;

    /// <summary>
    /// The SVG for one matrix. Colours are named rather than inherited so it stays
    /// readable on a dark background, where a symbol drawn in <c>currentColor</c>
    /// would come out inverted and scan as nothing.
    /// </summary>
    public static string Render(QrMatrix matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);

        var span = matrix.Size + (QuietZone * 2);
        var builder = new StringBuilder(matrix.Size * matrix.Size * 8);
        builder.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {span} {span}\" shape-rendering=\"crispEdges\" role=\"img\">");
        builder.Append(CultureInfo.InvariantCulture, $"<rect width=\"{span}\" height=\"{span}\" fill=\"#ffffff\"/>");
        builder.Append("<path fill=\"#000000\" d=\"");

        // One path of many small rectangles rather than one element each: the
        // markup is a third of the size and the browser draws it in one pass.
        for (var y = 0; y < matrix.Size; y++)
        {
            for (var x = 0; x < matrix.Size; x++)
            {
                if (matrix[x, y])
                {
                    builder.Append(CultureInfo.InvariantCulture, $"M{x + QuietZone} {y + QuietZone}h1v1h-1z");
                }
            }
        }

        builder.Append("\"/></svg>");
        return builder.ToString();
    }
}
