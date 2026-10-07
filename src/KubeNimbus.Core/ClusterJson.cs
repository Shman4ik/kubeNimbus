using System.Text.Json;

namespace KubeNimbus.Core;

/// <summary>
/// How JSON that came from a cluster is parsed: one depth limit for every parse, and the
/// fallback that lets one unreadable object in a list cost that object rather than the list.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="JsonDocument"/>'s default <c>MaxDepth</c> is 64, and the API server accepts
/// objects nested far deeper than that (its own limit is about 10,000). A
/// <c>x-kubernetes-preserve-unknown-fields</c> field — an Argo CD Application's
/// <c>spec.source.helm.valuesObject</c> is one — can hold whatever its author writes, so a
/// single Application nested 65 levels deep made every list and watch of the kind throw,
/// and the watch reported it as a lost connection and retried for ever, with every Argo
/// application gone from the Applications mode. <see cref="MaxDepth"/> is 256: deep enough
/// for anything a person writes by hand or a chart renders, and shallow enough that the
/// recursive readers downstream (<see cref="YamlJson"/>, <see cref="ResourceDiff"/>) stay a
/// few hundred frames deep on any thread.
/// </para>
/// <para>
/// An object deeper than that, or one that is not JSON at all, is skipped and named rather
/// than allowed to end the stream: <see cref="ReadListItems"/> for a list page, and the watch
/// loop for a watch frame, which report it through <see cref="UnreadableObjectException"/>.
/// </para>
/// </remarks>
public static class ClusterJson
{
    /// <summary>The nesting depth every cluster-JSON parse accepts.</summary>
    public const int MaxDepth = 256;

    /// <summary>The one options instance every parse of cluster data uses.</summary>
    public static readonly JsonDocumentOptions Options = new() { MaxDepth = MaxDepth };

    /// <summary>
    /// The depth the item-by-item fallback reads a list body with. The reader is iterative, so
    /// this costs a bit per level and nothing on the stack; it only has to be deep enough to
    /// walk past an over-deep item to the next one. Past it the page is unreadable as a whole,
    /// and that is reported like any other failed list.
    /// </summary>
    private const int ScanDepth = 16_384;

    public static JsonDocument Parse(string json) => JsonDocument.Parse(json, Options);

    public static JsonDocument Parse(ReadOnlyMemory<byte> utf8Json) => JsonDocument.Parse(utf8Json, Options);

    public static Task<JsonDocument> ParseAsync(Stream utf8Json, CancellationToken cancellationToken = default) =>
        JsonDocument.ParseAsync(utf8Json, Options, cancellationToken);

    /// <summary>
    /// A <c>*List</c> body's items, one parse each, when the body as a whole would not parse:
    /// the items that do parse are returned, and each that does not is named in
    /// <paramref name="unreadable"/>. Null when the body is not readable even item by item
    /// (it is not JSON, or it is nested past <see cref="ScanDepth"/>), which the caller treats
    /// as the failed request it is.
    /// </summary>
    /// <param name="continueToken">The page's <c>metadata.continue</c>.</param>
    /// <param name="resourceVersion">The page's <c>metadata.resourceVersion</c>.</param>
    internal static List<JsonDocument>? ReadListItems(
        ReadOnlyMemory<byte> body,
        List<string> unreadable,
        out string? continueToken,
        out string? resourceVersion)
    {
        continueToken = null;
        resourceVersion = null;
        var items = new List<JsonDocument>();
        try
        {
            var reader = new Utf8JsonReader(body.Span, new JsonReaderOptions { MaxDepth = ScanDepth });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("items"u8))
                {
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.StartArray)
                    {
                        reader.Skip();
                        continue;
                    }

                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        var start = (int)reader.TokenStartIndex;
                        reader.Skip();
                        var end = (int)reader.BytesConsumed;
                        var item = body[start..end];
                        try
                        {
                            items.Add(Parse(item));
                        }
                        catch (JsonException ex)
                        {
                            unreadable.Add(Describe(item.Span, ex));
                        }
                    }
                }
                else if (reader.ValueTextEquals("metadata"u8))
                {
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.StartObject)
                    {
                        reader.Skip();
                        continue;
                    }

                    while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                    {
                        var isContinue = reader.ValueTextEquals("continue"u8);
                        var isVersion = reader.ValueTextEquals("resourceVersion"u8);
                        reader.Read();
                        if (reader.TokenType == JsonTokenType.String && (isContinue || isVersion))
                        {
                            if (isContinue)
                            {
                                continueToken = reader.GetString();
                            }
                            else
                            {
                                resourceVersion = reader.GetString();
                            }
                        }
                        else
                        {
                            reader.Skip();
                        }
                    }
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }

            return items;
        }
        catch (JsonException)
        {
            foreach (var item in items)
            {
                item.Dispose();
            }

            return null;
        }
    }

    /// <summary>
    /// "Application argocd/web (nested deeper than 256 levels)" — the object a skipped JSON
    /// value claims to be, read with a reader that does not care how deep it goes, and why it
    /// could not be read. Used for list items and for a watch frame's <c>object</c>.
    /// </summary>
    internal static string Describe(ReadOnlySpan<byte> json, JsonException error)
    {
        var why = error.Message.Contains("depth", StringComparison.OrdinalIgnoreCase)
            ? $"nested deeper than {MaxDepth} levels"
            : "not valid JSON";
        return $"{NameOf(json)} ({why})";
    }

    /// <summary>"Application argocd/web", or "an object" when the JSON does not say.</summary>
    internal static string NameOf(ReadOnlySpan<byte> json)
    {
        var (kind, @namespace, name) = ReadIdentity(json);
        return name is { Length: > 0 }
            ? $"{(kind is { Length: > 0 } ? kind + " " : "")}{(@namespace is { Length: > 0 } ? @namespace + "/" : "")}{name}"
            : "an object";
    }

    /// <summary>
    /// The <c>kind</c>, <c>metadata.namespace</c> and <c>metadata.name</c> of one object, or
    /// of a watch frame's <c>object</c>, found by walking tokens rather than parsing — so an
    /// object too deep to parse can still be named. Nulls for whatever is not there or cannot
    /// be reached.
    /// </summary>
    internal static (string? Kind, string? Namespace, string? Name) ReadIdentity(ReadOnlySpan<byte> json)
    {
        string? kind = null, @namespace = null, name = null;
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = ScanDepth });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return default;
            }

            ReadObjectIdentity(ref reader, ref kind, ref @namespace, ref name, allowFrame: true);
        }
        catch (JsonException)
        {
            // Truncated or malformed: whatever was found before the error is still worth naming.
        }

        return (kind, @namespace, name);
    }

    private static void ReadObjectIdentity(
        ref Utf8JsonReader reader, ref string? kind, ref string? @namespace, ref string? name, bool allowFrame)
    {
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("kind"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.String)
                {
                    kind ??= reader.GetString();
                }
                else
                {
                    reader.Skip();
                }
            }
            else if (reader.ValueTextEquals("metadata"u8))
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    reader.Skip();
                    continue;
                }

                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    var isName = reader.ValueTextEquals("name"u8);
                    var isNamespace = reader.ValueTextEquals("namespace"u8);
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.String && (isName || isNamespace))
                    {
                        if (isName)
                        {
                            name = reader.GetString();
                        }
                        else
                        {
                            @namespace = reader.GetString();
                        }
                    }
                    else
                    {
                        reader.Skip();
                    }
                }
            }
            else if (allowFrame && reader.ValueTextEquals("object"u8))
            {
                // A watch frame: {"type":…,"object":{…}}. Its own "kind" is not a field the
                // frame has, so the object's is the only one read.
                reader.Read();
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    ReadObjectIdentity(ref reader, ref kind, ref @namespace, ref name, allowFrame: false);
                }
                else
                {
                    reader.Skip();
                }
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }
    }
}

/// <summary>
/// One object in a list or a watch that could not be read — nested past
/// <see cref="ClusterJson.MaxDepth"/>, not JSON, or a watch frame over the size cap. It is
/// skipped and the stream goes on, so this is reported through a watch's
/// <c>connectionLost</c> callback (the channel every consumer already states) but it is
/// <em>not</em> a <see cref="WatchConnectionException"/>: nothing was lost, and a reconnect
/// would read the same object again.
/// </summary>
public sealed class UnreadableObjectException(string message) : Exception(message);

/// <summary>A watch frame ran past <c>ClusterClient.MaxWatchFrameBytes</c>; the watch relists.</summary>
internal sealed class WatchFrameTooLargeException(string message) : Exception(message);
