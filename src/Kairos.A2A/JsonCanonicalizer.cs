using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Kairos.A2A;

/// <summary>
/// JSON Canonicalization Scheme (RFC 8785): the one byte sequence that every implementation produces for the same JSON value, which is what a
/// signature over JSON needs. Object members are ordered by their names compared as UTF-16 code units, strings use the minimal escaping, numbers
/// use the ECMAScript form, and there is no insignificant whitespace.
/// </summary>
public static class JsonCanonicalizer
{
    public static byte[] Canonicalize(JsonElement value)
    {
        var builder = new StringBuilder();
        Write(builder, value);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static void Write(StringBuilder b, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                b.Append("null");
                break;
            case JsonValueKind.True:
                b.Append("true");
                break;
            case JsonValueKind.False:
                b.Append("false");
                break;
            case JsonValueKind.String:
                WriteString(b, value.GetString()!);
                break;
            case JsonValueKind.Number:
                b.Append(FormatNumber(value.GetDouble()));
                break;
            case JsonValueKind.Array:
                b.Append('[');
                bool firstItem = true;
                foreach (JsonElement item in value.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        b.Append(',');
                    }

                    firstItem = false;
                    Write(b, item);
                }

                b.Append(']');
                break;
            case JsonValueKind.Object:
                b.Append('{');
                bool first = true;
                foreach (JsonProperty p in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        b.Append(',');
                    }

                    first = false;
                    WriteString(b, p.Name);
                    b.Append(':');
                    Write(b, p.Value);
                }

                b.Append('}');
                break;
            default:
                throw new ArgumentException("Not a JSON value.", nameof(value));
        }
    }

    private static void WriteString(StringBuilder b, string s)
    {
        b.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"':
                    b.Append("\\\"");
                    break;
                case '\\':
                    b.Append("\\\\");
                    break;
                case '\b':
                    b.Append("\\b");
                    break;
                case '\t':
                    b.Append("\\t");
                    break;
                case '\n':
                    b.Append("\\n");
                    break;
                case '\f':
                    b.Append("\\f");
                    break;
                case '\r':
                    b.Append("\\r");
                    break;
                default:
                    if (c < 0x20)
                    {
                        b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        b.Append(c);
                    }

                    break;
            }
        }

        b.Append('"');
    }

    /// <summary>The ECMAScript Number-to-string conversion (RFC 8785 section 3.2.2.3) for a finite double.</summary>
    internal static string FormatNumber(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d))
        {
            throw new ArgumentException("JSON numbers are finite.");
        }

        if (d == 0)
        {
            return "0"; // also -0
        }

        // The shortest representation that round-trips gives the significant digits and the exponent.
        string shortest = Math.Abs(d).ToString("R", CultureInfo.InvariantCulture);
        (string digits, int exponent) = Decompose(shortest);
        int k = digits.Length;
        int n = exponent + 1; // position of the decimal point relative to the digits
        string body;
        if (k <= n && n <= 21)
        {
            body = digits + new string('0', n - k);
        }
        else if (0 < n && n <= 21)
        {
            body = digits[..n] + "." + digits[n..];
        }
        else if (-6 < n && n <= 0)
        {
            body = "0." + new string('0', -n) + digits;
        }
        else
        {
            int e = n - 1;
            string sign = e < 0 ? "-" : "+";
            body = (k == 1 ? digits : digits[..1] + "." + digits[1..]) + "e" + sign + Math.Abs(e).ToString(CultureInfo.InvariantCulture);
        }

        return d < 0 ? "-" + body : body;
    }

    /// <summary>Splits a round-trip number string into its significant digits and the decimal exponent of the first digit.</summary>
    private static (string Digits, int Exponent) Decompose(string text)
    {
        int exp = 0;
        int e = text.IndexOfAny(['E', 'e']);
        string mantissa = text;
        if (e >= 0)
        {
            exp = int.Parse(text[(e + 1)..], CultureInfo.InvariantCulture);
            mantissa = text[..e];
        }

        int dot = mantissa.IndexOf('.', StringComparison.Ordinal);
        string intPart = dot < 0 ? mantissa : mantissa[..dot];
        string frac = dot < 0 ? "" : mantissa[(dot + 1)..];
        string all = intPart + frac;
        int leadingZeros = all.Length - all.TrimStart('0').Length;
        string digits = all.TrimStart('0').TrimEnd('0');
        if (digits.Length == 0)
        {
            return ("0", 0);
        }

        int exponent = intPart.Length - 1 - leadingZeros + exp;
        return (digits, exponent);
    }
}
