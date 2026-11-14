using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RhinoTrunk.Contracts;

/// <summary>
/// Canonical JSON in the spirit of RFC 8785 (JCS): object members sorted by UTF-16 code units,
/// no insignificant whitespace, minimal string escaping, numbers formatted like ECMAScript.
/// Equal content therefore always produces equal bytes and equal <see cref="ContentHash"/> values.
/// </summary>
public static class CanonicalJson
{
    public static byte[] Serialize(JsonNode? node) => Encoding.UTF8.GetBytes(SerializeToString(node));

    public static byte[] Serialize<T>(T value, JsonSerializerOptions? options = null) =>
        Serialize(JsonSerializer.SerializeToNode(value, options ?? RhinoTrunkJson.Options));

    public static string SerializeToString(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(sb, node);
        return sb.ToString();
    }

    /// <summary>Parses arbitrary UTF-8 JSON and returns its canonical bytes.</summary>
    public static byte[] Canonicalize(ReadOnlySpan<byte> utf8Json) => Serialize(JsonNode.Parse(utf8Json));

    /// <summary>True if the bytes are valid JSON and already in canonical form.</summary>
    public static bool IsCanonical(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            return Canonicalize(utf8Json).AsSpan().SequenceEqual(utf8Json);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static void Write(StringBuilder sb, JsonNode? node)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject obj:
                sb.Append('{');
                var first = true;
                foreach (var member in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, member.Key);
                    sb.Append(':');
                    Write(sb, member.Value);
                }
                sb.Append('}');
                break;
            case JsonArray array:
                sb.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Write(sb, array[i]);
                }
                sb.Append(']');
                break;
            default:
                WriteValue(sb, node.AsValue());
                break;
        }
    }

    private static void WriteValue(StringBuilder sb, JsonValue value)
    {
        // Round-trip through JsonElement so CLR-backed values (Guid, DateTimeOffset, enums...) are handled uniformly.
        using var doc = JsonDocument.Parse(value.ToJsonString());
        var element = doc.RootElement;
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                WriteString(sb, element.GetString()!);
                break;
            case JsonValueKind.Number:
                sb.Append(FormatNumber(element.GetDouble()));
                break;
            case JsonValueKind.True:
                sb.Append("true");
                break;
            case JsonValueKind.False:
                sb.Append("false");
                break;
            case JsonValueKind.Null:
                sb.Append("null");
                break;
            default:
                throw new ArgumentException($"Unsupported JSON value kind {element.ValueKind}.");
        }
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>Formats a double the way ECMAScript's Number.prototype.toString does (as required by RFC 8785).</summary>
    public static string FormatNumber(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d))
            throw new ArgumentException("NaN and Infinity are not valid JSON numbers.");
        if (d == 0) return "0"; // also covers -0

        var r = d.ToString("R", CultureInfo.InvariantCulture); // shortest round-trip digits
        var negative = r[0] == '-';
        if (negative) r = r.Substring(1);

        var exponent = 0;
        var mantissa = r;
        var e = r.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            exponent = int.Parse(r.AsSpan(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            mantissa = r.Substring(0, e);
        }

        var dot = mantissa.IndexOf('.');
        string digits;
        int pointPosition;
        if (dot >= 0)
        {
            digits = mantissa.Substring(0, dot) + mantissa.Substring(dot + 1);
            pointPosition = dot;
        }
        else
        {
            digits = mantissa;
            pointPosition = mantissa.Length;
        }

        var lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0') lead++;
        digits = digits.Substring(lead);
        pointPosition -= lead;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0) return "0";

        var k = digits.Length;
        var n = pointPosition + exponent; // value = 0.digits * 10^n
        string s;
        if (k <= n && n <= 21)
            s = digits + new string('0', n - k);
        else if (0 < n && n <= 21)
            s = digits.Substring(0, n) + "." + digits.Substring(n);
        else if (-6 < n && n <= 0)
            s = "0." + new string('0', -n) + digits;
        else
        {
            var exp = n - 1;
            var expText = exp >= 0 ? "+" + exp.ToString(CultureInfo.InvariantCulture) : exp.ToString(CultureInfo.InvariantCulture);
            s = k == 1 ? digits + "e" + expText : digits[0] + "." + digits.Substring(1) + "e" + expText;
        }
        return negative ? "-" + s : s;
    }
}
