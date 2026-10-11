using System.Text.Json.Nodes;

namespace KubeNimbus.Core;

/// <summary>
/// What a manifest typed or pasted into a blank editor says it is: its group, version, kind,
/// name and (when it carries one) namespace — everything a server-side apply needs to build
/// the request path. An object opened from the list has these from the row; a new document
/// has only its text, so they are read from it.
/// </summary>
public sealed record ManifestIdentity(string Group, string Version, string Kind, string Name, string? Namespace)
{
    public string ApiVersion => Group.Length == 0 ? Version : $"{Group}/{Version}";
}

/// <summary>
/// Reads a new document's identity, and says in one sentence what is missing when it cannot.
/// Pure and in Core, so every refusal is a unit test rather than a round trip: the API
/// server's own answer to most of these ("Object 'Kind' is missing", a 404 on a path built
/// from an empty name) reads like a cluster fault rather than something the editor can fix.
/// </summary>
public static class NewManifest
{
    /// <summary>Returns the identity, or null with <paramref name="problem"/> set to the reason.</summary>
    public static ManifestIdentity? Read(string yaml, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(yaml))
        {
            problem = "Nothing was applied: the editor is empty. Type or paste one manifest first.";
            return null;
        }

        JsonNode? node;
        try
        {
            // Every document is counted before the first is read: ParseYamlToJson reads only
            // the first, and a pasted multi-document file would otherwise apply its first
            // object and drop the rest without a word.
            if (YamlJson.CountDocuments(yaml) > 1)
            {
                problem = "Nothing was applied: this text holds more than one document (---). "
                    + "Apply one object per editor — open another New YAML for the next.";
                return null;
            }

            node = YamlJson.ParseYamlToJson(yaml);
        }
        catch (Exception ex) when (ex is YamlDotNet.Core.YamlException or FormatException or InvalidOperationException)
        {
            problem = $"Nothing was applied: this isn't valid YAML — {FirstLine(ex.Message)}";
            return null;
        }

        if (node is not JsonObject root)
        {
            problem = "Nothing was applied: a manifest is one object with apiVersion:, kind: and metadata:.";
            return null;
        }

        var apiVersion = Text(root["apiVersion"]);
        var kind = Text(root["kind"]);
        if (apiVersion is null || kind is null)
        {
            problem = "Nothing was applied: the manifest needs apiVersion: and kind:, which say what it is.";
            return null;
        }

        if (string.Equals(kind, "List", StringComparison.Ordinal) && apiVersion == "v1")
        {
            problem = "Nothing was applied: a List holds several objects. Apply one object per editor.";
            return null;
        }

        var metadata = root["metadata"] as JsonObject;
        var name = Text(metadata?["name"]);
        if (name is null)
        {
            problem = Text(metadata?["generateName"]) is not null
                ? "Nothing was applied: server-side apply needs metadata.name — it cannot use generateName."
                : "Nothing was applied: the manifest needs metadata.name.";
            return null;
        }

        if (!ResourceDescriptor.IsValidPathSegment(name))
        {
            problem = $"Nothing was applied: \"{name}\" cannot be an object's name.";
            return null;
        }

        var ns = Text(metadata?["namespace"]);
        if (ns is not null && !ResourceDescriptor.IsValidPathSegment(ns))
        {
            problem = $"Nothing was applied: \"{ns}\" cannot be a namespace.";
            return null;
        }

        var slash = apiVersion.IndexOf('/', StringComparison.Ordinal);
        var (group, version) = slash < 0 ? ("", apiVersion) : (apiVersion[..slash], apiVersion[(slash + 1)..]);
        if (version.Length == 0 || version.Contains('/', StringComparison.Ordinal))
        {
            problem = $"Nothing was applied: \"{apiVersion}\" is not an apiVersion (group/version, or v1).";
            return null;
        }

        return new ManifestIdentity(group, version, kind, name, ns);
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && text.Trim().Length > 0 ? text.Trim() : null;

    private static string FirstLine(string message)
    {
        var end = message.IndexOfAny(['\r', '\n']);
        return end < 0 ? message : message[..end];
    }
}
