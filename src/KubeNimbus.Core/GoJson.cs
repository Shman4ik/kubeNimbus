using System.Globalization;
using System.Text;
using System.Text.Json;

namespace KubeNimbus.Core;

/// <summary>
/// A JSON value written the way Go's <c>encoding/json.Marshal</c> writes it, for the
/// one place this app has to print exactly what the API server printed: a
/// <c>type: string</c> printer column over an array or an object (see
/// <see cref="PrinterColumns.Evaluate"/>).
/// </summary>
/// <remarks>
/// <para>
/// Three things differ from <see cref="JsonElement.GetRawText"/> or a default
/// <see cref="Utf8JsonWriter"/>, and each is Go's behaviour rather than a preference:
/// </para>
/// <list type="bullet">
/// <item>Compact — no whitespace at all.</item>
/// <item>Object keys sorted. The API server holds a custom resource as
/// <c>map[string]interface{}</c>, and Go marshals a map with its keys in sorted
/// order whatever order the manifest used.</item>
/// <item>HTML-safe escaping: <c>&lt;</c>, <c>&gt;</c> and <c>&amp;</c> are written as
/// six-character unicode escapes (backslash, u, four hex digits), as are U+2028 and U+2029,
/// while every other non-ASCII character passes through as itself —
/// <see cref="System.Text.Encodings.Web.JavaScriptEncoder"/>'s default escapes all
/// non-ASCII, which Go does not.</item>
/// </list>
/// <para>
/// Numbers are written as the JSON already spells them. Kubernetes decodes a whole
/// number in an unstructured object to <c>int64</c>, which Go prints the same way, and
/// a printer column over a fractional number inside an array is not a case worth a
/// float formatter that could disagree with the object.
/// </para>
/// </remarks>
public static class GoJson
{
    public static string Marshal(JsonElement value)
    {
        var builder = new StringBuilder();
        Write(builder, value);
        return builder.ToString();
    }

    private static void Write(StringBuilder builder, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var first = true;
                foreach (var property in value.EnumerateObject()
                             .OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    WriteString(builder, property.Name);
                    builder.Append(':');
                    Write(builder, property.Value);
                }

                builder.Append('}');
                break;

            case JsonValueKind.Array:
                builder.Append('[');
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    if (index++ > 0)
                    {
                        builder.Append(',');
                    }

                    Write(builder, item);
                }

                builder.Append(']');
                break;

            case JsonValueKind.String:
                WriteString(builder, value.GetString() ?? "");
                break;

            case JsonValueKind.Number:
                builder.Append(value.GetRawText());
                break;

            case JsonValueKind.True:
                builder.Append("true");
                break;

            case JsonValueKind.False:
                builder.Append("false");
                break;

            default:
                builder.Append("null");
                break;
        }
    }

    private static void WriteString(StringBuilder builder, string text)
    {
        builder.Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '<' or '>' or '&' or (char)0x2028 or (char)0x2029:
                    AppendUnicodeEscape(builder, c);
                    break;
                default:
                    if (c < 0x20)
                    {
                        AppendUnicodeEscape(builder, c);
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
    }

    private static void AppendUnicodeEscape(StringBuilder builder, char c) =>
        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
}
