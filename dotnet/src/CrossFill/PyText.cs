using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CrossFill;

/// <summary>
/// Formats values the way the Python implementation prints them, so error
/// messages and data read from files are identical in both implementations.
/// </summary>
internal static class PyText
{
    /// <summary>Python <c>repr()</c> of a string: <c>'abc'</c>.</summary>
    public static string Repr(string s)
    {
        char quote = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        var sb = new StringBuilder().Append(quote);
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\\"); break;
                case '\n': sb.Append(@"\n"); break;
                case '\r': sb.Append(@"\r"); break;
                case '\t': sb.Append(@"\t"); break;
                default:
                    if (c == quote) sb.Append('\\');
                    sb.Append(c);
                    break;
            }
        }
        return sb.Append(quote).ToString();
    }

    /// <summary>Python <c>repr()</c> of a parsed JSON value.</summary>
    public static string Repr(JsonNode? node) => node switch
    {
        null => "None",
        JsonValue v when v.GetValueKind() == JsonValueKind.String => Repr(v.GetValue<string>()),
        JsonValue v when v.GetValueKind() == JsonValueKind.True => "True",
        JsonValue v when v.GetValueKind() == JsonValueKind.False => "False",
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => Number(v.ToJsonString()),
        _ => node.ToJsonString(),
    };

    /// <summary>Python <c>type(value).__name__</c> for a parsed JSON value.</summary>
    public static string TypeName(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "dict",
        JsonValueKind.Array => "list",
        JsonValueKind.String => "str",
        JsonValueKind.Number => "float",
        JsonValueKind.True or JsonValueKind.False => "bool",
        _ => "NoneType",
    };

    /// <summary>Python <c>str()</c> of a JSON number: integers as written, floats in Python's repr style.</summary>
    public static string Number(string raw)
    {
        if (raw.IndexOfAny(['.', 'e', 'E']) < 0)
            return System.Numerics.BigInteger.Parse(raw, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        return Float(double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture));
    }

    /// <summary>Python's <c>repr(float)</c>: shortest round-trip digits, scientific below 1e-4 and from 1e16.</summary>
    public static string Float(double d)
    {
        if (double.IsPositiveInfinity(d)) return "inf";
        if (double.IsNegativeInfinity(d)) return "-inf";
        if (double.IsNaN(d)) return "nan";
        if (d == 0) return double.IsNegative(d) ? "-0.0" : "0.0";

        // "R" gives the shortest round-trip digits; split them into sign, digits and exponent.
        string r = Math.Abs(d).ToString("E16", CultureInfo.InvariantCulture);
        string shortest = Math.Abs(d).ToString("R", CultureInfo.InvariantCulture);
        int exp = int.Parse(r[(r.IndexOf('E') + 1)..], CultureInfo.InvariantCulture);
        string mantissa = shortest.Contains('E') ? shortest[..shortest.IndexOf('E')] : shortest;
        string digits = mantissa.Replace(".", "").TrimStart('0').TrimEnd('0');
        if (digits.Length == 0) digits = "0";
        string sign = d < 0 ? "-" : "";

        if (exp is >= -4 and < 16)
        {
            string text;
            if (exp >= 0)
            {
                string intPart = digits.Length > exp + 1 ? digits[..(exp + 1)] : digits.PadRight(exp + 1, '0');
                string frac = digits.Length > exp + 1 ? digits[(exp + 1)..] : "0";
                text = intPart + "." + frac;
            }
            else
            {
                text = "0." + new string('0', -exp - 1) + digits;
            }
            return sign + text;
        }
        string m = digits.Length > 1 ? digits[0] + "." + digits[1..] : digits;
        return $"{sign}{m}e{(exp < 0 ? "-" : "+")}{Math.Abs(exp):00}";
    }

    /// <summary>Python <c>str.capitalize()</c>: first character upper case, the rest lower case.</summary>
    public static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();

    /// <summary>A Python-style list literal of strings: <c>['a', 'b']</c>.</summary>
    public static string List(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(Repr)) + "]";
}
