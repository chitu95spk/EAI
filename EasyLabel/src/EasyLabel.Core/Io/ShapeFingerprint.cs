using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EasyLabel.Core.Io;

/// <summary>
/// The shape hash ("fp") the agent review protocol uses to tell whether an existing shape is still the one the agent looked at:
/// list_shapes reports it and the canvas rechecks it before it applies a replace or remove.
/// </summary>
public static class ShapeFingerprint
{
    /// <summary>Hex characters of a fingerprint (the first 8 bytes of a SHA-256).</summary>
    public const int Length = 16;

    /// <summary>
    /// SHA-256 over the UTF-8 text <c>label(trimmed) LF shapeType(trimmed, lower-case) LF x,y;x,y;...</c>, the coordinates rounded to
    /// 2 decimals (away from zero) and written as <c>0.00</c> with the invariant culture, in point order; the first 8 bytes as 16
    /// lower-case hex characters. Null label / type / points count as empty.
    /// </summary>
    public static string Compute(string? label, string? shapeType, IEnumerable<double[]>? points)
    {
        var text = new StringBuilder();
        text.Append((label ?? string.Empty).Trim()).Append('\n');
        text.Append((shapeType ?? string.Empty).Trim().ToLowerInvariant()).Append('\n');
        if (points != null)
        {
            foreach (double[] p in points)
            {
                if (p == null || p.Length < 2) continue;
                text.Append(Format(p[0])).Append(',').Append(Format(p[1])).Append(';');
            }
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        return System.Convert.ToHexString(hash, 0, Length / 2).ToLowerInvariant();
    }

    private static string Format(double value)
    {
        // "+ 0.0" turns a negative zero into 0 so that -0.001 and 0 give the same text.
        double rounded = Math.Round(value, 2, MidpointRounding.AwayFromZero) + 0.0;
        return rounded.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
