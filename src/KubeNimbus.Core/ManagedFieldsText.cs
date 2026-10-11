using YamlDotNet.RepresentationModel;

namespace KubeNimbus.Core;

/// <summary>
/// Takes <c>metadata.managedFields</c> out of an object's YAML for the editor, and puts it back
/// where it was when the reader asks to see it (#270).
/// </summary>
/// <remarks>
/// <para>
/// The field is the API server's record of which manager owns which field. On a Deployment it
/// is a couple of hundred lines at the top of <c>metadata</c>, above every field the editor is
/// opened to change, and <c>kubectl get -o yaml</c> and <c>kubectl edit</c> have hidden it by
/// default since 1.21. Hiding it loses nothing an apply could use: the apply body never carries
/// it (<c>ClusterClient.ApplyBody</c> removes it, because the server refuses an apply patch that
/// does), and a server-side apply without it leaves the server's own record as it was.
/// </para>
/// <para>
/// The work is done on YamlDotNet's node tree, not on the text, so a <c>managedFields</c> key
/// inside a value (a ConfigMap holding a manifest) is never touched: only the key directly
/// under the top-level <c>metadata</c> mapping of a single document is. Anything else — text
/// that does not parse, several documents, no such key — is left exactly as it was.
/// </para>
/// </remarks>
public static class ManagedFieldsText
{
    private const string MetadataKey = "metadata";
    private const string ManagedFieldsKey = "managedFields";

    /// <summary>
    /// Removes <c>metadata.managedFields</c>. True when it was there and is now gone, with the
    /// removed block in <paramref name="removed"/>; false, with <paramref name="hidden"/> equal
    /// to <paramref name="yaml"/>, when there was nothing to remove or the text could not be read.
    /// </summary>
    public static bool TryHide(string yaml, out string hidden, out ManagedFieldsBlock? removed)
    {
        hidden = yaml;
        removed = null;
        if (!yaml.Contains(ManagedFieldsKey, StringComparison.Ordinal)
            || Load(yaml) is not { } document
            || Metadata(document) is not { } metadata)
        {
            return false;
        }

        var entries = metadata.Children.ToList();
        var index = entries.FindIndex(kv => IsKey(kv.Key, ManagedFieldsKey));
        if (index < 0)
        {
            return false;
        }

        removed = new ManagedFieldsBlock(YamlJson.Save(new YamlDocument(entries[index].Value)), index);
        entries.RemoveAt(index);
        Replace(metadata, entries);
        hidden = YamlJson.Save(document);
        return true;
    }

    /// <summary>
    /// Puts a block <see cref="TryHide"/> removed back into <c>metadata</c>, at the position it
    /// was taken from (or last, when the metadata has fewer keys now). False, with
    /// <paramref name="shown"/> equal to <paramref name="yaml"/>, when the text cannot be read as
    /// one document with a <c>metadata</c> mapping, or already has the field.
    /// </summary>
    public static bool TryShow(string yaml, ManagedFieldsBlock block, out string shown)
    {
        shown = yaml;
        if (Load(yaml) is not { } document
            || Metadata(document) is not { } metadata
            || Load(block.Yaml) is not { } blockDocument)
        {
            return false;
        }

        var entries = metadata.Children.ToList();
        if (entries.Any(kv => IsKey(kv.Key, ManagedFieldsKey)))
        {
            return false;
        }

        var entry = new KeyValuePair<YamlNode, YamlNode>(new YamlScalarNode(ManagedFieldsKey), blockDocument.RootNode);
        entries.Insert(Math.Clamp(block.Index, 0, entries.Count), entry);
        Replace(metadata, entries);
        shown = YamlJson.Save(document);
        return true;
    }

    private static YamlDocument? Load(string yaml)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            return stream.Documents is [var only] ? only : null;
        }
        catch (Exception ex) when (ex is YamlDotNet.Core.YamlException or InvalidOperationException)
        {
            return null;
        }
    }

    private static YamlMappingNode? Metadata(YamlDocument document) =>
        document.RootNode is YamlMappingNode root
        && root.Children.FirstOrDefault(kv => IsKey(kv.Key, MetadataKey)).Value is YamlMappingNode metadata
            ? metadata
            : null;

    private static bool IsKey(YamlNode key, string name) => key is YamlScalarNode { Value: { } value } && value == name;

    private static void Replace(YamlMappingNode mapping, List<KeyValuePair<YamlNode, YamlNode>> entries)
    {
        mapping.Children.Clear();
        foreach (var (key, value) in entries)
        {
            mapping.Children.Add(key, value);
        }
    }
}

/// <summary>
/// The <c>metadata.managedFields</c> block <see cref="ManagedFieldsText.TryHide"/> took out of a
/// document: its YAML, and its position among the metadata keys.
/// </summary>
public sealed record ManagedFieldsBlock(string Yaml, int Index);
