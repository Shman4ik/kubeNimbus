using System.Text.Json;

namespace KubeNimbus.Core;

/// <summary>
/// The slice of JSONPath a CRD's <c>additionalPrinterColumns[].jsonPath</c> actually
/// uses, evaluated against a <see cref="JsonElement"/>.
/// </summary>
/// <remarks>
/// <para>
/// Kubernetes validates a printer column's path with
/// <c>validateSimpleJSONPath</c>, which only insists it starts with <c>.</c> and
/// parses as k8s JSONPath — so the grammar is nominally the whole of
/// <c>k8s.io/client-go/util/jsonpath</c>. In practice CRD authors write four shapes
/// and this supports exactly those:
/// </para>
/// <list type="bullet">
/// <item><c>.spec.replicas</c> — dotted field access.</item>
/// <item><c>.metadata.labels['app.kubernetes.io/name']</c> — bracketed field access,
/// which is the only way to reach a key containing a dot.</item>
/// <item><c>.spec.ports[0].port</c> / <c>.spec.rules[*].host</c> — index and wildcard.</item>
/// <item><c>.status.conditions[?(@.type=="Ready")].status</c> — the condition filter.
/// This one is not exotic: it is how cert-manager, Flux, KEDA and Argo all spell
/// their Ready column, so a subset without it would miss the columns people most
/// want from the CRDs they most have.</item>
/// </list>
/// <para>
/// Anything outside that (recursive descent, unions, script expressions) resolves to
/// <em>no match</em> rather than throwing. That is the same outcome as a path that
/// points at an absent field, and it is the right one: an unparseable column must
/// cost an empty cell, never an exception on a watch tick.
/// </para>
/// <para>
/// Only the <em>first</em> match is ever used, which is what the API server's own
/// <c>tableconvertor</c> does — its comment reads "as we only support simple JSON
/// path, we can assume to have only one result". <see cref="TryEvaluate"/> is
/// therefore the whole public surface.
/// </para>
/// </remarks>
public static class SimpleJsonPath
{
    /// <summary>
    /// Resolves <paramref name="path"/> against <paramref name="root"/>, returning the
    /// first match. False for an unresolvable path, an absent field, or a syntax this
    /// subset does not cover — the caller renders all three as an empty cell.
    /// </summary>
    public static bool TryEvaluate(JsonElement root, string path, out JsonElement value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var current = root;
        var i = 0;
        var span = path.AsSpan();

        // A leading "." is required by the CRD validator but a few charts ship without
        // it; treating it as optional costs nothing and rescues those columns.
        while (i < span.Length)
        {
            if (span[i] == '.')
            {
                i++;
                if (i < span.Length && span[i] == '.')
                {
                    return false; // recursive descent — outside this subset
                }

                var start = i;
                while (i < span.Length && span[i] != '.' && span[i] != '[')
                {
                    i++;
                }

                if (i == start)
                {
                    continue; // a trailing or doubled separator: nothing to select
                }

                if (!TryField(current, span[start..i].ToString(), out current))
                {
                    return false;
                }
            }
            else if (span[i] == '[')
            {
                var close = FindClosingBracket(span, i);
                if (close < 0)
                {
                    return false;
                }

                if (!TryBracket(current, span[(i + 1)..close], out current))
                {
                    return false;
                }

                i = close + 1;
            }
            else
            {
                // A bare leading segment ("spec.replicas" with no dot).
                var start = i;
                while (i < span.Length && span[i] != '.' && span[i] != '[')
                {
                    i++;
                }

                if (!TryField(current, span[start..i].ToString(), out current))
                {
                    return false;
                }
            }
        }

        value = current;
        return true;
    }

    private static bool TryField(JsonElement parent, string name, out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static int FindClosingBracket(ReadOnlySpan<char> span, int open)
    {
        // Quotes are tracked so a "]" inside a filter's string literal doesn't end the
        // segment early — .status.conditions[?(@.reason=="a]b")] is legal.
        var quote = '\0';
        for (var i = open + 1; i < span.Length; i++)
        {
            var c = span[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '\'' or '"')
            {
                quote = c;
            }
            else if (c == ']')
            {
                return i;
            }
        }

        return -1;
    }

    private static bool TryBracket(JsonElement parent, ReadOnlySpan<char> inner, out JsonElement value)
    {
        value = default;
        inner = inner.Trim();
        if (inner.Length == 0)
        {
            return false;
        }

        if (inner[0] is '\'' or '"')
        {
            // ['key'] — a field name that could not be spelled with a dot.
            var quote = inner[0];
            var end = inner.LastIndexOf(quote);
            return end > 0 && TryField(parent, inner[1..end].ToString(), out value);
        }

        if (inner[0] == '*')
        {
            return TryFirstItem(parent, out value);
        }

        if (inner[0] == '?')
        {
            return TryFilter(parent, inner, out value);
        }

        if (int.TryParse(inner, out var index))
        {
            if (parent.ValueKind == JsonValueKind.Array && index >= 0 && index < parent.GetArrayLength())
            {
                value = parent[index];
                return true;
            }

            return false;
        }

        // An unquoted bracketed name is not legal JSONPath but is a common typo in
        // published CRDs; accepting it costs nothing and rescues the column.
        return TryField(parent, inner.ToString(), out value);
    }

    private static bool TryFirstItem(JsonElement parent, out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Array && parent.GetArrayLength() > 0)
        {
            value = parent[0];
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// <c>[?(@.type=="Ready")]</c> — equality (and inequality) against one field of each
    /// array element. Anything more elaborate returns no match rather than guessing.
    /// </summary>
    private static bool TryFilter(JsonElement parent, ReadOnlySpan<char> inner, out JsonElement value)
    {
        value = default;
        if (parent.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        // ?( ... ) — strip the marker and the parentheses.
        var expr = inner[1..].Trim();
        if (expr.Length < 2 || expr[0] != '(' || expr[^1] != ')')
        {
            return false;
        }

        expr = expr[1..^1].Trim();

        var negate = false;
        var op = expr.IndexOf("==", StringComparison.Ordinal);
        if (op < 0)
        {
            op = expr.IndexOf("!=", StringComparison.Ordinal);
            negate = op >= 0;
        }

        if (op < 0)
        {
            return false;
        }

        var left = expr[..op].Trim();
        var right = Unquote(expr[(op + 2)..].Trim());

        if (left.Length < 2 || left[0] != '@')
        {
            return false;
        }

        // "@.type" -> "type"; "@['type']" is accepted through the same bracket reader.
        var fieldPath = left[1..].ToString();

        foreach (var item in parent.EnumerateArray())
        {
            if (!TryEvaluate(item, fieldPath, out var candidate))
            {
                continue;
            }

            var text = ScalarText(candidate);
            if (text is null)
            {
                continue;
            }

            if (string.Equals(text, right, StringComparison.Ordinal) != negate)
            {
                value = item;
                return true;
            }
        }

        return false;
    }

    private static string Unquote(ReadOnlySpan<char> literal) =>
        literal.Length >= 2 && (literal[0] is '\'' or '"') && literal[^1] == literal[0]
            ? literal[1..^1].ToString()
            : literal.ToString();

    /// <summary>The element's text if it is a scalar, null for objects and arrays.</summary>
    internal static string? ScalarText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "",
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };
}
